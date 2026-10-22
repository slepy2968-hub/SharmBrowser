using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Sharm;

public sealed record FirefoxCookie(string Name, string Value, string Domain, string Path, bool IsSecure, bool IsHttpOnly, int SameSite, DateTimeOffset? Expires);
public sealed class FirefoxImportResult
{
    public string ProfileName { get; init; } = "";
    public List<Bookmark> Bookmarks { get; } = [];
    public List<VisitRecord> History { get; } = [];
    public List<FirefoxCookie> Cookies { get; } = [];
    public int SavedPasswords { get; set; }
    public int UnsupportedCookies { get; set; }
    public int BookmarkCount => Bookmarks.Count;
    public int HistoryCount => History.Count;
}

public static class FirefoxImporter
{
    public static async Task<FirefoxImportResult?> ImportFromDefaultProfile()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox");
        var profilesFile = Path.Combine(root, "profiles.ini");
        if (!File.Exists(profilesFile)) return null;
        var profiles = ParseProfiles(root, await File.ReadAllLinesAsync(profilesFile));
        var profile = profiles.FirstOrDefault(p => p.IsInstallDefault && Directory.Exists(p.Path))
            ?? profiles.FirstOrDefault(p => p.IsDefault && Directory.Exists(p.Path))
            ?? profiles.OrderByDescending(p => File.Exists(Path.Combine(p.Path, "places.sqlite")) ? File.GetLastWriteTimeUtc(Path.Combine(p.Path, "places.sqlite")) : DateTime.MinValue).FirstOrDefault(p => Directory.Exists(p.Path));
        if (profile is null) return null;
        var result = new FirefoxImportResult { ProfileName = profile.Name };
        var places = Path.Combine(profile.Path, "places.sqlite");
        if (File.Exists(places)) await ReadPlaces(places, result);
        var cookieDb = Path.Combine(profile.Path, "cookies.sqlite");
        if (File.Exists(cookieDb)) await ReadCookies(cookieDb, result);
        var loginDb = Path.Combine(profile.Path, "logins.db");
        if (File.Exists(loginDb)) result.SavedPasswords = await CountSavedLogins(loginDb);
        var legacyLogins = Path.Combine(profile.Path, "logins.json");
        if (!File.Exists(loginDb) && File.Exists(legacyLogins))
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(legacyLogins));
            if (json.RootElement.TryGetProperty("logins", out var logins)) result.SavedPasswords = logins.GetArrayLength();
        }
        return result;
    }

    private sealed record ProfileEntry(string Name, string Path, bool IsDefault, bool IsInstallDefault);

    private static List<ProfileEntry> ParseProfiles(string root, string[] lines)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? section = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1]; section = sections[name] = new(StringComparer.OrdinalIgnoreCase); continue;
            }
            var separator = line.IndexOf('=');
            if (section is not null && separator > 0) section[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        var installed = sections.Values.Where(s => s.Keys.Any(k => k.StartsWith("Default", StringComparison.OrdinalIgnoreCase)) && s.TryGetValue("Default", out _))
            .Select(s => s.GetValueOrDefault("Default", "")).Where(v => v.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return sections.Where(pair => pair.Key.StartsWith("Profile", StringComparison.OrdinalIgnoreCase)).Select(pair =>
        {
            var info = pair.Value;
            var relative = info.GetValueOrDefault("IsRelative", "1") == "1";
            var path = info.GetValueOrDefault("Path", "");
            return new ProfileEntry(info.GetValueOrDefault("Name", pair.Key), relative ? Path.GetFullPath(Path.Combine(root, path)) : path,
                info.GetValueOrDefault("Default", "0") == "1", installed.Contains(path));
        }).Where(p => p.Path.Length > 0).ToList();
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static async Task ReadPlaces(string path, FirefoxImportResult result)
    {
        await using var connection = OpenReadOnly(path);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT b.title, p.url
                FROM moz_bookmarks AS b
                JOIN moz_places AS p ON p.id = b.fk
                WHERE b.type = 1 AND p.url IS NOT NULL
                  AND (p.url LIKE 'http://%' OR p.url LIKE 'https://%')
                  AND lower(coalesce((SELECT folder.title FROM moz_bookmarks AS folder WHERE folder.id=b.parent), '')) NOT IN ('firefox', 'mozilla firefox')
                ORDER BY b.position
                ;
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var url = reader.GetString(1);
                if (!MainWindow.IsWebUrl(url)) continue;
                result.Bookmarks.Add(new Bookmark(reader.IsDBNull(0) ? new Uri(url).Host : reader.GetString(0), url));
            }
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT p.url, coalesce(p.title, ''), count(v.id), max(v.visit_date)
                FROM moz_historyvisits AS v
                JOIN moz_places AS p ON p.id = v.place_id
                WHERE p.url LIKE 'http://%' OR p.url LIKE 'https://%'
                GROUP BY p.id ORDER BY max(v.visit_date) DESC;
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var url = reader.GetString(0);
                if (!MainWindow.IsWebUrl(url)) continue;
                var micros = reader.IsDBNull(3) ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000 : reader.GetInt64(3);
                DateTime visited;
                try { visited = DateTime.UnixEpoch.AddTicks(checked(micros * 10L)); }
                catch (ArgumentOutOfRangeException) { visited = DateTime.UtcNow; }
                result.History.Add(new VisitRecord { Url = url, Title = reader.GetString(1), Visits = reader.GetInt32(2), LastVisit = visited });
            }
        }
    }

    private static async Task ReadCookies(string path, FirefoxImportResult result)
    {
        await using var connection = OpenReadOnly(path);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, value, host, path, expiry, isSecure, isHttpOnly, sameSite,
                   originAttributes, coalesce(isPartitionedAttributeSet, 0)
            FROM moz_cookies ORDER BY lastAccessed DESC;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        while (await reader.ReadAsync())
        {
            var attributes = reader.GetString(8);
            if (attributes.Length > 0 || reader.GetInt32(9) != 0) { result.UnsupportedCookies++; continue; }
            var expiry = reader.GetInt64(4);
            if (expiry > 0 && expiry < 100_000_000_000L) expiry *= 1000;
            if (expiry > 0 && expiry <= now) continue;
            DateTimeOffset? expiration = expiry > now ? DateTimeOffset.FromUnixTimeMilliseconds(expiry) : null;
            var sameSite = reader.GetInt32(7) & 0xff;
            if (sameSite is < 0 or > 3) sameSite = 0;
            result.Cookies.Add(new FirefoxCookie(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt32(5) != 0, reader.GetInt32(6) != 0, sameSite, expiration));
        }
    }

    private static async Task<int> CountSavedLogins(string path)
    {
        await using var connection = OpenReadOnly(path);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('loginsL','loginsM')";
        await using var reader = await command.ExecuteReaderAsync();
        var count = 0;
        while (await reader.ReadAsync())
        {
            var table = reader.GetString(0);
            await using var countCommand = connection.CreateCommand();
            countCommand.CommandText = table == "loginsL"
                ? "SELECT count(*) FROM loginsL WHERE is_deleted=0"
                : "SELECT count(*) FROM loginsM WHERE is_overridden=0";
            count += Convert.ToInt32(await countCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }
        return count;
    }

}
