using API.Services;
using Domain.Entities;
using Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace Hung.UseCases.Tests;

public class PinProtectionTests
{
    [Fact]
    public void ProtectedPinSurvivesProviderRestartAndPreservesOfflineTicket()
    {
        var folder = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "smartlocker-pin-test-" + Guid.NewGuid().ToString("N")));
        folder.Create();
        try
        {
            IDataProtectionProvider Provider() => DataProtectionProvider.Create(folder, builder => {
                builder.SetApplicationName("SmartLocker.AccessTickets");
                if (OperatingSystem.IsWindows()) builder.ProtectKeysWithDpapi();
            });
            var booking = Guid.NewGuid();
            var original = new AccessPinProtector(Provider());
            var protectedPin = original.Protect(booking, "01234567");
            var restarted = new AccessPinProtector(Provider());
            var credential = new AccessCredential { BookingId = booking, ProtectedAccessCode = protectedPin,
                AccessCodeHash = OnlineTicket.HashPin("01234567"), OfflinePayload = "independent-offline-ticket" };
            Assert.NotEqual("01234567", protectedPin);
            Assert.Equal("01234567", OnlineTicket.GetDisplayPin(credential, restarted));
            Assert.Equal("independent-offline-ticket", credential.OfflinePayload);
            Assert.Null(restarted.Unprotect(Guid.NewGuid(), protectedPin));
            Assert.Null(restarted.Unprotect(booking, protectedPin[..^8] + "XXXXXXXX"));
            credential.AccessCodeHash = OnlineTicket.HashPin("76543210");
            Assert.Null(OnlineTicket.GetDisplayPin(credential, restarted));
        }
        finally { folder.Delete(true); }
    }

    [Fact]
    public void OfflinePayloadIsNeverDisplayedAsPinAndCannotReplaceUnreadableCiphertext()
    {
        var credential = new AccessCredential { OfflinePayload = "offline-bearer-token", AccessCodeHash = "irrelevant" };
        Assert.Null(OnlineTicket.GetDisplayPin(credential));
        credential.OfflinePayload = "01234567";
        credential.AccessCodeHash = OnlineTicket.HashPin("01234567");
        credential.ProtectedAccessCode = "unreadable-protected-pin";
        Assert.Null(OnlineTicket.GetDisplayPin(credential));
    }
}
