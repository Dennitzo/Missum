using System.Diagnostics.CodeAnalysis;

namespace Missum.App.Services;

internal static class ApplicationAssets
{
    [UnconditionalSuppressMessage(
        "SingleFile",
        "IL3000",
        Justification = "The Portable configuration extracts all bundled content before startup; the assembly directory is the bundle extraction root.")]
    public static string ResolvePath(params string[] segments)
        => ResolvePathFromDirectories(Path.GetDirectoryName(Environment.ProcessPath), AppContext.BaseDirectory,
            Path.GetDirectoryName(typeof(ApplicationAssets).Assembly.Location), segments);

    internal static string ResolvePathFromDirectories(string? executableDirectory, string baseDirectory,
        string? assemblyDirectory, params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        // Portable extracts bundled files into a temporary base/assembly folder.
        // External payloads, including PublicationTool, stay beside Missum.exe.
        foreach (var directory in new[] { executableDirectory, baseDirectory, assemblyDirectory })
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var path = Path.Combine([directory, .. segments]);
            if (File.Exists(path) || Directory.Exists(path))
            {
                return path;
            }
        }
        return Path.Combine([baseDirectory, .. segments]);
    }
}
