using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Sharm;

public sealed class VisitRecord
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public int Visits { get; set; }
    public DateTime LastVisit { get; set; }
}
public sealed record AddressSuggestion(string Title, string Detail, string Value, string Symbol);

public partial class MainWindow
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private readonly List<VisitRecord> history = [];
    private readonly List<Button> dockButtons = [];
    private readonly HashSet<string> hiddenSites = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? suggestionCancellation;
    private TextBox? suggestionOwner;
    private bool updatingAddress;
    private bool featuresReady;
    private readonly Dictionary<string, ImageSource> iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task> iconLoads = [];
    private string dockKey = "";
    private static readonly Bookmark[] DefaultSites =
    [
        new("YouTube", "https://www.youtube.com/"),
        new("ChatGPT", "https://chatgpt.com/"),
        new("GitHub", "https://github.com/"),
        new("Google", "https://www.google.com/"),
        new("Wikipedia", "https://www.wikipedia.org/"),
        new("Reddit", "https://www.reddit.com/")
    ];

    private string HiddenSitesPath => Path.Combine(App.ProfilePath, selfTest ? "test-hidden-sites.json" : "hidden-sites.json");
    private void InitializeFeatures()
    {
        featuresReady = true;
        try
        {
            if (File.Exists(HiddenSitesPath))
                foreach (var host in JsonSerializer.Deserialize<string[]>(File.ReadAllText(HiddenSitesPath)) ?? []) hiddenSites.Add(host);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        BookmarksPanel.MouseLeave += (_, _) => AnimateDock(-1);
        Deactivated += (_, _) => CloseSuggestions();
        Closing += (_, _) => { suggestionCancellation?.Cancel(); SuggestionPopup.IsOpen = false; };
        LocationChanged += (_, _) => CloseSuggestions();
        SuggestionPopup.Closed += (_, _) => suggestionCancellation?.Cancel();
    }

    private static string Host(string url) => new Uri(url).IdnHost.Replace("www.", "", StringComparison.OrdinalIgnoreCase);

    private void RecordVisit(string url, string title)
    {
        if (closing) return;
        var existing = history.FirstOrDefault(v => v.Url == url);
        if (existing is null) { existing = new VisitRecord { Url = url }; history.Add(existing); }
        existing.Title = string.IsNullOrWhiteSpace(title) ? Host(url) : title;
        existing.Visits++;
        existing.LastVisit = DateTime.UtcNow;
        SaveState();
        if (HomePage.Visibility == Visibility.Visible) RenderSiteDock();
    }

    private List<Bookmark> FrequentSites() => history
        .GroupBy(v => Host(v.Url), StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(g => g.Sum(v => (double)v.Visits / (1 + (DateTime.UtcNow - v.LastVisit).TotalDays / 14)))
        .Select(g => g.MaxBy(v => v.LastVisit)!)
        .Select(v => new Bookmark(v.Title, new Uri(v.Url).GetLeftPart(UriPartial.Authority) + "/"))
        .Concat(bookmarks).Concat(DefaultSites)
        .Where(b => !hiddenSites.Contains(Host(b.Url)))
        .DistinctBy(b => Host(b.Url), StringComparer.OrdinalIgnoreCase).Take(6).ToList();

    private void RenderSiteDock()
    {
        if (!featuresReady || closing || HomePage.Visibility != Visibility.Visible) return;
        var sites = FrequentSites();
        var key = string.Join('\n', sites.Select(site => site.Title + "\t" + site.Url));
        if (key == dockKey && dockButtons.Count > 0) return;
        dockKey = key;
        BookmarksPanel.Children.Clear();
        dockButtons.Clear();
        iconLoads.Clear();
        foreach (var site in sites)
        {
            var icon = new Image { Width = 29, Height = 29, Stretch = Stretch.Uniform };
            var fallback = new TextBlock { Text = "\uE774", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 24, Foreground = Brush("#9A9A9A"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var face = new Grid { Width = 52, Height = 52 };
            face.Children.Add(fallback); face.Children.Add(icon);
            var transforms = new TransformGroup();
            transforms.Children.Add(new ScaleTransform(1, 1));
            transforms.Children.Add(new TranslateTransform(0, 0));
            var button = new Button
            {
                Content = face, Width = 56, Height = 56, Margin = new Thickness(9, 10, 9, 9), Padding = new Thickness(0),
                Background = Brush("#232323"), ToolTip = site.Title, RenderTransformOrigin = new Point(0.5, 1), RenderTransform = transforms
            };
            AutomationProperties.SetName(button, site.Title);
            var index = dockButtons.Count;
            button.MouseEnter += (_, _) => AnimateDock(index);
            button.GotKeyboardFocus += (_, _) => AnimateDock(index);
            button.LostKeyboardFocus += (_, _) => AnimateDock(-1);
            button.Click += async (_, _) => await Navigate(site.Url);
            var menu = new ContextMenu();
            var newTab = new MenuItem { Header = "Открыть в новой вкладке" };
            newTab.Click += async (_, _) => await AddTab(site.Url);
            var hide = new MenuItem { Header = "Убрать с главной" };
            hide.Click += (_, _) =>
            {
                hiddenSites.Add(Host(site.Url));
                try { File.WriteAllText(HiddenSitesPath, JsonSerializer.Serialize(hiddenSites)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { statusMessage = ex.Message; }
                RenderSiteDock();
            };
            menu.Items.Add(newTab); menu.Items.Add(hide); button.ContextMenu = menu;
            dockButtons.Add(button); BookmarksPanel.Children.Add(button);
            iconLoads.Add(SetSiteIcon(icon, fallback, site.Url));
        }
    }

    private void AnimateDock(int hovered)
    {
        for (var i = 0; i < dockButtons.Count; i++)
        {
            var distance = hovered < 0 ? 99 : Math.Abs(i - hovered);
            var lift = distance == 0 ? -11 : distance == 1 ? -4 : 0;
            var scale = distance == 0 ? 1.13 : distance == 1 ? 1.035 : 1;
            var group = (TransformGroup)dockButtons[i].RenderTransform;
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 190 : 0);
            ((ScaleTransform)group.Children[0]).BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, duration) { EasingFunction = easing });
            ((ScaleTransform)group.Children[0]).BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, duration) { EasingFunction = easing });
            ((TranslateTransform)group.Children[1]).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(lift, duration) { EasingFunction = easing });
        }
    }

    private async Task SetSiteIcon(Image image, TextBlock fallback, string url)
    {
        try
        {
            var host = Host(url);
            if (!iconCache.TryGetValue(host, out var bitmap))
            {
                var folder = Path.Combine(App.ProfilePath, "Icons"); Directory.CreateDirectory(folder);
                var file = Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(host))) + ".png");
                byte[] bytes;
                if (File.Exists(file)) bytes = await File.ReadAllBytesAsync(file);
                else
                {
                    using var response = await Http.GetAsync("https://www.google.com/s2/favicons?sz=64&domain=" + Uri.EscapeDataString(host), HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > 524288) return;
                    using var stream = await response.Content.ReadAsStreamAsync();
                    using var data = new MemoryStream();
                    var buffer = new byte[8192]; int read;
                    while ((read = await stream.ReadAsync(buffer)) > 0) { if (data.Length + read > 524288) return; data.Write(buffer, 0, read); }
                    bytes = data.ToArray();
                    await File.WriteAllBytesAsync(file, bytes);
                }
                using var input = new MemoryStream(bytes);
                var source = new BitmapImage(); source.BeginInit(); source.CacheOption = BitmapCacheOption.OnLoad; source.DecodePixelWidth = 64; source.StreamSource = input; source.EndInit(); source.Freeze();
                bitmap = source;
                if (iconCache.Count >= 32) iconCache.Remove(iconCache.Keys.First());
                iconCache[host] = bitmap;
            }
            if (closing) return;
            image.Source = bitmap; fallback.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or NotSupportedException or ArgumentException or UnauthorizedAccessException or FormatException) { }
    }

    private void Address_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!updatingAddress && IsLoaded) QueueSuggestions(AddressBox);
    }

    private List<AddressSuggestion> LocalSuggestions(string query)
    {
        var list = new List<AddressSuggestion>();
        var all = tabs.Where(t => t.Url.Length > 0).Select(t => new Bookmark(t.Title, t.Url))
            .Concat(bookmarks)
            .Concat(history.OrderByDescending(v => v.LastVisit).Select(v => new Bookmark(v.Title, v.Url)))
            .Concat(DefaultSites);
        foreach (var site in all.Where(b => b.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || b.Url.Contains(query, StringComparison.OrdinalIgnoreCase)).DistinctBy(b => b.Url).Take(5))
            list.Add(new(site.Title, site.Url, site.Url, "\uE774"));
        var resolved = ResolveAddress(query);
        if (!resolved.StartsWith("https://www.google.com/search?q="))
            list.Insert(0, new(Host(resolved), resolved, resolved, "\uE72A"));
        else list.Add(new(query, "Поиск в Google", query, "\uE721"));
        return list.DistinctBy(s => s.Value).ToList();
    }

    private async void QueueSuggestions(TextBox owner)
    {
        if (!featuresReady || closing || !owner.IsKeyboardFocused) return;
        suggestionCancellation?.Cancel();
        suggestionCancellation?.Dispose();
        suggestionCancellation = new CancellationTokenSource();
        var token = suggestionCancellation.Token;
        var query = owner.Text.Trim();
        if (query.Length == 0) { CloseSuggestions(); return; }
        suggestionOwner = owner;
        var local = LocalSuggestions(query);
        ShowSuggestions(owner, local);
        if (query.Contains("://") || query.Contains('.') || query.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            await Task.Delay(220, token);
            var remote = await GetGoogleSuggestions(query, token);
            if (token.IsCancellationRequested || !owner.IsKeyboardFocused || closing) return;
            ShowSuggestions(owner, local.Concat(remote).DistinctBy(s => s.Value, StringComparer.OrdinalIgnoreCase).Take(8).ToList());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { }
    }

    private static async Task<List<AddressSuggestion>> GetGoogleSuggestions(string query, CancellationToken token)
    {
        var json = await Http.GetStringAsync("https://suggestqueries.google.com/complete/search?client=firefox&hl=ru&q=" + Uri.EscapeDataString(query), token);
        using var document = JsonDocument.Parse(json);
        return document.RootElement[1].EnumerateArray().Select(v => v.GetString()).Where(v => !string.IsNullOrWhiteSpace(v))
            .Take(5).Select(v => new AddressSuggestion(v!, "Поиск в Google", v!, "\uE721")).ToList();
    }

    private void ShowSuggestions(TextBox owner, List<AddressSuggestion> suggestions)
    {
        SuggestionPopup.PlacementTarget = owner == AddressBox ? (UIElement)AddressBox.Parent : SearchGlass;
        SuggestionSurface.Width = owner == AddressBox ? Math.Max(300, ((FrameworkElement)AddressBox.Parent).ActualWidth + 32) : SearchGlass.ActualWidth;
        SuggestionList.ItemsSource = suggestions;
        SuggestionList.SelectedIndex = -1;
        SuggestionPopup.IsOpen = suggestions.Count > 0;
    }

    private void CloseSuggestions()
    {
        suggestionCancellation?.Cancel();
        if (SuggestionPopup is not null) SuggestionPopup.IsOpen = false;
    }

    private async void Suggestion_KeyDown(object sender, KeyEventArgs e)
    {
        if (!SuggestionPopup.IsOpen) return;
        if (e.Key == Key.Escape) { e.Handled = true; CloseSuggestions(); return; }
        if (e.Key is Key.Down or Key.Up)
        {
            e.Handled = true;
            var count = SuggestionList.Items.Count;
            if (count == 0) return;
            var current = SuggestionList.SelectedIndex;
            SuggestionList.SelectedIndex = e.Key == Key.Down ? (current + 1) % count : current <= 0 ? count - 1 : current - 1;
            SuggestionList.ScrollIntoView(SuggestionList.SelectedItem);
        }
        else if (e.Key == Key.Enter && SuggestionList.SelectedItem is AddressSuggestion suggestion)
        { e.Handled = true; await Navigate(suggestion.Value); }
        else if (e.Key == Key.Tab) CloseSuggestions();
    }

    private async void Suggestion_Click(object sender, MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(SuggestionList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (item?.Content is AddressSuggestion suggestion) { e.Handled = true; await Navigate(suggestion.Value); }
    }
}
