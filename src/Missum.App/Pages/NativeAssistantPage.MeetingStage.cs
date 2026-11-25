using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Missum.App.Controls;
using Missum.App.Services.Meetings;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private Grid? _stageRoot;
    private Grid? _stageTiles;
    private ContentControl? _stageHeader;
    private ContentControl? _stageBar;
    private ContentControl? _stageNotice;
    private string? _stageTileSignature;

    private Grid MeetingStageRoot()
    {
        if (_stageRoot is not null) return _stageRoot;
        _stageRoot = new Grid { Padding = new Thickness(24, 18, 24, 20), RowSpacing = 14 };
        _stageRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _stageRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _stageRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _stageRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _stageHeader = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, IsTabStop = false };
        _stageNotice = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, IsTabStop = false };
        _stageTiles = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        _stageTiles.SizeChanged += (_, _) => { _stageTileSignature = null; _meetingStageDirty = true; };
        _stageBar = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Center, IsTabStop = false };
        Grid.SetRow(_stageNotice, 1);
        Grid.SetRow(_stageTiles, 2);
        Grid.SetRow(_stageBar, 3);
        _stageRoot.Children.Add(_stageHeader);
        _stageRoot.Children.Add(_stageNotice);
        _stageRoot.Children.Add(_stageTiles);
        _stageRoot.Children.Add(_stageBar);
        return _stageRoot;
    }

    private void RenderMeetingStage()
    {
        MeetingStageRoot();
        var service = Meetings;
        var room = service.Room;
        var view = service.View;
        _stageNotice!.Content = CurrentMeetingError is { Length: > 0 } error
            ? new InfoBar { IsOpen = true, Severity = InfoBarSeverity.Warning, Message = error, IsClosable = true }
            : view.Connection == MeetingConnectionState.Reconnecting
                ? new InfoBar { IsOpen = true, Severity = InfoBarSeverity.Informational, Message = "Verbindung zum Meeting wird wiederhergestellt …", IsClosable = false }
                : null;
        if (_stageNotice.Content is InfoBar bar) bar.Closed += (_, _) => { _meetingError = null; MarkMeetingsDirty(stage: true, overlay: true); };

        if (room is null)
        {
            _stageHeader!.Content = null;
            _stageBar!.Content = null;
            _stageTiles!.Children.Clear();
            _stageTileSignature = null;
            _stageTiles.Children.Add(new ProgressRing { IsActive = true, Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            return;
        }

        var (title, live, agent, participants) = room.Read(s => (
            s.Title,
            s.IsLive,
            new MeetingAgentStatus { State = s.Agent.State, Status = s.Agent.Status },
            s.Participants.Where(p => p.Connected).Select(MeetingJson.Clone).ToList()));
        _stageHeader!.Content = StageHeader(title, live, view);

        var inMeeting = live && view.Connection == MeetingConnectionState.Connected;
        if (!inMeeting)
        {
            _stageTileSignature = null;
            _stageTiles!.Children.Clear();
            _stageTiles.RowDefinitions.Clear();
            _stageTiles.ColumnDefinitions.Clear();
            _stageTiles.Children.Add(view.Role == "guest" ? GuestLobby(view, title) : HostLobby(agent));
            _stageBar!.Content = null;
            return;
        }

        RenderStageTiles(participants, agent, view);
        _stageBar!.Content = MeetingControlBar(compact: false);
    }

    private static Grid StageHeader(string title, bool live, MeetingView view)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        titleRow.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (live)
            titleRow.Children.Add(new Border
            {
                Background = ThemeBrush("MissumDangerBrush", 200),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 2, 8, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "LIVE", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Colors.White) },
            });
        heading.Children.Add(titleRow);
        heading.Children.Add(new TextBlock
        {
            Text = view.Role == "guest"
                ? "Du bist Gast in diesem Meeting. Notes und Whiteboard werden auch in deinem Projektordner gespiegelt."
                : view.Folder is { } folder ? "Gespeichert in " + ShortFolder(folder) : "Missum schreibt live mit.",
            FontSize = 12,
            Foreground = ThemeBrush("MissumMutedTextBrush", 160),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        grid.Children.Add(heading);
        if (view.ShareLink is { } link && (view.Role == "host" || live))
        {
            var linkPanel = InviteLinkBox(link, view.Role == "host");
            Grid.SetColumn(linkPanel, 1);
            grid.Children.Add(linkPanel);
        }
        return grid;
    }

    private static string ShortFolder(string folder)
    {
        var parts = folder.TrimEnd('\\', '/').Split('\\', '/');
        return parts.Length <= 3 ? folder : "…\\" + string.Join("\\", parts[^3..]);
    }

    private static Border InviteLinkBox(string link, bool host)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new FontIcon { Glyph = "", FontSize = 14, Foreground = NativeIconPalette.BrushFor("navigation"), Margin = new Thickness(4, 0, 2, 0) });
        row.Children.Add(new TextBlock
        {
            Text = link,
            FontSize = 12,
            MaxWidth = 260,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeBrush("MissumMutedTextBrush", 170),
            IsTextSelectionEnabled = true,
        });
        var copy = new Button
        {
            Content = new TextBlock { Text = "Link kopieren", FontSize = 12 },
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 4, 10, 5),
            Background = ThemeBrush("MissumSelectionBrush", 50),
            BorderThickness = new Thickness(0),
        };
        copy.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(link);
            Clipboard.SetContent(package);
            copy.Content = new TextBlock { Text = "Kopiert ✓", FontSize = 12 };
        };
        AutomationProperties.SetName(copy, "Einladungslink kopieren");
        row.Children.Add(copy);
        var box = new Border
        {
            Background = ThemeBrush("MissumLayerStrongBrush", 40),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(6, 4, 4, 4),
            Child = row,
        };
        ToolTipService.SetToolTip(box, host
            ? "Teile diesen Link mit Personen im selben Netzwerk. Ein Klick darauf öffnet Missum und tritt dem Meeting bei."
            : "Einladungslink dieses Meetings");
        return box;
    }

    private ScrollViewer? _hostLobby;
    private MeetingAgentVisual? _hostLobbyVisual;
    private TextBox? _hostLobbyName;
    private Guid _hostLobbySession;

    /// <summary>
    /// The lobby is built once per session and only updated afterwards, so a toggle
    /// or text field is never replaced while someone is using it.
    /// </summary>
    private ScrollViewer HostLobby(MeetingAgentStatus agent)
    {
        if (_hostLobby is null || _hostLobbySession != Meetings.SessionId)
        {
            _hostLobbySession = Meetings.SessionId;
            _hostLobby = BuildHostLobby();
        }
        _hostLobbyVisual!.SetState(agent.State == "idle" ? "listening" : agent.State);
        if (_hostLobbyName is { FocusState: FocusState.Unfocused } name && name.Text != Meetings.DisplayName) name.Text = Meetings.DisplayName;
        return _hostLobby;
    }

    private ScrollViewer BuildHostLobby()
    {
        var card = new Border
        {
            MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = ThemeBrush("MissumLayerBrush", 32),
            BorderBrush = ThemeBrush("MissumStrokeBrush", 48),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(28, 26, 28, 26),
        };
        var stack = new StackPanel { Spacing = 14 };
        _hostLobbyVisual = new MeetingAgentVisual { Height = 96, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(_hostLobbyVisual);
        stack.Children.Add(new TextBlock { Text = "Bereit für dein Meeting", FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock
        {
            Text = "Missum hört mit, fasst Chat und Gespräch in den Notes zusammen und ordnet alles auf dem Whiteboard. Schreib im Chat @Missum, um direkt zu fragen.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = ThemeBrush("MissumMutedTextBrush", 160),
        });
        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, HorizontalAlignment = HorizontalAlignment.Center };
        var mic = AccentToggle("Mikrofon", _meetingStartMic);
        mic.Toggled += (_, _) => _meetingStartMic = mic.IsOn;
        var cam = AccentToggle("Kamera", _meetingStartCamera);
        cam.Toggled += (_, _) => _meetingStartCamera = cam.IsOn;
        toggles.Children.Add(mic);
        toggles.Children.Add(cam);
        stack.Children.Add(toggles);
        stack.Children.Add(PrimaryMeetingButton("Meeting starten", "\uE714", async () =>
        {
            await ApplyLobbyNameAsync();
            await StartMeetingFromUiAsync();
        }));
        stack.Children.Add(MeetingPrepareLine());
        stack.Children.Add(new Border { Height = 1, Background = ThemeBrush("MissumStrokeBrush", 48), Margin = new Thickness(0, 4, 0, 4) });
        stack.Children.Add(JoinByLinkRow());
        card.Child = stack;
        return new ScrollViewer { Content = card, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Center };
    }

    /// <summary>Takes the lobby's name field over before starting or joining, so others see it from the first moment.</summary>
    private async Task ApplyLobbyNameAsync()
    {
        var value = _hostLobbyName?.Text.Trim() ?? "";
        if (value.Length > 0 && value != Meetings.DisplayName)
            await Meetings.ApplySettingsAsync(value, _settings.Current.MeetingCameraDeviceId, _settings.Current.MeetingMicrophoneDevice);
    }

    /// <summary>A ToggleSwitch that uses Missum's accent instead of the Windows accent.</summary>
    private static ToggleSwitch AccentToggle(string header, bool on)
    {
        var toggle = new ToggleSwitch { Header = header, IsOn = on, OnContent = "An", OffContent = "Aus" };
        var accent = ThemeBrush("MissumAccentBrush", 170);
        foreach (var key in new[] { "ToggleSwitchFillOn", "ToggleSwitchFillOnPointerOver", "ToggleSwitchFillOnPressed",
                     "ToggleSwitchStrokeOn", "ToggleSwitchStrokeOnPointerOver", "ToggleSwitchStrokeOnPressed" })
            toggle.Resources[key] = accent;
        var knob = ThemeBrush("MissumAccentTextBrush", 255);
        foreach (var key in new[] { "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed" })
            toggle.Resources[key] = knob;
        AutomationProperties.SetName(toggle, header);
        return toggle;
    }

    /// <summary>A text box whose focus line uses Missum's accent.</summary>
    private static TextBox AccentTextBox(string placeholder)
    {
        var box = new TextBox { PlaceholderText = placeholder, CornerRadius = new CornerRadius(10) };
        box.Resources["TextControlBorderBrushFocused"] = ThemeBrush("MissumAccentBrush", 170);
        box.Resources["TextControlSelectionHighlightColor"] = ThemeBrush("MissumAccentBrush", 170);
        AutomationProperties.SetName(box, placeholder);
        return box;
    }

    private StackPanel JoinByLinkRow()
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = "Einem anderen Meeting beitreten", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // The name is set before joining: it sits right above the invitation link.
        _hostLobbyName = AccentTextBox("Dein Name im Meeting");
        _hostLobbyName.Header = "Dein Name im Meeting";
        _hostLobbyName.Text = Meetings.DisplayName;
        _hostLobbyName.MaxLength = 60;
        _hostLobbyName.Margin = new Thickness(0, 0, 0, 2);
        _hostLobbyName.LostFocus += (_, _) => _ = ApplyLobbyNameAsync();
        _hostLobbyName.KeyDown += (_, args) => { if (args.Key == Windows.System.VirtualKey.Enter) { args.Handled = true; _ = ApplyLobbyNameAsync(); } };
        stack.Children.Add(_hostLobbyName);
        var input = AccentTextBox("Einladungslink einfügen");
        var join = new Button
        {
            Content = "Beitreten",
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 6, 14, 7),
            Background = ThemeBrush("MissumAccentSubtleBrush", 50),
            Foreground = ThemeBrush("MissumAccentReadableBrush", 190),
            BorderThickness = new Thickness(0),
        };
        join.Resources["ButtonBackgroundPointerOver"] = ThemeBrush("MissumSelectionBrush", 60);
        join.Resources["ButtonForegroundPointerOver"] = ThemeBrush("MissumTextBrush", 240);
        Grid.SetColumn(join, 1);
        join.Click += async (_, _) =>
        {
            if (!MeetingLink.TryParse(input.Text, out var link) || link is null)
            {
                ShowMeetingError("Das ist kein gültiger Missum-Einladungslink.");
                return;
            }
            join.IsEnabled = false;
            try
            {
                await ApplyLobbyNameAsync();
                await JoinMeetingAsync(link);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException) { ShowMeetingError(exception.Message); }
            finally { join.IsEnabled = true; }
        };
        input.KeyDown += (_, args) => { if (args.Key == Windows.System.VirtualKey.Enter) { args.Handled = true; join.Focus(FocusState.Programmatic); } };
        row.Children.Add(input);
        row.Children.Add(join);
        stack.Children.Add(row);
        return stack;
    }

    private Border GuestLobby(MeetingView view, string title)
    {
        var card = new Border
        {
            MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = ThemeBrush("MissumLayerBrush", 32),
            BorderBrush = ThemeBrush("MissumStrokeBrush", 48),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(28, 24, 28, 24),
        };
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(new FontIcon { Glyph = "", FontSize = 34, Foreground = NativeIconPalette.BrushFor("navigation") });
        stack.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        stack.Children.Add(new TextBlock
        {
            Text = view.Connection is MeetingConnectionState.Connecting or MeetingConnectionState.Reconnecting
                ? "Verbindung wird aufgebaut …"
                : "Du bist nicht verbunden. Notes und Whiteboard siehst du als Kopie aus deinem Projektordner.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = ThemeBrush("MissumMutedTextBrush", 160),
        });
        stack.Children.Add(PrimaryMeetingButton("Erneut beitreten", "", async () =>
        {
            await Meetings.RejoinAsync(_lifetime.Token);
            await Meetings.SetMicrophoneAsync(true);
        }));
        card.Child = stack;
        return card;
    }

    private void RenderStageTiles(List<MeetingParticipant> participants, MeetingAgentStatus agent, MeetingView view)
    {
        var tiles = _stageTiles!;
        var people = participants.OrderBy(p => p.IsAgent ? 0 : p.Id == view.LocalParticipantId ? 1 : 2).ThenBy(p => p.JoinedAt).ToList();
        var count = Math.Max(1, people.Count);
        var width = Math.Max(320, tiles.ActualWidth);
        var height = Math.Max(240, tiles.ActualHeight);
        var columns = BestColumns(count, width, height);
        var rows = (int)Math.Ceiling(count / (double)columns);
        var signature = string.Join("|", people.Select(p => $"{p.Id}:{p.CameraOn}:{p.MicOn}:{p.Name}:{IsSpeaking(p.Id)}:{MeetingVideoFor(p.Id, p.CameraOn) is not null}"))
            + $"#{columns}x{rows}#{agent.State}:{agent.Status}";
        if (signature == _stageTileSignature) return;
        _stageTileSignature = signature;

        tiles.Children.Clear();
        tiles.ColumnDefinitions.Clear();
        tiles.RowDefinitions.Clear();
        var tileHeight = Math.Floor(Math.Min((height - (rows - 1) * 12) / rows, (width - (columns - 1) * 12) / columns * 9 / 16));
        var tileWidth = Math.Floor(tileHeight * 16 / 9);
        // Rows are centered panels, so an incomplete last row sits in the middle like in every call app.
        var lines = new StackPanel { Spacing = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        for (var row = 0; row < rows; row++)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var person in people.Skip(row * columns).Take(columns))
            {
                var tile = person.IsAgent ? AgentTile(agent, tileHeight, compact: false) : PersonTile(person, tileHeight, person.Id == view.LocalParticipantId);
                tile.Width = tileWidth;
                line.Children.Add(tile);
            }
            lines.Children.Add(line);
        }
        tiles.Children.Add(lines);
    }

    private static int BestColumns(int count, double width, double height)
    {
        var best = 1;
        var bestArea = 0.0;
        for (var columns = 1; columns <= Math.Min(count, 4); columns++)
        {
            var rows = (int)Math.Ceiling(count / (double)columns);
            var tileWidth = (width - (columns - 1) * 12) / columns;
            var tileHeight = Math.Min((height - (rows - 1) * 12) / rows, tileWidth * 9 / 16);
            var area = tileHeight * tileHeight * 16 / 9;
            if (area > bestArea) { bestArea = area; best = columns; }
        }
        return best;
    }

    private Border PersonTile(MeetingParticipant person, double height, bool self)
    {
        var grid = new Grid();
        var video = MeetingVideoFor(person.Id, person.CameraOn);
        if (video is not null)
        {
            grid.Children.Add(new Image
            {
                Source = video,
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                // A self view is mirrored like every video call app does.
                RenderTransformOrigin = new Windows.Foundation.Point(.5, .5),
                RenderTransform = self ? new ScaleTransform { ScaleX = -1 } : null,
            });
        }
        else
        {
            var avatar = Avatar(person, Math.Clamp(height * .34, 44, 110));
            avatar.HorizontalAlignment = HorizontalAlignment.Center;
            avatar.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(avatar);
        }
        var speaking = IsSpeaking(person.Id) && person.MicOn;
        grid.Children.Add(NameChip(self ? person.Name + " (Du)" : person.Name, person.MicOn, speaking: speaking));
        if (!self)
        {
            var mute = new Button
            {
                Width = 30, Height = 30, Padding = new Thickness(0), CornerRadius = new CornerRadius(8), Margin = new Thickness(8),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), BorderThickness = new Thickness(0),
                Content = new FontIcon { Glyph = Meetings.Audio.IsMuted(person.Id) ? "" : "", FontSize = 13, Foreground = new SolidColorBrush(Colors.White) },
            };
            ToolTipService.SetToolTip(mute, Meetings.Audio.IsMuted(person.Id) ? "Für mich wieder hörbar machen" : "Für mich stummschalten");
            mute.Click += (_, _) =>
            {
                Meetings.Audio.SetMuted(person.Id, !Meetings.Audio.IsMuted(person.Id));
                _stageTileSignature = null;
                MarkMeetingsDirty(stage: true);
            };
            grid.Children.Add(mute);
        }
        return new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Blend(ParticipantColors[person.ColorIndex % ParticipantColors.Length], Color.FromArgb(255, 0x22, 0x22, 0x24), .82)),
            BorderBrush = speaking ? new SolidColorBrush(SpeakingGreen) : new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(3),
            Child = grid,
        };
    }

    private static Color Blend(Color a, Color b, double amountOfB) => Color.FromArgb(255,
        (byte)(a.R + (b.R - a.R) * amountOfB),
        (byte)(a.G + (b.G - a.G) * amountOfB),
        (byte)(a.B + (b.B - a.B) * amountOfB));
}
