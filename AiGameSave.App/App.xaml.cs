using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using AiGameSave.Infrastructure;

namespace AiGameSave.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var webIndex = Array.FindIndex(e.Args, argument => argument.Equals("--web-research-failed", StringComparison.OrdinalIgnoreCase));
        if (webIndex >= 0)
        {
            try
            {
                if (webIndex + 2 >= e.Args.Length) throw new ArgumentException("--web-research-failed 后必须提供游戏目录和报告路径。");
                var root = Path.GetFullPath(e.Args[webIndex + 1]);
                var reportPath = Path.GetFullPath(e.Args[webIndex + 2]);
                var scanned = await new BatchGameScanService().ScanAsync(root);
                var client = new WebResearchClient();
                var items = new List<object>();
                foreach (var item in scanned.Where(item => !item.Status.StartsWith("发现实际", StringComparison.Ordinal)))
                {
                    var queryList = new List<string> { $"\"{item.Name}\" 存档位置", $"\"{item.Name}\" save location Windows" };
                    var executableStem = string.IsNullOrWhiteSpace(item.ExecutablePath) ? null : Path.GetFileNameWithoutExtension(item.ExecutablePath);
                    queryList.Insert(0, !string.IsNullOrWhiteSpace(executableStem) && IsDistinctiveExecutableStem(executableStem)
                        ? $"\"{executableStem}\" game save location Windows"
                        : queryList[1]);
                    var queries = queryList.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    var results = new List<WebSearchItem>();
                    foreach (var query in queries) results.AddRange(await client.SearchAsync(query));
                    var unique = results.GroupBy(result => result.Url, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).Take(8).ToArray();
                    var sources = new List<object>();
                    foreach (var result in unique)
                    {
                        var searchable = $"{result.Title} {result.Snippet}";
                        var identity = BuildIdentity(item.Name);
                        var exactTitleMatch = identity.Length >= 5 && searchable.Contains(identity, StringComparison.OrdinalIgnoreCase);
                        var identityTokens = item.Name.Split(new[] { ' ', '_', '-', '【', '】', '！', '：', '～', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(BuildIdentity)
                            .Where(token => token.Length >= 4 && !GenericSearchToken(token))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                        var matchingTokens = identityTokens.Count(token => searchable.Contains(token, StringComparison.OrdinalIgnoreCase));
                        var executableMatch = IsDistinctiveExecutableStem(executableStem) && searchable.Contains(executableStem!, StringComparison.OrdinalIgnoreCase);
                        var saveMatch = ContainsAny(searchable, "save", "savedata", "save data", "存档", "セーブ", "saved game", "save location");
                        var pathMatch = ContainsAny(searchable, "appdata", "local low", "documents", "saved games", "userdata", "save folder", "save directory", "\n%userprofile%");
                        var identityMatch = exactTitleMatch || matchingTokens >= 2 || executableMatch;
                        var relevanceScore = (identityMatch ? 50 : 0) + (saveMatch ? 25 : 0) + (pathMatch ? 15 : 0) + (result.Source == "PCGamingWiki" ? 10 : 0);
                        sources.Add(new { result.Title, result.Url, result.Source, result.Snippet, relevanceScore, likelyRelevant = identityMatch && saveMatch && relevanceScore >= 75 });
                    }
                    items.Add(new { item.Name, item.Engine, item.ExecutablePath, item.Status, queries, results = sources });
                }
                var report = new
                {
                    generatedAt = DateTimeOffset.UtcNow,
                    rootPath = root,
                    detectionMode = "failed-items-web-research",
                    usedAi = false,
                    usedWebSearch = true,
                    usedGameSpecificRules = false,
                    items
                };
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                var options = new JsonSerializerOptions { WriteIndented = true };
                options.Converters.Add(new JsonStringEnumConverter());
                await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, options));
                Shutdown(0);
            }
            catch
            {
                Shutdown(1);
            }
            return;
        }
        var exportIndex = Array.FindIndex(e.Args, argument => argument.Equals("--batch-export-saves", StringComparison.OrdinalIgnoreCase));
        if (exportIndex >= 0)
        {
            try
            {
                if (exportIndex + 2 >= e.Args.Length) throw new ArgumentException("--batch-export-saves 后必须提供游戏目录和导出目录。");
                var root = Path.GetFullPath(e.Args[exportIndex + 1]);
                var output = Path.GetFullPath(e.Args[exportIndex + 2]);
                await new BatchSaveExportService().ExportAsync(root, output);
                Shutdown(0);
            }
            catch
            {
                Shutdown(1);
            }
            return;
        }
        var scanIndex = Array.FindIndex(e.Args, argument => argument.Equals("--batch-scan", StringComparison.OrdinalIgnoreCase));
        if (scanIndex >= 0)
        {
            try
            {
                if (scanIndex + 1 >= e.Args.Length) throw new ArgumentException("--batch-scan 后必须提供游戏目录。");
                var root = Path.GetFullPath(e.Args[scanIndex + 1]);
                var reportIndex = Array.FindIndex(e.Args, argument => argument.Equals("--report", StringComparison.OrdinalIgnoreCase));
                var reportPath = reportIndex >= 0 && reportIndex + 1 < e.Args.Length
                    ? Path.GetFullPath(e.Args[reportIndex + 1])
                    : Path.Combine(AppContext.BaseDirectory, "batch-scan-report.json");
                var items = await new BatchGameScanService().ScanAsync(root);
                var report = new
                {
                    generatedAt = DateTimeOffset.UtcNow,
                    rootPath = root,
                    detectionMode = "generic-static-local-only",
                    usedAi = false,
                    usedWebSearch = false,
                    usedGameSpecificRules = false,
                    items
                };
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                var options = new JsonSerializerOptions { WriteIndented = true };
                options.Converters.Add(new JsonStringEnumConverter());
                await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, options));
                Shutdown(0);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "批量扫描失败", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    private static bool IsDistinctiveExecutableStem(string? stem)
    {
        if (string.IsNullOrWhiteSpace(stem) || stem.Length < 5) return false;
        var normalized = stem.Replace("-", string.Empty).Replace("_", string.Empty).ToLowerInvariant();
        return normalized is not ("game" or "secret" or "clientwin64shipping" or "unitycrashhandler64" or "mumu模拟器去广告优化版");
    }

    private static string BuildIdentity(string value) => new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static bool GenericSearchToken(string token) => token is "game" or "save" or "windows" or "pc" or "the" or "hot" or "jack" or "secret";

    private static bool ContainsAny(string value, params string[] terms) => terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
}
