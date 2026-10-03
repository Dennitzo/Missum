using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Research;

namespace Missum.Tests;

public sealed class ScientificPresentationCoordinatorTests
{
    [Fact]
    public async Task SupersededRenderKeepsPreviousPdfPendingUntilCurrentRevisionIsPublished()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var session = await environment.Get<IChatRepository>().CreateSessionAsync("PDF während Forschung aktualisieren");
        var repository = environment.Get<IScientificResearchRepository>();
        var now = DateTimeOffset.UtcNow;
        var project = new ScientificResearchProject("research-coordinator-superseded", session.Id, "scientificEvidence",
            "Forschungsfrage", "Forschungsfrage", "sandboxResearch", "multiPath", "active", 1, 1, now, now);
        await repository.UpsertProjectAsync(project);
        var renderCalls = 0;
        var replacementEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowReplacement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var publications = new ScientificPublicationService(repository, async (source, token) =>
        {
            var invocation = Interlocked.Increment(ref renderCalls);
            if (invocation == 2)
                await repository.UpsertProjectAsync(project with { Revision = 3, UpdatedAt = now.AddSeconds(2) }, token);
            if (invocation == 3)
            {
                replacementEntered.TrySetResult();
                await allowReplacement.Task.WaitAsync(token);
            }
            var pdf = Path.ChangeExtension(source, ".pdf");
            await File.WriteAllBytesAsync(pdf, Encoding.ASCII.GetBytes("%PDF-1.7\n" + new string(' ', 2048) + "\n%%EOF\n"), token);
            return pdf;
        }, Path.Combine(environment.Directory, "publications"));
        using var simulations = new ScientificSimulationService(repository, new UnavailableSandbox());
        using var coordinator = new ScientificPresentationCoordinator(publications, simulations);
        // Coordination is asserted with barriers, not a wall-clock latency target.
        // Allow the repository work to run under the full parallel integration suite.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            coordinator.Queue(project.Id);
            await coordinator.WaitForIdleAsync(project.Id, timeout.Token);
            var initial = Assert.IsType<ScientificPresentationSnapshot>(coordinator.GetSnapshot(project.Id));
            var previous = Assert.IsType<ScientificPublicationArtifact>(initial.Publication);
            await repository.UpsertProjectAsync(project with { Revision = 2, UpdatedAt = now.AddSeconds(1) }, timeout.Token);
            coordinator.Queue(project.Id);
            await replacementEntered.Task.WaitAsync(timeout.Token);

            var pending = Assert.IsType<ScientificPresentationSnapshot>(coordinator.GetSnapshot(project.Id));
            Assert.Equal(previous, pending.Publication);
            Assert.Contains("noch nicht aktuell", pending.PublicationError);
            var idle = coordinator.WaitForIdleAsync(project.Id, timeout.Token);
            Assert.False(idle.IsCompleted);
            allowReplacement.TrySetResult();
            await idle;

            var final = Assert.IsType<ScientificPresentationSnapshot>(coordinator.GetSnapshot(project.Id));
            Assert.Null(final.PublicationError);
            var current = Assert.IsType<ScientificPublicationArtifact>(final.Publication);
            Assert.Equal(3, current.Revision);
            Assert.NotEqual(previous.PdfPath, current.PdfPath);
            Assert.True(File.Exists(current.PdfPath));
            Assert.Equal(3, Volatile.Read(ref renderCalls));
        }
        finally
        {
            allowReplacement.TrySetResult();
            coordinator.Dispose();
            await WaitForIdleAsync(coordinator, project.Id);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PdfFailureWithOlderPublicationBecomesIdleAndExternalRetryRecovers(bool canonical)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var session = await environment.Get<IChatRepository>().CreateSessionAsync("Publikationsfehler prüfen");
        var repository = environment.Get<IScientificResearchRepository>();
        var now = DateTimeOffset.UtcNow;
        var project = new ScientificResearchProject("research-coordinator-retry", session.Id, "scientificEvidence",
            "Forschungsfrage", "Forschungsfrage", "sandboxResearch", "multiPath", "active", canonical ? 2 : 1, 1, now, now);
        await repository.UpsertProjectAsync(project);
        if (canonical) await WriteCanonicalSectionAsync(repository, project.Id, 1);
        var failRenderer = false;
        var recoveryFailures = 0;
        var renderCalls = 0;
        using var publications = new ScientificPublicationService(repository, async (source, token) =>
        {
            Interlocked.Increment(ref renderCalls);
            if (Volatile.Read(ref failRenderer) || Interlocked.Exchange(ref recoveryFailures, 0) > 0)
                throw new IOException("publication-renderer-offline");
            var pdf = Path.ChangeExtension(source, ".pdf");
            await File.WriteAllBytesAsync(pdf, Encoding.ASCII.GetBytes("%PDF-1.7\n" + new string(' ', 2048) + "\n%%EOF\n"), token);
            return pdf;
        }, Path.Combine(environment.Directory, "publications"));
        using var simulations = new ScientificSimulationService(repository, new UnavailableSandbox());
        var delays = new ConcurrentQueue<TimeSpan>();
        using var coordinator = WithoutWaiting(publications, simulations, delays);
        try
        {
            coordinator.Queue(project.Id);
            await WaitForIdleAsync(coordinator, project.Id);
            var initial = Assert.IsType<ScientificPresentationSnapshot>(coordinator.GetSnapshot(project.Id));
            var firstPdf = Assert.IsType<ScientificPublicationArtifact>(initial.Publication);
            var initialBytes = await File.ReadAllBytesAsync(firstPdf.PdfPath);
            Assert.Null(initial.PublicationError);
            Assert.Equal(1, Volatile.Read(ref renderCalls));

            // Simulation discovery remains independent of publication revision and rendering.
            if (canonical) await WriteCanonicalSectionAsync(repository, project.Id, 2);
            else await repository.UpsertProjectAsync(project with { Revision = 2, UpdatedAt = now.AddSeconds(1) });
            Volatile.Write(ref failRenderer, true);
            coordinator.Queue(project.Id);
            await WaitForIdleAsync(coordinator, project.Id);

            var failed = Assert.IsType<ScientificPresentationSnapshot>(coordinator.GetSnapshot(project.Id));
            Assert.Equal(firstPdf, failed.Publication);
            Assert.Equal(initialBytes, await File.ReadAllBytesAsync(firstPdf.PdfPath));
            Assert.Contains("publication-renderer-offline", failed.PublicationError);
            Assert.Equal("failed", failed.Simulation?.Status);
            Assert.Contains("Python sandbox unavailable", failed.Simulation?.Detail);
            Assert.Null(failed.SimulationError);
            // The initial attempt and two automatic technical retries all fail,
            // then coordination becomes idle instead of retrying forever.
            Assert.Equal(4, Volatile.Read(ref renderCalls));
            Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(1) }, delays.ToArray());
            Assert.True(coordinator.WaitForIdleAsync(project.Id).IsCompletedSuccessfully);

            // An explicit update starts a fresh bounded cycle, even if its
            // first technical attempt is still temporarily unavailable.
            Volatile.Write(ref failRenderer, false);
            Volatile.Write(ref recoveryFailures, 1);
            coordinator.Queue(project.Id);
            await WaitForIdleAsync(coordinator, project.Id);

            var recovered = Assert.IsType<ScientificPresentationSnapshot>(coordinator.GetSnapshot(project.Id));
            var currentPdf = Assert.IsType<ScientificPublicationArtifact>(recovered.Publication);
            Assert.Equal(2, currentPdf.Revision);
            Assert.NotEqual(firstPdf.PdfPath, currentPdf.PdfPath);
            Assert.True(File.Exists(currentPdf.PdfPath));
            Assert.Null(recovered.PublicationError);
            Assert.Null(recovered.SimulationError);
            Assert.Equal(6, Volatile.Read(ref renderCalls));
            Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1) }, delays.ToArray());
        }
        finally
        {
            coordinator.Dispose();
            await WaitForIdleAsync(coordinator, project.Id);
        }
    }

    [Fact]
    public async Task ATransientTechnicalPdfFailureRecoversAutomaticallyWithoutAnotherQueue()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var session = await environment.Get<IChatRepository>().CreateSessionAsync("Technische PDF-Wiederherstellung");
        var repository = environment.Get<IScientificResearchRepository>();
        var now = DateTimeOffset.UtcNow;
        var project = new ScientificResearchProject("research-coordinator-transient", session.Id, "scientificEvidence",
            "Forschungsfrage", "Forschungsfrage", "sandboxResearch", "multiPath", "active", 2, 1, now, now);
        await repository.UpsertProjectAsync(project);
        await WriteCanonicalSectionAsync(repository, project.Id, 1);
        var renderCalls = 0;
        using var publications = new ScientificPublicationService(repository, async (source, token) =>
        {
            if (Interlocked.Increment(ref renderCalls) == 2) throw new IOException("temporary-renderer-unavailable");
            var pdf = Path.ChangeExtension(source, ".pdf");
            await File.WriteAllBytesAsync(pdf, Encoding.ASCII.GetBytes("%PDF-1.7\n" + new string(' ', 2048) + "\n%%EOF\n"), token);
            return pdf;
        }, Path.Combine(environment.Directory, "publications"));
        using var simulations = new ScientificSimulationService(repository, new UnavailableSandbox());
        var delays = new ConcurrentQueue<TimeSpan>();
        using var coordinator = WithoutWaiting(publications, simulations, delays);
        try
        {
            coordinator.Queue(project.Id);
            await WaitForIdleAsync(coordinator, project.Id);
            var old = Assert.IsType<ScientificPublicationArtifact>(coordinator.GetSnapshot(project.Id)?.Publication);
            await WriteCanonicalSectionAsync(repository, project.Id, 2);
            coordinator.Queue(project.Id);
            await WaitForIdleAsync(coordinator, project.Id);
            var recovered = Assert.IsType<ScientificPresentationSnapshot>(coordinator.GetSnapshot(project.Id));
            Assert.Equal(2, recovered.Publication?.Revision);
            Assert.NotEqual(old.PdfPath, recovered.Publication?.PdfPath);
            Assert.Null(recovered.PublicationError);
            Assert.Equal(3, Volatile.Read(ref renderCalls));
            Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1) }, delays.ToArray());
        }
        finally { coordinator.Dispose(); await WaitForIdleAsync(coordinator, project.Id); }
    }

    [Fact]
    public async Task CanonicalContentFailureRetainsTheOldPdfWithoutTechnicalRetries()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var session = await environment.Get<IChatRepository>().CreateSessionAsync("Formelfehler gezielt korrigieren");
        var repository = environment.Get<IScientificResearchRepository>();
        var now = DateTimeOffset.UtcNow;
        var project = new ScientificResearchProject("research-coordinator-content", session.Id, "scientificEvidence",
            "Forschungsfrage", "Forschungsfrage", "sandboxResearch", "multiPath", "active", 2, 1, now, now);
        await repository.UpsertProjectAsync(project);
        await WriteCanonicalSectionAsync(repository, project.Id, 1);
        var renderCalls = 0;
        using var publications = new ScientificPublicationService(repository, async (source, token) =>
        {
            if (Interlocked.Increment(ref renderCalls) > 1) throw new IOException("KaTeX: ungültige Formel im Abschnitt Grundlagen.");
            var pdf = Path.ChangeExtension(source, ".pdf");
            await File.WriteAllBytesAsync(pdf, Encoding.ASCII.GetBytes("%PDF-1.7\n" + new string(' ', 2048) + "\n%%EOF\n"), token);
            return pdf;
        }, Path.Combine(environment.Directory, "publications"));
        using var simulations = new ScientificSimulationService(repository, new UnavailableSandbox());
        var delays = new ConcurrentQueue<TimeSpan>();
        using var coordinator = WithoutWaiting(publications, simulations, delays);
        try
        {
            coordinator.Queue(project.Id);
            await WaitForIdleAsync(coordinator, project.Id);
            var old = Assert.IsType<ScientificPublicationArtifact>(coordinator.GetSnapshot(project.Id)?.Publication);
            var oldBytes = await File.ReadAllBytesAsync(old.PdfPath);
            await WriteCanonicalSectionAsync(repository, project.Id, 2);
            coordinator.Queue(project.Id);
            await WaitForIdleAsync(coordinator, project.Id);
            var failed = Assert.IsType<ScientificPresentationSnapshot>(coordinator.GetSnapshot(project.Id));
            Assert.Equal(old, failed.Publication);
            Assert.Equal(oldBytes, await File.ReadAllBytesAsync(old.PdfPath));
            Assert.Contains("KaTeX", failed.PublicationError, StringComparison.Ordinal);
            Assert.Contains("grundlagen", failed.PublicationError, StringComparison.Ordinal);
            Assert.Equal(2, Volatile.Read(ref renderCalls));
            Assert.Empty(delays);
        }
        finally { coordinator.Dispose(); await WaitForIdleAsync(coordinator, project.Id); }
    }

    private static ScientificPresentationCoordinator WithoutWaiting(ScientificPublicationService publications,
        ScientificSimulationService simulations, ConcurrentQueue<TimeSpan> delays) => new(publications, simulations, (duration, token) =>
        {
            token.ThrowIfCancellationRequested();
            delays.Enqueue(duration);
            return Task.CompletedTask;
        });

    private static async Task WriteCanonicalSectionAsync(IScientificResearchRepository repository, string projectId, long revision)
    {
        var states = Assert.IsAssignableFrom<IScientificResearchStateRepository>(repository);
        var result = await states.ApplyWorkingUpdateAsync(projectId, "coordinator-section-" + revision, null, "Fachliche Forschungsfrage",
            [new("grundlagen", "section", revision - 1, JsonSerializer.SerializeToElement(new
            {
                title = "Grundlagen", contentMarkdown = "Fachliche Ausgangsannahme $x=" + revision + "$.",
                status = "unresolved", order = 10,
            }))]);
        Assert.True(result.Success);
        Assert.Equal(revision, result.State.PublicationRevision);
    }

    private static async Task WaitForIdleAsync(ScientificPresentationCoordinator coordinator, string projectId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await coordinator.WaitForIdleAsync(projectId, timeout.Token);
    }

    private sealed class UnavailableSandbox : IResearchSandboxService
    {
        public Task<ResearchSandboxLayout> EnsureProjectAsync(string projectId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Python sandbox unavailable in coordinator fixture.");
        public Task<ResearchSandboxRuntimeStatus> PrepareRuntimeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResearchSandboxFileChange> WriteTextAsync(string projectId, string relativePath, string content,
            string? expectedSha256 = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RestoreChangeSetAsync(string projectId, string changeSetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResearchSandboxRunResult> RunPythonAsync(string projectId, string relativeScriptPath,
            IReadOnlyList<string>? arguments = null, int timeoutSeconds = 7200, string? relativeWorkingDirectory = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ArchiveProjectAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RestoreProjectAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
