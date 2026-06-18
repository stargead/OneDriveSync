namespace OneDriveSync.Models;

/// <summary>
/// Resolved configuration for a single sync run. Values are merged from
/// <c>appsettings.json</c> and command-line arguments, with CLI taking precedence.
/// </summary>
public sealed class SyncOptions
{
    /// <summary>Azure AD application (client) ID of the user's app registration.</summary>
    public required string ClientId { get; init; }

    /// <summary>
    /// Azure AD tenant. Use <c>common</c> for personal + work/school accounts,
    /// <c>consumers</c> for personal-only, or a specific tenant GUID/domain.
    /// </summary>
    public string TenantId { get; init; } = "common";

    /// <summary>Absolute path to the local source folder to upload.</summary>
    public required string LocalPath { get; init; }

    /// <summary>
    /// Destination folder path in OneDrive, relative to the drive root,
    /// e.g. <c>/Documents/Backup</c>.
    /// </summary>
    public required string OneDrivePath { get; init; }

    /// <summary>When true, remote files that no longer exist locally are deleted.</summary>
    public bool Delete { get; init; }

    /// <summary>
    /// When true, a <see cref="System.IO.FileSystemWatcher"/> monitors the local folder
    /// and immediately deletes the matching OneDrive item when any local file is removed.
    /// Can be combined with <see cref="AutoSyncIntervalSeconds"/> for layered sync coverage.
    /// </summary>
    public bool Watch { get; init; }

    /// <summary>When true, no changes are made; actions are only reported.</summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Seconds between automatic sync runs. 0 (default) means run once and exit.
    /// Configurable via <c>Sync:AutoSyncIntervalSeconds</c> in appsettings.json or <c>--interval</c>.
    /// </summary>
    public int AutoSyncIntervalSeconds { get; init; }

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
                "Azure AD ClientId is not configured. Set AzureAd:ClientId in appsettings.json " +
                "(see README.md for how to register an app).");
        }

        if (string.IsNullOrWhiteSpace(LocalPath))
        {
            throw new InvalidOperationException(
                "LocalPath is not configured. Pass --local or set Sync:LocalPath in appsettings.json.");
        }

        if (!Directory.Exists(LocalPath))
        {
            throw new DirectoryNotFoundException($"Local source folder does not exist: '{LocalPath}'.");
        }

        if (string.IsNullOrWhiteSpace(OneDrivePath))
        {
            throw new InvalidOperationException(
                "OneDrivePath is not configured. Pass --remote or set Sync:OneDrivePath in appsettings.json.");
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
