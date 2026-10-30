import Foundation

/// Mirror of the C# `PadButtons` enum. Keep both in sync with PROTOCOL.md.
enum PadButton: UInt16 {
    case a              = 0x0001
    case b              = 0x0002
    case x              = 0x0004
    case y              = 0x0008
    case leftShoulder   = 0x0010
    case rightShoulder  = 0x0020
    case back           = 0x0040
    case start          = 0x0080
    case leftThumb      = 0x0100
    case rightThumb     = 0x0200
    case dpadUp         = 0x0400
    case dpadDown       = 0x0800
    case dpadLeft       = 0x1000
    case dpadRight      = 0x2000
    case guideButton    = 0x4000
}

struct PadState {
    var buttons: UInt16 = 0
    var leftX: Int16 = 0
    var leftY: Int16 = 0
    var rightX: Int16 = 0
    var rightY: Int16 = 0
    var leftTrigger: UInt8 = 0
    var rightTrigger: UInt8 = 0

    mutating func set(_ button: PadButton, _ pressed: Bool) {
        if pressed {
            buttons |= button.rawValue
        } else {
            buttons &= ~button.rawValue
        }
    }
}

enum WireFormat {
    static let magic0: UInt8 = 0x47 // 'G'
    static let magic1: UInt8 = 0x50 // 'P'
    static let version: UInt8 = 1
    static let frameSize = 20

    /// Encodes one fixed-size frame. A nil `state` means "no controller
    /// attached" — the PC releases every input when it sees that flag clear.
    static func encode(seq: UInt32, state: PadState?) -> Data {
        var data = Data(capacity: frameSize)
        data.append(magic0)
        data.append(magic1)
        data.append(version)
        data.append(state == nil ? 0x00 : 0x01)
        data.appendLittleEndian(seq)

        let s = state ?? PadState()
        data.appendLittleEndian(s.buttons)
        data.appendLittleEndian(s.leftX)
        data.appendLittleEndian(s.leftY)
        data.appendLittleEndian(s.rightX)
        data.appendLittleEndian(s.rightY)
        data.append(s.leftTrigger)
        data.append(s.rightTrigger)

        assert(data.count == frameSize)
        return data
    }
}

private extension Data {
    mutating func appendLittleEndian<T: FixedWidthInteger>(_ value: T) {
        var le = value.littleEndian
        Swift.withUnsafeBytes(of: &le) { append(contentsOf: $0) }
    }
}
