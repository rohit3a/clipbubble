using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace ClipBubble;

// MARK: - Model

/// What changed in the model, so views redraw only the part they show.
enum Change { Clips, Recent, Query, Filter, Selection, Previewing, ShowSettings, Unseen, Mascot, Prefs, Shown, Transfers, Sync }

/// The app's state. Views subscribe to `Changed` and redraw the part named in it, like a SwiftUI ObservableObject.
sealed class Model
{
    public static readonly (string Tag, string Title)[] Filters =
        { ("all", "All"), ("text", "Text"), ("image", "Images"), ("file", "Files"), ("shots", "Screenshots"), ("downloads", "Downloads") };

    public event Action<Change>? Changed;
    void Raise(Change what) => Changed?.Invoke(what);

    public List<Clip> Clips { get; private set; } = new();
    public List<Clip> Recent { get; private set; } = new();
    string query = "", filter = "all";
    public string Query { get => query; set { if (value == query) return; query = value; Raise(Change.Query); Reload(); } }
    public string Filter { get => filter; set { if (value == filter) return; filter = value; Raise(Change.Filter); Reload(); } }
    long? selection;
    public long? Selection { get => selection; set { selection = value; Raise(Change.Selection); } }
    bool previewing, showSettings;
    public bool Previewing { get => previewing; set { previewing = value; Raise(Change.Previewing); } }
    public bool ShowSettings { get => showSettings; set { showSettings = value; Raise(Change.ShowSettings); } }
    public int Unseen { get => unseen; set { unseen = value; Raise(Change.Unseen); } }
    int unseen;
    public Mascot Mascot { get; private set; } = Mascots.Active();
    public List<Mascot> AllMascots { get; private set; } = Mascots.All();
    /// Reads happen here on the UI thread; every write goes through `Writer`.
    public readonly Db Db = new();
    public readonly Writer Writer = new();
    /// Sync with ClipBubble on other machines; null until the app starts it.
    public Engine? Sync;
    /// Files moving to or from other machines right now, by clip hash.
    public Dictionary<string, Transfer> Transfers { get; private set; } = new();
    /// Linked devices' names by id, for items synced from them.
    public Dictionary<string, string> Devices { get; private set; } = new();

    /// A transfer that finished changes the item itself, so that reloads; progress only redraws the rows.
    public void SetTransfers(Dictionary<string, Transfer> t)
    {
        var finished = Transfers.Keys.Any(h => !t.ContainsKey(h));
        Transfers = t;
        if (finished) Refresh(); else Raise(Change.Transfers);
    }

    public void SyncChanged() => Raise(Change.Sync);

    public Action<Clip, bool> Pick = (_, _) => { };
    public Action PrefsChanged = () => { };
    /// Tells the user something they asked for didn't work (title, then the reason).
    public Action<string, string> ShowError = (_, _) => { };
    /// Raised when the panel opens, after Reset has reloaded it: refocuses search and scrolls to the top.
    public void Shown() => Raise(Change.Shown);

    public Clip? Selected => Clips.FirstOrDefault(c => c.Id == Selection) ?? Clips.FirstOrDefault();

    /// Fresh state for a panel opening: no search, All, first item selected, with a single query.
    public void Reset()
    {
        (query, filter) = ("", "all");
        Raise(Change.Query);
        Raise(Change.Filter);
        Previewing = false;
        ShowSettings = false;
        Reload();
        Selection = Clips.FirstOrDefault()?.Id;
    }

    public void Reload()
    {
        var kind = Store.ParseKind(Filter);
        var source = Filter switch { "shots" => "Screenshot", "downloads" => "Downloads", _ => null };
        Clips = Db.Clips(kind: kind, source: source, query: Query).Where(c => !deleting.Contains(c.Id)).ToList();
        if (Clips.Any(c => c.Origin.Length > 0)) Devices = Db.DeviceNames();
        if (!Clips.Any(c => c.Id == Selection)) selection = Clips.FirstOrDefault()?.Id;
        Thumbs.Prewarm(Clips.Take(40));
        Raise(Change.Clips);
    }

    public void ReloadRecent()
    {
        Recent = Db.Clips(limit: 3 + deleting.Count).Where(c => !deleting.Contains(c.Id)).Take(3).ToList();
        Thumbs.Prewarm(Recent);
        Raise(Change.Recent);
    }

    public void SetPref(string key, bool on)
    {
        Prefs.SetOn(key, on);
        Refresh();
        PrefsChanged();
        Raise(Change.Prefs);
    }

    public void LoadMascots()
    {
        Glyphs.ClearCache();
        AllMascots = Mascots.All();
        Mascot = Mascots.Active(AllMascots);
        Raise(Change.Mascot);
    }

    public void Move(int delta)
    {
        if (Clips.Count == 0) return;
        var i = Math.Max(0, Clips.FindIndex(c => c.Id == Selection));
        Selection = Clips[Math.Min(Math.Max(i + delta, 0), Clips.Count - 1)].Id;
    }

    public void CycleFilter(int delta)
    {
        var i = Math.Max(0, Array.FindIndex(Filters, f => f.Tag == Filter));
        Filter = Filters[(i + delta + Filters.Length) % Filters.Length].Tag;
    }

    /// Clips deleted here that the writer hasn't removed yet: reloads leave them out, so they can't be deleted twice.
    readonly HashSet<long> deleting = new();
    bool clearing;
    public bool DeletesWaiting => deleting.Count > 0 || clearing;

    /// Gone from the list at once; the reload after the write shows what's really stored (the clip again, if the delete failed).
    public void Delete(Clip c)
    {
        var i = Math.Max(0, Clips.FindIndex(x => x.Id == c.Id));
        deleting.Add(c.Id);
        Clips.RemoveAll(x => x.Id == c.Id);
        if (Selection == c.Id) selection = Clips.Count == 0 ? null : Clips[Math.Min(i, Clips.Count - 1)].Id;
        Raise(Change.Clips);
        if (Recent.RemoveAll(x => x.Id == c.Id) > 0) Raise(Change.Recent);
        Writer.Write(db => db.Delete(c.Id), failed =>
        {
            deleting.Remove(c.Id);
            Refresh();
            if (failed is { } e) ShowError("Couldn't delete that", Why(e));
        });
    }

    public void ClearAll()
    {
        clearing = true;
        Writer.Write(db => db.DeleteAll(), failed =>
        {
            clearing = false;
            Refresh();
            if (failed is { } e) ShowError("Couldn't clear history", Why(e));
        });
    }

    /// SQLite reports a database another process holds as busy or locked; anything else (a full disk) is said as is.
    static string Why(Exception e) => e.Message.Contains("locked") || e.Message.Contains("busy")
        ? "Another app is using ClipBubble's history right now. Try again in a moment."
        : e.Message;

    public void Refresh()
    {
        Reload();
        ReloadRecent();
    }
}

// MARK: - Thumbnails

/// Decodes small thumbnails off the UI thread and keeps them in memory, so rows never flash.
static class Thumbs
{
    // UI thread only. Requests for a picture that's already decoding wait for that decode instead of starting another.
    static readonly Dictionary<(string, int), BitmapSource?> Cache = new();
    static readonly Dictionary<(string, int), List<Action<BitmapSource?>>> Pending = new();
    static readonly Dictionary<string, Size> Dims = new();
    /// Pictures that failed to decode, by file version: tried again only once the file changes (it may have been saving).
    static readonly Dictionary<(string, int), (long, DateTime)> Failed = new();

    static (long, DateTime) Version(string path)
    {
        var f = new FileInfo(path);
        return f.Exists ? (f.Length, f.LastWriteTimeUtc) : (-1, DateTime.MinValue);
    }

    public static Size? SizeOf(string path) => Dims.TryGetValue(path, out var s) ? s : null;

    /// Calls `done` right away for a cached thumbnail, otherwise once it's decoded.
    public static void Load(string path, int px, Action<BitmapSource?> done)
    {
        var key = (path, px);
        if (Cache.TryGetValue(key, out var hit)) { done(hit); return; }
        if (Pending.TryGetValue(key, out var waiting)) { waiting.Add(done); return; }
        Pending[key] = new List<Action<BitmapSource?>> { done };
        (long, DateTime)? failed = Failed.TryGetValue(key, out var f) ? f : null;
        var dispatcher = Dispatcher.CurrentDispatcher;
        Task.Run(() =>
        {
            BitmapSource? img = null;
            Size? dims = null;
            (long, DateTime)? version = null;
            try
            {
                version = Version(path);   // here, not on the UI thread: a network drive can take a while to answer
                if (version != failed) (img, dims) = Store.DecodeFit(path, px);
            }
            catch (Exception e)
            {
                // Whatever failed, the callbacks below still run; otherwise this picture would stay blank until a restart.
                if (!Store.Unreadable(e)) Log.Error(e);
            }
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (img == null && version is { } v) Failed[key] = v;
                else if (px <= 64) Remember(Cache, key, img);   // row thumbnails only, so previews don't pile up
                if (dims is { } d) Dims[path] = d;
                var callbacks = Pending[key];
                Pending.Remove(key);
                foreach (var callback in callbacks) callback(img);
            }));
        });
    }

    /// Caches stay small: past 300 entries they start over, and anything still on screen is decoded again.
    public static void Remember<TKey>(Dictionary<TKey, BitmapSource?> cache, TKey key, BitmapSource? image)
    {
        if (cache.Count >= 300) cache.Clear();
        cache[key] = image;
    }

    public static void Prewarm(IEnumerable<Clip> clips)
    {
        foreach (var c in clips)
            if (c.ImagePath is { } p) Load(p, 64, _ => { });
    }
}

sealed class Thumb : Border
{
    public Thumb(string path, int px, bool fit = false)
    {
        Background = new SolidColorBrush(Color.FromArgb(15, 255, 255, 255));
        var image = new Image { Stretch = fit ? Stretch.Uniform : Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        Child = image;
        Thumbs.Load(path, px, b => { image.Source = b; if (b != null) Background = Brushes.Transparent; });
    }
}

// MARK: - Theme and helpers

/// Fonts, colors and motion copied from the Mac app's SwiftUI values.
static class UI
{
    public static readonly FontFamily Text = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly Color Panel = Color.FromRgb(0x21, 0x1F, 0x1C);   // sampled from Assets/press/panel.png
    public static readonly Brush Primary = White(0.85), Secondary = White(0.55), Tertiary = White(0.25);
    public static readonly Color Accent = AccentColor();
    public static readonly Brush AccentBrush = Freeze(new SolidColorBrush(Accent));
    public static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58), Orange = Color.FromRgb(0xFF, 0x9F, 0x0A);

    public static Brush White(double opacity) => Freeze(new SolidColorBrush(Color.FromArgb((byte)Math.Round(opacity * 255), 255, 255, 255)));
    static Brush Freeze(Brush b) { b.Freeze(); return b; }

    /// The Windows accent color, like the Mac's system accent.
    static Color AccentColor()
    {
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null) is int abgr)
            return Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));
        return Color.FromRgb(0x0A, 0x84, 0xFF);
    }

    public static string Glyph(int code) => ((char)code).ToString();
    public static readonly string
        Search = Glyph(0xE721), Clear = Glyph(0xEB90), Gear = Glyph(0xE713), Clipboard = Glyph(0xE77F),
        Link = Glyph(0xE71B), Camera = Glyph(0xE722), Download = Glyph(0xE896), Back = Glyph(0xE76B), Ok = Glyph(0xEC61),
        Warning = Glyph(0xE7BA), Off = Glyph(0xE738), Plus = Glyph(0xE710), Copy = Glyph(0xE8C8), Check = Glyph(0xE73E),
        Power = Glyph(0xE7E8), Keyboard = Glyph(0xE765), Network = Glyph(0xE968), Pause = Glyph(0xE769), Close = Glyph(0xE711),
        Phone = Glyph(0xE8EA), Hide = Glyph(0xED1A);

    /// Line height follows SF Pro (1.2x) rather than Segoe's taller default, so rows sit where they do on the Mac.
    public static TextBlock Label(string text, double size, Brush? color = null, FontWeight? weight = null) => new()
    {
        Text = text, FontFamily = Text, FontSize = size, Foreground = color ?? Primary, FontWeight = weight ?? FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, LineHeight = Math.Round(size * 1.2),
    };

    public static TextBlock Icon(string glyph, double size, Brush? color = null) =>
        new() { Text = glyph, FontFamily = Icons, FontSize = size, Foreground = color ?? Secondary, VerticalAlignment = VerticalAlignment.Center };

    /// WPF has no letter spacing, so tracked caps are laid out one letter at a time.
    public static FrameworkElement Tracked(string text, double size, double tracking, Brush color, FontWeight weight)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var ch in text) row.Children.Add(new TextBlock { Text = ch.ToString(), FontFamily = Text, FontSize = size, FontWeight = weight, Foreground = color, Margin = new Thickness(0, 0, tracking, 0) });
        return row;
    }

    public static Border Divider() => new() { Height = 1, Background = White(0.07) };

    /// `#RRGGBB` or `RRGGBB` to a color.
    public static Color? HexColor(string s)
    {
        var t = s.Trim().TrimStart('#');
        if (t.Length != 6 || !uint.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var v)) return null;
        return Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static bool IsUrl(string s)
    {
        var t = s.Trim();
        return (t.StartsWith("http://") || t.StartsWith("https://")) && !t.Any(char.IsWhiteSpace);
    }

    public static string Size(long bytes) =>
        bytes < 1000 ? $"{bytes} B" : bytes < 1_000_000 ? $"{bytes / 1000.0:0} KB" : bytes < 1_000_000_000 ? $"{bytes / 1e6:0.#} MB" : $"{bytes / 1e9:0.##} GB";

    public static string Ago(DateTime d)
    {
        var s = (int)(DateTime.Now - d).TotalSeconds;
        if (s < 60) return "now";
        if (s < 3600) return $"{s / 60}m";
        if (s < 86400) return $"{s / 3600}h";
        return $"{s / 86400}d";
    }

    /// `C:\Users\me\Documents` as `~\Documents`, like the Mac's abbreviatingWithTildeInPath.
    public static string Tilde(string path, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var inside = path.Length == home.Length || (path.Length > home.Length && path[home.Length] is '\\' or '/');   // C:\Users\Ann, not C:\Users\Anna
        return inside && path.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + path.Substring(home.Length) : path;
    }

    /// A shallow curve: `up` is a "^" (happy eye); otherwise a smile, optionally closed into an open mouth.
    public static System.Windows.Shapes.Path Arc(double w, double h, bool up, bool closed = false, Color? fill = null, Color? stroke = null, double thickness = 0)
    {
        var y0 = up ? h : 0;
        var ctrl = up ? -h : h * 2;
        var fig = new PathFigure { StartPoint = new Point(0, y0), IsClosed = closed, IsFilled = closed };
        fig.Segments.Add(new QuadraticBezierSegment(new Point(w / 2, ctrl), new Point(w, y0), true));
        return new System.Windows.Shapes.Path
        {
            Width = w, Height = h, Data = new PathGeometry(new[] { fig }),
            Fill = fill is { } f ? new SolidColorBrush(f) : null,
            Stroke = stroke is { } s ? new SolidColorBrush(s) : null,
            StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
    }

    public static readonly IEasingFunction Cubic = new CubicEase { EasingMode = EasingMode.EaseInOut };

    /// SwiftUI's `.spring(response:dampingFraction:)`, run for as long as the spring takes to settle.
    /// `from` restarts the motion from a fixed value; without it, it continues from wherever the value is now.
    public static void Spring(IAnimatable target, DependencyProperty prop, double to, double response, double damping, double? from = null)
    {
        var ease = new SpringEase(response, damping);
        target.BeginAnimation(prop, new DoubleAnimation(to, new Duration(TimeSpan.FromSeconds(ease.Settle))) { From = from, EasingFunction = ease });
    }

    public static void Fade(UIElement target, double to, double seconds, double? from = null) =>
        target.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(to, new Duration(TimeSpan.FromSeconds(seconds))) { From = from });

    /// SwiftUI keyframe tracks: each step animates to `value` over `seconds` with its own curve.
    public static DoubleAnimationUsingKeyFrames Keyframes(params (double Value, double Seconds, IEasingFunction Ease)[] steps)
    {
        var anim = new DoubleAnimationUsingKeyFrames();
        var t = 0.0;
        foreach (var (value, seconds, ease) in steps)
        {
            t += seconds;
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t)), ease));
        }
        return anim;
    }

    /// A dark menu like macOS's: rounded, accent highlight on the hovered item.
    public static ContextMenu Menu()
    {
        var menu = new ContextMenu { HasDropShadow = true, Template = MenuLook.Value.Frame };
        menu.Resources[typeof(MenuItem)] = MenuLook.Value.Item;
        menu.Resources[typeof(Separator)] = MenuLook.Value.Separator;
        return menu;
    }

    /// Built once and shared by every row's menu.
    static readonly Lazy<(ControlTemplate Frame, Style Item, Style Separator)> MenuLook = new(() =>
    {
        var frame = new FrameworkElementFactory(typeof(Border));
        frame.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x2B, 0x2A, 0x28)));
        frame.SetValue(Border.BorderBrushProperty, White(0.12));
        frame.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        frame.SetValue(Border.PaddingProperty, new Thickness(5));
        var items = new FrameworkElementFactory(typeof(ItemsPresenter));
        frame.AppendChild(items);
        var frameTemplate = new ControlTemplate(typeof(ContextMenu)) { VisualTree = frame };

        var row = new FrameworkElementFactory(typeof(Border), "row");
        row.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        row.SetValue(Border.PaddingProperty, new Thickness(10, 3, 24, 4));
        row.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var header = new FrameworkElementFactory(typeof(ContentPresenter));
        header.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        row.AppendChild(header);
        var template = new ControlTemplate(typeof(MenuItem)) { VisualTree = row };
        template.Triggers.Add(new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true, Setters = { new Setter(Border.BackgroundProperty, AccentBrush, "row") } });
        var item = new Style(typeof(MenuItem));
        item.Setters.Add(new Setter(Control.TemplateProperty, template));
        item.Setters.Add(new Setter(TextElement.FontFamilyProperty, Text));
        item.Setters.Add(new Setter(TextElement.FontSizeProperty, 13.0));
        item.Setters.Add(new Setter(TextElement.ForegroundProperty, Primary));

        var line = new FrameworkElementFactory(typeof(Border));
        line.SetValue(FrameworkElement.HeightProperty, 1.0);
        line.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 5, 10, 5));
        line.SetValue(Border.BackgroundProperty, White(0.12));
        var separator = new Style(typeof(Separator));
        separator.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(Separator)) { VisualTree = line }));
        frameTemplate.Seal();
        item.Seal();
        separator.Seal();
        return (frameTemplate, item, separator);
    });
}

/// A damped spring with SwiftUI's parameters: `response` is the period, `damping` the damping fraction.
sealed class SpringEase : EasingFunctionBase
{
    readonly double omega, zeta;
    public readonly double Settle;

    public SpringEase(double response, double damping)
    {
        omega = 2 * Math.PI / response;
        zeta = damping;
        Settle = Math.Min(2, -Math.Log(0.001) / (zeta * omega));
        EasingMode = EasingMode.EaseIn;
    }

    protected override double EaseInCore(double normalized)
    {
        var t = normalized * Settle;
        if (zeta >= 1) return 1 - (1 + omega * t) * Math.Exp(-omega * t);
        var wd = omega * Math.Sqrt(1 - zeta * zeta);
        return 1 - Math.Exp(-zeta * omega * t) * (Math.Cos(wd * t) + zeta * omega / wd * Math.Sin(wd * t));
    }

    protected override Freezable CreateInstanceCore() => new SpringEase(2 * Math.PI / omega, zeta);
}

/// Icons for files and apps from the Windows shell, with transparency intact.
static unsafe class Shell
{
    // UI thread only. The shell takes up to ~150 ms for a file type's first icon (a PDF's), so icons are drawn on a
    // background thread, in the single-threaded apartment shell objects expect.
    static readonly Dictionary<string, BitmapSource?> Cache = new();
    static readonly Dictionary<string, List<Action<BitmapSource?>>> Pending = new();
    static readonly BlockingCollection<Action> Work = new();
    static readonly List<Worker> Workers = new();
    /// While icons are waiting: every 2 s, if every icon thread is stuck, another one starts (the first one too).
    static readonly DispatcherTimer Unstick = Watchdog();

    static DispatcherTimer Watchdog()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            if (Pending.Count == 0) { timer.Stop(); return; }
            if (Workers.All(w => w.Stuck)) Workers.Add(new Worker());
        };
        return timer;
    }
    static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    /// One icon thread, and when a third-party icon handler hangs it, another, so the icons queued behind still come.
    sealed class Worker
    {
        long busySince;   // Stopwatch ticks while drawing, 0 while waiting
        public bool Stuck => Volatile.Read(ref busySince) is var t && t != 0 && Stopwatch.GetTimestamp() - t > 2 * Stopwatch.Frequency;

        public Worker()
        {
            var thread = new Thread(() =>
            {
                foreach (var work in Work.GetConsumingEnumerable())
                {
                    Volatile.Write(ref busySince, Stopwatch.GetTimestamp());
                    work();
                    Volatile.Write(ref busySince, 0);
                }
            }) { IsBackground = true, Name = "Shell icons" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
    }

    /// Calls `done` right away for an icon already drawn, otherwise on the UI thread once the shell has drawn it.
    public static void Icon(string path, int px, Action<BitmapSource?> done)
    {
        var key = $"{px}|{path}";
        if (Cache.TryGetValue(key, out var hit)) { done(hit); return; }
        if (Pending.TryGetValue(key, out var waiting)) { waiting.Add(done); return; }
        Pending[key] = new List<Action<BitmapSource?>> { done };
        if (Workers.Count == 0) Workers.Add(new Worker());
        Unstick.Start();
        var ui = Dispatcher.CurrentDispatcher;
        Work.Add(() =>
        {
            BitmapSource? img = null;
            try { img = Draw(path, px); }
            catch (Exception e) { Log.Error(e); }   // the callbacks still run, so a row never waits forever
            ui.BeginInvoke(new Action(() =>
            {
                Thumbs.Remember(Cache, key, img);
                var callbacks = Pending[key];
                Pending.Remove(key);
                foreach (var callback in callbacks) callback(img);
            }));
        });
    }

    /// An image that fills in once its icon is drawn, and disappears if there's none.
    public static Image IconImage(string path, int px)
    {
        var image = new Image();
        Icon(path, px, img => { image.Source = img; if (img == null) image.Visibility = Visibility.Collapsed; });
        return image;
    }

    static BitmapSource? Draw(string path, int px)
    {
        BitmapSource? result = null;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, IID_IShellItemImageFactory, out var factory) >= 0)
        {
            IntPtr hbmp;
            // IShellItemImageFactory::GetImage, slot 3. SIIGBF_ICONONLY so files show their type icon like Finder's.
            if (((delegate* unmanaged[Stdcall]<IntPtr, Win.SIZE, int, IntPtr*, int>)(*(IntPtr**)factory)[3])(factory, new Win.SIZE { cx = px, cy = px }, 0x4, &hbmp) >= 0)
            {
                result = FromHBitmap(hbmp);
                DeleteObject(hbmp);
            }
            Marshal.Release(factory);
        }
        return result;
    }

    /// Reads the 32-bit premultiplied pixels directly; WPF's own HBITMAP conversion drops the alpha.
    static BitmapSource? FromHBitmap(IntPtr hbmp)
    {
        var bm = new BITMAP();
        if (GetObject(hbmp, Marshal.SizeOf<BITMAP>(), ref bm) == 0 || bm.bmBitsPixel != 32 || bm.bmBits == IntPtr.Zero) return null;
        var stride = bm.bmWidthBytes;
        var pixels = new byte[stride * bm.bmHeight];
        Marshal.Copy(bm.bmBits, pixels, 0, pixels.Length);
        var flipped = new byte[pixels.Length];
        for (var y = 0; y < bm.bmHeight; y++) Buffer.BlockCopy(pixels, y * stride, flipped, (bm.bmHeight - 1 - y) * stride, stride);
        var img = BitmapSource.Create(bm.bmWidth, bm.bmHeight, 96, 96, PixelFormats.Pbgra32, null, flipped, stride);
        img.Freeze();
        return img;
    }

    [StructLayout(LayoutKind.Sequential)] struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHCreateItemFromParsingName(string path, IntPtr bc, in Guid riid, out IntPtr item);
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, ref BITMAP bm);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    /// The Windows folder picker (NSOpenPanel with canChooseDirectories). Returns null if cancelled.
    public static string? PickFolder(IntPtr owner, string start, string title)
    {
        if (Glyphs.CoCreateInstance(new Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7"), IntPtr.Zero, 1, new Guid("d57c7288-d4ad-4768-be02-9d969532d960"), out var dlg) < 0) return null;
        try
        {
            var vt = *(IntPtr**)dlg;
            uint options;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)vt[10])(dlg, &options);   // GetOptions
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)vt[9])(dlg, options | 0x20 | 0x40);   // SetOptions: + FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM
            fixed (char* t = title) ((delegate* unmanaged[Stdcall]<IntPtr, char*, int>)vt[17])(dlg, t);   // SetTitle
            if (Directory.Exists(start) && SHCreateItemFromParsingName(start, IntPtr.Zero, new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), out var folder) >= 0)
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vt[12])(dlg, folder);   // SetFolder
                Marshal.Release(folder);
            }
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vt[3])(dlg, owner) < 0) return null;   // Show
            IntPtr item;
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)vt[20])(dlg, &item) < 0) return null;   // GetResult
            try
            {
                IntPtr name;
                if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(IntPtr**)item)[5])(item, 0x80058000, &name) < 0) return null;   // GetDisplayName(FILESYSPATH)
                try { return Marshal.PtrToStringUni(name); }
                finally { Marshal.FreeCoTaskMem(name); }
            }
            finally { Marshal.Release(item); }
        }
        finally { Marshal.Release(dlg); }
    }

}

// MARK: - Windows

/// The Win32 calls the bubble, panel and peek need for focus, position and pasting.
static class Win
{
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
    /// The union is its own struct so the runtime aligns it for 32- and 64-bit alike.
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public int type; public InputUnion u; }

    public static void NoActivate(Window w, bool noActivate)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        var ex = GetWindowLong(h, -20) | 0x80;   // WS_EX_TOOLWINDOW: no taskbar button, not in Alt+Tab
        if (noActivate) ex |= 0x08000000;         // WS_EX_NOACTIVATE
        SetWindowLong(h, -20, ex);
    }

    public static IntPtr Handle(Window w) => new WindowInteropHelper(w).Handle;

    public static RECT Frame(Window w) { GetWindowRect(Handle(w), out var r); return r; }

    public static void Move(Window w, int x, int y) => SetWindowPos(Handle(w), IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);   // NOSIZE NOZORDER NOACTIVATE

    public static double Scale(Window w) => GetDpiForWindow(Handle(w)) / 96.0;

    /// The work area (screen minus taskbar) of the monitor containing `p`, in physical pixels.
    public static RECT WorkArea(POINT p)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromPoint(p, 2), ref info);
        return info.rcWork;
    }

    public static POINT Cursor() { GetCursorPos(out var p); return p; }

    public static bool ModifiersDown() =>
        new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(vk => (GetAsyncKeyState(vk) & 0x8000) != 0);

    /// Presses Ctrl+V. False when Windows refused the keystrokes (another app's input block, the lock screen).
    public static bool SendPaste()
    {
        INPUT Key(ushort vk, bool up) => new() { type = 1, u = new InputUnion { ki = new KEYBDINPUT { vk = vk, flags = up ? 2u : 0u } } };
        var inputs = new[] { Key(0x11, false), Key(0x56, false), Key(0x56, true), Key(0x11, true) };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }

    /// Waits up to `timeout` seconds for `ready` (the app is back in front and the keys used to pick are released),
    /// then sends the paste, unless something else replaced the clipboard meanwhile. Null once sent, otherwise why not.
    public static async Task<string?> PasteWhenReady(Func<bool> ready, Func<bool> stillOurs, Func<bool> send, double timeout = 1.5)
    {
        const string ctrlV = "It's on the clipboard, so press Ctrl+V to paste it.";
        const string replaced = "Another app changed the clipboard first. Pick it again to paste it.";
        var started = DateTime.UtcNow;
        while (!ready())
        {
            if ((DateTime.UtcNow - started).TotalSeconds >= timeout) return stillOurs() ? "The app didn't come back in time. " + ctrlV : replaced;
            await Task.Delay(10);
        }
        if (!stillOurs()) return replaced;
        return send() ? null : "Windows blocked the keystrokes. " + ctrlV;
    }

    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();

    /// Windows silently drops keystrokes sent to an app at a higher integrity level (one running as administrator),
    /// so those just get the copy.
    public static bool CanPasteInto(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        var process = OpenProcess(0x1000, false, (int)pid);   // PROCESS_QUERY_LIMITED_INFORMATION
        if (process == IntPtr.Zero) return false;
        try { return IntegrityLevel(process) is { } theirs && IntegrityLevel(GetCurrentProcess()) is { } ours && theirs <= ours; }
        finally { CloseHandle(process); }
    }

    /// The process token's mandatory label: 0x2000 for normal apps, 0x3000 for elevated ones.
    static int? IntegrityLevel(IntPtr process)
    {
        if (!OpenProcessToken(process, 0x0008, out var token)) return null;   // TOKEN_QUERY
        try
        {
            GetTokenInformation(token, 25, IntPtr.Zero, 0, out var size);   // TokenIntegrityLevel
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, 25, buffer, size, out _)) return null;
                var sid = Marshal.ReadIntPtr(buffer);
                return Marshal.ReadInt32(GetSidSubAuthority(sid, Marshal.ReadByte(GetSidSubAuthorityCount(sid)) - 1));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(token); }
    }

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO info);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("advapi32.dll")] static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);
    [DllImport("advapi32.dll")] static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr buffer, int size, out int needed);
    [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthority(IntPtr sid, int index);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
}

/// A borderless always-on-top window. Bubble and peek never take focus; the panel does, because it has a search field.
sealed class FloatingWindow : Window
{
    public FloatingWindow(double width, double height, bool keyable)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = keyable;
        Width = width;
        Height = height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000;
        Top = -10000;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        SourceInitialized += (_, _) => Win.NoActivate(this, !keyable);
    }
}

/// The frosted surface the Mac draws with NSVisualEffectView: here the panel's sampled color, rounded,
/// with the hairline border drawn over the content like SwiftUI's strokeBorder overlay.
static class Glass
{
    /// Room around the panel and peek for the drop shadow every macOS window has.
    public const double Shadow = 24;

    public static FrameworkElement Wrap(FrameworkElement content, double radius, bool shadow = true)
    {
        var grid = new Grid { Margin = new Thickness(shadow ? Shadow : 0) };
        if (shadow) grid.Children.Add(ShadowOf(new Border { CornerRadius = new CornerRadius(radius), Background = Brushes.Black }, 28, 8, 0.45));
        grid.Children.Add(new Border { CornerRadius = new CornerRadius(radius), Background = new SolidColorBrush(UI.Panel) });
        grid.Children.Add(content);
        grid.Children.Add(new Border { CornerRadius = new CornerRadius(radius), BorderBrush = UI.White(0.1), BorderThickness = new Thickness(1), IsHitTestVisible = false });
        return grid;
    }

    /// Cached as a bitmap, so the shadow isn't redrawn when the content above it changes.
    public static T ShadowOf<T>(T element, double blur, double depth, double opacity) where T : UIElement
    {
        element.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = blur, ShadowDepth = depth, Direction = 270, Opacity = opacity, Color = Colors.Black };
        element.CacheMode = new BitmapCache();
        element.IsHitTestVisible = false;
        return element;
    }
}

/// The Mac's NSAlert in dark mode: Windows' own message box is always light, so this draws one in the panel's style.
static class Alert
{
    /// Returns true when the user picks `action` (Enter); false for Cancel or Esc. Centered on `owner` while it's on
    /// screen, otherwise on the screen (a hidden window, like the panel when an item is picked from the peek, can't own it).
    public static bool Show(Window owner, string title, string message, string action, string? cancel = "Cancel")
    {
        var result = false;
        var dialog = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
            Background = Brushes.Transparent, ShowInTaskbar = false, Topmost = true, SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
        };
        if (owner.IsVisible) dialog.Owner = owner;
        var body = new StackPanel { Width = 280, Margin = new Thickness(18, 16, 18, 14) };
        var heading = UI.Label(title, 13, UI.Primary, FontWeights.SemiBold);
        heading.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(heading);
        var text = UI.Label(message, 11.5, UI.Secondary);
        text.TextWrapping = TextWrapping.Wrap;
        text.Margin = new Thickness(0, 6, 0, 14);
        body.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Border Button(string label, bool primary)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(5), Padding = new Thickness(12, 3, 12, 4), Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Arrow,
                Background = primary ? UI.AccentBrush : UI.White(0.14), Child = UI.Label(label, 12, primary ? Brushes.White : UI.Primary, FontWeights.Medium),
            };
            b.MouseLeftButtonUp += (_, _) => { result = primary; dialog.Close(); };
            return b;
        }
        if (cancel != null) buttons.Children.Add(Button(cancel, false));
        buttons.Children.Add(Button(action, true));
        body.Children.Add(buttons);
        dialog.Content = Glass.Wrap(body, 12);
        dialog.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { result = true; dialog.Close(); }
            else if (e.Key == Key.Escape) dialog.Close();
        };
        dialog.ShowDialog();
        return result;
    }
}

// MARK: - Bubble face

/// The 64 x 64 bubble: glass circle, mascot, unseen badge and the ring that flashes on capture.
sealed class BubbleView : Grid
{
    readonly Model model;
    readonly Ellipse ring;
    readonly ScaleTransform ringScale = new(1, 1);
    readonly ScaleTransform hoverScale = new(1, 1);
    readonly Grid face;
    readonly Border badge;
    readonly TextBlock badgeText;
    MascotView mascot;
    public readonly Ellipse HitArea;
    /// What a click does; screen readers press the bubble through this too.
    public Action Pressed = () => { };

    /// One button for screen readers like Narrator: "ClipBubble", which opens the history.
    protected override AutomationPeer OnCreateAutomationPeer() => new BubblePeer(this);

    sealed class BubblePeer(BubbleView bubble) : FrameworkElementAutomationPeer(bubble), IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        protected override string GetClassNameCore() => nameof(BubbleView);
        protected override string GetNameCore() => "ClipBubble";
        protected override string GetHelpTextCore() => "Opens your clipboard history";
        protected override List<AutomationPeer>? GetChildrenCore() => null;
        public override object? GetPattern(PatternInterface pattern) => pattern == PatternInterface.Invoke ? this : base.GetPattern(pattern);
        public void Invoke() => bubble.Pressed();
    }

    public BubbleView(Model model)
    {
        this.model = model;
        Width = Height = 64;
        face = new Grid { Width = 46, Height = 46, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = hoverScale };
        face.Children.Add(Glass.ShadowOf(new Ellipse { Fill = Brushes.Black }, 10, 2, 0.35));
        face.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromArgb(0xE6, UI.Panel.R, UI.Panel.G, UI.Panel.B)) });
        ring = new Ellipse { Stroke = UI.AccentBrush, StrokeThickness = 2, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = ringScale };
        face.Children.Add(ring);
        face.Children.Add(new Ellipse { Fill = new LinearGradientBrush(Color.FromArgb(41, 255, 255, 255), Colors.Transparent, new Point(0.5, 0), new Point(0.5, 0.5)) });
        face.Children.Add(new Ellipse { StrokeThickness = 1, Stroke = new LinearGradientBrush(Color.FromArgb(115, 255, 255, 255), Color.FromArgb(20, 255, 255, 255), 90) });
        mascot = new MascotView(model.Mascot);
        face.Children.Add(mascot);
        Children.Add(face);

        badgeText = new TextBlock { FontFamily = UI.Text, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        badge = new Border
        {
            MinWidth = 18, Height = 18, Padding = new Thickness(5, 0, 5, 0), CornerRadius = new CornerRadius(9),
            Background = UI.AccentBrush, BorderBrush = UI.White(0.35), BorderThickness = new Thickness(1), Child = badgeText,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 9 - 5, 9 - 5, 0), Visibility = Visibility.Collapsed,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1),
        };
        Children.Add(badge);
        HitArea = new Ellipse { Width = 46, Height = 46, Fill = Brushes.Transparent };
        Children.Add(HitArea);
        model.Changed += what =>
        {
            if (what == Change.Unseen) UpdateBadge();
            if (what == Change.Mascot) ReplaceMascot();
        };
    }

    void ReplaceMascot()
    {
        face.Children.Remove(mascot);
        mascot = new MascotView(model.Mascot);
        face.Children.Add(mascot);
    }

    public void SetMood(bool hover, bool pressed, bool happy)
    {
        mascot.Update(hover, pressed, happy);
        UI.Spring(hoverScale, ScaleTransform.ScaleXProperty, hover ? 1.06 : 1, 0.2, 0.7);
        UI.Spring(hoverScale, ScaleTransform.ScaleYProperty, hover ? 1.06 : 1, 0.2, 0.7);
    }

    public void Wiggle() => mascot.Wiggle();

    /// The mascot hops and the accent ring ripples out.
    public void Pulse()
    {
        mascot.Hop();
        var grow = new DoubleAnimation(1, 1.55, TimeSpan.FromSeconds(0.7)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        ringScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        ringScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        ring.BeginAnimation(OpacityProperty, new DoubleAnimation(0.8, 0, TimeSpan.FromSeconds(0.7)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    void UpdateBadge()
    {
        var show = model.Unseen > 0;
        badgeText.Text = Math.Min(model.Unseen, 99).ToString();
        var scale = (ScaleTransform)badge.RenderTransform;
        if (show && badge.Visibility != Visibility.Visible)
        {
            badge.Visibility = Visibility.Visible;
            UI.Spring(scale, ScaleTransform.ScaleXProperty, 1, 0.25, 0.7, from: 0);
            UI.Spring(scale, ScaleTransform.ScaleYProperty, 1, 0.25, 0.7, from: 0);
            UI.Fade(badge, 1, 0.2, from: 0);
        }
        else if (!show) badge.Visibility = Visibility.Collapsed;
    }
}

// MARK: - Rows

sealed record RowItem(Clip Clip, int Index, bool Last, Model Model);

/// What the lazy list creates for each visible item. The list's 6 pt padding lives on the first and last rows,
/// so it scrolls with them as SwiftUI's does.
sealed class RowHost : Decorator
{
    public RowHost() => DataContextChanged += (_, _) =>
    {
        if (DataContext is not RowItem r) { Child = null; return; }
        Margin = new Thickness(6, r.Index == 0 ? 6 : 0, 6, r.Last ? 6 : 1);
        Child = new Row(r.Clip, r.Index, r.Clip.Id == r.Model.Selection, r.Model);
    };
}

/// One history item: tile, title, source and age, and the Ctrl+N hint for the first nine.
sealed class Row : Border
{
    readonly Clip clip;
    readonly Model model;
    bool selected;
    TextBlock? hint;
    readonly TextBlock subtitle;
    readonly StackPanel side = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    Point? down;

    public Row(Clip clip, int index, bool isSelected, Model model)
    {
        this.clip = clip;
        this.model = model;
        Height = 46;
        Padding = new Thickness(8, 0, 8, 0);
        CornerRadius = new CornerRadius(9);
        Cursor = Cursors.Arrow;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tile = new Tile(clip) { Margin = new Thickness(0, 0, 10, 0) };
        grid.Children.Add(tile);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = UI.Label(Title(), 13, UI.Primary, FontWeights.Medium);
        texts.Children.Add(title);
        var meta = new DockPanel { Margin = new Thickness(0, 2, 0, 0), LastChildFill = true };
        if (SourceIcon() is { } icon) { icon.Margin = new Thickness(0, 0, 4, 0); meta.Children.Add(icon); }
        subtitle = UI.Label(Subtitle(), 11, UI.Secondary);
        meta.Children.Add(subtitle);
        texts.Children.Add(meta);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var trailing = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        trailing.Children.Add(side);
        if (index is >= 0 and < 9)
        {
            hint = UI.Label($"Ctrl+{index + 1}", 11, UI.Tertiary, FontWeights.Medium);
            hint.Margin = new Thickness(6, 0, 0, 0);
            hint.VerticalAlignment = VerticalAlignment.Center;
            trailing.Children.Add(hint);
        }
        Grid.SetColumn(trailing, 2);
        grid.Children.Add(trailing);
        Child = grid;
        Selected = isSelected;

        // An image's size is known once its thumbnail has decoded.
        if (clip.Kind == ClipKind.Image && clip.ImagePath is { } p && Thumbs.SizeOf(p) == null)
            Thumbs.Load(p, 64, _ => title.Text = Title());

        MouseEnter += (_, _) => { if (!selected) Background = UI.White(0.06); };
        MouseLeave += (_, _) => { if (!selected) Background = Brushes.Transparent; down = null; };
        MouseLeftButtonDown += (_, e) => down = e.GetPosition(this);
        MouseMove += (_, e) =>
        {
            if (down is not { } d || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - d.X) < 4 && Math.Abs(p.Y - d.Y) < 4) return;
            down = null;
            DragDrop.DoDragDrop(this, DragData(), DragDropEffects.Copy);
        };
        MouseLeftButtonUp += (_, _) =>
        {
            if (down == null) return;
            down = null;
            model.Pick(clip, !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
        };
        // Built on each right-click: what it offers depends on transfers and links at that moment.
        ContextMenu = UI.Menu();
        ContextMenuOpening += (_, _) => { ContextMenu.Items.Clear(); Fill(ContextMenu); };
        UpdateTransfer();

        // Panel rows follow the keyboard selection and transfers; peek rows (index -1) are never selected.
        if (index < 0) return;
        Action<Change> follow = what =>
        {
            if (what == Change.Selection) Selected = clip.Id == model.Selection;
            else if (what == Change.Transfers) UpdateTransfer();
        };
        Loaded += (_, _) => model.Changed += follow;
        Unloaded += (_, _) => model.Changed -= follow;
    }

    bool Selected
    {
        set
        {
            selected = value;
            Background = selected ? UI.White(0.12) : IsMouseOver ? UI.White(0.06) : Brushes.Transparent;
            if (hint != null) hint.Foreground = selected ? UI.Secondary : UI.Tertiary;
        }
    }

    /// A synced item still on another machine gets a Get button; one moving shows its progress and a stop button.
    void UpdateTransfer()
    {
        side.Children.Clear();
        subtitle.Text = Subtitle();
        if (model.Transfers.TryGetValue(clip.Hash, out var t))
        {
            side.Children.Add(new ProgressBar { Width = 44, Height = 4, Value = t.Fraction, Maximum = 1, VerticalAlignment = VerticalAlignment.Center, Foreground = UI.AccentBrush, Background = UI.White(0.1), BorderThickness = new Thickness(0) });
            var stop = UI.Icon(UI.Close, 10, UI.Secondary);
            stop.Margin = new Thickness(6, 0, 0, 0);
            stop.Cursor = Cursors.Hand;
            stop.ToolTip = "Stop";
            stop.MouseLeftButtonUp += (_, e) => { e.Handled = true; model.Sync?.CancelTransfer(clip.Hash); };
            side.Children.Add(stop);
        }
        else if (clip.Remote)
        {
            var get = new Border { CornerRadius = new CornerRadius(5), Background = UI.White(0.1), Padding = new Thickness(8, 2, 8, 2), Cursor = Cursors.Hand, ToolTip = "Download it from the other machine" };
            get.Child = UI.Label("Get", 11, UI.Primary, FontWeights.Medium);
            get.MouseLeftButtonUp += (_, e) => { e.Handled = true; model.Sync?.Get(clip.Hash); };
            side.Children.Add(get);
        }
    }

    string DeviceName => model.Devices.TryGetValue(clip.Origin, out var n) ? n : "other machine";

    void Fill(ContextMenu menu)
    {
        void Add(string title, Action act) { var item = new MenuItem { Header = title }; item.Click += (_, _) => act(); menu.Items.Add(item); }
        var moving = model.Transfers.ContainsKey(clip.Hash);
        if (clip.Remote)
        {
            if (!moving) Add("Get from " + DeviceName, () => model.Sync?.Get(clip.Hash));
        }
        else
        {
            Add("Paste", () => model.Pick(clip, true));
            Add("Copy", () => model.Pick(clip, false));
            if (clip.Kind != ClipKind.Text && !moving && model.Sync != null)
                foreach (var peer in model.Sync.Linked)
                    Add("Send to " + peer.Name, () => model.Sync.SendTo(clip.Hash, peer.Device));
        }
        if (moving) Add("Stop transfer", () => model.Sync?.CancelTransfer(clip.Hash));
        if (!clip.Remote && (clip.ImagePath ?? clip.Paths.FirstOrDefault()) is { } p)
        {
            Add("Open", () => { try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); } catch (Win32Exception) { } });
            Add("Show in File Explorer", () => Process.Start("explorer.exe", $"/select,\"{p}\""));
        }
        menu.Items.Add(new Separator());
        Add("Delete", () => model.Delete(clip));
    }

    FrameworkElement? SourceIcon() => clip.Source switch
    {
        "Screenshot" => UI.Icon(UI.Camera, 9.5),
        "Downloads" => UI.Icon(UI.Download, 9.5),
        _ => Apps.ExePath(clip.Source) is { } exe ? Sized(Shell.IconImage(exe, 32), 12) : null,
    };

    static Image Sized(Image image, double size)
    {
        image.Width = image.Height = size;
        image.VerticalAlignment = VerticalAlignment.Center;
        return image;
    }

    /// Only the start of the text matters for a row, so long copies don't slow the list.
    string Head => Store.Head(clip.Text, 20_000);

    string FirstLine => Head.Split('\n', '\r').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "";

    string Title()
    {
        switch (clip.Kind)
        {
            case ClipKind.Image:
                return clip.ImagePath is { } p && Thumbs.SizeOf(p) is { } s ? $"Image  {s.Width}×{s.Height}" : "Image";
            case ClipKind.File:
                return string.Join(", ", clip.Paths.Select(Path.GetFileName));
            default:
                var first = FirstLine;
                var t = first.Length > 200 ? first.Substring(0, 200) : first;
                return UI.IsUrl(t) ? t.Replace("https://", "").Replace("http://", "") : t;
        }
    }

    string Subtitle()
    {
        if (model.Transfers.TryGetValue(clip.Hash, out var t))
            return $"{(t.Sending ? "Sending to" : "Getting from")} {t.Peer} · {UI.Size(t.Done)} of {UI.Size(t.Total)}";
        var parts = new List<string> { clip.Source, UI.Ago(clip.Created) };
        if (clip.Origin.Length > 0) parts.Insert(0, DeviceName);
        if (clip.Remote) parts.Add("not downloaded yet");
        else if (clip.Kind == ClipKind.File && clip.Paths.FirstOrDefault() is { } p) parts.Add(UI.Tilde(Path.GetDirectoryName(p) ?? ""));
        else if (clip.Kind == ClipKind.Text)
        {
            var head = Head;
            var lines = head.Split('\n').Count(l => l.Trim('\r').Length > 0);
            if (lines > 1) parts.Add(clip.Text.Length > head.Length ? $"{lines}+ lines" : $"{lines} lines");
        }
        return string.Join(" · ", parts);
    }

    DataObject DragData()
    {
        if (!clip.Remote && (clip.ImagePath ?? clip.Paths.FirstOrDefault()) is { } p && File.Exists(p))
        {
            var data = new DataObject();
            data.SetFileDropList(new System.Collections.Specialized.StringCollection { p });
            return data;
        }
        return new DataObject(DataFormats.UnicodeText, clip.Text);
    }
}

sealed class Tile : Border
{
    public Tile(Clip clip)
    {
        Width = Height = 30;
        CornerRadius = new CornerRadius(7);
        ClipToBounds = true;
        BorderBrush = UI.White(0.08);
        BorderThickness = new Thickness(1);
        if (clip.ImagePath is { } img)
        {
            Child = new Thumb(img, 64);
            Clip = new RectangleGeometry(new Rect(0, 0, 30, 30), 7, 7);
        }
        else if (clip.Paths.FirstOrDefault() is { } p)
        {
            BorderThickness = new Thickness(0);
            var icon = Shell.IconImage(p, 64);
            icon.Margin = new Thickness(2);
            Child = icon;
        }
        else if (UI.HexColor(clip.Text) is { } c) Background = new SolidColorBrush(c);
        else if (UI.IsUrl(clip.Text)) Symbol(UI.Link, Color.FromRgb(0x0A, 0x84, 0xFF), angle: -45);   // SF Symbols' link is diagonal
        else
        {
            // SF Symbols' text.alignleft: four rounded lines, heavier than the Fluent glyph.
            Background = new SolidColorBrush(Color.FromArgb(31, 255, 255, 255));
            var lines = new Canvas { Width = 12, Height = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            foreach (var (y, w) in new[] { (0.75, 12.0), (3.6, 8.0), (6.4, 12.0), (9.25, 7.0) })
                lines.Children.Add(new Line { X1 = 0.75, X2 = w - 0.75, Y1 = y, Y2 = y, Stroke = UI.White(0.85), StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            Child = lines;
        }
    }

    void Symbol(string glyph, Color tint, double angle = 0)
    {
        Background = new SolidColorBrush(Color.FromArgb(31, tint.R, tint.G, tint.B));
        Child = new TextBlock
        {
            Text = glyph, FontFamily = UI.Icons, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(217, tint.R, tint.G, tint.B)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(angle),
        };
    }
}

// MARK: - Panel

sealed class PanelView : Grid
{
    readonly Model model;
    readonly TextBox search;
    readonly TextBlock placeholder;
    readonly Border clear;
    readonly StackPanel filters = new() { Orientation = Orientation.Horizontal };
    readonly Grid listArea = new();
    readonly ListBox list;
    readonly Grid main = new();
    readonly TranslateTransform lift = new();
    SettingsView? settings;

    public PanelView(Model model, Func<SettingsView> makeSettings)
    {
        this.model = model;
        Width = 420;
        Height = 470;
        RenderTransform = lift;
        foreach (var h in new[] { 50, 0, 1, -1, 1, 36 })
            main.RowDefinitions.Add(new RowDefinition { Height = h < 0 ? new GridLength(1, GridUnitType.Star) : h == 0 ? GridLength.Auto : new GridLength(h) });

        var header = new Grid { Margin = new Thickness(16, 0, 16, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(UI.Icon(UI.Search, 16.5, UI.Secondary));
        search = new TextBox
        {
            FontFamily = UI.Text, FontSize = 16, Foreground = UI.Primary, CaretBrush = UI.Primary, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            SelectionBrush = UI.AccentBrush, FocusVisualStyle = null,
        };
        placeholder = UI.Label("Search clipboard…", 16, UI.White(0.8));
        placeholder.Margin = new Thickness(10, 0, 0, 0);
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        placeholder.IsHitTestVisible = false;
        Grid.SetColumn(placeholder, 1);
        Grid.SetColumn(search, 1);
        header.Children.Add(placeholder);
        header.Children.Add(search);
        clear = new Border { Child = UI.Icon(UI.Clear, 13, UI.Tertiary), Background = Brushes.Transparent, Visibility = Visibility.Collapsed, Cursor = Cursors.Arrow };
        clear.MouseLeftButtonUp += (_, _) => model.Query = "";
        Grid.SetColumn(clear, 2);
        header.Children.Add(clear);
        search.TextChanged += (_, _) => { model.Query = search.Text; placeholder.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; };
        Add(header, 0);

        var filterRow = new Grid { Margin = new Thickness(12, 0, 12, 8) };
        filterRow.Children.Add(filters);
        var tab = UI.Label("tab", 10, UI.Tertiary, FontWeights.Medium);
        tab.HorizontalAlignment = HorizontalAlignment.Right;
        tab.VerticalAlignment = VerticalAlignment.Center;
        filterRow.Children.Add(tab);
        Add(filterRow, 1);
        Add(UI.Divider(), 2);

        list = LazyList();
        listArea.Children.Add(list);
        Add(listArea, 3);
        Add(UI.Divider(), 4);
        Add(Footer(), 5);
        Children.Add(main);

        model.Changed += what =>
        {
            switch (what)
            {
                case Change.Clips: RenderRows(); break;
                case Change.Selection: Select(); break;
                case Change.Filter: backToTop = true; RenderFilters(); break;
                case Change.Query:
                    backToTop = true;
                    if (search.Text != model.Query) search.Text = model.Query;
                    clear.Visibility = model.Query.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case Change.Previewing: ShowPreview(); break;
                case Change.ShowSettings:
                    if (model.ShowSettings) { settings = makeSettings(); Children.Add(settings); main.Visibility = Visibility.Hidden; }
                    else { if (settings != null) Children.Remove(settings); settings = null; main.Visibility = Visibility.Visible; search.Focus(); }
                    break;
                case Change.Shown: Opened(); break;
            }
        };
        RenderFilters();
        RenderRows();
    }

    void Add(UIElement e, int row) { Grid.SetRow(e, row); main.Children.Add(e); }

    void Opened()
    {
        search.Focus();
        Keyboard.Focus(search);
        lift.Y = 6;
        UI.Spring(lift, TranslateTransform.YProperty, 0, 0.2, 0.9);
        if (list.Items.Count > 0) list.ScrollIntoView(list.Items[0]);
    }

    /// Only the rows on screen exist, like SwiftUI's LazyVStack: the list stays fast with a full week of history.
    ListBox LazyList()
    {
        var box = new ListBox { Focusable = false, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        // Just a scroller and the rows: ListBox's own template adds a border and padding the Mac list doesn't have.
        var scroller = new FrameworkElementFactory(typeof(ScrollViewer));
        scroller.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        scroller.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        scroller.SetValue(ScrollViewer.CanContentScrollProperty, true);
        scroller.SetValue(FocusableProperty, false);
        scroller.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        box.Template = new ControlTemplate(typeof(ListBox)) { VisualTree = scroller };
        VirtualizingPanel.SetIsVirtualizing(box, true);
        VirtualizingPanel.SetScrollUnit(box, ScrollUnit.Pixel);
        var item = new Style(typeof(ListBoxItem));
        item.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) }));
        item.Setters.Add(new Setter(FocusableProperty, false));
        box.ItemContainerStyle = item;
        box.ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(RowHost)) };
        return box;
    }

    void RenderFilters()
    {
        filters.Children.Clear();
        foreach (var (tag, title) in Model.Filters)
        {
            var on = model.Filter == tag;
            var pill = new Border
            {
                Padding = new Thickness(7, 4, 7, 4), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 2, 0),
                Background = on ? UI.White(0.14) : Brushes.Transparent,
                Child = UI.Label(title, 11.5, on ? UI.Primary : UI.Secondary, FontWeights.Medium),
            };
            pill.MouseLeftButtonUp += (_, _) => model.Filter = tag;
            filters.Children.Add(pill);
        }
    }

    /// A new search or filter shows its results from the top; a delete or a new clip keeps the list where it was.
    bool backToTop;

    void RenderRows()
    {
        listArea.Children.Clear();
        listArea.Children.Add(list);
        list.ItemsSource = model.Clips.Select((c, i) => new RowItem(c, i, i == model.Clips.Count - 1, model)).ToList();
        if (backToTop && list.Items.Count > 0) list.ScrollIntoView(list.Items[0]);
        backToTop = false;
        if (model.Clips.Count == 0)
        {
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var icon = UI.Icon(UI.Clipboard, 26, UI.Tertiary);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(icon);
            var text = UI.Label(model.Query.Length == 0 ? "Copy anything — it lands here" : "No matches", 12, UI.Secondary);
            text.Margin = new Thickness(0, 8, 0, 0);
            text.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(text);
            listArea.Children.Add(empty);
            return;
        }
        ShowPreview();
    }

    /// Rows repaint their own highlight; the list only scrolls.
    void Select()
    {
        if (list.ItemsSource is List<RowItem> items && items.FirstOrDefault(r => r.Clip.Id == model.Selection) is { } sel) list.ScrollIntoView(sel);
        ShowPreview();
    }

    void ShowPreview()
    {
        foreach (var old in listArea.Children.OfType<PreviewPane>().ToList()) listArea.Children.Remove(old);
        if (model.Previewing && model.Selected is { } sel) listArea.Children.Add(new PreviewPane(sel));
    }

    FrameworkElement Footer()
    {
        var footer = new Grid { Margin = new Thickness(14, 0, 14, 0) };
        var icon = new Grid();
        icon.Children.Add(UI.Icon(UI.Gear, 14, UI.Secondary));
        // A dot when an update is waiting in Settings.
        var dot = new Ellipse { Width = 6, Height = 6, Fill = UI.AccentBrush, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, -3, 0) };
        icon.Children.Add(dot);
        var gear = new Border { Child = icon, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Settings" };
        void ShowDot() => dot.Visibility = Updater.Shared.Now is Updater.Available ? Visibility.Visible : Visibility.Collapsed;
        ShowDot();
        Updater.Shared.Changed += ShowDot;
        gear.MouseLeftButtonUp += (_, _) => model.ShowSettings = true;
        footer.Children.Add(gear);
        var keys = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (k, label) in new[] { ("↩", "Paste"), ("Alt ↩", "Copy"), ("Ctrl Y", "Preview"), ("Ctrl Del", "Delete") })
            keys.Children.Add(Key(k, label, keys.Children.Count == 0 ? 0 : 14));
        footer.Children.Add(keys);
        return footer;
    }

    public static FrameworkElement Key(string keys, string label, double left = 0)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(left, 0, 0, 0) };
        var text = UI.Label(label, 11, UI.Secondary);
        text.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(text);
        row.Children.Add(new Border
        {
            Margin = new Thickness(5, 0, 0, 0), Padding = new Thickness(5, 1, 5, 2), CornerRadius = new CornerRadius(4), Background = UI.White(0.09),
            Child = UI.Label(keys, 10.5, UI.Secondary, FontWeights.SemiBold),
        });
        return row;
    }

}

/// Ctrl+Y: the selected item at full size.
sealed class PreviewPane : Border
{
    /// Laying out megabytes of text would freeze the panel; the full text still pastes.
    static string Shown(string text) =>
        text.Length > 100_000 ? Store.Head(text, 100_000) + "\n\n… the rest is too long to preview, but it pastes in full" : text;

    public PreviewPane(Clip clip)
    {
        Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x0E, 0x0D));
        if (clip.ImagePath is { } p) Child = new Thumb(p, 1400, fit: true) { Margin = new Thickness(12), Background = Brushes.Transparent };
        else if (clip.Paths is { Length: > 0 } paths)
        {
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20) };
            var icon = Shell.IconImage(paths[0], 128);
            icon.Width = icon.Height = 64;
            stack.Children.Add(icon);
            stack.Children.Add(new TextBox
            {
                Text = string.Join("\n", paths), IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                Foreground = UI.Secondary, FontFamily = UI.Text, FontSize = 11.5, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
            });
            Child = stack;
        }
        else
        {
            // The text box scrolls itself: inside a separate scroller it lays out far more than fits on screen
            // (a 100,000-character preview took about 0.5 s to appear, now about 0.2 s).
            Child = new TextBox
            {
                Text = Shown(clip.Text), IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = UI.Primary,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(14),
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            };
        }
    }
}

/// Hovering the bubble: the last three items, one click to paste.
sealed class PeekView : StackPanel
{
    readonly Model model;
    readonly Func<string> hotkey;

    public PeekView(Model model, Func<string> hotkey)
    {
        this.model = model;
        this.hotkey = hotkey;
        Width = 288;   // 300 with the padding, like SwiftUI's .padding(6).frame(width: 300)
        Margin = new Thickness(6);
        model.Changed += what => { if (what == Change.Recent) Render(); };
        Render();
    }

    void Render()
    {
        Children.Clear();
        // The Mac peek's header sits 8 pt lower than its padding suggests (measured from Assets/press/peek.png).
        var header = new Grid { Margin = new Thickness(8, 12, 8, 3) };
        header.Children.Add(UI.Tracked("RECENT", 10, 0.6, UI.Tertiary, FontWeights.SemiBold));
        var hint = UI.Label($"{hotkey()} for all", 10, UI.Tertiary);
        hint.HorizontalAlignment = HorizontalAlignment.Right;
        header.Children.Add(hint);
        Children.Add(header);
        if (model.Recent.Count == 0)
        {
            var empty = UI.Label("Nothing copied yet", 12, UI.Secondary);
            empty.Margin = new Thickness(8);
            Children.Add(empty);
        }
        foreach (var c in model.Recent) Children.Add(new Row(c, -1, false, model) { Margin = new Thickness(0, 1, 0, 0) });
    }
}

// MARK: - Settings

/// Settings, shown in place of the list (gear in the footer, Esc to go back).
sealed class SettingsView : Grid
{
    readonly Model model;
    readonly App app;
    readonly StackPanel content = new() { Margin = new Thickness(16) };
    readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly DispatcherTimer copiedReset = new() { Interval = TimeSpan.FromSeconds(1.6) };
    /// Link iPhone: the open pairing, the link its QR code shows, and what to tell the user.
    Pairing? pairing;
    string? pairLink, pairNote;

    public SettingsView(Model model, App app)
    {
        this.model = model;
        this.app = app;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
        RowDefinitions.Add(new RowDefinition());

        var header = new Grid { Margin = new Thickness(16, 0, 16, 0) };
        var back = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left };
        back.Children.Add(UI.Icon(UI.Back, 12, UI.Secondary));
        var title = UI.Label("Settings", 16, UI.Primary, FontWeights.SemiBold);
        title.Margin = new Thickness(6, 0, 0, 0);
        title.VerticalAlignment = VerticalAlignment.Center;
        back.Children.Add(title);
        back.MouseLeftButtonUp += (_, _) => model.ShowSettings = false;
        header.Children.Add(back);
        var esc = UI.Label("esc", 10, UI.Tertiary, FontWeights.Medium);
        esc.HorizontalAlignment = HorizontalAlignment.Right;
        esc.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(esc);
        Children.Add(header);
        var divider = UI.Divider();
        SetRow(divider, 1);
        Children.Add(divider);
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Focusable = false };
        SetRow(scroll, 2);
        Children.Add(scroll);

        // Focusable so it can hold the keyboard (Esc) while the list and its search field are hidden.
        Focusable = true;
        FocusVisualStyle = null;
        Render();
        // Like the Mac's 2 s refresh: notices folders appearing or becoming readable, without rebuilding
        // (and so interrupting) the controls when nothing changed.
        ticker.Tick += (_, _) => { if (Folders() != shown) Render(); };
        Action<Change> changed = what => { if (what is Change.Prefs or Change.Mascot or Change.Sync) Render(); };
        Action updated = Render;
        Loaded += (_, _) => { ticker.Start(); model.Changed += changed; Updater.Shared.Changed += updated; Keyboard.Focus(this); };
        Unloaded += (_, _) => { ticker.Stop(); model.Changed -= changed; Updater.Shared.Changed -= updated; };
    }

    (string Shots, bool ShotsOk, string? Downloads, bool DownloadsOk) shown;

    /// What the folder rows show: where each folder is and whether it can be read.
    static (string, bool, string?, bool) Folders()
    {
        var shots = FolderWatcher.ScreenshotFolder;
        var downloads = FolderWatcher.DownloadsFolder;
        return (shots, FolderWatcher.CanRead(shots), downloads, FolderWatcher.CanRead(downloads));
    }

    /// Rebuilt whole (it's small) when something it shows changes.
    void Render()
    {
        shown = Folders();
        content.Children.Clear();

        var bubble = Section("Bubble");
        bubble.Children.Add(Row(UI.Hide, "Hide bubble until needed", $"Shows up when you copy something or press {app.BubbleKey.Text}.",
            Switch(App.HideBubble, on => Save(() => { app.SetHideBubble(on); Render(); }))));
        if (App.HideBubble) bubble.Children.Add(Row(UI.Keyboard, "Show or hide bubble", null, HotkeyBox(app.BubbleKey)));

        var save = Section("Save to history");
        foreach (var c in Prefs.Categories.Where(c => c.Key is not ("screenshots" or "downloads")))
            save.Children.Add(Row(c.Icon, c.Title, c.Detail, Switch(Prefs.On(c.Key), on => Save(() => model.SetPref(c.Key, on)))));
        save.Children.Add(Note("Turning one off stops saving it and hides what's already there, for your agents too."));

        var folders = Section("Folders & permissions");
        var shots = shown.Shots;
        var custom = Prefs.Get<string?>("screenshotFolder", null) is { Length: > 0 };
        var shotExtra = new StackPanel();
        var links = new StackPanel { Orientation = Orientation.Horizontal };
        links.Children.Add(Link("Change folder…", PickShotsFolder));
        if (custom) links.Children.Add(Link("Use Pictures\\Screenshots", () => Save(() => { Prefs.Set("screenshotFolder", null); model.SetPref("screenshots", true); }), 12));
        shotExtra.Children.Add(links);
        shotExtra.Children.Add(Check("Also copy new ones to the clipboard", Prefs.Get("copyScreenshots", true), on => Save(() => Prefs.Set("copyScreenshots", on))));
        folders.Children.Add(PermRow(UI.Camera, "Screenshots", shots, custom ? "Folder you picked" : "Where Windows and Snipping Tool save them",
            Prefs.On("screenshots") ? shown.ShotsOk : null, "screenshots", shotExtra));
        var downloads = shown.Downloads;
        folders.Children.Add(PermRow(UI.Download, "Recent downloads", downloads, null,
            Prefs.On("downloads") ? shown.DownloadsOk : null, "downloads", null));
        folders.Children.Add(Row(UI.Keyboard, "Auto-paste", "Enter pastes into the app you were in. Apps running as administrator just get a copy.",
            Switch(Prefs.Get("autoPaste", true), on => Save(() => Prefs.Set("autoPaste", on)))));
        folders.Children.Add(Row(UI.Keyboard, "Open ClipBubble", null, HotkeyBox(app.PanelKey)));

        RenderSync();

        var mascots = Section("Mascot");
        var grid = new WrapPanel { Width = 7 * 50 };
        foreach (var m in model.AllMascots)
        {
            var active = m.Id == model.Mascot.Id;
            var cell = new Grid { Width = 42, Height = 42, Margin = new Thickness(0, 0, 8, 8), Background = Brushes.Transparent, ToolTip = m.Name };
            cell.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromRgb(23, 23, 23)), Stroke = active ? UI.AccentBrush : UI.White(0.1), StrokeThickness = active ? 2 : 1 });
            cell.Children.Add(new MascotView(m) { LayoutTransform = new ScaleTransform(0.8, 0.8) });
            cell.MouseLeftButtonUp += (_, _) => { if (Save(() => Mascots.SetActive(m.Id))) model.LoadMascots(); };
            grid.Children.Add(cell);
        }
        var plus = new Grid { Width = 42, Height = 42, Margin = new Thickness(0, 0, 8, 8), Background = Brushes.Transparent, ToolTip = "Add your own (PNG, GIF, SVG)" };
        plus.Children.Add(new Ellipse { Stroke = UI.White(0.18), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } });
        var plusIcon = UI.Icon(UI.Plus, 14, UI.Secondary);
        plusIcon.HorizontalAlignment = HorizontalAlignment.Center;
        plus.Children.Add(plusIcon);
        plus.MouseLeftButtonUp += (_, _) => AddMascot();
        grid.Children.Add(plus);
        mascots.Children.Add(grid);

        var agents = Section("AI agents (MCP)");
        var blurb = UI.Label("Let Claude Code, Codex, Cursor and other agents search your history, see your screenshots, put things on your clipboard and design mascots.", 12, UI.Secondary);
        blurb.TextWrapping = TextWrapping.Wrap;
        blurb.Margin = new Thickness(0, 0, 0, 6);
        agents.Children.Add(blurb);
        var copy = Button("Copy setup prompt", UI.Copy, () => { });
        copy.MouseLeftButtonUp += (_, _) =>
        {
            Board.WriteText(Mcp.SetupPrompt(Process.GetCurrentProcess().MainModule!.FileName), transient: true);
            SetButton(copy, "Copied! Paste it into your agent", UI.Check);
            copiedReset.Stop();
            copiedReset.Tick -= ResetCopy;
            copiedReset.Tick += ResetCopy;
            copiedReset.Start();
            void ResetCopy(object? s, EventArgs e) { copiedReset.Stop(); SetButton(copy, "Copy setup prompt", UI.Copy); }
        };
        agents.Children.Add(copy);
        agents.Children.Add(Note("Paste it into your agent and it connects itself.", top: 4));

        var general = Section("General");
        general.Children.Add(UpdateRow());
        general.Children.Add(Row(UI.Power, "Open at login", null, Switch(App.OpenAtLogin, on => Save(() => App.OpenAtLogin = on))));
        var buttons = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(Button("Clear all history…", null, ClearAll));
        var quit = Button("Quit ClipBubble", null, () => Application.Current.Shutdown());
        quit.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Children.Add(quit);
        general.Children.Add(buttons);
    }

    /// The version, whether there's a newer one, and the button that installs it.
    FrameworkElement UpdateRow()
    {
        var updater = Updater.Shared;
        var (detail, trailing) = updater.Now switch
        {
            Updater.Checking => ("Checking…", (FrameworkElement?)null),
            Updater.Current => ("Up to date.", Button("Check now", null, () => updater.Check())),
            Updater.Available a => ($"Version {a.Release.Version} is available.", Button("Update", UI.Download, () => updater.Install(a.Release))),
            Updater.Installing i => (i.Step, null),
            Updater.Failed f => (f.Why, Button("Try again", null, () => updater.Check())),
            _ => ("Checks GitHub for a new version every few hours.", Button("Check now", null, () => updater.Check())),
        };
        var box = new StackPanel();
        box.Children.Add(Row(UI.Download, "ClipBubble " + Engine.Version, detail, trailing ?? new Border()));
        var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(30, 0, 0, 0) };
        if (updater.Now is Updater.Available av) links.Children.Add(Link("What's new", () => Open(av.Release.Page)));
        if (updater.Now is Updater.Failed) links.Children.Add(Link("Open the releases page", () => Open(Updater.Releases)));
        if (links.Children.Count > 0) box.Children.Add(links);
        var auto = Check("Check for updates automatically", Updater.Automatic, on => Save(() => Updater.Automatic = on));
        auto.Margin = new Thickness(30, 2, 0, 0);
        box.Children.Add(auto);
        return box;
    }

    static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Win32Exception) { }
    }

    /// Hosts from ~/.ssh/config, each with a switch and how its link is doing.
    void RenderSync()
    {
        var sync = Section("Sync over SSH");
        sync.Children.Add(Note("Copy on one PC, paste on another, using your SSH config and keys.", top: 0));
        var link = Button("Link iPhone…", UI.Phone, StartPairing);
        link.Margin = new Thickness(0, 6, 0, 0);
        sync.Children.Add(link);
        if (pairLink != null) sync.Children.Add(QrImage(pairLink));
        if (pairNote != null) sync.Children.Add(Note(pairNote, top: 4));
        var enabled = new HashSet<string>(Engine.Hosts);
        var paused = Engine.Paused;
        var hosts = Engine.SshHosts();
        if (hosts.Count == 0) sync.Children.Add(Note("No hosts in ~\\.ssh\\config yet. Machines that link to this one show up by themselves.", top: 4));
        foreach (var h in hosts)
        {
            var state = paused && enabled.Contains(h) ? "Paused" : model.Sync?.Statuses.TryGetValue(h, out var st) == true ? st!.Text : null;
            sync.Children.Add(Row(UI.Network, h, state, Switch(enabled.Contains(h), on => Save(() => SetHost(h, on)))));
        }
        sync.Children.Add(Row(UI.Pause, "Pause sync", null, Switch(paused, on => Save(() => SetPaused(on)))));
        var linked = model.Sync?.Linked.Select(p => p.Name).ToList() ?? new List<string>();
        sync.Children.Add(Note($"This PC shows up as {model.Sync?.Name ?? Environment.MachineName}." +
            (linked.Count > 0 ? $" Linked now: {string.Join(", ", linked)}." : "") + " Files up to 25 MB move on their own."));
    }

    /// Shows a code for the iPhone to scan. The first time, Windows asks to turn on OpenSSH Server, which the phone
    /// connects to.
    void StartPairing()
    {
        pairing?.Stop();
        pairing = null;
        pairLink = null;
        pairNote = "Checking OpenSSH Server…";
        Render();
        var name = model.Sync?.Name ?? Environment.MachineName;
        void Show(string? link, string note) => Dispatcher.BeginInvoke(new Action(() => { pairLink = link; pairNote = note; Render(); }));
        Task.Run(() =>
        {
            try
            {
                if (!Pairing.Ready())
                {
                    Show(null, "Windows will ask to turn on OpenSSH Server, which the iPhone connects to. The first time can take a few minutes.");
                    Pairing.SetUp();
                }
                var p = new Pairing(name, note => Show(null, note));
                Dispatcher.BeginInvoke(new Action(() => pairing = p));
                Show(p.Link, "Scan with the iPhone's Camera, or in ClipBubble on the iPhone. It needs the same Wi-Fi as this PC.");
            }
            catch (PairingException e) { Show(null, e.Message); }
        });
    }

    /// Black on white with the quiet zone, four pixels a module, kept sharp at any display scale.
    static Image QrImage(string text)
    {
        var m = Qr.Encode(text);
        int size = m.GetLength(0), px = 4, w = (size + 8) * px;
        var pixels = new byte[w * w];
        for (var y = 0; y < w; y++)
            for (var x = 0; x < w; x++)
            {
                int my = y / px - 4, mx = x / px - 4;
                pixels[y * w + x] = my >= 0 && my < size && mx >= 0 && mx < size && m[my, mx] ? (byte)0 : (byte)255;
            }
        var img = new Image
        {
            Source = BitmapSource.Create(w, w, 96, 96, PixelFormats.Gray8, null, pixels, w),
            Width = w, Height = w, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0),
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        return img;
    }

    void SetHost(string host, bool on)
    {
        Prefs.Set("sync.hosts", Engine.Hosts.Where(x => x != host).Concat(on ? new[] { host } : Array.Empty<string>()).ToArray());
        model.Sync?.Post(model.Sync.ReconcileLinks);
    }

    void SetPaused(bool on)
    {
        Prefs.Set("sync.paused", on);
        if (model.Sync is not { } sync) return;
        sync.Post(() =>
        {
            if (on) foreach (var s in sync.Sessions.ToList()) s.Close();
            sync.ReconcileLinks();
        });
    }

    StackPanel Section(string title)
    {
        var s = new StackPanel { Margin = new Thickness(0, content.Children.Count == 0 ? 0 : 18, 0, 0) };
        var label = UI.Tracked(title.ToUpperInvariant(), 10, 0.6, UI.Tertiary, FontWeights.SemiBold);
        label.Margin = new Thickness(0, 0, 0, 6);
        s.Children.Add(label);
        content.Children.Add(s);
        return s;
    }

    static TextBlock Note(string text, double top = 2)
    {
        var t = UI.Label(text, 10.5, UI.Tertiary);
        t.TextWrapping = TextWrapping.Wrap;
        t.Margin = new Thickness(0, top, 0, 0);
        return t;
    }

    static FrameworkElement Row(string icon, string title, string? detail, FrameworkElement trailing)
    {
        var g = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var i = UI.Icon(icon, 13, UI.Secondary);
        i.HorizontalAlignment = HorizontalAlignment.Center;
        g.Children.Add(i);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        texts.Children.Add(UI.Label(title, 13, UI.Primary, FontWeights.Medium));
        if (detail != null)
        {
            var d = UI.Label(detail, 11, UI.Secondary);
            d.TextWrapping = TextWrapping.Wrap;
            d.Margin = new Thickness(0, 1, 0, 0);
            texts.Children.Add(d);
        }
        Grid.SetColumn(texts, 1);
        g.Children.Add(texts);
        trailing.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(trailing, 2);
        g.Children.Add(trailing);
        return g;
    }

    /// A feature that needs a folder: switch, where it looks, and whether it can read it.
    FrameworkElement PermRow(string icon, string title, string? folder, string? note, bool? ok, string pref, FrameworkElement? extra)
    {
        var g = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var i = UI.Icon(icon, 13, UI.Secondary);
        i.HorizontalAlignment = HorizontalAlignment.Center;
        i.VerticalAlignment = VerticalAlignment.Top;
        i.Margin = new Thickness(0, 1, 0, 0);
        g.Children.Add(i);
        var texts = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        texts.Children.Add(UI.Label(title, 13, UI.Primary, FontWeights.Medium));
        if (folder != null)
        {
            var f = UI.Label(UI.Tilde(folder), 11, UI.Secondary);
            f.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            f.Margin = new Thickness(0, 3, 0, 0);
            texts.Children.Add(f);
        }
        if (note != null) { var n = UI.Label(note, 11, UI.Secondary); n.TextWrapping = TextWrapping.Wrap; n.Margin = new Thickness(0, 3, 0, 0); texts.Children.Add(n); }
        var status = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        void Status(string glyph, string text, Brush color)
        {
            var icon = UI.Icon(glyph, 11, color);
            icon.Margin = new Thickness(0, 0, 4, 0);
            status.Children.Add(icon);
            status.Children.Add(UI.Label(text, 11, color, FontWeights.Medium));
        }
        switch (ok)
        {
            case true: Status(UI.Ok, "Allowed", new SolidColorBrush(UI.Green)); break;
            case false: Status(UI.Warning, "Can't find this folder", new SolidColorBrush(UI.Orange)); break;
            default: Status(UI.Off, "Off", UI.Tertiary); break;
        }
        texts.Children.Add(status);
        if (extra != null) { extra.Margin = new Thickness(0, 3, 0, 0); texts.Children.Add(extra); }
        Grid.SetColumn(texts, 1);
        g.Children.Add(texts);
        var sw = Switch(Prefs.On(pref), on => Save(() => model.SetPref(pref, on)));
        sw.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(sw, 2);
        g.Children.Add(sw);
        return g;
    }

    /// Saves a setting. If that fails (the data folder is read-only or locked), it says so and returns false, so the
    /// control that changed it stays as it was.
    bool Save(Action save)
    {
        try { save(); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Error(e);
            model.ShowError("Couldn't save that setting", e.Message);
            return false;
        }
    }

    /// The Mac's mini switch: a 26 x 15 capsule with a white knob. `changed` returns false when the change didn't take.
    static FrameworkElement Switch(bool on, Func<bool, bool> changed)
    {
        var knob = new Ellipse { Width = 13, Height = 13, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(on ? 12 : 1, 0, 0, 0) };
        var track = new Border { Width = 26, Height = 15, CornerRadius = new CornerRadius(7.5), Background = on ? UI.AccentBrush : UI.White(0.18), Child = knob, Cursor = Cursors.Arrow };
        track.MouseLeftButtonUp += (_, _) =>
        {
            if (!changed(!on)) return;
            on = !on;
            track.Background = on ? UI.AccentBrush : UI.White(0.18);
            knob.BeginAnimation(MarginProperty, new ThicknessAnimation(new Thickness(on ? 12 : 1, 0, 0, 0), TimeSpan.FromSeconds(0.15)));
        };
        return track;
    }

    static FrameworkElement Check(string title, bool on, Func<bool, bool> changed)
    {
        var box = new Border { Width = 13, Height = 13, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 6, 0), BorderThickness = new Thickness(1) };
        var mark = new TextBlock { Text = UI.Check, FontFamily = UI.Icons, FontSize = 9, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        box.Child = mark;
        void Paint() { box.Background = on ? UI.AccentBrush : UI.White(0.08); box.BorderBrush = on ? UI.AccentBrush : UI.White(0.25); mark.Visibility = on ? Visibility.Visible : Visibility.Hidden; }
        Paint();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, Margin = new Thickness(0, 4, 0, 0) };
        row.Children.Add(box);
        row.Children.Add(UI.Label(title, 11, UI.Secondary));
        row.MouseLeftButtonUp += (_, _) => { if (!changed(!on)) return; on = !on; Paint(); };
        return row;
    }

    static FrameworkElement Link(string title, Action act, double left = 0)
    {
        var t = UI.Label(title, 11, UI.AccentBrush);
        t.Margin = new Thickness(left, 0, 0, 0);
        t.Cursor = Cursors.Hand;
        t.MouseLeftButtonUp += (_, _) => act();
        return t;
    }

    static Border Button(string title, string? icon, Action act)
    {
        var b = new Border { CornerRadius = new CornerRadius(5), Background = UI.White(0.14), Padding = new Thickness(8, 2, 8, 3), HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Arrow };
        SetButton(b, title, icon);
        b.MouseEnter += (_, _) => b.Background = UI.White(0.2);
        b.MouseLeave += (_, _) => b.Background = UI.White(0.14);
        b.MouseLeftButtonUp += (_, _) => act();
        return b;
    }

    static void SetButton(Border b, string title, string? icon)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon != null) { var i = UI.Icon(icon, 11, UI.Primary); i.Margin = new Thickness(0, 0, 5, 0); row.Children.Add(i); }
        row.Children.Add(UI.Label(title, 11, UI.Primary));
        b.Child = row;
    }

    /// Marks the shortcut box while it's waiting for keys, so Esc cancels recording instead of leaving Settings.
    public const string Recording = "recording";

    /// Click, then press the new shortcut. Esc cancels.
    FrameworkElement HotkeyBox(App.GlobalKey hotkey)
    {
        var label = UI.Label(hotkey.Label, 11, UI.Primary, FontWeights.Medium);
        var box = new Border { CornerRadius = new CornerRadius(5), Background = UI.White(0.09), Padding = new Thickness(8, 2, 8, 3), Child = label, Focusable = true, Cursor = Cursors.Arrow, FocusVisualStyle = null };
        var recording = false;
        void Stop(string text)
        {
            recording = false;
            box.Tag = null;
            app.PauseHotkeys(false);
            label.Text = text;
            Keyboard.Focus(this);   // off the box, so Esc goes back to the list again
        }
        // The shortcuts are switched off while recording, so pressing one records it instead of closing the panel.
        box.MouseLeftButtonUp += (_, _) => { recording = true; box.Tag = Recording; app.PauseHotkeys(true); label.Text = "Press keys…"; box.Focus(); };
        box.LostKeyboardFocus += (_, _) => { if (recording) Stop(hotkey.Label); };
        box.Unloaded += (_, _) => { if (recording) Stop(hotkey.Label); };
        box.PreviewKeyDown += (_, e) =>
        {
            if (!recording) return;
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { Stop(hotkey.Label); return; }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
            var mods = Keyboard.Modifiers;
            if (mods == ModifierKeys.None || mods == ModifierKeys.Shift) return;   // a bare key would swallow typing everywhere
            var taken = false;
            Save(() => taken = !app.SetHotkey(hotkey, mods, key));
            Stop(taken ? "Taken, try another" : hotkey.Label);
        };
        return box;
    }

    void PickShotsFolder()
    {
        if (app.Modal(() => Shell.PickFolder(Win.Handle(app.PanelWindow), FolderWatcher.ScreenshotFolder, "Pick the folder ClipBubble should watch for screenshots")) is not { } folder) return;
        Save(() => { Prefs.Set("screenshotFolder", folder); model.SetPref("screenshots", true); });
    }

    /// Picks an image or GIF and makes it the mascot.
    void AddMascot()
    {
        var open = new OpenFileDialog { Filter = "Images|*.png;*.gif;*.jpg;*.jpeg;*.svg;*.webp;*.heic", Title = "Pick an image or GIF for your mascot. Square with a transparent background looks best." };
        if (app.Modal(() => open.ShowDialog(app.PanelWindow)) != true) return;
        try
        {
            var m = Mascots.Add(Path.GetFileNameWithoutExtension(open.FileName), "image", null, new Dictionary<string, string> { ["idle"] = open.FileName });
            Mascots.SetActive(m.Id);
            model.LoadMascots();
        }
        catch (Exception e) when (e is MascotException or IOException or UnauthorizedAccessException)
        {
            model.ShowError("Couldn't add that mascot", e.Message);
        }
    }

    void ClearAll()
    {
        if (!app.Modal(() => Alert.Show(app.PanelWindow, "Clear all clipboard history?", "This removes every saved item and image, here and on your linked machines. It can't be undone.", "Clear"))) return;
        model.ClearAll();
    }
}

// MARK: - App

sealed class App : Application
{
    const double IdleAlpha = 0.4, Margin = 10, HideAfter = 3;
    readonly Model model = new();
    /// Tests make an App only to host Settings. WPF still starts it on the first dispatcher pump; this keeps the bubble, capture and sync from starting.
    public bool Headless { get; init; }
    FloatingWindow bubble = null!, panel = null!, peek = null!;
    BubbleView face = null!;
    Capture capture = null!;
    FolderWatcher? screenshots, downloads;
    HwndSource messages = null!;
    IntPtr pasteTarget;
    DispatcherTimer? pulseWork, peekWork, hideWork;
    bool dragging, pressed, bubbleHover, peekHover, pulse, modal, hiding;
    public readonly GlobalKey PanelKey = new(1, "hotkey", ModifierKeys.Control | ModifierKeys.Shift, Key.Space);
    public readonly GlobalKey BubbleKey = new(2, "bubbleHotkey", ModifierKeys.Control | ModifierKeys.Alt, Key.B);

    public Window PanelWindow => panel;

    /// Queued writes get 5 s to finish on quit, or 30 s while a delete or Clear all the user asked for is still waiting
    /// behind them (a long backfill can be in front).
    protected override void OnExit(ExitEventArgs e)
    {
        model.Writer.Finish(TimeSpan.FromSeconds(model.DeletesWaiting ? 30 : 5));
        base.OnExit(e);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (Headless) return;
        // A bug in one handler shouldn't make the bubble disappear: it's logged and the app carries on.
        DispatcherUnhandledException += (_, x) => { Log.Error(x.Exception); x.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, x) => { if (x.ExceptionObject is Exception ex) Log.Error(ex); };
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        model.Writer.Write(db => db.Prune());   // storage only: lists already leave out anything older than 7 days
        model.Refresh();

        face = new BubbleView(model);
        bubble = new FloatingWindow(64, 64, keyable: false) { Content = face, Opacity = HideBubble ? 0 : IdleAlpha };
        face.HitArea.MouseEnter += (_, _) => SetBubbleHover(true);
        face.HitArea.MouseLeave += (_, _) => SetBubbleHover(false);
        face.HitArea.MouseLeftButtonDown += BubbleDown;
        face.HitArea.MouseMove += BubbleDrag;
        face.HitArea.MouseLeftButtonUp += BubbleUp;
        face.Pressed = Toggle;
        bubble.Show();   // even when hidden: placing it needs its handle and scale
        Snap(animated: false);
        if (HideBubble) bubble.Hide();

        var panelView = new PanelView(model, () => new SettingsView(model, this));
        panel = new FloatingWindow(420 + 2 * Glass.Shadow, 470 + 2 * Glass.Shadow, keyable: true) { Content = Glass.Wrap(panelView, 16) };
        panel.Deactivated += (_, _) => { if (!modal) HidePanel(); };
        panel.PreviewKeyDown += HandleKey;

        var peekGlass = Glass.Wrap(new PeekView(model, () => PanelKey.Text), 14);
        peek = new FloatingWindow(300 + 2 * Glass.Shadow, 100, keyable: false) { Content = peekGlass, SizeToContent = SizeToContent.Height };
        peekGlass.MouseEnter += (_, _) => { peekHover = true; UpdatePeek(); };
        peekGlass.MouseLeave += (_, _) => { peekHover = false; UpdatePeek(); };

        model.Pick = Pick;
        model.PrefsChanged = () => { SyncScreenshots(); SyncDownloads(); ImportFolders(); };
        model.ShowError = (title, reason) => Modal(() => Alert.Show(panel, title, reason, "OK", cancel: null));

        capture = new Capture(model.Writer, copied => { if (copied) { ShowBubble(); Captured(); } else RefreshLists(); });
        StartSync();
        SyncScreenshots();
        SyncDownloads();
        ImportFolders();
        After(8, ImportFolders);
        Every(3600, () => { model.Writer.Write(db => db.Prune()); ImportFolders(); });

        messages = new HwndSource(new HwndSourceParameters("ClipBubble messages") { ParentWindow = new IntPtr(-3) });
        messages.AddHook(Messages);
        RegisterHotkeys();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() => Snap(animated: false)));
        // Mascot changes from the MCP server (another process) and second launches land here.
        Listen(Mascots.ChangedEvent, () => { model.LoadMascots(); Captured(pulseOnly: true); });
        Listen(Program.OpenEvent, () => { if (!panel.IsVisible) ShowPanel(); });
    }

    int receivedPending, newPending;

    /// Links with ClipBubble on the hosts the user switched on, and lets other machines link here.
    void StartSync()
    {
        Engine.Downloads = () => Environment.GetEnvironmentVariable("CLIPBUBBLE_DOWNLOADS") is { Length: > 0 } d ? d
            : FolderWatcher.DownloadsFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var sync = new Engine();
        model.Sync = sync;
        sync.Apply = c => Dispatcher.BeginInvoke(new Action(() => ApplySynced(c)));
        // Many items arrive at once when a link first reconciles: one refresh covers them all.
        sync.Received = () => { if (Interlocked.Exchange(ref receivedPending, 1) == 0) Dispatcher.BeginInvoke(new Action(() => { receivedPending = 0; RefreshLists(); })); };
        sync.New = () => { if (Interlocked.Exchange(ref newPending, 1) == 0) Dispatcher.BeginInvoke(new Action(() => { newPending = 0; Captured(); })); };
        sync.Status = () => Dispatcher.BeginInvoke(new Action(model.SyncChanged));
        sync.Transfers = t => Dispatcher.BeginInvoke(new Action(() => model.SetTransfers(t)));
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, x) => { if (x.Mode == Microsoft.Win32.PowerModes.Resume) sync.Redial(); };
        sync.Start();
        Updater.Shared.Start();
    }

    /// A fresh copy from another machine goes on this clipboard. Capture then sees it as ours and leaves history alone.
    void ApplySynced(Clip c)
    {
        Recorder.Expect(Recorder.CaptureHash(c), null);
        if (Board.Write(c) is { } why) { SyncLog.Write($"couldn't put {c.Hash.Substring(0, Math.Min(10, c.Hash.Length))} on the clipboard: {why}"); return; }
        ShowBubble();
        Captured(pulseOnly: true);
    }

    void Listen(string name, Action act)
    {
        var handle = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        ThreadPool.RegisterWaitForSingleObject(handle, (_, _) => Dispatcher.BeginInvoke(act), null, -1, executeOnlyOnce: false);
    }

    void After(double seconds, Action act)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        t.Tick += (_, _) => { t.Stop(); act(); };
        t.Start();
    }

    void Every(double seconds, Action act)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        t.Tick += (_, _) => act();
        t.Start();
    }

    IntPtr Messages(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312) { if (wParam == new IntPtr(BubbleKey.Id)) ToggleBubble(); else Toggle(); }   // WM_HOTKEY
        if (msg == 0x001A && wParam == new IntPtr(0x2F)) Snap(animated: false);   // SPI_SETWORKAREA: the taskbar moved
        return IntPtr.Zero;
    }

    // MARK: Hotkey

    /// A global shortcut: what it is, the pref that keeps it, and whether Windows gave it to us.
    public sealed class GlobalKey(int id, string pref, ModifierKeys mods, Key key)
    {
        public readonly int Id = id;
        public readonly string Pref = pref;
        public (ModifierKeys Mods, Key Key) Now = (Prefs.Get<string?>(pref, null) is { } saved ? ParseHotkey(saved) : null) ?? (mods, key);
        /// Another app may already own it.
        public bool Works = true;

        public string Text => string.Join("+", new[] {
            Now.Mods.HasFlag(ModifierKeys.Control) ? "Ctrl" : null, Now.Mods.HasFlag(ModifierKeys.Alt) ? "Alt" : null,
            Now.Mods.HasFlag(ModifierKeys.Shift) ? "Shift" : null, Now.Mods.HasFlag(ModifierKeys.Windows) ? "Win" : null,
            Now.Key == Key.Space ? "Space" : new KeyConverter().ConvertToString(Now.Key) }.Where(s => s != null));

        public string Label => Works ? Text : Text + " (in use by another app)";
    }

    static (ModifierKeys, Key)? ParseHotkey(string text)
    {
        var mods = ModifierKeys.None;
        Key? key = null;
        foreach (var part in text.Split('+'))
        {
            switch (part)
            {
                case "Ctrl": mods |= ModifierKeys.Control; break;
                case "Alt": mods |= ModifierKeys.Alt; break;
                case "Shift": mods |= ModifierKeys.Shift; break;
                case "Win": mods |= ModifierKeys.Windows; break;
                default: try { key = (Key)new KeyConverter().ConvertFromString(part)!; } catch (Exception e) when (e is NotSupportedException or ArgumentException) { return null; } break;
            }
        }
        return key is { } k && mods != ModifierKeys.None ? (mods, k) : null;
    }

    /// Both are off while one is being recorded, so pressing either reaches the recorder instead of acting.
    public void PauseHotkeys(bool paused)
    {
        if (!paused) { RegisterHotkeys(); return; }
        Win.UnregisterHotKey(messages.Handle, PanelKey.Id);
        Win.UnregisterHotKey(messages.Handle, BubbleKey.Id);
    }

    /// The panel's shortcut always, the bubble's only while Hide bubble until needed is on.
    void RegisterHotkeys()
    {
        RegisterHotkey(PanelKey);
        if (HideBubble) RegisterHotkey(BubbleKey);
        else Win.UnregisterHotKey(messages.Handle, BubbleKey.Id);
    }

    bool RegisterHotkey(GlobalKey k)
    {
        Win.UnregisterHotKey(messages.Handle, k.Id);
        uint mods = 0x4000;   // MOD_NOREPEAT
        if (k.Now.Mods.HasFlag(ModifierKeys.Alt)) mods |= 0x1;
        if (k.Now.Mods.HasFlag(ModifierKeys.Control)) mods |= 0x2;
        if (k.Now.Mods.HasFlag(ModifierKeys.Shift)) mods |= 0x4;
        if (k.Now.Mods.HasFlag(ModifierKeys.Windows)) mods |= 0x8;
        return k.Works = Win.RegisterHotKey(messages.Handle, k.Id, mods, (uint)KeyInterop.VirtualKeyFromKey(k.Now.Key));
    }

    /// Switches a global shortcut; keeps the old one if the other shortcut or another app already owns the new one.
    public bool SetHotkey(GlobalKey k, ModifierKeys mods, Key key)
    {
        if ((k == PanelKey ? BubbleKey : PanelKey).Now == (mods, key)) return false;
        var old = k.Now;
        k.Now = (mods, key);
        if (!RegisterHotkey(k))
        {
            k.Now = old;
            RegisterHotkey(k);
            return false;
        }
        try { Prefs.Set(k.Pref, k.Text); }
        catch { k.Now = old; RegisterHotkey(k); throw; }   // a shortcut that's gone after a restart isn't really set
        model.ReloadRecent();
        return true;
    }

    public static bool HideBubble => Prefs.Get("hideBubble", false);

    public void SetHideBubble(bool on)
    {
        Prefs.Set("hideBubble", on);
        if (Headless) return;
        RegisterHotkeys();
        if (!on) ShowBubble();
        RestBubble();
    }

    public static bool OpenAtLogin
    {
        get => Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run", "ClipBubble", null) is string;
        set
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (run == null) return;
            if (value) run.SetValue("ClipBubble", $"\"{Process.GetCurrentProcess().MainModule!.FileName}\"");
            else run.DeleteValue("ClipBubble", throwOnMissingValue: false);
        }
    }

    /// Runs a dialog without the panel closing itself when the dialog takes focus.
    public T Modal<T>(Func<T> show)
    {
        modal = true;
        try { return show(); }
        finally { modal = false; if (panel.IsVisible) panel.Activate(); }
    }

    // MARK: Folders

    /// Every screenshot lands in history; copying it to the clipboard as well is optional.
    void SyncScreenshots()
    {
        var folder = FolderWatcher.ScreenshotFolder;
        if (!Prefs.On("screenshots")) { screenshots?.Dispose(); screenshots = null; return; }
        if (screenshots?.Folder == folder) return;
        screenshots?.Dispose();
        screenshots = new FolderWatcher(folder, 120, p => Store.IsImagePath(p) || new[] { ".mov", ".mp4" }.Contains(Path.GetExtension(p).ToLowerInvariant()), (path, date) =>
            capture.AddFiles("Screenshot", () => new[] { (path, date) }, () =>
            {
                // A copy to the clipboard is announced when the clipboard watcher saves it, unless copied files aren't saved.
                var copy = Prefs.Get("copyScreenshots", true);
                if (!copy || !Prefs.On("files")) Captured();
                if (copy) Board.WriteFiles(new[] { path });
            }));
    }

    /// Watches Downloads only while that's switched on.
    void SyncDownloads()
    {
        if (Prefs.On("downloads"))
        {
            if (downloads != null || FolderWatcher.DownloadsFolder is not { } dir) return;
            downloads = new FolderWatcher(dir, null, p => !FolderWatcher.TempExts.Contains(Path.GetExtension(p).TrimStart('.').ToLowerInvariant()), (path, date) =>
                capture.AddFiles("Downloads", () => new[] { (path, date) }, Captured));
        }
        else { downloads?.Dispose(); downloads = null; }
    }

    /// Backfills the last 7 days of screenshots and downloads, except ones the user removed. Cheap and idempotent,
    /// so it also reruns later.
    void ImportFolders()
    {
        // On the writer, each import first moves 1.3.0's list in; while that can't be done, nothing is imported.
        bool Ready() => model.Writer.Db.ImportLegacyForgotten(new[] { FolderWatcher.ScreenshotFolder, FolderWatcher.DownloadsFolder });
        foreach (var (watcher, source) in new[] { (screenshots, "Screenshot"), (downloads, "Downloads") })
            if (watcher != null)
                capture.AddFiles(source, () => Ready() ? watcher.Existing(7) : [], model.Refresh);
    }

    // MARK: Picking

    /// Puts the clip on the clipboard and, unless copying only, pastes it into the app underneath. If it can't go on
    /// the clipboard, it says why instead of pasting whatever was there before.
    async void Pick(Clip c, bool paste)
    {
        if (c.Remote) { model.Sync?.Get(c.Hash); return; }
        var target = panel.IsVisible ? pasteTarget : Win.GetForegroundWindow();
        // Files that came from another machine go on with their paths here: that copy still moves this item up.
        Recorder.Expect(Recorder.CaptureHash(c), c.Hash);
        if (Board.Write(c) is { } why) { model.ShowError("Couldn't copy that", why); return; }
        var copied = Win.GetClipboardSequenceNumber();
        HidePanel();
        HidePeek();
        if (!paste || !Prefs.Get("autoPaste", true) || target == IntPtr.Zero || !Win.IsWindow(target) || !Win.CanPasteInto(target)) return;
        Win.SetForegroundWindow(target);
        var failed = await Win.PasteWhenReady(
            () => Win.GetForegroundWindow() == target && !Win.ModifiersDown(),
            () => Win.GetClipboardSequenceNumber() == copied,
            Win.SendPaste);
        if (failed == null) return;
        model.ShowError("Couldn't paste", failed);
        Win.SetForegroundWindow(target);   // back to the app, ready for Ctrl+V
    }

    void HandleKey(object sender, KeyEventArgs e)
    {
        if (model.ShowSettings)
        {
            if (e.Key == Key.Escape && (Keyboard.FocusedElement as FrameworkElement)?.Tag is not SettingsView.Recording) { model.ShowSettings = false; e.Handled = true; }
            return;
        }
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;
        switch (key)
        {
            case Key.Down: model.Move(1); break;
            case Key.Up: model.Move(-1); break;
            case Key.Enter: if (model.Selected is { } c) Pick(c, !alt); break;
            case Key.Tab: model.CycleFilter(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1); break;
            case Key.Escape:
                if (model.Previewing) model.Previewing = false;
                else if (model.Query.Length > 0) model.Query = "";
                else HidePanel();
                break;
            case Key.Delete when ctrl: if (model.Selected is { } d) model.Delete(d); break;   // not Ctrl+Backspace: that deletes a word in the search box
            case Key.Y when ctrl: model.Previewing = !model.Previewing; break;
            case >= Key.D1 and <= Key.D9 when ctrl:
                var n = key - Key.D1;
                if (n < model.Clips.Count) Pick(model.Clips[n], !alt);
                break;
            default: e.Handled = false; break;
        }
    }

    // MARK: Corner magnet

    /// Moves the bubble to the nearest screen corner (or the saved one when not dragging), with a little overshoot.
    void Snap(bool animated, Win.POINT? around = null)
    {
        var f = Win.Frame(bubble);
        var center = around ?? new Win.POINT { X = (f.Left + f.Right) / 2, Y = (f.Top + f.Bottom) / 2 };
        if (!animated && around == null && f.Left < -5000) center = new Win.POINT();   // first placement: the primary screen
        var area = Win.WorkArea(center);
        var corner = Prefs.Get("corner", "br");
        if (animated)
        {
            corner = (center.Y < (area.Top + area.Bottom) / 2 ? "t" : "b") + (center.X > (area.Left + area.Right) / 2 ? "r" : "l");
            try { Prefs.Set("corner", corner); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error(e); }   // still snaps, just isn't remembered
        }
        var scale = Win.Scale(bubble);
        int w = f.Right - f.Left, h = f.Bottom - f.Top, m = (int)Math.Round(Margin * scale);
        var x = corner.EndsWith("l") ? area.Left + m : area.Right - w - m;
        var y = corner.StartsWith("t") ? area.Top + m : area.Bottom - h - m;
        if (!animated) { Win.Move(bubble, x, y); return; }
        Animate(bubble, f.Left, f.Top, x, y, 0.32, RestBubble);
    }

    EventHandler? snapping;

    void StopSnap()
    {
        if (snapping != null) CompositionTarget.Rendering -= snapping;
        snapping = null;
    }

    /// Moves a window along cubic-bezier(0.3, 1.35, 0.5, 1), the Mac's overshoot curve.
    void Animate(Window w, int x0, int y0, int x1, int y1, double seconds, Action done)
    {
        StopSnap();
        var start = DateTime.Now;
        snapping = (_, _) =>
        {
            var t = Math.Min(1, (DateTime.Now - start).TotalSeconds / seconds);
            var k = Bezier(t, 0.3, 1.35, 0.5, 1);
            Win.Move(w, (int)Math.Round(x0 + (x1 - x0) * k), (int)Math.Round(y0 + (y1 - y0) * k));
            if (t < 1) return;
            StopSnap();
            done();
        };
        CompositionTarget.Rendering += snapping;
    }

    static double Bezier(double t, double x1, double y1, double x2, double y2)
    {
        double X(double s) => 3 * (1 - s) * (1 - s) * s * x1 + 3 * (1 - s) * s * s * x2 + s * s * s;
        double Y(double s) => 3 * (1 - s) * (1 - s) * s * y1 + 3 * (1 - s) * s * s * y2 + s * s * s;
        double lo = 0, hi = 1;
        for (var i = 0; i < 30; i++) { var mid = (lo + hi) / 2; if (X(mid) < t) lo = mid; else hi = mid; }
        return Y((lo + hi) / 2);
    }

    // MARK: Bubble mouse

    Win.POINT downAt;
    Win.RECT startFrame;
    bool moved;

    void BubbleDown(object sender, MouseButtonEventArgs e)
    {
        StopSnap();
        downAt = Win.Cursor();
        startFrame = Win.Frame(bubble);
        moved = false;
        pressed = true;
        face.HitArea.CaptureMouse();
        RefreshMood();
    }

    void BubbleDrag(object sender, MouseEventArgs e)
    {
        if (!pressed || e.LeftButton != MouseButtonState.Pressed) return;
        var p = Win.Cursor();
        if (!moved)
        {
            if (Math.Sqrt(Math.Pow(p.X - downAt.X, 2) + Math.Pow(p.Y - downAt.Y, 2)) <= 3 * Win.Scale(bubble)) return;
            moved = true;
            dragging = true;
            HidePanel();
            HidePeek();
            SetAlpha(1);
        }
        Win.Move(bubble, startFrame.Left + p.X - downAt.X, startFrame.Top + p.Y - downAt.Y);
    }

    void BubbleUp(object sender, MouseButtonEventArgs e)
    {
        face.HitArea.ReleaseMouseCapture();
        if (!pressed) return;
        pressed = false;
        RefreshMood();
        if (moved) { dragging = false; Snap(animated: true); }
        else face.Pressed();
    }

    void SetBubbleHover(bool on)
    {
        if (on && !bubbleHover) face.Wiggle();
        bubbleHover = on;
        RefreshMood();
        UpdatePeek();
    }

    void RefreshMood() => face.SetMood(bubbleHover, pressed, pulse);

    void SetAlpha(double to) => UI.Fade(bubble, to, 0.25);

    /// The mascot hops and the bubble flashes opaque whenever something is captured.
    void Captured() => Captured(pulseOnly: false);

    void Captured(bool pulseOnly)
    {
        if (!pulseOnly)
        {
            RefreshLists();
            if (!panel.IsVisible) model.Unseen++;
        }
        pulse = true;
        RefreshMood();
        face.Pulse();
        SetAlpha(1);
        pulseWork?.Stop();
        pulseWork = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.1) };
        pulseWork.Tick += (_, _) => { pulseWork.Stop(); pulse = false; RefreshMood(); RestBubble(); };
        pulseWork.Start();
    }

    /// History changed, but there's nothing new to announce.
    void RefreshLists()
    {
        model.ReloadRecent();
        if (panel.IsVisible) model.Reload();
    }

    bool BubbleNeeded => panel.IsVisible || peek.IsVisible || bubbleHover || pulse || dragging || pressed;

    /// Nothing needs the bubble now: dim it and, in Hide bubble until needed, start the countdown.
    void RestBubble()
    {
        hideWork?.Stop();
        if (BubbleNeeded || !bubble.IsVisible || hiding) return;
        SetAlpha(IdleAlpha);
        if (!HideBubble) return;
        hideWork = new DispatcherTimer { Interval = TimeSpan.FromSeconds(HideAfter) };
        hideWork.Tick += (_, _) => { hideWork.Stop(); FadeBubble(); };
        hideWork.Start();
    }

    void FadeBubble()
    {
        if (!HideBubble || BubbleNeeded || !bubble.IsVisible) return;
        hiding = true;
        SetAlpha(0);
        After(0.25, () =>
        {
            if (!hiding) return;   // a reveal got there first
            hiding = false;
            if (!BubbleNeeded) bubble.Hide();
        });
    }

    /// Back on screen but still see-through: whoever calls this sets how opaque. Hidden, it has no position to
    /// jump from, so it comes back on the screen with the pointer.
    void ShowBubble()
    {
        hideWork?.Stop();
        hiding = false;
        if (bubble.IsVisible) return;
        UI.Fade(bubble, 0, 0, from: 0);
        bubble.Show();
        // Twice: moving to a screen with another scale resizes the window, and the corner depends on that size.
        Snap(animated: false, Win.Cursor());
        Snap(animated: false, Win.Cursor());
    }

    /// The bubble's own shortcut. While it's showing, everything goes at once.
    void ToggleBubble()
    {
        if (dragging || pressed) return;
        if (!bubble.IsVisible || hiding) { ShowBubble(); Captured(pulseOnly: true); return; }
        HidePanel();
        HidePeek();
        SetBubbleHover(false);
        pulseWork?.Stop();
        pulse = false;
        RefreshMood();
        hideWork?.Stop();
        FadeBubble();
    }

    // MARK: Panel

    void Toggle() { if (panel.IsVisible) HidePanel(); else ShowPanel(); }

    /// Opens the panel anchored to the bubble's corner, growing toward the screen center.
    void ShowPanel()
    {
        ShowBubble();
        HidePeek();
        var front = Win.GetForegroundWindow();
        if (front != Win.Handle(panel)) pasteTarget = front;
        model.Reset();
        model.Unseen = 0;

        UI.Fade(panel, 0, 0, from: 0);   // shown transparent, then faded in below
        panel.Show();
        var b = Win.Frame(bubble);
        var p = Card(panel);
        var area = Win.WorkArea(new Win.POINT { X = (b.Left + b.Right) / 2, Y = (b.Top + b.Bottom) / 2 });
        var s = Win.Scale(bubble);
        int pw = p.Right - p.Left, ph = p.Bottom - p.Top, edge = (int)(8 * s), inset = (int)(9 * s), lap = (int)(4 * s);
        var x = (b.Left + b.Right) / 2 > (area.Left + area.Right) / 2 ? b.Right - pw - inset : b.Left + inset;
        var y = (b.Top + b.Bottom) / 2 < (area.Top + area.Bottom) / 2 ? b.Bottom - lap : b.Top - ph + lap;
        MoveCard(panel, Math.Min(Math.Max(x, area.Left + edge), area.Right - pw - edge), Math.Min(Math.Max(y, area.Top + edge), area.Bottom - ph - edge));
        panel.Activate();
        model.Shown();
        UI.Fade(panel, 1, 0.1, from: 0);
        SetAlpha(1);
    }

    /// Instant, so a paste lands without waiting on an animation.
    void HidePanel()
    {
        if (!panel.IsVisible) return;
        model.ShowSettings = false;
        panel.Hide();
        RestBubble();
    }

    // MARK: Peek

    void UpdatePeek()
    {
        peekWork?.Stop();
        var want = (bubbleHover || peekHover) && !panel.IsVisible && !dragging;
        if (want && peek.IsVisible) return;
        peekWork = new DispatcherTimer { Interval = TimeSpan.FromSeconds(want ? 0.3 : 0.2) };
        peekWork.Tick += (_, _) => { peekWork.Stop(); if (want) ShowPeek(); else HidePeek(); };
        peekWork.Start();
        if (want) SetAlpha(1); else RestBubble();
    }

    void PlacePeek()
    {
        var b = Win.Frame(bubble);
        var p = Card(peek);
        var area = Win.WorkArea(new Win.POINT { X = (b.Left + b.Right) / 2, Y = (b.Top + b.Bottom) / 2 });
        var s = Win.Scale(bubble);
        int pw = p.Right - p.Left, ph = p.Bottom - p.Top, edge = (int)(8 * s), gap = (int)(4 * s), inset = (int)(9 * s);
        var x = (b.Left + b.Right) / 2 > (area.Left + area.Right) / 2 ? b.Left - pw - gap : b.Right + gap;
        var y = (b.Top + b.Bottom) / 2 < (area.Top + area.Bottom) / 2 ? b.Top + inset : b.Bottom - inset - ph;
        MoveCard(peek, Math.Min(Math.Max(x, area.Left + edge), area.Right - pw - edge), Math.Min(Math.Max(y, area.Top + edge), area.Bottom - ph - edge));
    }

    /// The visible panel or peek inside its window, without the shadow margin, in physical pixels.
    static Win.RECT Card(Window w)
    {
        var f = Win.Frame(w);
        var m = (int)Math.Round(Glass.Shadow * Win.Scale(w));
        return new Win.RECT { Left = f.Left + m, Top = f.Top + m, Right = f.Right - m, Bottom = f.Bottom - m };
    }

    static void MoveCard(Window w, int x, int y)
    {
        var m = (int)Math.Round(Glass.Shadow * Win.Scale(w));
        Win.Move(w, x - m, y - m);
    }

    void ShowPeek()
    {
        // Re-check at show time: a click may have opened the panel while this was scheduled.
        if (peek.IsVisible || panel.IsVisible || dragging || !(bubbleHover || peekHover)) return;
        model.ReloadRecent();
        model.Unseen = 0;
        UI.Fade(peek, 0, 0, from: 0);
        peek.Show();
        peek.UpdateLayout();
        PlacePeek();
        UI.Fade(peek, 1, 0.12, from: 0);
    }

    void HidePeek()
    {
        peekWork?.Stop();
        peekHover = false;
        if (!peek.IsVisible) return;
        peek.Hide();
        RestBubble();
    }
}
