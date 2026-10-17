import Foundation

// Every test gets throwaway data: point Paths at a temp folder before anything touches it.
let testHome = FileManager.default.temporaryDirectory.appendingPathComponent("clipbubble-tests-\(getpid())")
try? FileManager.default.removeItem(at: testHome)
setenv("CLIPBUBBLE_HOME", testHome.path, 1)

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

syncTests()
storeTests()
linkTests()

try? FileManager.default.removeItem(at: testHome)
print("\(ran) checks, \(failures) failed")
exit(failures == 0 ? 0 : 1)
