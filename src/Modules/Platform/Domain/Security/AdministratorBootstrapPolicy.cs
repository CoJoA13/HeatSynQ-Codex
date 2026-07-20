using System.Security.Cryptography;
using System.Text;

namespace HeatSynQ.Platform.Domain.Security;

public enum AdministratorBootstrapDecision
{
    Allowed,
    AlreadyProvisioned,
    NotConfigured,
    InvalidSecret
}

public static class AdministratorBootstrapPolicy
{
    public static AdministratorBootstrapDecision Evaluate(
        int existingUserCount,
        string? configuredSecret,
        string? suppliedSecret)
    {
        if (existingUserCount > 0)
        {
            return AdministratorBootstrapDecision.AlreadyProvisioned;
        }

        if (string.IsNullOrWhiteSpace(configuredSecret))
        {
            return AdministratorBootstrapDecision.NotConfigured;
        }

        var configuredHash = SHA256.HashData(Encoding.UTF8.GetBytes(configuredSecret));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedSecret ?? string.Empty));

        return CryptographicOperations.FixedTimeEquals(configuredHash, suppliedHash)
            ? AdministratorBootstrapDecision.Allowed
            : AdministratorBootstrapDecision.InvalidSecret;
    }
}
