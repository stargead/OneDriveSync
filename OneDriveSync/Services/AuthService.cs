using Azure.Core;
using Azure.Identity;
using Microsoft.Graph;
using OneDriveSync.Models;
using Spectre.Console;

namespace OneDriveSync.Services;

/// <summary>
/// Handles OAuth2 device-code authentication against Azure AD and produces an
/// authenticated <see cref="GraphServiceClient"/>. The refresh token is persisted
/// to disk so subsequent runs do not require re-authentication.
/// </summary>
public sealed class AuthService
{
    private const string TokenCacheName = "OneDriveSyncTokenCache";

    private readonly SyncOptions _options;
    private readonly string _authRecordPath;

    public AuthService(SyncOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        // Store the authentication record alongside the OS-managed persistent token cache.
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OneDriveSync");
        Directory.CreateDirectory(dir);
        _authRecordPath = Path.Combine(dir, "auth-record.json");
    }

    /// <summary>
    /// Builds an authenticated Graph client, reusing a cached token when available
    /// and otherwise driving the interactive device-code flow.
    /// </summary>
    public async Task<GraphServiceClient> CreateClientAsync(CancellationToken cancellationToken = default)
    {
        var credential = await BuildCredentialAsync(cancellationToken).ConfigureAwait(false);

        // Proactively acquire a token so any auth failure surfaces here, before sync starts.
        var tokenContext = new TokenRequestContext(SyncOptions.Scopes);
        await credential.GetTokenAsync(tokenContext, cancellationToken).ConfigureAwait(false);

        return new GraphServiceClient(credential, SyncOptions.Scopes);
    }

    private async Task<DeviceCodeCredential> BuildCredentialAsync(CancellationToken cancellationToken)
    {
        var cacheOptions = new TokenCachePersistenceOptions
        {
            Name = TokenCacheName,
            // Fall back to an unencrypted cache on headless Linux boxes without libsecret.
            UnsafeAllowUnencryptedStorage = true,
        };

        var deviceCodeOptions = new DeviceCodeCredentialOptions
        {
            ClientId = _options.ClientId,
            TenantId = _options.TenantId,
            TokenCachePersistenceOptions = cacheOptions,
            DeviceCodeCallback = (info, ct) =>
            {
                AnsiConsole.MarkupLine("[yellow]Authentication required.[/]");
                AnsiConsole.MarkupLine(info.Message.EscapeMarkup());
                return Task.CompletedTask;
            },
        };

        // Reuse a prior session silently when an authentication record exists.
        var existingRecord = await TryLoadAuthRecordAsync(cancellationToken).ConfigureAwait(false);
        if (existingRecord is not null)
        {
            deviceCodeOptions.AuthenticationRecord = existingRecord;
            return new DeviceCodeCredential(deviceCodeOptions);
        }

        var credential = new DeviceCodeCredential(deviceCodeOptions);

        // First run: authenticate and persist the record for future silent reuse.
        var record = await credential.AuthenticateAsync(
            new TokenRequestContext(SyncOptions.Scopes), cancellationToken).ConfigureAwait(false);
        await SaveAuthRecordAsync(record, cancellationToken).ConfigureAwait(false);

        return credential;
    }

    private async Task<AuthenticationRecord?> TryLoadAuthRecordAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_authRecordPath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_authRecordPath);
            return await AuthenticationRecord.DeserializeAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Corrupt or stale record: discard it and fall back to interactive auth.
            AnsiConsole.MarkupLine($"[grey]Could not load cached auth record ({ex.GetType().Name}); re-authenticating.[/]");
            return null;
        }
    }

    private async Task SaveAuthRecordAsync(AuthenticationRecord record, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.Create(_authRecordPath);
            await record.SerializeAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Non-fatal: the user simply re-authenticates next run.
            AnsiConsole.MarkupLine($"[grey]Warning: failed to persist auth record ({ex.Message.EscapeMarkup()}).[/]");
        }
    }
}
