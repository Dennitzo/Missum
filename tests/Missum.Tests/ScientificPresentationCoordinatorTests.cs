using System.Text;
using Missum.App.Services;
using Missum.Core.Research;

namespace Missum.Tests;

public sealed class ScientificPresentationCoordinatorTests
{
    [Fact]
    public async Task QueuedEditsPublishTheNewestFileAndRetainPreviousPdfDuringTheRender()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "workspace")).FullName;
        var manuscript = Path.Combine(workspace, "Publikation.md");
        await File.WriteAllTextAsync(manuscript, "# Ergebnis\n\nErster Stand.");
        var calls = 0;
        var enteredSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredLatest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowLatest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var publications = new ScientificPublicationService(environment.Get<IScientificResearchRepository>(), async (source, token) =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 2) { enteredSecond.TrySetResult(); await allowSecond.Task.WaitAsync(token); }
            if (call == 3) { enteredLatest.TrySetResult(); await allowLatest.Task.WaitAsync(token); }
            var target = Path.ChangeExtension(source, ".pdf");
            await File.WriteAllTextAsync(target, "%PDF-1.7\n" + new string(' ', 2048)
                + await File.ReadAllTextAsync(source, token), Encoding.ASCII, token);
            return target;
        }, workspace);
        using var coordinator = new ScientificPresentationCoordinator(publications);
        coordinator.RegisterWorkspace("project", workspace);
        try
        {
            coordinator.Queue("project");
            await coordinator.WaitForIdleAsync("project", timeout.Token);
            var initial = coordinator.GetSnapshot("project")!.Publication!;
            var originalPdf = await File.ReadAllBytesAsync(initial.PdfPath, timeout.Token);
            await File.WriteAllTextAsync(manuscript, "# Ergebnis\n\nZweiter Stand.", timeout.Token);
            coordinator.Queue("project");
            await enteredSecond.Task.WaitAsync(timeout.Token);
            await File.WriteAllTextAsync(manuscript, "# Ergebnis\n\nDritter und aktueller Stand.", timeout.Token);
            coordinator.Queue("project");
            allowSecond.TrySetResult();
            await enteredLatest.Task.WaitAsync(timeout.Token);
            Assert.Equal(initial, coordinator.GetSnapshot("project")!.Publication);
            Assert.Equal(originalPdf, await File.ReadAllBytesAsync(initial.PdfPath, timeout.Token));
            allowLatest.TrySetResult();
            await coordinator.WaitForIdleAsync("project", timeout.Token);
            var latest = coordinator.GetSnapshot("project")!;
            Assert.Null(latest.PublicationError);
            Assert.NotNull(latest.Publication);
            Assert.NotEqual(initial.ContentHash, latest.Publication.ContentHash);
            Assert.Equal(initial.PdfPath, latest.Publication.PdfPath);
            Assert.Contains("Dritter und aktueller Stand", await File.ReadAllTextAsync(latest.Publication.PdfPath, timeout.Token));
            Assert.Equal(3, calls);
            Assert.Empty(Directory.EnumerateDirectories(workspace));
        }
        finally
        {
            allowSecond.TrySetResult(); allowLatest.TrySetResult();
            await coordinator.WaitForIdleAsync("project", timeout.Token);
        }
    }

    [Fact]
    public async Task UnchangedPollingRetainsTheSnapshotWithoutRenderingOrNotifyingAgain()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "workspace")).FullName;
        await File.WriteAllTextAsync(Path.Combine(workspace, "Publikation.md"), "# Ergebnis\n\nDie Rechnung folgt hier.");
        var calls = 0;
        var notices = 0;
        using var publications = new ScientificPublicationService(environment.Get<IScientificResearchRepository>(), async (source, token) =>
        {
            Interlocked.Increment(ref calls);
            var target = Path.ChangeExtension(source, ".pdf");
            await File.WriteAllTextAsync(target, "%PDF-1.7\n" + new string(' ', 2048), token);
            return target;
        }, workspace);
        using var coordinator = new ScientificPresentationCoordinator(publications);
        coordinator.SnapshotPublished += (_, _) => Interlocked.Increment(ref notices);
        coordinator.RegisterWorkspace("project", workspace);
        coordinator.Queue("project");
        await coordinator.WaitForIdleAsync("project");
        var initial = coordinator.GetSnapshot("project");
        for (var index = 0; index < 4; index++)
        {
            coordinator.Queue("project");
            await coordinator.WaitForIdleAsync("project");
            Assert.Same(initial, coordinator.GetSnapshot("project"));
        }
        Assert.Equal(1, calls);
        Assert.Equal(1, notices);
    }
}
