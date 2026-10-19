#!/bin/zsh
# Builds ClipBubble.app, installs it to ~/Applications and relaunches it.
set -e
cd "$(dirname "$0")"
./package.sh ClipBubble.app
# Quit the running app, but not `ClipBubble --mcp` servers that agents are using.
pkill -f 'ClipBubble.app/Contents/MacOS/ClipBubble$' || true
rm -rf ~/Applications/ClipBubble.app && cp -R ClipBubble.app ~/Applications/
sleep 1  # let the old copy finish quitting, or open refuses
open ~/Applications/ClipBubble.app
