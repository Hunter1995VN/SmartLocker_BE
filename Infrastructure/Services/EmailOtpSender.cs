using System.Security.Cryptography;
using System.Text;
using MailKit.Net.Smtp;
using MimeKit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartLocker.Domain.Interfaces;

namespace SmartLocker.Infrastructure.Services;

/// <summary>
/// Cấu hình SMTP & Email để gửi OTP qua Gmail.
/// Cấu hình trong appsettings.json: SmtpSettings.{Host, Port, Username, Password, FromEmail, FromName}.
/// </summary>
public class SmtpSettings
{
    public string Host { get; set; } = "smtp.gmail.com";
    public int Port { get; set; } = 587;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromEmail { get; set; } = "no-reply@smartlocker.vn";
    public string FromName { get; set; } = "SmartLocker";
}

/// <summary>
/// Gửi OTP qua email (SMTP MailKit). Có thể thêm SMS Twilio sau bằng cách implement IOtpSender khác.
/// </summary>
public class EmailOtpSender : IOtpSender
{
    private readonly SmtpSettings _settings;
    private readonly ILogger<EmailOtpSender> _logger;
    private readonly IOtpGenerator _generator;

    public EmailOtpSender(
        IOptions<SmtpSettings> settings,
        ILogger<EmailOtpSender> logger,
        IOtpGenerator generator)
    {
        _settings = settings.Value;
        _logger = logger;
        _generator = generator;
    }

    public async Task SendAsync(string destination, string code, CancellationToken ct = default)
    {
        // Trong môi trường dev nếu chưa cấu hình SMTP thật, log OTP ra console để test
        if (string.IsNullOrEmpty(_settings.Username) || _settings.Username == "REPLACE_ME")
        {
            _logger.LogWarning("⚠️ [DEV MODE] Mã OTP {Code} sẽ được gửi tới {Destination}. Cấu hình SmtpSettings.Username/Password trong appsettings.json để gửi qua email thật.", code, destination);
            Console.WriteLine($"╔══════════════════════════════════════════╗\n║  OTP for {destination,-30}  ║\n║         → Mã xác thực: {code}            ║\n╚══════════════════════════════════════════╝");
            return;
        }

        try
        {
            var email = new MimeMessage();
            email.From.Add(new MailboxAddress(_settings.FromName, _settings.FromEmail));
            email.To.Add(MailboxAddress.Parse(destination));
            email.Subject = "[SmartLocker] Mã xác thực OTP của bạn";

            email.Body = new BodyBuilder
            {
                TextBody = $"Mã xác thực OTP của bạn là: {code}. Mã có hiệu lực trong 5 phút.",
                HtmlBody = BuildHtmlTemplate(code)
            }.ToMessageBody();

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_settings.Host, _settings.Port, MailKit.Security.SecureSocketOptions.StartTls, ct);
            await smtp.AuthenticateAsync(_settings.Username, _settings.Password, ct);
            await smtp.SendAsync(email, ct);
            await smtp.DisconnectAsync(true, ct);

            _logger.LogInformation("✅ Đã gửi OTP thành công tới {Destination}", destination);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Lỗi khi gửi OTP tới {Destination}", destination);
            throw;
        }
    }

    public async Task SendWelcomeAsync(string email, string fullName, CancellationToken ct = default)
    {
        // Có thể mở rộng sau, hiện tại chỉ log
        _logger.LogInformation("🎉 User mới đăng ký: {Email} - {FullName}", email, fullName);
        await Task.CompletedTask;
    }

    private static string BuildHtmlTemplate(string code)
    {
        return $@"
        <html><body style='font-family: Arial; max-width: 480px; margin: 0 auto; padding: 20px;'>
            <div style='background: linear-gradient(135deg, #2563eb, #004ac6); padding: 32px; text-align: center; border-radius: 16px 16px 0 0;'>
                <h1 style='color: white; margin: 0;'>SmartLocker</h1>
                <p style='color: rgba(255,255,255,0.85); margin: 8px 0 0;'>Mã xác thực OTP</p>
            </div>
            <div style='padding: 32px; background: #f8f9ff; border-radius: 0 0 16px 16px; text-align: center;'>
                <p style='color: #0b1c30;'>Mã OTP của bạn là:</p>
                <div style='font-size: 36px; font-weight: bold; letter-spacing: 8px; color: #004ac6; margin: 16px 0; padding: 16px; background: white; border-radius: 8px; border: 2px dashed #2563eb;'>{code}</div>
                <p style='color: #434655; font-size: 14px;'>Mã có hiệu lực trong <strong>5 phút</strong>. Không chia sẻ mã này với bất kỳ ai.</p>
            </div>
        </body></html>";
    }
}
