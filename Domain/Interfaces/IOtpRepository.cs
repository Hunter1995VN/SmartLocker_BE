using SmartLocker.Domain.Entities;

namespace SmartLocker.Domain.Interfaces;

/// <summary>
/// Giao diện Repository cho bảng OtpCodes.
/// </summary>
public interface IOtpRepository
{
    Task AddAsync(OtpCode otp, CancellationToken ct = default);
    Task<OtpCode?> GetLatestActiveAsync(string identifier, string purpose, CancellationToken ct = default);
    void Update(OtpCode otp);
    Task SaveChangesAsync(CancellationToken ct = default);
}
