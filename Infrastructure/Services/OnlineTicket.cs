namespace Infrastructure.Services;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Entities;

/// <summary>
/// UC-T09: Online tickets are opaque bearer tokens with dynamic QR code rotation and 8-digit access PIN.
/// Booking identifiers alone never authorize access.
/// </summary>
public static class OnlineTicket
{
    public static string HashPin(string pin) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(pin)));

    public static void Rotate(AccessCredential credential, Guid stationId, DateTime now)
    {
        credential.QrNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        credential.QrExpiresAt = now.AddSeconds(60);
        credential.QrPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            bookingId = credential.BookingId,
            stationId,
            nonce = credential.QrNonce,
            expiresAt = credential.QrExpiresAt
        })));
    }
}
