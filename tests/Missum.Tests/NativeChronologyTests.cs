using System.Text.Json;
using Missum.App.Pages;

namespace Missum.Tests;

public sealed class NativeChronologyTests
{
    private static readonly string[] ThreeReceipts = ["first", "second", "third"];
    private static readonly string[] ProvisionalLiveReceipts = ["persisted-zero", "live-one", "live-two"];
    private static readonly string[] PersistedLiveReceipts = ["persisted-zero", "live-two", "live-one"];
    private static readonly string[] OverlappingPageReceipts = ["step-2", "step-3", "step-4", "step-5"];
    private static readonly string[] GappedSnapshotReceipts = ["step-2", "step-5", "step-8", "step-9", "step-10"];
    private static readonly string[] LegacyReplacementReceipts = ["new-first", "new-second", "new-third"];

    private static JsonElement Step(string id, int offset = 0, string? output = null) => JsonSerializer.SerializeToElement(new
    { id, tool = "coding.read", status = "completed", contentOffset = offset, outputJson = output });

    private static JsonElement Message(IEnumerable<JsonElement> steps) => JsonSerializer.SerializeToElement(new
    { id = "message", content = "Vollständige Antwort.", toolSteps = steps.ToArray() });

    private static string[] Ids(NativeAssistantPage.NativeMessageProjection projection) => projection.Steps
        .Select(step => step.GetProperty("id").GetString()!).ToArray();

    [Fact]
    public void DelayedReceiptsUseDatabaseOrdinalsInsteadOfArrivalOrTextOffsets()
    {
        var projection = new NativeAssistantPage.NativeMessageProjection(Message([]), 0);
        projection.PutStep(Step("third", 0), 2);
        projection.PutStep(Step("first", 20), 0);
        projection.PutStep(Step("second", 10), 1);

        Assert.Equal(ThreeReceipts, Ids(projection));
        Assert.Equal(0, projection.FirstOrdinal);
        Assert.Equal(3, projection.TotalSteps);
        Assert.Equal("Vollständige Antwort.", projection.Content);
    }

    [Fact]
    public void DetailHydrationNeverMovesItsReceipt()
    {
        var projection = new NativeAssistantPage.NativeMessageProjection(Message([Step("first"), Step("second")]), 10);
        var detail = Step("first", 500, "Vollständiger Dateiinhalt");
        projection.PutStep(detail);
        projection.PutStep(Step("third"), 12);
        projection.PutStep(detail);

        Assert.Equal(ThreeReceipts, Ids(projection));
        Assert.True(detail.Equals(projection.Steps[0]));
        Assert.Equal(10, projection.FirstOrdinal);
        Assert.Equal(13, projection.TotalSteps);
    }

    [Fact]
    public void UnknownLiveReceiptsStayInArrivalOrderUntilTheirDatabasePositionArrives()
    {
        var projection = new NativeAssistantPage.NativeMessageProjection(Message([]), 0);
        projection.PutStep(Step("live-one"));
        projection.PutStep(Step("live-two"));
        projection.PutStep(Step("persisted-zero"), 0);
        Assert.Equal(ProvisionalLiveReceipts, Ids(projection));

        projection.PutStep(Step("live-two"), 1);
        projection.PutStep(Step("live-one"), 2);
        projection.PutStep(Step("live-two", output: "Details"));
        Assert.Equal(PersistedLiveReceipts, Ids(projection));
        Assert.Equal("Details", projection.Steps[1].GetProperty("outputJson").GetString());
    }

    [Fact]
    public void OutOfOrderStepRehydrationCanBeTrimmedAndPagedAgainWithoutReorderingTail()
    {
        var projection = new NativeAssistantPage.NativeMessageProjection(Message([]), 0);
        for (var ordinal = 0; ordinal < 300; ordinal++) projection.PutStep(Step("step-" + ordinal), ordinal);
        Assert.Equal(172, projection.TrimHead(128));
        projection.PutStep(Step("step-20", output: "Verspätetes Detail"), 20);
        Assert.Equal("step-20", Ids(projection)[0]);
        Assert.Equal("step-299", Ids(projection)[^1]);
        Assert.Equal(300, projection.TotalSteps);

        projection.Prepend(Enumerable.Range(0, 20).Select(ordinal => Step("step-" + ordinal)));
        Assert.Equal(0, projection.FirstOrdinal);
        Assert.Equal("step-20", Ids(projection)[20]);
        Assert.Equal("Verspätetes Detail", projection.Steps[20].GetProperty("outputJson").GetString());
        Assert.Equal(300, projection.TotalSteps);
    }

    [Fact]
    public void OverlappingPageKeepsHydratedLiveReceiptAndIsOrderIdempotent()
    {
        var hydrated = Step("step-4", output: "Geladener Inhalt");
        var projection = new NativeAssistantPage.NativeMessageProjection(Message([hydrated, Step("step-5")]), 4);
        var page = Enumerable.Range(2, 3).Select(ordinal => Step("step-" + ordinal)).ToArray();
        projection.Prepend(page);
        var version = projection.Version;
        projection.Prepend(page);

        Assert.Equal(OverlappingPageReceipts, Ids(projection));
        Assert.Equal(version, projection.Version);
        Assert.True(JsonElement.DeepEquals(hydrated, projection.Steps[2]));
        Assert.Equal(2, projection.FirstOrdinal);
        Assert.Equal(6, projection.TotalSteps);
    }

    [Fact]
    public void SnapshotAdoptionPreservesActualEarlierOrdinalsIncludingLoadedGaps()
    {
        var projection = new NativeAssistantPage.NativeMessageProjection(Message([Step("step-8"), Step("step-9")]), 8);
        projection.PutStep(Step("step-2"), 2);
        projection.Adopt(Message([Step("step-9"), Step("step-10")]), 9, preserveEarlier: true);
        projection.PutStep(Step("step-5"), 5);

        Assert.Equal(GappedSnapshotReceipts, Ids(projection));
        Assert.Equal(2, projection.FirstOrdinal);
        Assert.Equal(11, projection.TotalSteps);
    }

    [Fact]
    public void LegacyStepArrayEstablishesItsOwnStableSequence()
    {
        var projection = new NativeAssistantPage.NativeMessageProjection(Message([Step("old")]), 0);
        projection.PutStep(Step("late"), 50);
        projection.Apply(JsonSerializer.SerializeToElement(new { toolSteps = new[] { Step("new-first"), Step("new-second") } }));
        projection.PutStep(Step("new-first", output: "Details"));
        projection.PutStep(Step("new-third"), 2);

        Assert.Equal(LegacyReplacementReceipts, Ids(projection));
        Assert.Equal(0, projection.FirstOrdinal);
    }
}
