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
    private readonly ILogger<PaymentService> _logger;

    public PaymentService(ISmartLockerDbContext context, IPaymentGatewayService paymentGateway, ILogger<PaymentService> logger)
    {
        _context = context;
        _paymentGateway = paymentGateway;
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

            var randomCode = Guid.NewGuid().ToString("N")[..8].ToUpper();
            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(randomCode));
            var hashString = Convert.ToBase64String(hashBytes);

            var qrPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new
                {
                    bookingId = payment.BookingId,
                    bookingCode = payment.Booking.BookingCode,
                    stationId = payment.Booking.StationId,
                    accessCode = randomCode,
                    nonce = Guid.NewGuid().ToString("N"),
                    expiresAt = payment.Booking.EndAt
                })));

            _context.AccessCredentials.Add(new AccessCredential
            {
                BookingId = payment.BookingId,
                QrPayload = qrPayload,
                QrNonce = Guid.NewGuid().ToString("N"),
                QrExpiresAt = payment.Booking.EndAt,
                AccessCodeHash = hashString,
                OfflinePayload = randomCode,
                IsActive = true,
                IssuedAt = DateTime.UtcNow
            });

            _logger.LogInformation("Booking {Code} confirmed. AccessCode: {AccessCode}",
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
                        if (cred != null) cred.QrExpiresAt = newEndAt;
                    }
                }
                catch (Exception ex) { _logger.LogError(ex, "Error parsing extension payload"); }
            }
        }
        else if (payment.Kind == PaymentKind.OVERDUE)
        {
            payment.Booking.IsOverdue = false;
            payment.Booking.OverdueSince = null;
        }

        await _context.SaveChangesAsync();
    }
}
