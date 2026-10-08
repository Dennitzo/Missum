using Missum.Core.Models;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Missum.App.Services;

/// <summary>Persistent views and drafts. These IDs route UI state; they are not credentials.</summary>
public sealed class AssistantClientStateStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, AssistantClientView> _clients = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly string _path;

    public AssistantClientStateStore(AssistantRuntimeProfile profile) : this(profile.DataDirectory) { }

    public AssistantClientStateStore(string directory)
    {
        _path = Path.Combine(directory, "ClientState", "views.json");
        if (!File.Exists(_path)) return;
        try
        {
            using var source = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(source);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (!IsValidId(entry.Name) || entry.Value.ValueKind != JsonValueKind.Object) continue;
                try
                {
                    var view = entry.Value.Deserialize<AssistantClientView>(JsonOptions);
                    if (view is not null) _clients[entry.Name] = view.Normalize();
                }
                catch (JsonException)
                {
                    // A malformed client record must not discard another client's valid drafts.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged view cache must never damage, replace or prevent access to chats.
        }
    }

    public static bool IsValidId(string? id) => id is { Length: > 0 and <= 128 }
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    internal AssistantClientView Get(string id, AppSettings defaults)
    {
        if (!IsValidId(id)) throw new ArgumentException("Ungültige Clientkennung.", nameof(id));
        return _clients.GetOrAdd(id, _ => AssistantClientView.From(defaults));
    }

    internal AppSettings Resolve(string id, AppSettings defaults) => id == "desktop" ? defaults : Get(id, defaults).Apply(defaults);

    internal async Task UpdateAsync(string id, SettingsCoordinator settings, Func<AppSettings, AppSettings> update, CancellationToken token)
    {
        if (id == "desktop")
        {
            await settings.UpdateAsync(update, token).ConfigureAwait(false);
            return;
        }
        await _saveGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var before = Get(id, settings.Current);
            var updated = AssistantClientView.From(update(before.Apply(settings.Current))) with { Drafts = before.Drafts };
            await SaveCoreAsync(id, updated, token).ConfigureAwait(false);
            _clients[id] = updated;
        }
        finally { _saveGate.Release(); }
    }

    internal string Draft(string id, Guid sessionId, string legacy, AppSettings defaults)
    {
        var view = Get(id, defaults);
        return view.Drafts.GetValueOrDefault(sessionId.ToString("D")) ?? (id == "desktop" ? legacy : string.Empty);
    }

    internal async Task SaveDraftAsync(string id, Guid sessionId, string draft, AppSettings defaults, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(draft.Length, 100_000);
        await _saveGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var view = Get(id, defaults);
            var drafts = new Dictionary<string, string>(view.Drafts, StringComparer.Ordinal) { [sessionId.ToString("D")] = draft };
            var updated = view with { Drafts = drafts };
            await SaveCoreAsync(id, updated, token).ConfigureAwait(false);
            _clients[id] = updated;
        }
        finally { _saveGate.Release(); }
    }

    private async Task SaveCoreAsync(string id, AssistantClientView updated, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var snapshot = _clients.ToArray().ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            snapshot[id] = updated;
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(snapshot, JsonOptions), token).ConfigureAwait(false);
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { }
        }
    }

    public void Dispose() => _saveGate.Dispose();
}

internal sealed record AssistantClientView
{
    public ChatMode Mode { get; init; }
    public Guid? GeneralSession { get; init; }
    public Guid? CodingSession { get; init; }
    public Guid? ScienceSession { get; init; }
    public Guid? ActiveSession { get; init; }
    public Guid? ActiveProject { get; init; }
    public string? Model { get; init; }
    public bool SessionPaneOpen { get; init; } = true;
    public Dictionary<string, string> Reasoning { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Drafts { get; init; } = new(StringComparer.Ordinal);

    internal static AssistantClientView From(AppSettings value) => new()
    {
        Mode = value.SelectedChatMode, GeneralSession = value.ActiveGeneralSessionId,
        CodingSession = value.ActiveCodingSessionId, ScienceSession = value.ActiveClaudeScienceSessionId,
        ActiveSession = value.ActiveSessionId, ActiveProject = value.ActiveProjectId,
        Model = value.SelectedModel, SessionPaneOpen = value.IsAssistantSessionPaneOpen,
        Reasoning = new(value.ReasoningEffortsByModel, StringComparer.OrdinalIgnoreCase),
    };

    internal AppSettings Apply(AppSettings global) => global with
    {
        SelectedChatMode = Mode, ActiveGeneralSessionId = GeneralSession, ActiveCodingSessionId = CodingSession,
        ActiveClaudeScienceSessionId = ScienceSession, ActiveSessionId = ActiveSession, ActiveProjectId = ActiveProject,
        SelectedModel = Model ?? global.SelectedModel, SelectedCodingModel = Model ?? global.SelectedModel, IsAssistantSessionPaneOpen = SessionPaneOpen,
        // Reasoning is a shared PC preference. Legacy per-tab values remain
        // readable in the cache but may not shadow newer saved server choices.
    };

    internal AssistantClientView Normalize()
    {
        var reasoning = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Reasoning ?? [])
            if (IsValidText(entry.Key, 512) && IsValidText(entry.Value, 64)) reasoning[entry.Key] = entry.Value;
        var drafts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Drafts ?? [])
            if (Guid.TryParse(entry.Key, out var id) && id != Guid.Empty && entry.Value is { Length: <= 100_000 })
                drafts[id.ToString("D")] = entry.Value;
        return this with
        {
            Mode = Enum.IsDefined(Mode) ? Mode : ChatMode.General,
            GeneralSession = NormalizeId(GeneralSession), CodingSession = NormalizeId(CodingSession),
            ScienceSession = NormalizeId(ScienceSession), ActiveSession = NormalizeId(ActiveSession), ActiveProject = NormalizeId(ActiveProject),
            Model = IsValidText(Model, 512) ? Model : null,
            Reasoning = reasoning, Drafts = drafts,
        };
    }

    private static Guid? NormalizeId(Guid? value) => value == Guid.Empty ? null : value;
    private static bool IsValidText(string? value, int maximumLength) => value is { Length: > 0 }
        && value.Length <= maximumLength && !value.Any(char.IsControl);
}

/// <summary>Flows through async work without changing another client's settings. Queued runs freeze their input preferences.</summary>
internal static class AssistantClientExecutionScope
{
    private static readonly AsyncLocal<Context?> Current = new();
    internal static string ClientId => Current.Value?.ClientId ?? "desktop";
    internal static bool IsBrowser => ClientId != "desktop";
    internal static bool IsFrozen => Current.Value?.Frozen is not null;
    internal static AppSettings Resolve(AppSettings global) => Current.Value is { } context
        ? context.Frozen ?? context.Store?.Resolve(context.ClientId, global) ?? global : global;

    internal static IDisposable Enter(AssistantClientStateStore store, string clientId)
    {
        var previous = Current.Value;
        Current.Value = new(store, clientId, null);
        return new Restore(previous);
    }

    internal static IDisposable EnterFrozen(AppSettings submitted)
    {
        ArgumentNullException.ThrowIfNull(submitted);
        var previous = Current.Value;
        Current.Value = new(previous?.Store, previous?.ClientId ?? "desktop", submitted with
        {
            ReasoningEffortsByModel = new(submitted.ReasoningEffortsByModel, StringComparer.OrdinalIgnoreCase),
        });
        return new Restore(previous);
    }

    internal static Func<IDisposable> Capture(AppSettings global)
    {
        var captured = Current.Value;
        var frozen = Resolve(global);
        return () =>
        {
            var previous = Current.Value;
            Current.Value = captured is null ? null : new(captured.Store, captured.ClientId, frozen);
            return new Restore(previous);
        };
    }

    internal static Task UpdateAsync(SettingsCoordinator settings, Func<AppSettings, AppSettings> update, CancellationToken token)
    {
        if (Current.Value is not { } context) return settings.UpdateAsync(update, token);
        if (context.Frozen is not null) { context.Frozen = update(context.Frozen); return Task.CompletedTask; }
        return context.Store!.UpdateAsync(context.ClientId, settings, update, token);
    }

    internal static string Draft(Guid id, string legacy, AppSettings defaults) => Current.Value is { } context
        ? context.Store?.Draft(context.ClientId, id, legacy, defaults) ?? legacy : legacy;

    internal static Task SaveDraftAsync(Guid id, string draft, AppSettings defaults, CancellationToken token) => Current.Value is { Frozen: null } context
        ? context.Store!.SaveDraftAsync(context.ClientId, id, draft, defaults, token) : Task.CompletedTask;

    private sealed class Context(AssistantClientStateStore? store, string clientId, AppSettings? frozen)
    {
        internal AssistantClientStateStore? Store { get; } = store;
        internal string ClientId { get; } = clientId;
        internal AppSettings? Frozen { get; set; } = frozen;
    }
    private sealed class Restore(Context? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
