#!/bin/zsh
# Builds and signs ClipBubble.app at the path given (default ./ClipBubble.app). Signs with your Developer ID when the
# keychain has one, so macOS keeps ClipBubble's permissions across builds and updates; otherwise signs ad hoc.
set -e
cd "$(dirname "$0")"
APP=${1:-ClipBubble.app}
rm -rf $APP && mkdir -p $APP/Contents/MacOS $APP/Contents/Resources
cp Info.plist $APP/Contents/
cp Assets/AppIcon.icns $APP/Contents/Resources/
# The Linux app ships inside, so Settings can set up Linux hosts over SSH.
mkdir -p $APP/Contents/Resources/linux
cp -R Linux/clipbubble Linux/extension Linux/install.sh Linux/clipbubble.service $APP/Contents/Resources/linux/
find $APP/Contents/Resources/linux -name __pycache__ -prune -exec rm -rf {} +
sed -n 's/^VERSION = "\(.*\)"/\1/p' Linux/clipbubble/engine.py > $APP/Contents/Resources/linux/VERSION
swiftc -O -swift-version 5 Sources/*.swift -o $APP/Contents/MacOS/ClipBubble
ID=$(security find-identity -v -p codesigning | sed -n 's/.*"\(Developer ID Application: .*\)"/\1/p' | head -1)
if [[ -n $ID ]]; then
  # Notarization needs the hardened runtime and a secure timestamp; release.sh asks for the timestamp.
  codesign --force --options runtime ${CLIPBUBBLE_TIMESTAMP:+--timestamp} -s "$ID" $APP
  echo "Signed with $ID"
else
  codesign --force -s - $APP
  echo "Signed ad hoc (no Developer ID in the keychain): macOS will ask for ClipBubble's permissions again for this build"
fi
