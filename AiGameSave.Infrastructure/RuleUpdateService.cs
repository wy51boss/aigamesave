using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiGameSave.Infrastructure;

public sealed record RuleFeedManifest(int Version, string PackageUrl, string Sha256, string Signature);

public sealed class RuleUpdateService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly string _officialDirectory;
    public RuleUpdateService(string repositoryRoot) => _officialDirectory = Path.Combine(repositoryRoot, "rules", "official");

    public async Task<bool> UpdateAsync(string? feedUrl, string? publicKeyPem, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(feedUrl) || string.IsNullOrWhiteSpace(publicKeyPem)) return false;
        var manifest = JsonSerializer.Deserialize<RuleFeedManifest>(await _http.GetStringAsync(feedUrl, cancellationToken)) ?? throw new InvalidDataException("规则清单格式错误");
        var signed = Encoding.UTF8.GetBytes($"{manifest.Version}\n{manifest.PackageUrl}\n{manifest.Sha256}");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        if (!rsa.VerifyData(signed, Convert.FromBase64String(manifest.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new CryptographicException("规则包签名无效");
        var package = await _http.GetByteArrayAsync(manifest.PackageUrl, cancellationToken);
        if (!Convert.ToHexString(SHA256.HashData(package)).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase)) throw new CryptographicException("规则包哈希不匹配");
        var staging = Path.Combine(Path.GetTempPath(), "AiGameSaveRules", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var zip = Path.Combine(staging, "rules.zip");
            await File.WriteAllBytesAsync(zip, package, cancellationToken);
            var extracted = Path.Combine(staging, "extracted");
            ZipFile.ExtractToDirectory(zip, extracted);
            foreach (var file in Directory.EnumerateFiles(extracted, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(extracted, file);
                var target = Path.GetFullPath(Path.Combine(_officialDirectory, relative));
                if (!PathTemplateResolver.IsSubPath(target, _officialDirectory)) throw new InvalidDataException("规则包包含越界路径");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }
            return true;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
}
