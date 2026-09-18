using System.Security.Cryptography;
using System.Text;
using SmartLocker.Application.DTOs;
using SmartLocker.Application.DTOs.Auth;
using SmartLocker.Domain.Entities;
using SmartLocker.Domain.Interfaces;

namespace SmartLocker.Application.Services;

/// <summary>
/// Orchestrator xử lý tất cả flow xác thực: Đăng ký, OTP, Đăng nhập, Quên mật khẩu.
/// Đây là single point of contact cho Authentication - thiết kế theo pattern Application Service.
/// </summary>
public class AuthService
{
    private readonly IUserRepository _users;
    private readonly IOtpRepository _otps;
    private readonly IPasswordHasher _hasher;
    private readonly IOtpGenerator _otpGenerator;
    private readonly IOtpSender _otpSender;
    private readonly IJwtTokenService _jwt;
    private readonly IGoogleAuthService _googleAuthService;

    public AuthService(
        IUserRepository users,
        IOtpRepository otps,
        IPasswordHasher hasher,
        IOtpGenerator otpGenerator,
        IOtpSender otpSender,
        IJwtTokenService jwt,
        IGoogleAuthService googleAuthService)
    {
        _users = users;
        _otps = otps;
        _hasher = hasher;
        _otpGenerator = otpGenerator;
        _otpSender = otpSender;
        _jwt = jwt;
        _googleAuthService = googleAuthService;
    }

    #region 1. ĐĂNG KÝ

    /// <summary>
    /// Bước 1 - Tạo tài khoản và gửi OTP về email để xác thực.
    /// </summary>
    public async Task<Result<OtpSentResponse>> RegisterInitiateAsync(RegisterRequest req, CancellationToken ct = default)
    {
        // 1. Validate cơ bản
        if (string.IsNullOrWhiteSpace(req.FullName) || req.FullName.Length < 2)
            return Result<OtpSentResponse>.Failure("INVALID_NAME", "Vui lòng nhập họ và tên hợp lệ");

        if (!req.Email.Contains("@") || !req.Email.Contains("."))
            return Result<OtpSentResponse>.Failure("INVALID_EMAIL", "Email không đúng định dạng");

        if (req.Phone.Length < 10 || req.Phone.Length > 11)
            return Result<OtpSentResponse>.Failure("INVALID_PHONE", "Số điện thoại phải có 10-11 số");

        if (req.Password.Length < 8)
            return Result<OtpSentResponse>.Failure("WEAK_PASSWORD", "Mật khẩu phải có ít nhất 8 ký tự");

        if (req.Password != req.ConfirmPassword)
            return Result<OtpSentResponse>.Failure("PASSWORD_MISMATCH", "Mật khẩu xác nhận không khớp");

        if (!req.AgreeTerms)
            return Result<OtpSentResponse>.Failure("TERMS_NOT_AGREED", "Bạn cần đồng ý với điều khoản dịch vụ");

        // 2. Check trùng email & phone
        if (await _users.ExistsByEmailAsync(req.Email, ct))
            return Result<OtpSentResponse>.Failure("EMAIL_EXISTS", "Email này đã được đăng ký");

        if (await _users.ExistsByPhoneAsync(req.Phone, ct))
            return Result<OtpSentResponse>.Failure("PHONE_EXISTS", "Số điện thoại này đã được đăng ký");

        // 3. Hash mật khẩu
        var passwordHash = _hasher.Hash(req.Password);

        // 4. Tạo user với trạng thái PendingVerification (chưa kích hoạt)
        var user = new User
        {
            Id = Guid.NewGuid(), // Gán Id trước để UserId của OTP không bị null
            FullName = req.FullName.Trim(),
            Email = req.Email.ToLowerInvariant().Trim(),
            Phone = req.Phone.Trim(),
            PasswordHash = passwordHash,
            Role = "TRAVELER",
            Status = "PENDING_VERIFICATION",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await _users.AddAsync(user, ct);
        // Lưu user TRƯỚC để FK constraint của OtpCode (UserId) được thỏa mãn
        await _users.SaveChangesAsync(ct);

        // 5. Sinh mã OTP, hash & lưu DB
        var plainCode = _otpGenerator.Generate();
        var result = await SaveAndSendOtpAsync(req.Email, plainCode, "Register", ct, userId: user.Id);

        if (!result.IsSuccess)
            return Result<OtpSentResponse>.Failure(result.ErrorCode!, result.ErrorMessage!);

        return Result<OtpSentResponse>.Success(new OtpSentResponse
        {
            Email = req.Email,
            Purpose = "Register",
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            ResendCountdown = 60
        });
    }

    /// <summary>
    /// Bước 2 - Verify OTP. Nếu đúng → kích hoạt tài khoản + cấp JWT.
    /// </summary>
    public async Task<Result<AuthResponse>> VerifyOtpAsync(VerifyOtpRequest req, CancellationToken ct = default)
    {
        try
        {
            var purpose = string.IsNullOrEmpty(req.Purpose) ? "Register" : req.Purpose;
            var identifier = req.Email.ToLowerInvariant().Trim();

            // 1. Lấy OTP còn hiệu lực
            var otp = await _otps.GetLatestActiveAsync(identifier, purpose, ct);
            if (otp == null)
                return Result<AuthResponse>.Failure("OTP_NOT_FOUND", "Mã OTP không hợp lệ hoặc đã hết hạn. Vui lòng yêu cầu gửi lại.");

            // 2. Kiểm tra số lần sai
            if (!otp.CanRetry())
            {
                if (otp.IsExpired())
                    return Result<AuthResponse>.Failure("OTP_EXPIRED", "Mã OTP đã hết hạn, vui lòng yêu cầu mã mới");
                if (otp.Attempts >= otp.MaxAttempts)
                    return Result<AuthResponse>.Failure("OTP_LOCKED", "Bạn đã nhập sai quá số lần cho phép. Vui lòng yêu cầu mã mới");
                return Result<AuthResponse>.Failure("OTP_USED", "Mã OTP đã được sử dụng");
            }

            // 3. So sánh hash (không so sánh plaintext)
            var inputHash = HashOtp(req.Code);
            if (inputHash != otp.CodeHash)
            {
                otp.Attempts += 1;
                _otps.Update(otp);
                await _otps.SaveChangesAsync(ct);
                var remaining = otp.MaxAttempts - otp.Attempts;
                return Result<AuthResponse>.Failure("OTP_WRONG", $"Mã OTP không chính xác. Bạn còn {remaining} lần thử.");
            }

            // 4. Đánh dấu OTP đã dùng
            otp.UsedAt = DateTime.UtcNow;
            _otps.Update(otp);

            // 5. Tìm user theo mục đích
            var user = await _users.GetByEmailAsync(identifier, ct);
            if (user == null)
                return Result<AuthResponse>.Failure("USER_NOT_FOUND", "Không tìm thấy tài khoản");

            // 6. Kích hoạt user nếu đang Pending
            if (user.Status == "PENDING_VERIFICATION")
            {
                user.Status = "ACTIVE";
                _users.Update(user);
            }

            // 7. Lưu tất cả một lần (OTP + User cùng DbContext)
            await _users.SaveChangesAsync(ct);

            // 8. Cấp JWT
            var (token, expiresAt) = _jwt.GenerateAccessToken(user.Id, user.Email, user.Role);

            return Result<AuthResponse>.Success(new AuthResponse
            {
                UserId = user.Id,
                FullName = user.FullName,
                Email = user.Email,
                Phone = user.Phone,
                Role = user.Role,
                AccessToken = token,
                RefreshToken = _jwt.GenerateRefreshToken(),
                AccessTokenExpiresAt = expiresAt
            });
        }
        catch (Exception ex)
        {
            // Log lỗi để debug - trả về 500 có message rõ ràng
            Console.WriteLine($"❌ [VerifyOtp ERROR] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return Result<AuthResponse>.Failure("SERVER_ERROR", $"Lỗi server: {ex.Message}");
        }
    }

    #endregion

    #region 2. ĐĂNG NHẬP

    /// <summary>
    /// Đăng nhập bằng email + mật khẩu.
    /// </summary>
    public async Task<Result<AuthResponse>> LoginAsync(LoginRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
            return Result<AuthResponse>.Failure("INVALID_CREDENTIALS", "Email và mật khẩu là bắt buộc");

        var identifier = req.Email.ToLowerInvariant().Trim();
        var user = await _users.GetByEmailAsync(identifier, ct);

        if (user == null || !_hasher.Verify(req.Password, user.PasswordHash))
            return Result<AuthResponse>.Failure("INVALID_CREDENTIALS", "Email hoặc mật khẩu không chính xác");

        if (user.Status == "PENDING_VERIFICATION")
            return Result<AuthResponse>.Failure("ACCOUNT_NOT_VERIFIED", "Tài khoản chưa được xác thực. Vui lòng kiểm tra email.");

        if (user.Status == "SUSPENDED")
            return Result<AuthResponse>.Failure("ACCOUNT_LOCKED", "Tài khoản đang bị tạm khóa. Liên hệ hỗ trợ.");

        if (user.Status == "DELETED")
            return Result<AuthResponse>.Failure("ACCOUNT_DISABLED", "Tài khoản đã bị vô hiệu hóa");

        var (token, expiresAt) = _jwt.GenerateAccessToken(user.Id, user.Email, user.Role);

        return Result<AuthResponse>.Success(new AuthResponse
        {
            UserId = user.Id,
            FullName = user.FullName,
            Email = user.Email,
            Phone = user.Phone,
            Role = user.Role,
            AccessToken = token,
            RefreshToken = _jwt.GenerateRefreshToken(),
            AccessTokenExpiresAt = expiresAt
        });
    }

    #endregion

    #region 3. QUÊN MẬT KHẨU

    /// <summary>
    /// Bước 1 quên MK - Gửi OTP về email.
    /// Luôn trả về success (tránh leak email có tồn tại hay không).
    /// </summary>
    public async Task<Result<OtpSentResponse>> ForgotPasswordInitiateAsync(ForgotPasswordRequest req, CancellationToken ct = default)
    {
        var identifier = req.Email.ToLowerInvariant().Trim();
        var user = await _users.GetByEmailAsync(identifier, ct);

        // Không tiết lộ email có tồn tại hay không
        if (user == null)
            return Result<OtpSentResponse>.Success(new OtpSentResponse
            {
                Email = req.Email,
                Purpose = "ForgotPassword",
                ExpiresAt = DateTime.UtcNow.AddMinutes(5),
                ResendCountdown = 60
            });

        var plainCode = _otpGenerator.Generate();
        var result = await SaveAndSendOtpAsync(identifier, plainCode, "ForgotPassword", ct);

        if (!result.IsSuccess)
            return Result<OtpSentResponse>.Failure(result.ErrorCode!, result.ErrorMessage!);

        return Result<OtpSentResponse>.Success(new OtpSentResponse
        {
            Email = req.Email,
            Purpose = "ForgotPassword",
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            ResendCountdown = 60
        });
    }

    /// <summary>
    /// Bước 2 quên MK - Verify OTP + đặt mật khẩu mới.
    /// </summary>
    public async Task<Result<AuthResponse>> ResetPasswordAsync(ResetPasswordRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 8)
            return Result<AuthResponse>.Failure("WEAK_PASSWORD", "Mật khẩu mới phải có ít nhất 8 ký tự");

        if (req.NewPassword != req.ConfirmPassword)
            return Result<AuthResponse>.Failure("PASSWORD_MISMATCH", "Mật khẩu xác nhận không khớp");

        var identifier = req.Email.ToLowerInvariant().Trim();
        var otp = await _otps.GetLatestActiveAsync(identifier, "ForgotPassword", ct);

        if (otp == null || otp.IsExpired())
            return Result<AuthResponse>.Failure("OTP_EXPIRED", "Mã OTP đã hết hạn, vui lòng yêu cầu mã mới");

        if (otp.Attempts >= otp.MaxAttempts)
            return Result<AuthResponse>.Failure("OTP_LOCKED", "Bạn đã nhập sai quá số lần cho phép");

        if (HashOtp(req.Code) != otp.CodeHash)
        {
            otp.Attempts += 1;
            _otps.Update(otp);
            await _otps.SaveChangesAsync(ct);
            return Result<AuthResponse>.Failure("OTP_WRONG", "Mã OTP không chính xác");
        }

        var user = await _users.GetByEmailAsync(identifier, ct);
        if (user == null)
            return Result<AuthResponse>.Failure("USER_NOT_FOUND", "Không tìm thấy tài khoản");

        // Cập nhật mật khẩu mới
        user.PasswordHash = _hasher.Hash(req.NewPassword);
        user.Status = "ACTIVE"; // đảm bảo active
        _users.Update(user);

        otp.UsedAt = DateTime.UtcNow;
        _otps.Update(otp);

        await _users.SaveChangesAsync(ct);
        await _otps.SaveChangesAsync(ct);

        var (token, expiresAt) = _jwt.GenerateAccessToken(user.Id, user.Email, user.Role);
        return Result<AuthResponse>.Success(new AuthResponse
        {
            UserId = user.Id,
            FullName = user.FullName,
            Email = user.Email,
            Phone = user.Phone,
            Role = user.Role,
            AccessToken = token,
            RefreshToken = _jwt.GenerateRefreshToken(),
            AccessTokenExpiresAt = expiresAt
        });
    }

    #endregion

    #region 4. Resend OTP

    public async Task<Result<OtpSentResponse>> ResendOtpAsync(ResendOtpRequest req, CancellationToken ct = default)
    {
        var purpose = string.IsNullOrEmpty(req.Purpose) ? "Register" : req.Purpose;
        var identifier = req.Email.ToLowerInvariant().Trim();

        // Kiểm tra email có user hay không (theo purpose)
        var user = await _users.GetByEmailAsync(identifier, ct);
        if (user == null)
            return Result<OtpSentResponse>.Failure("USER_NOT_FOUND", "Không tìm thấy tài khoản");

        // Resend bằng cách tạo OTP mới (mã cũ vẫn nhưng failed attempts không tăng)
        var plainCode = _otpGenerator.Generate();
        var result = await SaveAndSendOtpAsync(identifier, plainCode, purpose, ct);

        if (!result.IsSuccess)
            return Result<OtpSentResponse>.Failure(result.ErrorCode!, result.ErrorMessage!);

        return Result<OtpSentResponse>.Success(new OtpSentResponse
        {
            Email = req.Email,
            Purpose = purpose,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            ResendCountdown = 60
        });
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Lưu OTP (hash) vào DB và gửi email. Tách riêng để dùng chung cho mọi flow.
    /// </summary>
    private async Task<Result<bool>> SaveAndSendOtpAsync(string identifier, string plainCode, string purpose, CancellationToken ct, Guid? userId = null)
    {
        var otp = new OtpCode
        {
            Id = Guid.NewGuid(),
            UserId = userId, // Gán UserId để thỏa mãn FK constraint
            Recipient = identifier,
            CodeHash = HashOtp(plainCode),
            Purpose = purpose,
            Attempts = 0,
            MaxAttempts = 3,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            IpAddress = "0.0.0.0" // Sẽ update IP thực tế từ HttpContext nếu cần
        };
        await _otps.AddAsync(otp, ct);
        // Lưu OTP vào DB ngay để VerifyOtp có thể tìm thấy sau đó
        await _otps.SaveChangesAsync(ct);

        try
        {
            await _otpSender.SendAsync(identifier, plainCode, ct);
        }
        catch (Exception ex)
        {
            return Result<bool>.Failure("SEND_FAILED", $"Không thể gửi OTP tới email: {ex.Message}");
        }

        return Result<bool>.Success(true);
    }

    /// <summary>
    /// Hash OTP code bằng SHA256 (kết hợp với secret salt cố định).
    /// Mục đích: không lưu plaintext OTP trong DB.
    /// </summary>
    private static string HashOtp(string code)
    {
        // Salt cố định cho OTP - production nên lấy từ config
        const string salt = "SmartLocker.Otp.Salt.2025";
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(salt + code);
        return Convert.ToHexString(sha.ComputeHash(bytes));
    }
    /// <summary>
    /// Đăng nhập/Đăng ký bằng Google.
    /// Verify id_token → tìm/tạo user → cấp JWT.
    /// </summary>
    public async Task<Result<AuthResponse>> GoogleLoginAsync(GoogleLoginRequest req, CancellationToken ct = default)
    {
        try
        {
            // 1. Verify Google access_token bằng UserInfo API
            var payload = await _googleAuthService.VerifyIdTokenAsync(req.IdToken, ct);
            if (payload == null)
                return Result<AuthResponse>.Failure("INVALID_TOKEN", "Token Google không hợp lệ hoặc đã hết hạn. Vui lòng thử lại.");

            var email = payload.Email.ToLowerInvariant().Trim();
            var googleId = payload.Subject; // Google unique user ID

            // 2. Tìm user trong DB
            var user = await _users.GetByEmailAsync(email, ct);

            if (user == null)
            {
                // 3a. Tạo user mới (đăng ký qua Google)
                // Phone để trống vì Google không cung cấp SĐT — DB index có filter [Phone] <> '' nên không conflict
                user = new User
                {
                    Id = Guid.NewGuid(),
                    FullName = payload.Name ?? email,
                    Email = email,
                    Phone = "",
                    PasswordHash = "",
                    GoogleId = googleId,
                    AvatarUrl = payload.Picture,
                    Role = "TRAVELER",
                    Status = "ACTIVE",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                await _users.AddAsync(user, ct);
                await _users.SaveChangesAsync(ct);
            }
            else
            {
                // 3b. Link Google ID vào tài khoản cũ nếu chưa có
                if (string.IsNullOrEmpty(user.GoogleId))
                {
                    user.GoogleId = googleId;
                    user.AvatarUrl ??= payload.Picture;
                    _users.Update(user);
                    await _users.SaveChangesAsync(ct);
                }

                // Nếu tài khoản bị khóa
                if (user.Status == "SUSPENDED" || user.Status == "DELETED")
                    return Result<AuthResponse>.Failure("ACCOUNT_LOCKED", "Tài khoản đang bị tạm khóa. Liên hệ hỗ trợ.");

                // Kích hoạt nếu đang pending
                if (user.Status == "PENDING_VERIFICATION")
                {
                    user.Status = "ACTIVE";
                    _users.Update(user);
                    await _users.SaveChangesAsync(ct);
                }
            }

            // 4. Cấp JWT
            var (token, expiresAt) = _jwt.GenerateAccessToken(user.Id, user.Email, user.Role);

            return Result<AuthResponse>.Success(new AuthResponse
            {
                UserId = user.Id,
                FullName = user.FullName,
                Email = user.Email,
                Phone = user.Phone,
                Role = user.Role,
                AccessToken = token,
                RefreshToken = _jwt.GenerateRefreshToken(),
                AccessTokenExpiresAt = expiresAt
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ [GoogleLogin ERROR] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return Result<AuthResponse>.Failure("SERVER_ERROR", $"Lỗi server khi đăng nhập Google: {ex.Message}");
        }
    }

    #endregion
}
