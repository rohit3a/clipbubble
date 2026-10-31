import AppKit

/// Minimal stdio MCP server (`ClipBubble --mcp`) so agents can read and set the clipboard history.
enum MCP {
    static let db = DB()

    static let instructions = """
        ClipBubble keeps the user's last 7 days of clipboard history on their Mac: text, images, files, screenshots, \
        downloads and copies from their iPhone. When they mention something they copied, a screenshot they took, a file \
        they downloaded or "that thing from earlier", look it up with clipboard_history (use query, kind or source to \
        narrow it) and read it in full with clipboard_get, which returns images so you can see them. Use clipboard_set to \
        hand the user something to paste. The mascot_* tools change the little character in their ClipBubble bubble.
        """

    /// A prompt the user pastes into any agent so it registers this server itself.
    static func setupPrompt(binary: String) -> String {
        """
        Connect yourself to ClipBubble, my Mac clipboard history app, via its MCP server.

        It's a local stdio MCP server: command "\(binary)", argument "--mcp". No API key or network.

        Register it with whatever agent you are:
        - Claude Code: claude mcp add --scope user clipbubble -- "\(binary)" --mcp
        - Codex: codex mcp add clipbubble -- "\(binary)" --mcp
        - Cursor, Claude Desktop, Windsurf or anything with a JSON MCP config: add
          "clipbubble": { "command": "\(binary)", "args": ["--mcp"] } under "mcpServers"

        Then check it works by calling clipboard_history with limit 3 (restart or reload MCP servers first if you need to), \
        and tell me what you found. Tools: clipboard_history, clipboard_get, clipboard_set, mascot_list, mascot_set, \
        mascot_add, mascot_remove.
        """
    }

    static let tools: [[String: Any]] = [
        [
            "name": "clipboard_history",
            "description": "List the user's recent clipboard items (last 7 days, newest first): text, images and files copied on this Mac or from their iPhone, plus screenshots. Use limit 1 for the latest copy. Returns ids for clipboard_get.",
            "inputSchema": ["type": "object", "properties": [
                "limit": ["type": "integer", "description": "Max items, default 20, max 100"],
                "kind": ["type": "string", "enum": ["text", "image", "file"]],
                "query": ["type": "string", "description": "Substring to search text, file paths and source app"],
                "source": ["type": "string", "description": "Source app name, \"iPhone\" for items copied on iPhone, \"Screenshot\" / \"Downloads\", or a linked device's name (e.g. \"ROG\") for everything synced from it"],
                "since_hours": ["type": "number", "description": "Only items from the last N hours"],
            ] as [String: Any]] as [String: Any],
        ],
        [
            "name": "clipboard_get",
            "description": "Get one clipboard item in full by id: the complete text, or the image itself (and its file path), or the file paths.",
            "inputSchema": ["type": "object", "properties": ["id": ["type": "integer"]], "required": ["id"]] as [String: Any],
        ],
        [
            "name": "clipboard_set",
            "description": "Put content on the user's Mac clipboard so they can paste it: either new text, or an existing history item by id.",
            "inputSchema": ["type": "object", "properties": [
                "text": ["type": "string"], "id": ["type": "integer"],
            ]] as [String: Any],
        ],
        [
            "name": "mascot_list",
            "description": "List the mascots available for the user's ClipBubble bubble (built-in and custom) and which one is active.",
            "inputSchema": ["type": "object", "properties": [String: Any]()] as [String: Any],
        ],
        [
            "name": "mascot_set",
            "description": "Switch the bubble's mascot by id (from mascot_list). The bubble updates live.",
            "inputSchema": ["type": "object", "properties": ["id": ["type": "string"]], "required": ["id"]] as [String: Any],
        ],
        [
            "name": "mascot_add",
            "description": """
                Create a custom mascot for the bubble and (by default) switch to it. It shows at about 34pt inside a dark \
                circle; it tilts when hovered, squishes when clicked and hops when something is copied. Three kinds: \
                "blob" = the built-in creature recolored (pass color as hex); "emoji" = idle/hover/active are emoji; \
                "image" = idle/hover/active are file paths (PNG, GIF, JPG, SVG) or inline SVG markup. For images use a \
                square canvas with a transparent background; animated GIFs play only while hovered or clicked. \
                hover and active fall back to idle.
                """,
            "inputSchema": ["type": "object", "properties": [
                "name": ["type": "string"],
                "kind": ["type": "string", "enum": ["blob", "emoji", "image"]],
                "color": ["type": "string", "description": "Hex color for blob, like #FF6AD5"],
                "idle": ["type": "string", "description": "Emoji, image path, or inline SVG"],
                "hover": ["type": "string"],
                "active": ["type": "string", "description": "Shown when clicked or when a copy is caught"],
                "activate": ["type": "boolean", "description": "Switch to it now (default true)"],
            ] as [String: Any], "required": ["name", "kind"]] as [String: Any],
        ],
        [
            "name": "mascot_remove",
            "description": "Delete a custom mascot by id. Built-in mascots can't be removed.",
            "inputSchema": ["type": "object", "properties": ["id": ["type": "string"]], "required": ["id"]] as [String: Any],
        ],
    ]

    static func run() {
        while let line = readLine() {
            guard let msg = (try? JSONSerialization.jsonObject(with: Data(line.utf8))) as? [String: Any] else {
                if !line.trimmingCharacters(in: .whitespaces).isEmpty {
                    send(["jsonrpc": "2.0", "id": NSNull(), "error": ["code": -32700, "message": "Parse error"]])
                }
                continue
            }
            guard let id = msg["id"] else { continue }  // notifications need no reply
            let params = msg["params"] as? [String: Any] ?? [:]
            switch msg["method"] as? String {
            case "initialize":
                reply(id, ["protocolVersion": params["protocolVersion"] as? String ?? "2025-06-18",
                           "capabilities": ["tools": [String: Any]()],
                           "serverInfo": ["name": "clipbubble", "version": "1.6.2"],
                           "instructions": instructions])
            case "tools/list": reply(id, ["tools": tools])
            case "tools/call": reply(id, call(params["name"] as? String ?? "", params["arguments"] as? [String: Any] ?? [:]))
            case "ping": reply(id, [String: Any]())
            default: send(["jsonrpc": "2.0", "id": id, "error": ["code": -32601, "message": "Method not found"]])
            }
        }
    }

    static func call(_ name: String, _ a: [String: Any]) -> [String: Any] {
        let fmt = DateFormatter()
        fmt.dateFormat = "yyyy-MM-dd HH:mm"
        let devices = db.deviceNames()
        func header(_ c: Clip) -> String {
            let from = c.origin.isEmpty ? "" : " · from \(devices[c.origin] ?? "another device")" + (c.remote ? " (not downloaded yet)" : "")
            return "#\(c.id) · \(c.kind) · \(c.source)\(from) · \(fmt.string(from: c.created))"
        }
        func int(_ k: String) -> Int64? { (a[k] as? NSNumber)?.int64Value }

        switch name {
        case "clipboard_history":
            let clips = db.clips(limit: max(1, min(Int(int("limit") ?? 20), 100)), kind: a["kind"] as? String,
                                 source: a["source"] as? String, query: a["query"] as? String,
                                 sinceHours: (a["since_hours"] as? NSNumber)?.doubleValue)
            if clips.isEmpty { return text("No clipboard items match.") }
            return text(clips.map { c in
                let body = c.kind == "image" ? (c.file ?? "") : String(c.text.prefix(300)) + (c.text.count > 300 ? "…" : "")
                return header(c) + "\n" + body
            }.joined(separator: "\n\n"))
        case "clipboard_get":
            guard let id = int("id"), let c = db.clip(id: id) else { return text("No item with that id.", error: true) }
            // Very long text is cut so one item can't flood the agent's context.
            let limit = 200_000
            let body = c.kind == "image" ? (c.file ?? "")
                : c.text.count > limit ? String(c.text.prefix(limit)) + "\n… (cut: \(c.text.count) characters in all)" : c.text
            var content: [[String: Any]] = [["type": "text", "text": header(c) + "\n" + body]]
            if let p = c.imagePath, let b64 = imageBase64(p) { content.append(["type": "image", "data": b64, "mimeType": "image/png"]) }
            return ["content": content]
        case "clipboard_set":
            if let t = a["text"] as? String { Board.writeText(t); return text("Copied text to the clipboard.") }
            guard let id = int("id"), let c = db.clip(id: id) else { return text("Pass text or a valid id.", error: true) }
            guard !c.remote, Board.write(c) else { return text("Item #\(id)'s image or files aren't on this Mac.", error: true) }
            return text("Copied item #\(id) to the clipboard.")
        case "mascot_list":
            let active = Mascots.activeID(), custom = Set(Mascots.custom().map(\.id))
            return text(Mascots.all().map { m in
                var line = "\(m.id) · \(m.name) · \(m.kind)\(custom.contains(m.id) ? " · custom" : "")"
                if let c = m.color { line += " · \(c)" }
                if m.kind == "emoji", let e = m.idle { line += " · \(e)" }
                return line + (m.id == active ? "  ← active" : "")
            }.joined(separator: "\n"))
        case "mascot_set":
            guard let id = a["id"] as? String, Mascots.all().contains(where: { $0.id == id }) else {
                return text("No mascot with that id. Use mascot_list.", error: true)
            }
            Mascots.setActive(id)
            return text("Mascot set to \(id).")
        case "mascot_add":
            do {
                var states: [String: String] = [:]
                for k in ["idle", "hover", "active"] { if let v = a[k] as? String { states[k] = v } }
                let m = try Mascots.add(name: a["name"] as? String ?? "Mascot", kind: a["kind"] as? String ?? "",
                                        color: a["color"] as? String, states: states)
                if a["activate"] as? Bool ?? true { Mascots.setActive(m.id) }
                return text("Added mascot \(m.id).")
            } catch {
                return text(error.localizedDescription, error: true)
            }
        case "mascot_remove":
            guard let id = a["id"] as? String, Mascots.remove(id) else { return text("No custom mascot with that id.", error: true) }
            return text("Removed mascot \(id).")
        default:
            return text("Unknown tool \(name).", error: true)
        }
    }

    /// Downscales to 1568px max so large screenshots fit in a model's context.
    static func imageBase64(_ path: String) -> String? {
        let opts = [kCGImageSourceCreateThumbnailFromImageAlways: true, kCGImageSourceThumbnailMaxPixelSize: 1568,
                    kCGImageSourceCreateThumbnailWithTransform: true] as CFDictionary
        guard let src = CGImageSourceCreateWithURL(URL(fileURLWithPath: path) as CFURL, nil),
              let cg = CGImageSourceCreateThumbnailAtIndex(src, 0, opts) else { return nil }
        return NSBitmapImageRep(cgImage: cg).representation(using: .png, properties: [:])?.base64EncodedString()
    }

    static func text(_ s: String, error: Bool = false) -> [String: Any] {
        ["content": [["type": "text", "text": s]], "isError": error]
    }

    static func reply(_ id: Any, _ result: [String: Any]) { send(["jsonrpc": "2.0", "id": id, "result": result]) }

    static func send(_ obj: [String: Any]) {
        guard var d = try? JSONSerialization.data(withJSONObject: obj) else { return }
        d.append(0x0A)
        FileHandle.standardOutput.write(d)
    }
}
