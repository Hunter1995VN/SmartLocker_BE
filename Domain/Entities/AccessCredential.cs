namespace Domain.Entities;

public class AccessCredential
{
    public Guid Id { get; set; }
    public Guid BookingId { get; set; }
    public string QrPayload { get; set; } = string.Empty;
    public string QrNonce { get; set; } = string.Empty;
    public DateTime QrExpiresAt { get; set; }
    public string AccessCodeHash { get; set; } = string.Empty;
    public string? OfflinePayload { get; set; }
    public DateTime? OfflineExpiresAt { get; set; }
    public string? OfflineNonce { get; set; }
    public short AuthFailedAttempts { get; set; }
    public DateTime? AuthLockUntil { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime IssuedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokeReason { get; set; }

    // Navigation
    public virtual Booking Booking { get; set; } = null!;
}
