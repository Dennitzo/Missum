using System.Globalization;
using System.Text.Json.Nodes;

namespace Missum.BlenderAdapter;

/// <summary>
/// The model tools Blender is unlocked with. They are deliberately small and general: read
/// state, change state, take a picture. Complex modelling is then a sequence of these calls chosen
/// by the model instead of a fixed recipe inside the adapter.
/// </summary>
internal static class BlenderTools
{
    public const string ExtensionId = "com.missum.blender";
    public const string ActionId = "com.missum.blender/edit";

    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan EditTimeout = TimeSpan.FromSeconds(240);
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(240);
    private static readonly TimeSpan CustomTimeout = TimeSpan.FromSeconds(180);

    public static async Task<JsonObject> DispatchAsync(string modelToolName, string actionId, JsonObject arguments)
    {
        var tool = ResolveTool(modelToolName, actionId);
        // Leave room for the initial probe and the edit receipt lookup after a command deadline.
        using var deadline = new CancellationTokenSource(TimeoutFor(tool) + TimeSpan.FromSeconds(5));
        return tool switch
        {
            "status" => await StatusAsync(arguments, deadline.Token).ConfigureAwait(false),
            "scene.read" => await SceneReadAsync(arguments, deadline.Token).ConfigureAwait(false),
            "scene.edit" => await SceneEditAsync(arguments, deadline.Token).ConfigureAwait(false),
            "view.capture" => await ViewCaptureAsync(arguments, deadline.Token).ConfigureAwait(false),
            "custom.run" => await CustomRunAsync(arguments, deadline.Token).ConfigureAwait(false),
            _ => throw new BlenderToolException("adapter.tool_unknown", $"Werkzeug '{tool}' wird nicht unterstützt."),
        };
    }

    private static string ResolveTool(string modelToolName, string actionId)
    {
        var name = modelToolName.Trim().ToLowerInvariant();
        if (name.Length == 0 && actionId.Contains('/', StringComparison.Ordinal))
            name = actionId[(actionId.IndexOf('/') + 1)..];
        return name switch
        {
            "blender.status" or "status" => "status",
            "blender.scene.read" or "scene.read" => "scene.read",
            "blender.scene.edit" or "scene.edit" => "scene.edit",
            "blender.view.capture" or "view.capture" => "view.capture",
            "blender.custom.run" or "custom.run" => "custom.run",
            _ => throw new BlenderToolException("adapter.tool_unknown",
                "Dieser Aufruf nennt kein Blender-Werkzeug. Erlaubt sind blender.status, blender.scene.read, "
                + "blender.scene.edit und blender.view.capture."),
        };
    }

    private static TimeSpan TimeoutFor(string tool) => tool switch
    {
        "status" => StatusTimeout,
        "scene.read" => ReadTimeout,
        "scene.edit" => EditTimeout,
        "custom.run" => CustomTimeout,
        _ => CaptureTimeout,
    };

    private static async Task<JsonObject> StatusAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var connect = Wire.ReadBool(arguments, "connect", true);
        var requested = Wire.ReadString(arguments, "instanceId");
        var seen = await BlenderChannel.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        // Windows found without any add-on are reported but never bound: only the Blender runtime
        // Missum started itself answers commands, everything else can only be looked at.
        var instances = seen.Where(static item => item.Controllable).ToList();
        var openWindows = seen.Where(static item => !item.Controllable).ToList();
        var requestedProject = Wire.ReadString(arguments, "projectPath");
        if (!string.IsNullOrWhiteSpace(requestedProject) && instances.Count == 1
            && !string.Equals(Normalize(Path.GetFullPath(requestedProject, IntegrationStateStore.WorkspaceRoot)),
                Normalize(instances[0].BlendPath), StringComparison.OrdinalIgnoreCase))
            throw new BlenderToolException("blender.project_already_open",
                $"In der Missum-Instanz ist '{Display(instances[0].BlendPath)}' geöffnet. "
                + "Die vorhandene Szene prüfen und bei Bedarf mit project.open gezielt wechseln.");
        var state = IntegrationStateStore.Load();
        var binding = IntegrationStateStore.Binding(state);
        var waitingForBridge = instances.Count == 0 && BlenderChannel.HasEndpoint
            && (binding is null && requested.Length == 0
                || binding is not null && BoundEndpointIsRunning(binding)
                && (requested.Length == 0 || requested == Wire.ReadString(binding, "instanceId")));

        BlenderInstance? selected = null;
        string? reason = null;
        if (waitingForBridge)
        {
            reason = BridgeUnavailableMessage;
        }
        else if (requested.Length > 0)
        {
            // Naming the instance is the explicit confirmation that also lifts a required rebind.
            var named = seen.FirstOrDefault(item => string.Equals(item.InstanceId, requested, StringComparison.Ordinal))
                ?? throw new BlenderToolException("blender.instance_missing",
                    $"Eine Blender-Instanz '{requested}' ist gerade nicht erreichbar. blender.status ohne instanceId "
                    + "listet die laufenden Instanzen und offenen Fenster.");
            if (!named.Controllable)
            {
                reason = $"Das gefundene Fenster '{Display(named.WindowTitle)}' gehört zu einem Blender, das Missum "
                    + "nicht startet. Ansehen ist möglich: blender.view.capture liefert ein Fensterbild. Ändern ist "
                    + "nur in einem Missum-Arbeitsfenster möglich: blender.status mit connect=true und projectPath.";
            }
            else
            {
                selected = named;
            }
        }
        else if (IntegrationStateStore.RequiresRebind(state))
        {
            reason = IntegrationStateStore.RebindReason(state);
            if (string.IsNullOrWhiteSpace(reason))
                reason = "Die bisherige Bindung ist ungültig; Ziel ausdrücklich wählen.";
            reason += " Diese Änderung wurde nicht ausgeführt. In instanceId die gewünschte Instanz setzen.";
        }
        else if (binding is not null)
        {
            var boundId = Wire.ReadString(binding, "instanceId");
            var boundPort = Wire.ReadInt(binding, "port", 0);
            var boundStamp = Wire.ReadString(binding, "sessionStamp");
            var boundPath = Wire.ReadString(binding, "projectPath");
            var boundScene = Wire.ReadString(binding, "sceneName");
            var live = instances.FirstOrDefault(item => item.Port == boundPort
                && string.Equals(item.InstanceId, boundId, StringComparison.Ordinal));
            if (live is null)
            {
                reason = "Die zuletzt gebundene Blender-Instanz ist nicht mehr erreichbar.";
                IntegrationStateStore.SetRequiresRebind(state, reason);
            }
            else if (!string.Equals(live.SessionStamp, boundStamp, StringComparison.Ordinal))
            {
                reason = "Blender wurde neu gestartet oder es wurde eine andere Datei geladen.";
                IntegrationStateStore.SetRequiresRebind(state, reason);
            }
            else if (!string.Equals(Normalize(boundPath), Normalize(live.BlendPath), StringComparison.OrdinalIgnoreCase))
            {
                reason = $"In dieser Blender-Instanz ist jetzt '{Display(live.BlendPath)}' offen, gebunden war "
                    + $"'{Display(boundPath)}'.";
                IntegrationStateStore.SetRequiresRebind(state, reason);
            }
            else if (boundScene.Length > 0 && !string.Equals(boundScene, live.SceneName, StringComparison.Ordinal))
            {
                reason = $"Aktive Szene ist jetzt '{live.SceneName}', gebunden war '{boundScene}'.";
                IntegrationStateStore.SetRequiresRebind(state, reason);
            }
            else
            {
                selected = live;
            }
        }
        else if (instances.Count == 1)
        {
            selected = instances[0];
        }

        if (selected is not null && connect) IntegrationStateStore.SetBinding(state, selected);

        // A capture only becomes a checked result once the run reports what vision actually looked at.
        // The verdict arrives here, on the tool that is also called when a run resumes, and is checked
        // against the capture that is still open — an uploaded picture alone is never enough.
        JsonObject? visionEcho = null;
        if (arguments["visionReview"] is JsonObject review)
        {
            // A picture of a found window has no live scene to bind to, but the verdict still belongs
            // to exactly that picture and must be recorded: the identity that matters is the capture.
            var windowReview = selected is null
                && string.Equals(IntegrationStateStore.CaptureSource(state), "window", StringComparison.Ordinal);
            if (!windowReview && waitingForBridge)
                throw new BlenderToolException("blender.bridge_unavailable", BridgeUnavailableMessage);
            if (!windowReview && (selected is null || !IntegrationStateStore.BindingMatches(state, selected)))
                throw RejectBinding(state, "blender.rebind_required",
                    "Die Sichtprüfung benötigt die verbundene Blender-Szene.");
            var verdict = Wire.ReadString(review, "verdict").Trim().ToLowerInvariant();
            if (verdict is not ("verified" or "deviation" or "unavailable"))
                throw new BlenderToolException("blender.vision_verdict_invalid",
                    "visionReview.verdict muss 'verified', 'deviation' oder 'unavailable' sein.");
            var finding = Wire.ReadString(review, "summary");
            if (finding.Trim().Length == 0)
                throw new BlenderToolException("blender.vision_summary_missing",
                    "visionReview.summary kurz ausfüllen: das hat media.analyze an diesem Bild festgestellt.");
            visionEcho = IntegrationStateStore.CompleteVisionReview(state,
                Wire.ReadString(review, "operationId"), Wire.ReadString(review, "analyzedSha256"),
                verdict, finding.Trim());
        }

        IntegrationStateStore.Save(state);
        IntegrationStateStore.WriteStatus(ActionId, ExtensionId, state, selected is not null,
            selected?.SceneName, selected?.ProjectName, StatusDetail(selected, reason, openWindows),
            openWindows.Count, openWindows.Count == 0 ? null : WindowDetail(openWindows));

        var result = new JsonObject
        {
            ["tool"] = "blender.status",
            ["connected"] = selected is not null,
            ["bridgeUnavailable"] = waitingForBridge,
            ["rebindRequired"] = IntegrationStateStore.RequiresRebind(state),
            ["instanceCount"] = seen.Count,
            ["controllableCount"] = instances.Count,
            ["observedCount"] = openWindows.Count,
            ["instances"] = new JsonArray([.. seen.Select(static instance => instance.ToJson())]),
            ["stateDirectory"] = IntegrationStateStore.RootDirectory,
        };
        if (selected is not null) result["selected"] = selected.ToJson();
        if (reason is not null) result["reason"] = reason;
        if (visionEcho is not null) result["visionReview"] = visionEcho;
        if (IntegrationStateStore.VisionPending(state) is { } openCapture) result["visionPending"] = openCapture.DeepClone();
        result["nextStep"] = waitingForBridge
            ? BridgeUnavailableMessage
            : selected is not null
            ? "Szene bei Bedarf mit blender.scene.read prüfen, ändern mit blender.scene.edit und danach "
              + "blender.view.capture mit media.analyze als Sichtprüfung."
            : openWindows.Count > 0 && !IntegrationStateStore.RequiresRebind(state)
                ? "Ein offenstehendes Blender wurde ohne Add-on gefunden. Ansehen: blender.view.capture liefert "
                  + "ein Fensterbild, danach image.input und media.analyze und den Befund mit visionReview melden. "
                  + "Ändern: blender.status mit connect=true öffnet ein eigenes Missum-Arbeitsfenster; die dort "
                  + "geöffnete Datei vorher im gefundenen Fenster speichern und schließen."
                : IntegrationStateStore.RequiresRebind(state)
                ? "Ziel ausdrücklich bestätigen: blender.status mit instanceId aus der Liste aufrufen; "
                  + "erst danach sind wieder Änderungen möglich."
                : "Mit blender.status (connect=true) kann Missum Blender für diesen Workspace öffnen.";
        // A reported verdict always wins over the general hint, including when vision was unavailable.
        if (visionEcho is not null)
        {
            if (!Wire.ReadBool(visionEcho, "matchesCapture", true))
                result["nextStep"] = "Die Meldung passt nicht zur offenen Sichtprüfung. operationId und Bildkennung der aktuellen Aufnahme verwenden.";
            else if (string.Equals(Wire.ReadString(visionEcho, "verdict"), "deviation", StringComparison.Ordinal))
                result["nextStep"] = "Sichtprüfung meldet eine Abweichung: Änderung gezielt nachziehen und den "
                    + "neuen Zustand erneut mit blender.view.capture und media.analyze ansehen.";
            else if (string.Equals(Wire.ReadString(visionEcho, "verdict"), "unavailable", StringComparison.Ordinal))
                result["nextStep"] = "Stand als 'Visuelle Prüfung ausstehend' melden und nachvollziehbar lassen; "
                    + "dieselbe Aufnahme nicht wiederholt in Schleife versuchen.";
            else
                result["nextStep"] = "Sichtprüfung ist dem Bild zugeordnet; nach der nächsten Änderung erneut aufnehmen.";
        }
        return result;
    }

    private static async Task<JsonObject> SceneReadAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var state = IntegrationStateStore.Load();
        var instance = await ResolveBoundAsync(state, cancellationToken).ConfigureAwait(false);
        var request = new JsonObject
        {
            ["detail"] = Choice(arguments, "detail", ["summary", "selection", "full"], "summary"),
            ["limit"] = Math.Clamp(Wire.ReadInt(arguments, "limit", 120), 1, 500),
        };
        var include = Wire.ReadStringArray(arguments, "include");
        if (include.Count > 0)
            request["include"] = new JsonArray([.. include.Select(static value => JsonValue.Create(value))]);
        var names = Wire.ReadStringArray(arguments, "objectNames");
        if (names.Count > 0)
            request["objectNames"] = new JsonArray([.. names.Select(static value => JsonValue.Create(value))]);

        var result = await CommandForBindingAsync(state, instance, "scene.read", request,
            ReadTimeout, null, cancellationToken).ConfigureAwait(false);
        Describe(result, instance);
        result["tool"] = "blender.scene.read";
        return result;
    }

    private static async Task<JsonObject> SceneEditAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        if (arguments["operations"] is not JsonArray operations || operations.Count == 0)
            throw new BlenderToolException("blender.operations_missing",
                "blender.scene.edit braucht operations mit mindestens einem Schritt, zum Beispiel "
                + "[{\"op\":\"object.create\",\"primitive\":\"cube\",\"name\":\"Tisch\"}].");
        if (operations.Count > 80)
            throw new BlenderToolException("blender.operations_too_many",
                "Eine Änderungsgruppe umfasst höchstens 80 Schritte; Auftrag in mehrere Etappen aufteilen.");

        var operationId = Wire.ReadString(arguments, "operationId").Trim();
        if (operationId.Length == 0) operationId = Guid.NewGuid().ToString("N");
        var save = Wire.ReadBool(arguments, "save", false);

        var state = IntegrationStateStore.Load();
        var instance = await ResolveBoundAsync(state, cancellationToken).ConfigureAwait(false);
        if (IntegrationStateStore.OperationEntry(state, operationId) is { } previous
            && string.Equals(Wire.ReadString(previous, "outcome"), "applied", StringComparison.Ordinal))
        {
            // The same mutation was already carried out: report it instead of running it twice.
            return new JsonObject
            {
                ["tool"] = "blender.scene.edit",
                ["operationId"] = operationId,
                ["outcome"] = "already_applied",
                ["deduplicated"] = true,
                ["summary"] = Wire.ReadString(previous, "summary", "Änderung wurde zuvor bereits ausgeführt."),
                ["visualCheck"] = VisualCheck(),
                ["nextStep"] = "Zustand mit blender.scene.read oder einer neuen Aufnahme prüfen; dieselbe "
                    + "operationId nicht noch einmal senden.",
            };
        }

        var request = new JsonObject
        {
            ["operations"] = operations.DeepClone(),
            ["save"] = save,
            ["stopOnError"] = Wire.ReadBool(arguments, "stopOnError", true),
        };

        JsonObject editResult;
        try
        {
            editResult = await CommandForBindingAsync(state, instance, "scene.edit", request,
                EditTimeout, operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (BlenderToolException exception) when (string.Equals(exception.Code, "blender.edit_partial", StringComparison.Ordinal))
        {
            // Part of the group landed in the scene. Remember it so the same group is never applied
            // twice and hand the model a clear instruction to inspect and repair.
            IntegrationStateStore.RememberOperation(state, operationId, "partial", exception.Message);
            IntegrationStateStore.MarkVisionPending(state, operationId, "", "");
            IntegrationStateStore.Save(state);
            IntegrationStateStore.WriteStatus(ActionId, ExtensionId, state, true, instance.SceneName,
                instance.ProjectName, "Änderungsgruppe unvollständig; Szene prüfen.");
            throw new BlenderToolException("blender.edit_partial",
                exception.Message + " Danach mit blender.view.capture und media.analyze nachsehen.");
        }
        catch (BlenderToolException exception)
            when (exception.Code is "blender.no_answer" or "blender.connection_lost")
        {
            // The outcome of this batch is unclear, so it must never be repeated blindly.
            IntegrationStateStore.RememberOperation(state, operationId, "unknown", exception.Message);
            IntegrationStateStore.Save(state);
            var status = await BlenderChannel
                .TryOperationStatusAsync(instance, operationId, CancellationToken.None)
                .ConfigureAwait(false);
            if (status is not null && Wire.ReadBool(status, "applied", false))
            {
                IntegrationStateStore.RememberOperation(state, operationId, "applied",
                    "Änderung wurde nach Zeitüberschreitung als ausgeführt bestätigt.");
                IntegrationStateStore.MarkVisionPending(state, operationId, "", "");
                IntegrationStateStore.Save(state);
                return new JsonObject
                {
                    ["tool"] = "blender.scene.edit",
                    ["operationId"] = operationId,
                    ["outcome"] = "applied_after_timeout",
                    ["summary"] = Wire.ReadString(status, "summary", "Von Blender als ausgeführt bestätigt."),
                    ["visualCheck"] = VisualCheck(),
                };
            }

            throw new BlenderToolException("blender.outcome_unknown",
                $"{exception.Message} Die Szene wurde seitdem nicht bestätigt. Zustand zuerst mit "
                + "blender.scene.read prüfen und gezielt nachbessern; dieselbe operationId nicht erneut senden.");
        }

        var projectPath = Wire.ReadString(editResult, "projectPath", instance.BlendPath);
        var sceneName = Wire.ReadString(editResult, "sceneName", instance.SceneName);
        IntegrationStateStore.UpdateBindingScene(state, projectPath, sceneName);
        IntegrationStateStore.RememberOperation(state, operationId, "applied",
            $"{Wire.ReadInt(editResult, "appliedCount", 0)} Schritte auf Szene '{sceneName}' angewendet.");
        if (Wire.ReadInt(editResult, "appliedCount", 0) > 0)
            IntegrationStateStore.MarkVisionPending(state, operationId, "", "");
        IntegrationStateStore.Save(state);
        IntegrationStateStore.WriteStatus(ActionId, ExtensionId, state, true, sceneName,
            NameOf(projectPath), SaveDetail(editResult));

        editResult["tool"] = "blender.scene.edit";
        editResult["operationId"] = operationId;
        editResult["outcome"] = "applied";
        Describe(editResult, instance with { BlendPath = projectPath, SceneName = sceneName });
        editResult["visualCheck"] = VisualCheck();
        if (IntegrationStateStore.VisionPending(state) is { } stillOpen)
        {
            editResult["pendingVisionReview"] = new JsonObject
            {
                ["state"] = "pending",
                ["capturePath"] = Wire.ReadString(stillOpen, "capturePath"),
                ["note"] = "Für den bearbeiteten Zustand steht die Auswertung durch media.analyze aus.",
            };
        }

        return editResult;
    }

    /// <summary>
    /// Runs modelling code in Blender's main thread and keeps the same scene binding.
    /// </summary>
    private static async Task<JsonObject> CustomRunAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var code = Wire.ReadString(arguments, "code");
        if (code.Trim().Length == 0)
            throw new BlenderToolException("blender.code_missing",
                "blender.custom.run braucht 'code' mit Python-Code für die Blender-API. Das Ergebnis dem "
                + "Namen 'result' zuweisen, sonst bleibt die Antwort leer.");

        var state = IntegrationStateStore.Load();
        var instance = await ResolveBoundAsync(state, cancellationToken).ConfigureAwait(false);
        var request = new JsonObject
        {
            ["code"] = code,
            ["save"] = Wire.ReadBool(arguments, "save", false),
        };
        var result = await CommandForBindingAsync(state, instance, "custom.python", request,
            CustomTimeout, null, cancellationToken).ConfigureAwait(false);
        var projectPath = Wire.ReadString(result, "projectPath", instance.BlendPath);
        var sceneName = Wire.ReadString(result, "sceneName", instance.SceneName);
        IntegrationStateStore.UpdateBindingScene(state, projectPath, sceneName);
        if (Wire.ReadBool(result, "changedScene", true))
        {
            var operationId = Guid.NewGuid().ToString("N");
            result["operationId"] = operationId;
            result["visualCheck"] = VisualCheck();
            IntegrationStateStore.MarkVisionPending(state, operationId, "", "");
        }
        IntegrationStateStore.Save(state);

        Describe(result, instance with { BlendPath = projectPath, SceneName = sceneName,
            IsDirty = Wire.ReadBool(result, "unsavedChanges", instance.IsDirty) });
        result["tool"] = "blender.custom.run";
        return result;
    }

    private static async Task<JsonObject> ViewCaptureAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var view = Choice(arguments, "view", ["window", "viewport", "render"], "window");
        var engine = Choice(arguments, "engine", ["workbench", "eevee", "cycles"], "workbench");
        var target = IntegrationStateStore.NewCapturePath(view);
        var state = IntegrationStateStore.Load();
        var pendingOperation = Wire.ReadString(IntegrationStateStore.VisionPending(state), "operationId");
        var operationId = Wire.ReadString(arguments, "operationId").Trim();
        if (pendingOperation.Length > 0 && operationId.Length > 0
            && !string.Equals(operationId, pendingOperation, StringComparison.Ordinal))
            throw new BlenderToolException("blender.vision_operation_mismatch",
                $"Die aktuelle Änderung hat operationId '{pendingOperation}'. Diese ID für die Aufnahme verwenden.");
        if (operationId.Length == 0)
            operationId = pendingOperation.Length > 0 ? pendingOperation : Guid.NewGuid().ToString("N");

        if (view == "window" && Wire.ReadString(arguments, "instanceId").StartsWith("fenster-", StringComparison.Ordinal))
        {
            if (pendingOperation.Length > 0 && IntegrationStateStore.CaptureSource(state) != "window")
                throw new BlenderToolException("blender.vision_window_mismatch",
                    "Die laufende Bearbeitung muss im zugehörigen Missum-Arbeitsfenster geprüft werden.");
            return await WindowPictureAsync(state, arguments, target, operationId, cancellationToken).ConfigureAwait(false);
        }

        BlenderInstance instance;
        try
        {
            instance = await ResolveBoundAsync(state, cancellationToken).ConfigureAwait(false);
        }
        catch (BlenderToolException exception)
            when (view == "window" && pendingOperation.Length == 0
                && exception.Code is "blender.not_controllable" or "blender.not_connected")
        {
            // A Blender window that is merely open cannot be asked for a render, but it can be shown:
            // the picture then comes from the window itself and says exactly that.
            return await WindowPictureAsync(state, arguments, target, operationId, cancellationToken)
                .ConfigureAwait(false);
        }

        var request = new JsonObject
        {
            ["view"] = view,
            ["engine"] = engine,
            ["width"] = Math.Clamp(Wire.ReadInt(arguments, "width", 1280), 160, 4096),
            ["height"] = Math.Clamp(Wire.ReadInt(arguments, "height", 720), 160, 4096),
            ["cameraView"] = Wire.ReadBool(arguments, "cameraView", false),
            ["targetImage"] = target,
        };

        var result = await CommandForBindingAsync(state, instance, "view.capture", request,
            CaptureTimeout, null, cancellationToken).ConfigureAwait(false);
        if (!File.Exists(target) || new FileInfo(target).Length == 0)
            throw new BlenderToolException("blender.capture_missing",
                $"Blender hat keine Bilddatei geschrieben. Erwartet wurde: {target}. "
                + "Ansicht prüfen (view=viewport oder view=render) und die Aufnahme einmal wiederholen.");

        var bytes = new FileInfo(target).Length;
        Describe(result, instance);
        result["tool"] = "blender.view.capture";
        result["imagePath"] = target;
        result["mediaType"] = "image/png";
        result["bytes"] = bytes;
        result["operationId"] = operationId;
        var captureSha256 = IntegrationStateStore.Sha256Of(target);
        result["sha256"] = captureSha256;
        // From now on the composer shows "Visuelle Prüfung ausstehend" until a verdict for exactly this
        // picture arrives through blender.status with visionReview.
        IntegrationStateStore.MarkVisionPending(state, operationId, target, captureSha256);
        IntegrationStateStore.Save(state);
        result["vision"] = VisionInstruction();
        IntegrationStateStore.WriteStatus(ActionId, ExtensionId, state, true, instance.SceneName,
            instance.ProjectName, "Aktuelle Aufnahme erstellt; Sichtprüfung steht aus.");
        return result;
    }

    /// <summary>
    /// Shows a Blender window that Missum does not control. The picture is a real window picture, it
    /// is not a render of a scene, and the answer says which of both the model is looking at.
    /// </summary>
    private static async Task<JsonObject> WindowPictureAsync(JsonObject state, JsonObject arguments, string target,
        string operationId, CancellationToken cancellationToken)
    {
        var seen = await BlenderChannel.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var candidates = seen.Where(static item => !item.Controllable && item.HasWindow).ToList();
        var requested = Wire.ReadString(arguments, "instanceId");
        BlenderInstance window;
        if (requested.Length > 0)
            window = candidates.FirstOrDefault(item => string.Equals(item.InstanceId, requested, StringComparison.Ordinal))
                ?? throw new BlenderToolException("blender.window_missing",
                    $"Ein Fenster '{requested}' ist nicht offen. blender.status listet die offenen Blender-Fenster "
                    + "mit ihrer instanceId.");
        else if (candidates.Count == 1) window = candidates[0];
        else if (candidates.Count == 0) throw NotControllable([]);
        else
            throw new BlenderToolException("blender.window_ambiguous",
                $"Es sind mehrere Blender-Fenster offen: {WindowDetail(candidates)}. Für die Aufnahme die "
                    + "instanceId des gewünschten Fensters aus blender.status setzen.");

        var picture = BlenderWindowCapture.Save(window.WindowHandle, target);
        if (picture.ContentMissing)
            throw new BlenderToolException("blender.capture_empty", picture.HonestNote);
        var info = new FileInfo(target);
        if (!info.Exists || info.Length == 0)
            throw new BlenderToolException("blender.capture_missing",
                $"Aus dem Blender-Fenster wurde kein Bild geschrieben. Erwartet wurde: {target}.");

        var captureSha256 = IntegrationStateStore.Sha256Of(target);
        // The review of a window picture is closed the same way as a scene capture, by the hash of
        // this picture; no live scene binding is required and none is invented.
        IntegrationStateStore.MarkVisionPending(state, operationId, target, captureSha256, "window");
        IntegrationStateStore.Save(state);
        IntegrationStateStore.WriteStatus(ActionId, ExtensionId, state, false, null, window.ProjectName,
            "Fensterbild erstellt; Sichtprüfung steht aus.", candidates.Count, WindowDetail(candidates));

        return new JsonObject
        {
            ["tool"] = "blender.view.capture",
            ["instanceId"] = window.InstanceId,
            ["detection"] = "window",
            ["captureMethod"] = picture.Method,
            ["liveScene"] = false,
            ["windowTitle"] = window.WindowTitle,
            ["projectName"] = window.ProjectName,
            ["observedProjectFile"] = window.ObservedProjectFile,
            ["imagePath"] = target,
            ["mediaType"] = "image/png",
            ["bytes"] = info.Length,
            ["width"] = picture.Width,
            ["height"] = picture.Height,
            ["contentColors"] = picture.ContentColors,
            ["sha256"] = captureSha256,
            ["operationId"] = operationId,
            ["observation"] = picture.HonestNote,
            ["vision"] = VisionInstruction(),
            ["nextStep"] = "Das Bild mit image.input und media.analyze prüfen und den Befund über blender.status "
                + "mit visionReview melden. Eine Änderung an dieser Szene ist nur in einem eigenen "
                + "Missum-Arbeitsfenster möglich: blender.status mit connect=true und projectPath.",
        };
    }

    private static JsonObject VisionInstruction() => new()
    {
        ["loadTool"] = "image.input",
        ["analyzeTool"] = "media.analyze",
        ["instruction"] = "Das Bild ist nur gespeichert, noch nicht gesehen. image.input mit operation=file "
            + "und diesem imagePath aufrufen und die erhaltene uploadId direkt an media.analyze mit der "
            + "konkreten Nutzeranforderung übergeben. Erst danach gilt die Änderung als sichtbar geprüft.",
        ["ifUnavailable"] = "Ist Vision nicht verfügbar oder die Aufnahme unbrauchbar, den Stand als "
            + "'Visuelle Prüfung ausstehend' melden und nicht dieselbe Aufnahme wiederholt versuchen.",
        ["reportVia"] = "Nach media.analyze blender.status mit visionReview aufrufen: operationId, "
            + "analyzedSha256, verdict und summary. verdict ist 'verified' wenn das Bild der Anforderung "
            + "entspricht, 'deviation' bei einer sichtbaren Abweichung und 'unavailable' wenn Vision das Bild "
            + "nicht auswerten konnte. Erst diese Meldung schließt die Prüfung ab.",
    };

    /// <summary>
    /// Every call is bound to one editor instance and one scene. A lost connection or a switched
    /// scene stops the call instead of running the change somewhere else.
    /// </summary>
    private static async Task<BlenderInstance> ResolveBoundAsync(JsonObject state, CancellationToken cancellationToken)
    {
        var seen = await BlenderChannel.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var instances = seen.Where(static item => item.Controllable).ToList();
        var openWindows = seen.Where(static item => !item.Controllable).ToList();
        if (IntegrationStateStore.RequiresRebind(state))
            throw new BlenderToolException("blender.rebind_required",
                $"{IntegrationStateStore.RebindReason(state)} Ohne ausdrücklich bestätigtes Ziel wird nichts "
                + "geändert: blender.status mit instanceId aufrufen und dann weiterarbeiten.");

        var binding = IntegrationStateStore.Binding(state);
        if (binding is null)
        {
            if (instances.Count == 0)
                throw BlenderChannel.HasEndpoint
                    ? new BlenderToolException("blender.bridge_unavailable", BridgeUnavailableMessage)
                    : openWindows.Count > 0
                    ? NotControllable(openWindows)
                    : new BlenderToolException("blender.not_connected",
                        "Die von Missum gestartete Blender-Instanz antwortet nicht. Verbindung mit blender.status prüfen.");
            IntegrationStateStore.SetBinding(state, instances[0]);
            IntegrationStateStore.Save(state);
            return instances[0];
        }

        var boundId = Wire.ReadString(binding, "instanceId");
        var boundPort = Wire.ReadInt(binding, "port", 0);
        var boundStamp = Wire.ReadString(binding, "sessionStamp");
        var boundPath = Wire.ReadString(binding, "projectPath");
        var boundScene = Wire.ReadString(binding, "sceneName");
        var live = instances.FirstOrDefault(item => item.Port == boundPort
            && string.Equals(item.InstanceId, boundId, StringComparison.Ordinal));
        if (live is null && instances.Count == 0 && BoundEndpointIsRunning(binding))
            throw new BlenderToolException("blender.bridge_unavailable", BridgeUnavailableMessage);
        if (live is null)
            throw RejectBinding(state, "blender.connection_lost",
                $"Die gebundene Blender-Instanz '{boundId}' ist nicht mehr erreichbar. "
                + "Keine Änderung ausgeführt; mit blender.status neu verbinden.");

        if (!string.Equals(live.SessionStamp, boundStamp, StringComparison.Ordinal))
            throw RejectBinding(state, "blender.session_restarted",
                $"Blender '{boundId}' wurde neu gestartet oder die Datei neu geladen. Keine Änderung ausgeführt; "
                + "Zustand mit blender.status und blender.scene.read erneut feststellen.");

        if (!string.Equals(Normalize(boundPath), Normalize(live.BlendPath), StringComparison.OrdinalIgnoreCase))
            throw RejectBinding(state, "blender.scene_changed",
                $"Die gebundene Projektdatei hat sich geändert: '{Display(boundPath)}' → '{Display(live.BlendPath)}'. "
                + "Keine Änderung ausgeführt, damit nichts in einer anderen Datei landet. Mit blender.status neu binden.");

        if (boundScene.Length > 0 && !string.Equals(boundScene, live.SceneName, StringComparison.Ordinal))
            throw RejectBinding(state, "blender.scene_switched",
                $"In Blender ist jetzt die Szene '{live.SceneName}' aktiv, gebunden war '{boundScene}'. "
                + "Keine Änderung ausgeführt; mit blender.status prüfen und bewusst neu binden.");

        return live;
    }

    private static async Task<JsonObject> CommandForBindingAsync(JsonObject state, BlenderInstance instance,
        string command, JsonObject arguments, TimeSpan timeout, string? operationId, CancellationToken cancellationToken)
    {
        try
        {
            return await BlenderChannel.CommandAsync(instance, command, arguments, timeout, operationId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (BlenderToolException exception) when (exception.Code == "blender.rebind_required")
        {
            throw RejectBinding(state, exception.Code, exception.Message);
        }
    }

    private const string BridgeUnavailableMessage =
        "Das Missum-Blender-Fenster läuft, die Brücke antwortet gerade nicht. Die bestehende Bindung bleibt erhalten. "
        + "Bei laufender Berechnung auf deren Ende warten. Falls der Zustand anhält, die Szene in Blender sichern "
        + "und das Arbeitsfenster nach dem Schließen mit blender.status (connect=true) neu öffnen.";

    private static bool BoundEndpointIsRunning(JsonObject binding) => BlenderChannel.IsEndpointFor(
        Wire.ReadString(binding, "instanceId"), Wire.ReadInt(binding, "port", 0));

    private static BlenderToolException RejectBinding(JsonObject state, string code, string message)
    {
        IntegrationStateStore.SetRequiresRebind(state, message);
        IntegrationStateStore.Save(state);
        IntegrationStateStore.WriteStatus(ActionId, ExtensionId, state, false, null, null, message);
        return new BlenderToolException(code, message);
    }

    private static void Describe(JsonObject result, BlenderInstance instance)
    {
        result["instanceId"] = instance.InstanceId;
        result["sceneName"] = instance.SceneName;
        result["projectPath"] = instance.BlendPath;
        result["projectName"] = instance.ProjectName;
        result["unsavedChanges"] = instance.IsDirty;
    }

    private static JsonObject VisualCheck() => new()
    {
        ["required"] = true,
        ["tool"] = "blender.view.capture",
        ["then"] = "image.input → media.analyze",
        ["instruction"] = "Nach einer sichtbaren Bearbeitungsetappe eine Aufnahme machen und mit media.analyze "
            + "gegen die Nutzeranforderung prüfen. Kleine Änderungen dürfen zu einer gemeinsamen Prüfung "
            + "zusammengefasst werden.",
    };

    private static string? SaveDetail(JsonObject editResult) => editResult["saved"] is JsonObject saved
        && Wire.ReadBool(saved, "saved", false)
            ? $"Geändert und gespeichert: {Display(Wire.ReadString(saved, "savedTo"))}"
            : "Änderung in der offenen Szene angewendet, noch nicht gespeichert.";

    /// <summary>
    /// The honest refusal for a Blender that is open but not commanded: in that process nothing was
    /// installed or started, so no scene can be read or changed. Both ways on are named, and the
    /// open file of the user is never touched.
    /// </summary>
    private static BlenderToolException NotControllable(List<BlenderInstance> openWindows)
    {
        var what = openWindows.Count == 0
            ? "Es läuft kein Blender, das Missum erreichen kann."
            : $"Offen ist: {WindowDetail(openWindows)}.";
        return new BlenderToolException("blender.not_controllable",
            $"{what} In diesem Fenster läuft keine Missum-Laufzeit, deshalb kann seine Szene weder gelesen noch "
            + "geändert werden. Ansehen: blender.view.capture liefert ein Fensterbild dieses Fensters. "
            + "Ändern: blender.status mit connect=true und projectPath öffnet ein eigenes Missum-Arbeitsfenster; "
            + "die dort zu öffnende Datei vorher in dem offenen Fenster speichern und schließen, damit nichts "
            + "überschrieben wird.");
    }

    private static string WindowDetail(List<BlenderInstance> openWindows) =>
        string.Join(", ", openWindows.Select(WindowLabel));

    private static string WindowLabel(BlenderInstance instance) =>
        instance.WindowTitle.Trim().Length > 0
            ? instance.WindowTitle.Trim()
            : $"Blender (Prozess {Number(instance.ProcessId)})";

    private static string StatusDetail(BlenderInstance? instance, string? reason, List<BlenderInstance> openWindows)
    {
        if (instance is null)
        {
            if (openWindows.Count > 0)
                return $"Im Fenster gefunden: {WindowDetail(openWindows)} · noch nicht gesteuert";
            return reason ?? "Es ist keine Blender-Instanz verbunden.";
        }
        var saved = string.IsNullOrEmpty(instance.BlendPath)
            ? "Szene ohne Projektpfad"
            : instance.IsDirty ? "offene Szene mit ungespeicherten Änderungen" : "Szene gespeichert";
        return $"{instance.SceneName} · {saved}";
    }

    private static string Choice(JsonObject arguments, string name, IReadOnlyList<string> allowed, string fallback)
    {
        var value = Wire.ReadString(arguments, name).Trim().ToLowerInvariant();
        return allowed.Any(item => string.Equals(item, value, StringComparison.Ordinal)) ? value : fallback;
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));

    private static string NameOf(string? value) => string.IsNullOrWhiteSpace(value)
        ? "ungespeicherte Szene"
        : Path.GetFileName(value);

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value)
        ? "ohne Projektpfad"
        : value;

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
