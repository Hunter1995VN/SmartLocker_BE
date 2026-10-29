namespace Infrastructure.Services;

using Application.DTOs.Common;
using Application.DTOs.Payment;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PayOS.Models.Webhooks;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public class PaymentService : IPaymentService
{
    private readonly ISmartLockerDbContext _context;
    private readonly IPaymentGatewayService _paymentGateway;
    private readonly IOfflineTicketService _offlineTicketService;
    private readonly ILogger<PaymentService> _logger;

    public PaymentService(
        ISmartLockerDbContext context,
        IPaymentGatewayService paymentGateway,
        IOfflineTicketService offlineTicketService,
        ILogger<PaymentService> logger)
    {
        _context = context;
        _paymentGateway = paymentGateway;
        _offlineTicketService = offlineTicketService;
        _logger = logger;
    }

    public async Task<ApiResponse<PaymentResultDto>> ProcessPayOSWebhookAsync(object webhookData)
    {
        try
        {
            Webhook? webhook = webhookData as Webhook;
            if (webhook == null)
                return ApiResponse<PaymentResultDto>.ErrorResponse("Dữ liệu webhook không hợp lệ.");

            var (orderCode, isSuccess) = await _paymentGateway.VerifyWebhookAsync(webhook);

            if (orderCode == 0)
                return ApiResponse<PaymentResultDto>.ErrorResponse("Không thể xác thực webhook.");

            var payment = await _context.Payments
                .Include(p => p.Booking)
                .FirstOrDefaultAsync(p => p.OrderCode == orderCode.ToString());

            if (payment == null)
                return ApiResponse<PaymentResultDto>.ErrorResponse($"Không tìm thấy giao dịch OrderCode={orderCode}.");

            // Idempotent check
            if (payment.Status == PaymentStatus.PAID)
            {
                return ApiResponse<PaymentResultDto>.SuccessResponse(new PaymentResultDto
                {
                    PaymentId = payment.Id,
                    BookingId = payment.BookingId,
                    OrderCode = payment.OrderCode,
                    Status = payment.Status.ToString(),
                    IsSuccess = true,
                    Message = "Giao dịch đã được xử lý trước đó."
                });
            }

            if (isSuccess && payment.Status == PaymentStatus.PENDING)
            {
                await ProcessSuccessfulPaymentAsync(payment);
            }
            else if (!isSuccess && payment.Status == PaymentStatus.PENDING)
            {
                payment.Status = PaymentStatus.FAILED;
                await _context.SaveChangesAsync();
            }

            return ApiResponse<PaymentResultDto>.SuccessResponse(new PaymentResultDto
            {
                PaymentId = payment.Id,
                BookingId = payment.BookingId,
                BookingCode = payment.Booking.BookingCode,
                OrderCode = payment.OrderCode,
                Amount = payment.Amount,
                Status = payment.Status.ToString(),
                IsSuccess = payment.Status == PaymentStatus.PAID,
                Message = payment.Status == PaymentStatus.PAID ? "Thanh toán thành công" : "Thanh toán thất bại"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing PayOS webhook");
            return ApiResponse<PaymentResultDto>.ErrorResponse("Lỗi xử lý webhook.");
        }
    }

    public async Task<ApiResponse<PaymentResultDto>> ProcessPayOSReturnAsync(long orderCode, string status)
    {
        var payment = await _context.Payments
            .Include(p => p.Booking)
            .FirstOrDefaultAsync(p => p.OrderCode == orderCode.ToString());

        if (payment == null)
            return ApiResponse<PaymentResultDto>.ErrorResponse("Không tìm thấy giao dịch.");

        return ApiResponse<PaymentResultDto>.SuccessResponse(new PaymentResultDto
        {
            PaymentId = payment.Id,
            BookingId = payment.BookingId,
            BookingCode = payment.Booking.BookingCode,
            OrderCode = payment.OrderCode,
            Amount = payment.Amount,
            Status = payment.Status.ToString(),
            IsSuccess = payment.Status == PaymentStatus.PAID,
            Message = payment.Status == PaymentStatus.PAID ? "Thanh toán thành công" : "Đang chờ xác nhận"
        });
    }

    public async Task<ApiResponse<PaymentDto>> GetPaymentByIdAsync(Guid paymentId)
    {
        var payment = await _context.Payments.FindAsync(paymentId);
        if (payment == null)
            return ApiResponse<PaymentDto>.ErrorResponse("Không tìm thấy giao dịch.");

        return ApiResponse<PaymentDto>.SuccessResponse(new PaymentDto
        {
            Id = payment.Id,
            BookingId = payment.BookingId,
            Amount = payment.Amount,
            Status = payment.Status.ToString(),
            Kind = payment.Kind.ToString(),
            Currency = payment.Currency,
            OrderCode = payment.OrderCode,
            Gateway = payment.Gateway,
            PaymentLink = payment.PaymentLink,
            PaidAt = payment.PaidAt,
            RefundAmount = payment.RefundAmount,
            RefundedAt = payment.RefundedAt,
            CreatedAt = payment.CreatedAt
        });
    }

    /// <summary>
    /// Chủ động đối soát trực tiếp với PayOS và cập nhật DB nếu đã thanh toán
    /// </summary>
    public async Task<ApiResponse<PaymentResultDto>> SyncPaymentStatusAsync(long orderCode)
    {
        var payment = await _context.Payments
            .Include(p => p.Booking)
            .FirstOrDefaultAsync(p => p.OrderCode == orderCode.ToString());

        if (payment == null)
            return ApiResponse<PaymentResultDto>.ErrorResponse($"Không tìm thấy giao dịch OrderCode={orderCode}.");

        if (payment.Status == PaymentStatus.PAID)
        {
            return ApiResponse<PaymentResultDto>.SuccessResponse(new PaymentResultDto
            {
                PaymentId = payment.Id,
                BookingId = payment.BookingId,
                BookingCode = payment.Booking?.BookingCode,
                OrderCode = payment.OrderCode,
                Amount = payment.Amount,
                Status = payment.Status.ToString(),
                IsSuccess = true,
                Message = "Giao dịch đã được thanh toán thành công."
            });
        }

        // Gọi trực tiếp PayOS API để đối soát trạng thái thực tế
        var payosStatus = await _paymentGateway.GetPaymentStatusAsync(orderCode);
        _logger.LogInformation("SyncPaymentStatusAsync for OrderCode={OrderCode}: PayOS status={Status}", orderCode, payosStatus);

        if (string.Equals(payosStatus, "PAID", StringComparison.OrdinalIgnoreCase))
        {
            await ProcessSuccessfulPaymentAsync(payment);
            await _context.SaveChangesAsync();

            return ApiResponse<PaymentResultDto>.SuccessResponse(new PaymentResultDto
            {
                PaymentId = payment.Id,
                BookingId = payment.BookingId,
                BookingCode = payment.Booking?.BookingCode,
                OrderCode = payment.OrderCode,
                Amount = payment.Amount,
                Status = payment.Status.ToString(),
                IsSuccess = true,
                Message = "Xác nhận thanh toán thành công từ cổng PayOS!"
            });
        }

        return ApiResponse<PaymentResultDto>.SuccessResponse(new PaymentResultDto
        {
            PaymentId = payment.Id,
            BookingId = payment.BookingId,
            BookingCode = payment.Booking?.BookingCode,
            OrderCode = payment.OrderCode,
            Amount = payment.Amount,
            Status = payment.Status.ToString(),
            IsSuccess = false,
            Message = $"Giao dịch đang ở trạng thái {payosStatus ?? "PENDING"} trên cổng PayOS."
        });
    }

    // === Private ===

    private async Task ProcessSuccessfulPaymentAsync(Payment payment)
    {
        payment.Status = PaymentStatus.PAID;
        payment.PaidAt = DateTime.UtcNow;
        payment.Gateway = "PAYOS";
        payment.WebhookReceivedAt = DateTime.UtcNow;

        if (payment.Kind == PaymentKind.BASE)
        {
            payment.Booking.Status = BookingStatus.CONFIRMED;

            // Chuẩn hóa mã PIN mở tủ thành 6 chữ số an toàn (100000 - 999999)
            var randomCode = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(randomCode));
            var hashString = Convert.ToBase64String(hashBytes);

            var offlineNonce = Guid.NewGuid().ToString("N");
            var offlineExpiresAt = payment.Booking.EndAt > DateTime.UtcNow
                ? payment.Booking.EndAt.AddHours(24)
                : DateTime.UtcNow.AddHours(24);

            var hmacToken = _offlineTicketService.GenerateHmacToken(payment.Booking, randomCode, offlineExpiresAt, offlineNonce);

            _context.AccessCredentials.Add(new AccessCredential
            {
                BookingId = payment.BookingId,
                QrPayload = hmacToken,
                QrNonce = offlineNonce,
                QrExpiresAt = offlineExpiresAt,
                AccessCodeHash = hashString,
                OfflinePayload = hmacToken,
                OfflineNonce = offlineNonce,
                OfflineExpiresAt = offlineExpiresAt,
                IsActive = true,
                IssuedAt = DateTime.UtcNow
            });

            _logger.LogInformation("Booking {Code} confirmed. AccessCode: {AccessCode}, OfflineHMAC generated",
                payment.Booking.BookingCode, randomCode);
        }
        else if (payment.Kind == PaymentKind.EXTENSION)
        {
            if (!string.IsNullOrEmpty(payment.WebhookPayload))
            {
                try
                {
                    var payload = JsonSerializer.Deserialize<JsonElement>(payment.WebhookPayload);
                    if (payload.TryGetProperty("NewEndAt", out var prop))
                    {
                        var newEndAt = prop.GetDateTime();
                        var oldEndAt = payment.Booking.EndAt;
                        payment.Booking.EndAt = newEndAt;
                        payment.Booking.IsOverdue = false;
                        payment.Booking.OverdueSince = null;
                        var bookingUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == payment.Booking.UserId);
                        if (bookingUser != null && bookingUser.OverdueDebt > 0)
                        {
                            bookingUser.OverdueDebt = Math.Max(0, bookingUser.OverdueDebt - payment.Amount);
                            bookingUser.UpdatedAt = DateTime.UtcNow;
                        }
                        payment.Booking.ExtensionCount++;

                        _context.BookingExtensions.Add(new BookingExtension
                        {
                            BookingId = payment.BookingId,
                            PaymentId = payment.Id,
                            OldEndAt = oldEndAt,
                            NewEndAt = newEndAt,
                            ExtendedAt = DateTime.UtcNow
                        });

                        var cred = await _context.AccessCredentials
                            .FirstOrDefaultAsync(ac => ac.BookingId == payment.BookingId && ac.IsActive);
                        if (cred != null)
                        {
                            var newExpires = newEndAt.AddHours(24);
                            cred.QrExpiresAt = newExpires;
                            cred.OfflineExpiresAt = newExpires;

                            var pin = _offlineTicketService.ExtractPinFromCredential(cred);
                            var nonce = Guid.NewGuid().ToString("N");
                            var newHmac = _offlineTicketService.GenerateHmacToken(payment.Booking, pin, newExpires, nonce);
                            cred.OfflinePayload = newHmac;
                            cred.QrPayload = newHmac;
                            cred.OfflineNonce = nonce;
                            cred.QrNonce = nonce;
                        }
                    }
                }
                catch (Exception ex) { _logger.LogError(ex, "Error parsing extension payload"); }
            }
        }
        else if (payment.Kind == PaymentKind.OVERDUE)
        {
            payment.Booking.IsOverdue = false;
            payment.Booking.OverdueSince = null;
            var bookingUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == payment.Booking.UserId);
            if (bookingUser != null && bookingUser.OverdueDebt > 0)
            {
                bookingUser.OverdueDebt = Math.Max(0, bookingUser.OverdueDebt - payment.Amount);
                bookingUser.UpdatedAt = DateTime.UtcNow;
            }
        }

        await _context.SaveChangesAsync();
    }
}
