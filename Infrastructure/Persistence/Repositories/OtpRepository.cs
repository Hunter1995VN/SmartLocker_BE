using Microsoft.EntityFrameworkCore;
using Domain.Entities;
using Domain.Interfaces;

namespace Infrastructure.Persistence.Repositories;

/// <summary>
/// Repository thao tác với bảng OtpCodes bằng EF Core.
/// </summary>
public class OtpRepository : IOtpRepository
{
    private readonly SmartLockerDbContext _db;

    public OtpRepository(SmartLockerDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(OtpCode otp, CancellationToken ct = default)
    {
        await _db.OtpCodes.AddAsync(otp, ct);
    }

    public async Task<OtpCode?> GetLatestActiveAsync(string identifier, string purpose, CancellationToken ct = default)
    {
        return await _db.OtpCodes
            .Where(o => o.Recipient == identifier
                && o.Purpose == purpose
                && o.UsedAt == null
                && o.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public void Update(OtpCode otp)
    {
        _db.OtpCodes.Update(otp);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _db.SaveChangesAsync(ct);
    }
}
