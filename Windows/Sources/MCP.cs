using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipBubble;

/// Minimal stdio MCP server (`ClipBubble.exe --mcp`) so agents can read and set the clipboard history.
static class Mcp
{
    const string Instructions =
        "ClipBubble keeps the user's last 7 days of clipboard history on their PC: text, images, files, screenshots " +
        "and downloads, plus everything synced from their other machines (their Mac and others). When they mention something they copied, a screenshot they took, a file they downloaded or " +
        "\"that thing from earlier\", look it up with clipboard_history (use query, kind or source to narrow it) and read " +
        "it in full with clipboard_get, which returns images so you can see them. Use clipboard_set to hand the user " +
        "something to paste. The mascot_* tools change the little character in their ClipBubble bubble.";

    /// A prompt the user pastes into any agent so it registers this server itself.
    public static string SetupPrompt(string binary)
    {
        var json = binary.Replace("\\", "\\\\");
        return $@"Connect yourself to ClipBubble, my Windows clipboard history app, via its MCP server.

It's a local stdio MCP server: command ""{binary}"", argument ""--mcp"". No API key or network.

Register it with whatever agent you are:
- Claude Code: claude mcp add --scope user clipbubble -- ""{binary}"" --mcp
- Codex: codex mcp add clipbubble -- ""{binary}"" --mcp
- Cursor, Claude Desktop, Windsurf or anything with a JSON MCP config: add
  ""clipbubble"": {{ ""command"": ""{json}"", ""args"": [""--mcp""] }} under ""mcpServers""

Then check it works by calling clipboard_history with limit 3 (restart or reload MCP servers first if you need to), " +
            "and tell me what you found. Tools: clipboard_history, clipboard_get, clipboard_set, mascot_list, mascot_set, " +
            "mascot_add, mascot_remove.";
    }

    static Dictionary<string, object> Obj(params (string Key, object Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);
    static Dictionary<string, object> Schema(params (string Key, object Value)[] props) => Obj(("type", "object"), ("properties", Obj(props)));
    static Dictionary<string, object> Str(string? description = null) => description == null ? Obj(("type", "string")) : Obj(("type", "string"), ("description", description));

    static readonly object[] Tools =
    {
        Obj(("name", "clipboard_history"),
            ("description", "List the user's recent clipboard items (last 7 days, newest first): text, images and files copied on this PC, plus screenshots, downloads and everything synced from their other machines. Use limit 1 for the latest copy. Returns ids for clipboard_get."),
            ("inputSchema", Schema(
                ("limit", Obj(("type", "integer"), ("description", "Max items, default 20, max 100"))),
                ("kind", Obj(("type", "string"), ("enum", new[] { "text", "image", "file" }))),
                ("query", Str("Substring to search text, file paths and source app")),
                ("source", Str("Source app name, \"Screenshot\" / \"Downloads\", or a linked device's name (e.g. \"Mac\") for everything synced from it")),
                ("since_hours", Obj(("type", "number"), ("description", "Only items from the last N hours")))))),
        Obj(("name", "clipboard_get"),
            ("description", "Get one clipboard item in full by id: the complete text, or the image itself (and its file path), or the file paths."),
            ("inputSchema", With(Schema(("id", Obj(("type", "integer")))), "required", new[] { "id" }))),
        Obj(("name", "clipboard_set"),
            ("description", "Put content on the user's clipboard so they can paste it: either new text, or an existing history item by id."),
            ("inputSchema", Schema(("text", Str()), ("id", Obj(("type", "integer")))))),
        Obj(("name", "mascot_list"),
            ("description", "List the mascots available for the user's ClipBubble bubble (built-in and custom) and which one is active."),
            ("inputSchema", Schema())),
        Obj(("name", "mascot_set"),
            ("description", "Switch the bubble's mascot by id (from mascot_list). The bubble updates live."),
            ("inputSchema", With(Schema(("id", Str())), "required", new[] { "id" }))),
        Obj(("name", "mascot_add"),
            ("description",
                "Create a custom mascot for the bubble and (by default) switch to it. It shows at about 34px inside a dark " +
                "circle; it tilts when hovered, squishes when clicked and hops when something is copied. Three kinds: " +
                "\"blob\" = the built-in creature recolored (pass color as hex); \"emoji\" = idle/hover/active are emoji; " +
                "\"image\" = idle/hover/active are file paths (PNG, GIF, JPG, SVG) or inline SVG markup. For images use a " +
                "square canvas with a transparent background; animated GIFs play only while hovered or clicked. " +
                "hover and active fall back to idle."),
            ("inputSchema", With(Schema(
                ("name", Str()),
                ("kind", Obj(("type", "string"), ("enum", new[] { "blob", "emoji", "image" }))),
                ("color", Str("Hex color for blob, like #FF6AD5")),
                ("idle", Str("Emoji, image path, or inline SVG")),
                ("hover", Str()),
                ("active", Str("Shown when clicked or when a copy is caught")),
                ("activate", Obj(("type", "boolean"), ("description", "Switch to it now (default true)")))), "required", new[] { "name", "kind" }))),
        Obj(("name", "mascot_remove"),
            ("description", "Delete a custom mascot by id. Built-in mascots can't be removed."),
            ("inputSchema", With(Schema(("id", Str())), "required", new[] { "id" }))),
    };

    static Dictionary<string, object> With(Dictionary<string, object> d, string key, object value) { d[key] = value; return d; }

    public static void Run() => Run(Console.OpenStandardInput(), Console.OpenStandardOutput());

    public static void Run(Stream input, Stream output)
    {
        using var db = new Db();
        var reader = new StreamReader(input, new UTF8Encoding(false));
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Trim().Length == 0) continue;
            Dictionary<string, object?>? msg;
            try { msg = Store.Json.Deserialize<Dictionary<string, object?>>(line); }
            catch (Exception) { msg = null; }   // however it fails to parse
            if (msg == null)   // not a JSON object (`null` parses, but isn't one either): say so and keep serving
            {
                Send(output, Obj(("jsonrpc", "2.0"), ("id", null!), ("error", Obj(("code", -32700), ("message", "Parse error")))));
                continue;
            }
            if (!msg.TryGetValue("id", out var id) || id == null) continue;   // notifications need no reply
            // One failing request (a locked database, a clipboard another app won't let go of) never stops the server.
            try { Handle(db, output, id, msg); }
            catch (Exception e)
            {
                Log.Error(e);
                Send(output, Obj(("jsonrpc", "2.0"), ("id", id), ("error", Obj(("code", -32603), ("message", e.Message)))));
            }
        }
    }

    static void Handle(Db db, Stream output, object id, Dictionary<string, object?> msg)
    {
        var p = msg.TryGetValue("params", out var raw) && raw is Dictionary<string, object?> d ? d : new Dictionary<string, object?>();
        switch (msg.TryGetValue("method", out var m) ? m as string : null)
        {
            case "initialize":
                Reply(output, id, Obj(("protocolVersion", p.TryGetValue("protocolVersion", out var v) && v is string s ? s : "2025-06-18"),
                    ("capabilities", Obj(("tools", Obj()))),
                    ("serverInfo", Obj(("name", "clipbubble"), ("version", "1.1"))),
                    ("instructions", Instructions)));
                break;
            case "tools/list": Reply(output, id, Obj(("tools", Tools))); break;
            case "tools/call":
                var args = p.TryGetValue("arguments", out var a) && a is Dictionary<string, object?> ad ? ad : new Dictionary<string, object?>();
                Reply(output, id, Call(db, p.TryGetValue("name", out var n) ? n as string ?? "" : "", args));
                break;
            case "ping": Reply(output, id, Obj()); break;
            default: Send(output, Obj(("jsonrpc", "2.0"), ("id", id), ("error", Obj(("code", -32601), ("message", "Method not found"))))); break;
        }
    }

    /// Out-of-range numbers count as missing rather than overflowing.
    static long? Int(object? o) => o switch
    {
        int i => i,
        long l => l,
        decimal m when m >= long.MinValue && m <= long.MaxValue => (long)m,
        double d when d >= long.MinValue && d <= long.MaxValue => (long)d,
        _ => null,
    };
    static double? Number(object? o) => o switch { int i => i, long l => l, decimal m => (double)m, double d => d, _ => null };

    /// The first `n` characters as the user sees them (emoji and accents count once), without walking huge clips.
    static string Prefix(string s, int n)
    {
        var e = StringInfo.GetTextElementEnumerator(s);
        for (var count = 0; e.MoveNext(); count++)
            if (count == n) return s.Substring(0, e.ElementIndex) + "…";
        return s;
    }

    public static Dictionary<string, object> Call(Db db, string name, Dictionary<string, object?> a)
    {
        Dictionary<string, string>? devices = null;
        string From(Clip c) => c.Origin.Length == 0 ? ""
            : " · from " + ((devices ??= db.DeviceNames()).TryGetValue(c.Origin, out var n) ? n : "another device") + (c.Remote ? " (not downloaded yet)" : "");
        string Header(Clip c) => $"#{c.Id} · {c.Kind.Name()} · {c.Source} · {c.Created.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}{From(c)}";
        string? S(string k) => a.TryGetValue(k, out var v) ? v as string : null;

        switch (name)
        {
            case "clipboard_history":
                Prefs.Reload();
                var clips = db.Clips(limit: (int)Math.Max(1, Math.Min(Int(a.GetValueOrDefault("limit")) ?? 20, 100)), kind: Store.ParseKind(S("kind")),
                                     source: S("source"), query: S("query"), sinceHours: Number(a.GetValueOrDefault("since_hours")));
                if (clips.Count == 0) return Text("No clipboard items match.");
                return Text(string.Join("\n\n", clips.Select(c => Header(c) + "\n" + (c.Kind == ClipKind.Image ? c.File ?? "" : Prefix(c.Text, 300)))));
            case "clipboard_get":
            {
                Prefs.Reload();
                if (Int(a.GetValueOrDefault("id")) is not { } id || db.Clip(id) is not { } c) return Text("No item with that id.", error: true);
                // Very long text is cut so one item can't flood the agent's context.
                const int limit = 200_000;
                var body = c.Kind == ClipKind.Image ? c.File ?? ""
                    : c.Text.Length > limit ? Store.Head(c.Text, limit) + $"\n… (cut: {c.Text.Length} characters in all)" : c.Text;
                var content = new List<object> { Obj(("type", "text"), ("text", Header(c) + "\n" + body)) };
                if (!c.Remote && c.ImagePath is { } p && ImageBase64(p) is { } b64) content.Add(Obj(("type", "image"), ("data", b64), ("mimeType", "image/png")));
                return Obj(("content", content));
            }
            case "clipboard_set":
            {
                Prefs.Reload();
                if (S("text") is { } t) { Board.WriteText(t); return Text("Copied text to the clipboard."); }
                if (Int(a.GetValueOrDefault("id")) is not { } id || db.Clip(id) is not { } c) return Text("Pass text or a valid id.", error: true);
                if (c.Remote) return Text($"Item #{id} is still on another machine. Get it in ClipBubble first.", error: true);
                return Board.Write(c) is { } why ? Text($"Couldn't copy item #{id}. {why}", error: true) : Text($"Copied item #{id} to the clipboard.");
            }
            case "mascot_list":
            {
                var active = Mascots.ActiveId();
                var customs = Mascots.Custom();
                var custom = new HashSet<string>(customs.Select(m => m.Id));
                return Text(string.Join("\n", Mascots.Builtins.Concat(customs).Select(m =>
                {
                    var line = $"{m.Id} · {m.Name} · {m.Kind}{(custom.Contains(m.Id) ? " · custom" : "")}";
                    if (m.Color != null) line += $" · {m.Color}";
                    if (m.Kind == "emoji" && m.Idle != null) line += $" · {m.Idle}";
                    return line + (m.Id == active ? "  ← active" : "");
                })));
            }
            case "mascot_set":
                if (S("id") is not { } setId || !Mascots.All().Any(m => m.Id == setId)) return Text("No mascot with that id. Use mascot_list.", error: true);
                Mascots.SetActive(setId);
                return Text($"Mascot set to {setId}.");
            case "mascot_add":
                try
                {
                    var states = new Dictionary<string, string>();
                    foreach (var k in new[] { "idle", "hover", "active" }) if (S(k) is { } v) states[k] = v;
                    var m = Mascots.Add(S("name") ?? "Mascot", S("kind") ?? "", S("color"), states);
                    if (a.GetValueOrDefault("activate") as bool? ?? true) Mascots.SetActive(m.Id);
                    return Text($"Added mascot {m.Id}.");
                }
                catch (Exception e) when (e is MascotException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    return Text(e.Message, error: true);
                }
            case "mascot_remove":
                if (S("id") is not { } removeId || !Mascots.Remove(removeId)) return Text("No custom mascot with that id.", error: true);
                return Text($"Removed mascot {removeId}.");
            default:
                return Text($"Unknown tool {name}.", error: true);
        }
    }

    /// Downscales to 1568px max so large screenshots fit in a model's context.
    public static string? ImageBase64(string path)
    {
        try
        {
            return Convert.ToBase64String(Store.Png(Store.DecodeFit(path, 1568).Image));
        }
        catch (Exception e) when (Store.Unreadable(e))
        {
            return null;
        }
    }

    static Dictionary<string, object> Text(string s, bool error = false) =>
        Obj(("content", new object[] { Obj(("type", "text"), ("text", s)) }), ("isError", error));

    static void Reply(Stream output, object id, Dictionary<string, object> result) => Send(output, Obj(("jsonrpc", "2.0"), ("id", id), ("result", result)));

    static void Send(Stream output, Dictionary<string, object> obj)
    {
        var bytes = Encoding.UTF8.GetBytes(Store.Json.Serialize(obj) + "\n");
        output.Write(bytes, 0, bytes.Length);
        output.Flush();
    }
}

static class DictionaryExtensions
{
    public static TValue? GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> d, TKey key) where TKey : notnull =>
        d.TryGetValue(key, out var v) ? v : default;
}
