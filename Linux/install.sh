#!/bin/sh
# Installs ClipBubble for this user from the folder this script is in. Safe to run again to update.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
app="$HOME/.local/share/clipbubble/app"
ext="$HOME/.local/share/gnome-shell/extensions/clipbubble@krnl.studio"
mkdir -p "$app" "$HOME/.local/bin" "$HOME/.config/systemd/user" "$ext"
rm -rf "$app/clipbubble.new"
cp -R "$here/clipbubble" "$app/clipbubble.new"
rm -rf "$app/clipbubble"
mv "$app/clipbubble.new" "$app/clipbubble"
find "$app" -name __pycache__ -prune -exec rm -rf {} +
cat > "$HOME/.local/bin/clipbubble" <<SH
#!/bin/sh
PYTHONPATH="$app" exec python3 -P -m clipbubble "\$@"
SH
chmod 755 "$HOME/.local/bin/clipbubble"
cp "$here/clipbubble.service" "$HOME/.config/systemd/user/clipbubble.service"

# The extension loads only after logging in again; replacing its files never touches a running shell.
cp "$here/extension/clipbubble@krnl.studio/"* "$ext/"
if command -v gnome-extensions >/dev/null 2>&1; then
  gnome-extensions enable clipbubble@krnl.studio 2>/dev/null || \
    gsettings set org.gnome.shell enabled-extensions \
      "$(python3 -c 'import ast,subprocess;v=subprocess.run(["gsettings","get","org.gnome.shell","enabled-extensions"],capture_output=True,text=True).stdout.strip();l=ast.literal_eval(v.replace("@as ","")) if v else [];l+=[] if "clipbubble@krnl.studio" in l else ["clipbubble@krnl.studio"];print(l)')" 2>/dev/null || true
fi

systemctl --user daemon-reload
systemctl --user enable clipbubble.service >/dev/null 2>&1
systemctl --user restart clipbubble.service
if gdbus call --session -d studio.krnl.ClipBubble.Shell -o /studio/krnl/ClipBubble/Shell \
     -m studio.krnl.ClipBubble.Shell.Version >/dev/null 2>&1; then
  echo "CLIPBUBBLE_READY"
else
  echo "CLIPBUBBLE_NEEDS_RELOGIN"
fi
