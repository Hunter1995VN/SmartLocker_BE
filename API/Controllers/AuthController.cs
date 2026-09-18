using Microsoft.AspNetCore.Mvc;
using SmartLocker.Application.DTOs;
using SmartLocker.Application.DTOs.Auth;
using SmartLocker.Application.Services;

namespace SmartLocker.API.Controllers;

/// <summary>
/// Controller xử lý mọi flow xác thực: Đăng ký, Xác thực OTP, Đăng nhập, Quên/Đặt lại mật khẩu.
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AuthService _auth;
    private readonly ILogger<AuthController> _logger;

    public AuthController(AuthService auth, ILogger<AuthController> logger)
    {
        _auth = auth;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/auth/register - Bước 1 đăng ký: tạo user + gửi OTP.
    /// </summary>
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request, CancellationToken ct)
    {
        _logger.LogInformation("📝 Yêu cầu đăng ký mới: {Email}", request.Email);
        var result = await _auth.RegisterInitiateAsync(request, ct);

        if (!result.IsSuccess)
        {
            // 409 nếu trùng, 400 cho lỗi validate
            var statusCode = result.ErrorCode switch
            {
                "EMAIL_EXISTS" or "PHONE_EXISTS" => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest
            };
            return StatusCode(statusCode, new
            {
                success = false,
                code = result.ErrorCode,
                message = result.ErrorMessage
            });
        }

        return Ok(new
        {
            success = true,
            message = "Đăng ký thành công. Vui lòng kiểm tra email để lấy mã OTP.",
            data = result.Data
        });
    }

    /// <summary>
    /// POST /api/auth/verify-otp - Xác thực OTP và kích hoạt tài khoản.
    /// </summary>
    [HttpPost("verify-otp")]
    public async Task<IActionResult> VerifyOtp([FromBody] VerifyOtpRequest request, CancellationToken ct)
    {
        var result = await _auth.VerifyOtpAsync(request, ct);

        if (!result.IsSuccess)
            return BadRequest(new
            {
                success = false,
                code = result.ErrorCode,
                message = result.ErrorMessage
            });

        return Ok(new
        {
            success = true,
            message = "Xác thực OTP thành công. Tài khoản đã được kích hoạt.",
            data = result.Data
        });
    }

    /// <summary>
    /// POST /api/auth/resend-otp - Gửi lại mã OTP.
    /// </summary>
    [HttpPost("resend-otp")]
    public async Task<IActionResult> ResendOtp([FromBody] ResendOtpRequest request, CancellationToken ct)
    {
        var result = await _auth.ResendOtpAsync(request, ct);

        if (!result.IsSuccess)
            return BadRequest(new
            {
                success = false,
                code = result.ErrorCode,
                message = result.ErrorMessage
            });

        return Ok(new
        {
            success = true,
            message = "Đã gửi lại mã OTP. Vui lòng kiểm tra email.",
            data = result.Data
        });
    }

    /// <summary>
    /// POST /api/auth/login - Đăng nhập bằng email + mật khẩu.
    /// </summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        _logger.LogInformation("🔑 Yêu cầu đăng nhập: {Email}", request.Email);
        var result = await _auth.LoginAsync(request, ct);

        if (!result.IsSuccess)
        {
            // 401 cho sai thông tin, 403 cho tài khoản bị khóa
            var statusCode = result.ErrorCode switch
            {
                "INVALID_CREDENTIALS" => StatusCodes.Status401Unauthorized,
                "ACCOUNT_LOCKED" or "ACCOUNT_DISABLED" or "ACCOUNT_NOT_VERIFIED" => StatusCodes.Status403Forbidden,
                _ => StatusCodes.Status400BadRequest
            };
            return StatusCode(statusCode, new
            {
                success = false,
                code = result.ErrorCode,
                message = result.ErrorMessage
            });
        }

        return Ok(new
        {
            success = true,
            message = "Đăng nhập thành công",
            data = result.Data
        });
    }

    /// <summary>
    /// POST /api/auth/forgot-password - Bắt đầu quên mật khẩu.
    /// </summary>
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request, CancellationToken ct)
    {
        var result = await _auth.ForgotPasswordInitiateAsync(request, ct);

        if (!result.IsSuccess)
            return BadRequest(new
            {
                success = false,
                code = result.ErrorCode,
                message = result.ErrorMessage
            });

        // Luôn trả success message dù user có tồn tại hay không (chống leak email)
        return Ok(new
        {
            success = true,
            message = "Nếu email tồn tại trong hệ thống, mã OTP đã được gửi.",
            data = result.Data
        });
    }

    /// <summary>
    /// POST /api/auth/reset-password - Đặt lại mật khẩu sau khi verify OTP.
    /// </summary>
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request, CancellationToken ct)
    {
        var result = await _auth.ResetPasswordAsync(request, ct);

        if (!result.IsSuccess)
            return BadRequest(new
            {
                success = false,
                code = result.ErrorCode,
                message = result.ErrorMessage
            });

        return Ok(new
        {
            success = true,
            message = "Đặt lại mật khẩu thành công. Bạn đã được đăng nhập.",
            data = result.Data
        });
    }

    /// <summary>
    /// POST /api/auth/google — Đăng nhập bằng Google OAuth.
    /// </summary>
    [HttpPost("google")]
    public async Task<IActionResult> GoogleLogin([FromBody] GoogleLoginRequest request, CancellationToken ct)
    {
        _logger.LogInformation("🔑 Đăng nhập Google: {Token}", request.IdToken?.Length > 20 ? request.IdToken[..20] + "..." : "Token quá ngắn");

        if (string.IsNullOrWhiteSpace(request.IdToken))
            return BadRequest(new { success = false, code = "MISSING_TOKEN", message = "Thiếu access token Google" });

        try
        {
            var result = await _auth.GoogleLoginAsync(request, ct);

            if (!result.IsSuccess)
                return BadRequest(new
                {
                    success = false,
                    code = result.ErrorCode,
                    message = result.ErrorMessage
                });

            return Ok(new
            {
                success = true,
                message = "Đăng nhập Google thành công",
                data = result.Data
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Lỗi đăng nhập Google");
            return StatusCode(500, new
            {
                success = false,
                code = "SERVER_ERROR",
                message = $"Lỗi server khi đăng nhập Google: {ex.Message}"
            });
        }
    }
}
