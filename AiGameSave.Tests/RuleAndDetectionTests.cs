using AiGameSave.Core;
using AiGameSave.Infrastructure;

namespace AiGameSave.Tests;

public sealed class RuleAndDetectionTests
{
    [Fact]
    public async Task BuiltInRules_IncludeKnownGame()
    {
        var catalog = new RuleCatalog(Path.Combine(Path.GetTempPath(), "AiGameSaveTests", Guid.NewGuid().ToString("N")));
        var matches = await catalog.FindAsync("星露谷物语", "Stardew Valley.exe");
        Assert.Contains(matches, x => x.Id == "stardew-valley");
    }

    [Fact]
    public void ActivityMerge_PromotesChangedSaveDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "AiGameSaveTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "slot1.sav");
        File.WriteAllText(file, "save");
        var candidate = new CandidateLocation(root, root, "save", 40, CandidateConfidence.Possible, Array.Empty<Evidence>(), Array.Empty<string>(), Array.Empty<string>());
        var promoted = LocalDetectionService.MergeActivityCandidates(new[] { candidate }, new[] { file });
        Assert.Equal(CandidateConfidence.Verified, promoted[0].Confidence);
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task LocalDetection_RecognizesGenericUnityAppInfo()
    {
        var root = Path.Combine(Path.GetTempPath(), "AiGameSaveTests", Guid.NewGuid().ToString("N"));
        var game = Path.Combine(root, "ExampleUnityGame");
        var data = Path.Combine(game, "ExampleUnityGame_Data");
        Directory.CreateDirectory(data);
        await File.WriteAllTextAsync(Path.Combine(game, "ExampleUnityGame.exe"), "test");
        await File.WriteAllTextAsync(Path.Combine(data, "app.info"), "ExampleStudio\nExampleProduct\n");
        var service = new LocalDetectionService();
        var candidates = await service.ScanAsync(new ResearchRequest("Example Unity Game", Path.Combine(game, "ExampleUnityGame.exe"), null, null), Array.Empty<CandidateLocation>());
        Assert.Contains(candidates, x => x.Evidence.Any(e => e.Type == "engine-unity-path") && x.PathTemplate.Contains("ExampleStudio", StringComparison.Ordinal));
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task EngineDetection_RecognizesRenPyConfiguration()
    {
        var root = Path.Combine(Path.GetTempPath(), "AiGameSaveTests", Guid.NewGuid().ToString("N"));
        var game = Path.Combine(root, "FictionalRenPy");
        Directory.CreateDirectory(Path.Combine(game, "renpy"));
        Directory.CreateDirectory(Path.Combine(game, "game", "saves"));
        await File.WriteAllTextAsync(Path.Combine(game, "FictionalRenPy.exe"), "test");
        await File.WriteAllTextAsync(Path.Combine(game, "game", "options.rpy"), "define config.save_directory = 'fictional-save-id'");
        try
        {
            var result = await new EngineDetectionService().DetectAsync(game, Path.Combine(game, "FictionalRenPy.exe"));
            Assert.Equal(GameEngineKind.RenPy, result.Engine);
            Assert.Contains(result.Candidates, x => x.ResolvedPath.EndsWith(Path.Combine("game", "saves"), StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Candidates, x => x.ResolvedPath.EndsWith(Path.Combine("RenPy", "fictional-save-id"), StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task EngineDetection_RecognizesRpgMakerAndExistingSave()
    {
        var root = Path.Combine(Path.GetTempPath(), "AiGameSaveTests", Guid.NewGuid().ToString("N"));
        var game = Path.Combine(root, "FictionalRpgMaker");
        Directory.CreateDirectory(Path.Combine(game, "www", "data"));
        Directory.CreateDirectory(Path.Combine(game, "www", "save"));
        await File.WriteAllTextAsync(Path.Combine(game, "Game.exe"), "test");
        await File.WriteAllTextAsync(Path.Combine(game, "www", "data", "System.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(game, "www", "save", "file1.rpgsave"), "save");
        try
        {
            var result = await new EngineDetectionService().DetectAsync(game, Path.Combine(game, "Game.exe"));
            Assert.Equal(GameEngineKind.RpgMakerMvMz, result.Engine);
            Assert.Contains(result.Candidates, x => x.Score >= 90 && Directory.Exists(x.ResolvedPath));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task BatchScan_UsesOnlyGenericDirectoryEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "AiGameSaveTests", Guid.NewGuid().ToString("N"));
        var unity = Path.Combine(root, "PackageOne", "InnerGame");
        var unknown = Path.Combine(root, "PackageTwo");
        Directory.CreateDirectory(Path.Combine(unity, "UnlistedTitle_Data"));
        Directory.CreateDirectory(unknown);
        await File.WriteAllTextAsync(Path.Combine(unity, "UnlistedTitle.exe"), "test");
        await File.WriteAllTextAsync(Path.Combine(unity, "UnlistedTitle_Data", "app.info"), "UnknownStudio\nUnlistedTitle\n");
        await File.WriteAllTextAsync(Path.Combine(unknown, "Mystery.exe"), "test");
        try
        {
            var results = await new BatchGameScanService().ScanAsync(root);
            Assert.Equal(2, results.Count);
            Assert.Equal(GameEngineKind.Unity, results.Single(x => x.Name == "PackageOne").Engine);
            Assert.Equal(GameEngineKind.Unknown, results.Single(x => x.Name == "PackageTwo").Engine);
            Assert.Contains("行为检测", results.Single(x => x.Name == "PackageTwo").Status, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task BatchSaveExport_CopiesOnlyDetectedSaveFilesAndWritesReport()
    {
        var root = Path.Combine(Path.GetTempPath(), "AiGameSaveTests", Guid.NewGuid().ToString("N"));
        var game = Path.Combine(root, "ExportFixture");
        var output = root + "-output";
        Directory.CreateDirectory(Path.Combine(game, "www", "data"));
        Directory.CreateDirectory(Path.Combine(game, "www", "save"));
        await File.WriteAllTextAsync(Path.Combine(game, "Game.exe"), "test");
        await File.WriteAllTextAsync(Path.Combine(game, "www", "data", "System.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(game, "www", "save", "file1.rpgsave"), "save-bytes");
        await File.WriteAllTextAsync(Path.Combine(game, "www", "save", "readme.txt"), "not a save");
        try
        {
            var report = await new BatchSaveExportService().ExportAsync(root, output);
            var item = Assert.Single(report.Items);
            Assert.Equal("成功", item.Status);
            Assert.Equal(1, item.FilesCopied);
            Assert.True(File.Exists(Path.Combine(output, "ExportFixture", "candidate-1", "file1.rpgsave")));
            Assert.False(File.Exists(Path.Combine(output, "ExportFixture", "candidate-1", "readme.txt")));
            Assert.True(File.Exists(Path.Combine(output, "scan-export-report.json")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (Directory.Exists(output)) Directory.Delete(output, true);
        }
    }

}
