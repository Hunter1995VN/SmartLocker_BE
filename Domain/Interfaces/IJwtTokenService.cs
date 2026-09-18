namespace Domain.Interfaces;

/// <summary>
/// Sinh và validate JSON Web Token (JWT) dùng cho xác thực stateless.
/// </summary>
public interface IJwtTokenService
{
    /// <summary>
    /// Tạo access token cho user sau khi đăng nhập thành công.
    /// </summary>
    (string token, DateTime expiresAt) GenerateAccessToken(Guid userId, string email, string role);

    /// <summary>
    /// Tạo refresh token ngẫu nhiên (lưu DB) để cấp access token mới khi hết hạn.
    /// </summary>
    string GenerateRefreshToken();
}
