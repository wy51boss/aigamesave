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
    public async Task LocalDetection_RecognizesUnityAppInfo()
    {
        var root = Path.Combine(Path.GetTempPath(), "AiGameSaveTests", Guid.NewGuid().ToString("N"));
        var game = Path.Combine(root, "ThornSin");
        var data = Path.Combine(game, "ThornSin_Data");
        Directory.CreateDirectory(data);
        await File.WriteAllTextAsync(Path.Combine(game, "ThornSin.exe"), "test");
        await File.WriteAllTextAsync(Path.Combine(data, "app.info"), "ScarletPaper\nThornSin\n");
        var service = new LocalDetectionService();
        var candidates = await service.ScanAsync(new ResearchRequest("ThornSin", Path.Combine(game, "ThornSin.exe"), null, null), Array.Empty<CandidateLocation>());
        Assert.Contains(candidates, x => x.Evidence.Any(e => e.Type == "unity-app-info") && x.PathTemplate.Contains("ScarletPaper", StringComparison.Ordinal));
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task RealGame_ThornSin_DetectsAndRestoresWhenConfigured()
    {
        var gameRoot = Environment.GetEnvironmentVariable("AIGAMESAVE_THORNSIN_ROOT");
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot)) return;
        var exe = Path.Combine(gameRoot, "ThornSin.exe");
        var originalSave = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "ScarletPaper", "ThornSin");
        Assert.True(File.Exists(exe));
        Assert.True(Directory.Exists(originalSave));

        var detection = await new LocalDetectionService().ScanAsync(new ResearchRequest("ThornSin", exe, null, null), Array.Empty<CandidateLocation>());
        Assert.Contains(detection, x => x.Evidence.Any(e => e.Type == "unity-app-info") && x.ResolvedPath.Equals(originalSave, StringComparison.OrdinalIgnoreCase));

        var testRoot = Path.Combine(Path.GetTempPath(), "AiGameSaveRealTest", Guid.NewGuid().ToString("N"));
        var clonedSave = Path.Combine(testRoot, "save");
        try
        {
            CopyDirectory(originalSave, clonedSave);
            var saveFile = Directory.EnumerateFiles(clonedSave, "*.save", SearchOption.TopDirectoryOnly).First();
            var expected = await File.ReadAllBytesAsync(saveFile);
            var profile = new GameProfile("thornsin-real", "ThornSin", exe, null, null, GamePersistenceKind.TemporarySystemDirectory,
                new[] { new SaveLocationRule(clonedSave, UserConfirmed: true, ExcludePatterns: SavePathDefaults.Excludes) }, DateTimeOffset.UtcNow, true);
            var repository = new JsonGameRepository(Path.Combine(testRoot, "repo"));
            await repository.SaveGameAsync(profile);
            var snapshot = await repository.CreateSnapshotAsync(profile);
            await File.WriteAllTextAsync(saveFile, "temporary mutation");
            await repository.RestoreSnapshotAsync(profile, snapshot);
            Assert.Equal(expected, await File.ReadAllBytesAsync(saveFile));
        }
        finally { if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true); }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(directory.Replace(source, target, StringComparison.OrdinalIgnoreCase));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = file.Replace(source, target, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
    }
}
