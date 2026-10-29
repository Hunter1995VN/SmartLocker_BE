namespace API.Controllers.Admin;

using Application.DTOs.Admin;
using Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

/// <summary>
/// UC-A05 & UC-A06: Quản lý tài khoản nội bộ & Phân quyền vai trò (Admin / Staff RBAC) + Kiểm soát danh sách khách hàng & nợ xấu
/// </summary>
[ApiController]
[Route("api/admin/users")]
[Authorize(Roles = "ADMIN,STAFF")]
public class AdminUsersController : ControllerBase
{
    private readonly IAdminUserService _userService;

    public AdminUsersController(IAdminUserService userService)
    {
        _userService = userService;
    }

    private Guid GetUserId()
    {
        var uid = User.FindFirstValue("uid")
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");
        return Guid.TryParse(uid, out var userId) ? userId : Guid.Empty;
    }

    /// <summary>
    /// GET /api/admin/users — Danh sách tất cả người dùng (hỗ trợ filter: role, status, hasDebt, search, phân trang)
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetUsers(
        [FromQuery] string? search = null,
        [FromQuery] string? role = null,
        [FromQuery] string? status = null,
        [FromQuery] bool? hasDebt = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var result = await _userService.GetUsersAsync(search, role, status, page, pageSize, hasDebt);
        return Ok(result);
    }

    /// <summary>
    /// GET /api/admin/users/internal — Danh sách tài khoản nội bộ (Admin & Staff) kèm thống kê phiên đăng nhập và thao tác (UC-A05)
    /// </summary>
    [HttpGet("internal")]
    public async Task<IActionResult> GetInternalUsers(
        [FromQuery] string? search = null,
        [FromQuery] string? role = null,
        [FromQuery] string? status = null)
    {
        var result = await _userService.GetInternalUsersAsync(search, role, status);
        return Ok(result);
    }

    /// <summary>
    /// GET /api/admin/users/rbac-summary — Ma trận Phân quyền RBAC, tổng quan vai trò & nhật ký thay đổi phân quyền (UC-A05)
    /// </summary>
    [HttpGet("rbac-summary")]
    public async Task<IActionResult> GetRbacSummary()
    {
        var result = await _userService.GetRbacSummaryAsync();
        return Ok(result);
    }

    /// <summary>
    /// POST /api/admin/users/staff — Tạo tài khoản nội bộ (Staff / Admin) mới (UC-A05)
    /// </summary>
    [HttpPost("staff")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> CreateStaff([FromBody] CreateStaffRequest request)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được người dùng" });

        var result = await _userService.CreateStaffAsync(request, userId);
        if (!result.Success)
            return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// PUT /api/admin/users/{userId} — Cập nhật hồ sơ tài khoản nội bộ (Họ tên, Email, SĐT, Vai trò) (UC-A05)
    /// </summary>
    [HttpPut("{userId:guid}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> UpdateInternalUser(Guid userId, [FromBody] UpdateInternalUserRequest request)
    {
        var adminId = GetUserId();
        if (adminId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được người dùng" });

        var result = await _userService.UpdateInternalUserAsync(userId, request, adminId);
        if (!result.Success)
            return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// POST /api/admin/users/{userId}/reset-password — Cấp lại / Đặt lại mật khẩu cho tài khoản nội bộ (UC-A05)
    /// </summary>
    [HttpPost("{userId:guid}/reset-password")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> ResetInternalPassword(Guid userId, [FromBody] ResetInternalPasswordRequest request)
    {
        var adminId = GetUserId();
        if (adminId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được người dùng" });

        var result = await _userService.ResetInternalPasswordAsync(userId, request, adminId);
        if (!result.Success)
            return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// DELETE /api/admin/users/{userId} — Vô hiệu hóa / Thu hồi tài khoản nội bộ (Soft Delete -> DELETED) (UC-A05)
    /// </summary>
    [HttpDelete("{userId:guid}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> DeactivateInternalUser(Guid userId, [FromQuery] string? reason = null)
    {
        var adminId = GetUserId();
        if (adminId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được người dùng" });

        var result = await _userService.DeactivateInternalUserAsync(userId, reason, adminId);
        if (!result.Success)
            return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// PATCH /api/admin/users/{userId}/status — Kích hoạt / Chặn tài khoản (UC-A05 & UC-A06)
    /// </summary>
    [HttpPatch("{userId:guid}/status")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> UpdateUserStatus(Guid userId, [FromBody] UpdateUserStatusRequest request)
    {
        var adminId = GetUserId();
        if (adminId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được người dùng" });

        var result = await _userService.UpdateUserStatusAsync(userId, request, adminId);
        if (!result.Success)
            return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// PATCH /api/admin/users/{userId}/role — Thay đổi phân quyền vai trò RBAC (UC-A05)
    /// </summary>
    [HttpPatch("{userId:guid}/role")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> UpdateUserRole(Guid userId, [FromBody] UpdateUserRoleRequest request)
    {
        var adminId = GetUserId();
        if (adminId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được người dùng" });

        var result = await _userService.UpdateUserRoleAsync(userId, request, adminId);
        if (!result.Success)
            return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// PATCH /api/admin/users/{userId}/debt — Cập nhật dư nợ quá hạn & tự động chặn/mở chặn tài khoản nợ xấu (UC-A06)
    /// </summary>
    [HttpPatch("{userId:guid}/debt")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> UpdateUserDebt(Guid userId, [FromBody] UpdateUserDebtRequest request)
    {
        var adminId = GetUserId();
        if (adminId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được người dùng" });

        var result = await _userService.UpdateUserDebtAsync(userId, request, adminId);
        if (!result.Success)
            return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// POST /api/admin/users/block-debtors — Chặn hàng loạt tất cả tài khoản khách hàng có nợ xấu (UC-A06)
    /// </summary>
    [HttpPost("block-debtors")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> BlockAllDebtors([FromBody] BlockDebtorsRequest? request)
    {
        var adminId = GetUserId();
        if (adminId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được người dùng" });

        var result = await _userService.BlockAllDebtorsAsync(request?.Reason, adminId);
        if (!result.Success)
            return BadRequest(result);
        return Ok(result);
    }
}
