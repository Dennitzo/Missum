namespace Missum.Ai.Client;

public static class MissumAiClientFactory
{
    public static MissumAiClient Create(MissumAiClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ServerUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Missum AI supports HTTP or HTTPS server addresses.", nameof(options));
        }
        var httpClient = new HttpClient(
            new HttpClientHandler { UseProxy = false },
            disposeHandler: true)
        {
            BaseAddress = EnsureTrailingSlash(options.ServerUri),
            Timeout = options.RequestTimeout ?? TimeSpan.FromMinutes(20),
        };
        return new MissumAiClient(httpClient, options.ClientId, ownsHttpClient: true);
    }

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        var text = uri.AbsoluteUri.EndsWith('/') ? uri.AbsoluteUri : uri.AbsoluteUri + "/";
        return new Uri(text, UriKind.Absolute);
    }
}
