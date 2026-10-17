# ClipBubble sync protocol

ClipBubble on macOS, Linux and Windows syncs history over SSH. This is the contract all three implement. `Tests/fixtures/sync/frames.json` holds one encoded frame of every type, and each implementation's tests decode and re-encode every one byte for byte.

## Model

History is a set of items keyed by hash (`t:` text, `i:` image, `f:` files). For each hash a machine knows its latest event, a stamp of `time` (seconds since 1970) and `deleted`. A later stamp wins. On a tie a delete wins. An item is in history when its winning stamp is a copy.

Because the winner depends only on the stamps, machines converge whatever order frames arrive in, and replaying a frame changes nothing. A copy made after a delete brings the item back. A screenshot deleted before its file's time stays deleted.

Each machine has a random device id. An item's `origin` is the device that first copied it, and relays keep it.

## Session

Whichever machine has the other in its SSH config runs

```
ssh -T -o BatchMode=yes -o ConnectTimeout=8 -o ServerAliveInterval=15 -o ServerAliveCountMax=2 -- <alias> <remote binary> --peer
```

`--peer` relays bytes between SSH's stdio and the running app's local endpoint. That is a Unix socket on macOS and Linux and a named pipe on Windows. The app is the only writer of its database.

1. Both sides send `hello`. A different `proto` ends the session, and the newer side shows "Update".
2. Both sides send `summary`, every stamp from the last 7 days except items whose bytes haven't arrived yet. Leaving those out makes the peer send their `put` again, so a download cut off by a dropped link starts again on the next one.
3. Each side sends `put` or `tombstone` for every hash where its stamp beats the peer's.
4. From then on each side sends a `put` or `tombstone` whenever its history changes, unless the peer already has that stamp.
5. Images and files move as `chunk` frames after a `fetch`. Items whose blobs total 25 MB or less are fetched whenever a `put` arrives for an item still missing its bytes, and bigger ones wait for the user to press Get on the receiving side or Send to on the sending side, which sends `offer`. A download cut off by a dropped link keeps its partial file and resumes from that offset.

A received item goes on the system clipboard only if it was copied less than 10 seconds ago on its origin's clock (corrected by the `hello` clock offset) and is newer than every local item. The receiver remembers that hash, so its own clipboard capture does not send it back.

Control frames always go before chunks, so a copy waits behind at most one 256 KB chunk.

## Frames

Each frame is a 4-byte big-endian length, then that many bytes, a 1-byte type and its fields. A length of 0 or over 4 MiB closes the session. Fields use these encodings.

| Field | Encoding |
| --- | --- |
| `u8`, `u32`, `i64` | big-endian integers |
| `f64` | big-endian IEEE 754, NaN and infinities rejected |
| `str` | `u32` byte length, then UTF-8 |
| `bytes` | `u32` length, then raw bytes |
| `stamp` | `str` hash, `f64` time, `u8` deleted (0 or 1) |

| Type | Name | Fields |
| --- | --- | --- |
| 1 | hello | `u32` proto, `str` device, `str` name, `str` os, `str` version, `f64` clock |
| 2 | summary | `u32` count, then count `stamp`s |
| 3 | put | `str` hash, `f64` time, `str` kind, `str` text, `str` source, `str` origin, `u32` count, then count blobs of `str` name, `i64` size, `str` sha |
| 4 | tombstone | `stamp` |
| 5 | fetch | `str` hash, `u32` blob index, `i64` offset |
| 6 | chunk | `str` hash, `u32` blob index, `i64` offset, `i64` total, `bytes` data (at most 256 KiB) |
| 7 | cancel | `str` hash |
| 8 | applied | `str` hash |
| 9 | ping | `f64` sender clock |
| 10 | pong | `f64` the ping's clock |
| 11 | offer | `str` hash: fetch this item now, whatever its size ("Send to") |

`kind` is `text`, `image` or `file`. For text, `text` is the copied text, at most 2 MB. For files it is the file names, one per line, and never local paths. Images carry one blob, the PNG, whose `sha` is the hash without `i:`. File blobs carry an empty `sha`, and their size and SSH's integrity checks cover them. Trailing bytes, unknown types and counts larger than the frame are malformed and close the session.

## Received files

Files land in `Downloads/ClipBubble/<device name>/`. Names lose path separators and leading dots, and a clash gets ` (2)`, ` (3)` and so on. A transfer resumes with `fetch` at the received length after a drop.
