using HeatSynQ.Platform.Domain.Security;

namespace HeatSynQ.Platform.Domain.Tests.Security;

public sealed class UserAccountTests
{
    [Fact]
    public void Disabling_an_account_revokes_existing_sessions()
    {
        var account = UserAccount.Create("operator1", "Operator One");
        var originalStamp = account.SessionVersion;

        account.Disable("Employment ended", DateTimeOffset.Parse("2026-07-19T15:00:00-05:00"));

        Assert.False(account.IsEnabled);
        Assert.NotEqual(originalStamp, account.SessionVersion);
        Assert.Equal("Employment ended", account.DisabledReason);
    }

    [Fact]
    public void Account_identity_cannot_be_blank()
    {
        Assert.Throws<ArgumentException>(() => UserAccount.Create(" ", "Operator One"));
        Assert.Throws<ArgumentException>(() => UserAccount.Create("operator1", " "));
    }

    [Fact]
    public void Revoking_sessions_does_not_disable_the_account()
    {
        var account = UserAccount.Create("quality1", "Quality One");
        var originalStamp = account.SessionVersion;

        account.RevokeSessions();

        Assert.True(account.IsEnabled);
        Assert.NotEqual(originalStamp, account.SessionVersion);
    }
}
