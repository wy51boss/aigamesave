using AiGameSave.Core;
using AiGameSave.Infrastructure;

namespace AiGameSave.Tests;

public sealed class CredentialVaultTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiGameSaveTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Vault_RoundTripsWithoutPlaintextKey()
    {
        var vault = new CredentialVault(_root);
        var profile = new ModelProfile("https://api.deepseek.com/v1", "deepseek-chat");
        await vault.SaveModelProfileAsync(profile, "secret-api-key", "strong password");
        var raw = await File.ReadAllTextAsync(Path.Combine(_root, "credentials.vault"));
        Assert.DoesNotContain("secret-api-key", raw);
        var unlocked = await vault.UnlockAsync("strong password");
        Assert.NotNull(unlocked);
        Assert.Equal("secret-api-key", unlocked.Value.ApiKey);
        Assert.Equal(profile, unlocked.Value.Profile);
    }

    [Fact]
    public async Task Vault_RejectsWrongPassword()
    {
        var vault = new CredentialVault(_root);
        await vault.SaveModelProfileAsync(new ModelProfile("https://example.test", "model"), "key", "correct");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => vault.UnlockAsync("wrong"));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
