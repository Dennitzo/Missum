using System.Text.Json;

namespace Missum.Core.Extensions;

public interface IExtensionActionCatalog
{
    IReadOnlyList<ExtensionActionDescriptor> GetActions(ExtensionChatMode mode);
    IReadOnlyList<ExtensionGroupDescriptor> GetGroups();
    bool TryGetAction(string actionId, out ExtensionActionDescriptor? descriptor);
    bool TryGetTool(string actionId, out ExtensionToolDescriptor? descriptor);
    bool TryGetTools(string actionId, out IReadOnlyList<ExtensionToolDescriptor>? descriptors);
    bool TryGetToolByModelName(string modelToolName, out ExtensionToolDescriptor? descriptor);
    void Register(ExtensionManifest manifest);
}

public sealed class ExtensionActionCatalog : IExtensionActionCatalog
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ExtensionActionDescriptor> _actions = new(StringComparer.Ordinal);
    // A selected action can unlock several model tools for the same run. The first entry keeps
    // working for integrations that were written against a single tool per action.
    private readonly Dictionary<string, List<ExtensionToolDescriptor>> _toolsByAction = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _actionsByToolName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExtensionGroupDescriptor> _groups =
        BuiltInExtensionCatalog.Groups.ToDictionary(static group => group.GroupId, StringComparer.Ordinal);

    public static ExtensionActionCatalog CreateWithBuiltIns()
    {
        var catalog = new ExtensionActionCatalog();
        catalog.RegisterDescriptors(BuiltInExtensionCatalog.Actions, BuiltInExtensionCatalog.Tools,
            BuiltInExtensionCatalog.Groups);
        return catalog;
    }

    public IReadOnlyList<ExtensionActionDescriptor> GetActions(ExtensionChatMode mode)
    {
        lock (_gate)
        {
            return _actions.Values
                .Where(action => action.SupportedChatModes.Contains(mode))
                .OrderBy(static action => action.GroupOrder)
                .ThenBy(static action => action.ItemOrder)
                .ThenBy(static action => action.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(CloneAction)
                .ToArray();
        }
    }

    public IReadOnlyList<ExtensionGroupDescriptor> GetGroups()
    {
        lock (_gate)
        {
            return _groups.Values
                .OrderBy(static group => group.GroupOrder)
                .ThenBy(static group => group.GroupId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public bool TryGetAction(string actionId, out ExtensionActionDescriptor? descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        lock (_gate)
        {
            if (_actions.TryGetValue(actionId, out var found))
            {
                descriptor = CloneAction(found);
                return true;
            }
        }

        descriptor = null;
        return false;
    }

    public bool TryGetTool(string actionId, out ExtensionToolDescriptor? descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        lock (_gate)
        {
            if (_toolsByAction.TryGetValue(actionId, out var found) && found.Count > 0)
            {
                descriptor = CloneTool(found[0]);
                return true;
            }
        }

        descriptor = null;
        return false;
    }

    public bool TryGetTools(string actionId, out IReadOnlyList<ExtensionToolDescriptor>? descriptors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        lock (_gate)
        {
            if (_toolsByAction.TryGetValue(actionId, out var found) && found.Count > 0)
            {
                descriptors = found.Select(CloneTool).ToArray();
                return true;
            }
        }

        descriptors = null;
        return false;
    }

    public bool TryGetToolByModelName(string modelToolName, out ExtensionToolDescriptor? descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelToolName);
        lock (_gate)
        {
            if (_actionsByToolName.TryGetValue(modelToolName, out var actionId)
                && _toolsByAction.TryGetValue(actionId, out var registered)
                && registered.FirstOrDefault(tool => string.Equals(tool.ModelToolName, modelToolName, StringComparison.Ordinal)) is { } found)
            {
                descriptor = CloneTool(found);
                return true;
            }
        }

        descriptor = null;
        return false;
    }

    public void Register(ExtensionManifest manifest)
    {
        ExtensionManifestValidator.Validate(manifest).ThrowIfInvalid();
        RegisterDescriptors(manifest.Actions, manifest.Tools, manifest.Groups ?? []);
    }

    private void RegisterDescriptors(
        IReadOnlyCollection<ExtensionActionDescriptor> actions,
        IReadOnlyCollection<ExtensionToolDescriptor> tools,
        IReadOnlyCollection<ExtensionGroupDescriptor> groups)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(groups);
        var groupCopies = groups.ToArray();

        var actionCopies = actions.Select(CloneAction).ToArray();
        var toolCopies = tools.Select(CloneTool).ToArray();
        var incomingActionIds = new HashSet<string>(actionCopies.Select(static action => action.ActionId), StringComparer.Ordinal);
        if (incomingActionIds.Count != actionCopies.Length)
            throw new InvalidDataException("Der Action-Katalog enthält doppelte Action-IDs.");
        var incomingToolNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in actionCopies)
        {
            var separator = action.ActionId.IndexOf('/');
            var expectedExtensionId = separator > 0 ? action.ActionId[..separator] : string.Empty;
            ExtensionManifestValidator.ValidateAction(action, expectedExtensionId).ThrowIfInvalid();
        }
        foreach (var tool in toolCopies)
        {
            if (!incomingActionIds.Contains(tool.ActionId))
                throw new InvalidDataException($"Tool '{tool.ModelToolName}' verweist nicht auf eine gleichzeitig registrierte Action.");
            if (!ExtensionIdentifiers.TryValidateModelToolName(tool.ModelToolName, out var toolError))
                throw new InvalidDataException(toolError);
            if (!incomingToolNames.Add(tool.ModelToolName))
                throw new InvalidDataException($"Modell-Toolname '{tool.ModelToolName}' ist im Registrierungssatz doppelt.");
            if (tool.InputSchema.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"InputSchema von '{tool.ModelToolName}' muss ein JSON-Objekt sein.");
            if (!Enum.IsDefined(tool.RiskClass))
                throw new InvalidDataException($"RiskClass von '{tool.ModelToolName}' ist unbekannt.");
        }

        lock (_gate)
        {
            var actionCollision = actionCopies.FirstOrDefault(action => _actions.ContainsKey(action.ActionId));
            if (actionCollision is not null)
                throw new InvalidDataException($"Action-ID '{actionCollision.ActionId}' ist bereits registriert.");
            var toolNameCollision = toolCopies.FirstOrDefault(tool => _actionsByToolName.ContainsKey(tool.ModelToolName));
            if (toolNameCollision is not null)
                throw new InvalidDataException($"Modell-Toolname '{toolNameCollision.ModelToolName}' ist bereits registriert.");

            foreach (var action in actionCopies) _actions.Add(action.ActionId, action);
            foreach (var tool in toolCopies)
            {
                if (!_toolsByAction.TryGetValue(tool.ActionId, out var registered))
                {
                    registered = [];
                    _toolsByAction.Add(tool.ActionId, registered);
                }
                registered.Add(tool);
                _actionsByToolName.Add(tool.ModelToolName, tool.ActionId);
            }
            // A package may rename or add a group. The first declaration wins so the built-in
            // purpose labels stay authoritative and a package cannot overwrite another package.
            foreach (var group in groupCopies.Where(static group => group is not null))
                _groups.TryAdd(group.GroupId, group);
        }
    }

    private static ExtensionActionDescriptor CloneAction(ExtensionActionDescriptor action) =>
        action with { SupportedChatModes = [.. action.SupportedChatModes] };

    private static ExtensionToolDescriptor CloneTool(ExtensionToolDescriptor tool) =>
        tool with { InputSchema = tool.InputSchema.Clone() };
}

public static class BuiltInExtensionCatalog
{
    private static readonly ExtensionChatMode[] AllModes =
        [ExtensionChatMode.General, ExtensionChatMode.Coding, ExtensionChatMode.ClaudeScience];

    // Purpose groups for the composer tool menu. Every tool sits in the group that matches what it
    // does, so an empty group simply disappears from the menu in the current chat mode.
    public const string GroupFiles = "files";
    public const string GroupPlanning = "planning";
    public const string GroupResearch = "research";
    public const string GroupVisual = "visual";
    public const string GroupSpeech = "speech";
    public const string GroupIntegrations = "integrations";

    public static IReadOnlyList<ExtensionGroupDescriptor> Groups { get; } =
    [
        new(GroupFiles, "Dateien und Dokumente", 10),
        new(GroupPlanning, "Planung und Kontext", 20),
        new(GroupResearch, "Recherche", 30),
        new(GroupVisual, "Bild und Video", 40),
        new(GroupSpeech, "Sprache und Audio", 50),
        new(GroupIntegrations, "Programmintegrationen", 60),
    ];

    public static IReadOnlyDictionary<string, string> GroupLabels { get; } =
        Groups.ToDictionary(static group => group.GroupId, static group => group.Label, StringComparer.Ordinal);

    public static IReadOnlyList<ExtensionActionDescriptor> Actions { get; } =
    [
        Immediate(BuiltInActionIds.AttachFilesAndFolders, "Dateien und Ordner", "Dateien oder Ordner zum aktuellen Chat hinzufügen", "attachment", GroupFiles, 10, 10),
        Selectable(BuiltInActionIds.CreateDocument, "Dokumente erstellen", "Word, PDF, Tabellen und Präsentationen erstellen", "document", GroupFiles, 10, 20),
        Immediate(BuiltInActionIds.ExportChatPdf, "Chat als PDF exportieren", "Den vollständigen Chat über den Speicherdialog als PDF exportieren", "pdf", GroupFiles, 10, 30),
        SelectableCoding(BuiltInActionIds.PlanMode, "Planmodus", "Projekt schreibgeschützt analysieren und vor Änderungen einen umsetzbaren Plan abstimmen", "plan", GroupPlanning, 20, 10),
        Selectable(BuiltInActionIds.WebSearch, "Websuche", "Aktuelle Quellen im Web durchsuchen", "web", GroupResearch, 30, 10),
        Selectable(BuiltInActionIds.DeepResearch, "Deep Research", "Eine mehrstufige, quellenbasierte Recherche starten", "research", GroupResearch, 30, 20),
        Selectable(BuiltInActionIds.Lean, "Lean", "Mathematische Aussagen mit Lean 4 und Mathlib formalisieren und Beweise prüfen", "lean", GroupResearch, 30, 30),
        Selectable(BuiltInActionIds.ImageAnalysis, "Bild analysieren", "Bilder inhaltlich untersuchen", "image", GroupVisual, 40, 10),
        Selectable(BuiltInActionIds.VideoAnalysis, "Video analysieren", "Videodateien inhaltlich untersuchen", "video", GroupVisual, 40, 20),
        Selectable(BuiltInActionIds.GenerateImage, "Bild erstellen", "Ein Bild aus einer Beschreibung erzeugen", "image", GroupVisual, 40, 30),
        Selectable(BuiltInActionIds.ReadAloud, "Vorlesen", "Text mit lokaler Sprachausgabe vorlesen", "speech", GroupSpeech, 50, 10),
        Selectable(BuiltInActionIds.Translate, "Übersetzen", "Text in eine andere Sprache übertragen", "translate", GroupSpeech, 50, 20),
        Immediate(BuiltInActionIds.LiveCaptions, "Live-Untertitel", "Untertitel für laufende Sprache ein- oder ausschalten", "captions", GroupSpeech, 50, 30),
        Selectable(BuiltInActionIds.CreateAudiobook, "Hörbuch erstellen", "Text als gegliedertes Hörbuch erzeugen", "audiobook", GroupSpeech, 50, 40),
        Selectable(BuiltInActionIds.AudioAnalysis, "Audio analysieren", "Audiodateien inhaltlich untersuchen", "audio", GroupSpeech, 50, 50),
    ];

    public static IReadOnlyList<ExtensionToolDescriptor> Tools { get; } =
    [
        Tool(BuiltInActionIds.WebSearch, "web.search", "Aktuelle Quellen im Web durchsuchen", 120, 2 * 1024 * 1024),
        Tool(BuiltInActionIds.DeepResearch, "research.deep", "Mehrstufige Recherche mit Quellen ausführen", 1_800, 16 * 1024 * 1024),
        Tool(BuiltInActionIds.ImageAnalysis, "media.image.analyze", "Bildinhalte analysieren", 600, 16 * 1024 * 1024),
        Tool(BuiltInActionIds.AudioAnalysis, "media.audio.analyze", "Audioinhalte analysieren", 600, 16 * 1024 * 1024),
        Tool(BuiltInActionIds.VideoAnalysis, "media.video.analyze", "Videoinhalte analysieren", 600, 16 * 1024 * 1024),
        Tool(BuiltInActionIds.CreateDocument, "document.create", "Ein Dokument erzeugen", 600, 16 * 1024 * 1024),
        Tool(BuiltInActionIds.GenerateImage, "image.generate", "Ein Bild erzeugen", 600, 16 * 1024 * 1024),
        Tool(BuiltInActionIds.CreateAudiobook, "audiobook.create", "Ein Hörbuch erzeugen", 1_800, 32 * 1024 * 1024),
        Tool(BuiltInActionIds.Translate, "speech.translate", "Text übersetzen", 300, 4 * 1024 * 1024),
        Tool(BuiltInActionIds.ReadAloud, "speech.synthesize", "Text lokal vorlesen", 300, 4 * 1024 * 1024),
    ];

    private static ExtensionActionDescriptor Immediate(
        string actionId,
        string name,
        string description,
        string icon,
        string group,
        int groupOrder,
        int itemOrder) =>
        new(actionId, name, description, icon, group, groupOrder, itemOrder, [.. AllModes],
            ExtensionActionKind.Immediate, ExtensionSelectionBehavior.None);

    private static ExtensionActionDescriptor Selectable(
        string actionId,
        string name,
        string description,
        string icon,
        string group,
        int groupOrder,
        int itemOrder) =>
        new(actionId, name, description, icon, group, groupOrder, itemOrder, [.. AllModes],
            ExtensionActionKind.SelectableTool, ExtensionSelectionBehavior.Toggle);

    private static ExtensionActionDescriptor SelectableCoding(
        string actionId,
        string name,
        string description,
        string icon,
        string group,
        int groupOrder,
        int itemOrder) =>
        new(actionId, name, description, icon, group, groupOrder, itemOrder, [ExtensionChatMode.Coding],
            ExtensionActionKind.SelectableTool, ExtensionSelectionBehavior.Toggle);

    private static ExtensionToolDescriptor Tool(
        string actionId,
        string modelToolName,
        string description,
        int timeoutSeconds,
        int maximumOutputBytes,
        ExtensionToolRiskClass riskClass = ExtensionToolRiskClass.ReadOnly,
        object? schema = null) =>
        new(actionId, modelToolName, description,
            JsonSerializer.SerializeToElement(schema ?? new { type = "object", additionalProperties = true }),
            timeoutSeconds, maximumOutputBytes, riskClass);
}
