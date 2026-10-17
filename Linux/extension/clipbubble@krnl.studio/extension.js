// ClipBubble Shell: what the ClipBubble app can't do from outside GNOME on Wayland.
//
// GNOME has no clipboard-watching protocol for other processes, can't keep an app's window above others or out of
// Alt+Tab, and has no global hotkeys or synthetic paste for apps. This extension runs inside the compositor and exports
// those abilities on the session bus, and draws the bubble and its hover peek from images and rows the app sends.
// A new extension only loads after logging in again, so this stays generic and the app does the rest.

import Clutter from 'gi://Clutter';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Graphene from 'gi://Graphene';
import Meta from 'gi://Meta';
import Shell from 'gi://Shell';
import St from 'gi://St';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';

const VERSION = '1';
const IFACE = `<node>
  <interface name="studio.krnl.ClipBubble.Shell">
    <signal name="ClipboardChanged"><arg type="as" name="mimetypes"/><arg type="b" name="ours"/></signal>
    <signal name="HotKey"><arg type="s" name="accelerator"/></signal>
    <signal name="BubbleClicked"/>
    <signal name="BubbleMoved"><arg type="s" name="corner"/></signal>
    <signal name="PeekPicked"><arg type="i" name="index"/><arg type="b" name="copyOnly"/></signal>
    <method name="Version"><arg type="s" direction="out" name="version"/></method>
    <method name="Mimetypes"><arg type="as" direction="out" name="mimetypes"/></method>
    <method name="ReadClipboard"><arg type="s" direction="in" name="mimetype"/><arg type="ay" direction="out" name="data"/></method>
    <method name="SetClipboard"><arg type="s" direction="in" name="mimetype"/><arg type="ay" direction="in" name="data"/></method>
    <method name="Paste"><arg type="b" direction="in" name="shift"/></method>
    <method name="FocusedWindow"><arg type="s" direction="out" name="json"/></method>
    <method name="SetHotKey"><arg type="s" direction="in" name="accelerator"/><arg type="b" direction="out" name="ok"/></method>
    <method name="Place">
      <arg type="s" direction="in" name="title"/><arg type="i" direction="in" name="x"/><arg type="i" direction="in" name="y"/>
      <arg type="b" direction="in" name="above"/><arg type="b" direction="out" name="ok"/>
    </method>
    <method name="Activate"><arg type="s" direction="in" name="title"/><arg type="b" direction="out" name="ok"/></method>
    <method name="Pointer"><arg type="i" direction="out" name="x"/><arg type="i" direction="out" name="y"/></method>
    <method name="WorkArea">
      <arg type="i" direction="out" name="x"/><arg type="i" direction="out" name="y"/>
      <arg type="i" direction="out" name="width"/><arg type="i" direction="out" name="height"/>
    </method>
    <method name="SetBubble"><arg type="s" direction="in" name="json"/></method>
    <method name="BubbleRect">
      <arg type="i" direction="out" name="x"/><arg type="i" direction="out" name="y"/>
      <arg type="i" direction="out" name="width"/><arg type="i" direction="out" name="height"/>
    </method>
    <method name="Pulse"/>
    <method name="SetPeek"><arg type="s" direction="in" name="json"/></method>
  </interface>
</node>`;

const CLIPBOARD = Meta.SelectionType.SELECTION_CLIPBOARD;
const SIZE = 64, FACE = 46, MARGIN = 10, IDLE = 102;

export default class ClipBubbleShell extends Extension {
    enable() {
        this._selection = global.display.get_selection();
        this._ours = null;
        this._ownerId = this._selection.connect('owner-changed', (_sel, type, source) => {
            if (type !== CLIPBOARD)
                return;
            const mimes = this._selection.get_mimetypes(CLIPBOARD);
            this._emit('ClipboardChanged', '(asb)', [mimes, source !== null && source === this._ours]);
        });
        this._action = 0;
        this._accel = '';
        this._accelId = global.display.connect('accelerator-activated', (_d, action) => {
            if (action === this._action && this._action)
                this._emit('HotKey', '(s)', [this._accel]);
        });
        this._keyboard = Clutter.get_default_backend().get_default_seat()
            .create_virtual_device(Clutter.InputDeviceType.KEYBOARD_DEVICE);
        this._state = {visible: false, frames: {}, badge: 0, paused: false, corner: 'br', panelOpen: false};
        this._peekRows = [];
        this._timers = new Set();

        this._impl = Gio.DBusExportedObject.wrapJSObject(IFACE, this);
        this._impl.export(Gio.DBus.session, '/studio/krnl/ClipBubble/Shell');
        this._nameId = Gio.bus_own_name_on_connection(Gio.DBus.session, 'studio.krnl.ClipBubble.Shell',
            Gio.BusNameOwnerFlags.REPLACE, null, null);
        // The app went away: take its bubble down with it.
        this._watchId = Gio.bus_watch_name(Gio.BusType.SESSION, 'studio.krnl.ClipBubble.App', Gio.BusNameWatcherFlags.NONE,
            null, () => this._destroyBubble());
        this._workId = Main.layoutManager.connect('monitors-changed', () => this._snap(false));
    }

    disable() {
        this._impl?.unexport();
        this._impl = null;
        if (this._nameId)
            Gio.bus_unown_name(this._nameId);
        this._nameId = 0;
        if (this._watchId)
            Gio.bus_unwatch_name(this._watchId);
        this._watchId = 0;
        Main.layoutManager.disconnect(this._workId);
        this._selection?.disconnect(this._ownerId);
        this._selection = null;
        this._ours = null;
        global.display.disconnect(this._accelId);
        this._ungrab();
        this._keyboard = null;
        this._destroyBubble();
    }

    _emit(name, sig, args) {
        this._impl?.emit_signal(name, sig ? new GLib.Variant(sig, args) : null);
    }

    _after(ms, fn) {
        const id = GLib.timeout_add(GLib.PRIORITY_DEFAULT, ms, () => {
            this._timers.delete(id);
            fn();
            return GLib.SOURCE_REMOVE;
        });
        this._timers.add(id);
        return id;
    }

    _cancel(id) {
        if (id && this._timers.delete(id))
            GLib.source_remove(id);
    }

    // Clipboard, keys and windows

    Version() {
        return VERSION;
    }

    Mimetypes() {
        return this._selection.get_mimetypes(CLIPBOARD);
    }

    ReadClipboardAsync([mimetype], invocation) {
        const out = Gio.MemoryOutputStream.new_resizable();
        this._selection.transfer_async(CLIPBOARD, mimetype, -1, out, null, (sel, res) => {
            try {
                sel.transfer_finish(res);
                out.close(null);
                const bytes = out.steal_as_bytes();
                invocation.return_value(new GLib.Variant('(ay)', [bytes.get_data() ?? new Uint8Array()]));
            } catch (e) {
                invocation.return_error_literal(Gio.DBusError, Gio.DBusError.FAILED, String(e.message ?? e));
            }
        });
    }

    SetClipboard(mimetype, data) {
        const source = Meta.SelectionSourceMemory.new(mimetype, new GLib.Bytes(data));
        this._ours = source;
        this._selection.set_owner(CLIPBOARD, source);
    }

    Paste(shift) {
        const keys = [Clutter.KEY_Control_L, ...(shift ? [Clutter.KEY_Shift_L] : []), Clutter.KEY_v];
        let t = GLib.get_monotonic_time();
        for (const k of keys)
            this._keyboard.notify_keyval(t++, k, Clutter.KeyState.PRESSED);
        for (const k of keys.reverse())
            this._keyboard.notify_keyval(t++, k, Clutter.KeyState.RELEASED);
    }

    FocusedWindow() {
        const w = global.display.get_focus_window();
        if (!w)
            return '{}';
        return JSON.stringify({
            title: w.get_title() ?? '',
            app: w.get_gtk_application_id() ?? '',
            wmClass: w.get_wm_class() ?? '',
            pid: w.get_pid(),
        });
    }

    SetHotKey(accelerator) {
        this._ungrab();
        if (!accelerator)
            return true;
        const action = global.display.grab_accelerator(accelerator, Meta.KeyBindingFlags.NONE);
        if (action === Meta.KeyBindingAction.NONE)
            return false;
        this._action = action;
        this._accel = accelerator;
        Main.wm.allowKeybinding(Meta.external_binding_name_for_action(action), Shell.ActionMode.ALL);
        return true;
    }

    _ungrab() {
        if (this._action)
            global.display.ungrab_accelerator(this._action);
        this._action = 0;
        this._accel = '';
    }

    _window(title) {
        return global.get_window_actors().map(a => a.get_meta_window()).find(m => m?.get_title() === title) ?? null;
    }

    Place(title, x, y, above) {
        const w = this._window(title);
        if (!w)
            return false;
        w.move_frame(true, x, y);
        // GNOME centers a window when it first shows (center-new-windows), after this call if it isn't shown yet.
        if (!w.get_compositor_private()?.visible) {
            const id = w.connect('shown', () => {
                w.disconnect(id);
                w.move_frame(true, x, y);
            });
        }
        if (above && !w.is_above())
            w.make_above();
        if (above && !w.is_on_all_workspaces())
            w.stick();
        return true;
    }

    Activate(title) {
        const w = this._window(title);
        if (!w)
            return false;
        Main.activateWindow(w);
        return true;
    }

    Pointer() {
        const [x, y] = global.get_pointer();
        return [x, y];
    }

    _area() {
        return Main.layoutManager.getWorkAreaForMonitor(this._monitor ?? Main.layoutManager.primaryIndex);
    }

    WorkArea() {
        const r = this._area();
        return [r.x, r.y, r.width, r.height];
    }

    // Bubble: the app sends images for each mood; this side does the motion so it never waits on the app.

    SetBubble(json) {
        let s;
        try {
            s = JSON.parse(json);
        } catch {
            return;
        }
        const was = this._state.corner;
        Object.assign(this._state, s);
        if (!this._state.visible) {
            this._destroyBubble();
            return;
        }
        if (!this._root)
            this._buildBubble();
        if (this._state.panelOpen)
            this._hidePeek();
        this._render();
        if (was !== this._state.corner || !this._placed)
            this._snap(false);
        this._rest();
    }

    BubbleRect() {
        if (!this._root)
            return [0, 0, 0, 0];
        const [x, y] = this._root.get_transformed_position();
        return [Math.round(x), Math.round(y), SIZE, SIZE];
    }

    Pulse() {
        if (!this._root)
            return;
        this._happy = true;
        this._render();
        this._root.ease({opacity: 255, duration: 120});
        this._face.ease({translation_y: -9, duration: 140, mode: Clutter.AnimationMode.EASE_OUT_QUAD,
            onComplete: () => this._face?.ease({translation_y: 0, duration: 260, mode: Clutter.AnimationMode.EASE_OUT_BOUNCE})});
        this._cancel(this._happyId);
        this._happyId = this._after(1100, () => {
            this._happy = false;
            this._render();
            this._rest();
        });
    }

    _buildBubble() {
        this._root = new St.Widget({width: SIZE, height: SIZE, reactive: true, track_hover: true, opacity: IDLE});
        this._disc = new St.Widget({
            x: (SIZE - FACE) / 2, y: (SIZE - FACE) / 2, width: FACE, height: FACE,
            style: `border-radius: ${FACE / 2}px; background-color: rgba(24,24,28,0.94);` +
                'border: 1px solid rgba(255,255,255,0.28); box-shadow: 0 2px 8px rgba(0,0,0,0.45);',
        });
        this._face = new St.Bin({width: 34, height: 34, x: (SIZE - 34) / 2, y: (SIZE - 34) / 2, pivot_point: new Graphene.Point({x: 0.5, y: 1})});
        this._icon = new St.Icon({icon_size: 34});
        this._emoji = new St.Label({style: 'font-size: 24px;', x_align: Clutter.ActorAlign.CENTER, y_align: Clutter.ActorAlign.CENTER});
        this._badge = new St.Label({
            x: SIZE - 24, y: 2, visible: false,
            style: 'background-color: #6E62F5; color: white; font-size: 10px; font-weight: bold; border-radius: 9px;' +
                'padding: 1px 5px; border: 1px solid rgba(255,255,255,0.35);',
        });
        this._pause = new St.Label({text: '⏸', x: 2, y: SIZE - 20, visible: false,
            style: 'color: white; background-color: #F59E0B; border-radius: 8px; font-size: 9px; padding: 1px 4px;'});
        for (const a of [this._disc, this._face, this._badge, this._pause])
            this._root.add_child(a);
        Main.layoutManager.addChrome(this._root, {trackFullscreen: true});

        this._root.connect('notify::hover', () => {
            const on = this._root.hover;
            if (on && !this._dragging) {
                this._face.ease({rotation_angle_z: -13, duration: 90, mode: Clutter.AnimationMode.EASE_OUT_QUAD,
                    onComplete: () => this._face?.ease({rotation_angle_z: 0, duration: 340, mode: Clutter.AnimationMode.EASE_OUT_ELASTIC})});
            }
            this._render();
            this._hoverChanged();
        });
        this._root.connect('button-press-event', (_a, e) => this._press(e));
        this._blinkLoop();
    }

    _destroyBubble() {
        for (const id of this._timers)
            GLib.source_remove(id);
        this._timers.clear();
        if (this._stageId)
            global.stage.disconnect(this._stageId);
        this._stageId = 0;
        this._hidePeek();
        this._root?.destroy();
        this._root = this._disc = this._face = this._icon = this._emoji = this._badge = this._pause = null;
        this._shown = null;
        this._placed = false;
        if (this._state)
            this._state.visible = false;
    }

    _mood() {
        if (this._happy || this._pressed)
            return 'active';
        if (this._blink)
            return 'blink';
        return this._root?.hover ? 'hover' : 'idle';
    }

    _render() {
        if (!this._root)
            return;
        const f = this._state.frames ?? {};
        const mood = this._mood();
        const path = f[mood] ?? (mood === 'active' ? f.hover : null) ?? f.idle;
        if (path && path !== this._shown) {
            this._shown = path;
            // A frame is a picture's path, or text: and an emoji to draw.
            if (path.startsWith('text:')) {
                this._emoji.text = path.slice(5);
                this._face.set_child(this._emoji);
            } else {
                this._icon.gicon = Gio.FileIcon.new(Gio.File.new_for_path(path));
                this._face.set_child(this._icon);
            }
        }
        const n = this._state.badge ?? 0;
        this._badge.visible = n > 0;
        this._badge.text = String(Math.min(n, 99));
        this._pause.visible = !!this._state.paused;
    }

    _blinkLoop() {
        this._after(2500 + Math.random() * 3000, () => {
            if (!this._root)
                return;
            if (this._state.frames?.blink && this._mood() === 'idle') {
                this._blink = true;
                this._render();
                this._after(110, () => {
                    this._blink = false;
                    this._render();
                });
            }
            this._blinkLoop();
        });
    }

    _rest() {
        if (!this._root)
            return;
        const awake = this._root.hover || this._peek || this._happy || this._dragging || this._state.panelOpen;
        this._root.ease({opacity: awake ? 255 : IDLE, duration: 160});
    }

    _corner() {
        const a = this._area();
        const [x, y] = this._root.get_position();
        return (y + SIZE / 2 > a.y + a.height / 2 ? 'b' : 't') + (x + SIZE / 2 > a.x + a.width / 2 ? 'r' : 'l');
    }

    _snap(animated) {
        if (!this._root)
            return;
        if (animated) {
            this._monitor = Main.layoutManager.findIndexForActor(this._root);
            this._state.corner = this._corner();
        }
        const a = this._area(), c = this._state.corner;
        const x = c.endsWith('l') ? a.x + MARGIN : a.x + a.width - SIZE - MARGIN;
        const y = c.startsWith('t') ? a.y + MARGIN : a.y + a.height - SIZE - MARGIN;
        this._placed = true;
        if (!animated) {
            this._root.set_position(x, y);
            return;
        }
        this._root.ease({x, y, duration: 320, mode: Clutter.AnimationMode.EASE_OUT_BACK, onComplete: () => this._rest()});
        this._emit('BubbleMoved', '(s)', [c]);
    }

    _press(e) {
        if (e.get_button() !== Clutter.BUTTON_PRIMARY)
            return Clutter.EVENT_PROPAGATE;
        const [px, py] = e.get_coords();
        const [bx, by] = this._root.get_position();
        this._pressed = true;
        this._render();
        this._face.ease({scale_x: 1.14, scale_y: 0.84, duration: 90});
        this._stageId = global.stage.connect('captured-event', (_s, ev) => {
            const t = ev.type();
            if (t === Clutter.EventType.MOTION) {
                const [mx, my] = ev.get_coords();
                if (!this._dragging && Math.hypot(mx - px, my - py) > 4) {
                    this._dragging = true;
                    this._hidePeek();
                    this._root.opacity = 255;
                }
                if (this._dragging)
                    this._root.set_position(bx + mx - px, by + my - py);
                return Clutter.EVENT_STOP;
            }
            if (t === Clutter.EventType.BUTTON_RELEASE) {
                global.stage.disconnect(this._stageId);
                this._stageId = 0;
                this._pressed = false;
                this._face.ease({scale_x: 1, scale_y: 1, duration: 260, mode: Clutter.AnimationMode.EASE_OUT_ELASTIC});
                this._render();
                if (this._dragging) {
                    this._dragging = false;
                    this._snap(true);
                } else {
                    this._hidePeek();
                    this._emit('BubbleClicked', null, null);
                }
                return Clutter.EVENT_STOP;
            }
            return Clutter.EVENT_PROPAGATE;
        });
        return Clutter.EVENT_STOP;
    }

    // Peek: hovering the bubble shows the last few items; a click pastes one.

    SetPeek(json) {
        try {
            this._peekRows = JSON.parse(json).rows ?? [];
            this._peekHint = JSON.parse(json).hint ?? '';
        } catch {
            return;
        }
        if (this._peek)
            this._buildPeek();
    }

    _hoverChanged() {
        this._cancel(this._peekId);
        const want = (this._root?.hover || this._peekBox?.hover) && !this._dragging && !this._state.panelOpen;
        if (want && this._peek)
            return;
        this._peekId = this._after(want ? 300 : 200, () => (want ? this._showPeek() : this._hidePeek()));
        this._rest();
    }

    _showPeek() {
        if (this._peek || !this._root || this._state.panelOpen || !(this._root.hover || this._peekBox?.hover))
            return;
        this._peek = true;
        this._buildPeek();
        this._rest();
    }

    _buildPeek() {
        this._peekBox?.destroy();
        const box = new St.BoxLayout({
            vertical: true, reactive: true, track_hover: true, width: 300,
            style: 'background-color: rgba(28,28,32,0.97); border-radius: 14px; border: 1px solid rgba(255,255,255,0.1);' +
                'padding: 6px; box-shadow: 0 4px 18px rgba(0,0,0,0.5);',
        });
        const head = new St.BoxLayout({style: 'padding: 4px 8px 3px 8px;'});
        head.add_child(new St.Label({text: 'RECENT', x_expand: true,
            style: 'font-size: 10px; font-weight: bold; color: rgba(255,255,255,0.4); letter-spacing: 0.6px;'}));
        head.add_child(new St.Label({text: this._peekHint ?? '', style: 'font-size: 10px; color: rgba(255,255,255,0.4);'}));
        box.add_child(head);
        if (!this._peekRows.length) {
            box.add_child(new St.Label({text: 'Nothing copied yet', style: 'font-size: 12px; color: rgba(255,255,255,0.6); padding: 8px;'}));
        }
        this._peekRows.forEach((r, i) => {
            const row = new St.Button({reactive: true, track_hover: true, x_expand: true,
                style_class: 'cb-peek-row', style: 'border-radius: 9px; padding: 6px 8px;'});
            row.connect('notify::hover', () => {
                row.style = `border-radius: 9px; padding: 6px 8px; background-color: rgba(255,255,255,${row.hover ? 0.08 : 0});`;
            });
            const line = new St.BoxLayout({x_expand: true, style: 'spacing: 10px;'});
            if (r.icon) {
                line.add_child(new St.Icon({gicon: Gio.FileIcon.new(Gio.File.new_for_path(r.icon)), icon_size: 30,
                    style: 'border-radius: 6px;'}));
            }
            const text = new St.BoxLayout({vertical: true, x_expand: true});
            const title = new St.Label({text: r.title ?? '', style: 'font-size: 13px; font-weight: 500; color: white;'});
            title.clutter_text.ellipsize = 3;
            const sub = new St.Label({text: r.subtitle ?? '', style: 'font-size: 11px; color: rgba(255,255,255,0.55);'});
            sub.clutter_text.ellipsize = 3;
            text.add_child(title);
            text.add_child(sub);
            line.add_child(text);
            row.set_child(line);
            row.connect('clicked', () => {
                const alt = (global.get_pointer()[2] & Clutter.ModifierType.MOD1_MASK) !== 0;
                this._hidePeek();
                this._emit('PeekPicked', '(ib)', [i, alt]);
            });
            box.add_child(row);
        });
        box.connect('notify::hover', () => this._hoverChanged());
        this._peekBox = box;
        Main.layoutManager.addChrome(box);
        const [w, h] = box.get_preferred_size().slice(2);
        const a = this._area(), [bx, by] = this._root.get_position(), c = this._state.corner;
        let x = c.endsWith('r') ? bx - w - 4 : bx + SIZE + 4;
        let y = c.startsWith('b') ? by + SIZE - 9 - h : by + 9;
        x = Math.min(Math.max(x, a.x + 8), a.x + a.width - w - 8);
        y = Math.min(Math.max(y, a.y + 8), a.y + a.height - h - 8);
        box.set_position(Math.round(x), Math.round(y));
        box.opacity = 0;
        box.ease({opacity: 255, duration: 120});
    }

    _hidePeek() {
        this._cancel(this._peekId);
        this._peek = false;
        this._peekBox?.destroy();
        this._peekBox = null;
        this._rest();
    }
}
