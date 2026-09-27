namespace Infrastructure.BackgroundJobs;

using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Background Job xử lý hết hạn đơn đặt tủ VÀ tự động đối soát thanh toán (Payment Reconciliation).
/// Cơ chế này đảm bảo:
/// 1. Nếu khách đã chuyển khoản thành công nhưng server bị trục trặc / rớt mạng / lỡ Webhook,
///    hệ thống sẽ chủ động hỏi PayOS để xác nhận đơn ngay lập tức, không để khách bị mất tiền oan.
/// 2. Nếu quá hạn thanh toán mà khách chưa trả, hệ thống tự động hủy link PayOS và nhả tủ cho người khác thuê.
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

                var now = DateTime.UtcNow;

                // Lấy tất cả đơn PENDING_PAYMENT kèm thông tin thanh toán & tủ
                var pendingBookings = await context.Bookings
                    .Include(b => b.Payments)
                    .Include(b => b.Locker)
                    .Where(b => b.Status == BookingStatus.PENDING_PAYMENT)
                    .ToListAsync(stoppingToken);

                if (pendingBookings.Any())
                {
                    bool hasChanges = false;

                    foreach (var booking in pendingBookings)
                    {
                        var payment = booking.Payments
                            .FirstOrDefault(p => p.Status == PaymentStatus.PENDING && !string.IsNullOrEmpty(p.OrderCode));

                        if (payment != null && long.TryParse(payment.OrderCode, out var orderCode))
                        {
                            // 1. CHỦ ĐỘNG HỎI PAYOS: Đơn này khách đã thanh toán chưa?
                            var payOsStatus = await paymentGateway.GetPaymentStatusAsync(orderCode);

                            if (string.Equals(payOsStatus, "PAID", StringComparison.OrdinalIgnoreCase))
                            {
                                // === TRƯỜNG HỢP 1: KHÁCH ĐÃ TRẢ TIỀN (nhưng Webhook bị lỡ / server downtime) ===
                                payment.Status = PaymentStatus.PAID;
                                payment.PaidAt = DateTime.UtcNow;
                                payment.Gateway = "PAYOS";
                                payment.WebhookReceivedAt = DateTime.UtcNow;

                                booking.Status = BookingStatus.CONFIRMED;

                                // Sinh mã PIN mở tủ 8 ký tự và mã QR
                                var randomCode = Guid.NewGuid().ToString("N")[..8].ToUpper();
                                using var sha256 = SHA256.Create();
                                var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(randomCode));
                                var hashString = Convert.ToBase64String(hashBytes);

                                var qrPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                                    JsonSerializer.Serialize(new
                                    {
                                        bookingId = booking.Id,
                                        nonce = Guid.NewGuid().ToString("N"),
                                        expiresAt = booking.EndAt
                                    })));

                                context.AccessCredentials.Add(new AccessCredential
                                {
                                    BookingId = booking.Id,
                                    QrPayload = qrPayload,
                                    QrNonce = Guid.NewGuid().ToString("N"),
                                    QrExpiresAt = booking.EndAt,
                                    AccessCodeHash = hashString,
                                    IsActive = true,
                                    IssuedAt = DateTime.UtcNow
                                });

                                if (booking.Locker != null)
                                {
                                    booking.Locker.BusinessStatus = LockerBusinessStatus.RESERVED;
                                }

                                hasChanges = true;
                                _logger.LogInformation(
                                    "🛡️ [RECONCILIATION] Đã tự động đối soát và KÍCH HOẠT đơn {BookingCode} (OrderCode={OrderCode}) do Webhook bị lỡ hoặc server vừa khởi động lại!",
                                    booking.BookingCode, orderCode);

                                continue;
                            }
                        }

                        // === TRƯỜNG HỢP 2: ĐƠN ĐÃ QUÁ HẠN MÀ KHÁCH CHƯA TRẢ TIỀN ===
                        if (booking.PaymentExpiresAt < now)
                        {
                            booking.Status = BookingStatus.EXPIRED;

                            var pendingPayments = booking.Payments.Where(p => p.Status == PaymentStatus.PENDING).ToList();
                            foreach (var p in pendingPayments)
                            {
                                p.Status = PaymentStatus.EXPIRED;

                                // Hủy link thanh toán trên PayOS để khách không quét nhầm nữa
                                if (long.TryParse(p.OrderCode, out var oc))
                                {
                                    await paymentGateway.CancelPaymentLinkAsync(oc, "Đơn đặt tủ đã hết hạn thanh toán");
                                }
                            }

                            // Giải phóng tủ đồ cho người khác đặt
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
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in BookingExpirationJob");
            }

            // Quét định kỳ mỗi 30 giây
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
