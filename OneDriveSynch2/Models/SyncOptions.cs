namespace OneDriveSynch2.Models;

/// <summary>
/// Resolved configuration for a sync session. Values are merged from
/// <c>appsettings.json</c>, environment variables and command-line arguments,
/// with CLI taking precedence.
/// </summary>
public sealed class SyncOptions
{
    /// <summary>Azure AD application (client) ID of the user's app registration.</summary>
    public required string ClientId { get; init; }

    /// <summary>
    /// Azure AD tenant. Use <c>consumers</c> for personal accounts,
    /// <c>common</c> for personal + work/school, or a specific tenant GUID/domain.
    /// </summary>
    public string TenantId { get; init; } = "consumers";

    /// <summary>Absolute path to the local folder to keep in sync.</summary>
    public required string LocalPath { get; init; }

    /// <summary>Destination folder path in OneDrive, relative to the drive root.</summary>
    public required string OneDrivePath { get; init; }

    /// <summary>Seconds between OneDrive polling cycles.</summary>
    public int OneDrivePollIntervalSeconds { get; init; } = 60;

    /// <summary>Microsoft Graph scopes required for file sync.</summary>
    public static readonly string[] Scopes = { "Files.ReadWrite", "User.Read", "offline_access" };

    /// <summary>
    /// Validates the resolved options, throwing a descriptive exception when
    /// required values are missing or invalid.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ClientId) ||
            ClientId == "00000000-0000-0000-0000-000000000000")
        {
            throw new InvalidOperationException(
                "Azure AD ClientId is not configured. Set AzureAd:ClientId in appsettings.json.");
        }

        if (string.IsNullOrWhiteSpace(LocalPath))
        {
            throw new InvalidOperationException(
                "LocalPath is not configured. Pass --local or set Sync:LocalPath in appsettings.json.");
        }

        if (!Directory.Exists(LocalPath))
        {
            throw new DirectoryNotFoundException($"Local folder does not exist: '{LocalPath}'.");
        }

        if (string.IsNullOrWhiteSpace(OneDrivePath))
        {
            throw new InvalidOperationException(
                "OneDrivePath is not configured. Pass --remote or set Sync:OneDrivePath in appsettings.json.");
        }

        if (OneDrivePollIntervalSeconds <= 0)
        {
            throw new InvalidOperationException(
                "OneDrivePollIntervalSeconds must be greater than zero.");
        }
    }

    /// <summary>
    /// Normalizes the OneDrive path to a leading-slash, no-trailing-slash form,
    /// e.g. <c>Documents/Backup/</c> -> <c>/Documents/Backup</c>.
    /// </summary>
    public string NormalizedOneDrivePath()
    {
        var trimmed = OneDrivePath.Replace('\\', '/').Trim();
        trimmed = "/" + trimmed.Trim('/');
        return trimmed == "/" ? "/" : trimmed.TrimEnd('/');
    }
}
