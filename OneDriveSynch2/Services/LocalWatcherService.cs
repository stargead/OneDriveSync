using System.Threading.Channels;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using OneDriveSynch2.Models;
using Spectre.Console;

namespace OneDriveSynch2.Services;

/// <summary>
/// Watches the local folder tree and propagates changes to OneDrive. File-system
/// events are debounced and funnelled through a <see cref="Channel{T}"/>, then
/// processed one at a time under the shared <see cref="SyncLock"/>.
/// </summary>
public sealed class LocalWatcherService : IAsyncDisposable
{
    private const long SimpleUploadMaxBytes = 4 * 1024 * 1024;
    private const int LockedFileMaxRetries = 5;
    private static readonly TimeSpan LockedFileRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(500);

    // Timestamp comparisons tolerate small clock/metadata skew between local and remote.
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);

    private enum LocalEventType { Created, Changed, Renamed, Deleted }

    private readonly record struct LocalFileEvent(LocalEventType Type, string Path, string? OldPath = null);

    private readonly GraphServiceClient _graph;
    private readonly SyncOptions _options;
    private readonly SyncLock _syncLock;

    private readonly Channel<LocalFileEvent> _channel =
        System.Threading.Channels.Channel.CreateUnbounded<LocalFileEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly Lock _debounceGate = new();
    private readonly Dictionary<string, CancellationTokenSource> _debounceTimers =
        new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _watcher;
    private string? _driveId;
    private string _remoteRoot = "/";

    public LocalWatcherService(GraphServiceClient graph, SyncOptions options, SyncLock syncLock)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _syncLock = syncLock ?? throw new ArgumentNullException(nameof(syncLock));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _driveId = await GetDriveIdAsync(cancellationToken).ConfigureAwait(false);
        _remoteRoot = _options.NormalizedOneDrivePath();

        _watcher = new FileSystemWatcher(_options.LocalPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite | NotifyFilters.Size,
        };

        _watcher.Created += OnCreated;
        _watcher.Changed += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Deleted += OnDeleted;
        _watcher.Error += OnError;
        _watcher.EnableRaisingEvents = true;

        AnsiConsole.MarkupLine($"[green]Local watcher active[/] on {_options.LocalPath.EscapeMarkup()}");

        try
        {
            await foreach (var evt in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                using (await _syncLock.AcquireAsync(cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        await ProcessEventAsync(evt, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine(
                            $"[red]Local sync failed[/] {evt.Path.EscapeMarkup()}: {ex.Message.EscapeMarkup()}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown.
        }
    }

    private Task ProcessEventAsync(LocalFileEvent evt, CancellationToken cancellationToken) => evt.Type switch
    {
        LocalEventType.Created or LocalEventType.Changed => ProcessUploadAsync(evt.Path, cancellationToken),
        LocalEventType.Renamed => ProcessRenameAsync(evt.OldPath!, evt.Path, cancellationToken),
        LocalEventType.Deleted => ProcessDeleteAsync(evt.Path, cancellationToken),
        _ => Task.CompletedTask,
    };

    // ---- Event handlers ---------------------------------------------------

    private void OnCreated(object sender, FileSystemEventArgs e) =>
        Debounce(new LocalFileEvent(LocalEventType.Created, e.FullPath));

    private void OnChanged(object sender, FileSystemEventArgs e) =>
        Debounce(new LocalFileEvent(LocalEventType.Changed, e.FullPath));

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // Renames are not debounced — they carry both old and new paths and must
        // be applied as a single PATCH/move.
        CancelDebounce(e.OldFullPath);
        CancelDebounce(e.FullPath);
        _channel.Writer.TryWrite(new LocalFileEvent(LocalEventType.Renamed, e.FullPath, e.OldFullPath));
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        CancelDebounce(e.FullPath);
        _channel.Writer.TryWrite(new LocalFileEvent(LocalEventType.Deleted, e.FullPath));
    }

    private void OnError(object sender, ErrorEventArgs e) =>
        AnsiConsole.MarkupLine($"[red]Local watcher error:[/] {e.GetException().Message.EscapeMarkup()}");

    /// <summary>
    /// Coalesces rapid Created/Changed events on the same path into a single enqueue
    /// after a quiet period, so a file being written incrementally is only synced once.
    /// </summary>
    private void Debounce(LocalFileEvent evt)
    {
        CancellationTokenSource cts;
        lock (_debounceGate)
        {
            if (_debounceTimers.TryGetValue(evt.Path, out var existing))
            {
                existing.Cancel();
                existing.Dispose();
            }

            cts = new CancellationTokenSource();
            _debounceTimers[evt.Path] = cts;
        }

        _ = Task.Delay(DebounceInterval, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;

            lock (_debounceGate)
            {
                if (_debounceTimers.TryGetValue(evt.Path, out var current) && current == cts)
                    _debounceTimers.Remove(evt.Path);
            }

            _channel.Writer.TryWrite(evt);
            cts.Dispose();
        }, TaskScheduler.Default);
    }

    private void CancelDebounce(string path)
    {
        lock (_debounceGate)
        {
            if (_debounceTimers.Remove(path, out var existing))
            {
                existing.Cancel();
                existing.Dispose();
            }
        }
    }

    // ---- Processing -------------------------------------------------------

    private async Task ProcessUploadAsync(string localPath, CancellationToken cancellationToken)
    {
        if (Directory.Exists(localPath))
            return; // Directories are created implicitly when their files upload.

        if (!File.Exists(localPath))
            return; // Removed between the event and processing.

        if (!await WaitForUnlockedAsync(localPath, cancellationToken).ConfigureAwait(false))
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Skipped (locked)[/] {Relative(localPath).EscapeMarkup()} after {LockedFileMaxRetries} retries.");
            return;
        }

        var info = new FileInfo(localPath);
        if (!info.Exists) return;

        var remotePath = RemotePathFor(localPath);
        var remoteItem = await TryGetRemoteItemAsync(remotePath, cancellationToken).ConfigureAwait(false);

        if (remoteItem is not null)
        {
            var remoteModified = remoteItem.LastModifiedDateTime?.UtcDateTime;
            if (remoteModified is null)
            {
                await UploadAsync(localPath, remotePath, info.Length, cancellationToken).ConfigureAwait(false);
                return;
            }

            var diff = info.LastWriteTimeUtc - remoteModified.Value;
            if (diff > TimestampTolerance)
            {
                await UploadAsync(localPath, remotePath, info.Length, cancellationToken).ConfigureAwait(false);
            }
            else if (diff < -TimestampTolerance)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]CONFLICT[/] {Relative(localPath).EscapeMarkup()} — OneDrive copy is newer; not uploading.");
            }
            // else: within tolerance -> identical, skip.
        }
        else
        {
            await UploadAsync(localPath, remotePath, info.Length, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessRenameAsync(string oldPath, string newPath, CancellationToken cancellationToken)
    {
        // FileSystemWatcher fires Renamed for directories too; for a directory rename the
        // child items move with it server-side once the parent moves, so the same PATCH
        // logic applies whether the path is a file or a folder.
        var oldRemotePath = RemotePathFor(oldPath);
        var oldItem = await TryGetRemoteItemAsync(oldRemotePath, cancellationToken).ConfigureAwait(false);

        if (oldItem is null)
        {
            // Nothing to move on the remote — treat the destination as a fresh create.
            await ProcessUploadAsync(newPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (IsInsideLocalRoot(newPath))
        {
            var newRelative = Relative(newPath);
            var newName = Path.GetFileName(newPath);
            var newRemoteParent = RemoteParentPath(newRelative);

            var patch = new DriveItem
            {
                Name = newName,
                ParentReference = new ItemReference
                {
                    Path = $"/drives/{_driveId}/root:{newRemoteParent}",
                },
            };

            await _graph.Drives[_driveId].Items[oldItem.Id!]
                .PatchAsync(patch, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            AnsiConsole.MarkupLine(
                $"[cyan]Moved[/] {Relative(oldPath).EscapeMarkup()} -> {newRelative.EscapeMarkup()}");
        }
        else
        {
            await _graph.Drives[_driveId].Items[oldItem.Id!]
                .DeleteAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            AnsiConsole.MarkupLine(
                $"[red]Deleted remote[/] {Relative(oldPath).EscapeMarkup()} (moved outside watched folder).");
        }
    }

    private async Task ProcessDeleteAsync(string localPath, CancellationToken cancellationToken)
    {
        var remotePath = RemotePathFor(localPath);
        var item = await TryGetRemoteItemAsync(remotePath, cancellationToken).ConfigureAwait(false);
        if (item is null) return; // Already gone.

        await _graph.Drives[_driveId].Items[item.Id!]
            .DeleteAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        AnsiConsole.MarkupLine($"[red]Deleted remote[/] {Relative(localPath).EscapeMarkup()}");
    }

    // ---- Upload helpers ---------------------------------------------------

    private async Task UploadAsync(string localPath, string remotePath, long size, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 81920, useAsync: true);

        if (size <= SimpleUploadMaxBytes)
        {
            await _graph.Drives[_driveId]
                .Root
                .ItemWithPath(remotePath)
                .Content
                .PutAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await LargeUploadAsync(remotePath, stream, cancellationToken).ConfigureAwait(false);
        }

        AnsiConsole.MarkupLine(
            $"[green]Uploaded[/] {Relative(localPath).EscapeMarkup()} ({FormatSize(size)})");
    }

    private async Task LargeUploadAsync(string remotePath, Stream stream, CancellationToken cancellationToken)
    {
        var uploadProps = new Microsoft.Graph.Drives.Item.Items.Item.CreateUploadSession.CreateUploadSessionPostRequestBody
        {
            Item = new DriveItemUploadableProperties
            {
                AdditionalData = new Dictionary<string, object>
                {
                    ["@microsoft.graph.conflictBehavior"] = "replace",
                },
            },
        };

        var uploadSession = await _graph.Drives[_driveId]
            .Root
            .ItemWithPath(remotePath)
            .CreateUploadSession
            .PostAsync(uploadProps, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        const int maxSliceSize = 5 * 320 * 1024; // ~1.5 MiB, multiple of 320 KiB.
        var uploadTask = new LargeFileUploadTask<DriveItem>(
            uploadSession, stream, maxSliceSize, _graph.RequestAdapter);

        await uploadTask.UploadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // ---- Graph helpers ----------------------------------------------------

    private async Task<DriveItem?> TryGetRemoteItemAsync(string remotePath, CancellationToken cancellationToken)
    {
        try
        {
            if (remotePath == "/")
            {
                return await _graph.Drives[_driveId].Root
                    .GetAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            return await _graph.Drives[_driveId].Root
                .ItemWithPath(remotePath)
                .GetAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            return null;
        }
    }

    private async Task<string> GetDriveIdAsync(CancellationToken cancellationToken)
    {
        var drive = await _graph.Me.Drive
            .GetAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (drive?.Id is null)
            throw new InvalidOperationException("Could not resolve the signed-in user's OneDrive.");

        return drive.Id;
    }

    // ---- Locked-file handling --------------------------------------------

    private async Task<bool> WaitForUnlockedAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < LockedFileMaxRetries; attempt++)
        {
            if (!IsFileLocked(path)) return true;
            await Task.Delay(LockedFileRetryDelay, cancellationToken).ConfigureAwait(false);
        }

        return !IsFileLocked(path);
    }

    private static bool IsFileLocked(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    // ---- Path helpers -----------------------------------------------------

    private string Relative(string localPath) =>
        ToRemoteRelative(Path.GetRelativePath(_options.LocalPath, localPath));

    private string RemotePathFor(string localPath) =>
        CombineRemote(_remoteRoot, Relative(localPath));

    private bool IsInsideLocalRoot(string localPath)
    {
        var relative = Path.GetRelativePath(_options.LocalPath, localPath);
        return !relative.StartsWith("..", StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    /// <summary>Remote parent folder (absolute, drive-root relative) for a given relative path.</summary>
    private string RemoteParentPath(string relativePath)
    {
        var remoteParentRelative = ToRemoteRelative(
            Path.GetDirectoryName(relativePath) ?? string.Empty);
        return CombineRemote(_remoteRoot, remoteParentRelative);
    }

    private static string ToRemoteRelative(string relativePath) =>
        relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace('\\', '/');

    private static string CombineRemote(string root, string relative)
    {
        var left = root.TrimEnd('/');
        var right = relative.Trim('/');
        return string.IsNullOrEmpty(right) ? (left.Length == 0 ? "/" : left) : $"{left}/{right}";
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.##} {units[unit]}";
    }

    public ValueTask DisposeAsync()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnCreated;
            _watcher.Changed -= OnChanged;
            _watcher.Renamed -= OnRenamed;
            _watcher.Deleted -= OnDeleted;
            _watcher.Error -= OnError;
            _watcher.Dispose();
            _watcher = null;
        }

        _channel.Writer.TryComplete();

        lock (_debounceGate)
        {
            foreach (var cts in _debounceTimers.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }
            _debounceTimers.Clear();
        }

        return ValueTask.CompletedTask;
    }
}
