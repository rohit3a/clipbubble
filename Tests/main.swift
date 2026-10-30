import Foundation

signal(SIGPIPE, SIG_IGN)
let child = ProcessInfo.processInfo.environment["CLIPBUBBLE_CHILD"] != nil
// Every test gets throwaway data: point Paths at a temp folder before anything touches it.
let testHome = URL(fileURLWithPath: ProcessInfo.processInfo.environment["TMPDIR"] ?? NSTemporaryDirectory()).appendingPathComponent("clipbubble-tests-\(getpid())")
if !child {
    try? FileManager.default.removeItem(at: testHome)
    setenv("CLIPBUBBLE_HOME", testHome.path, 1)
    setenv("CLIPBUBBLE_DOWNLOADS", testHome.appendingPathComponent("Downloads").path, 1)
}

var failures = 0
var ran = 0

func check(_ ok: @autoclosure () -> Bool, _ what: String, file: String = #fileID, line: Int = #line) {
    ran += 1
    if !ok() { failures += 1; print("FAIL \(file):\(line) \(what)") }
}

if CommandLine.arguments.contains("--write-fixtures") {
    writeFixtures(to: URL(fileURLWithPath: CommandLine.arguments.last!))
    exit(0)
}
if child { runChild() }

syncTests()
storeTests()
linkTests()
boundaryTests()
pairingTests()
updaterTests()

try? FileManager.default.removeItem(at: testHome)
print("\(ran) checks, \(failures) failed")
exit(failures == 0 ? 0 : 1)
