using Missum.App.Services.Extensions;

namespace Missum.Tests;

public sealed class ExtensionRuntimeHostResolutionTests
{
    private const string PortableDirectory = @"C:\Missum\portable";
    private const string ExtractedDirectory = @"C:\Temp\bundle\Missum\bundle-id";

    [Fact]
    public void PortableHostBesideExecutableTakesPrecedenceOverExtractedHost()
    {
        var publishedHost = Path.Combine(PortableDirectory, "ExtensionHost", "ExtensionHost.exe");
        var extractedHost = Path.Combine(ExtractedDirectory, "ExtensionHost", "ExtensionHost.exe");
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { publishedHost, extractedHost };

        var result = ExtensionRuntimeService.ResolveHostExecutable(
            Path.Combine(PortableDirectory, "Missum.exe"), ExtractedDirectory, files.Contains);

        Assert.Equal(publishedHost, result);
    }

    [Theory]
    [InlineData("ExtensionHost", "ExtensionHost.exe")]
    [InlineData("ExtensionHost", "ExtensionHost.dll")]
    [InlineData("", "ExtensionHost.exe")]
    [InlineData("", "ExtensionHost.dll")]
    public void ApplicationBaseDirectoryRemainsAvailableWhenPortableHostIsAbsent(string folder, string fileName)
    {
        var fallback = Path.Combine(ExtractedDirectory, folder, fileName);

        var result = ExtensionRuntimeService.ResolveHostExecutable(
            Path.Combine(PortableDirectory, "Missum.exe"), ExtractedDirectory,
            path => string.Equals(path, fallback, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(fallback, result);
    }

    [Fact]
    public void MissingProcessPathStillResolvesTheApplicationHost()
    {
        var host = Path.Combine(ExtractedDirectory, "ExtensionHost", "ExtensionHost.exe");

        var result = ExtensionRuntimeService.ResolveHostExecutable(null, ExtractedDirectory,
            path => string.Equals(path, host, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(host, result);
    }

    [Fact]
    public void MissingHostReturnsNullWithoutProbingTheWorkingDirectory()
    {
        var probes = new List<string>();

        var result = ExtensionRuntimeService.ResolveHostExecutable(
            Path.Combine(PortableDirectory, "Missum.exe"), ExtractedDirectory,
            path => { probes.Add(path); return false; });

        Assert.Null(result);
        Assert.NotEmpty(probes);
        Assert.All(probes, path => Assert.True(
            path.StartsWith(PortableDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(ExtractedDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
    }
}
