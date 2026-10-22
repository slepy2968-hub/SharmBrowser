using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Sharm;

public partial class MainWindow
{
    private readonly bool benchmarkMemory;

    private object MeasureMemory(string scenario, bool? videoPlaying = null)
    {
        var ids = new HashSet<int> { Environment.ProcessId };
        if (environment?.IsCompletedSuccessfully == true)
            foreach (var item in environment.Result.GetProcessInfos()) ids.Add((int)item.ProcessId);
        long resident = 0, committed = 0;
        foreach (var id in ids)
        {
            try
            {
                resident += NativeMemory.PrivateResidentBytes(id);
                using var process = Process.GetProcessById(id); committed += process.PrivateMemorySize64;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return new { scenario, tabs = tabs.Count, loadedPages = tabs.Count(tab => tab.View is not null), processes = ids.Count,
            privateResidentMB = Math.Round(resident / 1048576d, 1), privateCommittedMB = Math.Round(committed / 1048576d, 1), videoPlaying,
            lightweightPlayer = active?.View?.CoreWebView2 is { } core && IsYoutubePlayer(core.Source) };
    }

    private async Task RunMemoryBenchmark()
    {
        var samples = new List<object>();
        var file = Path.Combine(App.ProfilePath, "memory-benchmark.json");
        try
        {
            lightweightYoutube = false;
            await AddTab(); restored = true;
            await Task.Delay(1500);
            samples.Add(MeasureMemory("native home page"));
            File.WriteAllText(file, JsonSerializer.Serialize(samples));
            await AddTab("https://www.youtube.com/watch?v=jNQXAC9IVRw");
            await Task.Delay(12000);
            await active!.View!.CoreWebView2.ExecuteScriptAsync("(()=>{const v=document.querySelector('video');if(v){v.muted=true;v.loop=true;v.play().catch(()=>{});}})()");
            await Task.Delay(4000);
            var playing = await active.View.CoreWebView2.ExecuteScriptAsync("!!document.querySelector('video')&&!document.querySelector('video').paused&&document.querySelector('video').readyState>=2");
            samples.Add(MeasureMemory("YouTube watch page", playing == "true"));
            for (var index = 0; index < 98; index++) tabs.Add(new TabState { Url = $"https://example.com/#saved-{index}", Title = $"Saved {index}" });
            RenderTabs();
            await Task.Delay(3000);
            playing = await active.View.CoreWebView2.ExecuteScriptAsync("!!document.querySelector('video')&&!document.querySelector('video').paused&&document.querySelector('video').readyState>=2");
            samples.Add(MeasureMemory("100 tabs, YouTube selected", playing == "true"));
            await AddTab();
            await Task.Delay(2000);
            lightweightYoutube = true;
            await AddTab("https://www.youtube.com/watch?v=jNQXAC9IVRw");
            await Task.Delay(12000);
            await active!.View!.CoreWebView2.ExecuteScriptAsync("if(window.player?.mute){window.player.mute();window.player.playVideo()}");
            await Task.Delay(4000);
            playing = await active.View.CoreWebView2.ExecuteScriptAsync("window.player?.getPlayerState?.()===1");
            samples.Add(MeasureMemory("Sharm lightweight YouTube player, saved tabs retained", playing == "true"));
            await AddTab();
            await MaintainMemory(releaseAll: true);
            await Task.Delay(3000);
            samples.Add(MeasureMemory("home after unloading pages"));
        }
        catch (Exception ex) { samples.Add(new { error = ex.GetType().Name + ": " + ex.Message }); }
        File.WriteAllText(file, JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
        Close();
    }
}
