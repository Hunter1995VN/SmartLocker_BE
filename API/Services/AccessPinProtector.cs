using Application.Interfaces;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using System.Text;

namespace API.Services;

public sealed class AccessPinProtector(IConfiguration config, IDataProtectionProvider? provider = null) : IAccessPinProtector
{
    private byte[] DeriveKey(Guid bookingId)
    {
        var secret = config["JwtSettings:SecretKey"] ?? config["OfflineTicket:HmacSecret"] ?? "SmartLocker-AccessPin-Default-Key-2026";
        return SHA256.HashData(Encoding.UTF8.GetBytes($"SmartLocker.AccessPin.v2:{secret}:{bookingId:N}"));
    }

    public string Protect(Guid bookingId, string pin)
    {
        if (pin.Length != 8 || !pin.All(char.IsAsciiDigit))
            throw new ArgumentException("Expected eight numeric digits.", nameof(pin));

        var key = DeriveKey(bookingId);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plainBytes = Encoding.UTF8.GetBytes(pin);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var combined = new byte[12 + 16 + cipherBytes.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, 12);
        Buffer.BlockCopy(tag, 0, combined, 12, 16);
        Buffer.BlockCopy(cipherBytes, 0, combined, 28, cipherBytes.Length);
        return "v2:" + Convert.ToBase64String(combined);
    }

    public string? Unprotect(Guid bookingId, string protectedPin)
    {
        if (string.IsNullOrWhiteSpace(protectedPin)) return null;
        try
        {
            if (protectedPin.StartsWith("v2:", StringComparison.Ordinal))
            {
                var combined = Convert.FromBase64String(protectedPin[3..]);
                if (combined.Length < 28) return null;
                var nonce = combined[..12];
                var tag = combined[12..28];
                var cipherBytes = combined[28..];
                var plainBytes = new byte[cipherBytes.Length];
                using var aes = new AesGcm(DeriveKey(bookingId), 16);
                aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
                return Encoding.UTF8.GetString(plainBytes);
            }
            return provider?.CreateProtector("SmartLocker.AccessPin.v1", bookingId.ToString("N")).Unprotect(protectedPin);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
