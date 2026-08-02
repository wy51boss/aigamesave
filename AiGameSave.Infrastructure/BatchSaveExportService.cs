using System.Text.Json;
using System.Text.Json.Serialization;
using AiGameSave.Core;

namespace AiGameSave.Infrastructure;

public sealed class BatchSaveExportService : IBatchSaveExportService
{
    private static readonly HashSet<string> SaveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".save", ".sav", ".rpgsave", ".rmmzsave", ".rvdata", ".rvdata2", ".rxdata", ".sol", ".lsd", ".dat", ".bin", ".json", ".db", ".profile"
    };

    private readonly IBatchGameScanService _scanner;

    public BatchSaveExportService(IBatchGameScanService? scanner = null) => _scanner = scanner ?? new BatchGameScanService();

    public async Task<BatchSaveExportReport> ExportAsync(string rootPath, string outputPath, CancellationToken cancellationToken = default)
    {
        rootPath = Path.GetFullPath(rootPath);
        outputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(outputPath);
        await ClearPreviousExportAsync(outputPath, cancellationToken);
        var scans = await _scanner.ScanAsync(rootPath, cancellationToken);
        var exports = new List<SaveExportItem>();

        foreach (var item in scans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actual = item.Candidates
                .Where(candidate => Directory.Exists(candidate.ResolvedPath))
                .GroupBy(candidate => Path.GetFullPath(candidate.ResolvedPath).TrimEnd(Path.DirectorySeparatorChar), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            if (actual.Length == 0)
            {
                exports.Add(new SaveExportItem(item.Name, "失败", null, null, 0, item.Status));
                continue;
            }

            var gameOutput = Path.Combine(outputPath, SafeName(item.Name));
            var copied = 0;
            string? firstSource = null;
            string? firstDestination = null;
            var sourcePaths = new List<string>();
            var exportPaths = new List<string>();
            foreach (var candidate in actual)
            {
                var source = Path.GetFullPath(candidate.ResolvedPath).TrimEnd(Path.DirectorySeparatorChar);
                var files = EnumerateFiles(source, item.ExecutablePath, candidate.ExcludePatterns).ToArray();
                if (files.Length == 0) continue;
                var destination = Path.Combine(gameOutput, $"candidate-{actual.ToList().IndexOf(candidate) + 1}");
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(source, file);
                    var target = Path.GetFullPath(Path.Combine(destination, relative));
                    if (!PathTemplateResolver.IsSubPath(target, destination)) throw new InvalidDataException("检测到越界文件路径。");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, true);
                    copied++;
                }
                firstSource ??= source;
                firstDestination ??= destination;
                sourcePaths.Add(source);
                exportPaths.Add(destination);
            }

            exports.Add(copied == 0
                ? new SaveExportItem(item.Name, "失败", firstSource, firstDestination, 0, "候选目录存在，但没有可导出的存档文件。")
                : new SaveExportItem(item.Name, "成功", firstSource, firstDestination, copied, "由软件静态检测候选并复制；未调用 AI、网页搜索或专用规则。", sourcePaths, exportPaths));
        }

        var report = new BatchSaveExportReport(DateTimeOffset.UtcNow, rootPath, outputPath, exports);
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        await File.WriteAllTextAsync(Path.Combine(outputPath, "scan-export-report.json"), JsonSerializer.Serialize(report, options), cancellationToken);
        return report;
    }

    private static async Task ClearPreviousExportAsync(string outputPath, CancellationToken cancellationToken)
    {
        var reportPath = Path.Combine(outputPath, "scan-export-report.json");
        if (!File.Exists(reportPath)) return;
        try
        {
            var options = new JsonSerializerOptions();
            var previous = JsonSerializer.Deserialize<BatchSaveExportReport>(await File.ReadAllTextAsync(reportPath, cancellationToken), options);
            if (previous is not null && Path.GetFullPath(previous.OutputPath).Equals(outputPath, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var gameName in previous.Items.Select(item => SafeName(item.GameName)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var gameOutput = Path.GetFullPath(Path.Combine(outputPath, gameName));
                    if (PathTemplateResolver.IsSubPath(gameOutput, outputPath) && Directory.Exists(gameOutput)) Directory.Delete(gameOutput, true);
                }
            }
        }
        catch (JsonException) { }
        File.Delete(reportPath);
    }

    private static IEnumerable<string> EnumerateFiles(string source, string? executablePath, IReadOnlyList<string> excludes)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories); }
        catch { yield break; }
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(source, file);
            if (excludes.Any(pattern => IsExcluded(relative, pattern))) continue;
            if (!LooksLikeSave(file, relative)) continue;
            yield return file;
        }
    }

    private static bool LooksLikeSave(string file, string relative)
    {
        var extension = Path.GetExtension(file);
        var name = Path.GetFileNameWithoutExtension(file);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return SaveExtensions.Contains(extension)
            || name.Contains("save", StringComparison.OrdinalIgnoreCase)
            || name.Contains("slot", StringComparison.OrdinalIgnoreCase)
            || name.Contains("profile", StringComparison.OrdinalIgnoreCase)
            || segments.Any(segment => segment.Equals("save", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("saves", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("savedata", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("save_data", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsExcluded(string relative, string pattern)
    {
        pattern = pattern.Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar);
        return relative.Equals(pattern, StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith(pattern + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || relative.Split(Path.DirectorySeparatorChar).Any(segment => segment.Equals(pattern, StringComparison.OrdinalIgnoreCase));
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "unnamed-game" : safe;
    }
}
