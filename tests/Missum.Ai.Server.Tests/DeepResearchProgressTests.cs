using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class DeepResearchProgressTests
{
    private static readonly string[] HistoricalFindings = ["Original historical result"];
    private static readonly string[] ReportUncertainties = ["No independent confirmation"];

    [Fact]
    public async Task OperationSequencesUseDurableEventIdsAcrossConcurrentWritesAndRepositoryRecreation()
    {
        using var context = new TestServerContext();
        var notifier = new RunEventNotifier();
        var repository = new RunRepository(context.Database, notifier);
        var run = (await repository.CreateAsync(new(MissumAiProtocol.Version, RunMode.General,
            [new("user", [new("text", "Research")])]), null)).Snapshot.RunId;
        var first = await repository.AppendDeepResearchProgressAsync(Progress("operation-a"));
        await repository.AppendDeepResearchProgressAsync(Progress("operation-b"));
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => repository.AppendDeepResearchProgressAsync(Progress("operation-a"))));
        var recreated = new RunRepository(context.Database, notifier);
        var terminal = await recreated.AppendDeepResearchProgressAsync(Progress("operation-a") with { Status = "completed", Result = JsonSerializer.SerializeToElement(new { summary = "Actual report" }) });
        var journal = await recreated.GetEventsAfterAsync(run, 0);
        var progress = journal.Where(item => item.Type == RunEventTypes.DeepResearchProgress).ToArray();
        Assert.Equal(11, progress.Length);
        Assert.All(progress, item => Assert.Equal(item.Id, item.Data.GetProperty("sequence").GetInt64()));
        Assert.Equal(8, concurrent.Select(item => item.Id).Distinct().Count());
        Assert.True(terminal.Id > concurrent.Max(item => item.Id));
        Assert.True(first.Id < terminal.Id);
        Assert.Equal("Actual report", terminal.Data.GetProperty("result").GetProperty("summary").GetString());

        DeepResearchProgressEvent Progress(string operation) => new(operation, run, 0, "operation", "running", "Deep Research");
    }

    [Fact]
    public async Task AdditiveProgressLeavesExistingResearchEventsReadableAndUnchanged()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var run = (await repository.CreateAsync(new(MissumAiProtocol.Version, RunMode.General,
            [new("user", [new("text", "Research")])]), null)).Snapshot.RunId;
        var old = await repository.AppendEventAsync(run, RunEventTypes.ResearchReportCompleted, new { findings = HistoricalFindings });
        await repository.AppendDeepResearchProgressAsync(new("general-research", run, 0, "operation", "completed", "Deep Research", Text: "Original historical result"));
        var reloaded = Assert.Single(await repository.GetEventsAfterAsync(run, 0), item => item.Id == old.Id);
        Assert.Equal(old.Data.GetRawText(), reloaded.Data.GetRawText());
        Assert.Equal(old.Type, reloaded.Type);
    }

    [Fact]
    public void HumanReadableReportUsesOnlyActualResultContentAndKeepsUncertainty()
    {
        var result = JsonSerializer.SerializeToElement(new { summary = "The comparison remains incomplete.",
            findings = new[] { new { claim = "Actual finding", quote = "Exact original passage" } },
            uncertainties = ReportUncertainties });
        var report = RunProcessor.FormatDeepResearchReport(result);
        Assert.Contains("The comparison remains incomplete.", report, StringComparison.Ordinal);
        Assert.Contains("Actual finding", report, StringComparison.Ordinal);
        Assert.Contains("Exact original passage", report, StringComparison.Ordinal);
        Assert.Contains("No independent confirmation", report, StringComparison.Ordinal);
        Assert.DoesNotContain("\"findings\":", report, StringComparison.Ordinal);
    }
}
