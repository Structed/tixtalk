namespace PreTalxTixAzure.Helpers;

/// <summary>
/// Azure resource naming helpers.
/// </summary>
public static class NamingConventions
{
    /// <summary>
    /// Storage account names: 3-24 chars, lowercase alphanumeric only.
    /// </summary>
    public static string StorageAccountName(string prefix)
    {
        var sanitized = prefix.ToLowerInvariant().Replace("-", "").Replace("_", "");
        if (sanitized.Length > 18) sanitized = sanitized[..18];
        return $"{sanitized}store";
    }
}
