using Microsoft.EntityFrameworkCore;
using SmartLocker.Domain.Entities;
using SmartLocker.Domain.Interfaces;

namespace SmartLocker.Infrastructure.Persistence.Repositories;

/// <summary>
/// Repository thao tác với bảng Users bằng EF Core.
/// </summary>
public class UserRepository : IUserRepository
{
    private readonly SmartLockerDbContext _db;

    public UserRepository(SmartLockerDbContext db)
    {
        _db = db;
    }

    public async Task<User?> GetByEmailAsync(string email, CancellationToken ct = default)
    {
        return await _db.Users.FirstOrDefaultAsync(u =>
            u.Email == email && u.DeletedAt == null, ct);
    }

    public async Task<User?> GetByPhoneAsync(string phone, CancellationToken ct = default)
    {
        return await _db.Users.FirstOrDefaultAsync(u =>
            u.Phone == phone && u.DeletedAt == null, ct);
    }

    public async Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.Users.FirstOrDefaultAsync(u =>
            u.Id == id && u.DeletedAt == null, ct);
    }

    public async Task<bool> ExistsByEmailAsync(string email, CancellationToken ct = default)
    {
        return await _db.Users.AnyAsync(u => u.Email == email && u.DeletedAt == null, ct);
    }

    public async Task<bool> ExistsByPhoneAsync(string phone, CancellationToken ct = default)
    {
        return await _db.Users.AnyAsync(u => u.Phone == phone && u.DeletedAt == null, ct);
    }

    public async Task AddAsync(User user, CancellationToken ct = default)
    {
        await _db.Users.AddAsync(user, ct);
    }

    public void Update(User user)
    {
        user.UpdatedAt = DateTime.UtcNow;
        _db.Users.Update(user);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _db.SaveChangesAsync(ct);
    }
}
