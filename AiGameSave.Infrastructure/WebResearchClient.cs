using System.Net;
using System.Xml.Linq;
using AngleSharp;

namespace AiGameSave.Infrastructure;

public sealed record WebSearchItem(string Title, string Url, string Snippet, string Source);

public sealed class WebResearchClient
{
    private readonly HttpClient _http;
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    public WebResearchClient(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient(new HttpClientHandler { UseProxy = false });
        _http.Timeout = TimeSpan.FromSeconds(10);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AiGameSave/0.1 (save location research)");
    }

    public async Task<IReadOnlyList<WebSearchItem>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var delay = TimeSpan.FromMilliseconds(350) - (DateTimeOffset.UtcNow - _lastRequest);
        if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
        _lastRequest = DateTimeOffset.UtcNow;
        var result = new List<WebSearchItem>();
        try
        {
            var pcgw = $"https://www.pcgamingwiki.com/w/api.php?action=query&list=search&srsearch={Uri.EscapeDataString(query)}&format=json&origin=*";
            using var response = await _http.GetAsync(pcgw, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var document = System.Text.Json.JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("query", out var q) && q.TryGetProperty("search", out var search))
                    foreach (var item in search.EnumerateArray().Take(5))
                    {
                        var title = item.GetProperty("title").GetString() ?? string.Empty;
                        result.Add(new WebSearchItem(title, "https://www.pcgamingwiki.com/wiki/" + Uri.EscapeDataString(title.Replace(' ', '_')), Strip(item.GetProperty("snippet").GetString() ?? string.Empty), "PCGamingWiki"));
                    }
            }
        }
        catch { }

        try
        {
            var rss = await _http.GetStringAsync($"https://www.bing.com/search?format=rss&q={Uri.EscapeDataString(query)}", cancellationToken);
            var xml = XDocument.Parse(rss);
            result.AddRange(xml.Descendants("item").Take(5).Select(item => new WebSearchItem(item.Element("title")?.Value ?? string.Empty, item.Element("link")?.Value ?? string.Empty, item.Element("description")?.Value ?? string.Empty, "Bing")));
        }
        catch { }
        return result.GroupBy(x => x.Url, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToArray();
    }

    public async Task<string> FetchPageTextAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || IsPrivate(uri.Host)) return string.Empty;
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 1_048_576) return string.Empty;
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var context = BrowsingContext.New(Configuration.Default);
            var document = await context.OpenAsync(req => req.Content(html), cancellationToken);
            foreach (var node in document.QuerySelectorAll("script,style,noscript")) node.Remove();
            var text = string.Join(' ', document.Body?.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>()).Trim();
            return text[..Math.Min(12000, text.Length)];
        }
        catch { return string.Empty; }
    }

    private static bool IsPrivate(string host)
    {
        if (IPAddress.TryParse(host, out var address)) return IPAddress.IsLoopback(address) || address.ToString().StartsWith("10.") || address.ToString().StartsWith("192.168.");
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
    }

    private static string Strip(string text) => WebUtility.HtmlDecode(text.Replace("<span class=\"searchmatch\">", string.Empty).Replace("</span>", string.Empty));
}
