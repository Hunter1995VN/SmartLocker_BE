namespace Application.Interfaces;

using Application.DTOs.Admin;
using Application.DTOs.Common;

public interface IAdminUserService
{
    Task<ApiResponse<List<UserAdminListItemDto>>> GetUsersAsync(string? search, string? role, string? status, int page, int pageSize, bool? hasDebt = null);
    Task<ApiResponse<UserAdminListItemDto>> CreateStaffAsync(CreateStaffRequest request, Guid adminUserId);
    Task<ApiResponse<bool>> UpdateUserStatusAsync(Guid userId, UpdateUserStatusRequest request, Guid adminUserId);
    Task<ApiResponse<bool>> UpdateUserRoleAsync(Guid userId, UpdateUserRoleRequest request, Guid adminUserId);
    Task<ApiResponse<UserAdminListItemDto>> UpdateUserDebtAsync(Guid userId, UpdateUserDebtRequest request, Guid adminUserId);
    Task<ApiResponse<int>> BlockAllDebtorsAsync(string? reason, Guid adminUserId);
}
