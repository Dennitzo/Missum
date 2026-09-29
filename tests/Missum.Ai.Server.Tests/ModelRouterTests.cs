using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class ModelRouterTests
{
    [Theory]
    [InlineData(RunMode.Auto)]
    [InlineData(RunMode.General)]
    public void EveryConversationModeRoutesToGeneral(RunMode mode)
    {
        using var context = new TestServerContext();
        var router = new ModelRouter(context.WrappedOptions);

        var selection = router.Select(CreateRequest(mode, "Projektdatei analysieren und Fehler beheben"));

        Assert.Equal("general", selection.Role);
        Assert.Equal(context.Options.GeneralModelId, selection.ModelId);
        Assert.Equal(context.Options.GeneralContextLength, selection.ContextLength);
    }

    [Fact]
    public void AutoDoesNotInferAnotherRoleFromCodeLikeAttachmentsOrCapabilities()
    {
        using var context = new TestServerContext();
        var router = new ModelRouter(context.WrappedOptions);
        var request = new RunRequest(
            MissumAiProtocol.Version,
            RunMode.Auto,
            [new RunMessage("user", [new ContentPart("file", FileName: "MainWindow.xaml")])],
            ClientCapabilities: ["documentIo"]);

        var selection = router.Select(request);

        Assert.Equal("general", selection.Role);
        Assert.Equal(context.Options.GeneralModelId, selection.ModelId);
    }

    [Fact]
    public void GeneralModeHonorsThePersistedClientSelection()
    {
        using var context = new TestServerContext();
        var router = new ModelRouter(context.WrappedOptions);
        var request = CreateRequest(RunMode.General, "Rekursion erklären") with
        {
            PreferredGeneralModelId = "gpt-oss-120b",
        };

        var selection = router.Select(request);

        Assert.Equal("general", selection.Role);
        Assert.Equal("gpt-oss-120b", selection.ModelId);
    }

    [Fact]
    public void RuntimeCatalogFallsBackWhenClientLeftModelChoiceOpen()
    {
        var installed = new ModelRuntimeStatus(
            "coding/DeepSeek-V4-Flash-Vision-Exp-UD-IQ1_S~current",
            "general",
            Downloaded: true,
            Loaded: false,
            State: "unloaded",
            ContextTokens: 1_048_576);

        var resolved = ModelRouter.ResolveRuntimeModel(
            [installed],
            new ModelSelection("gpt-oss-120b", "general", 131_072),
            hasExplicitSelection: false);

        Assert.Same(installed, resolved);
    }

    [Fact]
    public void RuntimeCatalogDoesNotReplaceAnExplicitMissingSelection()
    {
        var installed = new ModelRuntimeStatus(
            "coding/DeepSeek-V4-Flash-Vision-Exp-UD-IQ1_S~current",
            "general",
            Downloaded: true,
            Loaded: false,
            State: "unloaded",
            ContextTokens: 1_048_576);

        var resolved = ModelRouter.ResolveRuntimeModel(
            [installed],
            new ModelSelection("removed-model", "general", 131_072),
            hasExplicitSelection: true);

        Assert.Null(resolved);
    }

    private static RunRequest CreateRequest(RunMode mode, string text) => new(
        MissumAiProtocol.Version,
        mode,
        [new RunMessage("user", [new ContentPart("text", text)])]);
}
