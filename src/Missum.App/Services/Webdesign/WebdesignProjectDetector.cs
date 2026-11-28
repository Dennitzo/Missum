using System.Text.Json;
using System.Text.RegularExpressions;
using Missum.Core.Models;

namespace Missum.App.Services.Webdesign;

/// <summary>
/// Recognises Codex work on a website while the Webdesign tool is still off, so the local AI is
/// offered webdesign.serve/webdesign.preview with a short hint. Only reads: it never starts a
/// server or writes into the project.
/// </summary>
internal static partial class WebdesignProjectDetector
{
    private const int MaximumPackageJsonBytes = 512 * 1024;
    private const int InspectedHistoryMessages = 40;

    /// <summary>Folders whose index.html marks a static or built website.</summary>
    private static readonly string[] PageFolders = ["", "public", "src", "dist"];

    /// <summary>npm packages of web frameworks, bundlers and dev servers.</summary>
    private static readonly HashSet<string> WebPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "vite", "next", "react", "vue", "svelte", "astro", "@angular/core", "nuxt", "gatsby", "remix", "parcel", "webpack-dev-server",
    };

    // German and English words for a website or its front end. Long words also match inside German
    // compounds (Firmenwebseite, Homepagelayout); HTML and CSS only as whole words.
    [GeneratedRegex(@"web-?seite|web-?site|webpage|web\s+page|homepage|landing[-\s]?page|webauftritt|webdesign|frontend|front-end|\b(?:html5?|css3?)\b|\bui\s+der\s+(?:web-?)?seite",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WebWords();

    /// <summary>The project or the conversation clearly concerns a website.</summary>
    public static bool Suggests(string? workspace, string? prompt, IReadOnlyList<ChatMessage>? history = null) =>
        IsWebPrompt(prompt)
        || (history ?? []).Where(static message => message.Role == ChatRole.User).TakeLast(InspectedHistoryMessages)
            .Any(static message => IsWebPrompt(message.Content))
        || IsWebProject(workspace);

    public static bool IsWebPrompt(string? prompt) => !string.IsNullOrWhiteSpace(prompt) && WebWords().IsMatch(prompt);

    /// <summary>index.html in the root, public, src or dist, or a package.json that uses a web framework.</summary>
    public static bool IsWebProject(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace)) return false;
        try
        {
            if (PageFolders.Any(folder => File.Exists(Path.Combine(workspace, folder, "index.html")))) return true;
            var package = new FileInfo(Path.Combine(workspace, "package.json"));
            if (!package.Exists || package.Length > MaximumPackageJsonBytes) return false;
            using var document = JsonDocument.Parse(File.ReadAllBytes(package.FullName));
            foreach (var section in new[] { "dependencies", "devDependencies", "peerDependencies" })
            {
                if (!document.RootElement.TryGetProperty(section, out var dependencies) || dependencies.ValueKind != JsonValueKind.Object) continue;
                foreach (var dependency in dependencies.EnumerateObject())
                    if (WebPackages.Contains(dependency.Name) || dependency.Name.StartsWith("@remix-run/", StringComparison.OrdinalIgnoreCase))
                        return true;
            }
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
