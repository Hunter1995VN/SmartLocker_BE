using Application.DTOs.Common;
using Application.DTOs.User;

namespace Application.Interfaces;

/// <summary>
/// Service xử lý nghiệp vụ Quản lý thông tin cá nhân và Tra cứu dư nợ quá hạn.
/// </summary>
public interface IUserProfileService
{
    /// <summary>
    /// Lấy thông tin cá nhân chi tiết của người dùng hiện tại.
    /// </summary>
    Task<ApiResponse<UserProfileDto>> GetProfileAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Cập nhật thông tin cá nhân (Họ tên, SĐT, Avatar).
    /// </summary>
    Task<ApiResponse<UserProfileDto>> UpdateProfileAsync(Guid userId, UpdateProfileRequest request, CancellationToken ct = default);

    /// <summary>
    /// Đổi mật khẩu tài khoản.
    /// </summary>
    Task<ApiResponse<bool>> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct = default);

    /// <summary>
    /// Tra cứu chi tiết dư nợ quá hạn, các đơn đang quá hạn và lịch sử thanh toán nợ quá hạn.
    /// </summary>
    Task<ApiResponse<UserOverdueDebtResponse>> GetOverdueDebtAsync(Guid userId, CancellationToken ct = default);
}
