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

    // ───────────── Ảnh đại diện: tải file lên (không nhập URL) ─────────────
    private const int MaxAvatarBytes = 2 * 1024 * 1024;
    private static readonly System.Text.RegularExpressions.Regex AvatarFileName =
        new(@"^[0-9a-f]{32}-[0-9a-f]{32}\.(jpg|png|webp)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string AvatarDirectory => Path.Combine(AppContext.BaseDirectory, "App_Data", "avatars");

    /// <summary>Nhận diện định dạng bằng chữ ký file (không tin tên file hay Content-Type do client gửi).</summary>
    private static string? DetectImageExtension(byte[] head)
    {
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return "jpg";
        if (head.Length >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47
            && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A) return "png";
        if (head.Length >= 12 && head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46
            && head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50) return "webp";
        return null;
    }

    private void DeleteStoredAvatar(string? avatarUrl)
    {
        const string prefix = "/api/Profile/avatar/";
        if (avatarUrl == null || !avatarUrl.StartsWith(prefix, StringComparison.Ordinal)) return;
        var name = avatarUrl[prefix.Length..];
        if (!AvatarFileName.IsMatch(name)) return;
        try { System.IO.File.Delete(Path.Combine(AvatarDirectory, name)); }
        catch (Exception ex) { _logger.LogWarning(ex, "Không xóa được ảnh đại diện cũ."); }
    }

    /// <summary>
    /// Tải ảnh đại diện lên (multipart/form-data, field "file"; JPEG/PNG/WebP, tối đa 2 MB).
    /// </summary>
    [HttpPost("avatar")]
    [RequestSizeLimit(MaxAvatarBytes + 64 * 1024)]
    public async Task<IActionResult> UploadAvatar(IFormFile? file, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(ApiResponse<UserProfileDto>.ErrorResponse("Không xác định được danh tính người dùng. Vui lòng đăng nhập lại."));
        if (file == null || file.Length == 0)
            return BadRequest(ApiResponse<UserProfileDto>.ErrorResponse("Vui lòng chọn một file ảnh."));
        if (file.Length > MaxAvatarBytes)
            return BadRequest(ApiResponse<UserProfileDto>.ErrorResponse("Ảnh quá lớn. Tối đa 2 MB."));

        byte[] bytes;
        await using (var input = file.OpenReadStream())
        {
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
        }
        if (bytes.Length > MaxAvatarBytes)
            return BadRequest(ApiResponse<UserProfileDto>.ErrorResponse("Ảnh quá lớn. Tối đa 2 MB."));
        var ext = DetectImageExtension(bytes);
        if (ext == null)
            return BadRequest(ApiResponse<UserProfileDto>.ErrorResponse("Chỉ chấp nhận ảnh JPEG, PNG hoặc WebP."));

        var before = await _profileService.GetProfileAsync(userId, ct);
        if (!before.Success)
            return NotFound(before);

        Directory.CreateDirectory(AvatarDirectory);
        var fileName = $"{userId:N}-{Guid.NewGuid():N}.{ext}";
        await System.IO.File.WriteAllBytesAsync(Path.Combine(AvatarDirectory, fileName), bytes, ct);

        var result = await _profileService.SetAvatarAsync(userId, "/api/Profile/avatar/" + fileName, ct);
        if (!result.Success)
        {
            DeleteStoredAvatar("/api/Profile/avatar/" + fileName);
            return BadRequest(result);
        }
        DeleteStoredAvatar(before.Data?.AvatarUrl);
        return Ok(result);
    }

    /// <summary>Xóa ảnh đại diện hiện tại.</summary>
    [HttpDelete("avatar")]
    public async Task<IActionResult> DeleteAvatar(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(ApiResponse<UserProfileDto>.ErrorResponse("Không xác định được danh tính người dùng. Vui lòng đăng nhập lại."));
        var before = await _profileService.GetProfileAsync(userId, ct);
        if (!before.Success)
            return NotFound(before);
        var result = await _profileService.SetAvatarAsync(userId, null, ct);
        if (!result.Success)
            return BadRequest(result);
        DeleteStoredAvatar(before.Data?.AvatarUrl);
        return Ok(result);
    }

    /// <summary>Phục vụ ảnh đại diện (công khai vì thẻ img không gửi được token; tên file ngẫu nhiên, đã kiểm tra chặt).</summary>
    [HttpGet("avatar/{fileName}")]
    [AllowAnonymous]
    public IActionResult GetAvatar(string fileName)
    {
        if (!AvatarFileName.IsMatch(fileName)) return NotFound();
        var path = Path.Combine(AvatarDirectory, fileName);
        if (!System.IO.File.Exists(path)) return NotFound();
        var contentType = fileName.EndsWith(".png", StringComparison.Ordinal) ? "image/png"
            : fileName.EndsWith(".webp", StringComparison.Ordinal) ? "image/webp" : "image/jpeg";
        Response.Headers.CacheControl = "public, max-age=86400";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return PhysicalFile(path, contentType);
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
