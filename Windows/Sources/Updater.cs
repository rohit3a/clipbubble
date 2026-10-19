using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ClipBubble;

/// Updates ClipBubble from its GitHub releases: checks now and then, and on request downloads the new exe,
/// makes sure it's ClipBubble at that version, swaps it in beside the running one and restarts.
sealed class Updater
{
    public static readonly Updater Shared = new();
    public const string Releases = "https://github.com/rohit3a/clipbubble/releases/latest";
    const string Latest = "https://api.github.com/repos/rohit3a/clipbubble/releases/latest";

    public sealed record Release(string Version, string Exe, string Page);

    public abstract record State;
    public sealed record Idle : State;
    public sealed record Checking : State;
    public sealed record Current : State;
    public sealed record Available(Release Release) : State;
    public sealed record Installing(string Step) : State;
    public sealed record Failed(string Why) : State;

    public State Now { get; private set; } = new Idle();
    /// Raised on the UI thread.
    public event Action? Changed;

    public static bool Automatic
    {
        get => Prefs.Get("updates.automatic", true);
        set => Prefs.Set("updates.automatic", value);
    }

    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromHours(6) };

    public void Start()
    {
        timer.Tick += (_, _) => Check(quietly: true);
        timer.Start();
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        first.Tick += (_, _) => { first.Stop(); Check(quietly: true); };
        first.Start();
    }

    /// A background check stays quiet when it can't reach GitHub, and leaves a found update or an install alone.
    public void Check(bool quietly = false)
    {
        if (Now is Checking or Installing || (quietly && (Now is Available || !Automatic))) return;
        if (!quietly) Set(new Checking());
        Task.Run(() =>
        {
            try
            {
                var r = Parse(Get(Latest));
                Post(() => Set(IsNewer(r.Version, Engine.Version) ? new Available(r) : new Current()));
            }
            catch (Exception e) when (e is WebException or IOException or FormatException or InvalidOperationException)
            {
                if (!quietly) Post(() => Set(new Failed("Couldn't check for updates: " + e.Message)));
            }
        });
    }

    public void Install(Release r)
    {
        if (Now is Installing) return;
        Set(new Installing($"Downloading {r.Version}…"));
        var exe = Process.GetCurrentProcess().MainModule!.FileName;
        Task.Run(() =>
        {
            try
            {
                Replace(exe, r, step => Post(() => Set(new Installing(step))));
                Post(() =>
                {
                    Set(new Installing("Restarting…"));
                    Process.Start(new ProcessStartInfo(exe, "--after " + Process.GetCurrentProcess().Id) { UseShellExecute = false });
                    Application.Current.Shutdown();
                });
            }
            catch (Exception e) when (e is WebException or IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
            {
                Log.Error(e);
                Post(() => Set(new Failed(e.Message)));
            }
        });
    }

    /// Started by an update: waits for the old copy to quit so the single-instance lock is free, and tidies up after it.
    public static void AfterUpdate(string[] args)
    {
        var i = Array.IndexOf(args, "--after");
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var pid))
        {
            try { using var old = Process.GetProcessById(pid); old.WaitForExit(15000); }
            catch (ArgumentException) { }   // already gone
        }
        try { File.Delete(Process.GetCurrentProcess().MainModule!.FileName + ".old"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static bool IsNewer(string a, string b) =>
        System.Version.TryParse(a, out var x) && System.Version.TryParse(b, out var y) && x > y;

    public static Release Parse(string json)
    {
        var o = Store.Json.DeserializeObject(json) as Dictionary<string, object>;
        var tag = o?.TryGetValue("tag_name", out var t) == true ? t as string : null;
        var page = o?.TryGetValue("html_url", out var p) == true ? p as string : null;
        var exe = (o?.TryGetValue("assets", out var a) == true ? a as object[] : null)?
            .OfType<Dictionary<string, object>>()
            .FirstOrDefault(x => x.TryGetValue("name", out var n) && n as string == "ClipBubble.exe")?["browser_download_url"] as string;
        if (tag == null || page == null || exe == null) throw new InvalidOperationException("the latest release has no Windows download");
        return new Release(tag.TrimStart('v'), exe, page);
    }

    /// Downloads beside the running exe (same folder, so the swap is a rename), checks it, then swaps:
    /// Windows lets a running exe be renamed, just not overwritten.
    static void Replace(string exe, Release r, Action<string> progress)
    {
        var dir = Path.GetDirectoryName(exe)!;
        var fresh = exe + ".new";
        var old = exe + ".old";
        try { File.WriteAllText(fresh, ""); }
        catch (UnauthorizedAccessException) { throw new UnauthorizedAccessException($"ClipBubble can't replace itself in {dir}. Download {r.Version} from the releases page instead."); }
        try
        {
            using (var web = Client()) web.DownloadFile(r.Exe, fresh);
            progress("Checking the download…");
            Verify(fresh, r.Version, exe);
            progress($"Installing {r.Version}…");
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { File.Move(fresh, exe); }
            catch { File.Move(old, exe); throw; }
        }
        finally
        {
            try { File.Delete(fresh); } catch (IOException) { }
        }
    }

    /// The new exe must be ClipBubble at the advertised version, and when this copy is signed, signed by the same publisher.
    static void Verify(string fresh, string version, string exe)
    {
        var name = AssemblyName.GetAssemblyName(fresh);   // throws BadImageFormatException for anything that isn't a .NET exe
        if (name.Name != "ClipBubble" || !System.Version.TryParse(version, out var want) || name.Version.ToString(3) != new System.Version(want.Major, want.Minor, Math.Max(want.Build, 0)).ToString(3))
            throw new InvalidOperationException($"the download isn't ClipBubble {version}");
        var publisher = Publisher(exe);
        if (publisher != null && Publisher(fresh) != publisher)
            throw new InvalidOperationException("the download isn't signed by the same publisher as this copy, so it wasn't installed");
    }

    static string? Publisher(string path)
    {
        try { return X509Certificate.CreateFromSignedFile(path).Subject; }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    static string Get(string url)
    {
        using var web = Client();
        web.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
        return web.DownloadString(url);
    }

    static WebClient Client()
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var web = new WebClient();
        web.Headers[HttpRequestHeader.UserAgent] = "ClipBubble/" + Engine.Version;
        return web;
    }

    void Set(State s)
    {
        Now = s;
        Changed?.Invoke();
    }

    static void Post(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);
}
