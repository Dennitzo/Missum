using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace Missum.App.Controls;

/// <summary>Makes the existing InfoBar text selectable and offers an exact, complete copy.</summary>
public static class NativeNotice
{
    private static readonly ConditionalWeakTable<InfoBar, NoticeState> States = new();
    private const string CopyLabel = "Fehlermeldung kopieren";

    /// <summary>
    /// Keeps Title, Message, the default template and an existing action intact.
    /// Repeated attachment does not add controls or property subscriptions.
    /// </summary>
    public static InfoBar Attach(InfoBar notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        _ = States.GetValue(notice, static bar => new NoticeState(bar));
        return notice;
    }

    internal static string GetCopyText(InfoBar notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var title = notice.Title ?? "";
        var message = notice.Message ?? "";
        // Do not trim, parse Markdown, or normalize LaTeX and line breaks.
        return title.Length == 0 ? message : message.Length == 0 ? title
            : title + Environment.NewLine + Environment.NewLine + message;
    }

    internal static IEnumerable<TextBlock> NoticeTextBlocks(InfoBar notice)
    {
        var pending = new Queue<DependencyObject>();
        pending.Enqueue(notice);
        while (pending.TryDequeue(out var element))
        {
            if (ReferenceEquals(element, notice.ActionButton) || ReferenceEquals(element, notice.Content)) continue;
            // These are the two named text parts of WinUI's InfoBar template.
            // Do not turn the label of an existing action into selectable text.
            if (element is TextBlock { Name: "Title" or "Message" } block) yield return block;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
                pending.Enqueue(VisualTreeHelper.GetChild(element, index));
        }
    }

    private sealed class NoticeState
    {
        private readonly InfoBar _notice;
        private readonly Button _copy = new()
        {
            Content = new FontIcon { Glyph = "\uE8C8", FontSize = 14 },
            Padding = new Thickness(8), MinWidth = 32, MinHeight = 32,
            VerticalAlignment = VerticalAlignment.Center,
        };
        private readonly MenuFlyoutItem _copyMenu = new() { Text = CopyLabel };
        private bool _refreshQueued;
        private long _loadRevision;
        private DateTimeOffset _lastFailureDiagnostic;

        public NoticeState(InfoBar notice)
        {
            _notice = notice;
            AutomationProperties.SetName(_copy, CopyLabel);
            ToolTipService.SetToolTip(_copy, CopyLabel);
            _copy.Click += (_, _) => TryUiUpdate("copy.Click", () => Copy(_copy));
            _copyMenu.Click += (_, _) => TryUiUpdate("copyMenu.Click", () => Copy(_copyMenu));
            if (_notice.ActionButton is null) _notice.ActionButton = _copy;
            // Preserve existing actions and menus. A context copy action also
            // covers notices whose primary ActionButton already has a purpose.
            if (_notice.ContextFlyout is null) _notice.ContextFlyout = new MenuFlyout();
            if (_notice.ContextFlyout is MenuFlyout menu) menu.Items.Add(_copyMenu);
            _notice.Loaded += OnLoaded;
            _notice.Unloaded += OnUnloaded;
            _notice.RegisterPropertyChangedCallback(InfoBar.MessageProperty, OnTextChanged);
            _notice.RegisterPropertyChangedCallback(InfoBar.TitleProperty, OnTextChanged);
            _notice.RegisterPropertyChangedCallback(InfoBar.IsOpenProperty, OnTemplateChanged);
            _notice.RegisterPropertyChangedCallback(Control.TemplateProperty, OnTemplateChanged);
            TryUiUpdate("Attach", () =>
            {
                RefreshCopyState();
                RequestTextSelection();
            });
        }

        private void OnLoaded(object sender, RoutedEventArgs args)
        {
            _loadRevision++;
            _refreshQueued = false;
            TryUiUpdate("Loaded", () =>
            {
                EnableTextSelection();
                RequestTextSelection();
            });
        }

        private void OnUnloaded(object sender, RoutedEventArgs args)
        {
            // Invalidate any queued work from the discarded template/view.
            _loadRevision++;
            _refreshQueued = false;
        }

        private void OnTextChanged(DependencyObject sender, DependencyProperty property)
        {
            TryUiUpdate("text.Changed", () =>
            {
                RefreshCopyState();
                // A previously empty Title/Message may be materialized by the
                // template after this callback. Refresh once, after that binding.
                RequestTextSelection();
            });
        }

        private void OnTemplateChanged(DependencyObject sender, DependencyProperty property) =>
            TryUiUpdate("template.Changed", RequestTextSelection);

        private void RefreshCopyState()
        {
            var enabled = GetCopyText(_notice).Length != 0;
            _copy.IsEnabled = enabled;
            _copyMenu.IsEnabled = enabled;
            ToolTipService.SetToolTip(_copy, CopyLabel);
            ToolTipService.SetToolTip(_copyMenu, CopyLabel);
        }

        private void RequestTextSelection()
        {
            if (!_notice.IsLoaded || _refreshQueued) return;
            _refreshQueued = true;
            var revision = _loadRevision;
            if (!_notice.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (revision != _loadRevision) return;
                _refreshQueued = false;
                TryUiUpdate("selection.Dispatcher", () =>
                {
                    if (_notice.IsLoaded) EnableTextSelection();
                });
            })) _refreshQueued = false;
        }

        private void TryUiUpdate(string phase, Action update)
        {
            try { update(); }
            catch (Exception exception) when (IsRecoverableUiFailure(exception))
            {
                // A closing/replaced WinUI template must not escape a WinRT
                // dispatcher callback. The next load or message update retries;
                // no timer or model request is started here.
                _refreshQueued = false;
                ReportUiFailure(phase, exception);
            }
        }

        private static bool IsRecoverableUiFailure(Exception exception) =>
            exception is not (OutOfMemoryException or AccessViolationException or StackOverflowException)
            && exception.HResult != unchecked((int)0x8007000E)
            && (exception is COMException or InvalidOperationException or ArgumentException
                || exception.HResult == unchecked((int)0x80004005));

        private void ReportUiFailure(string phase, Exception exception)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastFailureDiagnostic < TimeSpan.FromSeconds(5)) return;
            _lastFailureDiagnostic = now;
            try
            {
                var path = Path.Combine(App.Current.DataDirectory, "Diagnostics", "native-ui-render-errors.jsonl");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, JsonSerializer.Serialize(new
                {
                    at = now, phase = "NativeNotice." + phase, processId = Environment.ProcessId,
                    managedThreadId = Environment.CurrentManagedThreadId,
                    exceptionType = exception.GetType().FullName, hresult = $"0x{exception.HResult:X8}",
                    message = exception.Message, stack = exception.StackTrace,
                }) + Environment.NewLine);
            }
            catch (Exception loggingException) when (loggingException is not (OutOfMemoryException or AccessViolationException or StackOverflowException)
                && loggingException.HResult != unchecked((int)0x8007000E))
            {
                Debug.WriteLine($"Missum notice diagnostic failed ({phase}): {loggingException}; original: {exception}");
            }
        }

        private void EnableTextSelection()
        {
            _notice.ApplyTemplate();
            foreach (var block in NoticeTextBlocks(_notice))
            {
                if (!block.IsTextSelectionEnabled) block.IsTextSelectionEnabled = true;
                if (block.TextWrapping != TextWrapping.Wrap) block.TextWrapping = TextWrapping.Wrap;
                if (block.TextTrimming != TextTrimming.None) block.TextTrimming = TextTrimming.None;
            }
        }

        private void Copy(DependencyObject action)
        {
            var text = GetCopyText(_notice);
            if (text.Length == 0) return;
            var copied = TryCopy(text);
            ToolTipService.SetToolTip(action, copied ? "Fehlermeldung kopiert"
                : "Zwischenablage vorübergehend nicht verfügbar. Bitte erneut kopieren.");
        }

        private static bool TryCopy(string text)
        {
            try
            {
                var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                package.SetText(text);
                Clipboard.SetContent(package);
                Clipboard.Flush();
                return true;
            }
            catch (COMException exception) when (exception.HResult != unchecked((int)0x8007000E))
            {
                // An application may hold the clipboard open. Keep the error
                // and selection intact so the user can retry immediately.
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
