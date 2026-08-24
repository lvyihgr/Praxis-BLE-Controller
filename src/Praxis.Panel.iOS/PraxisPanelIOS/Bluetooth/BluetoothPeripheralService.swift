import CoreBluetooth
import Foundation
import UIKit

protocol BluetoothPeripheralServiceDelegate: AnyObject {
    func peripheralService(_ service: BluetoothPeripheralService, didChange state: BluetoothConnectionState)
    func peripheralService(_ service: BluetoothPeripheralService, didReceive message: Data)
    func peripheralService(_ service: BluetoothPeripheralService, didRejectFrame error: Error)
}

struct BluetoothTransmissionQueue {
    private struct Transmission {
        let frames: [Data]
        let coalescingKey: String?
        var nextFrameIndex = 0
    }

    private var transmissions: [Transmission] = []

    var nextFrame: Data? {
        guard let first = transmissions.first,
              first.nextFrameIndex < first.frames.count else { return nil }
        return first.frames[first.nextFrameIndex]
    }

    var pendingFrameCount: Int {
        transmissions.reduce(0) { $0 + max(0, $1.frames.count - $1.nextFrameIndex) }
    }

    mutating func enqueue(frames: [Data], coalescingKey: String?) {
        guard !frames.isEmpty else { return }
        if let coalescingKey = coalescingKey {
            // Preserve a message once its first frame has left the device, but
            // replace any still-unsent pulse with the user's newest direction.
            transmissions.removeAll {
                $0.coalescingKey == coalescingKey && $0.nextFrameIndex == 0
            }
        }
        transmissions.append(Transmission(frames: frames, coalescingKey: coalescingKey))
    }

    mutating func markNextFrameSent() {
        guard !transmissions.isEmpty else { return }
        transmissions[0].nextFrameIndex += 1
        if transmissions[0].nextFrameIndex >= transmissions[0].frames.count {
            transmissions.removeFirst()
        }
    }

    mutating func removeAll() {
        transmissions.removeAll()
    }
}

final class BluetoothPeripheralService: NSObject {
    static let serviceUUID = CBUUID(string: "7a51f000-5e8f-4c7a-9a1d-707261786973")
    static let commandToCoreUUID = CBUUID(string: "7a51f001-5e8f-4c7a-9a1d-707261786973")
    static let eventFromCoreUUID = CBUUID(string: "7a51f002-5e8f-4c7a-9a1d-707261786973")
    static let peerInfoUUID = CBUUID(string: "7a51f003-5e8f-4c7a-9a1d-707261786973")

    weak var delegate: BluetoothPeripheralServiceDelegate?
    private(set) var state: BluetoothConnectionState = .stopped {
        didSet {
            guard oldValue != state else { return }
            delegate?.peripheralService(self, didChange: state)
        }
    }

    private var manager: CBPeripheralManager?
    private var commandCharacteristic: CBMutableCharacteristic?
    private var eventCharacteristic: CBMutableCharacteristic?
    private var peerInfoCharacteristic: CBMutableCharacteristic?
    private var subscribedCentral: CBCentral?
    private var transmissionQueue = BluetoothTransmissionQueue()
    private let reassembler = PraxisFrameReassembler()
    private var nextMessageID: UInt16 = 0
    private var servicePublished = false
    private let peerInfo: Data = {
        let defaults = UserDefaults.standard
        let key = "praxis.panel.device-id"
        let deviceID: String
        if let stored = defaults.string(forKey: key), !stored.isEmpty {
            deviceID = stored
        } else {
            deviceID = UUID().uuidString.lowercased()
            defaults.set(deviceID, forKey: key)
        }
        let object: [String: String] = [
            "deviceId": deviceID,
            "version": "1.2.0",
            "name": UIDevice.current.name
        ]
        return (try? JSONSerialization.data(withJSONObject: object, options: [])) ?? Data()
    }()

    func start() {
        guard manager == nil else {
            recoverAdvertisingIfNeeded()
            return
        }
        state = .starting
        manager = CBPeripheralManager(
            delegate: self,
            queue: .main,
            options: [CBPeripheralManagerOptionRestoreIdentifierKey: "com.praxis.panel.ios.peripheral"]
        )
    }

    func stop() {
        manager?.stopAdvertising()
        if manager?.state == .poweredOn {
            manager?.removeAllServices()
        }
        resetSession(clearFrames: true)
        manager = nil
        servicePublished = false
        state = .stopped
    }

    func recoverAdvertisingIfNeeded() {
        guard let manager = manager, manager.state == .poweredOn else { return }
        if !servicePublished {
            publishService()
        } else if subscribedCentral == nil && !manager.isAdvertising {
            startAdvertising()
        }
    }

    func send(_ message: Data, coalescingKey: String? = nil) throws {
        guard !message.isEmpty && message.count <= PraxisFrameEncoder.maximumMessageBytes else {
            throw BluetoothPeripheralServiceError.invalidMessage
        }
        guard let central = subscribedCentral else {
            throw BluetoothPeripheralServiceError.notConnected
        }

        nextMessageID &+= 1
        if nextMessageID == 0 { nextMessageID = 1 }
        let frameSize = central.maximumUpdateValueLength
        let frames = try PraxisFrameEncoder.encode(
            message: message,
            maximumFrameBytes: frameSize,
            messageID: nextMessageID
        )
        transmissionQueue.enqueue(frames: frames, coalescingKey: coalescingKey)
        pumpNotifications()
    }

    private func publishService() {
        guard let manager = manager, manager.state == .poweredOn else { return }
        manager.stopAdvertising()
        manager.removeAllServices()
        resetSession(clearFrames: true)
        servicePublished = false

        let command = CBMutableCharacteristic(
            type: Self.commandToCoreUUID,
            properties: [.notify],
            value: nil,
            permissions: []
        )
        let event = CBMutableCharacteristic(
            type: Self.eventFromCoreUUID,
            properties: [.write, .writeWithoutResponse],
            value: nil,
            permissions: [.writeable]
        )
        let peer = CBMutableCharacteristic(
            type: Self.peerInfoUUID,
            properties: [.read],
            value: nil,
            permissions: [.readable]
        )
        commandCharacteristic = command
        eventCharacteristic = event
        peerInfoCharacteristic = peer

        let service = CBMutableService(type: Self.serviceUUID, primary: true)
        service.characteristics = [command, event, peer]
        manager.add(service)
        state = .starting
    }

    private func startAdvertising() {
        guard let manager = manager, manager.state == .poweredOn, servicePublished else { return }
        if !manager.isAdvertising {
            manager.startAdvertising([
                CBAdvertisementDataServiceUUIDsKey: [Self.serviceUUID],
                CBAdvertisementDataLocalNameKey: "Praxis Panel"
            ])
        }
        if subscribedCentral == nil {
            state = .advertising
        }
    }

    private func pumpNotifications() {
        guard let manager = manager,
              let command = commandCharacteristic,
              let central = subscribedCentral else { return }

        while let frame = transmissionQueue.nextFrame {
            let sent = manager.updateValue(frame, for: command, onSubscribedCentrals: [central])
            if !sent {
                state = .backpressured(maximumUpdateValueLength: central.maximumUpdateValueLength)
                return
            }
            transmissionQueue.markNextFrameSent()
        }
        state = .connected(maximumUpdateValueLength: central.maximumUpdateValueLength)
    }

    private func resetSession(clearFrames: Bool) {
        subscribedCentral = nil
        reassembler.reset()
        if clearFrames { transmissionQueue.removeAll() }
    }
}

extension BluetoothPeripheralService: CBPeripheralManagerDelegate {
    func peripheralManagerDidUpdateState(_ peripheral: CBPeripheralManager) {
        switch peripheral.state {
        case .poweredOn:
            publishService()
        case .poweredOff, .resetting, .unauthorized, .unsupported, .unknown:
            resetSession(clearFrames: true)
            servicePublished = false
            state = .bluetoothUnavailable
        @unknown default:
            state = .bluetoothUnavailable
        }
    }

    func peripheralManager(_ peripheral: CBPeripheralManager, didAdd service: CBService, error: Error?) {
        guard error == nil else {
            state = .error(error!.localizedDescription)
            return
        }
        servicePublished = true
        startAdvertising()
    }

    func peripheralManagerDidStartAdvertising(_ peripheral: CBPeripheralManager, error: Error?) {
        if let error = error {
            state = .error(error.localizedDescription)
        } else if subscribedCentral == nil {
            state = .advertising
        }
    }

    func peripheralManager(
        _ peripheral: CBPeripheralManager,
        central: CBCentral,
        didSubscribeTo characteristic: CBCharacteristic
    ) {
        guard characteristic.uuid == Self.commandToCoreUUID else { return }
        subscribedCentral = central
        reassembler.reset()
        transmissionQueue.removeAll()
        state = .connected(maximumUpdateValueLength: central.maximumUpdateValueLength)
        pumpNotifications()
    }

    func peripheralManager(
        _ peripheral: CBPeripheralManager,
        central: CBCentral,
        didUnsubscribeFrom characteristic: CBCharacteristic
    ) {
        guard characteristic.uuid == Self.commandToCoreUUID,
              subscribedCentral?.identifier == central.identifier else { return }
        resetSession(clearFrames: true)
        state = .advertising
        if !peripheral.isAdvertising { startAdvertising() }
    }

    func peripheralManagerIsReady(toUpdateSubscribers peripheral: CBPeripheralManager) {
        pumpNotifications()
    }

    func peripheralManager(_ peripheral: CBPeripheralManager, didReceiveRead request: CBATTRequest) {
        guard request.characteristic.uuid == Self.peerInfoUUID else {
            peripheral.respond(to: request, withResult: .requestNotSupported)
            return
        }
        guard request.offset <= peerInfo.count else {
            peripheral.respond(to: request, withResult: .invalidOffset)
            return
        }
        request.value = peerInfo.subdata(in: request.offset..<peerInfo.count)
        peripheral.respond(to: request, withResult: .success)
    }

    func peripheralManager(_ peripheral: CBPeripheralManager, didReceiveWrite requests: [CBATTRequest]) {
        for request in requests {
            guard request.characteristic.uuid == Self.eventFromCoreUUID else {
                peripheral.respond(to: request, withResult: .requestNotSupported)
                continue
            }
            guard request.offset == 0, let value = request.value else {
                peripheral.respond(to: request, withResult: .invalidOffset)
                continue
            }
            do {
                if let message = try reassembler.accept(value) {
                    delegate?.peripheralService(self, didReceive: message)
                }
                peripheral.respond(to: request, withResult: .success)
            } catch {
                delegate?.peripheralService(self, didRejectFrame: error)
                peripheral.respond(to: request, withResult: .unlikelyError)
            }
        }
    }

    func peripheralManager(_ peripheral: CBPeripheralManager, willRestoreState dict: [String: Any]) {
        servicePublished = false
        resetSession(clearFrames: true)
    }
}
