namespace API.Controllers;

using Application.DTOs.Auth;
using Application.DTOs.Common;
using Application.Interfaces;
using Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;

/// <summary>
/// UC-T22: Thiết lập tùy chọn kênh nhận thông báo (SMS, Email, WebPush) cho Traveler
/// </summary>
[ApiController]
[Route("api/notifications")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly ISmartLockerDbContext _db;

    public NotificationsController(ISmartLockerDbContext db)
    {
        _db = db;
    }

    private Guid GetUserId()
    {
        var uid = User.FindFirstValue("uid")
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");
        return Guid.TryParse(uid, out var userId) ? userId : Guid.Empty;
    }

    private string? GetUserEmail() => User.FindFirstValue(ClaimTypes.Email) ?? User.FindFirstValue("email");
    private string? GetUserRole() => User.FindFirstValue(ClaimTypes.Role) ?? User.FindFirstValue("role");

    /// <summary>
    /// GET /api/notifications/preferences — Lấy tùy chọn kênh nhận thông báo hiện tại của người dùng (UC-T22)
    /// </summary>
    [HttpGet("preferences")]
    public async Task<IActionResult> GetPreferences()
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(ApiResponse<NotificationPreferenceDto>.ErrorResponse("Vui lòng đăng nhập"));

        var latestPrefLog = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.UserId == userId && a.Action == "USER_NOTIFICATION_PREFS")
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync();

        if (latestPrefLog != null && !string.IsNullOrWhiteSpace(latestPrefLog.Details))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<NotificationPreferenceDto>(latestPrefLog.Details);
                if (saved != null)
                {
                    saved.UpdatedAt = latestPrefLog.CreatedAt;
                    return Ok(ApiResponse<NotificationPreferenceDto>.SuccessResponse(saved));
                }
            }
            catch
            {
                // Fallback về mặc định nếu JSON cũ lỗi
            }
        }

        var defaultPrefs = new NotificationPreferenceDto
        {
            EnableEmail = true,
            EnableSms = false,
            EnableWebPush = true,
            BookingConfirmAlert = true,
            CheckInReminderAlert = true,
            ExpiryWarningAlert = true,
            OverduePenaltyAlert = true,
            PromotionalAlert = false,
            PreferredLanguage = "vi",
            UpdatedAt = DateTime.UtcNow
        };

        return Ok(ApiResponse<NotificationPreferenceDto>.SuccessResponse(defaultPrefs));
    }

    /// <summary>
    /// PUT /api/notifications/preferences — Cập nhật tùy chọn kênh nhận thông báo (SMS, Email, WebPush - UC-T22)
    /// </summary>
    [HttpPut("preferences")]
    public async Task<IActionResult> UpdatePreferences([FromBody] NotificationPreferenceDto request)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(ApiResponse<NotificationPreferenceDto>.ErrorResponse("Vui lòng đăng nhập"));

        // Đảm bảo ít nhất 1 kênh nhận thông báo bảo mật (Email/SMS/WebPush) được bật cho cảnh báo quá hạn
        request.OverduePenaltyAlert = true;
        request.UpdatedAt = DateTime.UtcNow;

        var json = JsonSerializer.Serialize(request);

        var prefLog = new AuditLog
        {
            UserId = userId,
            UserEmail = GetUserEmail(),
            UserRole = GetUserRole() ?? "TRAVELER",
            Action = "USER_NOTIFICATION_PREFS",
            TargetEntity = "UserPreference",
            TargetId = userId.ToString(),
            Details = json,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = DateTime.UtcNow
        };

        _db.AuditLogs.Add(prefLog);
        await _db.SaveChangesAsync();

        return Ok(ApiResponse<NotificationPreferenceDto>.SuccessResponse(
            request,
            "Đã lưu tùy chọn kênh nhận thông báo (SMS, Email, WebPush) thành công"));
    }
}
