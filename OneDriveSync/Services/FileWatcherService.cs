using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using ThreadingChannel = System.Threading.Channels.Channel;
using UnboundedChannelOptions = System.Threading.Channels.UnboundedChannelOptions;
using OneDriveSync.Models;
using Spectre.Console;

namespace OneDriveSync.Services;

/// <summary>
/// Watches the local folder with <see cref="FileSystemWatcher"/> and immediately
/// deletes the corresponding OneDrive item whenever a local file or directory is removed.
/// Deletions are queued so the watcher callback is never blocked on network I/O.
/// </summary>
public sealed class FileWatcherService : IAsyncDisposable
{
    private readonly GraphServiceClient _graph;
    private readonly SyncOptions _options;
    private readonly FileSystemWatcher _watcher;
    private readonly System.Threading.Channels.Channel<string> _queue;
    private string _driveId = string.Empty;

    public FileWatcherService(GraphServiceClient graph, SyncOptions options)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        _queue = ThreadingChannel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

        _watcher = new FileSystemWatcher(options.LocalPath)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
        };

        // Enqueue the deleted path; the async loop in RunAsync picks it up.
        _watcher.Deleted += (_, e) => _queue.Writer.TryWrite(e.FullPath);
    }

    /// <summary>
    /// Resolves the drive ID, activates the watcher, then processes queued deletions
    /// until <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var drive = await _graph.Me.Drive.GetAsync(cancellationToken: ct).ConfigureAwait(false);
        _driveId = drive?.Id ?? throw new InvalidOperationException("Could not resolve OneDrive drive ID.");

        _watcher.EnableRaisingEvents = true;
        AnsiConsole.MarkupLine($"[grey]Watcher:[/] monitoring {_options.LocalPath.EscapeMarkup()} for deletions");

        try
        {
            await foreach (var localPath in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await HandleDeletionAsync(localPath, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task HandleDeletionAsync(string localPath, CancellationToken ct)
    {
        var relative = ToRemoteRelative(Path.GetRelativePath(_options.LocalPath, localPath));
        var remotePath = CombineRemote(_options.NormalizedOneDrivePath(), relative);

        try
        {
            if (_options.DryRun)
            {
                AnsiConsole.MarkupLine($"[blue][Watch] Would delete remote[/] {remotePath.EscapeMarkup()}");
                return;
            }

            DriveItem? item;
            try
            {
                item = await _graph.Drives[_driveId].Root
                    .ItemWithPath(remotePath)
                    .GetAsync(cancellationToken: ct)
                    .ConfigureAwait(false);
            }
            catch (ODataError ex) when (ex.ResponseStatusCode == 404)
            {
                return; // Already absent on OneDrive — nothing to do.
            }

            if (item?.Id is null) return;

            await _graph.Drives[_driveId].Items[item.Id]
                .DeleteAsync(cancellationToken: ct)
                .ConfigureAwait(false);

            AnsiConsole.MarkupLine($"[red][Watch] Deleted remote[/] {remotePath.EscapeMarkup()}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine(
                $"[red][Watch] Failed to delete[/] {remotePath.EscapeMarkup()}: {ex.Message.EscapeMarkup()}");
        }
    }

    public ValueTask DisposeAsync()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _queue.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private static string ToRemoteRelative(string path)
        => path.Replace(Path.DirectorySeparatorChar, '/').Replace('\\', '/');

    private static string CombineRemote(string root, string relative)
    {
        var left = root.TrimEnd('/');
        var right = relative.Trim('/');
        return string.IsNullOrEmpty(right) ? (left.Length == 0 ? "/" : left) : $"{left}/{right}";
    }
}
