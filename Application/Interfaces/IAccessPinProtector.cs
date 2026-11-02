namespace Application.Interfaces;
public interface IAccessPinProtector
{
    string Protect(Guid bookingId, string pin);
    string? Unprotect(Guid bookingId, string protectedPin);
}
