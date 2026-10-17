#!/bin/zsh
# Builds ClipBubble.app, installs it to ~/Applications and relaunches it.
set -e
cd "$(dirname "$0")"
APP=ClipBubble.app
rm -rf $APP && mkdir -p $APP/Contents/MacOS $APP/Contents/Resources
cp Info.plist $APP/Contents/
cp Assets/AppIcon.icns $APP/Contents/Resources/
# The Linux app ships inside, so Settings can set up Linux hosts over SSH.
mkdir -p $APP/Contents/Resources/linux
cp -R Linux/clipbubble Linux/extension Linux/install.sh Linux/clipbubble.service $APP/Contents/Resources/linux/
find $APP/Contents/Resources/linux -name __pycache__ -prune -exec rm -rf {} +
sed -n 's/^VERSION = "\(.*\)"/\1/p' Linux/clipbubble/engine.py > $APP/Contents/Resources/linux/VERSION
swiftc -O -swift-version 5 Sources/*.swift -o $APP/Contents/MacOS/ClipBubble
codesign --force -s - $APP
# Quit the running app, but not `ClipBubble --mcp` servers that agents are using.
pkill -f 'ClipBubble.app/Contents/MacOS/ClipBubble$' || true
rm -rf ~/Applications/$APP && cp -R $APP ~/Applications/
sleep 1  # let the old copy finish quitting, or open refuses
open ~/Applications/$APP
