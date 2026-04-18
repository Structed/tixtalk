using Pulumi.Random;

namespace TixTalk.Infra.Helpers;

public record GeneratedSecrets(
    RandomPassword DbPassword,
    RandomPassword PretixSecretKey,
    RandomPassword PretalxSecretKey,
    RandomPassword AdminPassword
);

/// <summary>
/// Generates cryptographically random secrets stored encrypted in Pulumi state.
/// </summary>
public static class SecretGenerator
{
    public static GeneratedSecrets Create(string prefix)
    {
        var dbPassword = new RandomPassword($"{prefix}-db-pwd", new RandomPasswordArgs
        {
            Length = 32,
            Special = false,
        });

        var pretixSecretKey = new RandomPassword($"{prefix}-pretix-secret", new RandomPasswordArgs
        {
            Length = 50,
            Special = false,
        });

        var pretalxSecretKey = new RandomPassword($"{prefix}-pretalx-secret", new RandomPasswordArgs
        {
            Length = 50,
            Special = false,
        });

        var adminPassword = new RandomPassword($"{prefix}-admin-pwd", new RandomPasswordArgs
        {
            Length = 24,
            Special = false,
        });

        return new GeneratedSecrets(dbPassword, pretixSecretKey, pretalxSecretKey, adminPassword);
    }
}
