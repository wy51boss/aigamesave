using System.Diagnostics;
using AiGameSave.Core;

namespace AiGameSave.Infrastructure;

public sealed class LocalDetectionService : ILocalDetectionService
{
    private readonly PathTemplateResolver _resolver = new();

    public async Task<IReadOnlyList<CandidateLocation>> ScanAsync(ResearchRequest request, IReadOnlyList<CandidateLocation> researchCandidates, CancellationToken cancellationToken = default)
    {
        var gameTokens = Tokens(request.GameName).ToArray();
        var candidates = new Dictionary<string, CandidateLocation>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in researchCandidates)
            Add(candidates, candidate);

        foreach (var rootTemplate in SavePathDefaults.Roots)
        {
            var root = _resolver.Resolve(rootTemplate);
            if (!Directory.Exists(root)) continue;
            foreach (var directory in EnumerateDirectories(root, 3, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(directory);
                if (!gameTokens.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase))) continue;
                var evidence = new List<Evidence> { new("local-directory", "经典存档目录中存在与游戏名称相关的目录", 20) };
                if (ContainsSaveLikeFiles(directory)) evidence.Add(new("save-files", "目录中存在疑似存档文件", 20));
                var score = evidence.Sum(x => x.Weight);
                Add(candidates, Build(directory, evidence, score));
            }
        }

        if (!string.IsNullOrWhiteSpace(request.ExecutablePath))
        {
            var gameDirectory = Path.GetDirectoryName(Path.GetFullPath(request.ExecutablePath));
            if (gameDirectory is not null && Directory.Exists(gameDirectory))
            {
                if (ContainsSaveLikeFiles(gameDirectory))
                    Add(candidates, Build(gameDirectory, new[] { new Evidence("game-directory", "游戏目录中存在疑似存档文件", 35) }, 35));

                var unityIdentity = ReadUnityIdentity(gameDirectory);
                if (unityIdentity is not null)
                {
                    var unityPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", unityIdentity.Value.Company, unityIdentity.Value.Product);
                    var evidence = new[] { new Evidence("unity-app-info", $"Unity app.info 识别到开发商 {unityIdentity.Value.Company} 和产品名 {unityIdentity.Value.Product}", 50) };
                    Add(candidates, Build(unityPath, evidence, Directory.Exists(unityPath) ? 75 : 50));
                }
            }
        }

        return await Task.FromResult(candidates.Values.OrderByDescending(x => x.Score).ToArray());
    }

    public async Task<IReadOnlyList<CandidateLocation>> VerifySaveWindowAsync(ResearchRequest request, IReadOnlyList<CandidateLocation> candidates, TimeSpan window, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - window;
        var result = new List<CandidateLocation>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = candidate.ResolvedPath;
            if (!Directory.Exists(path)) continue;
            var changed = EnumerateFiles(path, candidate.ExcludePatterns).Any(file =>
            {
                try { return File.GetLastWriteTimeUtc(file) >= cutoff; } catch { return false; }
            });
            if (!changed) { result.Add(candidate); continue; }
            var evidence = candidate.Evidence.Concat(new[] { new Evidence("write-window", "用户保存操作后目录内有近期文件变化", 40) }).ToArray();
            var score = Math.Min(100, candidate.Score + 40);
            result.Add(candidate with { Score = score, Confidence = ToConfidence(score, true), Evidence = evidence });
        }
        return await Task.FromResult(result.OrderByDescending(x => x.Score).ToArray());
    }

    public static CandidateConfidence ToConfidence(int score, bool behaviorEvidence = false) => behaviorEvidence && score >= 80 ? CandidateConfidence.Verified : score >= 70 ? CandidateConfidence.High : score >= 40 ? CandidateConfidence.Possible : CandidateConfidence.Low;

    public static IReadOnlyList<CandidateLocation> MergeActivityCandidates(IReadOnlyList<CandidateLocation> candidates, IReadOnlyList<string> changedPaths)
    {
        var result = candidates.ToDictionary(x => x.ResolvedPath.TrimEnd(Path.DirectorySeparatorChar), StringComparer.OrdinalIgnoreCase);
        foreach (var group in changedPaths.Where(File.Exists).GroupBy(path => Path.GetDirectoryName(path) ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            var existing = result.Values.FirstOrDefault(candidate => PathTemplateResolver.IsSubPath(group.Key, candidate.ResolvedPath));
            var saveLike = group.Any(file =>
            {
                var extension = Path.GetExtension(file).ToLowerInvariant();
                return extension is ".sav" or ".save" or ".dat" or ".bin" or ".json" or ".db" || Path.GetFileName(file).Contains("save", StringComparison.OrdinalIgnoreCase);
            });
            if (existing is not null)
            {
                var bonus = saveLike ? 50 : 40;
                var score = Math.Min(100, existing.Score + bonus);
                var evidence = existing.Evidence.Concat(new[] { new Evidence("write-behavior", saveLike ? "保存窗口内写入了疑似存档文件" : "保存窗口内该目录发生文件写入", bonus) }).ToArray();
                result[existing.ResolvedPath.TrimEnd(Path.DirectorySeparatorChar)] = existing with { Score = score, Confidence = ToConfidence(score, true), Evidence = evidence };
            }
            else
            {
                var score = saveLike ? 70 : 45;
                var evidence = new[] { new Evidence("write-behavior", saveLike ? "保存窗口内出现新的疑似存档文件" : "保存窗口内出现新的文件变化", score) };
                result[group.Key] = new CandidateLocation(group.Key, group.Key, "save", score, ToConfidence(score, true), evidence, Array.Empty<string>(), SavePathDefaults.Excludes);
            }
        }
        return result.Values.OrderByDescending(x => x.Score).ToArray();
    }

    private CandidateLocation Build(string path, IEnumerable<Evidence> evidence, int score)
    {
        var normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        return new CandidateLocation(normalized, normalized, "save", Math.Min(score, 100), ToConfidence(score), evidence.ToArray(), Array.Empty<string>(), SavePathDefaults.Excludes);
    }

    private static void Add(IDictionary<string, CandidateLocation> candidates, CandidateLocation candidate)
    {
        var key = candidate.ResolvedPath.TrimEnd(Path.DirectorySeparatorChar);
        if (!candidates.TryGetValue(key, out var existing) || candidate.Score > existing.Score) candidates[key] = candidate;
    }

    private static IEnumerable<string> EnumerateDirectories(string root, int maxDepth, CancellationToken token)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (current, depth) = queue.Dequeue();
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(current); } catch { continue; }
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (SavePathDefaults.Excludes.Any(x => name.Equals(x, StringComparison.OrdinalIgnoreCase))) continue;
                yield return child;
                if (depth + 1 < maxDepth) queue.Enqueue((child, depth + 1));
            }
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root, IReadOnlyList<string> excludes)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray(); }
        catch { return Array.Empty<string>(); }
        return files.Where(file =>
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            return !excludes.Any(pattern =>
            {
                pattern = pattern.Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar);
                return relative.Equals(pattern, StringComparison.OrdinalIgnoreCase)
                    || relative.StartsWith(pattern + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || relative.Split(Path.DirectorySeparatorChar).Any(segment => segment.Equals(pattern, StringComparison.OrdinalIgnoreCase));
            });
        });
    }

    private static bool ContainsSaveLikeFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).Any(file =>
            {
                var extension = Path.GetExtension(file).ToLowerInvariant();
                var name = Path.GetFileName(file).ToLowerInvariant();
                return new[] { ".sav", ".save", ".dat", ".bin", ".json", ".db", ".profile" }.Contains(extension) || name.Contains("save") || name.Contains("slot") || name.Contains("profile");
            });
        }
        catch { return false; }
    }

    private static IEnumerable<string> Tokens(string gameName)
    {
        yield return gameName.Trim();
        foreach (var token in gameName.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (token.Length >= 3) yield return token;
    }

    private static (string Company, string Product)? ReadUnityIdentity(string gameDirectory)
    {
        try
        {
            var exeName = Path.GetFileNameWithoutExtension(gameDirectory);
            var candidates = Directory.EnumerateFiles(gameDirectory, "app.info", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(gameDirectory, "app.info", SearchOption.AllDirectories).Take(1));
            var file = candidates.FirstOrDefault(path => path.Contains("_Data", StringComparison.OrdinalIgnoreCase));
            if (file is null) return null;
            var lines = File.ReadAllLines(file).Select(x => x.Trim()).Where(x => x.Length > 0).Take(2).ToArray();
            if (lines.Length < 2 || lines.Any(x => x.Length > 120 || x.Contains(Path.DirectorySeparatorChar))) return null;
            return (lines[0], lines[1]);
        }
        catch { return null; }
    }
}
