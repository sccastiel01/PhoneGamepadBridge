import SwiftUI
import UIKit

@main
struct GamepadRelayApp: App {
    @StateObject private var engine = RelayEngine()
    @Environment(\.scenePhase) private var scenePhase

    var body: some Scene {
        WindowGroup {
            StatusView(engine: engine)
                .onAppear {
                    // No background mode covers input polling, so the app must
                    // stay foreground. At least stop the screen from locking.
                    UIApplication.shared.isIdleTimerDisabled = true
                    engine.start()
                }
                .onChange(of: scenePhase) { phase in
                    // Backgrounding suspends us anyway; tear the socket down
                    // cleanly so the PC sees a close and releases the pad
                    // instead of waiting out its watchdog.
                    switch phase {
                    case .active:     engine.start()
                    case .background: engine.stop()
                    default:          break
                    }
                }
        }
    }
}

struct StatusView: View {
    @ObservedObject var engine: RelayEngine

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            Text("Gamepad Relay")
                .font(.largeTitle.bold())

            row("PC link", engine.linkStatus)
            row("Controller", engine.controllerName ?? "None paired")
            row("Frames sent", "\(engine.framesSent)")

            Divider()

            Text("Keep this screen open. Pair the controller in iOS Settings "
                 + "first, then start the bridge on the PC.")
                .font(.footnote)
                .foregroundStyle(.secondary)

            Spacer()
        }
        .padding(24)
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func row(_ label: String, _ value: String) -> some View {
        HStack {
            Text(label)
                .foregroundStyle(.secondary)
            Spacer()
            Text(value)
                .fontWeight(.medium)
                .multilineTextAlignment(.trailing)
        }
        .font(.body.monospacedDigit())
    }
}
