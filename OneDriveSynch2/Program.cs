using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Graph.Models.ODataErrors;
using OneDriveSynch2.Models;
using OneDriveSynch2.Services;
using Spectre.Console;

// Exit codes: 0=ok, 1=config error, 2=auth error, 3=permission denied,
// 4=dir not found, 5=unexpected, 130=cancelled.

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    AnsiConsole.MarkupLine("[grey]Shutdown requested — stopping watchers...[/]");
};

if (args.Contains("-h") || args.Contains("--help"))
{
    PrintHelp();
    return 0;
}

AnsiConsole.Write(new FigletText("OneDriveSynch2").Color(Color.Blue));

SyncOptions options;
try
{
    options = BuildOptions(args);
    options.Validate();
}
catch (DirectoryNotFoundException ex)
{
    AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message.EscapeMarkup()}");
    return 4;
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[red]Configuration error:[/] {ex.Message.EscapeMarkup()}");
    return 1;
}

var authService = new AuthService(options);
Microsoft.Graph.GraphServiceClient graph;
try
{
    graph = await authService.CreateClientAsync(cts.Token).ConfigureAwait(false);
}
catch (OperationCanceledException)
{
    return 130;
}
catch (AuthenticationFailedException ex)
{
    AnsiConsole.MarkupLine($"[red]Authentication failed:[/] {ex.Message.EscapeMarkup()}");
    return 2;
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[red]Authentication error:[/] {ex.Message.EscapeMarkup()}");
    return 2;
}

using var syncLock = new SyncLock();
await using var localWatcher = new LocalWatcherService(graph, options, syncLock);
await using var oneDrivePoller = new OneDrivePollerService(graph, options, syncLock);

try
{
    await Task.WhenAll(
        localWatcher.RunAsync(cts.Token),
        oneDrivePoller.RunAsync(cts.Token)).ConfigureAwait(false);
    return 0;
}
catch (OperationCanceledException)
{
    AnsiConsole.MarkupLine("[grey]Stopped.[/]");
    return 130;
}
catch (UnauthorizedAccessException ex)
{
    AnsiConsole.MarkupLine($"[red]Permission denied:[/] {ex.Message.EscapeMarkup()}");
    return 3;
}
catch (ODataError ex)
{
    AnsiConsole.MarkupLine($"[red]Graph error:[/] {ex.Message.EscapeMarkup()}");
    return 5;
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[red]Unexpected error:[/] {ex.Message.EscapeMarkup()}");
    return 5;
}

static SyncOptions BuildOptions(string[] args)
{
    var baseDir = AppContext.BaseDirectory;

    var config = new ConfigurationBuilder()
        .SetBasePath(baseDir)
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
        .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
        .AddEnvironmentVariables(prefix: "ONEDRIVESYNCH2_")
        .Build();

    var parsed = ParseArgs(args);

    var clientId = parsed.GetValueOrDefault("clientid") ?? config["AzureAd:ClientId"] ?? "";
    var tenant = parsed.GetValueOrDefault("tenant") ?? config["AzureAd:TenantId"] ?? "consumers";
    var localPath = parsed.GetValueOrDefault("local") ?? config["Sync:LocalPath"] ?? "";
    var remotePath = parsed.GetValueOrDefault("remote") ?? config["Sync:OneDrivePath"] ?? "";

    var intervalRaw = parsed.GetValueOrDefault("interval") ?? config["Sync:OneDrivePollIntervalSeconds"];
    var interval = 60;
    if (!string.IsNullOrWhiteSpace(intervalRaw) && int.TryParse(intervalRaw, out var parsedInterval))
        interval = parsedInterval;

    return new SyncOptions
    {
        ClientId = clientId,
        TenantId = string.IsNullOrWhiteSpace(tenant) ? "consumers" : tenant,
        LocalPath = localPath,
        OneDrivePath = remotePath,
        OneDrivePollIntervalSeconds = interval,
    };
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["--local"] = "local",
        ["--remote"] = "remote",
        ["--clientid"] = "clientid",
        ["--tenant"] = "tenant",
        ["--interval"] = "interval",
    };

    for (var i = 0; i < args.Length; i++)
    {
        if (aliases.TryGetValue(args[i], out var key) && i + 1 < args.Length)
        {
            map[key] = args[++i];
        }
    }

    return map;
}

static void PrintHelp()
{
    AnsiConsole.WriteLine("OneDriveSynch2 2.0.0 — bidirectional OneDrive folder sync.");
    AnsiConsole.WriteLine();
    AnsiConsole.WriteLine("Usage: onedrivesynch2 [options]");
    AnsiConsole.WriteLine();
    AnsiConsole.WriteLine("Options:");
    AnsiConsole.WriteLine("  --local <path>       Local folder to watch.");
    AnsiConsole.WriteLine("  --remote <path>      OneDrive folder (drive-root relative).");
    AnsiConsole.WriteLine("  --clientid <guid>    Azure AD application (client) ID.");
    AnsiConsole.WriteLine("  --tenant <id>        Azure AD tenant (default: consumers).");
    AnsiConsole.WriteLine("  --interval <sec>     OneDrive poll interval in seconds (default: 60).");
    AnsiConsole.WriteLine("  -h, --help           Show this help.");
}
