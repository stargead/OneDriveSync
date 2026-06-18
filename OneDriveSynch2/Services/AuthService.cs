using Azure.Core;
using Azure.Identity;
using Microsoft.Graph;
using OneDriveSynch2.Models;
using Spectre.Console;

namespace OneDriveSynch2.Services;

/// <summary>
/// Handles OAuth2 device-code authentication against Azure AD and produces an
/// authenticated <see cref="GraphServiceClient"/>. The refresh token is persisted
/// to disk so subsequent runs do not require re-authentication. The token cache
/// and auth-record location are shared with v1 so existing sessions are reused.
/// </summary>
public sealed class AuthService
{
    private const string TokenCacheName = "OneDriveSyncTokenCache";

    private readonly SyncOptions _options;
    private readonly string _authRecordPath;

    public AuthService(SyncOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OneDriveSync");
        Directory.CreateDirectory(dir);
        _authRecordPath = Path.Combine(dir, "auth-record.json");
    }

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

        var existingRecord = await TryLoadAuthRecordAsync(cancellationToken).ConfigureAwait(false);
        if (existingRecord is not null)
        {
            deviceCodeOptions.AuthenticationRecord = existingRecord;
            return new DeviceCodeCredential(deviceCodeOptions);
        }

        var credential = new DeviceCodeCredential(deviceCodeOptions);

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
            AnsiConsole.MarkupLine($"[grey]Warning: failed to persist auth record ({ex.Message.EscapeMarkup()}).[/]");
        }
    }
}
