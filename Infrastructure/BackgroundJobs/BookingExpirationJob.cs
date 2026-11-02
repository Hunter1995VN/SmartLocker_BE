namespace Infrastructure.BackgroundJobs;

using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Background Job xử lý hết hạn đơn đặt tủ VÀ tự động đối soát thanh toán (Payment Reconciliation).
/// Đồng thời hoàn trả lượt dùng & ngân sách chiết khấu Voucher nếu đơn chưa thanh toán quá hạn 10 phút.
/// </summary>
public class BookingExpirationJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BookingExpirationJob> _logger;

    public BookingExpirationJob(IServiceScopeFactory scopeFactory, ILogger<BookingExpirationJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<ISmartLockerDbContext>();
                var paymentGateway = scope.ServiceProvider.GetRequiredService<IPaymentGatewayService>();
                var paymentService = scope.ServiceProvider.GetRequiredService<IPaymentService>();
                var promotionService = scope.ServiceProvider.GetService<IPromotionVoucherService>();

                var now = DateTime.UtcNow;

                var pendingBookings = await context.Bookings
                    .Include(b => b.Payments)
                    .Include(b => b.Locker)
                    .Where(b => b.Status == BookingStatus.PENDING_PAYMENT)
                    .ToListAsync(stoppingToken);

                if (pendingBookings.Any())
                {
                    bool hasChanges = false;
                    var expiredBookingIds = new List<Guid>();

                    foreach (var booking in pendingBookings)
                    {
                        var payment = booking.Payments
                            .FirstOrDefault(p => p.Status == PaymentStatus.PENDING && !string.IsNullOrEmpty(p.OrderCode));

                        if (payment != null && long.TryParse(payment.OrderCode, out var orderCode))
                        {
                            var payOsStatus = await paymentGateway.GetPaymentStatusAsync(orderCode);

                            if (string.Equals(payOsStatus, "PAID", StringComparison.OrdinalIgnoreCase))
                            {
                                // All payment entry points use the same protected PIN / dynamic QR issuer.
                                var synced = await paymentService.SyncPaymentStatusAsync(orderCode);
                                if (!synced.Success || synced.Data?.IsSuccess != true) continue;

                                if (booking.Locker != null)
                                {
                                    booking.Locker.BusinessStatus = LockerBusinessStatus.RESERVED;
                                }

                                hasChanges = true;
                                _logger.LogInformation(
                                    "🛡️ [RECONCILIATION] Đã tự động đối soát và KÍCH HOẠT đơn {BookingCode} (OrderCode={OrderCode})!",
                                    booking.BookingCode, orderCode);

                                continue;
                            }
                        }

                        if (booking.PaymentExpiresAt < now)
                        {
                            booking.Status = BookingStatus.EXPIRED;
                            expiredBookingIds.Add(booking.Id);

                            var pendingPayments = booking.Payments.Where(p => p.Status == PaymentStatus.PENDING).ToList();
                            foreach (var p in pendingPayments)
                            {
                                p.Status = PaymentStatus.EXPIRED;

                                if (long.TryParse(p.OrderCode, out var oc))
                                {
                                    await paymentGateway.CancelPaymentLinkAsync(oc, "Đơn đặt tủ đã hết hạn thanh toán");
                                }
                            }

                            if (booking.Locker != null)
                            {
                                booking.Locker.BusinessStatus = LockerBusinessStatus.AVAILABLE;
                            }

                            hasChanges = true;
                            _logger.LogInformation(
                                "⏰ [EXPIRATION] Đơn {BookingCode} đã hết hạn thanh toán, đã hủy link PayOS và giải phóng tủ {LockerId}.",
                                booking.BookingCode, booking.LockerId);
                        }
                    }

                    if (hasChanges)
                    {
                        await context.SaveChangesAsync(stoppingToken);
                    }

                    if (promotionService != null && expiredBookingIds.Count > 0)
                    {
                        foreach (var expId in expiredBookingIds)
                        {
                            await promotionService.ReleaseVoucherForBookingAsync(
                                expId,
                                "Đơn đặt tủ hết hạn 10 phút thanh toán");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in BookingExpirationJob");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
