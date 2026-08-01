using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AiGameSave.Core;

namespace AiGameSave.Infrastructure;

public sealed class JsonGameRepository : IGameRepository
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly PathTemplateResolver _resolver = new();
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public JsonGameRepository(string rootPath)
    {
        RootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(Path.Combine(RootPath, "games"));
        Directory.CreateDirectory(Path.Combine(RootPath, "rules", "official"));
        Directory.CreateDirectory(Path.Combine(RootPath, "rules", "user"));
        Directory.CreateDirectory(Path.Combine(RootPath, ".staging"));
    }

    public string RootPath { get; }

    public async Task<IReadOnlyList<GameProfile>> ListGamesAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(RootPath, "games");
        var result = new List<GameProfile>();
        foreach (var file in Directory.EnumerateFiles(directory, "game.json", SearchOption.AllDirectories))
        {
            try
            {
                var profile = await ReadAsync<GameProfile>(file, cancellationToken);
                if (profile is not null) result.Add(profile);
            }
            catch { /* A damaged game entry must not hide other games. */ }
        }
        return result.OrderBy(x => x.Name).ToArray();
    }

    public async Task SaveGameAsync(GameProfile profile, CancellationToken cancellationToken = default)
    {
        var directory = GameDirectory(profile.Id);
        Directory.CreateDirectory(directory);
        await WriteAtomicAsync(Path.Combine(directory, "game.json"), profile, cancellationToken);
    }

    public Task<GameProfile?> GetGameAsync(string gameId, CancellationToken cancellationToken = default)
        => ReadAsync<GameProfile>(Path.Combine(GameDirectory(gameId), "game.json"), cancellationToken);

    public async Task<SnapshotManifest> CreateSnapshotAsync(GameProfile profile, bool pinned = false, bool safety = false, string? note = null, CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var snapshotId = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            var staging = Path.Combine(RootPath, ".staging", profile.Id + "_" + snapshotId);
            var final = Path.Combine(GameDirectory(profile.Id), "snapshots", snapshotId);
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(Path.Combine(GameDirectory(profile.Id), "snapshots"));
            var entries = new List<FileSnapshotEntry>();
            var zipPath = Path.Combine(staging, "files.zip");

            await using (var zipStream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                for (var locationIndex = 0; locationIndex < profile.SaveLocations.Count; locationIndex++)
                {
                    var location = profile.SaveLocations[locationIndex];
                    var source = _resolver.ResolveExisting(_resolver.Resolve(location.PathTemplate, profile));
                    if (!Directory.Exists(source) || !_resolver.IsAllowedPath(source)) continue;
                    foreach (var file in EnumerateFilesSafe(source, location.ExcludePatterns ?? Array.Empty<string>()))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var relative = Path.GetRelativePath(source, file);
                        var archivePath = $"loc-{locationIndex}/{relative.Replace('\\', '/')}";
                        var entry = archive.CreateEntry(archivePath, CompressionLevel.Fastest);
                        await using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, true))
                        await using (var output = entry.Open())
                            await input.CopyToAsync(output, cancellationToken);
                        var info = new FileInfo(file);
                        entries.Add(new FileSnapshotEntry($"{locationIndex}|{relative}", info.Length, info.LastWriteTimeUtc, await Sha256Async(file, cancellationToken)));
                    }
                }
            }

            var manifest = new SnapshotManifest(snapshotId, profile.Id, DateTimeOffset.UtcNow, Environment.MachineName, entries, profile.SaveLocations.SelectMany(x => x.RegistryKeys ?? Array.Empty<string>()).ToArray(), pinned, safety, note);
            await WriteAtomicAsync(Path.Combine(staging, "manifest.json"), manifest, cancellationToken);
            if (Directory.Exists(final)) Directory.Delete(final, true);
            Directory.Move(staging, final);
            await TrimSnapshotsAsync(profile.Id, cancellationToken);
            return manifest;
        }
        finally { _mutex.Release(); }
    }

    public async Task<IReadOnlyList<SnapshotManifest>> ListSnapshotsAsync(string gameId, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(GameDirectory(gameId), "snapshots");
        if (!Directory.Exists(directory)) return Array.Empty<SnapshotManifest>();
        var result = new List<SnapshotManifest>();
        foreach (var file in Directory.EnumerateFiles(directory, "manifest.json", SearchOption.AllDirectories))
        {
            try
            {
                var item = await ReadAsync<SnapshotManifest>(file, cancellationToken);
                if (item is not null) result.Add(item);
            }
            catch { }
        }
        return result.OrderByDescending(x => x.CreatedAt).ToArray();
    }

    public async Task RestoreSnapshotAsync(GameProfile profile, SnapshotManifest snapshot, CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var snapshotDirectory = Path.Combine(GameDirectory(profile.Id), "snapshots", snapshot.SnapshotId);
            var zipPath = Path.Combine(snapshotDirectory, "files.zip");
            if (!File.Exists(zipPath)) throw new FileNotFoundException("快照文件不存在", zipPath);
            var temp = Path.Combine(Path.GetTempPath(), "AiGameSave", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                ZipFile.ExtractToDirectory(zipPath, temp, true);
                foreach (var entry in Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(temp, entry).Replace('\\', '/');
                    var slash = relative.IndexOf('/');
                    if (slash < 5 || !relative.StartsWith("loc-", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!int.TryParse(relative[4..slash], out var index) || index < 0 || index >= profile.SaveLocations.Count) continue;
                    var targetRoot = _resolver.ResolveExisting(_resolver.Resolve(profile.SaveLocations[index].PathTemplate, profile));
                    if (!_resolver.IsAllowedPath(targetRoot)) throw new InvalidOperationException("存档规则解析到了不允许的目录");
                    var target = Path.GetFullPath(Path.Combine(targetRoot, relative[(slash + 1)..]));
                    if (!PathTemplateResolver.IsSubPath(target, targetRoot)) throw new InvalidOperationException("快照包含越界路径");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(entry, target, true);
                }
            }
            finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
        }
        finally { _mutex.Release(); }
    }

    private string GameDirectory(string id) => Path.Combine(RootPath, "games", id);

    private async Task TrimSnapshotsAsync(string gameId, CancellationToken cancellationToken)
    {
        var snapshots = (await ListSnapshotsAsync(gameId, cancellationToken)).Where(x => !x.Pinned && !x.Safety).OrderByDescending(x => x.CreatedAt).ToArray();
        foreach (var old in snapshots.Skip(10))
        {
            var directory = Path.Combine(GameDirectory(gameId), "snapshots", old.SnapshotId);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, IReadOnlyList<string> excludes)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories); } catch { yield break; }
        foreach (var file in files)
        {
            var lower = file.ToLowerInvariant();
            if (excludes.Any(x => lower.Contains(x.ToLowerInvariant()))) continue;
            if (new FileInfo(file).Length > 2L * 1024 * 1024 * 1024) continue;
            yield return file;
        }
    }

    private static async Task<string> Sha256Async(string file, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(file);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return default;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, _json, cancellationToken);
    }

    private async Task WriteAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await using (var stream = File.Create(temp)) await JsonSerializer.SerializeAsync(stream, value, _json, cancellationToken);
        if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
    }
}
