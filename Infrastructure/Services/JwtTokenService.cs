using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SmartLocker.Domain.Interfaces;

namespace SmartLocker.Infrastructure.Services;

/// <summary>
/// Cấu hình JWT - load từ appsettings.json: JwtSettings.{SecretKey, Issuer, Audience, AccessTokenMinutes}.
/// </summary>
public class JwtSettings
{
    public string SecretKey { get; set; } = string.Empty;
    public string Issuer { get; set; } = "SmartLocker";
    public string Audience { get; set; } = "SmartLocker.Clients";
    public int AccessTokenMinutes { get; set; } = 60 * 24; // mặc định 1 ngày
}

/// <summary>
/// Sinh Access Token (JWT) và Refresh Token (random string).
/// </summary>
public class JwtTokenService : IJwtTokenService
{
    private readonly JwtSettings _settings;

    public JwtTokenService(IOptions<JwtSettings> settings)
    {
        _settings = settings.Value;
    }

    public (string token, DateTime expiresAt) GenerateAccessToken(Guid userId, string email, string role)
    {
        var now = DateTime.UtcNow;
        var expiresAt = now.AddMinutes(_settings.AccessTokenMinutes);

        // 1. Tạo danh sách claims (thông tin đính kèm trong token)
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(JwtRegisteredClaimNames.Email, email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, role),
            new("uid", userId.ToString())
        };

        // 2. Tạo signing key từ SecretKey (fallback nếu chưa cấu hình)
        var secretKey = string.IsNullOrEmpty(_settings.SecretKey)
            ? "SmartLocker.Super.Secret.Key.2025.MinLength32Chars"
            : _settings.SecretKey;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        // 3. Tạo token
        var token = new JwtSecurityToken(
            issuer: _settings.Issuer,
            audience: _settings.Audience,
            claims: claims,
            notBefore: now,
            expires: expiresAt,
            signingCredentials: credentials
        );

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    public string GenerateRefreshToken()
    {
        var bytes = new byte[64];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }
}
