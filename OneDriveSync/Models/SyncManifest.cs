using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneDriveSync.Models;

/// <summary>
/// Persisted snapshot of every file successfully synced in a prior run.
/// Used to distinguish "deleted from one side" from "never existed on that side".
/// </summary>
public sealed class SyncManifest
{
    [JsonInclude]
    public Dictionary<string, ManifestEntry> Files { get; private set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public sealed class ManifestEntry
    {
        public string? Sha1Hash { get; set; }
        public DateTimeOffset LastSyncedUtc { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<SyncManifest> LoadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return new SyncManifest();
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<SyncManifest>(stream, JsonOptions, ct).ConfigureAwait(false)
                   ?? new SyncManifest();
        }
        catch
        {
            return new SyncManifest();
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
}