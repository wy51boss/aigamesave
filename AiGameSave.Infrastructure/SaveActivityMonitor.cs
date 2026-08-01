using System.Collections.Concurrent;
using AiGameSave.Core;

namespace AiGameSave.Infrastructure;

public sealed class SaveActivityMonitor : IDisposable
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _events = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Length, DateTime LastWrite)> _baseline = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = new();
    private bool _started;

    public void Start(IEnumerable<string> roots, IEnumerable<string> candidateRoots)
    {
        Dispose();
        _events.Clear();
        _baseline.Clear();
        foreach (var root in candidateRoots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            Capture(root, _baseline, 50_000);

        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                    EnableRaisingEvents = true
                };
                watcher.Changed += OnChange;
                watcher.Created += OnChange;
                watcher.Deleted += OnChange;
                watcher.Renamed += OnRename;
                _watchers.Add(watcher);
            }
            catch { }
        }
        _started = true;
    }

    public async Task<IReadOnlyList<string>> CompleteAsync(IEnumerable<string> candidateRoots, CancellationToken cancellationToken = default)
    {
        if (!_started) throw new InvalidOperationException("请先开始行为检测。");
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        foreach (var watcher in _watchers) watcher.EnableRaisingEvents = false;
        var after = new Dictionary<string, (long Length, DateTime LastWrite)>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in candidateRoots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase)) Capture(root, after, 50_000);
        foreach (var item in after)
            if (!_baseline.TryGetValue(item.Key, out var before) || before != item.Value) _events[item.Key] = DateTimeOffset.UtcNow;
        return _events.Keys.Where(path => !SavePathDefaults.Excludes.Any(x => path.Contains(Path.DirectorySeparatorChar + x + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        _started = false;
    }

    private void OnChange(object sender, FileSystemEventArgs e) => _events[e.FullPath] = DateTimeOffset.UtcNow;
    private void OnRename(object sender, RenamedEventArgs e)
    {
        _events[e.OldFullPath] = DateTimeOffset.UtcNow;
        _events[e.FullPath] = DateTimeOffset.UtcNow;
    }

    private static void Capture(string root, IDictionary<string, (long Length, DateTime LastWrite)> target, int limit)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Take(limit))
            {
                try
                {
                    var info = new FileInfo(file);
                    target[file] = (info.Length, info.LastWriteTimeUtc);
                }
                catch { }
            }
        }
        catch { }
    }
}
