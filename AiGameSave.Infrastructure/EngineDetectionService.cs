using System.Text.RegularExpressions;
using AiGameSave.Core;

namespace AiGameSave.Infrastructure;

public sealed class EngineDetectionService : IEngineDetectionService
{
    private static readonly HashSet<string> SaveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".save", ".sav", ".rpgsave", ".rmmzsave", ".rvdata", ".rvdata2", ".rxdata", ".sol", ".lsd"
    };

    public Task<EngineDetectionResult> DetectAsync(string gameRoot, string? executablePath, CancellationToken cancellationToken = default)
    {
        gameRoot = Path.GetFullPath(gameRoot);
        var candidates = new Dictionary<string, CandidateLocation>(StringComparer.OrdinalIgnoreCase);
        var engineEvidence = new List<Evidence>();
        var engine = GameEngineKind.Unknown;

        var appInfo = FindFile(gameRoot, "app.info", path => path.Contains("_Data", StringComparison.OrdinalIgnoreCase));
        if (appInfo is not null && TryReadUnityIdentity(appInfo, out var company, out var product))
        {
            engine = GameEngineKind.Unity;
            engineEvidence.Add(new Evidence("engine-unity", "从 *_Data/app.info 识别为 Unity", 40));
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", company, product);
            Add(candidates, Candidate(path, Directory.Exists(path) ? 85 : 55, "Unity app.info 推导 LocalLow 路径", "engine-unity-path", $"%USERPROFILE%\\AppData\\LocalLow\\{company}\\{product}"));
        }
        else if (IsRenPy(gameRoot))
        {
            engine = GameEngineKind.RenPy;
            engineEvidence.Add(new Evidence("engine-renpy", "检测到 Ren'Py 的 renpy/game 结构或 .rpyc 文件", 40));
            var internalSaves = Path.Combine(gameRoot, "game", "saves");
            Add(candidates, Candidate(internalSaves, Directory.Exists(internalSaves) ? 90 : 55, "Ren'Py game/saves", "engine-renpy-path", "%GAME_DIR%\\game\\saves"));
            var saveDirectory = ReadRenPySaveDirectory(gameRoot);
            if (!string.IsNullOrWhiteSpace(saveDirectory))
            {
                var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RenPy", saveDirectory);
                Add(candidates, Candidate(roaming, Directory.Exists(roaming) ? 90 : 60, "Ren'Py config.save_directory", "engine-renpy-config", $"%APPDATA%\\RenPy\\{saveDirectory}"));
            }
        }
        else if (IsRpgMakerMvMz(gameRoot))
        {
            engine = GameEngineKind.RpgMakerMvMz;
            engineEvidence.Add(new Evidence("engine-rpgmaker-mv", "检测到 RPG Maker MV/MZ 的 www/data 或 rpg_core 结构", 40));
            var save = Directory.Exists(Path.Combine(gameRoot, "www")) ? Path.Combine(gameRoot, "www", "save") : Path.Combine(gameRoot, "save");
            var relativeSave = Path.GetRelativePath(gameRoot, save);
            Add(candidates, Candidate(save, Directory.Exists(save) ? 95 : 65, "RPG Maker MV/MZ save 目录", "engine-rpgmaker-path", $"%GAME_DIR%\\{relativeSave}"));
        }
        else if (IsRpgMakerLegacy(gameRoot))
        {
            engine = GameEngineKind.RpgMakerLegacy;
            engineEvidence.Add(new Evidence("engine-rpgmaker-legacy", "检测到 RPG Maker XP/VX/VX Ace 数据文件", 40));
            Add(candidates, Candidate(gameRoot, HasLegacySave(gameRoot) ? 90 : 55, "旧版 RPG Maker 通常在游戏根目录保存 Save 文件", "engine-rpgmaker-legacy-path", "%GAME_DIR%"));
        }
        else if (IsUnreal(gameRoot, executablePath))
        {
            engine = GameEngineKind.Unreal;
            engineEvidence.Add(new Evidence("engine-unreal", "检测到 Unreal Engine/Shipping 结构", 40));
            var project = GetUnrealProjectName(gameRoot, executablePath);
            var save = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), project, "Saved", "SaveGames");
            Add(candidates, Candidate(save, Directory.Exists(save) ? 90 : 50, "Unreal Saved/SaveGames 约定路径", "engine-unreal-path", $"%LOCALAPPDATA%\\{project}\\Saved\\SaveGames"));
        }
        else if (HasFile(gameRoot, "*.pck"))
        {
            engine = GameEngineKind.Godot;
            engineEvidence.Add(new Evidence("engine-godot", "检测到 Godot .pck 文件", 40));
            var gameProduct = Path.GetFileNameWithoutExtension(executablePath) ?? Path.GetFileName(gameRoot);
            var save = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Godot", "app_userdata", gameProduct);
            Add(candidates, Candidate(save, Directory.Exists(save) ? 85 : 45, "Godot user:// 默认目录推导", "engine-godot-path", $"%APPDATA%\\Godot\\app_userdata\\{gameProduct}"));
        }
        else if (File.Exists(Path.Combine(gameRoot, "data.win")))
        {
            engine = GameEngineKind.GameMaker;
            engineEvidence.Add(new Evidence("engine-gamemaker", "检测到 GameMaker data.win", 40));
            var gameProduct = Path.GetFileNameWithoutExtension(executablePath) ?? Path.GetFileName(gameRoot);
            var save = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), gameProduct);
            Add(candidates, Candidate(save, Directory.Exists(save) ? 80 : 40, "GameMaker LocalAppData 常见路径推导", "engine-gamemaker-path", $"%LOCALAPPDATA%\\{gameProduct}"));
        }
        else if (HasWolfSignature(gameRoot))
        {
            engine = GameEngineKind.WolfRpg;
            engineEvidence.Add(new Evidence("engine-wolf", "检测到 Wolf RPG 数据结构", 40));
            var save = Path.Combine(gameRoot, "Save");
            Add(candidates, Candidate(Directory.Exists(save) ? save : gameRoot, Directory.Exists(save) ? 85 : 50, "Wolf RPG 游戏目录存档候选", "engine-wolf-path", Directory.Exists(save) ? "%GAME_DIR%\\Save" : "%GAME_DIR%"));
        }
        else if (File.Exists(Path.Combine(gameRoot, "package.json")))
        {
            engine = GameEngineKind.NwJs;
            engineEvidence.Add(new Evidence("engine-nwjs", "检测到 NW.js package.json", 35));
        }

        foreach (var group in FindExistingSaveFiles(gameRoot, cancellationToken).GroupBy(Path.GetDirectoryName, StringComparer.OrdinalIgnoreCase).Where(x => x.Key is not null).OrderByDescending(x => x.Count()).Take(10))
            Add(candidates, Candidate(group.Key!, 90, $"目录内存在 {group.Count()} 个通用存档文件", "existing-save-files", ToPortableTemplate(group.Key!, gameRoot)));

        return Task.FromResult(new EngineDetectionResult(engine, candidates.Values.OrderByDescending(x => x.Score).ToArray(), engineEvidence));
    }

    private static CandidateLocation Candidate(string path, int score, string description, string type, string? pathTemplate = null)
    {
        path = Path.GetFullPath(path);
        var confidence = score >= 80 ? CandidateConfidence.High : score >= 40 ? CandidateConfidence.Possible : CandidateConfidence.Low;
        return new CandidateLocation(pathTemplate ?? path, path, "save", score, confidence, new[] { new Evidence(type, description, score) }, Array.Empty<string>(), SavePathDefaults.Excludes);
    }

    private static string ToPortableTemplate(string path, string gameRoot)
    {
        path = Path.GetFullPath(path);
        if (PathTemplateResolver.IsSubPath(path, gameRoot))
        {
            var relative = Path.GetRelativePath(gameRoot, path);
            return relative == "." ? "%GAME_DIR%" : $"%GAME_DIR%\\{relative}";
        }
        var roots = new (string Path, string Token)[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%")
        };
        foreach (var root in roots.Where(item => !string.IsNullOrWhiteSpace(item.Path)).OrderByDescending(item => item.Path.Length))
            if (PathTemplateResolver.IsSubPath(path, root.Path))
                return root.Token + "\\" + Path.GetRelativePath(root.Path, path);
        return path;
    }

    private static void Add(IDictionary<string, CandidateLocation> result, CandidateLocation candidate)
    {
        var key = candidate.ResolvedPath.TrimEnd(Path.DirectorySeparatorChar);
        if (!result.TryGetValue(key, out var current) || candidate.Score > current.Score) result[key] = candidate;
    }

    private static bool TryReadUnityIdentity(string appInfo, out string company, out string product)
    {
        company = product = string.Empty;
        try
        {
            var lines = File.ReadAllLines(appInfo).Select(x => x.Trim()).Where(x => x.Length > 0).Take(2).ToArray();
            if (lines.Length != 2 || lines.Any(x => x.Length > 120 || x.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) return false;
            company = lines[0]; product = lines[1]; return true;
        }
        catch { return false; }
    }

    private static bool IsRenPy(string root) => (Directory.Exists(Path.Combine(root, "renpy")) && Directory.Exists(Path.Combine(root, "game"))) || FindFile(root, "*.rpyc") is not null;

    private static string? ReadRenPySaveDirectory(string root)
    {
        try
        {
            var options = FindFile(Path.Combine(root, "game"), "options.rpy");
            if (options is null) return null;
            var match = Regex.Match(File.ReadAllText(options), "config\\.save_directory\\s*=\\s*['\"]([^'\"]+)['\"]", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }
        catch { return null; }
    }

    private static bool IsRpgMakerMvMz(string root) => File.Exists(Path.Combine(root, "www", "data", "System.json")) || File.Exists(Path.Combine(root, "www", "js", "rpg_core.js")) || Directory.Exists(Path.Combine(root, "www", "save"));
    private static bool IsRpgMakerLegacy(string root) => Directory.Exists(Path.Combine(root, "Data")) && Directory.EnumerateFiles(Path.Combine(root, "Data"), "*.*", SearchOption.TopDirectoryOnly).Any(x => Path.GetExtension(x) is ".rvdata" or ".rvdata2" or ".rxdata");
    private static bool HasLegacySave(string root) => Directory.EnumerateFiles(root, "Save*.*", SearchOption.TopDirectoryOnly).Any(x => SaveExtensions.Contains(Path.GetExtension(x)));
    private static bool IsUnreal(string root, string? exe) => Directory.Exists(Path.Combine(root, "Engine")) || (exe?.Contains("Shipping", StringComparison.OrdinalIgnoreCase) ?? false) || Directory.EnumerateDirectories(root, "Engine", SearchOption.AllDirectories).Take(1).Any();
    private static string GetUnrealProjectName(string root, string? exe) => Regex.Replace(Path.GetFileNameWithoutExtension(exe) ?? Path.GetFileName(root), "-(Win64|Windows)-Shipping$", string.Empty, RegexOptions.IgnoreCase);
    private static bool HasWolfSignature(string root) => HasFile(root, "*.wolf") || File.Exists(Path.Combine(root, "Data", "BasicData", "Game.dat"));
    private static bool HasFile(string root, string pattern) => FindFile(root, pattern) is not null;

    private static string? FindFile(string root, string pattern, Func<string, bool>? predicate = null)
    {
        if (!Directory.Exists(root)) return null;
        try { return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).FirstOrDefault(path => predicate?.Invoke(path) ?? true); }
        catch { return null; }
    }

    private static IEnumerable<string> FindExistingSaveFiles(string root, CancellationToken cancellationToken)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories); } catch { yield break; }
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file);
            if (relative.Contains("node_modules", StringComparison.OrdinalIgnoreCase) || relative.Contains("StreamingAssets", StringComparison.OrdinalIgnoreCase) || relative.Contains($"Tool{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) continue;
            var extension = Path.GetExtension(file).ToLowerInvariant();
            var directoryName = new DirectoryInfo(Path.GetDirectoryName(file)!).Name;
            var legacyRpgSave = extension is ".rvdata" or ".rvdata2" or ".rxdata";
            if (legacyRpgSave && !Path.GetFileNameWithoutExtension(file).StartsWith("Save", StringComparison.OrdinalIgnoreCase)) continue;
            if (SaveExtensions.Contains(extension) || directoryName.Equals("save", StringComparison.OrdinalIgnoreCase) || directoryName.Equals("saves", StringComparison.OrdinalIgnoreCase)) yield return file;
        }
    }
}
