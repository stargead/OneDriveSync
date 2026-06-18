using System.Security.Cryptography;
using System.Text;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using OneDriveSynch2.Models;
using Spectre.Console;

namespace OneDriveSynch2.Services;

/// <summary>
/// Periodically enumerates the remote OneDrive folder and reconciles changes
/// (created, updated, moved, deleted) against the local folder. Change detection
/// is driven by a persisted snapshot keyed by item ID and relative path.
/// </summary>
public sealed class OneDrivePollerService : IAsyncDisposable
{
    // Timestamp comparisons tolerate small clock/metadata skew between local and remote.
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);

    private readonly GraphServiceClient _graph;
    private readonly SyncOptions _options;
    private readonly SyncLock _syncLock;
    private readonly string _snapshotPath;

    private string? _driveId;
    private string _remoteRoot = "/";

    public OneDrivePollerService(GraphServiceClient graph, SyncOptions options, SyncLock syncLock)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _syncLock = syncLock ?? throw new ArgumentNullException(nameof(syncLock));
        _snapshotPath = ComputeSnapshotPath(options);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _driveId = await GetDriveIdAsync(cancellationToken).ConfigureAwait(false);
        _remoteRoot = _options.NormalizedOneDrivePath();

        var snapshot = await OneDriveSnapshot.LoadAsync(_snapshotPath, cancellationToken).ConfigureAwait(false);

        AnsiConsole.MarkupLine(
            $"[green]OneDrive poller active[/] every {_options.OneDrivePollIntervalSeconds}s on {_remoteRoot.EscapeMarkup()}");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using (await _syncLock.AcquireAsync(cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        snapshot = await PollOnceAsync(snapshot, cancellationToken).ConfigureAwait(false);
                        await snapshot.SaveAsync(_snapshotPath, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine(
                            $"[red]OneDrive poll failed:[/] {ex.Message.EscapeMarkup()}");
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(_options.OneDrivePollIntervalSeconds), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown.
        }
    }

    /// <summary>
    /// Performs one reconciliation cycle and returns the snapshot reflecting the
    /// post-reconciliation remote state.
    /// </summary>
    private async Task<OneDriveSnapshot> PollOnceAsync(
        OneDriveSnapshot previous, CancellationToken cancellationToken)
    {
        var current = new OneDriveSnapshot();

        var rootItem = await TryGetRemoteItemAsync(_remoteRoot, cancellationToken).ConfigureAwait(false);
        var remoteItems = new List<(string RelativePath, DriveItem Item)>();
        if (rootItem?.Id is not null)
        {
            await CollectRemoteItemsAsync(rootItem.Id, string.Empty, remoteItems, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var (relativePath, item) in remoteItems)
        {
            var entry = new SnapshotEntry
            {
                Id = item.Id!,
                RelativePath = relativePath,
                LastModified = item.LastModifiedDateTime ?? DateTimeOffset.MinValue,
            };
            current.ByPath[relativePath] = entry;
            current.ById[item.Id!] = entry;
        }

        var itemsById = remoteItems.ToDictionary(
            x => x.Item.Id!, x => x.Item, StringComparer.OrdinalIgnoreCase);

        // (a) Deleted: present in previous snapshot but the ID is gone from the current scan.
        foreach (var prevEntry in previous.ById.Values)
        {
            if (!current.ById.ContainsKey(prevEntry.Id))
                DeleteLocalFile(prevEntry.RelativePath);
        }

        // (b)/(c)/(d): walk current items and classify against the previous snapshot.
        foreach (var (relativePath, item) in remoteItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hasPrevById = previous.ById.TryGetValue(item.Id!, out var prevById);

            if (hasPrevById && !string.Equals(prevById!.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
            {
                // (b) Moved: same ID, different path.
                await MoveLocalFileAsync(prevById.RelativePath, relativePath, item, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (hasPrevById)
            {
                // (c) Updated: same ID and path, but the modification time changed.
                var remoteModified = item.LastModifiedDateTime ?? DateTimeOffset.MinValue;
                if (Math.Abs((remoteModified - prevById!.LastModified).TotalSeconds) > TimestampTolerance.TotalSeconds)
                {
                    await DownloadIfRemoteNewerAsync(relativePath, item, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                // (d) Created: ID not seen before.
                await DownloadIfRemoteNewerAsync(relativePath, item, cancellationToken).ConfigureAwait(false);
            }
        }

        return current;
    }

    private async Task DownloadIfRemoteNewerAsync(
        string relativePath, DriveItem item, CancellationToken cancellationToken)
    {
        var localPath = LocalPathFor(relativePath);
        var remoteModified = item.LastModifiedDateTime?.UtcDateTime;

        if (File.Exists(localPath) && remoteModified is not null)
        {
            var localModified = File.GetLastWriteTimeUtc(localPath);
            if ((remoteModified.Value - localModified) <= TimestampTolerance)
                return; // Local is same or newer.
        }

        await DownloadAsync(item, relativePath, cancellationToken).ConfigureAwait(false);
    }

    // ---- Remote enumeration ----------------------------------------------

    private async Task CollectRemoteItemsAsync(
        string folderId,
        string folderRelativePath,
        List<(string RelativePath, DriveItem Item)> sink,
        CancellationToken cancellationToken)
    {
        var children = await _graph.Drives[_driveId].Items[folderId].Children
            .GetAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (children?.Value is null) return;

        var pageItems = new List<DriveItem>(children.Value);
        var nextLink = children.OdataNextLink;
        while (!string.IsNullOrEmpty(nextLink))
        {
            var page = await _graph.Drives[_driveId].Items[folderId].Children
                .WithUrl(nextLink)
                .GetAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (page?.Value is null) break;
            pageItems.AddRange(page.Value);
            nextLink = page.OdataNextLink;
        }

        foreach (var item in pageItems)
        {
            if (item.Name is null || item.Id is null) continue;

            var childRelative = CombineRelative(folderRelativePath, item.Name);
            if (item.Folder is not null)
            {
                await CollectRemoteItemsAsync(item.Id, childRelative, sink, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (item.File is not null)
            {
                sink.Add((childRelative, item));
            }
        }
    }

    // ---- Local file operations -------------------------------------------

    private async Task DownloadAsync(DriveItem item, string relativePath, CancellationToken cancellationToken)
    {
        var localPath = LocalPathFor(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

        var stream = await _graph.Drives[_driveId].Items[item.Id!].Content
            .GetAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (stream is null) return;

        await using (stream)
        await using (var fileStream = new FileStream(
            localPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true))
        {
            await stream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
        }

        if (item.LastModifiedDateTime.HasValue)
            File.SetLastWriteTimeUtc(localPath, item.LastModifiedDateTime.Value.UtcDateTime);

        AnsiConsole.MarkupLine($"[cyan]Downloaded[/] {relativePath.EscapeMarkup()}");
    }

    private void DeleteLocalFile(string relativePath)
    {
        var localPath = LocalPathFor(relativePath);
        if (!File.Exists(localPath)) return;

        File.Delete(localPath);
        AnsiConsole.MarkupLine($"[red]Deleted local[/] {relativePath.EscapeMarkup()}");

        var dir = Path.GetDirectoryName(localPath);
        while (!string.IsNullOrEmpty(dir) &&
               !string.Equals(dir, _options.LocalPath, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.GetFileSystemEntries(dir).Length > 0) break;
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir);
        }
    }

    private async Task MoveLocalFileAsync(
        string oldRelativePath, string newRelativePath, DriveItem item, CancellationToken cancellationToken)
    {
        var oldLocalPath = LocalPathFor(oldRelativePath);
        var newLocalPath = LocalPathFor(newRelativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(newLocalPath)!);

        if (!File.Exists(oldLocalPath))
        {
            // Source missing locally — just materialize the file at its new path.
            await DownloadAsync(item, newRelativePath, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (File.Exists(newLocalPath))
        {
            // Destination already exists — keep whichever copy is newer.
            var oldModified = File.GetLastWriteTimeUtc(oldLocalPath);
            var newModified = File.GetLastWriteTimeUtc(newLocalPath);
            if (oldModified > newModified)
            {
                File.Delete(newLocalPath);
                File.Move(oldLocalPath, newLocalPath);
                AnsiConsole.MarkupLine(
                    $"[yellow]Move conflict[/] {newRelativePath.EscapeMarkup()} — kept moved (newer) copy.");
            }
            else
            {
                File.Delete(oldLocalPath);
                AnsiConsole.MarkupLine(
                    $"[yellow]Move conflict[/] {newRelativePath.EscapeMarkup()} — kept existing destination.");
            }

            CleanupEmptyParents(oldLocalPath);
            return;
        }

        File.Move(oldLocalPath, newLocalPath, overwrite: false);
        CleanupEmptyParents(oldLocalPath);
        AnsiConsole.MarkupLine(
            $"[cyan]Moved local[/] {oldRelativePath.EscapeMarkup()} -> {newRelativePath.EscapeMarkup()}");
    }

    private void CleanupEmptyParents(string localPath)
    {
        var dir = Path.GetDirectoryName(localPath);
        while (!string.IsNullOrEmpty(dir) &&
               !string.Equals(dir, _options.LocalPath, StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir) || Directory.GetFileSystemEntries(dir).Length > 0) break;
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir);
        }
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

    // ---- Path helpers -----------------------------------------------------

    private string LocalPathFor(string relativePath) =>
        Path.Combine(_options.LocalPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string CombineRelative(string parent, string name) =>
        string.IsNullOrEmpty(parent) ? name : $"{parent}/{name}";

    private static string ComputeSnapshotPath(SyncOptions options)
    {
        var key = $"{options.LocalPath}|{options.NormalizedOneDrivePath()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var hashStr = Convert.ToHexString(hash)[..8];
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OneDriveSync");
        return Path.Combine(dir, $"snapshot-{hashStr}.json");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
