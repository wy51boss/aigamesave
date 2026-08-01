using System.Security.Cryptography;
using AiGameSave.Core;
using AiGameSave.Infrastructure;

namespace AiGameSave.Tests;

public sealed class RepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiGameSaveTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Snapshot_BackupDeleteRestore_PreservesBytes()
    {
        var repositoryPath = Path.Combine(_root, "repo");
        var savePath = Path.Combine(_root, "save");
        Directory.CreateDirectory(savePath);
        var saveFile = Path.Combine(savePath, "slot1.sav");
        var expected = RandomNumberGenerator.GetBytes(2048);
        await File.WriteAllBytesAsync(saveFile, expected);
        var profile = new GameProfile("test-game", "Test Game", null, null, null, GamePersistenceKind.TemporarySystemDirectory,
            new[] { new SaveLocationRule(savePath, UserConfirmed: true) }, DateTimeOffset.UtcNow, true);
        var repository = new JsonGameRepository(repositoryPath);
        await repository.SaveGameAsync(profile);
        var snapshot = await repository.CreateSnapshotAsync(profile);
        File.Delete(saveFile);
        await repository.RestoreSnapshotAsync(profile, snapshot);
        Assert.Equal(expected, await File.ReadAllBytesAsync(saveFile));
        Assert.Single((await repository.ListSnapshotsAsync(profile.Id)));
    }

    [Fact]
    public async Task Repository_PersistsGameProfile()
    {
        var repository = new JsonGameRepository(Path.Combine(_root, "repo"));
        var profile = new GameProfile("game", "Name", null, null, null, GamePersistenceKind.Unknown, Array.Empty<SaveLocationRule>(), DateTimeOffset.UtcNow);
        await repository.SaveGameAsync(profile);
        Assert.Equal(profile.Id, (await repository.GetGameAsync(profile.Id))?.Id);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
