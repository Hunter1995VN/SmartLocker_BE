using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Application.DTOs;
using Application.DTOs.Auth;
using Domain.Entities;
using Domain.Enums;
using Domain.Interfaces;

namespace Application.Services;

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

    /// <summary>
    /// Lưu tạm thông tin đăng ký khi chưa xác thực OTP để KHÔNG ghi tài khoản chưa kích hoạt vào bảng Users.
    /// Chỉ khi người dùng xác thực OTP thành công ở VerifyOtpAsync thì tài khoản mới được tạo trong Database.
    /// </summary>
    private static readonly ConcurrentDictionary<string, PendingRegistration> _pendingRegistrations =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed class PendingRegistration
    {
        public string FullName { get; init; } = string.Empty;
        public string Email { get; init; } = string.Empty;
        public string Phone { get; init; } = string.Empty;
        public string PasswordHash { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    }

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
    /// Bước 1 - Kiểm tra thông tin và gửi OTP về email để xác thực.
    /// Chưa lưu User vào bảng Users cho đến khi VerifyOtp thành công.
    /// </summary>
    public async Task<Result<OtpSentResponse>> RegisterInitiateAsync(RegisterRequest req, CancellationToken ct = default)
    {
        // 1. Validate cơ bản
        if (string.IsNullOrWhiteSpace(req.FullName) || req.FullName.Trim().Length < 2)
            return Result<OtpSentResponse>.Failure("INVALID_NAME", "Vui lòng nhập họ và tên hợp lệ");

        if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains("@") || !req.Email.Contains("."))
            return Result<OtpSentResponse>.Failure("INVALID_EMAIL", "Email không đúng định dạng");

        if (string.IsNullOrWhiteSpace(req.Phone) || req.Phone.Trim().Length < 10 || req.Phone.Trim().Length > 11)
            return Result<OtpSentResponse>.Failure("INVALID_PHONE", "Số điện thoại phải có 10-11 số");

        if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 8)
            return Result<OtpSentResponse>.Failure("WEAK_PASSWORD", "Mật khẩu phải có ít nhất 8 ký tự");

        if (req.Password != req.ConfirmPassword)
            return Result<OtpSentResponse>.Failure("PASSWORD_MISMATCH", "Mật khẩu xác nhận không khớp");

        if (!req.AgreeTerms)
            return Result<OtpSentResponse>.Failure("TERMS_NOT_AGREED", "Bạn cần đồng ý với điều khoản dịch vụ");

        var email = req.Email.Trim().ToLowerInvariant();
        var phone = req.Phone.Trim();

        // 2. Dọn dẹp các bản ghi User cũ bị kẹt ở trạng thái PENDING_VERIFICATION (chưa xác thực OTP) nếu có
        await _users.RemoveUnverifiedByEmailOrPhoneAsync(email, phone, ct);

        // 3. Check trùng email & phone với các tài khoản ĐÃ xác thực
        if (await _users.ExistsByEmailAsync(email, ct))
            return Result<OtpSentResponse>.Failure("EMAIL_EXISTS", "Email này đã được đăng ký");

        if (await _users.ExistsByPhoneAsync(phone, ct))
            return Result<OtpSentResponse>.Failure("PHONE_EXISTS", "Số điện thoại này đã được đăng ký");

        // 4. Sinh mã OTP, gửi email TRƯỚC rồi mới lưu mã OTP (không tạo User trong DB ở bước này)
        var plainCode = _otpGenerator.Generate();
        var result = await SaveAndSendOtpAsync(email, plainCode, "Register", ct, userId: null);

        if (!result.IsSuccess)
            return Result<OtpSentResponse>.Failure(result.ErrorCode!, result.ErrorMessage!);

        // 5. Lưu tạm thông tin đăng ký vào bộ nhớ chờ xác thực OTP
        _pendingRegistrations[email] = new PendingRegistration
        {
            FullName = req.FullName.Trim(),
            Email = email,
            Phone = phone,
            PasswordHash = _hasher.Hash(req.Password),
            CreatedAt = DateTime.UtcNow
        };

        return Result<OtpSentResponse>.Success(new OtpSentResponse
        {
            Email = email,
            Purpose = "Register",
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            ResendCountdown = 60
        });
    }

    /// <summary>
    /// Bước 2 - Verify OTP. Nếu đúng → tạo/kích hoạt tài khoản trong DB + cấp JWT.
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
            var inputHash = HashOtp(req.Code.Trim());
            if (inputHash != otp.CodeHash)
            {
                otp.Attempts += 1;
                _otps.Update(otp);
                await _otps.SaveChangesAsync(ct);
                var remaining = otp.MaxAttempts - otp.Attempts;
                return Result<AuthResponse>.Failure("OTP_WRONG", $"Mã OTP không chính xác. Bạn còn {remaining} lần thử.");
            }

            // 4. Tìm hoặc tạo user sau khi đã xác thực OTP thành công
            var user = await _users.GetByEmailAsync(identifier, ct);
            if (user == null)
            {
                if (!_pendingRegistrations.TryGetValue(identifier, out var pending))
                    return Result<AuthResponse>.Failure("USER_NOT_FOUND", "Phiên đăng ký đã hết hạn hoặc không tìm thấy thông tin đăng ký. Vui lòng đăng ký lại.");

                await _users.RemoveUnverifiedByEmailOrPhoneAsync(pending.Email, pending.Phone, ct);

                if (await _users.ExistsByEmailAsync(pending.Email, ct))
                    return Result<AuthResponse>.Failure("EMAIL_EXISTS", "Email này đã được đăng ký");

                if (await _users.ExistsByPhoneAsync(pending.Phone, ct))
                    return Result<AuthResponse>.Failure("PHONE_EXISTS", "Số điện thoại này đã được đăng ký");

                user = new User
                {
                    Id = Guid.NewGuid(),
                    FullName = pending.FullName,
                    Email = pending.Email,
                    Phone = pending.Phone,
                    PasswordHash = pending.PasswordHash,
                    Role = UserRole.TRAVELER,
                    Status = UserStatus.ACTIVE,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                await _users.AddAsync(user, ct);
                otp.UserId = user.Id;
                _pendingRegistrations.TryRemove(identifier, out _);
            }
            else
            {
                // Kích hoạt user nếu đang Pending
                if (user.Status == UserStatus.PENDING_VERIFICATION)
                {
                    user.Status = UserStatus.ACTIVE;
                    _users.Update(user);
                }
                _pendingRegistrations.TryRemove(identifier, out _);
            }

            // 5. Đánh dấu OTP đã dùng
            otp.UsedAt = DateTime.UtcNow;
            _otps.Update(otp);

            // 6. Lưu tất cả một lần (User + OTP cùng DbContext)
            await _users.SaveChangesAsync(ct);

            // 7. Cấp JWT
            var (token, expiresAt) = _jwt.GenerateAccessToken(user.Id, user.Email, user.Role.ToString());

            return Result<AuthResponse>.Success(new AuthResponse
            {
                UserId = user.Id,
                FullName = user.FullName,
                Email = user.Email,
                Phone = user.Phone,
                Role = user.Role.ToString(),
                AccessToken = token,
                RefreshToken = _jwt.GenerateRefreshToken(),
                AccessTokenExpiresAt = expiresAt
            });
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            Console.WriteLine($"❌ [VerifyOtp ERROR] {ex.GetType().Name}: {detail}\n{ex.StackTrace}");
            return Result<AuthResponse>.Failure("SERVER_ERROR", $"Lỗi server: {detail}");
        }
    }

    #endregion

    #region 2. ĐĂNG NHẬP

    /// <summary>
    /// Đăng nhập bằng email (hoặc SĐT) + mật khẩu.
    /// </summary>
    public async Task<Result<AuthResponse>> LoginAsync(LoginRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
            return Result<AuthResponse>.Failure("INVALID_CREDENTIALS", "Email và mật khẩu là bắt buộc");

        var identifier = req.Email.ToLowerInvariant().Trim();
        var user = await _users.GetByEmailAsync(identifier, ct)
                   ?? await _users.GetByPhoneAsync(req.Email.Trim(), ct);

        if (user == null || string.IsNullOrEmpty(user.PasswordHash) || !_hasher.Verify(req.Password, user.PasswordHash))
            return Result<AuthResponse>.Failure("INVALID_CREDENTIALS", "Email hoặc mật khẩu không chính xác");

        if (user.Status == UserStatus.PENDING_VERIFICATION)
            return Result<AuthResponse>.Failure("ACCOUNT_NOT_VERIFIED", "Tài khoản chưa được xác thực. Vui lòng kiểm tra email.");

        if (user.Status == UserStatus.SUSPENDED)
            return Result<AuthResponse>.Failure("ACCOUNT_LOCKED", "Tài khoản đang bị tạm khóa. Liên hệ hỗ trợ.");

        if (user.Status == UserStatus.DELETED)
            return Result<AuthResponse>.Failure("ACCOUNT_DISABLED", "Tài khoản đã bị vô hiệu hóa");

        var (token, expiresAt) = _jwt.GenerateAccessToken(user.Id, user.Email, user.Role.ToString());

        return Result<AuthResponse>.Success(new AuthResponse
        {
            UserId = user.Id,
            FullName = user.FullName,
            Email = user.Email,
            Phone = user.Phone.StartsWith("00") ? "" : user.Phone,
            Role = user.Role.ToString(),
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
        var result = await SaveAndSendOtpAsync(identifier, plainCode, "ForgotPassword", ct, userId: user.Id);

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

        if (HashOtp(req.Code.Trim()) != otp.CodeHash)
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
        user.Status = UserStatus.ACTIVE; // đảm bảo active
        _users.Update(user);

        otp.UsedAt = DateTime.UtcNow;
        _otps.Update(otp);

        await _users.SaveChangesAsync(ct);
        await _otps.SaveChangesAsync(ct);

        var (token, expiresAt) = _jwt.GenerateAccessToken(user.Id, user.Email, user.Role.ToString());
        return Result<AuthResponse>.Success(new AuthResponse
        {
            UserId = user.Id,
            FullName = user.FullName,
            Email = user.Email,
            Phone = user.Phone.StartsWith("00") ? "" : user.Phone,
            Role = user.Role.ToString(),
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

        // Kiểm tra email có user hoặc phiên đăng ký tạm hay không
        var user = await _users.GetByEmailAsync(identifier, ct);
        var hasPendingRegistration = purpose.Equals("Register", StringComparison.OrdinalIgnoreCase)
            && _pendingRegistrations.ContainsKey(identifier);

        if (user == null && !hasPendingRegistration)
            return Result<OtpSentResponse>.Failure("USER_NOT_FOUND", "Không tìm thấy thông tin đăng ký. Vui lòng đăng ký lại.");

        // Resend bằng cách tạo OTP mới
        var plainCode = _otpGenerator.Generate();
        var result = await SaveAndSendOtpAsync(identifier, plainCode, purpose, ct, userId: user?.Id);

        if (!result.IsSuccess)
            return Result<OtpSentResponse>.Failure(result.ErrorCode!, result.ErrorMessage!);

        return Result<OtpSentResponse>.Success(new OtpSentResponse
        {
            Email = identifier,
            Purpose = purpose,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            ResendCountdown = 60
        });
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Gửi OTP tới email TRƯỚC, nếu gửi thành công mới lưu OTP (hash) vào DB.
    /// Đảm bảo nếu gửi mail lỗi thì không lưu rác vào DB.
    /// </summary>
    private async Task<Result<bool>> SaveAndSendOtpAsync(string identifier, string plainCode, string purpose, CancellationToken ct, Guid? userId = null)
    {
        try
        {
            await _otpSender.SendAsync(identifier, plainCode, ct);
        }
        catch (Exception ex)
        {
            return Result<bool>.Failure("SEND_FAILED", $"Không thể gửi OTP tới email: {ex.Message}");
        }

        var otp = new OtpCode
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Recipient = identifier,
            CodeHash = HashOtp(plainCode),
            Purpose = purpose,
            Attempts = 0,
            MaxAttempts = 3,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            IpAddress = "0.0.0.0"
        };
        await _otps.AddAsync(otp, ct);
        await _otps.SaveChangesAsync(ct);

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
    /// Sinh SĐT đại diện 10 chữ số duy nhất (bắt đầu bằng 00) cho tài khoản đăng ký qua Google OAuth
    /// để không bị lỗi trùng UNIQUE KEY constraint uq_Users_Phone trong SQL Server khi nhiều user Google cùng có Phone rỗng.
    /// </summary>
    private async Task<string> GenerateUniqueGooglePhoneAsync(string googleId, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(googleId));
        var numeric = BitConverter.ToUInt32(hashBytes, 0) % 100_000_000u;
        var candidate = $"00{numeric:D8}";

        while (await _users.AnyPhoneInDbAsync(candidate, ct))
        {
            var randBytes = new byte[4];
            RandomNumberGenerator.Fill(randBytes);
            var randNum = BitConverter.ToUInt32(randBytes, 0) % 100_000_000u;
            candidate = $"00{randNum:D8}";
        }

        return candidate;
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

            // 2. Tìm user trong DB theo Email hoặc GoogleId
            var user = await _users.GetByEmailAsync(email, ct)
                       ?? await _users.GetByGoogleIdAsync(googleId, ct);

            if (user == null)
            {
                // 3a. Tạo user mới (đăng ký qua Google)
                // Sinh SĐT đại diện 10 số duy nhất (00xxxxxxxx) vì bảng Users trên SQL Server có UNIQUE constraint trên cột Phone
                var placeholderPhone = await GenerateUniqueGooglePhoneAsync(googleId, ct);

                user = new User
                {
                    Id = Guid.NewGuid(),
                    FullName = string.IsNullOrWhiteSpace(payload.Name) ? email : payload.Name.Trim(),
                    Email = email,
                    Phone = placeholderPhone,
                    PasswordHash = _hasher.Hash(Guid.NewGuid().ToString("N")),
                    GoogleId = googleId,
                    AvatarUrl = payload.Picture,
                    Role = UserRole.TRAVELER,
                    Status = UserStatus.ACTIVE,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                await _users.AddAsync(user, ct);
                await _users.SaveChangesAsync(ct);
            }
            else
            {
                // Nếu tài khoản bị khóa
                if (user.Status == UserStatus.SUSPENDED || user.Status == UserStatus.DELETED)
                    return Result<AuthResponse>.Failure("ACCOUNT_LOCKED", "Tài khoản đang bị tạm khóa. Liên hệ hỗ trợ.");

                // 3b. Link Google ID & kích hoạt nếu đang pending trong 1 lần SaveChanges duy nhất
                var needUpdate = false;
                if (string.IsNullOrEmpty(user.GoogleId))
                {
                    user.GoogleId = googleId;
                    user.AvatarUrl ??= payload.Picture;
                    needUpdate = true;
                }

                if (user.Status == UserStatus.PENDING_VERIFICATION)
                {
                    user.Status = UserStatus.ACTIVE;
                    needUpdate = true;
                }

                if (needUpdate)
                {
                    _users.Update(user);
                    await _users.SaveChangesAsync(ct);
                }
            }

            // 4. Cấp JWT
            var (token, expiresAt) = _jwt.GenerateAccessToken(user.Id, user.Email, user.Role.ToString());

            return Result<AuthResponse>.Success(new AuthResponse
            {
                UserId = user.Id,
                FullName = user.FullName,
                Email = user.Email,
                Phone = user.Phone.StartsWith("00") ? "" : user.Phone,
                Role = user.Role.ToString(),
                AccessToken = token,
                RefreshToken = _jwt.GenerateRefreshToken(),
                AccessTokenExpiresAt = expiresAt
            });
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            Console.WriteLine($"❌ [GoogleLogin ERROR] {ex.GetType().Name}: {detail}\n{ex.StackTrace}");
            return Result<AuthResponse>.Failure("SERVER_ERROR", $"Lỗi server khi đăng nhập Google: {detail}");
        }
    }

    #endregion
}
