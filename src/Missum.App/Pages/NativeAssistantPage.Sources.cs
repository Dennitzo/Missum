using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private sealed record SourceAction(string Id, string Title, string[] Urls, DateTimeOffset Time,
        JsonElement? Attachment = null, IReadOnlyDictionary<string, string>? PageTitles = null);
    private readonly Dictionary<Guid, SourceAction[]> _sessionSources = [];
    private readonly HashSet<Guid> _sourceTabs = [];
    private Button? _sourcesTabButton;
    private Border? _sourcesTabContainer;
    private string? _sourcesSignature;
    private string? _sourcesContentSignature;
    private string? _childSourcesSignature;
    private Guid? _sourcesContentOwner;
    private Guid? _activeSourcesSession;
    private readonly ContentControl _sourcesHost = new() { Visibility = Visibility.Collapsed, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };

    private IEnumerable<JsonElement> OwnerSourceMessages() => _messages.Values.Concat(_subagents.Values
        .Where(child => child.SessionId == _session).SelectMany(child => child.Messages.Values));

    private void RefreshSubagentSources()
    {
        var signature = _session + "|" + string.Join("|", _subagents.Values.Where(child => child.SessionId == _session)
            .SelectMany(child => child.Messages.Values).SelectMany(message => Items(message, "toolSteps"))
            .Where(step => S(step, "tool").StartsWith("web.", StringComparison.Ordinal) || S(step, "tool").StartsWith("research.", StringComparison.Ordinal))
            .Select(step => S(step, "id") + S(step, "updatedAt") + S(step, "inputJson").GetHashCode(StringComparison.Ordinal)
                + S(step, "outputJson").GetHashCode(StringComparison.Ordinal)));
        if (_childSourcesSignature == signature) return;
        _childSourcesSignature = signature;
        _sourcesSignature = null;
        RenderSources(_snapshot);
    }

    private SourceAction[] WebSourceActions()
    {
        var actions = new List<SourceAction>();
        foreach (var message in OwnerSourceMessages())
        foreach (var step in Items(message, "toolSteps"))
        {
            var tool = S(step, "tool");
            if (!(tool.StartsWith("web.", StringComparison.Ordinal) || tool.StartsWith("research.", StringComparison.Ordinal))) continue;
            var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pageTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            CollectSourceUrls(S(step, "outputJson"), urls, pageTitles);
            CollectSourceUrls(S(step, "inputJson"), urls, pageTitles);
            if (urls.Count == 0) continue;
            var title = tool == "web.search" ? "Websuche" : tool == "web.fetch" ? "Webseite" : "Recherche";
            var input = ToolStepView.ReadMetadata(S(step, "inputJson"));
            var query = S(input, "query");
            actions.Add(new(S(step, "id"), query.Length > 0 ? title + ": " + query : title,
                urls.ToArray(), DateTimeOffset.TryParse(S(step, "updatedAt", S(step, "startedAt")), out var time) ? time : MessageCreatedAt(message), PageTitles: pageTitles));
        }
        var state = ResearchState(_session);
        var actionUrls = actions.SelectMany(action => action.Urls).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items(state.Detail, "works"))
        {
            var url = S(item, "url", S(item, "canonicalUrl"));
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && actionUrls.Add(uri.AbsoluteUri))
                actions.Add(new("research-source:" + uri.AbsoluteUri, S(item, "title", uri.Host), [uri.AbsoluteUri], DateTimeOffset.TryParse(S(item, "updatedAt"), out var updated) ? updated : DateTimeOffset.MinValue,
                    PageTitles: new Dictionary<string, string> { [uri.AbsoluteUri] = S(item, "title", uri.Host) }));
        }
        foreach (var item in Items(_snapshot, "documents").Concat(Items(_snapshot, "attachments")))
            actions.Add(new("attachment:" + S(item, "id"), S(item, "fileName"), [],
                DateTimeOffset.TryParse(S(item, "createdAt"), out var created) ? created : DateTimeOffset.MinValue, item.Clone()));
        return ResolveSourceTitles(actions.GroupBy(item => item.Id).Select(group => group.Last()).OrderByDescending(item => item.Time).ToArray(), Items(state.Detail, "works"));
    }

    private static SourceAction[] ResolveSourceTitles(SourceAction[] actions, JsonElement[] works)
    {
        // Fetch responses lack a title field. Reuse titles already discovered in
        // this session, without extra network requests or cross-session state.
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in actions)
            foreach (var page in action.PageTitles ?? new Dictionary<string, string>())
                if (!string.IsNullOrWhiteSpace(page.Value)) known.TryAdd(page.Key, page.Value);
        foreach (var work in works)
        {
            var title = S(work, "title").Trim();
            if (title.Length > 0 && Uri.TryCreate(S(work, "url", S(work, "canonicalUrl")), UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https") known.TryAdd(uri.AbsoluteUri, title);
        }
        return actions.Select(action =>
        {
            var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var url in action.Urls)
                if (known.TryGetValue(url, out var title)) titles[url] = title;
            // An original request and its redirect target refer to the same page.
            if (action.Title == "Webseite" && titles.Values.FirstOrDefault() is { } fetchedTitle)
                foreach (var url in action.Urls) titles.TryAdd(url, fetchedTitle);
            return action with { PageTitles = titles };
        }).ToArray();
    }

    private static void CollectSourceUrls(string json, HashSet<string> urls, Dictionary<string, string> titles)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try { using var document = JsonDocument.Parse(json); CollectSourceUrls(document.RootElement, urls, titles, 0); }
        catch (JsonException) { }
    }

    private static void CollectSourceUrls(JsonElement value, HashSet<string> urls, Dictionary<string, string> titles, int depth)
    {
        if (depth > 24 || urls.Count >= 1000) return;
        if (value.ValueKind == JsonValueKind.Object)
        {
            var title = S(value, "title", S(value, "pageTitle", S(value, "name"))).Trim();
            foreach (var property in value.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String
                    && property.Name.Contains("url", StringComparison.OrdinalIgnoreCase)
                    && !property.Name.Contains("thumbnail", StringComparison.OrdinalIgnoreCase)
                    && !property.Name.Contains("image", StringComparison.OrdinalIgnoreCase)
                    && !property.Name.Contains("icon", StringComparison.OrdinalIgnoreCase)
                    && Uri.TryCreate(property.Value.GetString(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                {
                    urls.Add(uri.AbsoluteUri);
                    if (title.Length > 0) titles.TryAdd(uri.AbsoluteUri, title);
                }
                else CollectSourceUrls(property.Value, urls, titles, depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) CollectSourceUrls(child, urls, titles, depth + 1);
        else if (value.ValueKind == JsonValueKind.String && value.GetString() is { } nested && nested.TrimStart().StartsWith('{'))
        { try { using var document = JsonDocument.Parse(nested); CollectSourceUrls(document.RootElement, urls, titles, depth + 1); } catch (JsonException) { } }
    }

    private Button SourceButton(string title, string url)
    {
        var button = SidebarButton("\uE774", title); button.Padding = new(0, 6, 0, 6); button.MinHeight = 48; button.Height = double.NaN;
        var row = (Grid)button.Content;
        row.Children.OfType<FontIcon>().Single().Foreground = Missum.App.Controls.NativeIconPalette.BrushFor("web");
        var oldTitle = row.Children.OfType<TextBlock>().Single();
        var labels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = title, FontSize = 14, Foreground = Brush(230), TextTrimming = TextTrimming.CharacterEllipsis });
        labels.Children.Add(new TextBlock { Text = url, FontSize = 12, Foreground = ThemeBrush("MissumMutedTextBrush", 145), Opacity = .75, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(labels, Grid.GetColumn(oldTitle)); row.Children.Remove(oldTitle); row.Children.Add(labels);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, title + " · " + url);
        ToolTipService.SetToolTip(button, title + "\n" + url);
        button.Click += async (_, _) =>
        { try { await Launcher.LaunchUriAsync(new Uri(url)); } catch (Exception exception) { ShowError(exception.Message); } };
        return button;
    }

    private Button SourceActionButton(SourceAction action)
    {
        if (action.Attachment is not { } item) return SourceButton(SourcePageTitle(action, action.Urls[0]), action.Urls[0]);
        var id = S(item, "id");
        var document = item.TryGetProperty("pageCount", out _);
        var button = SidebarButton("\uE8A5", action.Title);
        ((Grid)button.Content).Children.OfType<FontIcon>().Single().Foreground =
            Missum.App.Controls.NativeIconPalette.BrushFor(Missum.App.Controls.NativeIconPalette.FileKey(action.Title));
        button.Padding = new(0, 6, 0, 6); button.MinHeight = 32;
        ToolTipService.SetToolTip(button, action.Title);
        var menu = new MenuFlyout(); var remove = new MenuFlyoutItem { Text = "Anhang entfernen" };
        remove.Click += async (_, _) => await CommandAsync(document ? "document.remove" : "attachment.remove",
            document ? new { documentId = id } : (object)new { attachmentId = id });
        menu.Items.Add(remove); button.ContextFlyout = menu;
        return button;
    }

    private void RenderWebSources()
    {
        var sources = WebSourceActions(); _sessionSources[_session] = sources;
        foreach (var item in sources.Take(1)) SourcesPanel.Children.Add(SourceActionButton(item));
        if (sources.Length > 0)
            SourcesPanel.Children.Add(AllOutputsButton("Alle Quellen anzeigen", OpenSourcesTab));
        if (_activeSourcesSession == _session) RenderSourcesContent();
    }

    private static Button AllOutputsButton(string name, Action open)
    {
        var all = SidebarButton("\uE71B", "Alle anzeigen"); all.Padding = new(0, 6, 0, 6);
        all.Foreground = ThemeBrush("MissumMutedTextBrush", 145);
        var row = (Grid)all.Content;
        row.Children.OfType<TextBlock>().Single().Opacity = .75;
        var icon = row.Children.OfType<FontIcon>().Single();
        icon.Foreground = Missum.App.Controls.NativeIconPalette.BrushFor("link"); icon.Opacity = .75;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(all, name);
        ToolTipService.SetToolTip(all, name);
        all.Click += (_, _) => open();
        return all;
    }

    private void OpenSourcesTab()
    {
        ShowChatView(); BodyGrid.Visibility = Visibility.Collapsed;
        _sourceTabs.Add(_session); _activeSourcesSession = _session;
        if (_sourcesHost.Parent is null)
        {
            var parent = (Panel)ResearchHost.Parent; parent.Children.Add(_sourcesHost);
            Grid.SetRow(_sourcesHost, Grid.GetRow(ResearchHost)); Grid.SetColumn(_sourcesHost, Grid.GetColumn(ResearchHost));
        }
        _sourcesHost.Visibility = Visibility.Visible; RenderSourcesContent(); RenderSessionTabs();
    }

    private void RenderSourcesContent()
    {
        var sources = _sessionSources.GetValueOrDefault(_session, []);
        var signature = _session + string.Join("|", sources.Select(item => item.Id + item.Title + item.Time + string.Join("|", item.Urls.Select(url => url + SourcePageTitle(item, url)))));
        if (_sourcesContentSignature == signature) return;
        var offset = _sourcesContentOwner == _session && _sourcesHost.Content is ScrollViewer previous ? previous.VerticalOffset : 0;
        _sourcesContentSignature = signature; _sourcesContentOwner = _session;
        var body = new StackPanel { Spacing = 16, Padding = new Thickness(24) };
        body.Children.Add(new TextBlock { Text = "Quellen", FontSize = 24 });
        foreach (var item in sources)
        {
            var group = new StackPanel { Spacing = 4 }; group.Children.Add(new TextBlock { Text = item.Title, FontSize = 16, TextWrapping = TextWrapping.Wrap });
            if (item.Attachment is not null) group.Children.Add(SourceActionButton(item));
            foreach (var url in item.Urls) group.Children.Add(SourceButton(SourcePageTitle(item, url), url));
            body.Children.Add(group);
        }
        var scroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        if (offset > 0) scroll.Loaded += (_, _) => scroll.ChangeView(null, offset, null, disableAnimation: true);
        _sourcesHost.Content = scroll;
    }

    private static string SourcePageTitle(SourceAction action, string url) =>
        action.PageTitles?.GetValueOrDefault(url) is { Length: > 0 } title ? title : new Uri(url).Host;

    private int RenderSourcesTab(int index)
    {
        if (!_sourceTabs.Contains(_session))
        {
            if (_sourcesTabContainer is not null) SessionTabsPanel.Children.Remove(_sourcesTabContainer);
            return index;
        }
        if (_sourcesTabContainer is null)
        {
            _sourcesTabButton = TabButton();
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            content.Children.Add(new FontIcon { Glyph = "\uE774", FontSize = 13, Foreground = Missum.App.Controls.NativeIconPalette.BrushFor("web") });
            content.Children.Add(new TextBlock { Text = "Quellen", FontSize = 13 });
            _sourcesTabButton.Content = content;
            _sourcesTabButton.Height = 30;
            _sourcesTabButton.Padding = new Thickness(10, 0, 5, 0);
            _sourcesTabButton.Click += (_, _) => OpenSourcesTab();
            var close = TabButton();
            close.Content = new FontIcon { Glyph = "\uE711", FontSize = 11 };
            close.Width = 24; close.Height = 24;
            close.Margin = new Thickness(0, 0, 3, 0);
            close.VerticalAlignment = VerticalAlignment.Center;
            ToolTipService.SetToolTip(close, "Quellen-Tab schließen");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close, "Quellen-Tab schließen");
            close.Click += (_, _) =>
            {
                _sourceTabs.Remove(_session);
                if (_activeSourcesSession == _session) ShowChatView();
                RenderSessionTabs();
            };
            var row = new Grid();
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(_sourcesTabButton); Grid.SetColumn(close, 1); row.Children.Add(close);
            _sourcesTabContainer = new Border { Child = row };
        }
        ApplyTabAppearance(_sourcesTabContainer, _sourcesTabButton!, _activeSourcesSession == _session);
        SessionTabsPanel.Children.Remove(_sourcesTabContainer);
        SessionTabsPanel.Children.Insert(Math.Min(index, SessionTabsPanel.Children.Count), _sourcesTabContainer);
        return index + 1;
    }
}
