namespace API.Controllers;

using Application.DTOs.Common;
using Application.DTOs.User;
using Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ProfileController : ControllerBase
{
    private readonly IUserProfileService _profileService;
    private readonly IBookingService _bookingService;
    private readonly ILogger<ProfileController> _logger;

    public ProfileController(
        IUserProfileService profileService,
        IBookingService bookingService,
        ILogger<ProfileController> logger)
    {
        _profileService = profileService;
        _bookingService = bookingService;
        _logger = logger;
    }

    /// <summary>
    /// Lấy UserId từ token JWT claims
    /// </summary>
    private Guid GetUserId()
    {
        var uid = User.FindFirstValue("uid")
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");

        return Guid.TryParse(uid, out var userId) ? userId : Guid.Empty;
    }

    /// <summary>
    /// Lấy thông tin cá nhân của người dùng hiện tại (Profile & Overview Dư nợ)
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetProfile(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(ApiResponse<UserProfileDto>.ErrorResponse("Không xác định được danh tính người dùng. Vui lòng đăng nhập lại."));

        var result = await _profileService.GetProfileAsync(userId, ct);
        if (!result.Success)
            return NotFound(result);

        return Ok(result);
    }

    /// <summary>
    /// Cập nhật thông tin cá nhân (Họ và tên, Số điện thoại, Ảnh đại diện)
    /// </summary>
    [HttpPut]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(ApiResponse<UserProfileDto>.ErrorResponse("Không xác định được danh tính người dùng. Vui lòng đăng nhập lại."));

        var result = await _profileService.UpdateProfileAsync(userId, request, ct);
        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }

    /// <summary>
    /// Đổi mật khẩu tài khoản
    /// </summary>
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(ApiResponse<bool>.ErrorResponse("Không xác định được danh tính người dùng. Vui lòng đăng nhập lại."));

        var result = await _profileService.ChangePasswordAsync(userId, request, ct);
        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }

    /// <summary>
    /// Tra cứu chi tiết dư nợ quá hạn, các đơn đang phát sinh phụ phí và lịch sử thanh toán
    /// </summary>
    [HttpGet("overdue-debt")]
    public async Task<IActionResult> GetOverdueDebt(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(ApiResponse<UserOverdueDebtResponse>.ErrorResponse("Không xác định được danh tính người dùng. Vui lòng đăng nhập lại."));

        var result = await _profileService.GetOverdueDebtAsync(userId, ct);
        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }

    /// <summary>
    /// Tạo yêu cầu thanh toán phụ phí quá hạn trực tiếp qua cổng thanh toán PayOS
    /// </summary>
    [HttpPost("pay-overdue/{bookingId:guid}")]
    public async Task<IActionResult> PayOverdueFee(Guid bookingId)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được danh tính người dùng." });

        var result = await _bookingService.PayOverdueFeeAsync(userId, bookingId);
        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }
}
