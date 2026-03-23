using Pulumi.Random;

namespace PreTalxTixAzure.Helpers;

public record GeneratedSecrets(
    RandomPassword DbPassword,
    RandomPassword PretixSecretKey,
    RandomPassword PretalxSecretKey
);

/// <summary>
/// Generates cryptographically random secrets using Pulumi.Random.
/// Secrets are encrypted in Pulumi state and never stored in plain text.
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

        return new GeneratedSecrets(dbPassword, pretixSecretKey, pretalxSecretKey);
    }
}
