using Application.Interfaces;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
namespace API.Services;
public sealed class AccessPinProtector(IDataProtectionProvider provider) : IAccessPinProtector
{
    private IDataProtector ForBooking(Guid id) => provider.CreateProtector("SmartLocker.AccessPin.v1", id.ToString("N"));
    public string Protect(Guid bookingId, string pin)
    {
        if (pin.Length != 8 || !pin.All(char.IsAsciiDigit)) throw new ArgumentException("Expected eight numeric digits.", nameof(pin));
        return ForBooking(bookingId).Protect(pin);
    }
    public string? Unprotect(Guid bookingId, string protectedPin)
    {
        try { return ForBooking(bookingId).Unprotect(protectedPin); }
        catch (CryptographicException) { return null; }
    }
}
