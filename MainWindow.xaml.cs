using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Sharm;

public sealed class TabState : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private string title = "Новая вкладка";
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Url { get; set; } = "";
    public string Title { get => title; set { if (title == value) return; title = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title))); } }
    public DateTime LastActive { get; set; } = DateTime.UtcNow;
    public bool Pinned { get; set; }
    public double ScrollX { get; set; }
    public double ScrollY { get; set; }
    public double MediaTime { get; set; }
    public bool FullYoutube { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public WebView2? View { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public Task? Initializing { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public int Downloads { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool HasUnsavedForm { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool SwitchingYoutube { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool HasCapturePermission { get; set; }
}
public sealed record Bookmark(string Title, string Url);
public sealed class SavedState
{
    public List<TabState> Tabs { get; set; } = [];
    public List<Bookmark> Bookmarks { get; set; } = [];
    public Guid ActiveId { get; set; }
    public List<VisitRecord> History { get; set; } = [];
}

public partial class MainWindow : Window
{
    private readonly ObservableCollection<TabState> tabs = [];
    private readonly List<Bookmark> bookmarks = [];
    private readonly Stack<TabState> closed = new();
    private readonly DispatcherTimer memoryTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool maintainingMemory;
    private readonly SemaphoreSlim viewCreationGate = new(1, 1);
    private Task<CoreWebView2Environment>? environment;
    private TabState? active;
    private bool closing;
    private bool closeReady;
    private bool restored;
    private string statusMessage = "";
    private readonly bool selfTest;
    private readonly bool verifyCookie;
    private readonly bool verifySession;
    private string StatePath => Path.Combine(App.ProfilePath, selfTest ? benchmarkMemory ? "benchmark-session.json" : "test-session.json" : "session.json");
    private string BrowserProfile => Path.Combine(App.ProfilePath, selfTest ? benchmarkMemory ? "BenchmarkProfile" : "TestProfile" : "Profile");
    private string ImportMarkerPath => Path.Combine(App.ProfilePath, "firefox-import.json");

    public MainWindow(string[] args)
    {
        selfTest = args.Contains("--self-test");
        benchmarkMemory = args.Contains("--memory-benchmark");
        verifyCookie = args.Contains("--verify-cookie");
        verifySession = args.Contains("--verify-session");
        InitializeComponent();
        InitializeFeatures();
        LoadPreferences();
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); PersistState(); };
        PreviewKeyDown += HandleShortcut;
        Loaded += async (_, _) =>
        {
            if (selfTest && benchmarkMemory) { await RunMemoryBenchmark(); return; }
            if (selfTest) { await RunSelfTest(); return; }
            LoadState();
            restored = true;
            await ImportFirefoxOnce();
            if (tabs.Count == 0) await AddTab();
            else await Activate(tabs.FirstOrDefault(t => t.Id == restoreId) ?? tabs[0]);
            restored = true;
            if (active?.Url.Length == 0) RenderBookmarks();
        };
        SizeChanged += (_, _) => HomeContent.Width = Math.Clamp(ActualWidth - 100, 360, 480);
        memoryTimer.Tick += async (_, _) => await MaintainMemory();
        memoryTimer.Start();
        Closing += async (_, e) =>
        {
            if (closing) { e.Cancel = !closeReady; return; }
            e.Cancel = true;
            closing = true;
            memoryTimer.Stop();
            saveTimer.Stop();
            SaveState();
            try
            {
                var core = tabs.Select(tab => tab.View?.CoreWebView2).FirstOrDefault(core => core is not null);
                if (core is not null) await SaveSessionCookies(core);
            }
            finally
            {
                foreach (var tab in tabs) tab.View?.Dispose();
                closeReady = true;
                await Dispatcher.InvokeAsync(Close);
            }
        };
    }

    private Guid restoreId;
    private void MergeFirefoxData(FirefoxImportResult result)
    {
        restoreId = active?.Id ?? Guid.Empty;
        foreach (var bookmark in result.Bookmarks)
            if (!bookmarks.Any(existing => existing.Url == bookmark.Url)) bookmarks.Add(bookmark);
        foreach (var visit in result.History)
        {
            var existing = history.FirstOrDefault(entry => entry.Url == visit.Url);
            if (existing is null) history.Add(visit);
            else { existing.Visits = Math.Max(existing.Visits, visit.Visits); if (existing.LastVisit.Year < 2000 || existing.LastVisit > DateTime.UtcNow.AddDays(1) || visit.LastVisit > existing.LastVisit) existing.LastVisit = visit.LastVisit; if (existing.Title.Length == 0) existing.Title = visit.Title; }
        }
        RenderBookmarks(); SaveState();
    }

    private async Task<(int Added, int Skipped)> ImportFirefoxCookies(List<FirefoxCookie> source)
    {
        if (source.Count == 0) return (0, 0);
        var seed = new WebView2 { Visibility = Visibility.Collapsed, DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 17, 17, 17) };
        BrowserHost.Children.Add(seed);
        var added = 0; var skipped = 0;
        try
        {
            await seed.EnsureCoreWebView2Async(await BrowserEnvironment());
            if (!tabs.Any(tab => tab.View?.CoreWebView2 is not null)) restoredCookieProcesses.Remove(seed.CoreWebView2.BrowserProcessId);
            await RestoreSessionCookies(seed.CoreWebView2);
            foreach (var item in source)
            {
                try
                {
                    var cookie = seed.CoreWebView2.CookieManager.CreateCookie(item.Name, item.Value, item.Domain, item.Path);
                    cookie.IsSecure = item.IsSecure;
                    cookie.IsHttpOnly = item.IsHttpOnly;
                    cookie.SameSite = item.SameSite switch
                    {
                        1 => CoreWebView2CookieSameSiteKind.Lax,
                        2 => CoreWebView2CookieSameSiteKind.Strict,
                        3 when item.IsSecure => CoreWebView2CookieSameSiteKind.None,
                        3 => CoreWebView2CookieSameSiteKind.Lax,
                        _ => CoreWebView2CookieSameSiteKind.Lax
                    };
                    if (item.Expires is { } expiry) cookie.Expires = expiry.UtcDateTime;
                    seed.CoreWebView2.CookieManager.AddOrUpdateCookie(cookie); added++;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { skipped++; }
            }
            if (!await SaveSessionCookies(seed.CoreWebView2)) throw new IOException("Не удалось сохранить cookies в профиль Sharm.");
            await Task.Delay(250);
        }
        finally { BrowserHost.Children.Remove(seed); seed.Dispose(); }
        return (added, skipped);
    }
    private void LoadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            using var input = File.OpenRead(StatePath);
            var saved = JsonSerializer.Deserialize<SavedState>(input);
            if (saved is null) return;
            tabs.AddRange(saved.Tabs.Where(t => t.Url.Length == 0 || IsWebUrl(t.Url)).DistinctBy(t => t.Id));
            bookmarks.AddRange(saved.Bookmarks.Where(b => IsWebUrl(b.Url)));
            restoreId = saved.ActiveId;
            history.AddRange(saved.History.Where(v => IsWebUrl(v.Url)).OrderByDescending(v => v.LastVisit));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { statusMessage = "Сохранённая сессия недоступна. Открыта новая вкладка."; }
    }

    private void SaveState()
    {
        if (!restored && !selfTest) return;
        if (!closing && !selfTest) { saveTimer.Stop(); saveTimer.Start(); return; }
        PersistState();
    }

    private void PersistState()
    {
        try
        {
            using (var output = File.Create(StatePath + ".tmp"))
                JsonSerializer.Serialize(output, new SavedState { Tabs = tabs.ToList(), Bookmarks = bookmarks, ActiveId = active?.Id ?? Guid.Empty, History = history });
            File.Move(StatePath + ".tmp", StatePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { if (!closing) statusMessage = "Не удалось сохранить сессию: " + ex.Message; }
    }

    internal static bool IsWebUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
    internal static string ResolveAddress(string input)
    {
        input = input.Trim();
        if (input.Length == 0) return "";
        if (IsWebUrl(input)) return new Uri(input).AbsoluteUri;
        if (!input.Any(char.IsWhiteSpace) && !input.Contains("://") &&
            (input.Contains('.') || input.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || input.StartsWith("[::1]")))
        {
            var prefix = input.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || input.StartsWith("127.0.0.1") || input.StartsWith("[::1]") ? "http://" : "https://";
            if (Uri.TryCreate(prefix + input, UriKind.Absolute, out var candidate) && !string.IsNullOrEmpty(candidate.Host)) return candidate.AbsoluteUri;
        }
        return "https://www.google.com/search?q=" + Uri.EscapeDataString(input);
    }

    private async Task AddTab(string url = "")
    {
        if (closing) return;
        var tab = new TabState { Url = url };
        tabs.Add(tab);
        await Activate(tab);
        if (url.Length == 0) SearchBox.Focus();
        SaveState();
    }

    private async Task Activate(TabState tab)
    {
        if (closing || !tabs.Contains(tab)) return;
        var previous = active;
        active = tab;
        CloseSuggestions();
        tab.LastActive = DateTime.UtcNow;
        if (previous?.View is { } oldView && previous != tab)
        {
            previous.LastActive = DateTime.UtcNow;
            if (aggressiveMemory && !previous.Pinned && !previous.HasUnsavedForm && !previous.HasCapturePermission && previous.Downloads == 0 && oldView.CoreWebView2 is { IsDocumentPlayingAudio: false })
                await CaptureTabPosition(previous);
            if (active != tab || closing) return;
            oldView.Visibility = Visibility.Collapsed;
            if (previous.Downloads == 0 && !previous.HasUnsavedForm && !previous.HasCapturePermission && oldView.CoreWebView2 is { IsDocumentPlayingAudio: false } core)
            {
                try { await core.TrySuspendAsync(); if (active == previous) core.Resume(); } catch (InvalidOperationException) { }
            }
        }
        if (active != tab || closing) return;
        await MaintainMemory(enforceBudget: true, releaseAll: aggressiveMemory);
        if (active != tab || closing) return;
        ErrorPanel.Visibility = Visibility.Collapsed;
        HomePage.Visibility = tab.Url.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        BrowserHost.Visibility = tab.Url.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        SearchBox.Clear();
        UpdateChrome();
        RenderTabs();
        if (tab.Url.Length > 0)
        {
            try
            {
                await MaintainMemory(enforceBudget: true);
                await EnsureView(tab);
                if (active != tab || closing || tab.View is null) return;
                tab.View.CoreWebView2.Resume();
                tab.View.Visibility = Visibility.Visible;
                tab.View.Focus();
            }
            catch (Exception ex) { if (active == tab) ShowError(ex.Message); }
        }
        else
        {
            RenderSiteDock();
            if (SystemParameters.ClientAreaAnimation)
                HomeContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            SearchBox.Focus();
        }
        SaveState();
    }

    private async Task EnsureView(TabState tab)
    {
        if (tab.Initializing is { IsCompleted: false } pending) { await pending; return; }
        tab.Initializing = null;
        if (tab.View?.CoreWebView2 is { } existing && IsYoutubePlayer(existing.Source) != IsYoutubePlayer(ContentAddress(tab)))
        { await SaveSessionCookies(existing); BrowserHost.Children.Remove(tab.View); tab.View.Dispose(); tab.View = null; }
        if (tab.View?.CoreWebView2 is not null) return;
        var initialization = InitializeView(tab);
        tab.Initializing = initialization.IsCompleted ? null : initialization;
        await initialization;
    }

    private async Task InitializeView(TabState tab)
    {
        await viewCreationGate.WaitAsync();
        try
        {
            if (closing || active != tab || !tabs.Contains(tab)) return;
            await MaintainMemory(enforceBudget: true, releaseAll: aggressiveMemory);
            if (closing || active != tab || !tabs.Contains(tab)) return;
            await InitializeViewCore(tab);
        }
        finally { tab.Initializing = null; viewCreationGate.Release(); }
    }

    private async Task InitializeViewCore(TabState tab)
    {
        var view = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 17, 17, 17), Visibility = Visibility.Hidden };
        tab.View = view;
        BrowserHost.Children.Add(view);
        memoryTimer.Start();
        try
        {
            await view.EnsureCoreWebView2Async(await BrowserEnvironment());
            if (closing || !tabs.Contains(tab)) return;
            var core = view.CoreWebView2;
            if (tabs.Count(item => item.View?.CoreWebView2 is not null) == 1) restoredCookieProcesses.Remove(core.BrowserProcessId);
            await RestoreSessionCookies(core);
            if (benchmarkMemory) core.IsMuted = true;
            ConfigureYoutubePlayer(tab, core);
            core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = true;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;
            core.Settings.IsPasswordAutosaveEnabled = true;
            core.Settings.IsGeneralAutofillEnabled = true;
            core.NavigationStarting += (_, e) =>
            {
                if (!IsWebUrl(e.Uri) && e.Uri != "about:blank") { e.Cancel = true; return; }
                if (!string.Equals(ContentAddress(tab), e.Uri, StringComparison.Ordinal)) { tab.ScrollX = 0; tab.ScrollY = 0; tab.MediaTime = 0; }
                tab.HasUnsavedForm = false;
                tab.HasCapturePermission = false;
                if (active == tab) { ErrorPanel.Visibility = Visibility.Collapsed; statusMessage = "Загрузка…"; }
            };
            await core.AddScriptToExecuteOnDocumentCreatedAsync("(()=>{let dirty=false;addEventListener('input',e=>{const t=e.target;if(!(t instanceof HTMLTextAreaElement||t.isContentEditable||(t instanceof HTMLInputElement&&!/^(range|checkbox|radio|hidden|button|submit|reset|image)$/i.test(t.type))))return;if(!dirty){dirty=true;chrome.webview.postMessage('sharm:form-dirty');}},true);addEventListener('submit',()=>{dirty=false;chrome.webview.postMessage('sharm:form-clean');},true);})()");
            core.WebMessageReceived += (_, e) =>
            {
                if (e.WebMessageAsJson == "\"sharm:form-dirty\"") tab.HasUnsavedForm = true;
                else if (e.WebMessageAsJson == "\"sharm:form-clean\"") tab.HasUnsavedForm = false;
            };
            core.SourceChanged += (_, _) =>
            {
                if (IsWebUrl(core.Source) && !IsYoutubePlayer(core.Source)) tab.Url = core.Source;
                if (active == tab && !tab.SwitchingYoutube && IsYoutubePlayer(ContentAddress(tab)) && !IsYoutubePlayer(core.Source))
                {
                    tab.SwitchingYoutube = true;
                    Dispatcher.BeginInvoke(async () => { try { if (active == tab && !closing) await Navigate(tab.Url); } finally { tab.SwitchingYoutube = false; } });
                }
                if (active == tab) UpdateChrome();
                SaveState();
            };
            core.DocumentTitleChanged += (_, _) =>
            {
                tab.Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? new Uri(tab.Url).Host : core.DocumentTitle;
                RenderTabs();
                if (active == tab) UpdateChrome();
                SaveState();
            };
            core.HistoryChanged += (_, _) => { if (active == tab) UpdateChrome(); };
            core.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess && (tab.ScrollY > 0 || tab.ScrollX > 0 || tab.MediaTime > 0)) RestoreTabPosition(tab);
                if (e.IsSuccess && IsWebUrl(tab.Url)) RecordVisit(tab.Url, core.DocumentTitle);
                if (active != tab) return;
                statusMessage = e.IsSuccess ? "Готово" : "Ошибка соединения";
                if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                    ShowError($"{e.WebErrorStatus}\nПроверьте адрес и подключение к интернету.");
                UpdateChrome();
            };
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (IsWebUrl(e.Uri))
                {
                    var target = e.Uri;
                    Dispatcher.BeginInvoke(async () => await AddTab(target));
                }
            };
            core.PermissionRequested += (_, e) =>
            {
                if (active != tab) { e.State = CoreWebView2PermissionState.Deny; return; }
                var deferral = e.GetDeferral();
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        if (closing || active != tab) { e.State = CoreWebView2PermissionState.Deny; return; }
                        var result = MessageBox.Show(this, $"{new Uri(e.Uri).Host}\nРазрешить: {e.PermissionKind}?", "Sharm · разрешение", MessageBoxButton.YesNo, MessageBoxImage.Question);
                        e.State = result == MessageBoxResult.Yes ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
                        if (result == MessageBoxResult.Yes && e.PermissionKind is CoreWebView2PermissionKind.Camera or CoreWebView2PermissionKind.Microphone) tab.HasCapturePermission = true;
                    }
                    finally { deferral.Complete(); }
                });
            };
            core.DownloadStarting += (_, e) =>
            {
                e.Handled = true;
                if (selfTest) e.ResultFilePath = Path.Combine(App.ProfilePath, "test-download.txt");
                tab.Downloads++;
                var operation = e.DownloadOperation;
                var counted = true;
                operation.StateChanged += (_, _) =>
                {
                    if (counted && (operation.State == CoreWebView2DownloadState.Completed ||
                        operation.State == CoreWebView2DownloadState.Interrupted && !operation.CanResume))
                    { counted = false; tab.Downloads = Math.Max(0, tab.Downloads - 1); }
                };
                statusMessage = "Загрузка файла: " + Path.GetFileName(e.ResultFilePath);
                Dispatcher.BeginInvoke(() => TrackDownload(operation));
            };
            core.ProcessFailed += (_, e) => { if (active == tab) ShowError("Процесс страницы остановлен: " + e.ProcessFailedKind); };
            core.Navigate(ContentAddress(tab));
        }
        catch
        {
            BrowserHost.Children.Remove(view);
            view.Dispose();
            tab.View = null;
            throw;
        }
        finally { tab.Initializing = null; }
    }

    private async Task Navigate(string input)
    {
        CloseSuggestions();
        var url = ResolveAddress(input);
        if (url.Length == 0 || active is null) return;
        var tab = active;
        if (!string.Equals(tab.Url, url, StringComparison.Ordinal)) { tab.ScrollX = 0; tab.ScrollY = 0; tab.MediaTime = 0; }
        tab.Url = url;
        HomePage.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        BrowserHost.Visibility = Visibility.Visible;
        try
        {
            if (tab.View?.CoreWebView2 is { } existing && (IsYoutubePlayer(existing.Source) || IsYoutubePlayer(ContentAddress(tab))))
            {
                await SaveSessionCookies(existing);
                BrowserHost.Children.Remove(tab.View); tab.View.Dispose(); tab.View = null;
            }
            var hadView = tab.View?.CoreWebView2 is not null;
            await MaintainMemory(enforceBudget: true);
            await EnsureView(tab);
            if (active != tab || tab.View is null) return;
            tab.View.Visibility = Visibility.Visible;
            tab.View.CoreWebView2.Resume();
            if (hadView) tab.View.CoreWebView2.Navigate(ContentAddress(tab));
            tab.View.Focus();
            UpdateChrome();
            SaveState();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void ShowError(string message)
    {
        if (active?.View is { } view) view.Visibility = Visibility.Collapsed;
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void UpdateChrome()
    {
        if (active is null) return;
        if (!AddressBox.IsKeyboardFocused || !SuggestionPopup.IsOpen)
        {
            updatingAddress = true;
            AddressBox.Text = active.Url;
            updatingAddress = false;
        }
        Title = "Sharm";
        BackButton.IsEnabled = active.View?.CoreWebView2?.CanGoBack == true;
        ForwardButton.IsEnabled = active.View?.CoreWebView2?.CanGoForward == true;
        SecurityIcon.Text = active.Url.StartsWith("https://") ? "\uE72E" : "\uE774";
        BookmarkButton.Foreground = Brush(bookmarks.Any(b => b.Url == active.Url) ? "#E0D9B9" : "#B6B6B6");
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private bool updatingTabStrip;
    private void RenderTabs()
    {
        updatingTabStrip = true;
        try
        {
            TabStrip.ItemsSource ??= tabs;
            TabStrip.SelectedItem = active;
            if (active is not null) TabStrip.ScrollIntoView(active);
        }
        finally { updatingTabStrip = false; }
    }

    private async void TabSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingTabStrip && TabStrip.SelectedItem is TabState tab && active != tab) await Activate(tab);
    }

    private async void TabClose_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: TabState tab }) await CloseTab(tab);
    }

    private async void Tab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && sender is ListBoxItem { DataContext: TabState tab })
        { e.Handled = true; await CloseTab(tab); }
    }

    private void Tab_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: TabState tab } container) return;
        var menu = new ContextMenu();
        var pin = new MenuItem { Header = tab.Pinned ? "Разрешить выгрузку" : "Не выгружать эту вкладку" };
        pin.Click += (_, _) => { tab.Pinned = !tab.Pinned; SaveState(); };
        menu.Items.Add(pin); container.ContextMenu = menu;
    }

    private async Task CloseTab(TabState tab)
    {
        var index = tabs.IndexOf(tab);
        if (index < 0) return;
        tabs.Remove(tab);
        closed.Push(new TabState { Url = tab.Url, Title = tab.Title });
        if (closed.Count > 20)
        {
            var recent = closed.Take(20).Reverse().ToArray(); closed.Clear(); foreach (var item in recent) closed.Push(item);
        }
        if (tab.View is { } view) { if (view.CoreWebView2 is { } core) await SaveSessionCookies(core); BrowserHost.Children.Remove(view); view.Dispose(); tab.View = null; }
        if (tabs.Count == 0) await AddTab();
        else if (active == tab) await Activate(tabs[Math.Min(index, tabs.Count - 1)]);
        RenderTabs();
        SaveState();
    }

    private async Task MaintainMemory(bool enforceBudget = false, bool releaseAll = false)
    {
        if (closing || maintainingMemory) return;
        maintainingMemory = true;
        var changed = false;
        try
        {
            var allowed = aggressiveMemory ? 1 : 6;
            var incoming = enforceBudget && active is { Url.Length: > 0, View: null } ? 1 : 0;
            var loaded = tabs.Count(tab => tab.View is not null) + incoming;
            foreach (var tab in tabs.OrderBy(tab => tab.LastActive).ToArray())
            {
                if (tab == active || tab.Pinned || tab.HasUnsavedForm || tab.HasCapturePermission || tab.Initializing is { IsCompleted: false } || tab.View?.CoreWebView2 is not { } core) continue;
                if (core.IsDocumentPlayingAudio || tab.Downloads > 0) continue;
                var timeout = aggressiveMemory ? TimeSpan.Zero : TimeSpan.FromMinutes(10);
                if (releaseAll || DateTime.UtcNow - tab.LastActive > timeout || (enforceBudget && loaded > allowed))
                {
                    await SaveSessionCookies(core);
                    if (tab == active || tab.View is null) continue;
                    BrowserHost.Children.Remove(tab.View);
                    tab.View.Dispose(); tab.View = null; loaded--; changed = true;
                }
                else { try { await core.TrySuspendAsync(); if (tab == active) core.Resume(); } catch (InvalidOperationException) { } }
            }
        }
        finally { maintainingMemory = false; }
        if (changed && !closing) RenderTabs();
        if (!tabs.Any(tab => tab.View is not null)) memoryTimer.Stop();
    }

    private Task<CoreWebView2Environment> BrowserEnvironment() => environment ??= CoreWebView2Environment.CreateAsync(null, BrowserProfile,
        new CoreWebView2EnvironmentOptions("--js-flags=--optimize-for-size"));

    private async Task CaptureTabPosition(TabState tab)
    {
        try
        {
            var script = IsYoutubePlayer(tab.View!.CoreWebView2.Source)
                ? "JSON.stringify({x:scrollX,y:scrollY,t:window.player?.getCurrentTime?.()||0})"
                : "JSON.stringify({x:scrollX,y:scrollY,t:document.querySelector('video')?.currentTime||0})";
            var result = await tab.View.CoreWebView2.ExecuteScriptAsync(script).WaitAsync(TimeSpan.FromMilliseconds(200));
            var json = JsonSerializer.Deserialize<string>(result);
            if (json is null) return;
            using var position = JsonDocument.Parse(json);
            if (position.RootElement.TryGetProperty("x", out var x) && x.TryGetDouble(out var left) && double.IsFinite(left)) tab.ScrollX = Math.Max(0, left);
            if (position.RootElement.TryGetProperty("y", out var y) && y.TryGetDouble(out var top) && double.IsFinite(top)) tab.ScrollY = Math.Max(0, top);
            if (position.RootElement.TryGetProperty("t", out var time) && time.TryGetDouble(out var seconds) && double.IsFinite(seconds)) tab.MediaTime = Math.Max(0, seconds);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or JsonException or System.Runtime.InteropServices.COMException) { }
    }

    private async void RestoreTabPosition(TabState tab)
    {
        var data = JsonSerializer.Serialize(new { x = tab.ScrollX, y = tab.ScrollY, t = tab.MediaTime });
        try
        {
            if (tab.View?.CoreWebView2 is { } core && IsYoutubePlayer(core.Source))
            {
                await core.ExecuteScriptAsync("window.sharmResume=" + JsonSerializer.Serialize(tab.MediaTime) + ";if(window.player?.seekTo)window.player.seekTo(window.sharmResume,true);");
                return;
            }
            await tab.View!.CoreWebView2.ExecuteScriptAsync("(()=>{const p=" + data + ";scrollTo(p.x,p.y);if(p.t<=0)return;const v=document.querySelector('video');if(!v)return;const restore=()=>{if(Number.isFinite(v.duration)&&p.t<v.duration)v.currentTime=p.t;};if(v.readyState)restore();else v.addEventListener('loadedmetadata',restore,{once:true});})()");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or NullReferenceException or System.Runtime.InteropServices.COMException) { }
    }

    private void RenderBookmarks()
    {
        RenderSiteDock();
    }

    private void Bookmark_Click(object? sender, RoutedEventArgs e)
    {
        if (active is null || active.Url.Length == 0) return;
        var existing = bookmarks.FirstOrDefault(b => b.Url == active.Url);
        if (existing is not null) bookmarks.Remove(existing);
        else bookmarks.Add(new Bookmark(active.Title, active.Url));
        RenderBookmarks(); UpdateChrome(); SaveState();
    }

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        void Item(string title, Action action) { var parts = title.Split("  ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); var item = new MenuItem { Header = parts[0], InputGestureText = parts.Length > 1 ? parts[1] : "" }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Item("Новая вкладка                         Ctrl+T", async () => await AddTab());
        Item("Вернуть закрытую вкладку     Ctrl+Shift+T", async () => { if (closed.TryPop(out var tab)) await AddTab(tab.Url); });
        Item("Домой                                      Alt+Home", async () => await AddTab());
        menu.Items.Add(new Separator());
        Item("Загрузки                                    Ctrl+J", OpenDownloads);
        var saved = new MenuItem { Header = "Закладки", IsEnabled = bookmarks.Count > 0 };
        foreach (var bookmark in bookmarks)
        {
            var item = new MenuItem { Header = bookmark.Title, ToolTip = bookmark.Url };
            item.Click += async (_, _) => await Navigate(bookmark.Url); saved.Items.Add(item);
        }
        menu.Items.Add(saved);
        Item("Увеличить                                  Ctrl++", () => Zoom(0.1));
        Item("Уменьшить                                 Ctrl+−", () => Zoom(-0.1));
        menu.Items.Add(new Separator());
        Item("Настройки", OpenSettings);
        Item("О Sharm", () => MessageBox.Show(this, "Sharm 1.0\n\nТихий браузер. Google. Тёмная тема.\nCookies и вкладки сохраняются автоматически.\nВыгрузка страниц и лёгкий YouTube управляются в настройках.\nАудио, звонки, загрузки и заполненные формы защищены.\n\nПрофиль: " + App.ProfilePath, "Sharm"));
        menu.PlacementTarget = (Button)sender; menu.IsOpen = true;
    }

    private void Zoom(double delta) { if (active?.View is { } view) view.ZoomFactor = Math.Clamp(view.ZoomFactor + delta, 0.25, 3); }
    private static bool IsShortcut(Key key, ModifierKeys mods) =>
        (mods.HasFlag(ModifierKeys.Control) && key is Key.L or Key.T or Key.W or Key.R or Key.D or Key.J or Key.Tab or Key.Add or Key.Subtract or Key.OemPlus or Key.OemMinus or Key.D0)
        || (mods.HasFlag(ModifierKeys.Alt) && key is Key.Left or Key.Right or Key.Home) || key is Key.F5 or Key.F11;
    private void HandleShortcut(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!IsShortcut(key, Keyboard.Modifiers)) return;
        e.Handled = true; DispatchShortcut(key, Keyboard.Modifiers);
    }
    private async void DispatchShortcut(Key key, ModifierKeys mods)
    {
        if (mods.HasFlag(ModifierKeys.Control))
        {
            switch (key)
            {
                case Key.L: AddressBox.Focus(); AddressBox.SelectAll(); break;
                case Key.T:
                    if (mods.HasFlag(ModifierKeys.Shift)) { if (closed.TryPop(out var tab)) await AddTab(tab.Url); }
                    else await AddTab(); break;
                case Key.W: if (active is not null) await CloseTab(active); break;
                case Key.R: Reload_Click(this, new()); break;
                case Key.D: Bookmark_Click(this, new()); break;
                case Key.J: OpenDownloads(); break;
                case Key.Tab:
                    if (active is not null) await Activate(tabs[(tabs.IndexOf(active) + (mods.HasFlag(ModifierKeys.Shift) ? tabs.Count - 1 : 1)) % tabs.Count]); break;
                case Key.Add: case Key.OemPlus: Zoom(0.1); break;
                case Key.Subtract: case Key.OemMinus: Zoom(-0.1); break;
                case Key.D0: if (active?.View is { } view) view.ZoomFactor = 1; break;
            }
        }
        else if (mods.HasFlag(ModifierKeys.Alt))
        {
            if (key == Key.Left) Back_Click(this, new());
            if (key == Key.Right) Forward_Click(this, new());
            if (key == Key.Home) await AddTab();
        }
        else if (key == Key.F5) Reload_Click(this, new());
        else if (key == Key.F11) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private async void Address_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await Navigate(AddressBox.Text); } }
    private void Address_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => Dispatcher.BeginInvoke(() => AddressBox.SelectAll());
    private async void Search_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await Navigate(SearchBox.Text); } }
    private void Search_TextChanged(object sender, TextChangedEventArgs e) { if (SearchHint is not null) SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; if (IsLoaded) QueueSuggestions(SearchBox); }
    private async void NewTab_Click(object sender, RoutedEventArgs e) => await AddTab();
    private void Back_Click(object sender, RoutedEventArgs e) { if (active?.View?.CoreWebView2 is { CanGoBack: true } core) core.GoBack(); }
    private void Forward_Click(object sender, RoutedEventArgs e) { if (active?.View?.CoreWebView2 is { CanGoForward: true } core) core.GoForward(); }
    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (active is not { Url.Length: > 0 } tab) return;
        ErrorPanel.Visibility = Visibility.Collapsed;
        if (tab.View?.CoreWebView2 is { } core) { tab.View.Visibility = Visibility.Visible; core.Reload(); }
        else await Activate(tab);
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async Task RunSelfTest()
    {
        var results = new List<string>();
        try
        {
            void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); results.Add("PASS " + name); }
            Check(IsDraggableTitleSource(BrandCaption), "brand title is a window drag target");
            Check(IsDraggableTitleSource(TitleBar), "empty title bar is a window drag target");
            var titleButton = new Button(); TitleBar.Children.Add(titleButton);
            Check(!IsDraggableTitleSource(titleButton), "window buttons do not start dragging"); TitleBar.Children.Remove(titleButton);
            var testTab = new ListBoxItem(); TitleBar.Children.Add(testTab);
            Check(!IsDraggableTitleSource(testTab), "tab clicks do not start window dragging"); TitleBar.Children.Remove(testTab);
            Check(NativeMemory.PrivateResidentBytes(Environment.ProcessId) > 0, "native resident memory measurement available");
            Check(YoutubeVideoId("https://www.youtube.com/watch?v=jNQXAC9IVRw") == "jNQXAC9IVRw", "lightweight YouTube recognizes watch URLs");
            Check(YoutubeVideoId("https://youtu.be/jNQXAC9IVRw") == "jNQXAC9IVRw", "lightweight YouTube recognizes short URLs");
            Check(YoutubeVideoId("https://youtube.com.example.org/watch?v=jNQXAC9IVRw") is null, "YouTube routing rejects lookalike domains");
            Maximize_Click(this, new RoutedEventArgs());
            Check(WindowState == WindowState.Maximized, "window can maximize");
            Maximize_Click(this, new RoutedEventArgs());
            Check(WindowState == WindowState.Normal, "window restores to movable normal state");
            if (verifySession)
            {
                LoadState();
                Check(tabs.Count == 3, "open tabs survive application restart");
                Check(restoreId == tabs[1].Id && tabs[1].Url == "https://example.com/#restored", "selected tab and URL survive restart");
                await Activate(tabs[1]);
                Check(active == tabs[1] && active.View?.CoreWebView2 is not null, "selected session tab reopens");
                Check(tabs[0].View is null && tabs[2].View is null, "restored background tabs stay unloaded");
                foreach (var tab in tabs) { if (tab.View is { } view) { BrowserHost.Children.Remove(view); view.Dispose(); } }
                tabs.Clear(); active = null;
            }
            Check(ResolveAddress("example.com") == "https://example.com/", "domain normalization");
            Check(ResolveAddress("тёмный браузер").StartsWith("https://www.google.com/search?q="), "Google search");
            Check(ResolveAddress("localhost:8000") == "http://localhost:8000/", "localhost address");
            Check(!IsWebUrl("javascript:alert(1)"), "reject non-web schemes");
            await AddTab(); restored = true;
            Check(tabs.Count == 1 && active?.View is null, "new tab creates no web renderer");
            var tabIdsBeforeImport = tabs.Select(tab => tab.Id).ToArray();
            var activeBeforeImport = active;
            var sampleImport = new FirefoxImportResult();
            for (var index = 0; index < 1200; index++) sampleImport.History.Add(new VisitRecord { Url = $"https://import-test.example/{index}", LastVisit = DateTime.UtcNow });
            MergeFirefoxData(sampleImport);
            Check(tabIdsBeforeImport.SequenceEqual(tabs.Select(tab => tab.Id)) && active == activeBeforeImport, "Firefox merge leaves Sharm tabs and selection unchanged");
            PersistState();
            using (var savedImport = File.OpenRead(StatePath)) Check(JsonSerializer.Deserialize<SavedState>(savedImport)!.History.Count >= 1200, "imported history is not truncated to 1000 addresses");
            history.RemoveAll(visit => visit.Url.StartsWith("https://import-test.example/"));
            var brandTypeface = new Typeface(BrandTitle.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            Check(brandTypeface.TryGetGlyphTypeface(out var brandGlyph) && brandGlyph.FontUri.ToString().Contains("SF-Pro-Display-Semibold", StringComparison.OrdinalIgnoreCase), "real embedded SF Pro Display Semibold");
            Check(dockButtons.Count == 6 && dockButtons.All(b => b.Content is Grid), "icon-only frequent site dock");
            Check(LocalSuggestions("youtu").Any(s => s.Value.Contains("youtube.com")), "site address autocomplete");
            Check(LocalSuggestions("тёмный браузер").Any(s => s.Value == "тёмный браузер"), "search suggestion fallback");
            var onlineSuggestions = await GetGoogleSuggestions("youtube", CancellationToken.None);
            Check(onlineSuggestions.Count > 0, "live Google autocomplete service");
            ShowSuggestions(AddressBox, LocalSuggestions("you").Concat(onlineSuggestions).Take(8).ToList());
            Check(SuggestionList.Items.Count > 0, "address suggestion dropdown populated");
            var downKey = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(AddressBox), 0, Key.Down) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Suggestion_KeyDown(AddressBox, downKey);
            Check(downKey.Handled && SuggestionList.SelectedIndex == 0, "arrow key selects address suggestion");
            await Task.Delay(200);
            SuggestionSurface.UpdateLayout();
            var suggestionImage = new RenderTargetBitmap((int)SuggestionSurface.ActualWidth, (int)SuggestionSurface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            suggestionImage.Render(SuggestionSurface);
            var suggestionEncoder = new PngBitmapEncoder(); suggestionEncoder.Frames.Add(BitmapFrame.Create(suggestionImage));
            using (var output = File.Create(Path.Combine(App.ProfilePath, "suggestions-preview.png"))) suggestionEncoder.Save(output);
            var escapeKey = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(AddressBox), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Suggestion_KeyDown(AddressBox, escapeKey);
            Check(escapeKey.Handled && !SuggestionPopup.IsOpen, "Escape dismisses address suggestions");
            RecordVisit("https://sharm-test.example/path", "Frequent test site");
            RecordVisit("https://sharm-test.example/path", "Frequent test site");
            Check(FrequentSites().Any(s => s.Url == "https://sharm-test.example/"), "visited websites appear in dock");
            history.RemoveAll(v => v.Url.StartsWith("https://sharm-test.example"));
            RenderSiteDock();
            await Task.WhenAll(iconLoads);
            Check(dockButtons.Any(b => ((Grid)b.Content).Children.OfType<Image>().Any(i => i.Source is not null)), "site favicons downloaded and displayed");
            AnimateDock(2);
            await Task.Delay(250);
            Check(((TranslateTransform)((TransformGroup)dockButtons[2].RenderTransform).Children[1]).Y < -10, "hover raises dock icon");
            Check(((TranslateTransform)((TransformGroup)dockButtons[1].RenderTransform).Children[1]).Y < -3, "neighboring dock icons follow wave");
            AnimateDock(-1);
            await Task.Delay(350);
            UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(this); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(Path.Combine(App.ProfilePath, "preview.png"))) encoder.Save(output);
            var importStamp = File.Exists(ImportMarkerPath) ? File.GetLastWriteTimeUtc(ImportMarkerPath) : DateTime.MinValue;
            OpenSettings();
            settingsWindow!.UpdateLayout();
            static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
            {
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                {
                    var child = VisualTreeHelper.GetChild(root, i);
                    if (child is T match) yield return match;
                    foreach (var descendant in Descendants<T>(child)) yield return descendant;
                }
            }
            Check(Descendants<Button>(settingsWindow).Any(button => Equals(button.Content, "Импорт из Firefox")), "Firefox import available in settings only");
            var settingsBitmap = new RenderTargetBitmap((int)settingsWindow.ActualWidth, (int)settingsWindow.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            settingsBitmap.Render(settingsWindow);
            var settingsEncoder = new PngBitmapEncoder(); settingsEncoder.Frames.Add(BitmapFrame.Create(settingsBitmap));
            using (var output = File.Create(Path.Combine(App.ProfilePath, "settings-preview.png"))) settingsEncoder.Save(output);
            settingsWindow.Close();
            Check(importStamp == (File.Exists(ImportMarkerPath) ? File.GetLastWriteTimeUtc(ImportMarkerPath) : DateTime.MinValue), "startup and settings do not trigger import");
            await Navigate("https://example.com");
            var first = active!;
            Check(first.View?.CoreWebView2 is not null, "WebView2 initialization");
            var loaded = new TaskCompletionSource<bool>();
            first.View!.CoreWebView2.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            await first.View.CoreWebView2.ExecuteScriptAsync("document.readyState");
            Check(await loaded.Task.WaitAsync(TimeSpan.FromSeconds(40)), "HTTPS page navigation");
            var title = await first.View.CoreWebView2.ExecuteScriptAsync("document.title");
            Check(title.Contains("Example Domain"), "real page content");
            await first.View.CoreWebView2.ExecuteScriptAsync("var a=document.createElement('a');a.href=URL.createObjectURL(new Blob(['Sharm download check'],{type:'text/plain'}));a.download='test-download.txt';document.body.append(a);a.click();a.remove();");
            for (var attempt = 0; attempt < 100 && (downloadOperations.Count == 0 || downloadOperations[0].State != CoreWebView2DownloadState.Completed); attempt++) await Task.Delay(100);
            Check(downloadOperations.Count > 0 && downloadOperations[0].State == CoreWebView2DownloadState.Completed, "custom downloads panel tracks actual download completion");
            Check(File.Exists(Path.Combine(App.ProfilePath, "test-download.txt")) && File.ReadAllText(Path.Combine(App.ProfilePath, "test-download.txt")) == "Sharm download check", "downloaded file contents preserved");
            Check(downloadsWindow is not null, "custom downloads panel opens automatically");
            if (downloadsWindow is not null)
            {
                downloadsWindow.UpdateLayout();
                var downloadBitmap = new RenderTargetBitmap((int)downloadsWindow.ActualWidth, (int)downloadsWindow.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                downloadBitmap.Render(downloadsWindow);
                var downloadEncoder = new PngBitmapEncoder(); downloadEncoder.Frames.Add(BitmapFrame.Create(downloadBitmap));
                using (var output = File.Create(Path.Combine(App.ProfilePath, "downloads-preview.png"))) downloadEncoder.Save(output);
                downloadsWindow.Close();
            }
            await first.View.CoreWebView2.ExecuteScriptAsync("document.body.style.minHeight='2400px';scrollTo(0,180)");
            await CaptureTabPosition(first);
            Check(first.ScrollY >= 170, "scroll position saved before unloading page");
            var manager = first.View.CoreWebView2.CookieManager;
            if (verifyCookie)
            {
                var restartedCookies = await manager.GetCookiesAsync("https://example.com");
                Check(restartedCookies.Any(c => c.Name == "sharm_test" && c.Value == "saved"), "cookie survives application restart");
                Check(restartedCookies.Any(c => c.Name == "sharm_session_test" && c.Value == "session-saved" && c.IsSession), "session cookie survives application restart without Firefox");
            }
            var sessionCookie = manager.CreateCookie("sharm_session_test", "session-saved", ".example.com", "/");
            sessionCookie.SameSite = CoreWebView2CookieSameSiteKind.Lax;
            manager.AddOrUpdateCookie(sessionCookie);
            Check(await SaveSessionCookies(first.View.CoreWebView2), "session cookies saved with Windows user encryption");
            Check(!System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(SessionCookiesPath)).Contains("session-saved"), "cookie snapshot contains no plaintext values");
            manager.DeleteCookies("sharm_session_test", "https://example.com");
            manager.DeleteCookiesWithDomainAndPath("sharm_session_test", ".example.com", "/");
            await Task.Delay(300);
            Check(!(await manager.GetCookiesAsync("https://example.com")).Any(c => c.Name == "sharm_session_test"), "engine removes logged-out session cookie");
            Check(await SaveSessionCookies(first.View.CoreWebView2), "cookie deletion updates durable snapshot");
            restoredCookieProcesses.Remove(first.View.CoreWebView2.BrowserProcessId);
            await RestoreSessionCookies(first.View.CoreWebView2);
            Check(!(await manager.GetCookiesAsync("https://example.com")).Any(c => c.Name == "sharm_session_test"), "logged-out session is not resurrected");
            manager.AddOrUpdateCookie(sessionCookie);
            Check(await SaveSessionCookies(first.View.CoreWebView2), "session cookie ready for cross-process verification");
            var cookie = manager.CreateCookie("sharm_test", "saved", ".example.com", "/"); cookie.Expires = DateTime.UtcNow.AddDays(1); manager.AddOrUpdateCookie(cookie);
            Check((await manager.GetCookiesAsync("https://example.com")).Any(c => c.Name == "sharm_test"), "persistent profile cookies");
            first.Pinned = true;
            await AddTab();
            Check(first.View!.Visibility == Visibility.Collapsed, "background tab hidden");
            first.LastActive = DateTime.UtcNow.AddMinutes(-11);
            first.Pinned = true;
            await MaintainMemory();
            Check(first.View is not null, "pinned tab protected from unloading");
            first.Pinned = false;
            await MaintainMemory();
            Check(first.View is null, "idle tab renderer unloaded");
            await Activate(first);
            Check(first.View?.Visibility == Visibility.Visible, "unloaded tab recreated");
            Check((await first.View!.CoreWebView2.CookieManager.GetCookiesAsync("https://example.com")).Any(c => c.Name == "sharm_test"), "cookies retained after tab unloading");
            await CloseTab(first);
            Check(tabs.Count == 1, "close tab disposes renderer");
            OpenDownloads();
            Check(downloadRows is not null, "download history reopens after source tab closes");
            downloadsWindow!.Close();
            var home = active!;
            for (var index = 0; index < 100; index++) tabs.Add(new TabState { Url = $"https://example.com/#stress-{index}", Title = $"Page {index}" });
            RenderTabs(); UpdateLayout();
            Check(tabs.Count == 101 && tabs.All(tab => tab.View is null), "100 restored tabs allocate no web renderers");
            var realizedHeaders = Enumerable.Range(0, tabs.Count).Count(index => TabStrip.ItemContainerGenerator.ContainerFromIndex(index) is not null);
            Check(realizedHeaders < 24, "tab strip virtualizes offscreen headers");
            tabs.RemoveRange(1, 100); RenderTabs();
            await AddTab("https://example.com/#cache-one");
            await AddTab("https://example.com/#cache-two");
            await AddTab("https://example.com/#cache-three");
            Check(tabs.Count(tab => tab.View is not null) <= 1, "memory saver keeps only selected ordinary page loaded");
            var dirty = active!; dirty.HasUnsavedForm = true;
            await Activate(home);
            await MaintainMemory(releaseAll: true);
            Check(dirty.View is not null, "unsubmitted forms protected from unloading");
            dirty.HasUnsavedForm = false;
            await MaintainMemory(releaseAll: true);
            Check(tabs.All(tab => tab.View is null), "manual release unloads eligible background pages");
            foreach (var extra in tabs.Where(tab => tab != home).ToArray()) await CloseTab(extra);
            var burst = Enumerable.Range(0, 15).Select(index => AddTab($"https://example.com/#burst-{index}")).ToArray();
            await Task.WhenAll(burst);
            Check(active?.Url.EndsWith("#burst-14", StringComparison.Ordinal) == true, "rapid tab opening retains final selection");
            Check(tabs.Count(tab => tab.View is not null) <= 1, "rapid tab opening does not create parallel background renderers");
            foreach (var extra in tabs.Where(tab => tab != home).ToArray()) await CloseTab(extra);
            Check(closed.Count <= 20, "closed tab metadata stays bounded");
            SaveState();
            Check(File.Exists(StatePath) && JsonSerializer.Deserialize<SavedState>(File.ReadAllText(StatePath))?.Tabs.Count == 1, "session saved");
            tabs.Add(new TabState { Url = "https://example.com/#restored", Title = "Example Domain" });
            tabs.Add(new TabState { Url = "https://example.org/", Title = "Example" });
            active = tabs[1];
            SaveState();
        }
        catch (Exception ex) { results.Add("FAIL " + ex); }
        File.WriteAllLines(Path.Combine(App.ProfilePath, "self-test.txt"), results);
        Close();
    }
}




