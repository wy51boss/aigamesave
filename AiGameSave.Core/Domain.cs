namespace AiGameSave.Core;

public enum CandidateConfidence { Low, Possible, High, Verified }
public enum GamePersistenceKind { Unknown, PersistentGameDirectory, PersistentSaveOutsideGame, TemporarySystemDirectory }
public enum GameEngineKind { Unknown, Unity, RenPy, RpgMakerMvMz, RpgMakerLegacy, Unreal, Godot, GameMaker, WolfRpg, NwJs }

public sealed record Evidence(string Type, string Description, int Weight, string? SourceUrl = null);

public sealed record CandidateLocation(
    string PathTemplate,
    string ResolvedPath,
    string Kind,
    int Score,
    CandidateConfidence Confidence,
    IReadOnlyList<Evidence> Evidence,
    IReadOnlyList<string> IncludePatterns,
    IReadOnlyList<string> ExcludePatterns,
    string? Notes = null);

public sealed record SaveLocationRule(
    string PathTemplate,
    string Kind = "save",
    IReadOnlyList<string>? IncludePatterns = null,
    IReadOnlyList<string>? ExcludePatterns = null,
    IReadOnlyList<string>? RegistryKeys = null,
    string? Source = null,
    bool UserConfirmed = false);

public sealed record GameProfile(
    string Id,
    string Name,
    string? ExecutablePath,
    string? Platform,
    string? AppId,
    GamePersistenceKind PersistenceKind,
    IReadOnlyList<SaveLocationRule> SaveLocations,
    DateTimeOffset UpdatedAt,
    bool IsVerified = false);

public sealed record FileSnapshotEntry(string RelativePath, long Length, DateTimeOffset LastWriteUtc, string Sha256);

public sealed record SnapshotManifest(
    string SnapshotId,
    string GameId,
    DateTimeOffset CreatedAt,
    string SourceMachine,
    IReadOnlyList<FileSnapshotEntry> Files,
    IReadOnlyList<string> RegistryKeys,
    bool Pinned = false,
    bool Safety = false,
    string? Note = null);

public sealed record ModelProfile(string BaseUrl, string Model, string Protocol = "Auto", string? DisplayName = null);
public sealed record ResearchRequest(string GameName, string? ExecutablePath, string? Platform, string? AppId);
public sealed record ResearchResult(string GameName, IReadOnlyList<CandidateLocation> Candidates, string Summary, bool UsedWebSearch);
public sealed record GameRuleDefinition(string Id, string Name, IReadOnlyList<string> Aliases, IReadOnlyList<string> ExecutableNames, IReadOnlyList<SaveLocationRule> SaveLocations, string? Platform = null, string? AppId = null);
public sealed record EngineDetectionResult(GameEngineKind Engine, IReadOnlyList<CandidateLocation> Candidates, IReadOnlyList<Evidence> Evidence);
public sealed record BatchGameScanItem(string Name, string RootPath, string? ExecutablePath, GameEngineKind Engine, IReadOnlyList<CandidateLocation> Candidates, string Status);
public sealed record SaveExportItem(string GameName, string Status, string? SourcePath, string? ExportPath, int FilesCopied, string Reason, IReadOnlyList<string>? SourcePaths = null, IReadOnlyList<string>? ExportPaths = null);
public sealed record BatchSaveExportReport(DateTimeOffset GeneratedAt, string RootPath, string OutputPath, IReadOnlyList<SaveExportItem> Items, bool UsedAi = false, bool UsedWebSearch = false, bool UsedGameSpecificRules = false);

public interface IPathTemplateResolver
{
    string Resolve(string template, GameProfile? game = null);
    bool IsAllowedPath(string resolvedPath, IReadOnlyCollection<string>? extraRoots = null);
}

public interface IGameRepository
{
    string RootPath { get; }
    Task<IReadOnlyList<GameProfile>> ListGamesAsync(CancellationToken cancellationToken = default);
    Task SaveGameAsync(GameProfile profile, CancellationToken cancellationToken = default);
    Task<GameProfile?> GetGameAsync(string gameId, CancellationToken cancellationToken = default);
    Task<SnapshotManifest> CreateSnapshotAsync(GameProfile profile, bool pinned = false, bool safety = false, string? note = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SnapshotManifest>> ListSnapshotsAsync(string gameId, CancellationToken cancellationToken = default);
    Task RestoreSnapshotAsync(GameProfile profile, SnapshotManifest snapshot, CancellationToken cancellationToken = default);
}

public interface ICredentialVault
{
    Task SaveModelProfileAsync(ModelProfile profile, string apiKey, string masterPassword, CancellationToken cancellationToken = default);
    Task<(ModelProfile Profile, string ApiKey)?> UnlockAsync(string masterPassword, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(CancellationToken cancellationToken = default);
    Task ResetAsync(CancellationToken cancellationToken = default);
}

public interface IGameResearchService
{
    Task<ResearchResult> ResearchAsync(ResearchRequest request, ModelProfile? profile, string? apiKey, CancellationToken cancellationToken = default);
}

public interface ILocalDetectionService
{
    Task<IReadOnlyList<CandidateLocation>> ScanAsync(ResearchRequest request, IReadOnlyList<CandidateLocation> researchCandidates, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CandidateLocation>> VerifySaveWindowAsync(ResearchRequest request, IReadOnlyList<CandidateLocation> candidates, TimeSpan window, CancellationToken cancellationToken = default);
}

public interface IEngineDetectionService
{
    Task<EngineDetectionResult> DetectAsync(string gameRoot, string? executablePath, CancellationToken cancellationToken = default);
}

public interface IBatchGameScanService
{
    Task<IReadOnlyList<BatchGameScanItem>> ScanAsync(string rootPath, CancellationToken cancellationToken = default);
}

public interface IBatchSaveExportService
{
    Task<BatchSaveExportReport> ExportAsync(string rootPath, string outputPath, CancellationToken cancellationToken = default);
}

public static class SavePathDefaults
{
    public static readonly string[] Roots =
    {
        "%USERPROFILE%\\Documents\\My Games", "%USERPROFILE%\\Saved Games", "%APPDATA%", "%LOCALAPPDATA%", "%USERPROFILE%\\AppData\\LocalLow", "%PROGRAMDATA%"
    };
    public static readonly string[] Excludes = { "cache", "caches", "logs", "log", "crashdumps", "shadercache", "temp", "tmp" };
}
