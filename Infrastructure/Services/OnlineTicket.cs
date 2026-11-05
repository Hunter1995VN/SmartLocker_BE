namespace Infrastructure.Services;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Entities;
using Application.Interfaces;

/// <summary>
/// UC-T09: Online tickets are opaque bearer tokens with dynamic QR code rotation and 8-digit access PIN.
/// Booking identifiers alone never authorize access.
/// </summary>
public static class OnlineTicket
{
    public static string? GetDisplayPin(AccessCredential? credential, IAccessPinProtector? protector = null)
    {
        if (credential == null) return null;
        var pin = !string.IsNullOrEmpty(credential.ProtectedAccessCode)
            ? protector?.Unprotect(credential.BookingId, credential.ProtectedAccessCode)
            : credential.OfflinePayload;
        if (string.IsNullOrEmpty(pin) && !string.IsNullOrWhiteSpace(credential.OfflinePayload))
        {
            pin = TryExtractPinFromJws(credential.OfflinePayload);
        }
        return pin is { Length: 8 } && pin.All(char.IsAsciiDigit)
            && HashPin(pin) == credential.AccessCodeHash ? pin : null;
    }

    private static string? TryExtractPinFromJws(string offlinePayload)
    {
        try
        {
            var parts = offlinePayload.Trim().Split('.');
            if (parts.Length != 3) return offlinePayload.Trim();
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.TryGetProperty("pin", out var pinProp) ? pinProp.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    public static string HashPin(string pin) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(pin)));

    public static bool NeedsRotation(AccessCredential credential, Guid stationId, DateTime now)
    {
        if (credential.QrExpiresAt <= now || credential.QrExpiresAt > now.AddSeconds(120)) return true;
        try
        {
            using var json = JsonDocument.Parse(Convert.FromBase64String(credential.QrPayload));
            var ticket = json.RootElement;
            return ticket.GetProperty("bookingId").GetGuid() != credential.BookingId
                || ticket.GetProperty("stationId").GetGuid() != stationId
                || ticket.GetProperty("nonce").GetString() != credential.QrNonce
                || credential.QrNonce.Length != 64
                || ticket.GetProperty("expiresAt").GetDateTime() != credential.QrExpiresAt;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        { return true; }
    }

    public static void Rotate(AccessCredential credential, Guid stationId, DateTime now)
    {
        credential.QrNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        credential.QrExpiresAt = now.AddSeconds(120);
        credential.QrPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            bookingId = credential.BookingId,
            stationId,
            nonce = credential.QrNonce,
            expiresAt = credential.QrExpiresAt
        })));
    }
}
