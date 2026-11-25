using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Missum.App.Services.ModelHub;

/// <summary>
/// Read-only access to the public Hugging Face Hub for unsloth GGUF repositories:
/// the newest models (or a search), a repository's files at one commit and its model card.
/// Anonymous; honours the hub's rate limit by waiting when it answers 429.
/// </summary>
public sealed partial class HuggingFaceHubClient : IDisposable
{
    public const string Author = "unsloth";
    private static readonly Uri BaseUri = new("https://huggingface.co/");
    private readonly HttpClient _http;

    public HuggingFaceHubClient(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = BaseUri;
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Missum", typeof(HuggingFaceHubClient).Assembly.GetName().Version?.ToString(3) ?? "2.0"));
    }

    [GeneratedRegex("<(?<url>[^>]+)>;\\s*rel=\"next\"")]
    private static partial Regex NextLink();

    /// <summary>Newest unsloth GGUF repositories first; with a search, only matching names.</summary>
    public async Task<HubModelPage> ListAsync(string? search, string? cursor, CancellationToken cancellationToken)
    {
        var url = cursor ?? "api/models?author=" + Author + "&filter=gguf&sort=lastModified&direction=-1&limit=40"
            + (string.IsNullOrWhiteSpace(search) ? "" : "&search=" + Uri.EscapeDataString(search.Trim()));
        using var response = await SendAsync(url, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var models = new List<HubModel>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(id)) continue;
            models.Add(new HubModel(id, Date(item, "lastModified"), Text(item, "pipeline_tag"), IsGated(item)));
        }
        string? next = null;
        if (response.Headers.TryGetValues("Link", out var links) && NextLink().Match(string.Join(",", links)) is { Success: true } match)
            next = match.Groups["url"].Value;
        return new HubModelPage(models, next);
    }

    /// <summary>Commit, metadata and every file with size and LFS sha256.</summary>
    public async Task<HubModelDetails> GetDetailsAsync(string repo, CancellationToken cancellationToken)
    {
        ValidateRepo(repo);
        string commit;
        DateTimeOffset lastModified;
        string? baseModel = null, pipeline;
        long? parameters = null;
        int? context = null;
        using (var response = await SendAsync("api/models/" + repo, cancellationToken).ConfigureAwait(false))
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            commit = Text(root, "sha") ?? throw new InvalidOperationException("Hugging Face lieferte keinen Commit für " + repo + ".");
            lastModified = Date(root, "lastModified");
            pipeline = Text(root, "pipeline_tag");
            if (root.TryGetProperty("gguf", out var gguf) && gguf.ValueKind == JsonValueKind.Object)
            {
                if (gguf.TryGetProperty("total", out var total) && total.TryGetInt64(out var count)) parameters = count;
                if (gguf.TryGetProperty("context_length", out var length) && length.TryGetInt32(out var tokens)) context = tokens;
            }
            if (root.TryGetProperty("cardData", out var card) && card.ValueKind == JsonValueKind.Object && card.TryGetProperty("base_model", out var based))
                baseModel = based.ValueKind == JsonValueKind.Array ? based.EnumerateArray().Select(e => e.GetString()).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
                    : based.ValueKind == JsonValueKind.String ? based.GetString() : null;
        }

        var files = await GetFilesAsync(repo, commit, cancellationToken).ConfigureAwait(false);
        return new HubModelDetails(repo, commit, lastModified, baseModel, pipeline, parameters, context, files);
    }

    /// <summary>Every file of a repository at one commit, with size and LFS sha256.</summary>
    public async Task<IReadOnlyList<HubFile>> GetFilesAsync(string repo, string commit, CancellationToken cancellationToken)
    {
        ValidateRepo(repo);
        var files = new List<HubFile>();
        string? url = $"api/models/{repo}/tree/{Uri.EscapeDataString(commit)}?recursive=true";
        while (url is not null)
        {
            using var response = await SendAsync(url, cancellationToken).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (Text(entry, "type") != "file") continue;
                var path = Text(entry, "path");
                if (string.IsNullOrWhiteSpace(path)) continue;
                var size = entry.TryGetProperty("size", out var bytes) && bytes.TryGetInt64(out var value) ? value : 0;
                string? sha = null;
                if (entry.TryGetProperty("lfs", out var lfs) && lfs.ValueKind == JsonValueKind.Object) sha = Text(lfs, "oid");
                files.Add(new HubFile(path, size, sha));
            }
            url = response.Headers.TryGetValues("Link", out var links) && NextLink().Match(string.Join(",", links)) is { Success: true } next
                ? next.Groups["url"].Value : null;
        }
        return files;
    }

    /// <summary>The model card as markdown, without its YAML front matter.</summary>
    public async Task<string> GetReadmeAsync(string repo, string commit, CancellationToken cancellationToken)
    {
        ValidateRepo(repo);
        using var response = await SendAsync($"{repo}/raw/{commit}/README.md", cancellationToken, allowNotFound: true).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return "";
        return StripFrontMatter(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    internal static string StripFrontMatter(string markdown)
    {
        var text = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) return text;
        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) return text;
        var after = text.IndexOf('\n', end + 4);
        return after < 0 ? "" : text[(after + 1)..].TrimStart('\n');
    }

    /// <summary>The resolve URL for one file; it redirects to a signed CDN address that expires.</summary>
    public static Uri ResolveUri(string repo, string commit, string path) =>
        new(BaseUri, $"{repo}/resolve/{commit}/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}");

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken cancellationToken, bool allowNotFound = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 2)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10 * (attempt + 1));
                response.Dispose();
                await Task.Delay(wait < TimeSpan.FromMinutes(1) ? wait : TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (response.IsSuccessStatusCode || (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)) return response;
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new HttpRequestException(status switch
            {
                401 or 403 => "Dieses Modell ist geschützt und braucht ein Hugging-Face-Konto.",
                404 => "Das Modell wurde auf Hugging Face nicht gefunden.",
                429 => "Hugging Face begrenzt gerade die Anfragen. Bitte gleich noch einmal versuchen.",
                _ => $"Hugging Face antwortete mit {status}.",
            }, null, response.StatusCode);
        }
    }

    private static void ValidateRepo(string repo)
    {
        if (!repo.StartsWith(Author + "/", StringComparison.OrdinalIgnoreCase) || repo.Contains("..", StringComparison.Ordinal) || repo.Count(c => c == '/') != 1)
            throw new ArgumentException("Nur unsloth-Modelle werden unterstützt.", nameof(repo));
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset Date(JsonElement element, string name) =>
        Text(element, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : DateTimeOffset.MinValue;

    private static bool IsGated(JsonElement element) =>
        element.TryGetProperty("gated", out var gated) && (gated.ValueKind == JsonValueKind.String || gated.ValueKind == JsonValueKind.True);

    public void Dispose() => _http.Dispose();
}
