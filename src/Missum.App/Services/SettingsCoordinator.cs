using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.App.Services;

public sealed class SettingsCoordinator(ISettingsStore store) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppSettings Current { get; private set; } = new();

    public event EventHandler<SettingsChangedEventArgs>? Changed;

    public string DataDirectory => Path.GetDirectoryName(Path.GetFullPath(store.SettingsPath))!;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Current = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            Current = Current with { SelectedCodingModel = Current.SelectedModel };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateAsync(
        Func<AppSettings, AppSettings> update,
        CancellationToken cancellationToken = default)
        => await UpdateCoreAsync(update, null, cancellationToken).ConfigureAwait(false);

    public async Task UpdatePreferencesAsync(
        Func<AppSettings, AppSettings> update,
        long expectedRevision,
        CancellationToken cancellationToken = default)
        => await UpdateCoreAsync(update, expectedRevision, cancellationToken).ConfigureAwait(false);

    private async Task UpdateCoreAsync(
        Func<AppSettings, AppSettings> update,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        SettingsChangedEventArgs change;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (expectedRevision is { } revision && revision != Current.PreferencesRevision)
                throw new AssistantSettingsConflictException(Current.PreferencesRevision);
            var previous = Current;
            var updated = update(previous);
            var preferencesChanged = !PreferencesEqual(previous, updated);
            updated = updated with
            {
                SelectedCodingModel = updated.SelectedModel,
                PreferencesRevision = preferencesChanged
                    ? checked(previous.PreferencesRevision + 1)
                    : previous.PreferencesRevision,
            };
            // Failed writes must not publish an unsaved in-memory state.
            await store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            Current = updated;
            change = new(previous, updated, preferencesChanged);
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(this, change);
    }

    private static bool PreferencesEqual(AppSettings left, AppSettings right) =>
        left.MissumAiServerUrl == right.MissumAiServerUrl
        && left.IsAutomaticSpeechEnabled == right.IsAutomaticSpeechEnabled
        && left.LiveCaptionLanguage == right.LiveCaptionLanguage
        && left.CodingToolStepsExpanded == right.CodingToolStepsExpanded
        && left.Theme == right.Theme
        && left.AccentColor == right.AccentColor
        && left.BackgroundColor == right.BackgroundColor
        && left.Language == right.Language;

    public void Dispose() => _gate.Dispose();
}

public sealed class SettingsChangedEventArgs(AppSettings previous, AppSettings current, bool preferencesChanged) : EventArgs
{
    public AppSettings Previous { get; } = previous;
    public AppSettings Current { get; } = current;
    public bool PreferencesChanged { get; } = preferencesChanged;
}

public sealed class AssistantSettingsConflictException(long actualRevision)
    : InvalidOperationException("Die Einstellungen wurden auf einem anderen Gerät geändert. Der lokale Entwurf bleibt erhalten.")
{
    public long ActualRevision { get; } = actualRevision;
}
