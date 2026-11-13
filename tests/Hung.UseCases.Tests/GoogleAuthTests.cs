using System.Net;
using Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hung.UseCases.Tests;

public class GoogleAuthTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData(",\"email_verified\":null", false)]
    [InlineData(",\"email_verified\":false", false)]
    [InlineData(",\"email_verified\":true", true)]
    public async Task RequiresGoogleToConfirmEmailVerification(string verificationField, bool expectedSuccess)
    {
        var body = "{\"sub\":\"google-user\",\"email\":\"user@example.test\"" + verificationField + "}";
        using var client = new HttpClient(new UserInfoHandler(body));
        var service = new GoogleAuthService(client, new ConfigurationBuilder().Build(),
            NullLogger<GoogleAuthService>.Instance);

        var payload = await service.VerifyIdTokenAsync("test-token");

        Assert.Equal(expectedSuccess, payload is not null);
    }

    private sealed class UserInfoHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }
    }
}
