using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using AiGameSave.Core;
using AiGameSave.Infrastructure;

namespace AiGameSave.App;

public partial class MainWindow : Window
{
    private IGameRepository _repository;
    private readonly ILocalDetectionService _detector = new LocalDetectionService();
    private readonly PathTemplateResolver _resolver = new();
    private ResearchService _research;
    private IReadOnlyList<CandidateLocation> _candidates = Array.Empty<CandidateLocation>();
    private IReadOnlyList<GameProfile> _games = Array.Empty<GameProfile>();
    private SaveActivityMonitor? _activityMonitor;

    public MainWindow()
    {
        InitializeComponent();
        RepositoryPathText.Text = Path.Combine(AppContext.BaseDirectory, "AiGameSaveData");
        _repository = new JsonGameRepository(RepositoryPathText.Text);
        _research = new ResearchService(repositoryRoot: _repository.RootPath);
        Loaded += async (_, _) => await RefreshGamesAsync();
        CandidatesList.SelectionChanged += CandidatesList_SelectionChanged;
    }

    private async void ApplyRepository_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(RepositoryPathText.Text)) throw new InvalidOperationException("请填写持久仓库路径。");
            _repository = new JsonGameRepository(RepositoryPathText.Text);
            _research = new ResearchService(repositoryRoot: _repository.RootPath);
            await RefreshGamesAsync();
            SetStatus("仓库已切换。建议将仓库放在关机不清空的云硬盘。", false);
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    private void ChooseExe_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "游戏程序 (*.exe)|*.exe|所有文件 (*.*)|*.*", Title = "选择游戏 EXE" };
        if (dialog.ShowDialog() == true)
        {
            ExecutableText.Text = dialog.FileName;
            if (string.IsNullOrWhiteSpace(GameNameText.Text)) GameNameText.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        }
    }

    private async void BatchScan_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择包含多个游戏的目录", Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        await RunBusyAsync(async () =>
        {
            var results = await new BatchGameScanService().ScanAsync(dialog.FolderName);
            new BatchScanWindow(dialog.FolderName, results) { Owner = this }.ShowDialog();
            SetStatus($"批量本地扫描完成：共 {results.Count} 个目录。", false);
        });
    }

    private async void Research_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            var request = BuildRequest();
            var key = ApiKeyText.Password;
            var profile = string.IsNullOrWhiteSpace(BaseUrlText.Text) || string.IsNullOrWhiteSpace(ModelText.Text)
                ? null
                : new ModelProfile(BaseUrlText.Text.Trim(), ModelText.Text.Trim(), "Auto", "当前模型");
            if (profile is not null && !string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(MasterPasswordText.Password))
                await new CredentialVault(_repository.RootPath).SaveModelProfileAsync(profile, key, MasterPasswordText.Password);
            var result = await _research.ResearchAsync(request, profile, string.IsNullOrWhiteSpace(key) ? null : key);
            _candidates = result.Candidates;
            CandidatesList.ItemsSource = _candidates.Select(CandidateRow.From).ToArray();
            SetStatus(result.Summary, false);
        });
    }

    private async void UnlockVault_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var vault = new CredentialVault(_repository.RootPath);
            if (!await vault.ExistsAsync()) throw new InvalidOperationException("当前仓库还没有保存的模型凭据。");
            var unlocked = await vault.UnlockAsync(MasterPasswordText.Password);
            if (unlocked is null) throw new InvalidOperationException("当前仓库还没有保存的模型凭据。");
            BaseUrlText.Text = unlocked.Value.Profile.BaseUrl;
            ModelText.Text = unlocked.Value.Profile.Model;
            ApiKeyText.Password = unlocked.Value.ApiKey;
            SetStatus("模型凭据已解锁，仅在本次运行期间使用。", false);
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    private void BeginBehavior_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var request = BuildRequest();
            _activityMonitor?.Dispose();
            _activityMonitor = new SaveActivityMonitor();
            var candidateRoots = _candidates.Select(x => x.ResolvedPath).Where(path => !path.Contains('*') && Directory.Exists(path)).ToArray();
            var watchRoots = SavePathDefaults.Roots.Select(x => _resolver.Resolve(x)).Concat(candidateRoots).ToList();
            if (!string.IsNullOrWhiteSpace(request.ExecutablePath)) watchRoots.Add(Path.GetDirectoryName(request.ExecutablePath)!);
            _activityMonitor.Start(watchRoots, candidateRoots);
            if (!string.IsNullOrWhiteSpace(request.ExecutablePath) && File.Exists(request.ExecutablePath))
                Process.Start(new ProcessStartInfo(request.ExecutablePath) { WorkingDirectory = Path.GetDirectoryName(request.ExecutablePath), UseShellExecute = true });
            SetStatus("行为检测已开始。请进入游戏执行一次保存，然后点击“我已保存”。", false);
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    private async void CompleteBehavior_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            if (_activityMonitor is null) throw new InvalidOperationException("请先点击“开始行为检测”。");
            var candidateRoots = _candidates.Select(x => x.ResolvedPath).Where(path => !path.Contains('*') && Directory.Exists(path)).ToArray();
            var changes = await _activityMonitor.CompleteAsync(candidateRoots);
            _candidates = LocalDetectionService.MergeActivityCandidates(_candidates, changes);
            CandidatesList.ItemsSource = _candidates.Select(CandidateRow.From).ToArray();
            _activityMonitor.Dispose();
            _activityMonitor = null;
            SetStatus($"行为检测完成，捕获 {changes.Count} 个文件变化。", false);
        });
    }

    private async void ConfirmCandidate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var row = CandidatesList.SelectedItem as CandidateRow ?? throw new InvalidOperationException("请先选择一个候选目录。");
            var request = BuildRequest();
            var id = MakeGameId(request.GameName, request.AppId);
            var rule = new SaveLocationRule(row.PathTemplate, "save", Array.Empty<string>(), SavePathDefaults.Excludes, Array.Empty<string>(), "user-confirmed", true);
            var kind = IsGameDirectory(row.ResolvedPath, request.ExecutablePath) ? GamePersistenceKind.PersistentGameDirectory : GamePersistenceKind.TemporarySystemDirectory;
            var profile = new GameProfile(id, request.GameName, request.ExecutablePath, request.Platform, request.AppId, kind, new[] { rule }, DateTimeOffset.UtcNow, row.Confidence == CandidateConfidence.Verified);
            await _repository.SaveGameAsync(profile);
            await RefreshGamesAsync();
            GamesList.SelectedItem = _games.FirstOrDefault(x => x.Id == profile.Id);
            SetStatus("存档位置已确认。以后可以使用“还原并启动”或“一键备份”。", false);
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            var game = SelectedGame() ?? throw new InvalidOperationException("请先选择已保护的游戏。");
            var snapshot = await _repository.CreateSnapshotAsync(game, note: "用户手动备份");
            SetStatus($"备份完成：{snapshot.SnapshotId}，共 {snapshot.Files.Count} 个文件。", false);
            await RefreshGameStatusAsync(game);
        });
    }

    private async void RestoreLaunch_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            var game = SelectedGame() ?? throw new InvalidOperationException("请先选择已保护的游戏。");
            var snapshots = await _repository.ListSnapshotsAsync(game.Id);
            if (snapshots.Count > 0)
            {
                var latest = snapshots[0];
                await _repository.RestoreSnapshotAsync(game, latest);
                SetStatus($"已还原快照 {latest.SnapshotId}，正在启动游戏。", false);
            }
            Launch(game);
        });
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        try { Launch(SelectedGame() ?? throw new InvalidOperationException("请先选择已保护的游戏。")); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    private void OpenSaveDirectory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var game = SelectedGame() ?? throw new InvalidOperationException("请先选择已保护的游戏。");
            var path = _resolver.ResolveExisting(_resolver.Resolve(game.SaveLocations[0].PathTemplate, game));
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    private async void History_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var game = SelectedGame() ?? throw new InvalidOperationException("请先选择已保护的游戏。");
            var snapshots = await _repository.ListSnapshotsAsync(game.Id);
            GameStatus.Text = snapshots.Count == 0 ? "还没有备份。" : string.Join(Environment.NewLine, snapshots.Select(x => $"{x.CreatedAt.LocalDateTime:g}  {x.SnapshotId}  文件:{x.Files.Count}  {(x.Pinned ? "固定" : "普通")}"));
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    private async void GamesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SelectedGame() is { } game) await RefreshGameStatusAsync(game);
    }

    private void CandidatesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CandidatesList.SelectedItem is CandidateRow row)
            CandidateDetails.Text = $"{row.Confidence} · 评分 {row.Score}\n{row.ResolvedPath}\n{row.Evidence}";
    }

    private ResearchRequest BuildRequest() => new(
        string.IsNullOrWhiteSpace(GameNameText.Text) ? throw new InvalidOperationException("请填写游戏名称。") : GameNameText.Text.Trim(),
        string.IsNullOrWhiteSpace(ExecutableText.Text) ? null : ExecutableText.Text.Trim(), null, null);

    private async Task RefreshGamesAsync()
    {
        _games = await _repository.ListGamesAsync();
        GamesList.ItemsSource = _games;
        if (_games.Count > 0 && GamesList.SelectedIndex < 0) GamesList.SelectedIndex = 0;
    }

    private async Task RefreshGameStatusAsync(GameProfile game)
    {
        var snapshots = await _repository.ListSnapshotsAsync(game.Id);
        var path = _resolver.Resolve(game.SaveLocations[0].PathTemplate, game);
        GameStatus.Text = $"{game.Name}\n状态：{(game.IsVerified ? "已验证" : "已确认，待验证")}\n类型：{game.PersistenceKind}\n存档目录：{path}\n最近快照：{(snapshots.FirstOrDefault()?.CreatedAt.LocalDateTime.ToString("g") ?? "无")}";
    }

    private GameProfile? SelectedGame() => GamesList.SelectedItem as GameProfile;

    private static string MakeGameId(string name, string? appId) => string.Join('-', (appId ?? name).ToLowerInvariant().Split(Path.GetInvalidFileNameChars()).SelectMany(x => x.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries))).Trim('-');

    private static bool IsGameDirectory(string candidate, string? executable) => executable is not null && PathTemplateResolver.IsSubPath(candidate, Path.GetDirectoryName(Path.GetFullPath(executable))!);

    private static void Launch(GameProfile game)
    {
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath)) throw new FileNotFoundException("游戏 EXE 不存在，请重新选择。", game.ExecutablePath);
        Process.Start(new ProcessStartInfo(game.ExecutablePath) { WorkingDirectory = Path.GetDirectoryName(game.ExecutablePath), UseShellExecute = true });
    }

    private async Task RunBusyAsync(Func<Task> operation)
    {
        try { SetStatus("处理中，请稍候…", false); await operation(); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    private void SetStatus(string message, bool error) { StatusText.Text = message; StatusText.Foreground = error ? System.Windows.Media.Brushes.Firebrick : System.Windows.Media.Brushes.DarkSlateGray; }

    private sealed record CandidateRow(string Display, string PathTemplate, string ResolvedPath, int Score, CandidateConfidence Confidence, string Evidence)
    {
        public static CandidateRow From(CandidateLocation x) => new($"[{x.Confidence}] {x.PathTemplate} · {x.Score}", x.PathTemplate, x.ResolvedPath, x.Score, x.Confidence, string.Join("；", x.Evidence.Select(e => e.Description)));
    }
}
