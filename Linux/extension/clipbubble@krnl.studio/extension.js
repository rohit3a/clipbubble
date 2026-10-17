// ClipBubble Shell: what the ClipBubble app can't do from outside GNOME on Wayland.
//
// GNOME has no clipboard-watching protocol for other processes, can't let an app place its own window, and has no
// global hotkeys or synthetic paste for apps. This extension runs inside the compositor and exports those abilities on
// the session bus. A new extension only loads after logging in again, so this stays small and the app does the rest.

import Clutter from 'gi://Clutter';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Meta from 'gi://Meta';
import Shell from 'gi://Shell';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';

const VERSION = '1';
const IFACE = `<node>
  <interface name="studio.krnl.ClipBubble.Shell">
    <signal name="ClipboardChanged"><arg type="as" name="mimetypes"/><arg type="b" name="ours"/></signal>
    <signal name="HotKey"><arg type="s" name="accelerator"/></signal>
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
    <method name="Pointer"><arg type="i" direction="out" name="x"/><arg type="i" direction="out" name="y"/></method>
    <method name="WorkArea">
      <arg type="i" direction="out" name="x"/><arg type="i" direction="out" name="y"/>
      <arg type="i" direction="out" name="width"/><arg type="i" direction="out" name="height"/>
    </method>
  </interface>
</node>`;

const CLIPBOARD = Meta.SelectionType.SELECTION_CLIPBOARD;

export default class ClipBubbleShell extends Extension {
    enable() {
        this._selection = global.display.get_selection();
        this._ours = null;
        this._ownerId = this._selection.connect('owner-changed', (_sel, type, source) => {
            if (type !== CLIPBOARD)
                return;
            const mimes = this._selection.get_mimetypes(CLIPBOARD);
            this._impl?.emit_signal('ClipboardChanged', new GLib.Variant('(asb)', [mimes, source !== null && source === this._ours]));
        });
        this._action = 0;
        this._accel = '';
        this._accelId = global.display.connect('accelerator-activated', (_d, action) => {
            if (action === this._action && this._action)
                this._impl?.emit_signal('HotKey', new GLib.Variant('(s)', [this._accel]));
        });
        this._keyboard = Clutter.get_default_backend().get_default_seat()
            .create_virtual_device(Clutter.InputDeviceType.KEYBOARD_DEVICE);

        this._impl = Gio.DBusExportedObject.wrapJSObject(IFACE, this);
        this._impl.export(Gio.DBus.session, '/studio/krnl/ClipBubble/Shell');
        this._nameId = Gio.bus_own_name_on_connection(Gio.DBus.session, 'studio.krnl.ClipBubble.Shell',
            Gio.BusNameOwnerFlags.REPLACE, null, null);
    }

    disable() {
        this._impl?.unexport();
        this._impl = null;
        if (this._nameId)
            Gio.bus_unown_name(this._nameId);
        this._nameId = 0;
        this._selection?.disconnect(this._ownerId);
        this._selection = null;
        this._ours = null;
        global.display.disconnect(this._accelId);
        this._ungrab();
        this._keyboard = null;
    }

    Version() {
        return VERSION;
    }

    Mimetypes() {
        return this._selection.get_mimetypes(CLIPBOARD);
    }

    ReadClipboardAsync([mimetype], invocation) {
        const out = new Gio.MemoryOutputStream();
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

    Place(title, x, y, above) {
        const w = global.get_window_actors().map(a => a.get_meta_window()).find(m => m?.get_title() === title);
        if (!w)
            return false;
        w.move_frame(true, x, y);
        if (above && !w.is_above())
            w.make_above();
        if (above && !w.is_on_all_workspaces())
            w.stick();
        return true;
    }

    Pointer() {
        const [x, y] = global.get_pointer();
        return [x, y];
    }

    WorkArea() {
        const r = Main.layoutManager.getWorkAreaForMonitor(global.display.get_current_monitor());
        return [r.x, r.y, r.width, r.height];
    }
}
