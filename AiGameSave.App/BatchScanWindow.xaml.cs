using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using Microsoft.Win32;
using AiGameSave.Core;

namespace AiGameSave.App;

public partial class BatchScanWindow : Window
{
    private readonly string _rootPath;
    private readonly IReadOnlyList<BatchGameScanItem> _items;

    public BatchScanWindow(string rootPath, IReadOnlyList<BatchGameScanItem> items)
    {
        InitializeComponent();
        _rootPath = rootPath;
        _items = items;
        ResultsGrid.ItemsSource = items.Select(BatchScanRow.From).ToArray();
        var actual = items.Count(item => item.Status.StartsWith("发现实际", StringComparison.Ordinal));
        var inferred = items.Count(item => item.Status.Contains("推测", StringComparison.Ordinal));
        var unknown = items.Count - actual - inferred;
        SummaryText.Text = $"{rootPath}  |  共 {items.Count} 项，实际候选 {actual}，仅推测 {inferred}，未定位 {unknown}";
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出批量扫描报告",
            Filter = "JSON 文件 (*.json)|*.json",
            FileName = $"aigamesave-local-scan-{DateTime.Now:yyyyMMdd-HHmmss}.json"
        };
        if (dialog.ShowDialog() != true) return;
        var report = new
        {
            generatedAt = DateTimeOffset.UtcNow,
            rootPath = _rootPath,
            detectionMode = "generic-static-local-only",
            usedAi = false,
            usedWebSearch = false,
            usedGameSpecificRules = false,
            items = _items
        };
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(report, options));
        MessageBox.Show("报告已导出。", "AiGameSave", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private sealed record BatchScanRow(string Name, string Engine, string Status, string? ExecutablePath, string CandidateSummary)
    {
        public static BatchScanRow From(BatchGameScanItem item)
        {
            var candidates = item.Candidates.Count == 0
                ? "无"
                : string.Join(Environment.NewLine, item.Candidates.Select(candidate =>
                    $"[{candidate.Confidence}/{candidate.Score}] {candidate.ResolvedPath} | {string.Join("；", candidate.Evidence.Select(evidence => evidence.Description))}"));
            return new BatchScanRow(item.Name, item.Engine.ToString(), item.Status, item.ExecutablePath, candidates);
        }
    }
}
