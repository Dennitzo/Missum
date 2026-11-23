using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Missum.UnrealAdapter;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Action = "com.missum.unreal/edit";
    // Das Gateway erlaubt höchstens 16 Modellwerkzeuge pro Aktion. Deshalb bündelt das
    // Manifest die Editorbefehle in Gruppenwerkzeuge; hier steht die Übersetzung zurück.
    private static readonly Dictionary<string, string[]> GroupedCommands =
        new Dictionary<string, string[]>
        {
            ["unreal.level"] = ["list", "create", "open", "save", "read"],
            ["unreal.asset"] = ["list", "read", "import"],
            ["unreal.material"] = ["create", "read", "edit", "assign"],
            ["unreal.actor"] = ["place", "read", "set", "remove", "verify"],
            ["unreal.camera"] = ["create", "set", "frame"],
            ["unreal.light"] = ["create", "set", "read"],
            ["unreal.sky"] = ["create", "read"],
            ["unreal.class"] = ["search"],
            ["unreal.blueprint"] = ["create", "read", "edit", "compile", "save", "template"],
        };

    // Gebündelte Werkzeuge tragen die Teiloperation im Feld operation; für edit steht die
    // eigentliche Teiloperation im Feld edit. Der Adapter übersetzt das in den granularen
    // Editorbefehl und entfernt die Steuerfelder, damit die Laufzeit sie nicht als Argument sieht.
    private static readonly string[] MaterialValueFields =
        ["baseColor", "emissiveColor", "metallic", "roughness", "specular", "opacity", "emissiveIntensity"];

    private static string GroupedCommand(string tool, JsonObject args, string[] verbs)
    {
        var verb = String(args, "operation");
        if (Array.IndexOf(verbs, verb) < 0)
            throw new ToolException("unreal.operation_unknown", $"Die Operation '{verb}' gehört nicht zu {tool}.");
        var prefix = tool[(tool.IndexOf('.') + 1)..];
        if (verb != "edit")
        {
            args.Remove("operation");
            return prefix + "." + verb;
        }
        var sub = String(args, "edit");
        // Models send material values directly (operation=edit, baseColor, roughness ...) without
        // the edit field; that is the "values" sub-operation, not an unknown request.
        if (sub.Length == 0 && prefix == "material"
            && MaterialValueFields.Any(field => args[field] is not null))
            sub = "values";
        if (sub.Length == 0)
            throw new ToolException("unreal.operation_unknown", prefix == "material"
                ? "Für unreal.material mit operation=edit fehlt edit (parameter, expression.add, …) oder ein Wert wie baseColor, metallic, roughness."
                : $"Für {tool} mit operation=edit ist das Feld edit erforderlich.");
        if (prefix == "blueprint")
        {
            var command = sub switch
            {
                "variable.add" => "blueprint.variable.add",
                "variable.remove" => "blueprint.variable.remove",
                "graph.create" => "blueprint.graph.create",
                "graph.remove" => "blueprint.graph.remove",
                "component.add" => "blueprint.component.add",
                "component.remove" => "blueprint.component.remove",
                "component.set" => "blueprint.component.set",
                "node.add" => "blueprint.node.add",
                "node.remove" => "blueprint.node.remove",
                "pin.connect" => "blueprint.pin.connect",
                "pin.disconnect" => "blueprint.pin.disconnect",
                _ => throw new ToolException("unreal.operation_unknown", "Die Blueprint-Operation wird nicht unterstützt."),
            };
            args.Remove("operation");
            args.Remove("edit");
            return command;
        }
        // Material: die Teiloperation wird zum Argument operation des Editorbefehls.
        args["operation"] = JsonValue.Create(sub);
        return "material.edit";
    }
    private static string Workspace => Path.TrimEndingDirectorySeparator(Path.GetFullPath(
        Environment.GetEnvironmentVariable("ASSISTANT_EXTENSION_WORKSPACE_ROOT")
        ?? throw new ToolException("unreal.workspace_missing", "Wähle zuerst einen vorhandenen Projektordner.")));
    private static string Root => Path.Combine(Path.GetTempPath(), "missum-program-integrations");
    private static string StatePath => Path.Combine(Root, "unreal-workspaces",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Workspace.ToUpperInvariant()))), "unreal.json");

    private static async Task Main()
    {
        Console.InputEncoding = new UTF8Encoding(false, true);
        Console.OutputEncoding = new UTF8Encoding(false, true);
        var id = "unknown";
        try
        {
            var root = JsonNode.Parse(await Console.In.ReadLineAsync().ConfigureAwait(false) ?? "") as JsonObject
                ?? throw new ToolException("adapter.no_request", "Der ExtensionHost hat keine Aktion übergeben.");
            id = String(root, "requestId", "unknown");
            if (String(root, "type") != "action.invoke") throw new ToolException("adapter.unsupported_message", "Unbekannter Nachrichtentyp.");
            var payload = root["payload"] as JsonObject ?? new();
            if (String(payload, "actionId") != Action) throw new ToolException("adapter.unknown_action", "Unbekannte Unreal-Aktion.");
            var args = payload["arguments"] as JsonObject ?? new();
            var endpoint = args["_missumUnreal"] as JsonObject;
            var engines = args["_missumUnrealEngines"]?.DeepClone();
            var channel = args["_missumUnrealChannel"]?.DeepClone();
            var launcher = args["_missumUnrealLauncher"]?.DeepClone();
            args.Remove("_missumUnreal");
            args.Remove("_missumUnrealEngines");
            // The launch receipt must not travel into the editor arguments: it would change
            // the operation fingerprint and leak a local path into the command payload.
            args.Remove("_missumUnrealChannel");
            args.Remove("_missumUnrealLauncher");
            var result = await DispatchAsync(String(payload, "modelToolName"), args, endpoint, engines, channel, launcher).ConfigureAwait(false);
            await ReplyAsync("action.result", id, new JsonObject { ["result"] = result }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var code = ex is ToolException error ? error.Code : "unreal.adapter_failed";
            await ReplyAsync("host.error", id, new JsonObject { ["code"] = code, ["message"] = ex.Message }).ConfigureAwait(false);
        }
    }

    private static async Task<JsonObject> DispatchAsync(string tool, JsonObject args, JsonObject? endpoint,
        JsonNode? engines, JsonNode? launchChannel, JsonNode? launcher)
    {
        // The outer host already enforces the per-tool budget from the manifest. A shorter
        // internal limit would cancel heavy operations such as compiling a Blueprint of a
        // Character before the caller could report a result.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var token = deadline.Token;
        await using var stateLock = await LockWorkspaceAsync(token).ConfigureAwait(false);
        var state = LoadState();
        JsonObject? editor = null;
        if (endpoint is not null)
        {
            try { editor = await SendAsync(endpoint, "status", new JsonObject(), token).ConfigureAwait(false); }
            catch (Exception ex) when (tool == "unreal.status" && ex is SocketException or IOException)
            {
                // A stopped editor should not hide other discoverable windows.
            }
        }
        if (editor is not null) ValidateEditor(editor, endpoint!);
        if (tool == "unreal.status" || tool == "unreal.connect")
        {
            var windows = ObserveWindows();
            if (args["visionReview"] is JsonObject review) return Review(state, review, editor);
            var result = new JsonObject
            {
                ["tool"] = tool, ["connected"] = editor is not null,
                ["instances"] = windows, ["selected"] = editor?.DeepClone(),
                ["availableEngines"] = engines,
                // Kanal getrennt vom Rest: der Start ist lokal belegt, die Fähigkeiten melden aus dem Editor.
                ["launchChannel"] = launchChannel?.DeepClone(),
                ["editorChannel"] = editor?["channel"]?.DeepClone(),
                ["editorCapabilities"] = editor?["capabilities"]?.DeepClone(),
                ["capabilityLimits"] = editor?["capabilityLimits"]?.DeepClone(),
                ["launcher"] = launcher?.DeepClone(),
                ["visionPending"] = state["visionPending"]?.DeepClone(),
                ["nextStep"] = editor is null
                    ? "Mit unreal.connect ein Arbeitsfenster öffnen. Bereits geöffnete Fenster werden nur beobachtet."
                    : "Fähigkeiten des Editors stehen in editorCapabilities; Aufnahmen mit unreal.view.capture.",
                ["channel"] = editor is not null
                    ? $"{String(editor["channel"], "type", "unknown")} über {String(editor["channel"], "pump", "unbekannt")}"
                    : launchChannel is null ? "kein laufender Editor" : $"{String(launchChannel, "channel", "unknown")} über {String(launchChannel, "pump", "unbekannt")}",
            };
            WriteStatus(editor, windows.Count, state);
            return result;
        }
        if (tool == "unreal.launcher.start")
        {
            // Works without a running editor: it prepares the machine for the Fab download run.
            if (launcher is not JsonObject found)
                throw new ToolException("unreal.launcher_not_found",
                    "Der Epic Games Launcher wurde auf diesem Rechner nicht gefunden. Installiere ihn oder gib ihn manuell an.");
            found["tool"] = tool;
            found["nextStep"] = Boolean(found, "running")
                ? "Der Launcher ist offen. Im Steuerlauf kann die Fab-Bibliothek jetzt bedient werden."
                : "Der Launcherpfad ist bekannt, das Fenster wurde aber nicht beobachtet.";
            WriteStatus(editor, 0, state);
            return found;
        }
        if (editor is null) throw new ToolException("unreal.not_connected", "Unreal ist nicht verbunden. Zuerst unreal.connect aufrufen.");
        ValidateAssetPath(args);
        if (tool == "unreal.asset" && String(args, "operation") == "import") ValidateImportFile(args);
        var operationId = String(args, "operationId");
        if (operationId.Length == 0) operationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        string? captureOperationId = null;
        var command = tool switch
        {
            _ when GroupedCommands.TryGetValue(tool, out var verbs) => GroupedCommand(tool, args, verbs),
            "unreal.view.capture" => "view.capture",
            "unreal.python" => "python.run",
            _ => throw new ToolException("unreal.tool_unknown", "Unbekanntes Unreal-Werkzeug."),
        };
        var visual = Boolean(args, "requireVisualReview");
        args.Remove("requireVisualReview");
        if (command == "view.capture")
        {
            // A change that still waits for its picture claims the capture, whatever the caller named
            // it: the capture shows the current state either way. A capture that already exists does
            // not block the next one; the new picture simply replaces the pending review.
            if (PendingCaptureOperation(state["visionPending"]) is { } pendingId) operationId = pendingId;
            var directory = Path.Combine(Root, "captures");
            Directory.CreateDirectory(directory);
            args["outputPath"] = Path.Combine(directory, "unreal-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".png");
            captureOperationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        }
        args["operationId"] = captureOperationId ?? operationId;
        var output = await SendAsync(endpoint!, command, args, token).ConfigureAwait(false);
        if (command == "view.capture")
        {
            while (String(output, "state") == "pending")
            {
                await Task.Delay(100, token).ConfigureAwait(false);
                output = await SendAsync(endpoint!, "operation.status", new JsonObject { ["operationId"] = captureOperationId }, token).ConfigureAwait(false);
            }
            if (String(output, "state") == "failed")
                throw new ToolException(String(output, "errorCode", "unreal.capture_failed"),
                    String(output, "errorMessage", "Die Editoraufnahme schlug fehl. "
                        + "Als Rückfall steht die Fensteraufnahme über image.input offen."));
            var path = String(args, "outputPath");
            if (!File.Exists(path) || new FileInfo(path).Length is <= 8 or > 25 * 1024 * 1024)
                throw new ToolException("unreal.capture_missing", "Die Unreal-Aufnahme wurde nicht als gültige PNG-Datei gespeichert.");
            var header = new byte[8];
            using (var input = File.OpenRead(path)) { _ = input.Read(header); }
            if (!header.AsSpan().SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new ToolException("unreal.capture_invalid", "Die Unreal-Aufnahme ist keine PNG-Datei.");
            if (!string.Equals(String(output, "projectPath"), String(editor, "projectPath"), StringComparison.OrdinalIgnoreCase)
                || String(output, "world") != String(editor, "world")
                || String(output, "instanceId") != String(editor, "instanceId")
                || String(output, "source") != String(args, "source")
                || String(args, "source") == "camera" && String(output, "cameraRequest") != String(args, "cameraName"))
                throw new ToolException("unreal.capture_changed", "Projekt oder Welt änderte sich während der Aufnahme.");
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false))).ToLowerInvariant();
            output["imagePath"] = path; output["sha256"] = hash; output["mediaType"] = "image/png";
            output["operationId"] = operationId;
            state["visionPending"] = new JsonObject { ["operationId"] = operationId, ["sha256"] = hash,
                ["projectPath"] = String(editor, "projectPath"), ["world"] = String(editor, "world"),
                ["instanceId"] = String(editor, "instanceId"), ["capturePath"] = path };
            output["vision"] = new JsonObject { ["loadTool"] = "image.input", ["analyzeTool"] = "media.analyze",
                ["instruction"] = "Dieses Bild laden, mit media.analyze prüfen und den Befund an unreal.status als visionReview melden." };
            SaveState(state);
        }
        else if (IsVisualMutation(command))
        {
            output["operationId"] = operationId;
            output["visualCheck"] = new JsonObject { ["required"] = visual };
            if (visual)
            {
                state["visionPending"] = new JsonObject { ["operationId"] = operationId, ["sha256"] = "",
                    ["projectPath"] = String(editor, "projectPath"), ["world"] = String(editor, "world"),
                    ["instanceId"] = String(editor, "instanceId") };
                SaveState(state);
            }
        }
        if (command == "python.run" && String(args, "code").Contains("HighResShot", StringComparison.OrdinalIgnoreCase))
            output["captureNotice"] = HighResShotNotice;
        output["tool"] = tool;
        WriteStatus(editor, 0, state);
        return output;
    }

    // In the live run the agent fired HighResShot itself and then guessed the newest file in
    // Saved/Screenshots: the file arrives seconds later, during PIE it shows the game window, and
    // it picked an older picture of a different view. The result says so right away.
    internal const string HighResShotNotice =
        "HighResShot schreibt asynchron, oft erst Sekunden später, und nimmt während einer Spielvorschau "
        + "das Spielfenster statt des Editor-Viewports auf. Die neueste Datei in Saved/Screenshots ist deshalb "
        + "nicht sicher diese Aufnahme. Für eine belegte Aufnahme unreal.view.capture verwenden; sie meldet "
        + "imagePath und sha256 des tatsächlichen Bildes.";

    /// <summary>The operation of a visible change that has no picture yet, or null.</summary>
    internal static string? PendingCaptureOperation(JsonNode? pending) =>
        String(pending, "operationId") is { Length: > 0 } id && String(pending, "sha256").Length == 0 ? id : null;

    /// <summary>
    /// Changes that can be seen in the viewport get an operation identity and, when the caller
    /// asks for it, a pending visual review. Pure reads never do.
    /// </summary>
    private static bool IsVisualMutation(string command) => command switch
    {
        "camera.create" or "camera.set" or "camera.frame" or "view.capture" or "python.run" => true,
        _ when command.StartsWith("blueprint.", StringComparison.Ordinal)
            => command is not ("blueprint.read" or "blueprint.compile"),
        _ when command.StartsWith("level.", StringComparison.Ordinal)
            => command is not ("level.read" or "level.list"),
        _ when command.StartsWith("asset.", StringComparison.Ordinal)
            => command is not ("asset.read" or "asset.list"),
        _ when command.StartsWith("actor.", StringComparison.Ordinal)
            => command is not ("actor.read" or "actor.verify"),
        _ when command.StartsWith("material.", StringComparison.Ordinal) => command != "material.read",
        _ when command.StartsWith("light.", StringComparison.Ordinal) => command != "light.read",
        _ when command.StartsWith("sky.", StringComparison.Ordinal) => command != "sky.read",
        _ => false,
    };

    private static JsonObject Review(JsonObject state, JsonObject review, JsonObject? editor)
    {
        var verdict = String(review, "verdict");
        var summary = String(review, "summary").Trim();
        if (verdict is not ("verified" or "deviation" or "unavailable") || summary.Length == 0)
            throw new ToolException("unreal.vision_invalid", "Der Sichtbefund benötigt verdict und summary.");
        var pending = state["visionPending"] as JsonObject;
        var sha = String(review, "analyzedSha256").ToLowerInvariant();
        var matches = pending is not null && editor is not null
            && String(review, "operationId") == String(pending, "operationId")
            && (verdict == "unavailable" && sha.Length == 0
                || String(pending, "sha256").Length > 0 && String(pending, "sha256") == sha)
            && string.Equals(String(pending, "projectPath"), String(editor, "projectPath"), StringComparison.OrdinalIgnoreCase)
            && String(pending, "world") == String(editor, "world")
            && String(pending, "instanceId") == String(editor, "instanceId");
        var echo = new JsonObject { ["operationId"] = String(review, "operationId"), ["verdict"] = verdict,
            ["analyzedSha256"] = sha, ["matchesCapture"] = matches, ["summary"] = summary };
        if (matches) { state["visionPending"] = null; state["visionReview"] = echo.DeepClone(); SaveState(state); }
        WriteStatus(editor, 0, state);
        return new JsonObject { ["tool"] = "unreal.status", ["connected"] = editor is not null, ["visionReview"] = echo };
    }

    private static void ValidateEditor(JsonObject editor, JsonObject endpoint)
    {
        if (Number(editor, "processId") != Number(endpoint, "processId") || String(editor, "instanceId").Length == 0)
            throw new ToolException("unreal.identity_mismatch", "Die Editoridentität stimmt nicht mit der gestarteten Sitzung überein.");
        var path = Path.GetFullPath(String(editor, "projectPath"));
        if (!path.StartsWith(Workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ToolException("unreal.project_outside_workspace", "Das Unreal-Projekt liegt außerhalb des Projektordners.");
    }

    private static void ValidateAssetPath(JsonObject args)
    {
        var asset = String(args, "assetPath");
        if (asset.Length == 0) return;
        if (!asset.StartsWith("/Game/", StringComparison.Ordinal) || asset.Contains("..", StringComparison.Ordinal)
            || asset.Contains('\\') || asset.Contains("//", StringComparison.Ordinal))
            throw new ToolException("unreal.invalid_asset_path", "Assetpfade müssen unter /Game/ liegen.");
    }

    private static readonly string[] ImportExtensions = [".fbx", ".obj", ".gltf", ".glb", ".usda", ".abc"];

    /// <summary>
    /// Import sources must stay inside the opened workspace. Reparse points are resolved first,
    /// so a link out of the workspace cannot smuggle a foreign file into the project.
    /// </summary>
    private static void ValidateImportFile(JsonObject args)
    {
        var raw = String(args, "filePath");
        if (raw.Length == 0) throw new ToolException("unreal.import_file_required", "Eine Importdatei fehlt.");
        var full = Path.GetFullPath(Path.IsPathFullyQualified(raw) ? raw : Path.Combine(Workspace, raw));
        var resolved = ResolveReparsePoint(full);
        var extension = Path.GetExtension(resolved).ToLowerInvariant();
        if (!ImportExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new ToolException("unreal.import_extension_unsupported",
                "Unterstützte Formate: " + string.Join(", ", ImportExtensions) + ".");
        if (!File.Exists(resolved))
            throw new ToolException("unreal.import_file_missing", "Die Importdatei existiert nicht: " + resolved);
        if (!resolved.StartsWith(Workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ToolException("unreal.import_outside_workspace",
                "Importdateien müssen innerhalb des Projektordners liegen.");
        args["filePath"] = resolved;
    }

    private static string ResolveReparsePoint(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            var file = new FileInfo(full);
            if (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                var fileTarget = file.ResolveLinkTarget(true);
                if (fileTarget is not null) full = Path.GetFullPath(fileTarget.FullName);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A link that cannot be read stays as it is; the workspace check below still applies.
        }
        var name = Path.GetFileName(full);
        var directory = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(directory)) return ResolveDirectory(full);
        return Path.GetFullPath(Path.Combine(ResolveDirectory(directory), name));
    }

    /// <summary>Follows directory links a bounded number of times so a link cannot leave the workspace.</summary>
    private static string ResolveDirectory(string directory)
    {
        var current = Path.GetFullPath(directory);
        for (var hop = 0; hop < 8; hop++)
        {
            DirectoryInfo info;
            try
            {
                info = new DirectoryInfo(current);
                if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) == 0) return current;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return current;
            }
            var target = info.ResolveLinkTarget(true);
            if (target is null) return current;
            var next = Path.GetFullPath(target.FullName);
            if (string.Equals(next, current, StringComparison.OrdinalIgnoreCase)) return current;
            current = next;
        }
        return current;
    }

    private static async Task<JsonObject> SendAsync(JsonObject endpoint, string command, JsonObject args, CancellationToken token)
    {
        var port = Number(endpoint, "port");
        var secret = String(endpoint, "token");
        if (port is < 1 or > 65535 || secret.Length < 32)
            throw new ToolException("unreal.endpoint_invalid", "Die Verbindung zum Unreal Editor ist ungültig.");
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, token).ConfigureAwait(false);
        await using var stream = client.GetStream();
        var id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var request = new JsonObject { ["token"] = secret, ["requestId"] = id, ["command"] = command,
            ["arguments"] = args.DeepClone() };
        var bytes = Encoding.UTF8.GetBytes(request.ToJsonString(Json) + "\n");
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
        var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
        if (line is null || line.Length > 4 * 1024 * 1024) throw new ToolException("unreal.reply_invalid", "Der Editor hat nicht gültig geantwortet.");
        var response = JsonNode.Parse(line) as JsonObject ?? throw new ToolException("unreal.reply_invalid", "Ungültige Editorantwort.");
        if (String(response, "requestId") != id) throw new ToolException("unreal.reply_mismatch", "Die Editorantwort gehört zu einem anderen Auftrag.");
        if (!Boolean(response, "ok"))
            throw new ToolException(String(response["error"], "code", "unreal.command_failed"),
                String(response["error"], "message", "Der Editor hat den Auftrag abgelehnt."));
        return response["result"]?.DeepClone() as JsonObject ?? new();
    }

    private static JsonArray ObserveWindows()
    {
        var windows = new JsonArray();
        foreach (var process in Process.GetProcessesByName("UnrealEditor"))
        {
            using (process)
            {
                try
                {
                    if (process.MainWindowHandle != IntPtr.Zero)
                        windows.Add(new JsonObject { ["processId"] = process.Id, ["instanceId"] = "fenster-" + process.Id.ToString(CultureInfo.InvariantCulture),
                            ["windowTitle"] = process.MainWindowTitle, ["controllable"] = false, ["detection"] = "window" });
                }
                catch (InvalidOperationException) { }
            }
        }
        return windows;
    }

    private static JsonObject LoadState()
    {
        try { return File.Exists(StatePath) ? JsonNode.Parse(File.ReadAllText(StatePath)) as JsonObject ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    private static async Task<FileStream> LockWorkspaceAsync(CancellationToken cancellationToken)
    {
        var path = StatePath + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(100, cancellationToken).ConfigureAwait(false); }
        }
    }
    private static void SaveState(JsonObject state) => WriteFile(StatePath, state.ToJsonString(Json));
    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, true);
    }
    private static void WriteStatus(JsonObject? editor, int count, JsonObject state)
    {
        try
        {
            var path = Path.Combine(Root, "status.json");
            var array = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonArray ?? new() : new();
            for (var i = array.Count - 1; i >= 0; i--) if (String(array[i], "actionId") == Action) array.RemoveAt(i);
            array.Add(new JsonObject { ["actionId"] = Action, ["extensionId"] = "com.missum.unreal",
                ["connected"] = editor is not null, ["sceneName"] = String(editor, "world"),
                ["projectName"] = Path.GetFileName(String(editor, "projectPath")), ["updatedAt"] = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                ["observedCount"] = count, ["visionState"] = state["visionPending"] is null ? "" : "pending" });
            WriteFile(path, array.ToJsonString(Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }
    private static string String(JsonNode? node, string key, string fallback = "") =>
        node is JsonObject obj && obj[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : fallback;
    private static int Number(JsonNode? node, string key) =>
        node is JsonObject obj && obj[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;
    private static bool Boolean(JsonNode? node, string key) =>
        node is JsonObject obj && obj[key] is JsonValue value && value.TryGetValue<bool>(out var boolean) && boolean;
    private static Task ReplyAsync(string type, string id, JsonObject payload) =>
        Console.Out.WriteLineAsync(new JsonObject { ["protocolVersion"] = 1, ["type"] = type, ["requestId"] = id, ["payload"] = payload }.ToJsonString(Json));

    private sealed class ToolException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
