using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SmartLocker.Domain.Interfaces;

namespace SmartLocker.Infrastructure.Services;

public class GoogleAuthService : IGoogleAuthService
{
    private readonly HttpClient _httpClient;
    private readonly string _clientId;
    private readonly ILogger<GoogleAuthService> _logger;

    public GoogleAuthService(HttpClient httpClient, IConfiguration config, ILogger<GoogleAuthService> logger)
    {
        _httpClient = httpClient;
        _clientId = config["GoogleAuth:ClientId"] ?? "";
        _logger = logger;
    }

    public async Task<GoogleUserPayload?> VerifyIdTokenAsync(
        string accessToken, CancellationToken ct = default)
    {
        try
        {
            // Frontend dùng `useGoogleLogin` trả về access_token,
            // ta gọi API UserInfo thay vì decode JWT.
            var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/oauth2/v3/userinfo");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var response = await _httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("⚠️ Google UserInfo API trả lỗi {Status}: {Body}", (int)response.StatusCode, body);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            _logger.LogInformation("✅ Google UserInfo response: {Content}", content);

            var payload = JsonSerializer.Deserialize<GoogleUserInfoResponse>(content);

            if (payload == null || string.IsNullOrEmpty(payload.Email))
            {
                _logger.LogWarning("⚠️ Google payload null hoặc thiếu email");
                return null;
            }

            return new GoogleUserPayload
            {
                Subject = payload.Sub,
                Email = payload.Email,
                Name = payload.Name,
                Picture = payload.Picture
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Lỗi khi gọi Google UserInfo API");
            return null;
        }
    }

    private class GoogleUserInfoResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("sub")]
        public string Sub { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string? Name { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("picture")]
        public string? Picture { get; set; }
    }
}
