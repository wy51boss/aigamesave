using AiGameSave.Core;

namespace AiGameSave.Infrastructure;

public sealed class BatchGameScanService : IBatchGameScanService
{
    private readonly IEngineDetectionService _engineDetection;
    private static readonly string[] ExcludedPathSegments = { "node_modules", "\\lib\\", "\\Tool\\", "\\ReiPatcher\\", "\\BepInEx\\", "\\Managed\\" };

    public BatchGameScanService(IEngineDetectionService? engineDetection = null) => _engineDetection = engineDetection ?? new EngineDetectionService();

    public async Task<IReadOnlyList<BatchGameScanItem>> ScanAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        rootPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(rootPath)) throw new DirectoryNotFoundException(rootPath);
        var result = new List<BatchGameScanItem>();
        foreach (var directory in Directory.EnumerateDirectories(rootPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var executable = FindMainExecutable(directory);
            if (executable is null)
            {
                result.Add(new BatchGameScanItem(Path.GetFileName(directory), directory, null, GameEngineKind.Unknown, Array.Empty<CandidateLocation>(), "未找到主游戏 EXE"));
                continue;
            }
            var actualRoot = Path.GetDirectoryName(executable)!;
            var detection = await _engineDetection.DetectAsync(actualRoot, executable, cancellationToken);
            var existingCandidates = detection.Candidates.Count(candidate => Directory.Exists(candidate.ResolvedPath) || File.Exists(candidate.ResolvedPath));
            var status = existingCandidates > 0
                ? $"发现实际存档候选 ({existingCandidates})"
                : detection.Candidates.Count > 0
                    ? "已识别引擎，仅有推测路径"
                    : detection.Engine == GameEngineKind.Unknown
                        ? "未知引擎，需要行为检测"
                        : "已识别引擎，尚未发现存档";
            result.Add(new BatchGameScanItem(Path.GetFileName(directory), directory, executable, detection.Engine, detection.Candidates, status));
        }
        return result;
    }

    private static string? FindMainExecutable(string root)
    {
        IEnumerable<string> executables;
        try { executables = Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories); } catch { return null; }
        return executables
            .Where(path => !ExcludedPathSegments.Any(segment => path.Contains(segment, StringComparison.OrdinalIgnoreCase)))
            .Where(path => !Path.GetFileName(path).Contains("UnityCrashHandler", StringComparison.OrdinalIgnoreCase))
            .Where(path => !Path.GetFileName(path).Contains("notification_helper", StringComparison.OrdinalIgnoreCase))
            .Where(path => !Path.GetFileName(path).Contains("unins", StringComparison.OrdinalIgnoreCase))
            .Select(path => new { Path = path, Score = ScoreExecutable(path) })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Path.Count(ch => ch == Path.DirectorySeparatorChar))
            .Select(x => x.Path)
            .FirstOrDefault();
    }

    private static int ScoreExecutable(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var score = 0;
        if (Directory.Exists(Path.Combine(directory, stem + "_Data"))) score += 120;
        if (Directory.Exists(Path.Combine(directory, "renpy")) && Directory.Exists(Path.Combine(directory, "game"))) score += 110;
        if (Directory.Exists(Path.Combine(directory, "www"))) score += 100;
        if (Path.GetFileName(path).Contains("Shipping", StringComparison.OrdinalIgnoreCase)) score += 110;
        if (File.Exists(Path.Combine(directory, "data.win"))) score += 100;
        if (File.Exists(Path.Combine(directory, stem + ".pck"))) score += 100;
        if (Path.GetFileName(path).Equals("Game.exe", StringComparison.OrdinalIgnoreCase)) score += 30;
        try { score += (int)Math.Min(20, new FileInfo(path).Length / 1_000_000); } catch { }
        return score;
    }
}
