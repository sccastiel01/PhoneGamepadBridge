import GameController
import Foundation

/// Reads the currently-attached Bluetooth controller through the GameController
/// framework. This is the only supported route to a third-party pad on iOS —
/// there is no raw HID or HCI access. Supports Xbox Wireless, DualShock 4,
/// DualSense, Switch Pro (iOS 16+) and MFi pads.
final class ControllerReader {

    /// Called on an arbitrary queue whenever a pad connects or disconnects.
    var onAttachmentChange: ((String?) -> Void)?

    init() {
        let center = NotificationCenter.default
        center.addObserver(
            forName: .GCControllerDidConnect, object: nil, queue: nil
        ) { [weak self] note in
            let name = (note.object as? GCController)?.vendorName
            self?.onAttachmentChange?(name)
        }
        center.addObserver(
            forName: .GCControllerDidDisconnect, object: nil, queue: nil
        ) { [weak self] _ in
            self?.onAttachmentChange?(GCController.current?.vendorName)
        }
    }

    var attachedName: String? {
        GCController.current?.vendorName
    }

    /// Samples the pad. Returns nil when nothing is attached.
    func snapshot() -> PadState? {
        guard let pad = GCController.current?.extendedGamepad else { return nil }

        var s = PadState()

        s.set(.a, pad.buttonA.isPressed)
        s.set(.b, pad.buttonB.isPressed)
        s.set(.x, pad.buttonX.isPressed)
        s.set(.y, pad.buttonY.isPressed)
        s.set(.leftShoulder, pad.leftShoulder.isPressed)
        s.set(.rightShoulder, pad.rightShoulder.isPressed)
        s.set(.start, pad.buttonMenu.isPressed)

        // These four are optional on the protocol — older MFi pads lack them.
        s.set(.back, pad.buttonOptions?.isPressed ?? false)
        s.set(.leftThumb, pad.leftThumbstickButton?.isPressed ?? false)
        s.set(.rightThumb, pad.rightThumbstickButton?.isPressed ?? false)
        s.set(.guideButton, pad.buttonHome?.isPressed ?? false)

        s.set(.dpadUp, pad.dpad.up.isPressed)
        s.set(.dpadDown, pad.dpad.down.isPressed)
        s.set(.dpadLeft, pad.dpad.left.isPressed)
        s.set(.dpadRight, pad.dpad.right.isPressed)

        // GameController and XInput agree that +Y is up, so no inversion here.
        s.leftX = Self.axis(pad.leftThumbstick.xAxis.value)
        s.leftY = Self.axis(pad.leftThumbstick.yAxis.value)
        s.rightX = Self.axis(pad.rightThumbstick.xAxis.value)
        s.rightY = Self.axis(pad.rightThumbstick.yAxis.value)

        s.leftTrigger = Self.trigger(pad.leftTrigger.value)
        s.rightTrigger = Self.trigger(pad.rightTrigger.value)

        return s
    }

    // Clamp before converting: an out-of-range Float would trap on Int16 init.
    @inline(__always)
    private static func axis(_ v: Float) -> Int16 {
        Int16(min(max(v, -1), 1) * 32767)
    }

    @inline(__always)
    private static func trigger(_ v: Float) -> UInt8 {
        UInt8(min(max(v, 0), 1) * 255)
    }
}
