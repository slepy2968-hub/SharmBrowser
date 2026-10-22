using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace Sharm;

public partial class MainWindow
{
    private bool lightweightYoutube = true;
    private static readonly Lazy<byte[]> YoutubePage = new(() =>
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("Sharm.Assets.Youtube.html")
            ?? throw new InvalidOperationException("YouTube player resource is missing.");
        using var output = new MemoryStream(); resource.CopyTo(output); return output.ToArray();
    });

    private static string? YoutubeVideoId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var host = uri.IdnHost;
        string? id = null;
        if (host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)) id = uri.AbsolutePath.Trim('/');
        else if ((host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)) && uri.AbsolutePath == "/watch")
            id = uri.Query.TrimStart('?').Split('&').FirstOrDefault(part => part.StartsWith("v=", StringComparison.Ordinal))?[2..];
        else if ((host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)) && (uri.AbsolutePath.StartsWith("/shorts/", StringComparison.Ordinal) || uri.AbsolutePath.StartsWith("/live/", StringComparison.Ordinal)))
            id = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
        return id is { Length: 11 } && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-') ? id : null;
    }

    private static bool IsYoutubePlayer(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IdnHost == "sharm.local" && uri.AbsolutePath == "/youtube";

    private string ContentAddress(TabState tab)
    {
        var id = lightweightYoutube && !tab.FullYoutube ? YoutubeVideoId(tab.Url) : null;
        if (id is null) return tab.Url;
        var query = new Uri(tab.Url).Query.TrimStart('?').Split('&');
        var time = query.FirstOrDefault(part => part.StartsWith("t=", StringComparison.Ordinal))?[2..]
            ?? query.FirstOrDefault(part => part.StartsWith("start=", StringComparison.Ordinal))?[6..];
        var seconds = 0;
        if (time is { Length: <= 32 })
        {
            var match = System.Text.RegularExpressions.Regex.Match(time, @"^(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s?)?$");
            if (match.Success)
            {
                int.TryParse(match.Groups[1].Value, out var hours); int.TryParse(match.Groups[2].Value, out var minutes); int.TryParse(match.Groups[3].Value, out var remainder);
                seconds = (int)Math.Clamp((long)hours * 3600 + (long)minutes * 60 + remainder, 0, 86400);
            }
        }
        return "https://sharm.local/youtube?v=" + id + (seconds > 0 ? "&start=" + seconds : "");
    }

    private void ConfigureYoutubePlayer(TabState tab, CoreWebView2 core)
    {
        core.AddWebResourceRequestedFilter("https://sharm.local/youtube*", CoreWebView2WebResourceContext.Document);
        core.WebResourceRequested += (_, e) =>
        {
            if (!IsYoutubePlayer(e.Request.Uri)) return;
            e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(YoutubePage.Value, writable: false), 200, "OK", "Content-Type: text/html; charset=utf-8\r\nReferrer-Policy: strict-origin-when-cross-origin");
        };
        core.WebMessageReceived += (_, e) =>
        {
            if (e.WebMessageAsJson != "\"sharm:youtube-full\"" || !IsYoutubePlayer(e.Source) || YoutubeVideoId(tab.Url) is null || closing) return;
            tab.FullYoutube = true;
            Dispatcher.BeginInvoke(async () => { if (active == tab && !closing) await Navigate(tab.Url); });
        };
    }
}
