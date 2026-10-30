import Network
import Foundation

/// TCP listener the PC reaches through the usbmux tunnel (`iproxy`). The phone
/// is the server here: iproxy forwards a port on the PC to a port the device is
/// listening on, so the direction is fixed by usbmux, not by choice.
final class RelayServer {

    enum Status {
        case stopped
        case listening
        case connected
        case failed(String)
    }

    /// Cap on unacknowledged sends. Input is worthless once it is stale, so if
    /// the PC stalls we drop frames rather than growing an unbounded queue.
    private static let maxInFlight = 4

    private let port: NWEndpoint.Port
    private let queue = DispatchQueue(label: "com.local.gamepadrelay.server")
    private var listener: NWListener?
    private var connection: NWConnection?
    private var inFlight = 0

    /// Called on the server queue on every status transition.
    var onStatus: ((Status) -> Void)?

    init?(port: UInt16) {
        guard let p = NWEndpoint.Port(rawValue: port) else { return nil }
        self.port = p
    }

    func start() {
        let params = NWParameters.tcp
        params.allowLocalEndpointReuse = true
        if let tcp = params.defaultProtocolStack.transportProtocol as? NWProtocolTCP.Options {
            tcp.noDelay = true          // Nagle would batch our 20-byte frames.
            tcp.connectionTimeout = 5
        }

        do {
            let listener = try NWListener(using: params, on: port)

            listener.stateUpdateHandler = { [weak self] state in
                switch state {
                case .ready:
                    self?.onStatus?(.listening)
                case .failed(let error):
                    self?.onStatus?(.failed(error.localizedDescription))
                case .cancelled:
                    self?.onStatus?(.stopped)
                default:
                    break
                }
            }

            listener.newConnectionHandler = { [weak self] conn in
                self?.adopt(conn)
            }

            listener.start(queue: queue)
            self.listener = listener
        } catch {
            onStatus?(.failed(error.localizedDescription))
        }
    }

    func stop() {
        queue.async {
            self.connection?.cancel()
            self.connection = nil
            self.listener?.cancel()
            self.listener = nil
        }
    }

    /// Fire-and-forget. Safe to call from the sampling timer's queue.
    func send(_ data: Data) {
        queue.async {
            guard let conn = self.connection, self.inFlight < Self.maxInFlight else { return }

            self.inFlight += 1
            conn.send(content: data, completion: .contentProcessed { [weak self] error in
                // Delivered on `queue`, so this needs no extra hop.
                guard let self else { return }
                self.inFlight -= 1
                if error != nil { self.drop() }
            })
        }
    }

    // MARK: - Private

    private func adopt(_ conn: NWConnection) {
        // Only one PC at a time; the newest connection wins.
        connection?.cancel()
        connection = conn

        conn.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            switch state {
            case .ready:
                self.inFlight = 0
                self.onStatus?(.connected)
            case .failed, .cancelled:
                self.drop()
            default:
                break
            }
        }

        conn.start(queue: queue)
    }

    private func drop() {
        connection?.cancel()
        connection = nil
        inFlight = 0
        onStatus?(.listening)
    }
}
