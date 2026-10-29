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
                return ApiResponse<PaymentResultDto>.ErrorResponse("Dá»¯ liá»‡u webhook khÃ´ng há»£p lá»‡.");

            var (orderCode, isSuccess) = await _paymentGateway.VerifyWebhookAsync(webhook);

            if (orderCode == 0)
                return ApiResponse<PaymentResultDto>.ErrorResponse("KhÃ´ng thá»ƒ xÃ¡c thá»±c webhook.");

            var payment = await _context.Payments
                .Include(p => p.Booking)
                .FirstOrDefaultAsync(p => p.OrderCode == orderCode.ToString());

            if (payment == null)
                return ApiResponse<PaymentResultDto>.ErrorResponse($"KhÃ´ng tÃ¬m tháº¥y giao dá»‹ch OrderCode={orderCode}.");

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
                    Message = "Giao dá»‹ch Ä‘Ã£ Ä‘Æ°á»£c xá»­ lÃ½ trÆ°á»›c Ä‘Ã³."
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
                Message = payment.Status == PaymentStatus.PAID ? "Thanh toÃ¡n thÃ nh cÃ´ng" : "Thanh toÃ¡n tháº¥t báº¡i"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing PayOS webhook");
            return ApiResponse<PaymentResultDto>.ErrorResponse("Lá»—i xá»­ lÃ½ webhook.");
        }
    }

    public async Task<ApiResponse<PaymentResultDto>> ProcessPayOSReturnAsync(long orderCode, string status)
    {
        var payment = await _context.Payments
            .Include(p => p.Booking)
            .FirstOrDefaultAsync(p => p.OrderCode == orderCode.ToString());

        if (payment == null)
            return ApiResponse<PaymentResultDto>.ErrorResponse("KhÃ´ng tÃ¬m tháº¥y giao dá»‹ch.");

        return ApiResponse<PaymentResultDto>.SuccessResponse(new PaymentResultDto
        {
            PaymentId = payment.Id,
            BookingId = payment.BookingId,
            BookingCode = payment.Booking.BookingCode,
            OrderCode = payment.OrderCode,
            Amount = payment.Amount,
            Status = payment.Status.ToString(),
            IsSuccess = payment.Status == PaymentStatus.PAID,
            Message = payment.Status == PaymentStatus.PAID ? "Thanh toÃ¡n thÃ nh cÃ´ng" : "Äang chá» xÃ¡c nháº­n"
        });
    }

    public async Task<ApiResponse<PaymentDto>> GetPaymentByIdAsync(Guid paymentId)
    {
        var payment = await _context.Payments.FindAsync(paymentId);
        if (payment == null)
            return ApiResponse<PaymentDto>.ErrorResponse("KhÃ´ng tÃ¬m tháº¥y giao dá»‹ch.");

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
    /// Chá»§ Ä‘á»™ng Ä‘á»‘i soÃ¡t trá»±c tiáº¿p vá»›i PayOS vÃ  cáº­p nháº­t DB náº¿u Ä‘Ã£ thanh toÃ¡n
    /// </summary>
    public async Task<ApiResponse<PaymentResultDto>> SyncPaymentStatusAsync(long orderCode)
    {
        var payment = await _context.Payments
            .Include(p => p.Booking)
            .FirstOrDefaultAsync(p => p.OrderCode == orderCode.ToString());

        if (payment == null)
            return ApiResponse<PaymentResultDto>.ErrorResponse($"KhÃ´ng tÃ¬m tháº¥y giao dá»‹ch OrderCode={orderCode}.");

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
                Message = "Giao dá»‹ch Ä‘Ã£ Ä‘Æ°á»£c thanh toÃ¡n thÃ nh cÃ´ng."
            });
        }

        // Gá»i trá»±c tiáº¿p PayOS API Ä‘á»ƒ Ä‘á»‘i soÃ¡t tráº¡ng thÃ¡i thá»±c táº¿
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
                Message = "XÃ¡c nháº­n thanh toÃ¡n thÃ nh cÃ´ng tá»« cá»•ng PayOS!"
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
            Message = $"Giao dá»‹ch Ä‘ang á»Ÿ tráº¡ng thÃ¡i {payosStatus ?? "PENDING"} trÃªn cá»•ng PayOS."
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

            // Chuáº©n hÃ³a mÃ£ PIN má»Ÿ tá»§ thÃ nh 6 chá»¯ sá»‘ an toÃ n (100000 - 999999)
            var randomCode = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
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
