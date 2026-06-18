using System.Security.Cryptography;
using System.Text;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using OneDriveSync.Models;
using Spectre.Console;

namespace OneDriveSync.Services;

/// <summary>
/// Performs a two-way synchronization between a local folder and OneDrive using the
/// Microsoft Graph SDK. Each file is compared by content hash (SHA-1) when available,
/// falling back to modification timestamps, and synced in whichever direction is newer.
///
/// When <c>--delete</c> is set the service maintains a JSON manifest of every file it
/// has successfully synced. On subsequent runs, a file that is present in the manifest
/// but has disappeared from one side is treated as a deletion and is removed from the
/// other side, propagating deletes in both directions.
/// </summary>
public sealed class SyncService
{
    // Graph recommends simple PUT uploads only for small files; use a resumable
    // upload session above this threshold (4 MiB).
    private const long SimpleUploadMaxBytes = 4 * 1024 * 1024;

    private readonly GraphServiceClient _graph;
    private readonly SyncOptions _options;

    private int _uploaded;
    private int _downloaded;
    private int _skipped;
    private int _deletedLocal;
    private int _deletedRemote;
    private int _failed;

    private enum SyncAction { Skip, Upload, Download, DeleteLocal, DeleteRemote }

    public SyncService(GraphServiceClient graph, SyncOptions options)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Runs the full two-way synchronization (including deletion propagation when
    /// <c>--delete</c> is set) and renders a summary table on completion.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var manifestPath = ComputeManifestPath(_options);
        var manifest = await SyncManifest.LoadAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var newManifest = new SyncManifest();

        var driveId = await GetDriveIdAsync(cancellationToken).ConfigureAwait(false);
        var remoteRoot = _options.NormalizedOneDrivePath();

        AnsiConsole.MarkupLine($"[grey]Drive:[/] {driveId}");
        AnsiConsole.MarkupLine($"[grey]Local:[/] {_options.LocalPath.EscapeMarkup()}");
        AnsiConsole.MarkupLine($"[grey]Remote:[/] {remoteRoot.EscapeMarkup()}");
        if (_options.DryRun)
            AnsiConsole.MarkupLine("[yellow]DRY RUN — no changes will be made.[/]");
        if (_options.Delete && manifest.Files.Count == 0)
            AnsiConsole.MarkupLine("[grey]No prior manifest found — building state now. Deletions will be tracked from the next run.[/]");

        // Enumerate local files, keyed by their equivalent remote path.
        var localByRemotePath = Directory
            .EnumerateFiles(_options.LocalPath, "*", SearchOption.AllDirectories)
            .Select(path => new LocalFile(
                FullPath: path,
                RelativePath: ToRemoteRelative(Path.GetRelativePath(_options.LocalPath, path))))
            .ToDictionary(
                f => CombineRemote(remoteRoot, f.RelativePath),
                f => f,
                StringComparer.OrdinalIgnoreCase);

        // Enumerate remote files (full DriveItem metadata needed for comparison).
        var remoteByPath = new Dictionary<string, DriveItem>(StringComparer.OrdinalIgnoreCase);
        var rootItem = await TryGetRemoteItemAsync(driveId, remoteRoot, cancellationToken).ConfigureAwait(false);
        if (rootItem?.Id is not null)
        {
            var remoteList = new List<(string Path, DriveItem Item)>();
            await CollectRemoteFilesAsync(driveId, rootItem.Id, remoteRoot, remoteList, cancellationToken)
                .ConfigureAwait(false);
            foreach (var (path, item) in remoteList)
                remoteByPath[path] = item;
        }

        // Union of local + remote + manifest paths.
        // Manifest paths are included so that files deleted from either side since the
        // last run are still visited and can be propagated as deletions.
        var allPaths = new HashSet<string>(localByRemotePath.Keys, StringComparer.OrdinalIgnoreCase);
        allPaths.UnionWith(remoteByPath.Keys);
        allPaths.UnionWith(manifest.Files.Keys);

        await AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[green]Syncing files[/]", maxValue: Math.Max(allPaths.Count, 1));

                foreach (var remotePath in allPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var inManifest = manifest.Files.TryGetValue(remotePath, out var manifestEntry);
                    localByRemotePath.TryGetValue(remotePath, out var localFile);
                    remoteByPath.TryGetValue(remotePath, out var remoteItem);
                    var localInfo = localFile.FullPath is not null ? new FileInfo(localFile.FullPath) : null;

                    try
                    {
                        var action = await DetermineActionAsync(localInfo, remoteItem, inManifest, cancellationToken)
                            .ConfigureAwait(false);

                        switch (action)
                        {
                            case SyncAction.Upload:
                                if (_options.DryRun)
                                {
                                    AnsiConsole.MarkupLine(
                                        $"[blue]Would upload[/] {localFile.RelativePath.EscapeMarkup()} ({FormatSize(localInfo!.Length)})");
                                }
                                else
                                {
                                    await UploadAsync(driveId, localFile, remotePath, localInfo!.Length, cancellationToken)
                                        .ConfigureAwait(false);
                                    newManifest.Files[remotePath] = new SyncManifest.ManifestEntry
                                    {
                                        Sha1Hash = await ComputeSha1Async(localInfo.FullName, cancellationToken).ConfigureAwait(false),
                                        LastSyncedUtc = DateTimeOffset.UtcNow,
                                    };
                                }
                                _uploaded++;
                                break;

                            case SyncAction.Download:
                                if (_options.DryRun)
                                {
                                    var rel = remotePath[remoteRoot.Length..].TrimStart('/');
                                    AnsiConsole.MarkupLine($"[blue]Would download[/] {rel.EscapeMarkup()}");
                                }
                                else
                                {
                                    await DownloadAsync(driveId, remoteItem!, remotePath, cancellationToken)
                                        .ConfigureAwait(false);
                                    newManifest.Files[remotePath] = new SyncManifest.ManifestEntry
                                    {
                                        Sha1Hash = remoteItem!.File?.Hashes?.Sha1Hash,
                                        LastSyncedUtc = DateTimeOffset.UtcNow,
                                    };
                                }
                                _downloaded++;
                                break;

                            case SyncAction.Skip:
                                // Retain manifest entry only when at least one side still exists.
                                if (localInfo is not null || remoteItem is not null)
                                {
                                    newManifest.Files[remotePath] = manifestEntry ?? new SyncManifest.ManifestEntry
                                    {
                                        Sha1Hash = remoteItem?.File?.Hashes?.Sha1Hash,
                                        LastSyncedUtc = DateTimeOffset.UtcNow,
                                    };
                                }
                                _skipped++;
                                break;

                            case SyncAction.DeleteLocal:
                                DeleteLocalFile(remotePath, remoteRoot);
                                _deletedLocal++;
                                break;

                            case SyncAction.DeleteRemote:
                                await DeleteRemoteItemAsync(driveId, remoteItem!, remotePath, cancellationToken)
                                    .ConfigureAwait(false);
                                _deletedRemote++;
                                break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _failed++;
                        AnsiConsole.MarkupLine(
                            $"[red]Failed[/] {remotePath.EscapeMarkup()}: {ex.Message.EscapeMarkup()}");
                        // Retain the prior manifest entry on failure so state is not lost.
                        if (manifestEntry is not null)
                            newManifest.Files[remotePath] = manifestEntry;
                    }

                    task.Increment(1);
                }
            })
            .ConfigureAwait(false);

        if (!_options.DryRun)
            await newManifest.SaveAsync(manifestPath, cancellationToken).ConfigureAwait(false);

        RenderSummary();
    }

    /// <summary>
    /// Determines the sync action for one file path.
    /// <list type="bullet">
    ///   <item>Both sides exist → compare by SHA-1 / timestamp → Upload / Download / Skip.</item>
    ///   <item>Only local exists + in manifest + <c>--delete</c> → remote was deleted → DeleteLocal.</item>
    ///   <item>Only remote exists + in manifest + <c>--delete</c> → local was deleted → DeleteRemote.</item>
    ///   <item>Only one side exists, not in manifest → new file → Upload / Download.</item>
    ///   <item>Neither side exists → Skip (stale manifest entry, both already deleted).</item>
    /// </list>
    /// </summary>
    private async Task<SyncAction> DetermineActionAsync(
        FileInfo? local, DriveItem? remote, bool inManifest, CancellationToken cancellationToken)
    {
        if (local is not null && remote is not null)
        {
            // SHA-1 is the most reliable equality check: matching hashes mean identical content.
            var remoteSha1 = remote.File?.Hashes?.Sha1Hash;
            if (!string.IsNullOrEmpty(remoteSha1))
            {
                var localSha1 = await ComputeSha1Async(local.FullName, cancellationToken).ConfigureAwait(false);
                if (string.Equals(localSha1, remoteSha1, StringComparison.OrdinalIgnoreCase))
                    return SyncAction.Skip;
            }

            // Fall back to modification time to determine which side is newer.
            var remoteModified = remote.LastModifiedDateTime?.UtcDateTime;
            if (remoteModified is null) return SyncAction.Upload;

            var diff = local.LastWriteTimeUtc - remoteModified.Value;
            if (Math.Abs(diff.TotalSeconds) <= 1) return SyncAction.Skip;
            return diff.TotalSeconds > 0 ? SyncAction.Upload : SyncAction.Download;
        }

        if (local is not null && remote is null)
        {
            // Remote side is gone. If we have a manifest entry the file was previously synced,
            // meaning the remote deletion is intentional — propagate it locally.
            if (_options.Delete && inManifest) return SyncAction.DeleteLocal;
            return SyncAction.Upload;
        }

        if (local is null && remote is not null)
        {
            // Local side is gone. If we have a manifest entry the file was previously synced,
            // meaning the local deletion is intentional — propagate it to remote.
            if (_options.Delete && inManifest) return SyncAction.DeleteRemote;
            return SyncAction.Download;
        }

        return SyncAction.Skip; // Both missing (both deleted, or stale manifest entry).
    }

    private void DeleteLocalFile(string remotePath, string remoteRoot)
    {
        var relative = remotePath[remoteRoot.Length..].TrimStart('/');
        var localPath = Path.Combine(_options.LocalPath, relative.Replace('/', Path.DirectorySeparatorChar));

        if (_options.DryRun)
        {
            AnsiConsole.MarkupLine($"[blue]Would delete local[/] {relative.EscapeMarkup()}");
            return;
        }

        if (!File.Exists(localPath)) return;

        File.Delete(localPath);
        AnsiConsole.MarkupLine($"[red]Deleted local[/] {relative.EscapeMarkup()}");

        // Remove empty parent directories left behind by the deletion.
        var dir = Path.GetDirectoryName(localPath);
        while (!string.IsNullOrEmpty(dir) &&
               !string.Equals(dir, _options.LocalPath, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.GetFileSystemEntries(dir).Length > 0) break;
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir);
        }
    }

    private async Task DeleteRemoteItemAsync(
        string driveId, DriveItem remoteItem, string remotePath, CancellationToken cancellationToken)
    {
        if (_options.DryRun)
        {
            AnsiConsole.MarkupLine($"[blue]Would delete remote[/] {remotePath.EscapeMarkup()}");
            return;
        }

        await _graph.Drives[driveId].Items[remoteItem.Id!]
            .DeleteAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        AnsiConsole.MarkupLine($"[red]Deleted remote[/] {remotePath.EscapeMarkup()}");
    }

    private async Task DownloadAsync(
        string driveId, DriveItem remoteItem, string remotePath, CancellationToken cancellationToken)
    {
        var remoteRoot = _options.NormalizedOneDrivePath();
        var relative = remotePath[remoteRoot.Length..].TrimStart('/');
        var localPath = Path.Combine(_options.LocalPath, relative.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

        var stream = await _graph.Drives[driveId].Items[remoteItem.Id!].Content
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

        // Preserve the remote modification time so subsequent runs skip unchanged files.
        if (remoteItem.LastModifiedDateTime.HasValue)
            File.SetLastWriteTimeUtc(localPath, remoteItem.LastModifiedDateTime.Value.UtcDateTime);

        AnsiConsole.MarkupLine($"[cyan]Downloaded[/] {relative.EscapeMarkup()}");
    }

    private async Task UploadAsync(
        string driveId, LocalFile file, string remoteItemPath, long size, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);

        if (size <= SimpleUploadMaxBytes)
        {
            await _graph.Drives[driveId]
                .Root
                .ItemWithPath(remoteItemPath)
                .Content
                .PutAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            AnsiConsole.MarkupLine($"[green]Uploaded[/] {file.RelativePath.EscapeMarkup()} ({FormatSize(size)})");
            return;
        }

        await LargeUploadAsync(driveId, remoteItemPath, stream, cancellationToken)
            .ConfigureAwait(false);
        AnsiConsole.MarkupLine($"[green]Uploaded[/] {file.RelativePath.EscapeMarkup()} ({FormatSize(size)})");
    }

    private async Task LargeUploadAsync(
        string driveId, string remoteItemPath, Stream stream, CancellationToken cancellationToken)
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

        var uploadSession = await _graph.Drives[driveId]
            .Root
            .ItemWithPath(remoteItemPath)
            .CreateUploadSession
            .PostAsync(uploadProps, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        const int maxSliceSize = 5 * 320 * 1024; // ~1.5 MiB, must be a multiple of 320 KiB.
        var fileUploadTask = new LargeFileUploadTask<DriveItem>(
            uploadSession, stream, maxSliceSize, _graph.RequestAdapter);

        await fileUploadTask.UploadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task CollectRemoteFilesAsync(
        string driveId,
        string folderId,
        string folderPath,
        List<(string Path, DriveItem Item)> sink,
        CancellationToken cancellationToken)
    {
        var children = await _graph.Drives[driveId].Items[folderId].Children
            .GetAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (children?.Value is null) return;

        // Page through all children rather than assuming a single response page.
        var pageItems = new List<DriveItem>(children.Value);
        var nextLink = children.OdataNextLink;
        while (!string.IsNullOrEmpty(nextLink))
        {
            var page = await _graph.Drives[driveId].Items[folderId].Children
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

            var childPath = CombineRemote(folderPath, item.Name);
            if (item.Folder is not null)
            {
                await CollectRemoteFilesAsync(driveId, item.Id, childPath, sink, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (item.File is not null)
            {
                sink.Add((childPath, item));
            }
        }
    }

    private async Task<DriveItem?> TryGetRemoteItemAsync(
        string driveId, string remotePath, CancellationToken cancellationToken)
    {
        try
        {
            if (remotePath == "/")
            {
                return await _graph.Drives[driveId].Root
                    .GetAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            return await _graph.Drives[driveId].Root
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

    private void RenderSummary()
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Result");
        table.AddColumn(new TableColumn("Count").RightAligned());

        table.AddRow("[green]Uploaded[/]", _uploaded.ToString());
        table.AddRow("[cyan]Downloaded[/]", _downloaded.ToString());
        table.AddRow("[grey]Skipped (unchanged)[/]", _skipped.ToString());
        if (_options.Delete)
        {
            table.AddRow("[red]Deleted local[/]", _deletedLocal.ToString());
            table.AddRow("[red]Deleted remote[/]", _deletedRemote.ToString());
        }
        table.AddRow(_failed > 0 ? "[red]Failed[/]" : "[grey]Failed[/]", _failed.ToString());

        AnsiConsole.Write(table);
    }

    /// <summary>
    /// Derives a stable per-sync-pair path for the manifest file, stored alongside
    /// the authentication record in <c>~/.local/share/OneDriveSync/</c>.
    /// </summary>
    private static string ComputeManifestPath(SyncOptions options)
    {
        var key = $"{options.LocalPath}|{options.NormalizedOneDrivePath()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var hashStr = Convert.ToHexString(hash)[..16];
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OneDriveSync");
        return Path.Combine(dir, $"manifest-{hashStr}.json");
    }

    private static async Task<string> ComputeSha1Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        using var sha1 = SHA1.Create();
        var hash = await sha1.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash); // Upper-case hex, matching Graph's sha1Hash format.
    }

    private static string ToRemoteRelative(string relativePath)
        => relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace('\\', '/');

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

    private readonly record struct LocalFile(string FullPath, string RelativePath);
}