using System.Text.RegularExpressions;
using AiGameSave.Core;

namespace AiGameSave.Infrastructure;

public sealed class PathTemplateResolver : IPathTemplateResolver
{
    public string Resolve(string template, GameProfile? game = null)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["%USERPROFILE%"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ["%DOCUMENTS%"] = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ["%SAVEDGAMES%"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games"),
            ["%APPDATA%"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ["%LOCALAPPDATA%"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ["%PROGRAMDATA%"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ["%STEAM_ROOT%"] = FindSteamRoot(),
            ["%STEAM_USERDATA%"] = FindSteamUserData()
        };

        if (game is not null)
        {
            values["%GAME_DIR%"] = string.IsNullOrWhiteSpace(game.ExecutablePath)
                ? string.Empty
                : Path.GetDirectoryName(game.ExecutablePath) ?? string.Empty;
            values["%APP_ID%"] = game.AppId ?? string.Empty;
        }

        var result = template;
        foreach (var pair in values)
            result = result.Replace(pair.Key, pair.Value, StringComparison.OrdinalIgnoreCase);

        result = Regex.Replace(result, @"\{[^}]+\}", "*");
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(result));
    }

    public bool IsAllowedPath(string resolvedPath, IReadOnlyCollection<string>? extraRoots = null)
    {
        try
        {
            var full = Path.GetFullPath(resolvedPath).TrimEnd(Path.DirectorySeparatorChar);
            var windows = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).TrimEnd(Path.DirectorySeparatorChar);
            var system = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.System)).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Equals(windows, StringComparison.OrdinalIgnoreCase) || full.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            if (full.Equals(system, StringComparison.OrdinalIgnoreCase) || full.StartsWith(system + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            if (Path.GetPathRoot(full)?.Equals(full, StringComparison.OrdinalIgnoreCase) == true) return false;
            return extraRoots is null || extraRoots.Count == 0 || extraRoots.Any(root => IsSubPath(full, Path.GetFullPath(root)));
        }
        catch { return false; }
    }

    public string ResolveExisting(string path)
    {
        if (!path.Contains('*') && !path.Contains('?')) return path;
        var root = Path.GetPathRoot(path) ?? throw new InvalidOperationException("通配路径缺少盘符");
        var parts = path[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = new[] { root };
        foreach (var part in parts)
        {
            var next = new List<string>();
            foreach (var parent in current)
            {
                try
                {
                    if (part.Contains('*') || part.Contains('?')) next.AddRange(Directory.EnumerateDirectories(parent, part, SearchOption.TopDirectoryOnly));
                    else
                    {
                        var child = Path.Combine(parent, part);
                        if (Directory.Exists(child)) next.Add(child);
                    }
                }
                catch { }
            }
            if (next.Count == 0) throw new DirectoryNotFoundException($"无法解析存档通配路径：{path}");
            current = next.ToArray();
        }
        return current[0];
    }

    public static bool IsSubPath(string candidate, string root)
    {
        candidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindSteamRoot()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidate = Path.Combine(programFiles, "Steam");
        return Directory.Exists(candidate) ? candidate : string.Empty;
    }

    private static string FindSteamUserData()
    {
        var root = FindSteamRoot();
        var path = string.IsNullOrEmpty(root) ? string.Empty : Path.Combine(root, "userdata");
        return Directory.Exists(path) ? path : string.Empty;
    }
}
