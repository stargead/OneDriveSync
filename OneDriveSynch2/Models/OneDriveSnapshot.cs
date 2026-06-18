using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneDriveSynch2.Models;

public sealed class SnapshotEntry
{
    /// <summary>OneDrive item ID.</summary>
    public string Id { get; set; } = "";

    /// <summary>Path relative to the configured OneDrive root, using '/' separators.</summary>
    public string RelativePath { get; set; } = "";

    public DateTimeOffset LastModified { get; set; }
}

/// <summary>
/// Persisted state used by the OneDrive poller to detect creates, updates, moves
/// and deletes between polling cycles. Only <see cref="ByPath"/> is serialized;
/// <see cref="ById"/> is reconstructed from it on load.
/// </summary>
public sealed class OneDriveSnapshot
{
    /// <summary>Keyed by relative path (relative to the OneDrive root), case-insensitive.</summary>
    [JsonInclude]
    public Dictionary<string, SnapshotEntry> ByPath { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Keyed by OneDrive item ID. Not serialized; rebuilt on load.</summary>
    [JsonIgnore]
    public Dictionary<string, SnapshotEntry> ById { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<OneDriveSnapshot> LoadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return new OneDriveSnapshot();

        try
        {
            await using var stream = File.OpenRead(path);
            var snapshot = await JsonSerializer
                .DeserializeAsync<OneDriveSnapshot>(stream, JsonOptions, ct)
                .ConfigureAwait(false) ?? new OneDriveSnapshot();

            snapshot.RebuildById();
            return snapshot;
        }
        catch
        {
            return new OneDriveSnapshot();
        }
    }

    public async Task SaveAsync(string path, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null) Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        await using (var stream = File.Create(tmp))
            await JsonSerializer.SerializeAsync(stream, this, JsonOptions, ct).ConfigureAwait(false);

        File.Move(tmp, path, overwrite: true);
    }

    private void RebuildById()
    {
        ById = new Dictionary<string, SnapshotEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in ByPath.Values)
        {
            if (!string.IsNullOrEmpty(entry.Id))
                ById[entry.Id] = entry;
        }
    }
}
