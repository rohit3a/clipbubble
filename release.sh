#!/bin/zsh
# Builds the release downloads into dist/: ClipBubble.zip (what the in-app updater installs) and ClipBubble.dmg.
# With a Developer ID and a notarytool profile named "clipbubble", both are notarized and stapled. Set up the profile
# once with: xcrun notarytool store-credentials clipbubble --apple-id <you> --team-id <team>
set -e
cd "$(dirname "$0")"
# macOS ties permissions like Accessibility to the signature: an ad hoc release would make every user grant them again.
if ! security find-identity -v -p codesigning | grep -q '"Developer ID Application: ' && [[ -z $CLIPBUBBLE_ALLOW_ADHOC ]]; then
  echo "No Developer ID in the keychain: not building an ad hoc release. Set CLIPBUBBLE_ALLOW_ADHOC=1 for a test build." >&2
  exit 1
fi
OUT=dist
rm -rf $OUT && mkdir -p $OUT/stage
CLIPBUBBLE_TIMESTAMP=1 ./package.sh $OUT/stage/ClipBubble.app
APP=$OUT/stage/ClipBubble.app
ID=$(codesign -dv $APP 2>&1 | sed -n 's/^Authority=\(Developer ID Application: .*\)/\1/p')
notarize() { xcrun notarytool submit "$1" --keychain-profile clipbubble --wait; xcrun stapler staple "$2"; }
if [[ -n $ID ]]; then
  ditto -c -k --keepParent $APP $OUT/notarize.zip
  notarize $OUT/notarize.zip $APP
  rm $OUT/notarize.zip
fi
ditto -c -k --keepParent $APP $OUT/ClipBubble.zip
ln -s /Applications $OUT/stage/Applications
hdiutil create -quiet -volname ClipBubble -srcfolder $OUT/stage -ov -format UDZO $OUT/ClipBubble.dmg
if [[ -n $ID ]]; then
  codesign --force --timestamp -s "$ID" $OUT/ClipBubble.dmg
  notarize $OUT/ClipBubble.dmg $OUT/ClipBubble.dmg
fi
rm -rf $OUT/stage
echo "Built $OUT/ClipBubble.zip and $OUT/ClipBubble.dmg ($(defaults read "$PWD/Info" CFBundleShortVersionString), ${ID:-ad hoc})"
