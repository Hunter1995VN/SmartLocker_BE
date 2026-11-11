using System.Net;
using Application.Interfaces;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Infrastructure.Services;

/// <summary>
/// UC-T16: gửi mã ủy quyền lấy đồ tới email của người nhận qua cấu hình SmtpSettings sẵn có.
/// Hệ thống chưa tích hợp nhà cung cấp SMS, nên liên hệ là số điện thoại sẽ trả về MANUAL.
/// Mã PIN không được ghi vào log.
/// </summary>
public class EmailPickupDelegationNotifier(IOptionsSnapshot<SmtpSettings> options, ILogger<EmailPickupDelegationNotifier> logger) : IPickupDelegationNotifier
{
    public async Task<DelegationDelivery> SendAsync(string contact, string pin, DateTime expiresAtUtc, string stationName, string bookingCode, CancellationToken ct = default)
    {
        var to = contact.Trim();
        if (!to.Contains('@')) return new("MANUAL", false, "NO_SMS_PROVIDER");

        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.Username) || string.IsNullOrWhiteSpace(settings.Password)
            || settings.Username == "REPLACE_ME" || settings.Password == "your-app-password")
        {
            logger.LogWarning("Chưa cấu hình SMTP nên không gửi mã ủy quyền tới người nhận của đơn {BookingCode}.", bookingCode);
            return new("EMAIL", false, "SMTP_NOT_CONFIGURED");
        }

        try
        {
            if (!MailboxAddress.TryParse(to, out var recipient)) return new("EMAIL", false, "INVALID_ADDRESS");
            var fromEmail = string.IsNullOrWhiteSpace(settings.FromEmail) ? settings.Username.Trim() : settings.FromEmail.Trim();
            var fromName = string.IsNullOrWhiteSpace(settings.FromName) ? "SmartLocker" : settings.FromName.Trim();

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(fromName, fromEmail));
            message.To.Add(recipient);
            message.Subject = "[SmartLocker] Mã lấy hành lý hộ";
            var vietnam = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(expiresAtUtc, DateTimeKind.Utc), VietnamTimeZone());
            var spaced = $"{pin[..4]} {pin[4..]}";
            message.Body = new BodyBuilder
            {
                TextBody = $"Bạn được ủy quyền lấy hành lý tại {stationName} (đơn {bookingCode}).\n" +
                           $"Mã 8 số, dùng một lần: {spaced}\n" +
                           $"Hết hạn lúc {vietnam:HH:mm dd/MM/yyyy}. Nhập mã tại kiosk của đúng trạm này. Không chia sẻ mã cho người khác.",
                HtmlBody = $"<div style='font-family:Arial,sans-serif;max-width:480px;margin:0 auto;padding:24px;color:#0f1b2a'>" +
                           $"<h2 style='margin:0 0 8px'>Mã lấy hành lý hộ</h2>" +
                           $"<p>Bạn được ủy quyền lấy hành lý tại <strong>{WebUtility.HtmlEncode(stationName)}</strong> (đơn {WebUtility.HtmlEncode(bookingCode)}).</p>" +
                           $"<p style='font-size:34px;font-weight:700;letter-spacing:6px;margin:16px 0'>{spaced}</p>" +
                           $"<p>Dùng một lần, hết hạn lúc <strong>{vietnam:HH:mm dd/MM/yyyy}</strong>. Nhập mã tại kiosk của đúng trạm này.</p>" +
                           $"<p style='color:#5a6b7d;font-size:13px'>Không chia sẻ mã cho người khác.</p></div>",
            }.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(settings.Host, settings.Port, MailKit.Security.SecureSocketOptions.StartTls, ct);
            await smtp.AuthenticateAsync(settings.Username.Trim(), settings.Password.Replace(" ", string.Empty), ct);
            await smtp.SendAsync(message, ct);
            await smtp.DisconnectAsync(true, ct);
            return new("EMAIL", true, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Không gửi được mã ủy quyền của đơn {BookingCode}.", bookingCode);
            return new("EMAIL", false, "SEND_FAILED");
        }
    }

    private static TimeZoneInfo VietnamTimeZone()
    {
        foreach (var id in new[] { "SE Asia Standard Time", "Asia/Ho_Chi_Minh" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (TimeZoneNotFoundException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("ICT", TimeSpan.FromHours(7), "ICT", "ICT");
    }
}
