<p align="center">
  <img src="Assets/clipbubble-app-icon.png" width="128" alt="ClipBubble icon">
</p>

<h1 align="center">ClipBubble</h1>

<p align="center">
  <b>A tiny bubble that remembers everything you copy.</b><br>
  Clipboard history for macOS and Windows with a mascot, a magnet, and an MCP server for your AI agents.
</p>

<p align="center">
  <a href="https://github.com/rohit3a/clipbubble/releases/latest">Download</a> ·
  <a href="https://clipbubble.krnl.studio">Website</a> ·
  <a href="#mcp-for-ai-agents">MCP</a> ·
  <a href="#mascots">Mascots</a>
</p>

<p align="center">
  <img src="Assets/press/panel.png" width="420" alt="The ClipBubble panel">
  &nbsp;&nbsp;
  <img src="Assets/press/mascot-blob-active.png" width="120" alt="The blob mascot">
</p>

## What it does

- **A floating bubble with a pet.** It stays translucent in a screen corner until you need it. Drag it anywhere and it magnet-snaps to the nearest corner. When it catches a copy, the mascot hops and a badge counts up.
- **Remembers 7 days of everything.** Text, images, files, **every screenshot** (even the ones you never copied), **recent downloads**, and copies from your iPhone through Universal Clipboard.
- **You choose what shows up.** Settings (⚙ in the panel) has a switch for each kind. Off means it isn't saved and is hidden everywhere, your agents included.
- **Paste without hunting.** Press <kbd>⌃⌥V</kbd> anywhere, type to search, and press <kbd>↩</kbd>: it pastes straight into the app you were in. Or drag any item out into Slack, Figma or a browser upload.
- **Hover to peek** at your last three copies; one click pastes.
- **Built-in MCP server.** Claude Code, Codex, Cursor and Claude Desktop can read your clipboard history (and actually see the images), put things on your clipboard, and design new mascots for you.
- **Private and tiny.** Native Swift with zero dependencies. Everything lives in a local SQLite file, and nothing leaves your Mac apart from an update check with GitHub, which you can turn off. It skips password-manager copies and deletes everything after 7 days. It uses about 30 MB of RAM and 0% CPU at idle.

## Install

**Download:** grab `ClipBubble.dmg` from [Releases](https://github.com/rohit3a/clipbubble/releases/latest), open it, and drag `ClipBubble.app` to Applications. It's signed and notarized by Apple, so it opens like any other app.

**Or build from source** (needs the Xcode command line tools):

```bash
git clone https://github.com/rohit3a/clipbubble && cd clipbubble && ./build.sh
```

`build.sh` compiles the app, installs it to `~/Applications` and launches it. Requires macOS 14 or later.

**Updates:** ClipBubble checks GitHub for a new version every few hours. When there is one, a dot appears on ⚙, and **Settings → General → Update** installs it and reopens the app. `ClipBubble --update` does the same from a terminal.

**Permissions:** Settings (⚙ in the panel) → **Folders & permissions** shows each one live, with a switch and a **Fix** button that opens the right page in System Settings:
- **Screenshots:** ClipBubble watches the folder from your macOS screenshot setting (⌘⇧5 → Options → Save to), and notices when you change it. You can also pick a different folder.
- **Recent downloads:** your Downloads folder.
- **Auto-paste:** needs Accessibility access. Without it (or with it switched off), picking an item just copies it.

## Windows

The Windows version lives in [`Windows/`](Windows). It's a separate C# app with the same panel, mascots, history database and MCP tools. It needs Windows 10 22H2 or Windows 11 and nothing else. It runs on the .NET Framework that ships with Windows, as one 220 KB `ClipBubble.exe`.

**Download:** grab `ClipBubble.exe` from [Releases](https://github.com/rohit3a/clipbubble/releases/latest), put it anywhere and run it. It isn't signed, so the first launch shows SmartScreen: click **More info** → **Run anyway**. After that it updates itself from **Settings → General → Update**.

**Or build from source** (needs the [.NET SDK](https://dotnet.microsoft.com/download)):

```powershell
git clone https://github.com/rohit3a/clipbubble; cd clipbubble
powershell -ExecutionPolicy Bypass -File Windows\build.ps1
```

Windows blocks unsigned scripts by default, which is why the command allows this one. `build.ps1` builds the app, installs it to `%LOCALAPPDATA%\Programs\ClipBubble` and launches it. To run the tests, run `dotnet build Windows\Tests\ClipBubble.Tests.csproj -c Release`, then `Windows\Tests\bin\Release\net48\ClipBubble.Tests.exe`.

What's different on Windows:
- <kbd>Ctrl</kbd><kbd>Shift</kbd><kbd>Space</kbd> opens the panel. You can change it in Settings. In the panel, <kbd>Ctrl</kbd> replaces <kbd>⌘</kbd> and <kbd>Alt</kbd> replaces <kbd>⌥</kbd>, and <kbd>Ctrl</kbd><kbd>Delete</kbd> deletes, because <kbd>Ctrl</kbd><kbd>⌫</kbd> deletes a word in the search box.
- Screenshots come from `Pictures\Screenshots`, where <kbd>Win</kbd><kbd>PrtScn</kbd> and Snipping Tool save them, or from a folder you pick.
- There's no iPhone filter, because Windows has no Universal Clipboard.
- Auto-paste needs no permission. Apps running as administrator only get a copy, because Windows blocks typing into them.
- Copies marked as secret by password managers (`ExcludeClipboardContentFromMonitorProcessing`) are never stored.
- History lives in `%LOCALAPPDATA%\ClipBubble`.
- Sync over SSH uses the OpenSSH client built into Windows. For other machines to link to this PC, turn on the OpenSSH Server feature and keep `ClipBubble.exe` in `%LOCALAPPDATA%\Programs\ClipBubble`, where `build.ps1` puts it.

For MCP, use the path of your `ClipBubble.exe`, or copy the setup prompt from Settings:

```powershell
claude mcp add --scope user clipbubble -- "$env:LOCALAPPDATA\Programs\ClipBubble\ClipBubble.exe" --mcp
```

## Keys

| Key | Action |
| --- | --- |
| <kbd>⌃⌥V</kbd> | Open or close the panel from anywhere |
| <kbd>↑</kbd> <kbd>↓</kbd> | Move the selection |
| <kbd>↩</kbd> | Paste into the app you were in |
| <kbd>⌥↩</kbd> | Copy only |
| <kbd>⌘1</kbd>–<kbd>⌘9</kbd> | Paste one of the first nine items |
| <kbd>⌘Y</kbd> | Full-size preview |
| <kbd>⌘⌫</kbd> | Delete from history |
| <kbd>Tab</kbd> | Next filter (All, Text, Images, Files, Screenshots, Downloads, iPhone) |
| <kbd>Esc</kbd> | Close the preview, clear the search, or close the panel |

## Mascots

<p>
  <img src="Assets/press/mascot-blob-idle.png" width="64">
  <img src="Assets/press/mascot-mint-hover.png" width="64">
  <img src="Assets/press/mascot-peach-active.png" width="64">
  <img src="Assets/press/mascot-sky-idle.png" width="64">
  <img src="Assets/press/mascot-cat-active.png" width="64">
  <img src="Assets/press/mascot-frog-idle.png" width="64">
  <img src="Assets/press/mascot-ghost-idle.png" width="64">
  <img src="Assets/press/mascot-robot-idle.png" width="64">
</p>

The blob family blinks, wiggles when you hover, squishes when you click and grins when it catches a copy. Switch mascots from the ⚙ menu in the panel, or add your own image (PNG, GIF, JPG or SVG; GIFs play while you interact with it).

Custom mascots are small JSON files in `~/Library/Application Support/ClipBubble/mascots/`:

```json
{ "id": "pinky", "name": "Pinky", "kind": "blob", "color": "#FF6AD5" }
{ "id": "owl", "name": "Owl", "kind": "emoji", "idle": "🦉", "active": "🤩" }
{ "id": "fox", "name": "Fox", "kind": "image", "idle": "fox-idle.svg", "hover": "fox-wave.gif" }
```

`hover` and `active` (shown on click or when a copy lands) fall back to `idle`.

## MCP for AI agents

The app binary doubles as a stdio MCP server. The easiest way to connect an agent: open the panel, click ⚙ → **AI agents (MCP)** → **Copy setup prompt**, and paste it into Claude Code, Codex, Cursor or any other agent. It registers ClipBubble itself, using the exact path of your copy of the app, and checks that it works.

Or register it by hand (adjust the path if the app is in `/Applications`):

```bash
claude mcp add --scope user clipbubble -- ~/Applications/ClipBubble.app/Contents/MacOS/ClipBubble --mcp
```

For Codex it's `codex mcp add clipbubble -- <same path> --mcp`. For Claude Desktop, Cursor or Windsurf, add `"clipbubble": { "command": "<same path>", "args": ["--mcp"] }` under `mcpServers`.

| Tool | What it does |
| --- | --- |
| `clipboard_history` | Recent items, filterable by kind, source (`iPhone`, `Screenshot`, `Downloads`, an app name), search text and time |
| `clipboard_get` | One item in full, including the image itself so the model can see it |
| `clipboard_set` | Put text or a history item on your clipboard |
| `mascot_list` / `mascot_set` | See and switch mascots |
| `mascot_add` / `mascot_remove` | Create mascots from a colour, emoji, image files or inline SVG |

Try asking your agent: *"Make my ClipBubble mascot a tiny pixel-art fox"* or *"What was that error I copied an hour ago?"*

## How it works

- `Sources/Capture.swift` polls the clipboard every 0.4 s and waits for it to settle first, so paste-through tools like Wispr Flow (which paste and then restore your clipboard) aren't disturbed and don't clutter history. Copies marked as concealed or transient by password managers are never stored.
- `Sources/Store.swift` keeps everything in `~/Library/Application Support/ClipBubble/clips.sqlite` (WAL mode, so the MCP server can read while the app writes). Copied images are stored as PNGs next to it.
- `Sources/UI.swift` has the bubble, the panel and the peek: non-activating panels, so your app keeps focus and ↩ can paste into it.
- `Sources/MCP.swift` is a small JSON-RPC server over stdio, with no SDK.
- `Sources/Mascot.swift` draws the mascots with plain SwiftUI shapes. When nobody is interacting, the only thing that animates is a blink every few seconds.

## License

[MIT](LICENSE). Made by [KRNL](https://krnl.studio).
