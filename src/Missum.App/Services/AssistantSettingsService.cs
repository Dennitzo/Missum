using System.Collections.Concurrent;
using System.Text.Json;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Extensions;
using Missum.Core.Models;

namespace Missum.App.Services;

/// <summary>Shared preference/trigger/backup operations for WinUI and the LAN browser.</summary>
public sealed class AssistantSettingsService : IDisposable
{
    private const int MaximumBackupBytes = 64 * 1024 * 1024;
    private readonly SettingsCoordinator _settings;
    private readonly MissumAiConnectionService _connection;
    private readonly IPromptTriggerRepository _triggers;
    private readonly IBackupService _backups;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, AssistantSettingsResource> _resources = new();
    private readonly ConcurrentDictionary<Guid, AssistantPreparedBackupRestore> _preparedRestores = new();

    public AssistantSettingsService(SettingsCoordinator settings, MissumAiConnectionService connection,
        IPromptTriggerRepository triggers, IBackupService backups)
    {
        _settings = settings;
        _connection = connection;
        _triggers = triggers;
        _backups = backups;
        settings.Changed += OnSettingsChanged;
    }

    public event EventHandler? Changed;

    /// <summary>Evaluated on the PC by the app host. Defaults to dark without a native window.</summary>
    public Func<string>? ResolvedThemeProvider { get; set; }

    /// <summary>Immutable colors captured on the native UI thread; safe to read from LAN requests.</summary>
    public Func<AssistantResolvedAppearance?>? ResolvedAppearanceProvider { get; set; }

    /// <summary>The host checks active work, calls CompleteRestoreAsync, and schedules app restart.</summary>
    public Func<AssistantPreparedBackupRestore, CancellationToken, Task<bool>>? RestoreHandler { get; set; }

    public static bool CanHandle(string type) => type is
        "settings.get" or "settings.update" or "settings.connectionTest"
        or "promptTriggers.list" or "promptTriggers.apply"
        or "backup.create" or "backup.restore" or "backup.restoreCommit" or "backup.restoreCancel";

    public async Task<object> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await BuildSnapshotAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<object> BuildSnapshotAsync(CancellationToken cancellationToken)
    {
        var current = _settings.Current;
        var items = await _triggers.ListAsync(cancellationToken).ConfigureAwait(false);
        var appearance = ResolvedAppearanceProvider?.Invoke();
        return new
        {
            revision = current.PreferencesRevision,
            values = new
            {
                missumAiServerUrl = current.MissumAiServerUrl,
                isAutomaticSpeechEnabled = current.IsAutomaticSpeechEnabled,
                liveCaptionLanguage = current.LiveCaptionLanguage,
                theme = EnumText(current.Theme),
                language = current.Language,
                accentColor = current.AccentColor,
                backgroundColor = current.BackgroundColor,
                codingToolStepsExpanded = current.CodingToolStepsExpanded,
            },
            resolvedAppearance = appearance,
            resolvedTheme = appearance?.Theme ?? (current.Theme == AppTheme.System
                ? ResolvedThemeProvider?.Invoke() ?? "dark"
                : EnumText(current.Theme)),
            triggerActions = PromptTriggerEditorItem.AvailableActions.Select(option => new
            {
                value = EnumText(option.Value), label = option.Label, extensionActionId = option.ExtensionActionId,
            }).ToArray(),
            triggers = items.Select(trigger => new
            {
                id = trigger.Id,
                revision = trigger.Revision,
                action = EnumText(trigger.Action),
                phrase = trigger.Phrase,
                description = trigger.Description,
                isEnabled = trigger.IsEnabled,
                extensionActionId = trigger.ExtensionActionId,
            }).ToArray(),
        };
    }

    public async Task HandleAsync(WebBridgeEnvelope envelope, Func<string, object, string?, Task> emit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(emit);
        try
        {
            switch (envelope.Type)
            {
                case "settings.get":
                case "promptTriggers.list":
                    await emit("settings.snapshot", await GetSnapshotAsync(cancellationToken).ConfigureAwait(false), envelope.RequestId).ConfigureAwait(false);
                    break;
                case "settings.update":
                {
                    RequireObject(envelope.Payload);
                    var revision = RequiredRevision(envelope.Payload, "expectedRevision");
                    var patch = envelope.Payload.TryGetProperty("values", out var values)
                        ? ParsePatch(values) : new AssistantSettingsPatch();
                    var edits = await ParseTriggersAsync(envelope.Payload, cancellationToken).ConfigureAwait(false);
                    var deleted = ParseDeleted(envelope.Payload);
                    await ApplyAsync(patch, revision, edits, deleted, cancellationToken).ConfigureAwait(false);
                    await emit("settings.changed", await GetSnapshotAsync(cancellationToken).ConfigureAwait(false), envelope.RequestId).ConfigureAwait(false);
                    break;
                }
                case "promptTriggers.apply":
                    await ApplyTriggersAsync(await ParseTriggersAsync(envelope.Payload, cancellationToken).ConfigureAwait(false), ParseDeleted(envelope.Payload), cancellationToken).ConfigureAwait(false);
                    await emit("settings.changed", await GetSnapshotAsync(cancellationToken).ConfigureAwait(false), envelope.RequestId).ConfigureAwait(false);
                    break;
                case "settings.connectionTest":
                {
                    var address = RequiredString(envelope.Payload, "missumAiServerUrl");
                    var result = await TestConnectionAsync(address, cancellationToken).ConfigureAwait(false);
                    await emit("settings.connectionResult", new
                    {
                        missumAiServerUrl = NormalizeGatewayAddress(address),
                        isReachable = result.IsReachable, isReady = result.IsReady,
                        message = result.Message, capabilities = result.Capabilities, health = result.Health,
                    }, envelope.RequestId).ConfigureAwait(false);
                    break;
                }
                case "backup.create":
                {
                    var result = await CreateBrowserBackupAsync(cancellationToken).ConfigureAwait(false);
                    await emit("backup.ready", result, envelope.RequestId).ConfigureAwait(false);
                    break;
                }
                case "backup.restore":
                {
                    var prepared = await PrepareRestoreAsync(RequiredString(envelope.Payload, "fileName"),
                        RequiredString(envelope.Payload, "base64"), cancellationToken).ConfigureAwait(false);
                    await emit("backup.restoreReady", new { restoreId = prepared.Id, fileName = prepared.FileName,
                        message = "Backup geprüft. Beim Wiederherstellen wird der aktuelle Zustand zuerst gesichert und Missum anschließend neu gestartet." },
                        envelope.RequestId).ConfigureAwait(false);
                    break;
                }
                case "backup.restoreCommit":
                {
                    var id = RequiredGuid(envelope.Payload, "restoreId");
                    if (!_preparedRestores.TryGetValue(id, out var prepared) || prepared.ExpiresAt <= DateTimeOffset.UtcNow)
                        throw new InvalidOperationException("Das vorbereitete Backup ist abgelaufen. Bitte erneut hochladen.");
                    if (RestoreHandler is null || !await RestoreHandler(prepared, cancellationToken).ConfigureAwait(false))
                    {
                        await emit("backup.restoreDeferred", new { restoreId = id,
                            message = "Während aktiver Aufträge kann kein Backup wiederhergestellt werden. Bitte nach Abschluss erneut bestätigen." },
                            envelope.RequestId).ConfigureAwait(false);
                        break;
                    }
                    await emit("backup.restored", new { restoreId = id, restartRequired = true }, envelope.RequestId).ConfigureAwait(false);
                    break;
                }
                case "backup.restoreCancel":
                    CancelRestore(RequiredGuid(envelope.Payload, "restoreId"));
                    await emit("backup.cancelled", new { }, envelope.RequestId).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException("Unbekannter Einstellungsbefehl.");
            }
        }
        catch (Exception exception) when (exception is AssistantSettingsConflictException or RevisionConflictException)
        {
            await emit("settings.conflict", new { message = exception.Message,
                snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false) }, envelope.RequestId).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or InvalidDataException
            or FormatException or IOException or JsonException or UnauthorizedAccessException)
        {
            await emit("settings.error", new { operation = envelope.Type, message = exception.Message }, envelope.RequestId).ConfigureAwait(false);
        }
    }

    public async Task UpdatePreferencesAsync(AssistantSettingsPatch patch, long expectedRevision,
        CancellationToken cancellationToken = default)
        => await ApplyAsync(patch, expectedRevision, [], [], cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<PromptTrigger>> ApplyAsync(AssistantSettingsPatch patch, long expectedRevision,
        IReadOnlyList<PromptTrigger> triggers, IReadOnlyList<AssistantDeletedTrigger> deleted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<PromptTrigger> saved;
        try
        {
            if (_settings.Current.PreferencesRevision != expectedRevision)
                throw new AssistantSettingsConflictException(_settings.Current.PreferencesRevision);
            var normalized = NormalizePatch(patch);
            await ValidateTriggerBatchAsync(triggers, deleted, cancellationToken).ConfigureAwait(false);
            // Preflight every edit before persistence; normal PC/browser writes use this shared gate.
            await _settings.UpdatePreferencesAsync(current => ApplyPatch(current, normalized), expectedRevision, cancellationToken).ConfigureAwait(false);
            saved = await SaveTriggerBatchAsync(triggers, deleted, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        if (triggers.Count != 0 || deleted.Count != 0) Changed?.Invoke(this, EventArgs.Empty);
        return saved;
    }

    public async Task<IReadOnlyList<PromptTrigger>> ApplyTriggersAsync(IReadOnlyList<PromptTrigger> triggers,
        IReadOnlyList<AssistantDeletedTrigger> deleted, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<PromptTrigger> saved;
        try
        {
            await ValidateTriggerBatchAsync(triggers, deleted, cancellationToken).ConfigureAwait(false);
            saved = await SaveTriggerBatchAsync(triggers, deleted, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        if (triggers.Count != 0 || deleted.Count != 0) Changed?.Invoke(this, EventArgs.Empty);
        return saved;
    }

    private async Task ValidateTriggerBatchAsync(IReadOnlyList<PromptTrigger> triggers,
        IReadOnlyList<AssistantDeletedTrigger> deleted, CancellationToken cancellationToken)
    {
        var ids = new HashSet<Guid>();
        foreach (var trigger in triggers)
        {
            if (!ids.Add(trigger.Id)) throw new InvalidOperationException("Ein Prompt-Trigger wurde mehrfach übertragen.");
            if (!Enum.IsDefined(trigger.Action) || !Enum.IsDefined(trigger.MatchMode)
                || trigger.Priority is < -10_000 or > 10_000 || trigger.Revision < 0
                || string.IsNullOrWhiteSpace(trigger.Phrase) || trigger.Phrase.Trim().Length > 160
                || trigger.Description.Trim().Length > 500)
                throw new InvalidOperationException("Ungültiger Prompt-Trigger: Phrase 1–160, Beschreibung höchstens 500 Zeichen.");
            _ = PromptActionExtensionIds.ResolveForWrite(trigger.Action, trigger.ExtensionActionId);
            var current = await _triggers.GetAsync(trigger.Id, cancellationToken).ConfigureAwait(false);
            if (trigger.Revision == 0 ? current is not null : current is null || current.Revision != trigger.Revision)
                throw new RevisionConflictException(nameof(PromptTrigger), trigger.Id);
        }
        foreach (var item in deleted)
        {
            if (!ids.Add(item.Id)) throw new InvalidOperationException("Ein Prompt-Trigger wurde mehrfach übertragen.");
            var current = await _triggers.GetAsync(item.Id, cancellationToken).ConfigureAwait(false);
            if (current is null || current.Revision != item.Revision)
                throw new RevisionConflictException(nameof(PromptTrigger), item.Id);
        }
    }

    private async Task<IReadOnlyList<PromptTrigger>> SaveTriggerBatchAsync(IReadOnlyList<PromptTrigger> triggers,
        IReadOnlyList<AssistantDeletedTrigger> deleted, CancellationToken cancellationToken)
    {
        foreach (var item in deleted)
            await _triggers.DeleteAsync(item.Id, item.Revision, cancellationToken).ConfigureAwait(false);
        var saved = new List<PromptTrigger>();
        foreach (var trigger in triggers)
            saved.Add(trigger.Revision == 0
                ? await _triggers.CreateAsync(trigger, cancellationToken).ConfigureAwait(false)
                : await _triggers.UpdateAsync(trigger, trigger.Revision, cancellationToken).ConfigureAwait(false));
        return saved;
    }

    public async Task<MissumAiConnectionStatus> TestConnectionAsync(string address, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeGatewayAddress(address);
        try
        {
            // Probe the editor input, without saving it or starting/changing the configured runtime.
            using var client = await _connection.CreateClientAsync(new Uri(normalized + "/"),
                ensureProfileNativeRuntime: false, cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var live = await client.GetLiveHealthAsync(timeout.Token).ConfigureAwait(false);
            if (live.ProtocolVersion != _settings.Current.MissumAiProtocolVersion)
                return new(true, false, $"Protokollabweichung: Gateway {live.ProtocolVersion}, Missum {_settings.Current.MissumAiProtocolVersion}.", Health: live);
            var capabilities = await client.GetCapabilitiesAsync(timeout.Token).ConfigureAwait(false);
            var health = await client.GetReadyHealthAsync(timeout.Token).ConfigureAwait(false);
            var ready = health.Status is "ready" or "modelLoading" or "modelNotLoaded";
            return new(true, ready, ready ? $"Verbunden · {capabilities.ServerTools.Count} Servertools" : $"Verbunden · Eingeschränkt · {health.Reason}", capabilities, health);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(false, false, exception is OperationCanceledException
                ? "Die Verbindung zum Docker-Gateway hat das Zeitlimit überschritten."
                : $"Der Missum Docker-Gatewaystack ist nicht erreichbar: {exception.Message}");
        }
    }

    public Task<BackupResult> CreateBackupAsync(string destinationPath, CancellationToken cancellationToken = default) =>
        _backups.CreateAsync(destinationPath, cancellationToken);

    public async Task RestoreBackupAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        await _backups.ValidateAsync(backupPath, cancellationToken).ConfigureAwait(false);
        await _backups.RestoreAsync(backupPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> CreateBrowserBackupAsync(CancellationToken cancellationToken)
    {
        RemoveExpiredResources();
        var id = Guid.NewGuid();
        var fileName = $"Missum-Backup-{DateTimeOffset.Now:yyyy-MM-dd-HHmmss}.missumbackup";
        var path = ResourcePath(id);
        var result = await _backups.CreateAsync(path, cancellationToken).ConfigureAwait(false);
        _resources[id] = new(path, "application/octet-stream", fileName, DateTimeOffset.UtcNow.AddHours(1));
        return new { id, url = $"/assistant/settings-resources/{id:D}", fileName, sha256 = result.Sha256,
            createdAt = result.CreatedAt, bytes = new FileInfo(path).Length };
    }

    public bool TryGetResource(Guid id, out AssistantSettingsResource? resource)
    {
        if (_resources.TryGetValue(id, out resource) && resource.ExpiresAt > DateTimeOffset.UtcNow && File.Exists(resource.Path)) return true;
        resource = null;
        return false;
    }

    private async Task<AssistantPreparedBackupRestore> PrepareRestoreAsync(string fileName, string base64, CancellationToken cancellationToken)
    {
        if (!fileName.EndsWith(".missumbackup", StringComparison.OrdinalIgnoreCase)
            || base64.Length > ((long)MaximumBackupBytes + 2) / 3 * 4)
            throw new InvalidDataException("Bitte ein Missum-Backup bis 64 MiB hochladen.");
        var bytes = Convert.FromBase64String(base64);
        if (bytes.Length == 0 || bytes.Length > MaximumBackupBytes)
            throw new InvalidDataException("Bitte ein Missum-Backup bis 64 MiB hochladen.");
        RemoveExpiredResources();
        var id = Guid.NewGuid();
        var path = ResourcePath(id);
        try
        {
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            await _backups.ValidateAsync(path, cancellationToken).ConfigureAwait(false);
            var prepared = new AssistantPreparedBackupRestore(id, path, Path.GetFileName(fileName), DateTimeOffset.UtcNow.AddMinutes(30));
            _preparedRestores[id] = prepared;
            return prepared;
        }
        catch { DeleteFile(path); throw; }
    }

    public async Task CompleteRestoreAsync(Guid restoreId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_preparedRestores.TryGetValue(restoreId, out var prepared) || prepared.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new InvalidOperationException("Das vorbereitete Backup ist abgelaufen.");
            // ZipBackupService validates, creates a safety backup, and rolls back failed installation.
            await RestoreBackupAsync(prepared.Path, cancellationToken).ConfigureAwait(false);
            CancelRestore(restoreId);
        }
        finally { _gate.Release(); }
    }

    private void CancelRestore(Guid id)
    {
        if (_preparedRestores.TryRemove(id, out var prepared)) DeleteFile(prepared.Path);
    }

    private string ResourcePath(Guid id)
    {
        var directory = Path.Combine(_settings.DataDirectory, "LanWeb", "SettingsResources");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{id:N}.missumbackup");
    }

    private void RemoveExpiredResources()
    {
        foreach (var item in _resources.Where(item => item.Value.ExpiresAt <= DateTimeOffset.UtcNow).ToArray())
            if (_resources.TryRemove(item.Key, out var resource)) DeleteFile(resource.Path);
        foreach (var item in _preparedRestores.Where(item => item.Value.ExpiresAt <= DateTimeOffset.UtcNow).ToArray()) CancelRestore(item.Key);
    }

    private static void DeleteFile(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs args)
    {
        if (args.PreferencesChanged) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        foreach (var resource in _resources.Values) DeleteFile(resource.Path);
        foreach (var prepared in _preparedRestores.Values) DeleteFile(prepared.Path);
        _resources.Clear();
        _preparedRestores.Clear();
        _gate.Dispose();
    }

    private static AssistantSettingsPatch NormalizePatch(AssistantSettingsPatch patch)
    {
        if (patch.Theme is { } theme && !Enum.IsDefined(theme)) throw new ArgumentException("Ungültiges Theme.", nameof(patch));
        if (patch.LiveCaptionLanguage is { } caption && caption.Trim() is not ("auto" or "de" or "en"))
            throw new ArgumentException("Die Untertitelsprache muss auto, de oder en sein.", nameof(patch));
        if (patch.Language is { } language && language.Trim() is not ("de-DE" or "en-US"))
            throw new ArgumentException("Die Sprache muss de-DE oder en-US sein.", nameof(patch));
        return patch with
        {
            MissumAiServerUrl = patch.MissumAiServerUrl is { } address ? NormalizeGatewayAddress(address) : null,
            LiveCaptionLanguage = patch.LiveCaptionLanguage?.Trim(),
            Language = patch.Language?.Trim(),
            AccentColor = patch.AccentColor is { } accent ? NormalizeColor(accent) : null,
            BackgroundColor = patch.BackgroundColor is { } background ? NormalizeColor(background) : null,
        };
    }

    private static AppSettings ApplyPatch(AppSettings current, AssistantSettingsPatch patch) => current with
    {
        MissumAiServerUrl = patch.MissumAiServerUrl ?? current.MissumAiServerUrl,
        IsAutomaticSpeechEnabled = patch.IsAutomaticSpeechEnabled ?? current.IsAutomaticSpeechEnabled,
        LiveCaptionLanguage = patch.LiveCaptionLanguage ?? current.LiveCaptionLanguage,
        Theme = patch.Theme ?? current.Theme,
        Language = patch.Language ?? current.Language,
        AccentColor = patch.AccentColor ?? current.AccentColor,
        BackgroundColor = patch.BackgroundColor ?? current.BackgroundColor,
        CodingToolStepsExpanded = patch.CodingToolStepsExpanded ?? current.CodingToolStepsExpanded,
    };

    private static string NormalizeColor(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length != 7 || normalized[0] != '#' || !normalized.Skip(1).All(char.IsAsciiHexDigit))
            throw new ArgumentException("Farben müssen als #RRGGBB angegeben werden.", nameof(value));
        return normalized.ToUpperInvariant();
    }

    private static string NormalizeGatewayAddress(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.Query))
            throw new ArgumentException("Missum benötigt eine gültige HTTP- oder HTTPS-Adresse zum Docker-Gateway.", nameof(value));
        return uri.AbsoluteUri.TrimEnd('/');
    }

    private static AssistantSettingsPatch ParsePatch(JsonElement values)
    {
        RequireObject(values);
        var patch = new AssistantSettingsPatch();
        foreach (var field in values.EnumerateObject())
            patch = field.Name switch
            {
                "missumAiServerUrl" => patch with { MissumAiServerUrl = StringValue(field.Value, field.Name) },
                "isAutomaticSpeechEnabled" => patch with { IsAutomaticSpeechEnabled = BooleanValue(field.Value, field.Name) },
                "liveCaptionLanguage" => patch with { LiveCaptionLanguage = StringValue(field.Value, field.Name) },
                "theme" => patch with { Theme = Enum.TryParse<AppTheme>(StringValue(field.Value, field.Name), true, out var theme) && Enum.IsDefined(theme)
                    ? theme : throw new ArgumentException("Ungültiges Theme.") },
                "language" => patch with { Language = StringValue(field.Value, field.Name) },
                "accentColor" => patch with { AccentColor = StringValue(field.Value, field.Name) },
                "backgroundColor" => patch with { BackgroundColor = StringValue(field.Value, field.Name) },
                "codingToolStepsExpanded" => patch with { CodingToolStepsExpanded = BooleanValue(field.Value, field.Name) },
                _ => throw new ArgumentException($"Nicht bearbeitbare Einstellung: {field.Name}"),
            };
        return patch;
    }

    private async Task<IReadOnlyList<PromptTrigger>> ParseTriggersAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        RequireObject(payload);
        if (!payload.TryGetProperty("triggers", out var array)) return [];
        if (array.ValueKind != JsonValueKind.Array) throw new ArgumentException("triggers muss eine Liste sein.");
        var result = new List<PromptTrigger>();
        foreach (var item in array.EnumerateArray())
        {
            var revision = RequiredRevision(item, "revision");
            var id = item.TryGetProperty("id", out var idValue)
                ? idValue.ValueKind == JsonValueKind.String && Guid.TryParse(idValue.GetString(), out var parsedId) && parsedId != Guid.Empty
                    ? parsedId : throw new ArgumentException("Ein Prompt-Trigger enthält eine ungültige ID.")
                : revision == 0 ? Guid.NewGuid() : throw new ArgumentException("Die Prompt-Trigger-ID fehlt.");
            if (!Enum.TryParse<PromptTriggerAction>(RequiredString(item, "action"), true, out var action)
                || !Enum.IsDefined(action)) throw new ArgumentException("Ungültige Promptaktion.");
            var now = DateTimeOffset.UtcNow;
            var current = revision == 0 ? null : await _triggers.GetAsync(id, cancellationToken).ConfigureAwait(false);
            var extensionActionId = item.TryGetProperty("extensionActionId", out var extension) && extension.ValueKind == JsonValueKind.String
                ? extension.GetString() : current?.Action == action ? current.ExtensionActionId : null;
            result.Add(new(id, action, RequiredString(item, "phrase"), RequiredString(item, "description"),
                current?.MatchMode ?? PromptTriggerMatchMode.Prefix, RequiredBoolean(item, "isEnabled"),
                current?.Priority ?? 100, revision, current?.CreatedAt ?? now, now, extensionActionId));
        }
        return result;
    }

    private static AssistantDeletedTrigger[] ParseDeleted(JsonElement payload)
    {
        RequireObject(payload);
        if (!payload.TryGetProperty("deletedTriggers", out var array)) return [];
        if (array.ValueKind != JsonValueKind.Array) throw new ArgumentException("deletedTriggers muss eine Liste sein.");
        return array.EnumerateArray().Select(item => new AssistantDeletedTrigger(
            RequiredGuid(item, "id"), RequiredRevision(item, "revision"))).ToArray();
    }

    private static string EnumText<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
    private static void RequireObject(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw new ArgumentException("Ein Objekt wird erwartet.");
    }
    private static string StringValue(JsonElement value, string field) => value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw new ArgumentException($"{field} muss eine Zeichenfolge sein.");
    private static bool BooleanValue(JsonElement value, string field) => value.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? value.GetBoolean() : throw new ArgumentException($"{field} muss ein Wahrheitswert sein.");
    private static string RequiredString(JsonElement payload, string field)
    {
        RequireObject(payload);
        return payload.TryGetProperty(field, out var value) ? StringValue(value, field)
            : throw new ArgumentException($"{field} fehlt.");
    }
    private static bool RequiredBoolean(JsonElement payload, string field)
    {
        RequireObject(payload);
        return payload.TryGetProperty(field, out var value) ? BooleanValue(value, field)
            : throw new ArgumentException($"{field} fehlt.");
    }
    private static long RequiredRevision(JsonElement payload, string field)
    {
        RequireObject(payload);
        return payload.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var revision) && revision >= 0 ? revision
            : throw new ArgumentException($"{field} muss eine nichtnegative Revision sein.");
    }
    private static Guid RequiredGuid(JsonElement payload, string field) =>
        Guid.TryParse(RequiredString(payload, field), out var id) && id != Guid.Empty ? id
            : throw new ArgumentException($"{field} muss eine gültige ID sein.");
}

public sealed record AssistantSettingsPatch
{
    public string? MissumAiServerUrl { get; init; }
    public bool? IsAutomaticSpeechEnabled { get; init; }
    public string? LiveCaptionLanguage { get; init; }
    public AppTheme? Theme { get; init; }
    public string? Language { get; init; }
    public string? AccentColor { get; init; }
    public string? BackgroundColor { get; init; }
    public bool? CodingToolStepsExpanded { get; init; }
}

public sealed record AssistantDeletedTrigger(Guid Id, long Revision);
public sealed record AssistantSettingsResource(string Path, string ContentType, string FileName, DateTimeOffset ExpiresAt);
public sealed record AssistantPreparedBackupRestore(Guid Id, string Path, string FileName, DateTimeOffset ExpiresAt);
public sealed record AssistantResolvedAppearance(string Theme, bool HighContrast, IReadOnlyDictionary<string, string> Colors);
