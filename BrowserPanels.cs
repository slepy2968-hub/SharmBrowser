using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Sharm;

public partial class MainWindow
{
    private Window? downloadsWindow;
    private StackPanel? downloadRows;
    private TextBlock? downloadsNotice;
    private sealed class DownloadItem(CoreWebView2DownloadOperation operation)
    {
        public CoreWebView2DownloadOperation Operation { get; } = operation;
        public string Path { get; } = operation.ResultFilePath;
        public string Source { get; } = operation.Uri;
        public long Received { get; private set; }
        public ulong? Total { get; private set; }
        public CoreWebView2DownloadState State { get; private set; }
        public CoreWebView2DownloadInterruptReason Reason { get; private set; }
        public bool CanResume { get; private set; }
        public void Refresh()
        {
            Received = Operation.BytesReceived; Total = Operation.TotalBytesToReceive;
            State = Operation.State; Reason = Operation.InterruptReason; CanResume = Operation.CanResume;
        }
    }
    private readonly List<DownloadItem> downloadOperations = [];

    private Window CreatePanel(string title, string subtitle, string glyph, out StackPanel content)
    {
        var window = new Window
        {
            Owner = this, Title = "Sharm · " + title, Width = 610, Height = 780,
            MinWidth = 480, MinHeight = 420, MaxHeight = SystemParameters.WorkArea.Height - 30,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.CanResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("#171717"), Foreground = Brush("#EEEEEE"),
            FontFamily = (FontFamily)FindResource("InterfaceFont"), UseLayoutRounding = true
        };
        WindowChrome.SetWindowChrome(window, new WindowChrome { CaptionHeight = 48, ResizeBorderThickness = new Thickness(5), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(16), UseAeroCaptionButtons = false });
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        var chrome = new DockPanel { Margin = new Thickness(22, 0, 8, 0) };
        var close = new Button { Content = "\uE8BB", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11, Width = 34, Height = 32, ToolTip = "Закрыть" };
        close.Click += (_, _) => window.Close();
        WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        DockPanel.SetDock(close, Dock.Right); chrome.Children.Add(close);
        chrome.Children.Add(new TextBlock { Text = "◒  Sharm", Foreground = Brush("#999999"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        layout.Children.Add(chrome);
        var heading = new StackPanel { Margin = new Thickness(30, 14, 30, 24) };
        heading.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 23, Foreground = Brush("#BFC0CC"), Margin = new Thickness(0, 0, 0, 17) });
        heading.Children.Add(new TextBlock { Text = title, FontSize = 30, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(PanelText(subtitle, 13, "#929292", new Thickness(0, 8, 0, 0)));
        Grid.SetRow(heading, 1); layout.Children.Add(heading);
        content = new StackPanel { Margin = new Thickness(30, 0, 30, 25) };
        var scroll = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 2); layout.Children.Add(scroll);
        window.Content = new Border { BorderBrush = Brush("#363636"), BorderThickness = new Thickness(1), Child = layout };
        window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { window.Close(); e.Handled = true; } };
        return window;
    }

    private static TextBlock PanelText(string text, double size = 12, string color = "#EEEEEE", Thickness margin = default) =>
        new() { Text = text, FontSize = size, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap, LineHeight = size * 1.55, Margin = margin };

    private static StackPanel PanelSection(StackPanel parent, string title)
    {
        if (title.Length > 0) parent.Children.Add(PanelText(title, 13, "#AAAAAA", new Thickness(2, 0, 0, 10)));
        var body = new StackPanel();
        parent.Children.Add(new Border { Background = Brush("#202020"), BorderBrush = Brush("#303030"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Padding = new Thickness(18), Margin = new Thickness(0, 0, 0, 22), Child = body });
        return body;
    }

    private static Button PanelButton(string title, Action action)
    {
        var button = new Button { Content = title, HorizontalAlignment = HorizontalAlignment.Left, Background = Brush("#303030"), Foreground = Brush("#E8E8E8"), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 8, 6, 0), FontSize = 12 };
        button.Click += (_, _) => action();
        return button;
    }

    private void TrackDownload(CoreWebView2DownloadOperation operation)
    {
        var item = new DownloadItem(operation);
        item.Refresh();
        operation.BytesReceivedChanged += (_, _) => { item.Refresh(); UpdateDownloadRows(); };
        operation.StateChanged += (_, _) => { item.Refresh(); UpdateDownloadRows(); };
        downloadOperations.Insert(0, item);
        OpenDownloads();
    }

    private void OpenDownloads()
    {
        if (downloadsWindow is not null) { RenderDownloads(); downloadsWindow.Activate(); return; }
        var window = CreatePanel("Загрузки", "Все файлы под рукой.", "\uE896", out var content);
        downloadsWindow = window;
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 20) };
        actions.Children.Add(PanelButton("Открыть папку загрузок", () =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:Downloads") { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception ex) { if (downloadsNotice is not null) downloadsNotice.Text = ex.Message; }
        }));
        actions.Children.Add(PanelButton("Очистить завершённые", () => { downloadOperations.RemoveAll(item => item.State == CoreWebView2DownloadState.Completed); RenderDownloads(); }));
        content.Children.Add(actions);
        downloadsNotice = PanelText("", 12, "#E5B8AC", new Thickness(0, 0, 0, 8)); content.Children.Add(downloadsNotice);
        downloadRows = new StackPanel(); content.Children.Add(downloadRows);
        window.Closed += (_, _) => { downloadsWindow = null; downloadRows = null; downloadsNotice = null; };
        RenderDownloads(); window.Show();
    }

    private void RenderDownloads()
    {
        if (downloadRows is null) return;
        downloadRows.Children.Clear();
        if (downloadOperations.Count == 0)
        {
            var empty = PanelSection(downloadRows, "Файлы");
            empty.Children.Add(new TextBlock { Text = "\uE896", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 32, Foreground = Brush("#777777"), Margin = new Thickness(0, 15, 0, 16) });
            empty.Children.Add(PanelText("Пока нет загрузок", 20));
            empty.Children.Add(PanelText("Скачанные файлы появятся здесь.\nОткрывайте эту панель сочетанием Ctrl+J.", 13, "#999999", new Thickness(0, 8, 0, 15)));
        }
        foreach (var item in downloadOperations)
        {
            var operation = item.Operation;
            var row = PanelSection(downloadRows, "");
            row.Tag = item;
            row.Children.Add(PanelText(Path.GetFileName(item.Path), 15));
            var source = Uri.TryCreate(item.Source, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : "Файл со страницы";
            row.Children.Add(PanelText(source, 11, "#858585", new Thickness(0, 4, 0, 12)));
            row.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Height = 4, Margin = new Thickness(0, 0, 0, 10) });
            row.Children.Add(PanelText("", 12, "#A5A5AE"));
            var actions = new WrapPanel();
            actions.Children.Add(PanelButton("Пауза", () => { if (operation.State == CoreWebView2DownloadState.InProgress) operation.Pause(); UpdateDownloadRows(); }));
            actions.Children.Add(PanelButton("Продолжить", () => { if (operation.CanResume) operation.Resume(); UpdateDownloadRows(); }));
            actions.Children.Add(PanelButton("Отменить", () => { if (operation.State == CoreWebView2DownloadState.InProgress || operation.CanResume) operation.Cancel(); UpdateDownloadRows(); }));
            actions.Children.Add(PanelButton("Открыть файл", () => OpenDownloadPath(item.Path)));
            actions.Children.Add(PanelButton("В папке", () => OpenDownloadPath(item.Path, true)));
            row.Children.Add(actions);
        }
        UpdateDownloadRows();
    }

    private void UpdateDownloadRows()
    {
        if (downloadRows is null) return;
        foreach (var border in downloadRows.Children.OfType<Border>())
        {
            if (border.Child is not StackPanel { Tag: DownloadItem item } row) continue;
            var complete = item.State == CoreWebView2DownloadState.Completed;
            var running = item.State == CoreWebView2DownloadState.InProgress;
            var progress = row.Children.OfType<ProgressBar>().First();
            var total = item.Total;
            progress.Value = complete ? 100 : total > 0 ? Math.Clamp(item.Received * 100d / total.Value, 0, 100) : 0;
            static string Size(double bytes) => bytes < 1024 ? $"{bytes:0} Б" : bytes < 1048576 ? $"{bytes / 1024:0.#} КБ" : bytes < 1073741824 ? $"{bytes / 1048576:0.#} МБ" : $"{bytes / 1073741824:0.#} ГБ";
            var size = Size(item.Received);
            if (total > 0) size += " из " + Size(total.Value);
            var state = complete ? "Готово" : running ? "Скачивается" : item.Reason == CoreWebView2DownloadInterruptReason.UserPaused ? "На паузе" : item.Reason == CoreWebView2DownloadInterruptReason.UserCanceled ? "Отменено" : "Прервано: " + item.Reason;
            row.Children.OfType<TextBlock>().Last().Text = state + " · " + size;
            var buttons = row.Children.OfType<WrapPanel>().First().Children.OfType<Button>().ToArray();
            buttons[0].Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            buttons[1].Visibility = item.CanResume ? Visibility.Visible : Visibility.Collapsed;
            buttons[2].Visibility = running || item.CanResume ? Visibility.Visible : Visibility.Collapsed;
            buttons[3].Visibility = complete ? Visibility.Visible : Visibility.Collapsed;
            buttons[4].Visibility = complete ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OpenDownloadPath(string path, bool select = false)
    {
        if (downloadsNotice is not null) downloadsNotice.Text = "";
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) { if (downloadsNotice is not null) downloadsNotice.Text = "Файл или папка больше не существует."; return; }
            if (select) Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = "/select,\"" + path + "\"", UseShellExecute = true });
            else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { if (downloadsNotice is not null) downloadsNotice.Text = ex.Message; }
    }
}
