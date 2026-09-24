namespace API.Controllers.Admin;

using Application.DTOs.Admin;
using Application.DTOs.Common;
using Application.Interfaces;
using Domain.Enums;
using Domain.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// UC-A01: Admin/Staff Login — Đăng nhập phân quyền cho Admin Portal
/// </summary>
[ApiController]
[Route("api/admin/auth")]
public class AdminAuthController : ControllerBase
{
    private readonly ISmartLockerDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly IAuditLogService _auditLog;
    private readonly ILogger<AdminAuthController> _logger;

    public AdminAuthController(
        ISmartLockerDbContext db,
        IPasswordHasher passwordHasher,
        IJwtTokenService jwtTokenService,
        IAuditLogService auditLog,
        ILogger<AdminAuthController> logger)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _jwtTokenService = jwtTokenService;
        _auditLog = auditLog;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/admin/auth/login — Đăng nhập Admin/Staff
    /// </summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] AdminLoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(ApiResponse<object>.ErrorResponse("Email và mật khẩu không được để trống"));

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == request.Email);

        if (user == null || !_passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            _logger.LogWarning("❌ Admin login thất bại cho email: {Email}", request.Email);
            return Unauthorized(ApiResponse<object>.ErrorResponse("Email hoặc mật khẩu không đúng"));
        }

        // Chỉ cho phép ADMIN và STAFF đăng nhập Admin Portal
        if (user.Role != UserRole.ADMIN && user.Role != UserRole.STAFF)
        {
            _logger.LogWarning("⛔ User {Email} không có quyền truy cập Admin Portal (Role: {Role})", user.Email, user.Role);
            return StatusCode(403, ApiResponse<object>.ErrorResponse("Bạn không có quyền truy cập Admin Portal. Chỉ Admin và Staff được phép."));
        }

        if (user.Status != UserStatus.ACTIVE)
            return StatusCode(403, ApiResponse<object>.ErrorResponse($"Tài khoản đang ở trạng thái {user.Status}. Liên hệ quản trị viên."));

        var (token, expiresAt) = _jwtTokenService.GenerateAccessToken(user.Id, user.Email, user.Role.ToString());

        _logger.LogInformation("✅ Admin login thành công: {Email} (Role: {Role})", user.Email, user.Role);

        await _auditLog.LogAsync(user.Id, user.Email, user.Role.ToString(), "ADMIN_LOGIN", "User", user.Id.ToString(), $"Đăng nhập Admin Portal thành công");

        var response = new AdminLoginResponse(
            Token: token,
            UserId: user.Id,
            FullName: user.FullName,
            Email: user.Email,
            Role: user.Role.ToString(),
            AvatarUrl: user.AvatarUrl
        );

        return Ok(ApiResponse<AdminLoginResponse>.SuccessResponse(response, "Đăng nhập Admin Portal thành công"));
    }
}
