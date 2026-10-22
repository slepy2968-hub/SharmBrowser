using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Sharm;

public partial class MainWindow
{
    private bool aggressiveMemory = true;
    private bool importingFirefox;
    private Window? settingsWindow;
    private string PreferencesPath => Path.Combine(App.ProfilePath, selfTest ? "test-settings.json" : "settings.json");

    private void LoadPreferences()
    {
        try
        {
            if (File.Exists(PreferencesPath))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(PreferencesPath));
                if (json.RootElement.TryGetProperty("aggressiveMemory", out var value)) aggressiveMemory = value.GetBoolean();
                if (json.RootElement.TryGetProperty("lightweightYoutube", out var youtube)) lightweightYoutube = youtube.GetBoolean();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
    }

    private void OpenSettings()
    {
        if (settingsWindow is not null) { settingsWindow.Activate(); return; }
        var window = CreatePanel("Настройки", "Ваш браузер. Ваш ритм.", "\uE713", out var content);
        settingsWindow = window;
        window.Closed += (_, _) => settingsWindow = null;
        var performance = PanelSection(content, "Производительность");
        var toggle = new CheckBox { Content = "Экономия памяти", IsChecked = aggressiveMemory, Foreground = Brush("#EEEEEE"), FontSize = 14 };
        performance.Children.Add(toggle);
        performance.Children.Add(new TextBlock { Text = "При включённой экономии загружена только выбранная страница. Фоновые страницы выгружаются сразу. Аудио, загрузки, закреплённые вкладки и формы сохраняются.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brush("#959595"), LineHeight = 20, Margin = new Thickness(0, 10, 0, 16) });
        toggle.Click += async (_, _) =>
        {
            aggressiveMemory = toggle.IsChecked == true;
            try { File.WriteAllText(PreferencesPath, JsonSerializer.Serialize(new { aggressiveMemory, lightweightYoutube })); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { statusMessage = ex.Message; }
            await MaintainMemory(enforceBudget: true);
        };
        var youtubeToggle = new CheckBox { Content = "Лёгкий YouTube · только плеер", IsChecked = lightweightYoutube, Foreground = Brush("#EEEEEE"), FontSize = 14, Margin = new Thickness(0, 0, 0, 12) };
        youtubeToggle.Click += async (_, _) =>
        {
            lightweightYoutube = youtubeToggle.IsChecked == true;
            try { File.WriteAllText(PreferencesPath, JsonSerializer.Serialize(new { aggressiveMemory, lightweightYoutube })); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { statusMessage = ex.Message; }
            if (active is { } current && YoutubeVideoId(current.Url) is not null) { current.FullYoutube = false; await Navigate(current.Url); }
        };
        performance.Children.Add(youtubeToggle);
        performance.Children.Add(PanelText("Открывает видео без тяжёлой ленты рекомендаций.", 12, "#959595", new Thickness(0, 0, 0, 6)));
        var resources = PanelSection(content, "Память браузера");
        var memory = new TextBlock { Text = MemorySummary(), Foreground = Brush("#959595"), FontSize = 12, Margin = new Thickness(0, 0, 0, 12) };
        resources.Children.Add(memory);
        var release = new Button { Content = "Выгрузить фоновые страницы", HorizontalAlignment = HorizontalAlignment.Left, Background = Brush("#2B2B2B"), Padding = new Thickness(15, 10, 15, 10) };
        release.Click += async (_, _) => { await MaintainMemory(releaseAll: true); memory.Text = MemorySummary(); };
        resources.Children.Add(release);
        var transfer = PanelSection(content, "Переезд из Firefox");
        var import = new Button { Content = "Импорт из Firefox", HorizontalAlignment = HorizontalAlignment.Left, Background = Brush("#2B2B2B"), Padding = new Thickness(15, 10, 15, 10) };
        var result = new TextBlock { Text = FirefoxImportCompleted() ? "Данные уже сохранены в Sharm. Вкладки Firefox не импортируются. Повторный перенос — только вручную." : "Однократный перенос закладок, истории и cookies. Без вкладок Firefox.", TextWrapping = TextWrapping.Wrap, Foreground = Brush("#959595"), FontSize = 12, Margin = new Thickness(0, 12, 0, 0) };
        import.Click += async (_, _) =>
        {
            if (importingFirefox) return;
            importingFirefox = true; import.IsEnabled = false; result.Text = "Перенос данных…";
            try
            {
                result.Text = await TransferFirefoxData();
                memory.Text = MemorySummary();
            }
            catch (Exception ex) { result.Text = "Импорт не завершён: " + ex.Message; }
            finally { importingFirefox = false; import.IsEnabled = true; }
        };
        transfer.Children.Add(import); transfer.Children.Add(result);

        window.Show();
    }

    private string MemorySummary()
    {
        long bytes = 0;
        var ids = new HashSet<int> { Environment.ProcessId };
        if (environment?.IsCompletedSuccessfully == true)
        {
            try { foreach (var info in environment.Result.GetProcessInfos()) ids.Add((int)info.ProcessId); }
            catch (InvalidOperationException) { }
        }
        foreach (var id in ids)
        {
            try { bytes += NativeMemory.PrivateResidentBytes(id); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return $"Загружено страниц: {tabs.Count(tab => tab.View is not null)} / {tabs.Count}\nПамять Sharm и страниц: ≈{bytes / 1048576} МБ";
    }
}
