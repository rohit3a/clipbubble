import Foundation

func updaterTests() {
    check(Updater.isNewer("1.10.0", than: "1.9.2"), "1.10.0 is newer than 1.9.2")
    check(Updater.isNewer("1.6.1", than: "1.6.0"), "1.6.1 is newer than 1.6.0")
    check(!Updater.isNewer("1.6.0", than: "1.6.0"), "the same version isn't an update")
    check(!Updater.isNewer("1.5.0", than: "1.6.0"), "an older release isn't an update")

    let release = """
    {"tag_name": "v1.7.0", "html_url": "https://github.com/rohit3a/clipbubble/releases/tag/v1.7.0",
     "assets": [{"name": "ClipBubble.dmg", "browser_download_url": "https://example.com/ClipBubble.dmg"},
                {"name": "ClipBubble.zip", "browser_download_url": "https://example.com/ClipBubble.zip"}]}
    """
    let r = try? Updater.parse(Data(release.utf8))
    check(r?.version == "1.7.0", "the tag's v is dropped")
    check(r?.zip.lastPathComponent == "ClipBubble.zip", "the Mac update is the zip, not the dmg")
    check(r?.page.absoluteString.hasSuffix("/v1.7.0") == true, "what's new links to the release")

    let windowsOnly = #"{"tag_name": "v1.7.0", "html_url": "https://x", "assets": [{"name": "ClipBubble.exe", "browser_download_url": "https://x/e"}]}"#
    check((try? Updater.parse(Data(windowsOnly.utf8))) == nil, "a release without the Mac zip isn't offered")
}
