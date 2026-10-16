using System.Text;
using Missum.App.Services;
using Missum.Core.Research;

namespace Missum.Tests;

public sealed class ScientificPresentationFeedbackTests
{
    [Fact]
    public async Task DirectFileRepairPreservesLastPdfAndClearsTheRendererError()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "workspace")).FullName;
        var source = Path.Combine(workspace, "Publikation.md");
        await File.WriteAllTextAsync(source, "# Modell\n\n$$x=1$$");
        var renders = 0;
        using var publications = new ScientificPublicationService(environment.Get<IScientificResearchRepository>(), async (path, token) =>
        {
            var content = await File.ReadAllTextAsync(path, token);
            Interlocked.Increment(ref renders);
            if (content.Contains("notACommand", StringComparison.Ordinal))
                throw new InvalidOperationException("KaTeX parse error: Undefined control sequence: notACommand");
            var target = Path.ChangeExtension(path, ".pdf");
            await File.WriteAllTextAsync(target, "%PDF-1.7\n" + new string(' ', 2048) + content, Encoding.ASCII, token);
            return target;
        }, workspace);
        using var coordinator = new ScientificPresentationCoordinator(publications);
        coordinator.RegisterWorkspace("project", workspace, "Modell");
        coordinator.Queue("project");
        await coordinator.WaitForIdleAsync("project");
        var initial = coordinator.GetSnapshot("project")!.Publication!;
        var initialBytes = await File.ReadAllBytesAsync(initial.PdfPath);

        await File.WriteAllTextAsync(source, "# Modell\n\n$$x=\\notACommand{1}$$");
        coordinator.Queue("project");
        await coordinator.WaitForIdleAsync("project");
        var failed = coordinator.GetSnapshot("project")!;
        Assert.Contains("notACommand", failed.PublicationError, StringComparison.Ordinal);
        Assert.Equal(initial, failed.Publication);
        Assert.Equal(initialBytes, await File.ReadAllBytesAsync(initial.PdfPath));

        await File.WriteAllTextAsync(source, "# Modell\n\n$$x=2$$");
        coordinator.Queue("project");
        await coordinator.WaitForIdleAsync("project");
        var repaired = coordinator.GetSnapshot("project")!;
        Assert.Null(repaired.PublicationError);
        Assert.NotNull(repaired.Publication);
        Assert.Equal(source, repaired.Publication.MarkdownPath);
        Assert.Equal(initial.PdfPath, repaired.Publication.PdfPath);
        Assert.NotEqual(initial.ContentHash, repaired.Publication.ContentHash);
        Assert.Equal(0, repaired.Publication.Revision);
        Assert.Empty(Directory.EnumerateDirectories(workspace));
        Assert.Equal(3, renders);

        coordinator.Queue("project");
        await coordinator.WaitForIdleAsync("project");
        Assert.Equal(3, renders);
    }

    [Fact]
    public async Task PassiveTabObserverCannotInterruptPublicationRendering()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "workspace")).FullName;
        await File.WriteAllTextAsync(Path.Combine(workspace, "Publikation.md"), "# Ergebnis\n\nGleichung und Begründung.");
        using var publications = new ScientificPublicationService(environment.Get<IScientificResearchRepository>(), async (path, token) =>
        {
            var target = Path.ChangeExtension(path, ".pdf");
            await File.WriteAllTextAsync(target, "%PDF-1.7\n" + new string(' ', 2048), token);
            return target;
        }, workspace);
        using var coordinator = new ScientificPresentationCoordinator(publications);
        coordinator.SnapshotPublished += (_, _) => throw new InvalidOperationException("Closed tab");
        coordinator.RegisterWorkspace("project", workspace);
        coordinator.Queue("project");
        await coordinator.WaitForIdleAsync("project");
        Assert.NotNull(coordinator.GetSnapshot("project")!.Publication);
        Assert.Null(coordinator.GetSnapshot("project")!.PublicationError);
    }
}
