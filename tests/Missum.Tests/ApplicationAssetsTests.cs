using Missum.App.Services;

namespace Missum.Tests;

public sealed class ApplicationAssetsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "missum-assets-" + Guid.NewGuid().ToString("N"));
    private string Executable => Path.Combine(_root, "portable");
    private string Extracted => Path.Combine(_root, "extracted");
    private string Assembly => Path.Combine(_root, "assembly");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExternalPortableAssetsAreFoundBesideTheExecutable(bool extractedCopyExists)
    {
        var package = Path.Combine(Executable, "Assets", "PublicationTool");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "Publikation-erzeugen.ps1"), "# Installed beside Missum.exe");
        if (extractedCopyExists) Directory.CreateDirectory(Path.Combine(Extracted, "Assets", "PublicationTool"));

        Assert.Equal(package, ApplicationAssets.ResolvePathFromDirectories(Executable, Extracted, Assembly, "Assets", "PublicationTool"));
        Assert.Equal(Path.Combine(package, "Publikation-erzeugen.ps1"),
            ApplicationAssets.ResolvePathFromDirectories(Executable, Extracted, Assembly, "Assets", "PublicationTool", "Publikation-erzeugen.ps1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BundledAssetsRemainAvailableWhenTheyHaveNoExternalCopy(bool baseCopyExists)
    {
        var directory = baseCopyExists ? Extracted : Assembly;
        var asset = Path.Combine(directory, "Assets", "styles.css");
        Directory.CreateDirectory(Path.GetDirectoryName(asset)!);
        File.WriteAllText(asset, "body {}");
        Assert.Equal(asset, ApplicationAssets.ResolvePathFromDirectories(null, Extracted, Assembly, "Assets", "styles.css"));
    }

    [Fact]
    public void MissingAssetsReturnTheExpectedBasePath()
    {
        Assert.Equal(Path.Combine(Extracted, "Assets", "missing"),
            ApplicationAssets.ResolvePathFromDirectories(Executable, Extracted, null, "Assets", "missing"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
