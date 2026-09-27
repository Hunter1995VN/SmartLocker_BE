using Domain.Interfaces;

namespace Infrastructure.Services;

/// <summary>
/// Sinh mã OTP 6 chữ số ngẫu nhiên (an toàn về mặt crypto).
/// </summary>
public class OtpGenerator : IOtpGenerator
{
    public string Generate()
    {
        // 6 số từ 000000 đến 999999 - dùng RandomNumberGenerator để crypto-safe
        var bytes = new byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        var value = BitConverter.ToUInt32(bytes, 0) % 1_000_000u;
        return value.ToString("D6");
    }
}

/// <summary>
/// Hash và verify mật khẩu dùng thuật toán BCrypt (work factor = 11).
/// </summary>
public class BcryptPasswordHasher : IPasswordHasher
{
    private const int WorkFactor = 11;

    public string Hash(string password)
    {
        return BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);
    }

    public bool Verify(string password, string hash)
    {
        try
        {
            return BCrypt.Net.BCrypt.Verify(password, hash);
        }
        catch
        {
            return false;
        }
    }
}
