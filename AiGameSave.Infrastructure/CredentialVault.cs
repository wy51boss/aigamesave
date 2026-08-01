using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiGameSave.Core;
using Konscious.Security.Cryptography;

namespace AiGameSave.Infrastructure;

public sealed class CredentialVault : ICredentialVault
{
    private sealed record Envelope(int Version, string Salt, string Nonce, string Tag, string Ciphertext);
    private sealed record Stored(ModelProfile Profile, string ApiKey);
    private readonly string _path;

    public CredentialVault(string repositoryRoot) => _path = Path.Combine(repositoryRoot, "credentials.vault");

    public Task<bool> ExistsAsync(CancellationToken cancellationToken = default) => Task.FromResult(File.Exists(_path));

    public async Task SaveModelProfileAsync(ModelProfile profile, string apiKey, string masterPassword, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(masterPassword)) throw new ArgumentException("主密码不能为空", nameof(masterPassword));
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = await DeriveAsync(masterPassword, salt);
        var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Stored(profile, apiKey)));
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        var json = JsonSerializer.Serialize(new Envelope(1, Convert.ToBase64String(salt), Convert.ToBase64String(nonce), Convert.ToBase64String(tag), Convert.ToBase64String(cipher)), new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_path, json, cancellationToken);
    }

    public async Task<(ModelProfile Profile, string ApiKey)?> UnlockAsync(string masterPassword, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return null;
        var envelope = JsonSerializer.Deserialize<Envelope>(await File.ReadAllTextAsync(_path, cancellationToken)) ?? throw new InvalidDataException("凭据文件格式错误");
        try
        {
            var key = await DeriveAsync(masterPassword, Convert.FromBase64String(envelope.Salt));
            var cipher = Convert.FromBase64String(envelope.Ciphertext);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(Convert.FromBase64String(envelope.Nonce), cipher, Convert.FromBase64String(envelope.Tag), plain);
            var stored = JsonSerializer.Deserialize<Stored>(plain) ?? throw new InvalidDataException("凭据内容为空");
            return (stored.Profile, stored.ApiKey);
        }
        catch (CryptographicException) { throw new UnauthorizedAccessException("主密码错误或凭据已损坏"); }
    }

    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(_path)) File.Delete(_path);
        return Task.CompletedTask;
    }

    private static async Task<byte[]> DeriveAsync(string password, byte[] salt)
    {
        var argon = new Argon2id(Encoding.UTF8.GetBytes(password)) { Salt = salt, DegreeOfParallelism = 2, Iterations = 3, MemorySize = 65536 };
        return await argon.GetBytesAsync(32);
    }
}
