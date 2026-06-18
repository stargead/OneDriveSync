using Microsoft.Extensions.Configuration;
using OneDriveSync.Models;
using OneDriveSync.Services;
using Spectre.Console;

namespace OneDriveSync;

/// <summary>
/// Entry point. Resolves configuration, authenticates against Microsoft Graph,
/// and runs a one-way folder sync to OneDrive.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // Graceful Ctrl+C handling: cancel the in-flight sync rather than hard-killing it.
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            AnsiConsole.MarkupLine("[yellow]Cancellation requested — finishing current operation...[/]");
        };

        AnsiConsole.Write(new FigletText("OneDriveSync").Color(Color.Blue));

        if (args.Any(a => a is "-h" or "--help"))
        {
            PrintUsage();
            return 0;
        }

        SyncOptions options;
        try
        {
            options = BuildOptions(args);
            options.Validate();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Configuration error:[/] {ex.Message.EscapeMarkup()}");
            PrintUsage();
            return 1;
        }

        try
        {
            var auth = new AuthService(options);
            var graph = await auth.CreateClientAsync(cts.Token).ConfigureAwait(false);

            // Start the real-time file watcher before the first sync so no local
            // deletion that occurs during sync is missed.
            await using var watcher = options.Watch ? new FileWatcherService(graph, options) : null;
            var watcherTask = watcher is not null ? watcher.RunAsync(cts.Token) : null;

            var run = 0;
            while (true)
            {
                if (run > 0)
                {
                    AnsiConsole.MarkupLine($"[grey]--- Auto-sync run #{run + 1} ---[/]");
                }

                var sync = new SyncService(graph, options);
                await sync.RunAsync(cts.Token).ConfigureAwait(false);
                run++;

                if (options.AutoSyncIntervalSeconds <= 0)
                {
                    if (watcherTask is not null)
                    {
                        AnsiConsole.MarkupLine(
                            "[green]Sync done.[/] [grey]File watcher active — Ctrl+C to stop.[/]");
                        await watcherTask.ConfigureAwait(false);
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[green]Done.[/]");
                    }
                    return 0;
                }

                var watcherHint = options.Watch ? " (file watcher active)" : string.Empty;
                AnsiConsole.MarkupLine(
                    $"[green]Sync complete.[/] [grey]Next sync in {options.AutoSyncIntervalSeconds}s{watcherHint} — Ctrl+C to stop.[/]");
                await Task.Delay(
                    TimeSpan.FromSeconds(options.AutoSyncIntervalSeconds), cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Sync cancelled.[/]");
            return 130; // Conventional exit code for SIGINT.
        }
        catch (Azure.Identity.AuthenticationFailedException ex)
        {
            AnsiConsole.MarkupLine($"[red]Authentication failed:[/] {ex.Message.EscapeMarkup()}");
            return 2;
        }
        catch (UnauthorizedAccessException ex)
        {
            AnsiConsole.MarkupLine($"[red]Permission denied:[/] {ex.Message.EscapeMarkup()}");
            return 3;
        }
        catch (DirectoryNotFoundException ex)
        {
            AnsiConsole.MarkupLine($"[red]Folder not found:[/] {ex.Message.EscapeMarkup()}");
            return 4;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Unexpected error:[/] {ex.Message.EscapeMarkup()}");
            AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
            return 5;
        }
    }

    /// <summary>
    /// Merges <c>appsettings.json</c> with CLI flags. CLI flags win over file config.
    /// </summary>
    private static SyncOptions BuildOptions(string[] args)
    {
        var basePath = AppContext.BaseDirectory;

        var config = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "ONEDRIVESYNC_")
            .Build();

        var cli = ParseArgs(args);

        var clientId = cli.GetValueOrDefault("clientid")
                       ?? config["AzureAd:ClientId"]
                       ?? string.Empty;

        var tenantId = cli.GetValueOrDefault("tenant")
                       ?? config["AzureAd:TenantId"]
                       ?? "common";

        var localPath = cli.GetValueOrDefault("local")
                        ?? config["Sync:LocalPath"]
                        ?? string.Empty;

        var remotePath = cli.GetValueOrDefault("remote")
                         ?? config["Sync:OneDrivePath"]
                         ?? string.Empty;

        var intervalRaw = cli.GetValueOrDefault("interval") ?? config["Sync:AutoSyncIntervalSeconds"];
        int.TryParse(intervalRaw, out var autoSyncInterval);

        return new SyncOptions
        {
            ClientId = clientId,
            TenantId = string.IsNullOrWhiteSpace(tenantId) ? "common" : tenantId,
            LocalPath = string.IsNullOrWhiteSpace(localPath)
                ? localPath
                : Path.GetFullPath(localPath),
            OneDrivePath = remotePath,
            Delete = cli.ContainsKey("delete"),
            DryRun = cli.ContainsKey("dry-run"),
            Watch = cli.ContainsKey("watch"),
            AutoSyncIntervalSeconds = autoSyncInterval < 0 ? 0 : autoSyncInterval,
        };
    }

    /// <summary>
    /// Minimal long-option parser supporting <c>--key value</c> and boolean <c>--flag</c> forms.
    /// </summary>
    private static Dictionary<string, string?> ParseArgs(string[] args)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var booleanFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "delete", "dry-run", "watch",
        };

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = arg[2..];

            // Support --key=value as well as --key value.
            var eq = key.IndexOf('=');
            if (eq >= 0)
            {
                result[key[..eq]] = key[(eq + 1)..];
                continue;
            }

            if (booleanFlags.Contains(key))
            {
                result[key] = "true";
                continue;
            }

            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result[key] = args[++i];
            }
            else
            {
                result[key] = "true";
            }
        }

        return result;
    }

    private static void PrintUsage()
    {
        // Note: usage text below is written with Console.WriteLine (not Spectre markup)
        // because it contains literal '[' / ']' / '<' / '>' characters that Spectre
        // would otherwise try to interpret as style tags.
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Usage:[/]");
        Console.WriteLine(
            "  onedrivesync --local /path/to/folder --remote /RemoteFolder [--delete] [--watch] [--dry-run]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Options:[/]");
        Console.WriteLine("  --local <path>     Local source folder (overrides Sync:LocalPath).");
        Console.WriteLine("  --remote <path>    OneDrive destination, e.g. /Documents/Backup.");
        Console.WriteLine("  --clientid <guid>  Azure AD app client id (overrides AzureAd:ClientId).");
        Console.WriteLine("  --tenant <id>      Azure AD tenant (default: common).");
        Console.WriteLine("  --delete           Bidirectional deletion sync: propagate deletions from either side (uses a local manifest to track prior state).");
        Console.WriteLine("  --watch            Real-time watcher: delete from OneDrive immediately when a local file is removed.");
        Console.WriteLine("  --dry-run          Report actions without making changes.");
        Console.WriteLine("  --interval <sec>   Auto-sync every N seconds (0 = run once). Overrides Sync:AutoSyncIntervalSeconds.");
        Console.WriteLine("  -h, --help         Show this help.");
    }
}
