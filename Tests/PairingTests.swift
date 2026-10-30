import Foundation

/// AuthorizedKeys on a temp file. Pairing must never cost the owner a key.
func pairingTests() {
    let dir = testHome.appendingPathComponent("ssh")
    try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    let file = dir.appendingPathComponent("authorized_keys")
    let body = "AAAAC3NzaC1lZDI1NTE5AAAAIPqR3mUOcskK0AMMwJekuAgZW350J"
    let key = "ssh-ed25519 \(body)"
    func lines() -> [Data] { (try? Data(contentsOf: file))?.split(separator: 10, omittingEmptySubsequences: false).map { Data($0) } ?? [] }
    func starts(_ prefix: Data) -> Bool { (try? Data(contentsOf: file))?.starts(with: prefix) ?? false }

    var old = Data("ssh-ed25519 AAAAone caf".utf8) + Data([0xE9]) + Data(" laptop\nssh-ed25519 AAAAtwo work\n".utf8)
    try? old.write(to: file)
    check((try? AuthorizedKeys.add(key: key, name: "iPhone", to: file)) != nil, "pairing works beside a byte that isn't text")
    check(starts(old) && lines().count == 4, "and every other key stays, unchanged")

    old = Data("ssh-ed25519 AAAAone laptop\r\nrestrict,command=\"'old' --peer\" \(key) ClipBubble on iPhone\r\nssh-ed25519 AAAAtwo work\r\n".utf8)
    try? old.write(to: file)
    check((try? AuthorizedKeys.add(key: key, name: "iPhone", to: file)) != nil, "pairing works on CRLF lines")
    check(starts(Data("ssh-ed25519 AAAAone laptop\r\nssh-ed25519 AAAAtwo work\r\n".utf8)) && lines().count == 4, "and pairing again keeps them")
    check(lines().filter { $0.range(of: Data(body.utf8)) != nil }.count == 1, "the phone's line is replaced, not doubled")

    try? Data("ssh-ed25519 AAAAone laptop".utf8).write(to: file)
    try? AuthorizedKeys.add(key: key, name: "iPhone", to: file)
    check(lines().first == Data("ssh-ed25519 AAAAone laptop".utf8), "no final newline keeps the last key whole")

    try? FileManager.default.removeItem(at: file)
    let target = dir.appendingPathComponent("dotfiles-keys")
    try? Data("ssh-ed25519 AAAAone laptop\n".utf8).write(to: target)
    try? FileManager.default.createSymbolicLink(at: file, withDestinationURL: target)
    try? AuthorizedKeys.add(key: key, name: "iPhone", to: file)
    check((try? FileManager.default.destinationOfSymbolicLink(atPath: file.path)) != nil, "a symlink stays a symlink")
    check((try? Data(contentsOf: target))?.starts(with: Data("ssh-ed25519 AAAAone laptop\n".utf8)) ?? false, "and its target gets the key")
    check((try? Data(contentsOf: target))?.range(of: Data(body.utf8)) != nil, "beside the keys it had")
    try? FileManager.default.removeItem(at: file)

    try? Data("ssh-ed25519 AAAAone laptop\n".utf8).write(to: file)
    try? FileManager.default.setAttributes([.posixPermissions: 0], ofItemAtPath: file.path)
    check((try? AuthorizedKeys.add(key: key, name: "iPhone", to: file)) == nil, "a file it can't read stops pairing")
    try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
    check((try? Data(contentsOf: file)) == Data("ssh-ed25519 AAAAone laptop\n".utf8), "and is left alone")

    try? FileManager.default.removeItem(at: file)
    try? AuthorizedKeys.add(key: key, name: "Rohit's iPhone!", to: file)
    let line = lines().first.map { String(decoding: $0, as: UTF8.self) } ?? ""
    check(line.hasPrefix("restrict,command=\"'") && line.hasSuffix("' --peer\" \(key) ClipBubble on Rohits iPhone"), "a new key is restricted to the relay")
    check((try? FileManager.default.attributesOfItem(atPath: file.path)[.posixPermissions] as? Int) == 0o600, "and only this user can read the file")
    check((try? AuthorizedKeys.add(key: "ssh-rsa AAAA", name: "x", to: file)) == nil, "other keys are refused")
}
