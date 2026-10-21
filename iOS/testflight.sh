#!/bin/zsh
# Archives the iPhone app and uploads it to TestFlight with an App Store Connect API key (Admin role):
# ASC_KEY_ID, ASC_ISSUER_ID and ASC_KEY_PATH, the path to its AuthKey_<id>.p8 file.
# The build number is the time, so each upload is newer than the last.
set -e
cd "$(dirname "$0")"
: ${ASC_KEY_ID:?} ${ASC_ISSUER_ID:?} ${ASC_KEY_PATH:?}
OUT=build/testflight
rm -rf $OUT
xcodegen generate -q
auth=(-allowProvisioningUpdates -authenticationKeyPath "$ASC_KEY_PATH" -authenticationKeyID $ASC_KEY_ID -authenticationKeyIssuerID $ASC_ISSUER_ID)
xcodebuild -quiet -project ClipBubble.xcodeproj -scheme ClipBubble -configuration Release -destination generic/platform=iOS \
  -archivePath $OUT/ClipBubble.xcarchive CURRENT_PROJECT_VERSION=$(date -u +%y%m%d%H%M) $auth archive
xcodebuild -quiet -exportArchive -archivePath $OUT/ClipBubble.xcarchive -exportOptionsPlist ExportOptions.plist -exportPath $OUT $auth
xcrun altool --upload-package $OUT/ClipBubble.ipa --api-key $ASC_KEY_ID --api-issuer $ASC_ISSUER_ID --p8-file-path "$ASC_KEY_PATH"
