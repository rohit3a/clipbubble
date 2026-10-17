using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.IO.Path;

namespace ClipBubble;

/// A bubble mascot. "blob" draws the built-in creature in `Color`; "emoji" shows emoji; "image" shows files
/// (PNG, GIF, JPG, SVG) from the mascots folder. `Hover` and `Active` (click/capture) fall back to `Idle`.
/// The JSON shape is shared with the Mac app.
sealed record Mascot(string Id, string Name, string Kind, string? Color = null, string? Idle = null, string? Hover = null, string? Active = null)
{
    public Dictionary<string, object> ToJson()
    {
        var d = new SortedDictionary<string, object?> { ["id"] = Id, ["name"] = Name, ["kind"] = Kind, ["color"] = Color, ["idle"] = Idle, ["hover"] = Hover, ["active"] = Active };
        return d.Where(p => p.Value != null).ToDictionary(p => p.Key, p => p.Value!);
    }

    public static Mascot? FromJson(Dictionary<string, object?>? d)
    {
        string? S(string k) => d != null && d.TryGetValue(k, out var v) ? v as string : null;
        return S("id") is { } id && S("name") is { } name && S("kind") is { } kind
            ? new Mascot(id, name, kind, S("color"), S("idle"), S("hover"), S("active")) : null;
    }
}

sealed class MascotException(string message) : Exception(message);

/// Mascot storage shared by the app and the MCP server. Changes are signalled so the running bubble updates live.
static class Mascots
{
    public static readonly string Dir = Path.Combine(Paths.Dir, "mascots");
    static readonly string Settings = Path.Combine(Paths.Dir, "settings.json");
    public static readonly string ChangedEvent = Program.Named("mascots");

    public static readonly Mascot[] Builtins =
    {
        new("blob", "Blob", "blob", Color: "#8E7CFF"),
        new("mint", "Mint", "blob", Color: "#34D399"),
        new("peach", "Peach", "blob", Color: "#FF9466"),
        new("sky", "Sky", "blob", Color: "#4FC3F7"),
        new("cat", "Cat", "emoji", Idle: "🐱", Hover: "😺", Active: "😸"),
        new("frog", "Frog", "emoji", Idle: "🐸"),
        new("ghost", "Ghost", "emoji", Idle: "👻"),
        new("robot", "Robot", "emoji", Idle: "🤖"),
        new("classic", "Classic icon", "icon"),
    };

    public static List<Mascot> Custom()
    {
        if (!Directory.Exists(Dir)) return new();
        return Directory.GetFiles(Dir, "*.json")
            .Select(f => { try { return Mascot.FromJson(Store.Json.Deserialize<Dictionary<string, object?>>(Store.ReadText(f))); } catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException) { return null; } })
            .OfType<Mascot>()
            .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static List<Mascot> All() => Builtins.Concat(Custom()).ToList();

    public static string ActiveId()
    {
        try { return Store.Json.Deserialize<Dictionary<string, object?>>(Store.ReadText(Settings))?.GetValueOrDefault("mascot") as string ?? "blob"; }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException) { return "blob"; }
    }

    public static Mascot Active() => Active(All());

    public static Mascot Active(List<Mascot> all)
    {
        var id = ActiveId();
        return all.FirstOrDefault(m => m.Id == id) ?? Builtins[0];
    }

    public static void SetActive(string id)
    {
        Directory.CreateDirectory(Paths.Dir);
        Store.WriteAtomic(Settings, Encoding.UTF8.GetBytes(Store.Json.Serialize(new Dictionary<string, object> { ["mascot"] = id })));
        Notify();
    }

    public static void Notify()
    {
        if (EventWaitHandle.TryOpenExisting(ChangedEvent, out var e)) using (e) e.Set();
    }

    public static string Url(string file) => Path.IsPathRooted(file) ? file : Path.Combine(Dir, file);

    /// Saves a custom mascot. For images, each state is a file path or inline SVG markup; files are copied in.
    public static Mascot Add(string name, string kind, string? color, Dictionary<string, string> states)
    {
        if (!new[] { "blob", "emoji", "image" }.Contains(kind)) throw new MascotException("kind must be blob, emoji or image");
        if (kind == "blob" && UI.HexColor(color ?? "") == null) throw new MascotException("blob needs color as a hex like #FF6AD5");
        if (kind != "blob" && string.IsNullOrEmpty(states.GetValueOrDefault("idle"))) throw new MascotException($"{kind} needs an idle value");
        Directory.CreateDirectory(Dir);

        var slug = new StringBuilder();
        foreach (var ch in name.ToLowerInvariant())
        {
            var c = char.IsLetterOrDigit(ch) ? ch : '-';
            if (!(c == '-' && slug.Length > 0 && slug[slug.Length - 1] == '-')) slug.Append(c);
        }
        var b = slug.ToString().Trim('-');
        if (b.Length == 0) b = "mascot";
        var id = b;
        var taken = new HashSet<string>(All().Select(m => m.Id));
        for (var n = 2; taken.Contains(id); n++) id = $"{b}-{n}";

        var values = new Dictionary<string, string>();
        var copied = new List<string>();
        try
        {
            foreach (var state in new[] { "idle", "hover", "active" })
            {
                var value = states.GetValueOrDefault(state)?.Trim();
                if (string.IsNullOrEmpty(value)) continue;
                if (kind == "image")
                {
                    string file;
                    if (value!.StartsWith("<svg") || value.StartsWith("<?xml"))
                    {
                        file = $"{id}-{state}.svg";
                        var dest = Path.Combine(Dir, file);
                        if (!File.Exists(dest)) copied.Add(dest);   // only files this import makes are cleaned up
                        try { File.WriteAllText(dest, value); }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                        {
                            throw new MascotException($"couldn't save the {state} SVG: {e.Message}");
                        }
                    }
                    else
                    {
                        var src = Environment.ExpandEnvironmentVariables(value.StartsWith("~") ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + value.Substring(1) : value);
                        file = $"{id}-{state}{Path.GetExtension(src).ToLowerInvariant()}";
                        var dest = Path.Combine(Dir, file);
                        try
                        {
                            // Already in place (a leftover file with this name): use it, and never delete it on failure.
                            if (!string.Equals(Path.GetFullPath(src), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                            {
                                if (!File.Exists(dest)) copied.Add(dest);
                                File.Copy(src, dest, overwrite: true);
                            }
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                        {
                            throw new MascotException($"couldn't read the {state} image at {src}");
                        }
                    }
                    if (Glyphs.Load(Url(file), 34) == null) throw new MascotException($"couldn't read the {state} image; use PNG, GIF, JPG or SVG");
                    value = file;
                }
                values[state] = value!;
            }
            var m = new Mascot(id, name, kind, kind == "blob" ? color : null, values.GetValueOrDefault("idle"), values.GetValueOrDefault("hover"), values.GetValueOrDefault("active"));
            Store.WriteAtomic(Path.Combine(Dir, id + ".json"), Encoding.UTF8.GetBytes(Store.Json.Serialize(m.ToJson())));
            Notify();
            return m;
        }
        catch
        {
            // A failed add leaves nothing behind.
            foreach (var f in copied) try { File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// Deletes a custom mascot, its manifest and the image files Add made for it (named `id-state.ext`), never a file
    /// another mascot owns. Built-ins can't be removed.
    public static bool Remove(string id)
    {
        if (Custom().FirstOrDefault(c => c.Id == id) is not { } m) return false;
        var owned = m.Kind != "image" ? Array.Empty<string>() : new[] { ("idle", m.Idle), ("hover", m.Hover), ("active", m.Active) }
            .Where(s => s.Item2 is { } f && Path.GetFileName(f) == f && f.StartsWith($"{id}-{s.Item1}."))
            .Select(s => s.Item2!).ToArray();
        foreach (var f in owned.Append(id + ".json"))
            try { File.Delete(Path.Combine(Dir, f)); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (ActiveId() == id) SetActive("blob"); else Notify();
        return true;
    }
}

// MARK: - Views

/// The mascot inside the bubble. Everything is a cheap transform; nothing animates while idle except a blink.
sealed class MascotView : Grid
{
    readonly Mascot mascot;
    readonly ScaleTransform squish = new();
    readonly RotateTransform tilt = new();
    readonly TranslateTransform hop = new();
    BlobFace? blob;
    Image? picture;
    TextBlock? icon;
    GifPlayer? gif;
    string mood = "idle";
    bool hover, pressed, happy;

    public MascotView(Mascot mascot)
    {
        this.mascot = mascot;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        RenderTransformOrigin = new Point(0.5, 1);
        var face = new Grid { RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = tilt };
        RenderTransform = new TransformGroup { Children = { squish, hop } };
        Children.Add(face);
        switch (mascot.Kind)
        {
            case "blob":
                blob = new BlobFace(UI.HexColor(mascot.Color ?? "") ?? Color.FromRgb(0x8F, 0x7D, 0xFF));
                face.Children.Add(blob);
                break;
            case "emoji":
            case "image":
                picture = new Image { Width = mascot.Kind == "emoji" ? 30 : 34, Height = mascot.Kind == "emoji" ? 30 : 34 };
                RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
                face.Children.Add(picture);
                break;
            default:
                icon = new TextBlock { FontFamily = UI.Icons, FontSize = 20, Foreground = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)) };
                face.Children.Add(icon);
                break;
        }
        Update(false, false, false);
        Unloaded += (_, _) => gif?.Stop();
    }

    /// Hover, press and capture moods. Hover tilts and capture hops are triggered separately via Wiggle/Hop.
    public void Update(bool hover, bool pressed, bool happy)
    {
        var wasPressed = this.pressed;
        (this.hover, this.pressed, this.happy) = (hover, pressed, happy);
        mood = happy || pressed ? "active" : hover ? "hover" : "idle";
        if (wasPressed != pressed)
        {
            UI.Spring(squish, ScaleTransform.ScaleXProperty, pressed ? 1.14 : 1, 0.2, 0.45);
            UI.Spring(squish, ScaleTransform.ScaleYProperty, pressed ? 0.84 : 1, 0.2, 0.45);
        }
        blob?.SetMood(mood);
        if (icon != null) icon.Text = happy ? "\uE73E" : "\uE77F";
        if (picture != null) ShowPicture();
    }

    string? Value() => mood switch
    {
        "active" => mascot.Active ?? mascot.Hover ?? mascot.Idle,
        "hover" => mascot.Hover ?? mascot.Idle,
        _ => mascot.Idle,
    };

    void ShowPicture()
    {
        var v = Value();
        if (v == null) { picture!.Source = null; return; }
        if (mascot.Kind == "emoji") { picture!.Source = Glyphs.Emoji(v, 30); return; }
        var path = Mascots.Url(v);
        var playing = hover || pressed || happy;
        if (Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase))
        {
            if (gif?.Path != path) { gif?.Stop(); gif = GifPlayer.Create(path, picture!); }
            if (gif != null) { if (playing) gif.Play(); else gif.Stop(); return; }
        }
        gif?.Stop();
        gif = null;
        picture!.Source = Glyphs.Load(path, 34);
    }

    public void Wiggle() => tilt.BeginAnimation(RotateTransform.AngleProperty, UI.Keyframes(
        (-13, 0.09, UI.Cubic), (10, 0.12, UI.Cubic), (-5, 0.10, UI.Cubic), (0, 0.12, UI.Cubic)));

    public void Hop() => hop.BeginAnimation(TranslateTransform.YProperty, UI.Keyframes(
        (-9, 0.14, new SpringEase(0.3, 0.85)), (0, 0.18, new SpringEase(0.4, 0.7)), (-3, 0.10, new SpringEase(0.4, 0.7)), (0, 0.14, new SpringEase(0.4, 0.7))));
}

/// The built-in creature: a glossy blob that blinks, perks up on hover and grins when it catches a copy.
sealed class BlobFace : Grid
{
    static readonly Random Rng = new();
    readonly ScaleTransform blink = new();
    readonly Grid eyes = new() { Margin = new Thickness(0, 0, 0, 0) };
    readonly Grid mouth = new();
    readonly DispatcherTimer blinker = new();
    readonly DispatcherTimer reopen = new() { Interval = TimeSpan.FromMilliseconds(110) };
    string mood = "";

    public BlobFace(Color color)
    {
        Width = 33;
        Height = 30;
        Children.Add(new Ellipse { Fill = new SolidColorBrush(color) });
        Children.Add(new Ellipse { Fill = new LinearGradientBrush(Color.FromArgb(115, 255, 255, 255), Colors.Transparent, new Point(0.5, 0), new Point(0.5, 0.5)) });
        Children.Add(new Ellipse { Fill = new LinearGradientBrush(Colors.Transparent, Color.FromArgb(46, 0, 0, 0), new Point(0.5, 0.5), new Point(0.5, 1)) });
        Children.Add(Center(eyes, 0, -2.5));
        var cheeks = new StackPanel { Orientation = Orientation.Horizontal };
        var pink = new SolidColorBrush(Color.FromArgb(140, 255, 115, 153));
        cheeks.Children.Add(new Ellipse { Width = 4.5, Height = 3.5, Fill = pink, Margin = new Thickness(0, 0, 16, 0) });
        cheeks.Children.Add(new Ellipse { Width = 4.5, Height = 3.5, Fill = pink });
        Children.Add(Center(cheeks, 0, 3.5));
        Children.Add(Center(mouth, 0, 6.5));
        blinker.Tick += (_, _) => Blink();
        reopen.Tick += (_, _) => { reopen.Stop(); blink.ScaleY = 1; Schedule(); };
        Loaded += (_, _) => Schedule();
        Unloaded += (_, _) => { blinker.Stop(); reopen.Stop(); };
        SetMood("idle");
    }

    static FrameworkElement Center(FrameworkElement e, double x, double y)
    {
        e.HorizontalAlignment = HorizontalAlignment.Center;
        e.VerticalAlignment = VerticalAlignment.Center;
        e.RenderTransform = new TranslateTransform(x, y);
        return e;
    }

    void Schedule()
    {
        blinker.Interval = TimeSpan.FromSeconds(2.5 + Rng.NextDouble() * 3);
        blinker.Start();
    }

    void Blink()
    {
        blinker.Stop();
        blink.ScaleY = 0.12;
        reopen.Start();
    }

    public void SetMood(string mood)
    {
        if (mood == this.mood) return;
        this.mood = mood;
        eyes.Children.Clear();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Eye());
        row.Children.Add(new Border { Width = 7 });
        row.Children.Add(Eye());
        eyes.Children.Add(row);

        mouth.Children.Clear();
        mouth.Children.Add(mood switch
        {
            "active" => UI.Arc(8, 4.5, up: false, closed: true, fill: Color.FromArgb(191, 0, 0, 0)),
            "hover" => new Ellipse { Width = 3.5, Height = 4, Fill = new SolidColorBrush(Color.FromArgb(179, 0, 0, 0)) },
            _ => UI.Arc(6, 2.5, up: false, stroke: Color.FromArgb(179, 0, 0, 0), thickness: 1.5),
        });
    }

    UIElement Eye()
    {
        if (mood == "active") return UI.Arc(6, 3.5, up: true, stroke: Color.FromArgb(204, 0, 0, 0), thickness: 1.8);
        var hover = mood == "hover";
        var eye = new Grid { Width = 6.5, Height = hover ? 9.5 : 8.5, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = blink, VerticalAlignment = VerticalAlignment.Center };
        eye.Children.Add(new Rectangle { RadiusX = 3.25, RadiusY = 3.25, Fill = Brushes.White });
        eye.Children.Add(Center(new Ellipse { Width = 4, Height = 4, Fill = new SolidColorBrush(Color.FromArgb(217, 0, 0, 0)) }, 0, hover ? -1 : 1));
        eye.Children.Add(Center(new Ellipse { Width = 1.4, Height = 1.4, Fill = Brushes.White }, 0.8, hover ? -1.8 : 0.2));
        return eye;
    }
}

/// Plays an animated GIF frame by frame; it only runs while the mascot is being interacted with.
sealed class GifPlayer
{
    public readonly string Path;
    readonly Image target;
    readonly BitmapSource[] frames;
    readonly TimeSpan[] delays;
    readonly DispatcherTimer timer = new();
    int index;

    GifPlayer(string path, Image target, BitmapSource[] frames, TimeSpan[] delays)
    {
        Path = path;
        this.target = target;
        this.frames = frames;
        this.delays = delays;
        target.Source = frames[0];
        timer.Tick += (_, _) => Advance();
    }

    static readonly Dictionary<string, (BitmapSource[] Frames, TimeSpan[] Delays)?> Decoded = new();

    /// Frames are decoded once per file version and rendered at the size they're shown (34 px at 3x), not full size.
    public static GifPlayer? Create(string path, Image target)
    {
        var key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
        if (!Decoded.TryGetValue(key, out var decoded)) Decoded[key] = decoded = Decode(path);
        return decoded is { } d ? new GifPlayer(path, target, d.Frames, d.Delays) : null;
    }

    static (BitmapSource[], TimeSpan[])? Decode(string path)
    {
        try
        {
            var decoder = Store.Decode(path, BitmapCreateOptions.PreservePixelFormat);
            if (decoder.Frames.Count == 0) return null;
            int w = decoder.Metadata?.GetQuery("/logscrdesc/Width") is ushort lw ? lw : decoder.Frames[0].PixelWidth;
            int h = decoder.Metadata?.GetQuery("/logscrdesc/Height") is ushort lh ? lh : decoder.Frames[0].PixelHeight;
            var scale = Math.Min(1.0, 34.0 * 3 / Math.Max(w, h));
            var frames = new List<BitmapSource>();
            var delays = new List<TimeSpan>();
            BitmapSource? previous = null;
            foreach (var f in decoder.Frames.Take(300))
            {
                var meta = (BitmapMetadata)f.Metadata;
                double Num(string q) => meta.GetQuery(q) is ushort u ? u : 0;
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.PushTransform(new ScaleTransform(scale, scale));
                    if (previous != null) dc.DrawImage(previous, new Rect(0, 0, w, h));
                    dc.DrawImage(f, new Rect(Num("/imgdesc/Left"), Num("/imgdesc/Top"), f.PixelWidth, f.PixelHeight));
                }
                var rendered = new RenderTargetBitmap((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale), 96, 96, PixelFormats.Pbgra32);
                rendered.Render(visual);
                rendered.Freeze();
                frames.Add(rendered);
                var delay = Num("/grctlext/Delay");
                delays.Add(TimeSpan.FromMilliseconds(Math.Max(20, delay * 10)));
                // Disposal 2 clears to the background and 3 restores the canvas from before this frame.
                previous = meta.GetQuery("/grctlext/Disposal") switch { (byte)2 => null, (byte)3 => previous, _ => rendered };
            }
            return (frames.ToArray(), delays.ToArray());
        }
        catch (Exception e) when (Store.Unreadable(e)) { return null; }
    }

    public void Play()
    {
        if (timer.IsEnabled || frames.Length < 2) return;
        timer.Interval = delays[index];
        timer.Start();
    }

    public void Stop() => timer.Stop();

    void Advance()
    {
        index = (index + 1) % frames.Length;
        target.Source = frames[index];
        timer.Interval = delays[index];
    }
}

/// WPF can't draw color emoji or SVG, so Direct2D renders them into bitmaps once and they're cached.
static unsafe class Glyphs
{
    const int Scale = 3;   // renders at 3x so it stays sharp up to 300% display scaling
    static readonly Dictionary<string, BitmapSource?> Cache = new();
    static IntPtr d2d, dwrite, wic;

    public static void ClearCache() => Cache.Clear();

    public static BitmapSource? Emoji(string text, double size) => Cached($"e|{size}|{text}", () => DrawEmoji(text, (int)(size * Scale)));

    /// Any image file the mascot folder may hold: SVG through Direct2D, everything else through WIC.
    public static BitmapSource? Load(string path, double size) => Cached($"f|{size}|{path}|{SafeStamp(path)}", () =>
    {
        if (!File.Exists(path)) return null;
        try
        {
            if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return DrawSvg(File.ReadAllBytes(path), (int)(size * Scale));
            return Store.DecodeFit(path, (int)(size * Scale)).Image;
        }
        catch (Exception e) when (Store.Unreadable(e)) { return null; }
    });

    static long SafeStamp(string path) { try { return File.GetLastWriteTimeUtc(path).Ticks; } catch (Exception) { return 0; } }

    static BitmapSource? Cached(string key, Func<BitmapSource?> make)
    {
        if (!Cache.TryGetValue(key, out var hit)) Cache[key] = hit = make();
        return hit;
    }

    [DllImport("d2d1.dll")] static extern int D2D1CreateFactory(int type, in Guid riid, IntPtr opts, out IntPtr factory);
    [DllImport("dwrite.dll")] static extern int DWriteCreateFactory(int type, in Guid riid, out IntPtr factory);
    [DllImport("ole32.dll")] internal static extern int CoCreateInstance(in Guid clsid, IntPtr outer, int ctx, in Guid riid, out IntPtr obj);
    [DllImport("shlwapi.dll")] static extern IntPtr SHCreateMemStream(byte[] data, uint size);

    static readonly Guid IID_ID2D1Factory = new("06152247-6f50-465a-9245-118bfd3b6007");
    static readonly Guid IID_IDWriteFactory = new("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
    static readonly Guid CLSID_WICImagingFactory = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
    static readonly Guid IID_IWICImagingFactory = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
    static readonly Guid IID_ID2D1DeviceContext5 = new("7836d248-68cc-4df6-b9e8-de991bf62eb7");
    static readonly Guid PBGRA = new("6fddc324-4e03-4bfe-b185-3d77768dc910");

    [StructLayout(LayoutKind.Sequential)] struct RtProps { public int type, format, alpha; public float dpiX, dpiY; public int usage, minLevel; }
    [StructLayout(LayoutKind.Sequential)] struct ColorF { public float r, g, b, a; }
    [StructLayout(LayoutKind.Sequential)] struct RectF { public float l, t, r, b; }
    [StructLayout(LayoutKind.Sequential)] struct SizeF { public float w, h; }

    // COM methods are called by vtable slot; the numbers are the method's position in the d2d1/dwrite/wincodec headers.
    static IntPtr Slot(IntPtr com, int i) => (*(IntPtr**)com)[i];

    /// A failed call is logged with its name, so a broken renderer shows up in errors.log instead of as a blank mascot.
    static bool Ok(int hr, string call)
    {
        if (hr < 0) Log.Error(new COMException(call + " failed", hr));
        return hr >= 0;
    }

    /// All three factories or none, so a half-made set is never used.
    static bool Ready()
    {
        if (wic != IntPtr.Zero) return true;
        if (!Ok(D2D1CreateFactory(0, IID_ID2D1Factory, IntPtr.Zero, out var f), "D2D1CreateFactory")) return false;
        if (!Ok(DWriteCreateFactory(0, IID_IDWriteFactory, out var w), "DWriteCreateFactory")) { Marshal.Release(f); return false; }
        if (!Ok(CoCreateInstance(CLSID_WICImagingFactory, IntPtr.Zero, 1, IID_IWICImagingFactory, out var i), "CoCreateInstance(WICImagingFactory)")) { Marshal.Release(f); Marshal.Release(w); return false; }
        (d2d, dwrite, wic) = (f, w, i);
        return true;
    }

    static BitmapSource? Draw(int px, Func<IntPtr, bool> paint)
    {
        if (!Ready()) return null;
        IntPtr bmp = IntPtr.Zero, rt = IntPtr.Zero;
        try
        {
            fixed (Guid* fmt = &PBGRA)
                if (!Ok(((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, Guid*, int, IntPtr*, int>)Slot(wic, 17))(wic, (uint)px, (uint)px, fmt, 1, &bmp), "IWICImagingFactory.CreateBitmap")) return null;
            var props = new RtProps { format = 87, alpha = 1 };   // B8G8R8A8, premultiplied
            if (!Ok(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, RtProps*, IntPtr*, int>)Slot(d2d, 13))(d2d, bmp, &props, &rt), "ID2D1Factory.CreateWicBitmapRenderTarget")) return null;
            ((delegate* unmanaged[Stdcall]<IntPtr, void>)Slot(rt, 48))(rt);   // BeginDraw
            var clear = new ColorF();
            ((delegate* unmanaged[Stdcall]<IntPtr, ColorF*, void>)Slot(rt, 47))(rt, &clear);
            var ok = paint(rt);
            if (!Ok(((delegate* unmanaged[Stdcall]<IntPtr, ulong*, ulong*, int>)Slot(rt, 49))(rt, null, null), "ID2D1RenderTarget.EndDraw") || !ok) return null;
            var pixels = new byte[px * px * 4];
            fixed (byte* p = pixels)
                if (!Ok(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, byte*, int>)Slot(bmp, 7))(bmp, IntPtr.Zero, (uint)px * 4, (uint)pixels.Length, p), "IWICBitmap.CopyPixels")) return null;
            var img = BitmapSource.Create(px, px, 96.0 * Scale, 96.0 * Scale, PixelFormats.Pbgra32, null, pixels, px * 4);
            img.Freeze();
            return img;
        }
        finally
        {
            if (rt != IntPtr.Zero) Marshal.Release(rt);
            if (bmp != IntPtr.Zero) Marshal.Release(bmp);
        }
    }

    static BitmapSource? DrawEmoji(string text, int px)
    {
        if (!Ready()) return null;
        IntPtr created;
        fixed (char* family = "Segoe UI Emoji") fixed (char* locale = "en-us")
            if (((delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr, int, int, int, float, char*, IntPtr*, int>)Slot(dwrite, 15))(
                dwrite, family, IntPtr.Zero, 400, 0, 5, px * 0.8f, locale, &created) is var hr && !Ok(hr, "IDWriteFactory.CreateTextFormat")) return null;
        var format = created;
        try
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(format, 3))(format, 2);   // centered
            ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(format, 4))(format, 2);
            return Draw(px, rt =>
            {
                var black = new ColorF { a = 1 };
                IntPtr brush;
                if (!Ok(((delegate* unmanaged[Stdcall]<IntPtr, ColorF*, IntPtr, IntPtr*, int>)Slot(rt, 8))(rt, &black, IntPtr.Zero, &brush), "ID2D1RenderTarget.CreateSolidColorBrush")) return false;
                var rect = new RectF { r = px, b = px };
                fixed (char* s = text)
                    ((delegate* unmanaged[Stdcall]<IntPtr, char*, uint, IntPtr, RectF*, IntPtr, int, int, void>)Slot(rt, 27))(
                        rt, s, (uint)text.Length, format, &rect, brush, 4 /* color font */, 0);
                Marshal.Release(brush);
                return true;
            });
        }
        finally { Marshal.Release(format); }
    }

    static BitmapSource? DrawSvg(byte[] svg, int px)
    {
        var stream = SHCreateMemStream(svg, (uint)svg.Length);
        if (stream == IntPtr.Zero) return null;
        try
        {
            return Draw(px, rt =>
            {
                var iid = IID_ID2D1DeviceContext5;
                if (!Ok(Marshal.QueryInterface(rt, ref iid, out var dc5), "QueryInterface(ID2D1DeviceContext5)")) return false;
                try
                {
                    IntPtr doc;
                    if (!Ok(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, SizeF, IntPtr*, int>)Slot(dc5, 115))(dc5, stream, new SizeF { w = px, h = px }, &doc), "ID2D1DeviceContext5.CreateSvgDocument")) return false;
                    ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)Slot(dc5, 116))(dc5, doc);
                    Marshal.Release(doc);
                    return true;
                }
                finally { Marshal.Release(dc5); }
            });
        }
        finally { Marshal.Release(stream); }
    }
}
