using System.Text.Json;

namespace Missum.Ai.Contracts;

/// <summary>Shared schema validation for native workspace tools.</summary>
public static class WorkspaceTools
{
    public const string ImageInput = "image.input";
    public const string Open = "workspace.open";
    public const string WebdesignServe = "webdesign.serve";
    public const string WebdesignPreview = "webdesign.preview";
    public static bool IsLocal(string name) => name is ImageInput or Open or WebdesignServe or WebdesignPreview;
    public static bool IsWebdesign(string name) => name is WebdesignServe or WebdesignPreview;
    public static readonly string[] ServeOperations = ["status", "start", "stop", "restart", "log", "configure"];
    public static readonly string[] PreviewOperations = ["open", "capture", "console", "inspect", "annotations", "resolve"];
    public static readonly string[] Devices = ["smartphone", "tablet", "laptop", "free"];

    public static void Validate(string name, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("Werkzeugargumente müssen ein Objekt sein.");
        string[] allowed = name switch
        {
            ImageInput => ["operation", "path", "windowId"],
            Open => ["path"],
            WebdesignServe => ["operation", "root", "startPage", "command", "kind"],
            WebdesignPreview => ["operation", "path", "device", "selector", "ids", "note"],
            _ => throw new ArgumentException("Unbekanntes Workspace-Werkzeug."),
        };
        if (args.EnumerateObject().Any(p => !allowed.Contains(p.Name, StringComparer.Ordinal)))
            throw new ArgumentException("Unbekannte Werkzeugeigenschaft.");
        if (name == Open) { Text(args, "path", 1024); return; }
        if (name == WebdesignServe) { ValidateServe(args); return; }
        if (name == WebdesignPreview) { ValidatePreview(args); return; }
        var operation = Text(args, "operation", 20);
        if (operation == "file") Text(args, "path", 1024);
        else if (operation == "capture") Text(args, "windowId", 100);
        else if (operation != "windows") throw new ArgumentException("Ungültige Bildoperation.");
    }

    private static void ValidateServe(JsonElement args)
    {
        var operation = Text(args, "operation", 20);
        if (!ServeOperations.Contains(operation, StringComparer.Ordinal)) throw new ArgumentException("Ungültige Serve-Operation.");
        foreach (var field in new[] { "root", "startPage" }) Optional(args, field, 260);
        Optional(args, "command", 600);
        if (Optional(args, "kind", 20) is { } kind && kind is not ("auto" or "static" or "command"))
            throw new ArgumentException("Ungültige Serverart.");
    }

    private static void ValidatePreview(JsonElement args)
    {
        var operation = Text(args, "operation", 20);
        if (!PreviewOperations.Contains(operation, StringComparer.Ordinal)) throw new ArgumentException("Ungültige Vorschau-Operation.");
        if (operation == "open") Text(args, "path", 1024); else Optional(args, "path", 1024);
        if (operation == "inspect") Text(args, "selector", 500); else Optional(args, "selector", 500);
        if (Optional(args, "device", 20) is { } device && !Devices.Contains(device, StringComparer.Ordinal))
            throw new ArgumentException("Ungültiges Gerät.");
        Optional(args, "note", 2000);
        if (args.TryGetProperty("ids", out var ids))
        {
            if (ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() > 100
                || ids.EnumerateArray().Any(static id => id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var value) || value is < 1 or > 9999))
                throw new ArgumentException("Ungültiges Feld: ids.");
        }
        else if (operation == "resolve") throw new ArgumentException("resolve benötigt ids.");
    }

    private static string? Optional(JsonElement args, string name, int maximum) =>
        args.TryGetProperty(name, out _) ? Text(args, name, maximum) : null;

    private static string Text(JsonElement args, string name, int maximum) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { } text && !string.IsNullOrWhiteSpace(text) && text.Length <= maximum
            ? text : throw new ArgumentException($"Ungültiges Feld: {name}.");
}
