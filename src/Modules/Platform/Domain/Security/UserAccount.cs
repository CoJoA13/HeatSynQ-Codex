namespace HeatSynQ.Platform.Domain.Security;

public sealed class UserAccount
{
    private UserAccount(string username, string displayName)
    {
        Id = Guid.NewGuid();
        Username = username;
        DisplayName = displayName;
        SessionVersion = Guid.NewGuid();
        IsEnabled = true;
    }

    public Guid Id { get; }
    public string Username { get; }
    public string DisplayName { get; private set; }
    public bool IsEnabled { get; private set; }
    public Guid SessionVersion { get; private set; }
    public string? DisabledReason { get; private set; }
    public DateTimeOffset? DisabledAt { get; private set; }

    public static UserAccount Create(string username, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        return new UserAccount(username.Trim(), displayName.Trim());
    }

    public void Disable(string reason, DateTimeOffset disabledAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        IsEnabled = false;
        DisabledReason = reason.Trim();
        DisabledAt = disabledAt;
        RevokeSessions();
    }

    public void RevokeSessions() => SessionVersion = Guid.NewGuid();
}
