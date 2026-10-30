#!/bin/sh
# Installs ClipBubble for this user from the folder this script is in. Safe to run again to update.
# The Mac's "Set up" button runs it over SSH and reads the CLIPBUBBLE_* line it prints last.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
if ! python3 -c 'import sys; sys.exit(sys.version_info < (3, 8))' 2>/dev/null; then
  echo "CLIPBUBBLE_NEEDS_PYTHON"
  exit 2
fi
app="$HOME/.local/share/clipbubble/app"
ext="$HOME/.local/share/gnome-shell/extensions/clipbubble@krnl.studio"
mkdir -p "$app" "$HOME/.local/bin"
rm -rf "$app/clipbubble.new"
cp -R "$here/clipbubble" "$app/clipbubble.new"
rm -rf "$app/clipbubble"
mv "$app/clipbubble.new" "$app/clipbubble"
cat > "$HOME/.local/bin/clipbubble" <<SH
#!/bin/sh
# From its own folder, so a json.py in the folder you're in can't stand in for Python's. -P would do that, but needs 3.11.
cd "$app" && PYTHONPATH="$app" exec python3 -m clipbubble "\$@"
SH
chmod 755 "$HOME/.local/bin/clipbubble"

# The extension loads only after logging in again; replacing its files never touches a running shell.
mkdir -p "$ext"
cp "$here/extension/clipbubble@krnl.studio/"* "$ext/"
if command -v gsettings >/dev/null 2>&1; then
  on=$(gsettings get org.gnome.shell enabled-extensions 2>/dev/null || echo "")
  case "$on" in
    *clipbubble@krnl.studio*) ;;
    "@as []"|"[]"|"") gsettings set org.gnome.shell enabled-extensions "['clipbubble@krnl.studio']" 2>/dev/null || true ;;
    *) gsettings set org.gnome.shell enabled-extensions "${on%]}, 'clipbubble@krnl.studio']" 2>/dev/null || true ;;
  esac
fi

if systemctl --user show-environment >/dev/null 2>&1; then
  mkdir -p "$HOME/.config/systemd/user"
  cp "$here/clipbubble.service" "$HOME/.config/systemd/user/clipbubble.service"
  systemctl --user daemon-reload
  systemctl --user enable clipbubble.service >/dev/null 2>&1
  systemctl --user restart clipbubble.service
else
  pkill -u "$(id -u)" -f 'python3( -P)? -m clipbubble$' 2>/dev/null || true
fi

if gdbus call --session -d studio.krnl.ClipBubble.Shell -o /studio/krnl/ClipBubble/Shell \
     -m studio.krnl.ClipBubble.Shell.Version >/dev/null 2>&1; then
  echo "CLIPBUBBLE_READY"
elif pgrep -u "$(id -u)" -x gnome-shell >/dev/null 2>&1; then
  version=$(gnome-shell --version 2>/dev/null | sed 's/[^0-9]*\([0-9]*\).*/\1/')
  if grep -q "\"$version\"" "$ext/metadata.json"; then echo "CLIPBUBBLE_NEEDS_RELOGIN"; else echo "CLIPBUBBLE_OLD_GNOME"; fi
else
  echo "CLIPBUBBLE_HEADLESS"
fi
