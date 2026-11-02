namespace Infrastructure.Services;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.DTOs.Common;
using Application.DTOs.Booking;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

public class OfflineTicketService : IOfflineTicketService
{
    private readonly ISmartLockerDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OfflineTicketService> _logger;
    private readonly byte[] _secretKeyBytes;
    private readonly IAccessPinProtector? _pinProtector;

    public OfflineTicketService(
        ISmartLockerDbContext db,
        IConfiguration configuration,
        ILogger<OfflineTicketService> logger, IAccessPinProtector? pinProtector = null)
    {
        _db = db;
        _configuration = configuration;
        _logger = logger;
        _pinProtector = pinProtector;

        var secret = _configuration["JwtSettings:SecretKey"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            secret = "SmartLocker.Super.Secret.Key.2025.MinLength32Chars";
        }
        _secretKeyBytes = Encoding.UTF8.GetBytes(secret);
    }

    public string GenerateHmacToken(Booking booking, string pin, DateTime expiresAt, string nonce)
    {
        var header = new
        {
            alg = "HS256",
            typ = "JWS"
        };

        var payload = new
        {
            bid = booking.Id.ToString(),
            code = booking.BookingCode,
            sid = booking.StationId.ToString(),
            sname = booking.Station?.Name ?? string.Empty,
            lid = booking.LockerId?.ToString() ?? string.Empty,
            lcode = booking.Locker?.LockerCode ?? string.Empty,
            size = booking.Size.ToString(),
            pin = pin,
            iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            exp = new DateTimeOffset(expiresAt.ToUniversalTime()).ToUnixTimeSeconds(),
            nonce = nonce
        };

        var headerJson = JsonSerializer.Serialize(header);
        var payloadJson = JsonSerializer.Serialize(payload);

        var headerEncoded = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var payloadEncoded = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));

        var unsignedToken = $"{headerEncoded}.{payloadEncoded}";

        using var hmac = new HMACSHA256(_secretKeyBytes);
        var signatureBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(unsignedToken));
        var signatureEncoded = Base64UrlEncode(signatureBytes);

        return $"{unsignedToken}.{signatureEncoded}";
    }

    public (bool IsValid, ParsedOfflineTicket? Ticket, string? Error) VerifyHmacToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return (false, null, "Mã token trống.");

        var parts = token.Trim().Split('.');
        if (parts.Length != 3)
            return (false, null, "Định dạng token HMAC không hợp lệ (yêu cầu 3 phần chuẩn JWS).");

        var headerEncoded = parts[0];
        var payloadEncoded = parts[1];
        var signatureEncoded = parts[2];

        var unsignedToken = $"{headerEncoded}.{payloadEncoded}";

        using var hmac = new HMACSHA256(_secretKeyBytes);
        var expectedSignature = hmac.ComputeHash(Encoding.UTF8.GetBytes(unsignedToken));
        byte[] actualSignature;
        try { actualSignature = Base64UrlDecode(signatureEncoded); }
        catch (FormatException) { return (false, null, "Chữ ký vé không đúng định dạng."); }

        if (!CryptographicOperations.FixedTimeEquals(expectedSignature, actualSignature))
        {
            return (false, null, "Chữ ký mật mã HMAC-SHA256 không hợp lệ hoặc đã bị chỉnh sửa.");
        }

        try
        {
            var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(payloadEncoded));
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;

            long exp = 0;
            if (root.TryGetProperty("exp", out var expProp))
            {
                exp = expProp.GetInt64();
            }

            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (exp > 0 && nowUnix > exp)
            {
                return (false, null, "Vé mở khóa ngoại tuyến đã hết thời hạn hiệu lực.");
            }

            var ticket = new ParsedOfflineTicket
            {
                BookingId = root.TryGetProperty("bid", out var bId) && Guid.TryParse(bId.GetString(), out var bidGuid) ? bidGuid : Guid.Empty,
                BookingCode = root.TryGetProperty("code", out var code) ? code.GetString() ?? string.Empty : string.Empty,
                StationId = root.TryGetProperty("sid", out var sId) && Guid.TryParse(sId.GetString(), out var sidGuid) ? sidGuid : Guid.Empty,
                StationName = root.TryGetProperty("sname", out var sName) ? sName.GetString() : null,
                LockerId = root.TryGetProperty("lid", out var lId) && Guid.TryParse(lId.GetString(), out var lidGuid) ? lidGuid : null,
                LockerCode = root.TryGetProperty("lcode", out var lCode) ? lCode.GetString() : null,
                LockerSize = root.TryGetProperty("size", out var size) ? size.GetString() : null,
                AccessPin = root.TryGetProperty("pin", out var pin) ? pin.GetString() ?? string.Empty : string.Empty,
                IssuedAt = root.TryGetProperty("iat", out var iatProp) ? DateTimeOffset.FromUnixTimeSeconds(iatProp.GetInt64()).UtcDateTime : DateTime.UtcNow,
                ExpiresAt = exp > 0 ? DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime : DateTime.UtcNow.AddHours(24),
                Nonce = root.TryGetProperty("nonce", out var n) ? n.GetString() ?? string.Empty : string.Empty
            };

            return (true, ticket, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi giải mã payload của token HMAC.");
            return (false, null, "Không thể đọc nội dung vé ngoại tuyến.");
        }
    }

    public string ExtractPinFromCredential(AccessCredential credential)
    {
        if (!string.IsNullOrEmpty(credential.ProtectedAccessCode))
            return OnlineTicket.GetDisplayPin(credential, _pinProtector) ?? string.Empty;
        // 1. Kiểm tra nếu OfflinePayload là token JWS
        if (!string.IsNullOrWhiteSpace(credential.OfflinePayload))
        {
            var raw = credential.OfflinePayload.Trim();
            if (raw.Contains('.'))
            {
                var (isValid, ticket, _) = VerifyHmacToken(raw);
                if (isValid && ticket != null && MatchesPin(credential, ticket.AccessPin))
                    return ticket.AccessPin;
            }
            else if (MatchesPin(credential, raw))
            {
                return raw;
            }
        }

        // 2. Kiểm tra nếu QrPayload là token JWS
        if (!string.IsNullOrWhiteSpace(credential.QrPayload))
        {
            var raw = credential.QrPayload.Trim();
            if (raw.Contains('.'))
            {
                var (isValid, ticket, _) = VerifyHmacToken(raw);
                if (isValid && ticket != null && MatchesPin(credential, ticket.AccessPin))
                    return ticket.AccessPin;
            }
        }

        // 3. Fallback: Nếu không có PIN đọc được
        return string.Empty;
    }

    private static bool MatchesPin(AccessCredential credential, string pin) =>
        pin.Length is 6 or 8 && pin.All(char.IsAsciiDigit) && OnlineTicket.HashPin(pin) == credential.AccessCodeHash;

    public async Task<ApiResponse<OfflineTicketDto>> GetOfflineTicketAsync(Guid userId, Guid bookingId, CancellationToken ct = default)
    {
        var booking = await _db.Bookings
            .Include(b => b.Station)
            .Include(b => b.Locker)
            .Include(b => b.AccessCredential)
            .FirstOrDefaultAsync(b => b.Id == bookingId, ct);

        if (booking == null)
            return ApiResponse<OfflineTicketDto>.ErrorResponse("Không tìm thấy thông tin đơn đặt chỗ.");

        if (booking.UserId != userId)
            return ApiResponse<OfflineTicketDto>.ErrorResponse("Bạn không có quyền truy cập vé mở khóa của đơn đặt này.");

        if (booking.Status is not (BookingStatus.CONFIRMED or BookingStatus.STORED)
            || booking.AccessCredential is not { IsActive: true, RevokedAt: null })
            return ApiResponse<OfflineTicketDto>.ErrorResponse("Đơn không có vé đang hoạt động. Không thể cấp lại quyền mở tủ.");

        var credential = booking.AccessCredential;
        string pin;
        string hmacToken;
        DateTime validUntil;

        // Reading an offline ticket must not replace the dynamic QR or invent a PIN.
        pin = ExtractPinFromCredential(credential!);
        if (string.IsNullOrEmpty(pin))
            return ApiResponse<OfflineTicketDto>.ErrorResponse("Không đọc được PIN hợp lệ của vé. Vui lòng liên hệ nhân viên.");
        validUntil = credential!.OfflineExpiresAt ?? booking.EndAt.AddHours(24);
        var existingToken = credential.OfflinePayload;
        if (!string.IsNullOrWhiteSpace(existingToken) && existingToken.Contains('.'))
        {
            var (valid, ticket, _) = VerifyHmacToken(existingToken);
            if (valid && ticket != null && ticket.BookingId == booking.Id && ticket.StationId == booking.StationId
                && ticket.AccessPin == pin)
                validUntil = ticket.ExpiresAt;
            else existingToken = null;
        }
        else existingToken = null;
        if (existingToken == null)
        {
            validUntil = booking.EndAt > DateTime.UtcNow ? booking.EndAt.AddHours(24) : DateTime.UtcNow.AddHours(24);
            var nonce = Guid.NewGuid().ToString("N");
            hmacToken = GenerateHmacToken(booking, pin, validUntil, nonce);
            credential.OfflinePayload = hmacToken;
            credential.OfflineNonce = nonce;
            credential.OfflineExpiresAt = validUntil;
            await _db.SaveChangesAsync(ct);
        }
        else hmacToken = existingToken;

        var dto = new OfflineTicketDto
        {
            BookingId = booking.Id,
            BookingCode = booking.BookingCode,
            StationId = booking.StationId,
            StationName = booking.Station?.Name ?? "Trạm SmartLocker",
            StationAddress = booking.Station?.Address ?? string.Empty,
            LockerId = booking.LockerId,
            LockerCode = booking.Locker?.LockerCode,
            LockerSize = booking.Size.ToString(),
            AccessPin = pin,
            OfflineHmacToken = hmacToken,
            QrData = hmacToken,
            StartAt = booking.StartAt,
            EndAt = booking.EndAt,
            ValidUntil = validUntil,
            IsOfflineReady = true,
            SecurityAlgorithm = "HMAC-SHA256 (JWS RFC 7515)",
            Instructions = "Mã QR và PIN này được mã hóa bảo mật HMAC-SHA256 cục bộ. Bạn có thể quét trực tiếp tại camera Kiosk hoặc nhập mã PIN trên màn hình cảm ứng để mở tủ ngay cả khi điện thoại hoặc trạm mất kết nối Internet."
        };

        return ApiResponse<OfflineTicketDto>.SuccessResponse(dto);
    }

    private static string Base64UrlEncode(byte[] input)
    {
        var output = Convert.ToBase64String(input);
        output = output.Split('=')[0]; // Remove padding
        output = output.Replace('+', '-'); // 62nd char of encoding
        output = output.Replace('/', '_'); // 63rd char of encoding
        return output;
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var output = input;
        output = output.Replace('-', '+'); // 62nd char of encoding
        output = output.Replace('_', '/'); // 63rd char of encoding
        switch (output.Length % 4) // Pad with trailing '='s
        {
            case 0: break; // No pad char in this case
            case 2: output += "=="; break; // Two pad chars
            case 3: output += "="; break; // One pad char
            default: throw new FormatException("Chuỗi Base64Url không hợp lệ!");
        }
        return Convert.FromBase64String(output);
    }
}
