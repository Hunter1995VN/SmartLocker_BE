namespace Infrastructure.Services;

using Application.DTOs.Admin;
using Application.DTOs.Common;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

public class AdminUserService : IAdminUserService
{
    private readonly ISmartLockerDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAuditLogService _auditLog;

    public AdminUserService(ISmartLockerDbContext db, IPasswordHasher passwordHasher, IAuditLogService auditLog)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _auditLog = auditLog;
    }

    public async Task<ApiResponse<List<UserAdminListItemDto>>> GetUsersAsync(string? search, string? role, string? status, int page, int pageSize)
    {
        var query = _db.Users.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(u => u.FullName.Contains(search) || u.Email.Contains(search) || u.Phone.Contains(search));
        }

        if (!string.IsNullOrWhiteSpace(role) && Enum.TryParse<UserRole>(role, true, out var roleEnum))
        {
            query = query.Where(u => u.Role == roleEnum);
        }

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<UserStatus>(status, true, out var statusEnum))
        {
            query = query.Where(u => u.Status == statusEnum);
        }

        var totalItems = await query.CountAsync();
        var users = await query
            .OrderByDescending(u => u.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new UserAdminListItemDto
            {
                Id = u.Id,
                FullName = u.FullName,
                Email = u.Email,
                Phone = u.Phone,
                Role = u.Role.ToString(),
                Status = u.Status.ToString(),
                OverdueDebt = u.OverdueDebt,
                CreatedAt = u.CreatedAt
            })
            .ToListAsync();

        return ApiResponse<List<UserAdminListItemDto>>.SuccessResponse(users);
    }

    public async Task<ApiResponse<UserAdminListItemDto>> CreateStaffAsync(CreateStaffRequest request, Guid adminUserId)
    {
        var existingUser = await _db.Users.FirstOrDefaultAsync(u => u.Email == request.Email || u.Phone == request.Phone);
        if (existingUser != null)
            return ApiResponse<UserAdminListItemDto>.ErrorResponse("Email hoặc Số điện thoại đã được đăng ký trong hệ thống");

        var hashedPassword = _passwordHasher.Hash(request.Password);

        var staff = new User
        {
            Id = Guid.NewGuid(),
            FullName = request.FullName,
            Email = request.Email,
            Phone = request.Phone,
            PasswordHash = hashedPassword,
            Role = request.Role,
            Status = UserStatus.ACTIVE,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Users.Add(staff);
        await _db.SaveChangesAsync();

        await _auditLog.LogAsync(adminUserId, null, "ADMIN", "CREATE_STAFF", "User", staff.Id.ToString(), $"Tạo tài khoản nhân viên mới: {staff.FullName} ({staff.Email}) - Role: {staff.Role}");

        var dto = new UserAdminListItemDto
        {
            Id = staff.Id,
            FullName = staff.FullName,
            Email = staff.Email,
            Phone = staff.Phone,
            Role = staff.Role.ToString(),
            Status = staff.Status.ToString(),
            OverdueDebt = 0,
            CreatedAt = staff.CreatedAt
        };

        return ApiResponse<UserAdminListItemDto>.SuccessResponse(dto, "Tạo tài khoản nhân viên thành công");
    }

    public async Task<ApiResponse<bool>> UpdateUserStatusAsync(Guid userId, UpdateUserStatusRequest request, Guid adminUserId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
            return ApiResponse<bool>.ErrorResponse("Không tìm thấy người dùng");

        var oldStatus = user.Status;
        user.Status = request.Status;
        user.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        await _auditLog.LogAsync(adminUserId, null, "ADMIN", "UPDATE_USER_STATUS", "User", user.Id.ToString(), $"Cập nhật trạng thái user {user.Email} từ {oldStatus} sang {request.Status}. Lý do: {request.Reason}");

        return ApiResponse<bool>.SuccessResponse(true, $"Đã chuyển trạng thái tài khoản thành {request.Status}");
    }

    public async Task<ApiResponse<bool>> UpdateUserRoleAsync(Guid userId, UpdateUserRoleRequest request, Guid adminUserId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
            return ApiResponse<bool>.ErrorResponse("Không tìm thấy người dùng");

        var oldRole = user.Role;
        user.Role = request.Role;
        user.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        await _auditLog.LogAsync(adminUserId, null, "ADMIN", "UPDATE_USER_ROLE", "User", user.Id.ToString(), $"Đổi phân quyền user {user.Email} từ {oldRole} sang {request.Role}");

        return ApiResponse<bool>.SuccessResponse(true, $"Đã cập nhật phân quyền thành {request.Role}");
    }
}
