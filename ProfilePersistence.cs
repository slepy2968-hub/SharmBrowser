using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Sharm;

public partial class MainWindow
{
    private readonly SemaphoreSlim cookieStorageGate = new(1, 1);
    private readonly HashSet<uint> restoredCookieProcesses = [];
    private string SessionCookiesPath => Path.Combine(BrowserProfile, "sharm-session-cookies.bin");
    private sealed record StoredSessionCookie(string Name, string Value, string Domain, string Path,
        bool Secure, bool HttpOnly, CoreWebView2CookieSameSiteKind SameSite);

    private async Task RestoreSessionCookies(CoreWebView2 core)
    {
        await cookieStorageGate.WaitAsync();
        try
        {
            if (!restoredCookieProcesses.Add(core.BrowserProcessId) || !File.Exists(SessionCookiesPath)) return;
            var encrypted = await File.ReadAllBytesAsync(SessionCookiesPath);
            var plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            try
            {
                var cookies = JsonSerializer.Deserialize<List<StoredSessionCookie>>(plaintext) ?? [];
                foreach (var saved in cookies)
                {
                    var cookie = core.CookieManager.CreateCookie(saved.Name, saved.Value, saved.Domain, saved.Path);
                    cookie.IsSecure = saved.Secure;
                    cookie.IsHttpOnly = saved.HttpOnly;
                    cookie.SameSite = !saved.Secure && saved.SameSite == CoreWebView2CookieSameSiteKind.None
                        ? CoreWebView2CookieSameSiteKind.Lax : saved.SameSite;
                    core.CookieManager.AddOrUpdateCookie(cookie);
                }
                await core.CookieManager.GetCookiesAsync("");
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or ArgumentException or InvalidOperationException)
        { statusMessage = "Не удалось восстановить сессионные cookies."; }
        finally { cookieStorageGate.Release(); }
    }

    private async Task<bool> SaveSessionCookies(CoreWebView2 core)
    {
        await cookieStorageGate.WaitAsync();
        try
        {
            var cookies = await core.CookieManager.GetCookiesAsync("");
            var snapshot = cookies.Where(cookie => cookie.IsSession).Select(cookie => new StoredSessionCookie(
                cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.IsSecure, cookie.IsHttpOnly, cookie.SameSite)).ToList();
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(snapshot);
            byte[] encrypted;
            try { encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
            Directory.CreateDirectory(BrowserProfile);
            await File.WriteAllBytesAsync(SessionCookiesPath + ".tmp", encrypted);
            File.Move(SessionCookiesPath + ".tmp", SessionCookiesPath, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException or InvalidOperationException)
        { statusMessage = "Не удалось сохранить сессионные cookies."; return false; }
        finally { cookieStorageGate.Release(); }
    }

    private bool FirefoxImportCompleted()
    {
        try
        {
            if (!File.Exists(ImportMarkerPath)) return false;
            using var json = JsonDocument.Parse(File.ReadAllText(ImportMarkerPath));
            return json.RootElement.TryGetProperty("version", out var version) && version.GetInt32() >= 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        { return false; }
    }

    private async Task ImportFirefoxOnce()
    {
        if (FirefoxImportCompleted()) return;
        try { statusMessage = await TransferFirefoxData(); }
        catch (Exception ex) { statusMessage = "Импорт не завершён: " + ex.Message; }
    }

    private async Task<string> TransferFirefoxData()
    {
        var data = await FirefoxImporter.ImportFromDefaultProfile();
        if (data is null) return "Профиль Firefox не найден.";
        MergeFirefoxData(data);
        var cookies = await ImportFirefoxCookies(data.Cookies);
        PersistState();
        var report = new
        {
            version = 2, data.ProfileName, importedAt = DateTimeOffset.Now,
            data.BookmarkCount, data.HistoryCount, OpenTabs = 0,
            cookiesAdded = cookies.Added, cookiesSkipped = cookies.Skipped + data.UnsupportedCookies,
            passwordsFound = data.SavedPasswords, passwordsImported = 0
        };
        await File.WriteAllTextAsync(ImportMarkerPath + ".tmp", JsonSerializer.Serialize(report));
        File.Move(ImportMarkerPath + ".tmp", ImportMarkerPath, true);
        return $"Сохранено в Sharm: закладок — {data.BookmarkCount}, адресов — {data.HistoryCount}, cookies — {cookies.Added}. "
            + (data.SavedPasswords == 0 ? "В Firefox нет сохранённых паролей. " : $"Пароли не перенесены: {data.SavedPasswords}; импорт в менеджер WebView2 недоступен. ")
            + (cookies.Skipped + data.UnsupportedCookies > 0 ? $"Пропущено несовместимых cookies: {cookies.Skipped + data.UnsupportedCookies}. " : "")
            + "Вкладки Firefox не переносятся. Повторный импорт при запуске отключён.";
    }
}
