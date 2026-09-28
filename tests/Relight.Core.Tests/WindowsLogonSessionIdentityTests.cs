using Relight.Windows;

namespace Relight.Core.Tests;

public sealed class WindowsLogonSessionIdentityTests
{
    [Fact]
    [Trait("Category", "WindowsDesktop")]
    public void Current_token_has_a_stable_logon_identity_with_an_opaque_storage_key()
    {
        WindowsLogonSessionIdentity first = WindowsLogonSessionIdentity.Current();
        WindowsLogonSessionIdentity again = WindowsLogonSessionIdentity.Current();

        Assert.Equal(first, again);
        Assert.StartsWith("S-1-5-", first.UserSid);
        Assert.True(first.SessionId >= 0);
        Assert.True(first.AuthenticationId > 0);
        Assert.Equal(32, first.StorageKey.Length);
        Assert.Equal(first.StorageKey, again.StorageKey);
        Assert.DoesNotContain(first.UserSid, first.StorageKey);
        Assert.NotEqual(first.StorageKey,
            (first with { SessionId = first.SessionId + 1 }).StorageKey);
        Assert.NotEqual(first.StorageKey,
            (first with { AuthenticationId = first.AuthenticationId + 1 }).StorageKey);
    }
}
