# Wire protocol v1

Fixed-size 20-byte frames, little-endian, sent phone -> PC at a constant rate
(default 125 Hz). Constant-rate transmission (rather than on-change) gives the
PC side a free watchdog signal: silence means the link is gone.

## Frame layout

| Offset | Type | Field     | Notes                                  |
|--------|------|-----------|----------------------------------------|
| 0      | u8   | magic0    | 0x47 'G'                               |
| 1      | u8   | magic1    | 0x50 'P'                               |
| 2      | u8   | version   | 1                                      |
| 3      | u8   | flags     | bit0 = a controller is attached        |
| 4      | u32  | seq       | monotonic, wraps                       |
| 8      | u16  | buttons   | bitmask, see below                     |
| 10     | i16  | leftX     | -32767..32767, +X right                |
| 12     | i16  | leftY     | -32767..32767, +Y up                   |
| 14     | i16  | rightX    |                                        |
| 16     | i16  | rightY    |                                        |
| 18     | u8   | leftTrig  | 0..255                                 |
| 19     | u8   | rightTrig | 0..255                                 |

Total: 20 bytes. At 125 Hz that is 2.5 KB/s — irrelevant over USB 2.0.

## Button bitmask

```
bit  0  A
bit  1  B
bit  2  X
bit  3  Y
bit  4  LeftShoulder
bit  5  RightShoulder
bit  6  Back   (Options / Share / View)
bit  7  Start  (Menu)
bit  8  LeftThumb  (L3)
bit  9  RightThumb (R3)
bit 10  DpadUp
bit 11  DpadDown
bit 12  DpadLeft
bit 13  DpadRight
bit 14  Guide  (Home — often swallowed by iOS, see README)
bit 15  reserved
```

## Framing / resync

The PC side scans for the `0x47 0x50` magic pair before parsing. If the stream
desynchronises it drops one byte at a time until magic realigns. Version
mismatches are dropped, not fatal — the sender may be newer.

## Axis sign convention

Both `GCExtendedGamepad` and XInput/ViGEm treat **+Y as up** on thumbsticks, so
axes map straight through with no inversion. Do not "fix" this.
