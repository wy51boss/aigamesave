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
}
