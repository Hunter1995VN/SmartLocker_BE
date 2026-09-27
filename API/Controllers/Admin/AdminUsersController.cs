namespace API.Controllers.Admin;

using Application.DTOs.Admin;
using Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

/// <summary>
/// UC-A06: Manage Internal Users — Quản lý tài khoản Staff, phân quyền, kích hoạt/khóa
/// </summary>
[ApiController]
[Route("api/admin/users")]
[Authorize(Roles = "ADMIN")]
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
    /// GET /api/admin/users — Danh sách tất cả người dùng (hỗ trợ filter: role, status, search, phân trang)
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetUsers(
        [FromQuery] string? search = null,
        [FromQuery] string? role = null,
        [FromQuery] string? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var result = await _userService.GetUsersAsync(search, role, status, page, pageSize);
        return Ok(result);
    }

    /// <summary>
    /// POST /api/admin/users/staff — Tạo tài khoản Staff mới
    /// </summary>
    [HttpPost("staff")]
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
    /// PATCH /api/admin/users/{userId}/status — Kích hoạt / Khóa tài khoản
    /// </summary>
    [HttpPatch("{userId:guid}/status")]
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
}
