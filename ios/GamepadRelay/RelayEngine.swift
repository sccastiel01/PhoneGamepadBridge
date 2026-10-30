import Foundation
import Combine

/// Ties the controller sampler to the socket. Samples at a fixed rate and sends
/// unconditionally — constant cadence doubles as the PC's watchdog heartbeat.
///
/// Concurrency contract: every stored property below is touched *only* from
/// `sampleQueue` (a serial queue), except the `@Published` ones, which are
/// written *only* on the main queue. Nothing is shared across the two.
final class RelayEngine: ObservableObject {

    static let defaultPort: UInt16 = 5111

    @Published private(set) var linkStatus = "Stopped"
    @Published private(set) var controllerName: String?
    @Published private(set) var framesSent: UInt32 = 0

    private let reader = ControllerReader()
    private let server: RelayServer?
    private let sampleQueue = DispatchQueue(label: "com.local.gamepadrelay.sample")
    private var timer: DispatchSourceTimer?
    private var seq: UInt32 = 0

    /// Main-queue only. Scene phase can go active -> inactive -> active without
    /// a stop in between, and re-binding a live port would fail.
    private var isRunning = false

    /// 125 Hz. Below ~100 Hz the added latency starts to show; above ~250 Hz
    /// you are only burning battery, since the Bluetooth link itself reports at
    /// roughly 100–250 Hz depending on the pad.
    private let sampleInterval = DispatchTimeInterval.milliseconds(8)

    init(port: UInt16 = RelayEngine.defaultPort) {
        server = RelayServer(port: port)

        reader.onAttachmentChange = { [weak self] name in
            self?.publish { $0.controllerName = name }
        }

        server?.onStatus = { [weak self] status in
            let text: String
            switch status {
            case .stopped:            text = "Stopped"
            case .listening:          text = "Waiting for PC"
            case .connected:          text = "Connected"
            case .failed(let reason): text = "Failed: \(reason)"
            }
            self?.publish { $0.linkStatus = text }
        }
    }

    /// Idempotent. Call from the main queue.
    func start() {
        guard !isRunning else { return }

        guard let server else {
            publish { $0.linkStatus = "Invalid port" }
            return
        }

        isRunning = true
        let name = reader.attachedName
        publish { $0.controllerName = name }

        server.start()
        startSampling()
    }

    /// Idempotent. Call from the main queue.
    func stop() {
        guard isRunning else { return }
        isRunning = false

        sampleQueue.sync {
            timer?.cancel()
            timer = nil
        }
        server?.stop()
        publish { $0.linkStatus = "Stopped" }
    }

    // MARK: - Private

    private func startSampling() {
        sampleQueue.async {
            self.timer?.cancel()

            let t = DispatchSource.makeTimerSource(queue: self.sampleQueue)
            t.schedule(deadline: .now(), repeating: self.sampleInterval, leeway: .milliseconds(1))
            t.setEventHandler { [weak self] in self?.tick() }
            t.resume()
            self.timer = t
        }
    }

    /// Runs on `sampleQueue` only.
    private func tick() {
        seq &+= 1
        let state = reader.snapshot()
        server?.send(WireFormat.encode(seq: seq, state: state))

        // Publishing at 125 Hz would thrash the main thread; every 32nd frame
        // gives a ~4 Hz status readout, which is plenty.
        if seq % 32 == 0 {
            let sent = seq
            publish { $0.framesSent = sent }
        }
    }

    private func publish(_ mutate: @escaping (RelayEngine) -> Void) {
        DispatchQueue.main.async { mutate(self) }
    }
}
