namespace API.Controllers.Admin;

using Application.DTOs.Admin;
using Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

/// <summary>
/// UC-A05 & UC-A06: Manage Users & Bad Debt Blocking — Quản lý tài khoản nội bộ, danh sách khách hàng & chặn tài khoản nợ xấu
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
    /// POST /api/admin/users/staff — Tạo tài khoản Staff mới
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
    /// PATCH /api/admin/users/{userId}/status — Kích hoạt / Chặn tài khoản (UC-A06)
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
    /// PATCH /api/admin/users/{userId}/role — Thay đổi phân quyền (Role)
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
