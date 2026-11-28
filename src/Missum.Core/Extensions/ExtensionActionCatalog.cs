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
    // ExtensionChatMode.Webdesign is a legacy value: the former Webdesign mode is the Codex tool
    // "Webdesign" now, so no action is offered for it any more.
    private static readonly ExtensionChatMode[] AllModes =
        [ExtensionChatMode.General, ExtensionChatMode.Coding, ExtensionChatMode.ClaudeScience, ExtensionChatMode.ClaudeMeetings];

    /// <summary>
    /// Every mode except ChatGPT (General). ChatGPT stays limited to text answers, research and
    /// memory, so tools that create files or media, control the computer or build code are absent there.
    /// </summary>
    public static IReadOnlyList<ExtensionChatMode> ModesWithoutChatGpt { get; } =
        [ExtensionChatMode.Coding, ExtensionChatMode.ClaudeScience, ExtensionChatMode.ClaudeMeetings];

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

    /// <summary>Model tool name of Browser-Use (runs browser-use in the app, driven by the local model).</summary>
    public const string BrowserUseToolName = "browser.use";

    public static IReadOnlyDictionary<string, string> GroupLabels { get; } =
        Groups.ToDictionary(static group => group.GroupId, static group => group.Label, StringComparer.Ordinal);

    public static IReadOnlyList<ExtensionActionDescriptor> Actions { get; } =
    [
        Immediate(BuiltInActionIds.AttachFilesAndFolders, "Dateien und Ordner", "Dateien oder Ordner zum aktuellen Chat hinzufügen", "attachment", GroupFiles, 10, 10),
        Selectable(BuiltInActionIds.CreateDocument, "Dokumente erstellen", "Word, PDF, Tabellen und Präsentationen erstellen", "document", GroupFiles, 10, 20,
            [.. ModesWithoutChatGpt]),
        Immediate(BuiltInActionIds.ExportChatPdf, "Chat als PDF exportieren", "Den vollständigen Chat über den Speicherdialog als PDF exportieren", "pdf", GroupFiles, 10, 30),
        Selectable(BuiltInActionIds.PlanMode, "Planmodus", "Projekt schreibgeschützt analysieren und vor Änderungen einen umsetzbaren Plan abstimmen", "plan", GroupPlanning, 20, 10,
            [ExtensionChatMode.Coding, ExtensionChatMode.ClaudeScience]),
        // A per-session switch beside the selected tool: it shows the "Webseite" tab and gives Codex runs
        // the webdesign.serve/webdesign.preview tools (see BuiltInActionIds.Webdesign).
        Selectable(BuiltInActionIds.Webdesign, "Webdesign", "Webseite live im Tab ansehen, bearbeiten und hosten", "web", GroupIntegrations, 60, 20,
            [ExtensionChatMode.Coding]),
        Selectable(BuiltInActionIds.ComputerUse, "Computer-Use", "Windows und beliebige Programme sehen, verstehen und direkt mit Maus und Tastatur bedienen", "computer", GroupIntegrations, 60, 30,
            [.. ModesWithoutChatGpt]),
        Selectable(BuiltInActionIds.SelfImprovement, "Selbstverbesserung", "Den zugeordneten Anwendungsquellcode bauen und prüfen", "computer", GroupIntegrations, 60, 40,
            [.. ModesWithoutChatGpt]),
        Selectable(BuiltInActionIds.WebSearch, "Websuche", "Aktuelle Quellen im Web durchsuchen", "web", GroupResearch, 30, 10),
        Selectable(BuiltInActionIds.BrowserUse, "Browser-Use", "Webseiten in einem echten Browser öffnen, durchsuchen, anklicken, ausfüllen und Ergebnisse auslesen", "browser", GroupResearch, 30, 15),
        Selectable(BuiltInActionIds.DeepResearch, "Deep Research", "Eine mehrstufige, quellenbasierte Recherche starten", "research", GroupResearch, 30, 20),
        Selectable(BuiltInActionIds.Lean, "Lean", "Mathematische Aussagen mit Lean 4 und Mathlib formalisieren und Beweise prüfen", "lean", GroupResearch, 30, 30,
            [.. ModesWithoutChatGpt]),
        Selectable(BuiltInActionIds.ImageAnalysis, "Bild analysieren", "Bilder inhaltlich untersuchen", "image", GroupVisual, 40, 10),
        Selectable(BuiltInActionIds.VideoAnalysis, "Video analysieren", "Videodateien inhaltlich untersuchen", "video", GroupVisual, 40, 20),
        Selectable(BuiltInActionIds.GenerateImage, "Bild erstellen", "Ein Bild aus einer Beschreibung erzeugen", "image", GroupVisual, 40, 30,
            [.. ModesWithoutChatGpt]),
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
        Tool(BuiltInActionIds.SelfImprovement, "selfimprovement.build", "Startet einen isolierten Missum-Build mit Pflichtprüfungen. Liefert die Kandidatenkennung für die Statusabfrage.", 60, 64 * 1024,
            ExtensionToolRiskClass.Process, new { type = "object", properties = new { }, required = Array.Empty<string>(), additionalProperties = false }),
        Tool(BuiltInActionIds.SelfImprovement, "selfimprovement.status", "Ruft den Build-Bericht ab. Warte bei Bedarf auf Fortschritt oder lies mit logStep und lineOffset das Protokoll eines Prüfschritts.", 30, 64 * 1024,
            schema: new { type = "object", properties = new
            {
                candidateId = new { type = "string" },
                waitSeconds = new { type = "integer", minimum = 0, maximum = 20 },
                logStep = new { type = "string", @enum = new[] { "portable", "app-tests", "ai-stack", "portable-smoke", "gateway-smoke" } },
                lineOffset = new { type = "integer", minimum = 0, maximum = 100000 },
            }, required = new[] { "candidateId" }, additionalProperties = false }),
        Tool(BuiltInActionIds.SelfImprovement, "selfimprovement.apply", "Prüft einen vollständig geprüften Kandidaten erneut und übernimmt ihn. Liefert bei Abweichungen einen konkreten Fehlerbericht.", 3600, 64 * 1024,
            ExtensionToolRiskClass.Process, new { type = "object", properties = new { candidateId = new { type = "string" } }, required = new[] { "candidateId" }, additionalProperties = false }),
        Tool(BuiltInActionIds.BrowserUse, BrowserUseToolName, "Erledigt eine Webaufgabe in einem echten Chrome- oder Edge-Browser, den die lokale AI Schritt für Schritt bedient, und liefert das Ergebnis als Text. Ist Webdesign aktiv, arbeitet es im Tab Webseite des Projekts. Für Seiten, die geklickt, ausgefüllt oder durchsucht werden müssen.", 900, 256 * 1024,
            ExtensionToolRiskClass.Process, new
            {
                type = "object", properties = new
                {
                    task = new { type = "string", description = "Vollständige Aufgabe für den Browser in natürlicher Sprache, inklusive gewünschtem Ergebnis." },
                    startUrl = new { type = "string", description = "Optionale http(s)-Adresse, mit der der Browser beginnt." },
                    maxSteps = new { type = "integer", minimum = 1, maximum = 60, description = "Optionale Höchstzahl an Browser-Schritten (Standard 25)." },
                },
                required = new[] { "task" }, additionalProperties = false,
            }),
        Tool(BuiltInActionIds.ComputerUse, "computer.observe", "Beobachtet den Desktop: Bild mit nummerierten Bedienelementen (elements für elementIndex), erkanntes Programm (app, etwa Unreal Editor), Fensterliste und Beobachtungskennung.", 300, 4 * 1024 * 1024,
            schema: new { type = "object", properties = new { scope = new { type = "string", @enum = new[] { "desktop", "window" }, description = "desktop: gesamter Desktop mit Taskleiste (Standard); window: nur das Programmfenster." } }, required = Array.Empty<string>(), additionalProperties = false }),
        Tool(BuiltInActionIds.ComputerUse, "computer.act", "Führt eine Aktion zur aktuellen Beobachtung aus, bevorzugt per elementIndex, sonst x/y mit coordinateSpace. kind: click, double_click, right_click, type, set_value, keypress (ctrl+s, alt+f4, win, f5), scroll, drag, window, launch, wait. Danach erneut beobachten.", 300, 1024 * 1024,
            ExtensionToolRiskClass.LocalMutation, new
            {
                type = "object", properties = new
                {
                    observationId = new { type = "string" },
                    kind = new { type = "string", @enum = new[] { "click", "double_click", "right_click", "middle_click", "type", "set_value", "keypress", "scroll", "drag", "move", "inspect", "focus", "window", "launch", "wait" } },
                    elementIndex = new { type = "integer", minimum = 1, description = "Nummer eines markierten Elements aus elements; zuverlässiger als Koordinaten." },
                    x = new { type = "number", description = "Horizontale Bildkoordinate, wenn kein elementIndex passt." },
                    y = new { type = "number", description = "Vertikale Bildkoordinate, wenn kein elementIndex passt." },
                    endX = new { type = "number", description = "Zielpunkt bei drag." }, endY = new { type = "number", description = "Zielpunkt bei drag." },
                    imageWidth = new { type = "integer" }, imageHeight = new { type = "integer" },
                    text = new { type = "string", description = "Text für type oder set_value (set_value ersetzt den Inhalt des Elements)." },
                    key = new { type = "string", description = "Taste oder Kombination, z. B. enter, ctrl+s, alt+f4, ctrl+shift+esc, win, win+r, f2, f5." },
                    scrollY = new { type = "integer", description = "Vertikal scrollen: positiv nach unten, negativ nach oben; 120 je Mausradrastung." },
                    scrollX = new { type = "integer", description = "Horizontal scrollen: positiv nach rechts." },
                    windowId = new { type = "string", description = "Fenster aus windows für focus oder window." },
                    windowAction = new { type = "string", @enum = new[] { "focus", "maximize", "minimize", "restore", "close" } },
                    target = new { type = "string", description = "Für launch: Programm (notepad, calc, explorer, chrome, unreal), Pfad, Ordner, .uproject-Datei oder URL." },
                    arguments = new { type = "string", description = "Optionale Startparameter für launch." },
                    coordinateSpace = new { type = "string", @enum = new[] { "pixels", "normalized1000" },
                        description = "Pflicht bei x/y oder endX/endY. pixels: Bildpixel; normalized1000: je Bildachse 0 bis 999." },
                },
                required = new[] { "observationId", "kind" }, additionalProperties = false,
            }),
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
        int itemOrder,
        ExtensionChatMode[]? supportedModes = null) =>
        new(actionId, name, description, icon, group, groupOrder, itemOrder, [.. (supportedModes ?? AllModes)],
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
