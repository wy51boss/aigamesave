using System.Reflection;
using System.Text.Json;
using AiGameSave.Core;

namespace AiGameSave.Infrastructure;

public interface IRuleCatalog
{
    Task<IReadOnlyList<GameRuleDefinition>> ListAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GameRuleDefinition>> FindAsync(string gameName, string? executablePath, CancellationToken cancellationToken = default);
}

public sealed class RuleCatalog : IRuleCatalog
{
    private readonly string _userDirectory;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public RuleCatalog(string repositoryRoot) => _userDirectory = Path.Combine(repositoryRoot, "rules", "user");

    public async Task<IReadOnlyList<GameRuleDefinition>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<GameRuleDefinition>();
        var assembly = Assembly.GetExecutingAssembly();
        await using var stream = assembly.GetManifestResourceStream("AiGameSave.Infrastructure.Rules.builtin-rules.json");
        if (stream is not null)
            result.AddRange(await JsonSerializer.DeserializeAsync<List<GameRuleDefinition>>(stream, _json, cancellationToken) ?? new());
        if (Directory.Exists(_userDirectory))
            foreach (var file in Directory.EnumerateFiles(_userDirectory, "*.json"))
            {
                try
                {
                    var userRule = await JsonSerializer.DeserializeAsync<GameRuleDefinition>(File.OpenRead(file), _json, cancellationToken);
                    if (userRule is not null) result.RemoveAll(x => x.Id.Equals(userRule.Id, StringComparison.OrdinalIgnoreCase));
                    if (userRule is not null) result.Add(userRule);
                }
                catch { }
            }
        return result;
    }

    public async Task<IReadOnlyList<GameRuleDefinition>> FindAsync(string gameName, string? executablePath, CancellationToken cancellationToken = default)
    {
        var exe = Path.GetFileName(executablePath) ?? string.Empty;
        var tokens = gameName.Split(new[] { ' ', '-', '_', ':' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (await ListAsync(cancellationToken)).Where(rule =>
            rule.Name.Contains(gameName, StringComparison.OrdinalIgnoreCase) || rule.Aliases.Any(alias => alias.Contains(gameName, StringComparison.OrdinalIgnoreCase) || tokens.Any(t => alias.Contains(t, StringComparison.OrdinalIgnoreCase))) || rule.ExecutableNames.Any(x => x.Equals(exe, StringComparison.OrdinalIgnoreCase))).ToArray();
    }
}
