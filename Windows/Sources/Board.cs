using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ClipBubble;

// The parts of the store that need WPF: pictures and the clipboard. Kept apart so the rest builds anywhere .NET runs.

static partial class Store
{
    /// Same pixels, whatever the encoding: a screenshot tool's clipboard copy and the file it saved.
    public static bool SamePicture(Func<Stream> a, Func<Stream> b)
    {
        try
        {
            static (int, int) Size(Func<Stream> open)
            {
                using var s = open();
                var frame = BitmapDecoder.Create(s, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
                return (frame.PixelWidth, frame.PixelHeight);
            }
            static byte[] Pixels(Func<Stream> open)
            {
                using var s = open();
                var frame = new FormatConvertedBitmap(BitmapDecoder.Create(s, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0],
                    System.Windows.Media.PixelFormats.Bgra32, null, 0);
                var px = new byte[frame.PixelWidth * frame.PixelHeight * 4];
                frame.CopyPixels(px, frame.PixelWidth * 4, 0);
                return px;
            }
            // Comparing sizes reads only the headers; the full decode runs only for a likely match.
            return Size(a) == Size(b) && Pixels(a).SequenceEqual(Pixels(b));
        }
        catch (Exception e) when (Unreadable(e)) { return false; }
    }

    /// Decodes at most `px` on the longer side, so a huge picture shown small doesn't hold its full size in memory.
    /// Also returns the full size.
    public static (BitmapSource Image, Size Full) DecodeFit(string path, int px)
    {
        using var file = OpenShared(path);
        var frame = BitmapDecoder.Create(file, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
        file.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = file;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (frame.PixelWidth >= frame.PixelHeight) image.DecodePixelWidth = Math.Min(px, frame.PixelWidth);
        else image.DecodePixelHeight = Math.Min(px, frame.PixelHeight);
        image.EndInit();
        image.Freeze();
        return (image, new Size(frame.PixelWidth, frame.PixelHeight));
    }

    /// Decodes through a stream that's closed right after. Decoding straight from a path leaves the file locked
    /// until the next garbage collection when it fails, so a broken picture couldn't be deleted or renamed.
    public static BitmapDecoder Decode(string path, BitmapCreateOptions options = BitmapCreateOptions.IgnoreColorProfile)
    {
        using var s = OpenShared(path);
        return BitmapDecoder.Create(s, options, BitmapCacheOption.OnLoad);
    }

    public static byte[] Png(BitmapSource image)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(image as BitmapFrame ?? BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}

sealed partial class Db
{
    // Win+PrtScn and Snipping Tool both copy a screenshot and save it. The saved file's row stands for both,
    // whichever of the two ClipBubble sees first (the Mac never does both, so this is Windows-only).
    const double ScreenshotWindow = 10;

    /// A screenshot file just arrived: drop the copy of the same picture recorded moments ago.
    public void DropCopiesOf(string screenshot)
    {
        var copies = Select("WHERE kind = 'image' AND created >= ?", Now - ScreenshotWindow);
        if (copies.Count == 0 || !Store.IsImagePath(screenshot)) return;   // a screen recording is never a copied picture
        Store.WaitUntilWritten(screenshot);
        foreach (var c in copies)
            if (c.File is { } f && Store.SamePicture(() => Store.OpenShared(f), () => Store.OpenShared(screenshot))) Delete(c.Id);
    }

    /// A copied picture is a screenshot that was saved moments ago.
    public bool IsRecentScreenshot(byte[] png) =>
        Select("WHERE source = 'Screenshot' AND created >= ?", Now - ScreenshotWindow).Where(c => Store.IsImagePath(c.Text))
            .Any(c => { Store.WaitUntilWritten(c.Text); return Store.SamePicture(() => new MemoryStream(png), () => Store.OpenShared(c.Text)); });
}

/// Writes clips back to the Windows clipboard.
static class Board
{
    /// Password managers and clipboard tools mark copies that history apps must skip.
    public const string ExcludeFormat = "ExcludeClipboardContentFromMonitorProcessing";

    public static byte[]? PngData(string path)
    {
        try
        {
            if (Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)) return System.IO.File.ReadAllBytes(path);
            return Store.Png(Store.Decode(path, BitmapCreateOptions.None).Frames[0]);
        }
        catch (Exception e) when (Store.Unreadable(e)) { return null; }
    }

    /// Null once it's on the clipboard, otherwise why it isn't. Pasting then would paste whatever was there before.
    /// Of several files, only the ones still there go on.
    public static string? Write(Clip c)
    {
        var files = c.Paths.Where(p => System.IO.File.Exists(p) || Directory.Exists(p)).ToList();
        var data = c.Kind switch
        {
            ClipKind.Text => TextData(c.Text),
            ClipKind.Image => c.File is { } f && PngData(f) is { } png ? ImageData(png) : null,
            _ => files.Count > 0 ? FilesData(files) : null,
        };
        if (data == null) return c.Kind == ClipKind.Image ? "Its image file is gone or can't be read." : "Its files are gone.";
        try { Set(data); return null; }
        catch (ExternalException) { return "Another app is using the clipboard. Try again in a moment."; }   // past WPF's own retries
    }

    /// `transient` marks it so clipboard managers (ClipBubble included) and Windows' own history don't save it.
    public static void WriteText(string s, bool transient = false) => Set(TextData(s, transient));

    public static void WriteFiles(IReadOnlyList<string> paths) => Set(FilesData(paths));

    static DataObject TextData(string s, bool transient = false)
    {
        var data = new DataObject();
        data.SetText(s, TextDataFormat.UnicodeText);
        if (transient)
        {
            data.SetData(ExcludeFormat, new MemoryStream(new byte[4]));
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
        }
        return data;
    }

    /// A single image file goes on as both the file and its pixels, so it pastes into Explorer and into chat/web apps.
    static DataObject FilesData(IReadOnlyList<string> paths)
    {
        var data = (paths.Count == 1 && Store.IsImagePath(paths[0]) && PngData(paths[0]) is { } png ? ImageData(png) : null) ?? new DataObject();
        var list = new StringCollection();
        list.AddRange(paths.ToArray());
        data.SetFileDropList(list);
        return data;
    }

    /// PNG keeps transparency for apps that read it; the bitmap is for everything else. Null when the pixels can't
    /// be read (a file still being written).
    static DataObject? ImageData(byte[] png)
    {
        try
        {
            var frame = BitmapFrame.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var data = new DataObject();
            data.SetData("PNG", new MemoryStream(png));
            data.SetImage(frame);
            return data;
        }
        catch (Exception e) when (Store.Unreadable(e)) { return null; }
    }

    /// WPF already retries for about a second if another app holds the clipboard. `copy` keeps the data after we exit.
    static void Set(DataObject data) => Clipboard.SetDataObject(data, true);
}
