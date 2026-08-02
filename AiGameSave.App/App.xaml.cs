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
}
