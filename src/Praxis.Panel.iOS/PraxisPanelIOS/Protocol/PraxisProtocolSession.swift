import Foundation

protocol PraxisProtocolSessionDelegate: AnyObject {
    func protocolSession(_ session: PraxisProtocolSession, didChange connection: BluetoothConnectionState)
    func protocolSession(_ session: PraxisProtocolSession, didReceive coreInfo: PraxisCoreInfo)
    func protocolSession(_ session: PraxisProtocolSession, didReceiveAction type: String, event: PraxisActionEvent)
    func protocolSession(_ session: PraxisProtocolSession, didReceiveNotice title: String, message: String, isError: Bool)
    func protocolSession(_ session: PraxisProtocolSession, didReceiveSystemSummary output: String)
    func protocolSession(_ session: PraxisProtocolSession, didReceiveIconFor actionID: String)
    func protocolSession(_ session: PraxisProtocolSession, requiresPairingWith serverName: String)
    func protocolSessionDidAuthenticate(_ session: PraxisProtocolSession)
    func protocolSession(_ session: PraxisProtocolSession, didPlanBootstrapTotal total: Int, missing: Int)
    func protocolSessionDidSynchronize(_ session: PraxisProtocolSession)
}

final class PraxisProtocolSession: NSObject {
    weak var delegate: PraxisProtocolSessionDelegate?
    let configurationStore = PraxisConfigurationStore()
    let stateStore = PraxisStateStore()
    let iconStore = PraxisIconStore()
    private let peripheralService: BluetoothPeripheralService
    private let encoder = JSONEncoder()
    private let decoder = JSONDecoder()
    private let secureSession = PraxisSecureSession()
    private let pairingWorkQueue = DispatchQueue(label: "com.praxis.panel.pairing", qos: .userInitiated)
    private var pingStartedAt: [String: Date] = [:]
    private var pairingAttempt: UInt64 = 0
    private var pairingResponseTimeout: DispatchWorkItem?
    private var bootstrapReady = false

    private(set) var connectionState: BluetoothConnectionState = .stopped
    private(set) var coreInfo: PraxisCoreInfo?
    var isReady: Bool { connectionState.isConnected && secureSession.isActive && bootstrapReady }

    init(peripheralService: BluetoothPeripheralService = BluetoothPeripheralService()) {
        self.peripheralService = peripheralService
        super.init()
        peripheralService.delegate = self
    }

    func start() {
        peripheralService.start()
    }

    func applicationDidBecomeActive() {
        peripheralService.recoverAdvertisingIfNeeded()
    }

    @discardableResult
    func ping() -> String? {
        let message = PraxisProtocolMessage(type: "ping", payload: .object([:]))
        do {
            try send(message)
            pingStartedAt[message.id] = Date()
            return message.id
        } catch {
            delegate?.protocolSession(self, didReceiveNotice: "连接测试失败", message: error.localizedDescription, isError: true)
            return nil
        }
    }

    func refreshConfiguration() {
        guard isReady else {
            delegate?.protocolSession(self, didReceiveNotice: "正在同步", message: "控制配置尚未准备完成。", isError: false)
            return
        }
        do {
            try send(PraxisProtocolMessage(type: "config.refresh", payload: .object([:])))
        } catch {
            delegate?.protocolSession(self, didReceiveNotice: "刷新失败", message: error.localizedDescription, isError: true)
        }
    }

    func submitPairingCode(_ code: String) {
        guard connectionState.isConnected else {
            delegate?.protocolSession(
                self,
                didReceiveNotice: "配对失败",
                message: BluetoothPeripheralServiceError.notConnected.localizedDescription,
                isError: true
            )
            return
        }

        invalidatePairingAttempt()
        let attempt = pairingAttempt
        schedulePairingResponseTimeout(for: attempt)
        secureSession.submit(code: code, workQueue: pairingWorkQueue) { [weak self] result in
            guard let self = self, self.pairingAttempt == attempt else { return }
            switch result {
            case .success(let request):
                do {
                    try self.sendPlain(request)
                } catch {
                    self.failPairingAttempt(error.localizedDescription)
                }
            case .failure(let error):
                self.failPairingAttempt(error.localizedDescription)
            }
        }
    }

    @discardableResult
    func invoke(
        actionID: String,
        controlID: String?,
        confirmed: Bool = false,
        itemID: String? = nil,
        suppressFeedback: Bool = false
    ) -> String? {
        guard isReady else {
            delegate?.protocolSession(self, didReceiveNotice: "正在同步", message: "本次操作未发送，请等待控制配置准备完成。", isError: false)
            return nil
        }
        var payload: [String: JSONValue] = [
            "actionId": .string(actionID),
            "confirmed": .bool(confirmed),
            "controlId": controlID.map(JSONValue.string) ?? .null,
            "itemId": itemID.map(JSONValue.string) ?? .null,
            "suppressFeedback": .bool(suppressFeedback)
        ]
        if itemID == nil { payload["itemId"] = .null }
        let message = PraxisProtocolMessage(type: "control.invoke", payload: .object(payload))
        do {
            let coalescingKey = actionID == "presentation.previous" || actionID == "presentation.next"
                ? "presentation.navigation"
                : nil
            try send(message, coalescingKey: coalescingKey)
            return message.id
        } catch {
            delegate?.protocolSession(self, didReceiveNotice: "当前离线", message: error.localizedDescription, isError: true)
            return nil
        }
    }

    private func send(_ message: PraxisProtocolMessage, coalescingKey: String? = nil) throws {
        guard connectionState.isConnected else { throw BluetoothPeripheralServiceError.notConnected }
        let secured = try secureSession.wrap(message, encoder: encoder)
        let data = try encoder.encode(secured)
        try peripheralService.send(data, coalescingKey: coalescingKey)
    }

    private func sendPlain(_ message: PraxisProtocolMessage) throws {
        guard connectionState.isConnected else { throw BluetoothPeripheralServiceError.notConnected }
        try peripheralService.send(encoder.encode(message))
    }

    private func handle(_ data: Data) {
        do {
            let envelope = try decoder.decode(PraxisProtocolMessage.self, from: data)
            guard envelope.isValid else {
                throw BluetoothPeripheralServiceError.invalidMessage
            }
            if envelope.type.hasPrefix("pairing.") {
                handlePairing(secureSession.handlePairing(envelope))
                return
            }
            let message = try secureSession.unwrap(envelope, decoder: decoder)
            handleAuthenticated(message)
        } catch {
            delegate?.protocolSession(self, didReceiveNotice: "消息解析失败", message: error.localizedDescription, isError: true)
        }
    }

    private func handlePairing(_ event: PraxisPairingEvent) {
        switch event {
        case .send(let message):
            do { try sendPlain(message) }
            catch { delegate?.protocolSession(self, didReceiveNotice: "配对失败", message: error.localizedDescription, isError: true) }
        case .requiresCode(let serverName):
            invalidatePairingAttempt()
            delegate?.protocolSession(self, requiresPairingWith: serverName)
        case .activated:
            invalidatePairingAttempt()
            bootstrapReady = false
            delegate?.protocolSessionDidAuthenticate(self)
            sendBootstrapPreparation()
        case .failed(let error):
            invalidatePairingAttempt()
            delegate?.protocolSession(self, didReceiveNotice: "配对失败", message: error, isError: true)
        case .ignored:
            break
        }
    }

    private func schedulePairingResponseTimeout(for attempt: UInt64) {
        pairingResponseTimeout?.cancel()
        let timeout = DispatchWorkItem { [weak self] in
            guard let self = self, self.pairingAttempt == attempt, !self.secureSession.isActive else { return }
            self.failPairingAttempt("配对未在预期时间内完成，请确认配对码和 PC 服务后重试。")
        }
        pairingResponseTimeout = timeout
        DispatchQueue.main.asyncAfter(deadline: .now() + 15, execute: timeout)
    }

    private func failPairingAttempt(_ message: String) {
        invalidatePairingAttempt()
        delegate?.protocolSession(self, didReceiveNotice: "配对失败", message: message, isError: true)
    }

    private func invalidatePairingAttempt() {
        pairingAttempt &+= 1
        pairingResponseTimeout?.cancel()
        pairingResponseTimeout = nil
    }

    private func handleAuthenticated(_ message: PraxisProtocolMessage) {
        do {
            switch message.type {
            case "core.hello":
                let info = PraxisCoreInfo(payload: message.payload)
                coreInfo = info
                delegate?.protocolSession(self, didReceive: info)
            case "config.snapshot":
                try configurationStore.apply(snapshot: message.payload)
            case "config.unchanged":
                guard let hash = message.payload["configurationHash"]?.stringValue,
                      configurationStore.confirms(hash: hash) else {
                    throw BluetoothPeripheralServiceError.invalidMessage
                }
            case "state.snapshot":
                try stateStore.apply(snapshot: message.payload)
            case "state.changed":
                try stateStore.apply(change: message.payload)
            case "app.icon":
                guard let actionID = message.payload["actionId"]?.stringValue,
                      let base64 = message.payload["data"]?.stringValue,
                      let hash = message.payload["sha256"]?.stringValue,
                      iconStore.apply(actionID: actionID, base64: base64, expectedHash: hash) != nil else {
                    throw BluetoothPeripheralServiceError.invalidMessage
                }
                delegate?.protocolSession(self, didReceiveIconFor: actionID)
                try send(PraxisProtocolMessage(type: "app.icon.received", payload: .object([
                    "actionId": .string(actionID), "sha256": .string(hash)
                ])))
            case "bootstrap.plan":
                delegate?.protocolSession(
                    self,
                    didPlanBootstrapTotal: message.payload["totalIconCount"]?.intValue ?? 0,
                    missing: message.payload["missingIconCount"]?.intValue ?? 0
                )
            case "bootstrap.completed":
                guard let configurationHash = message.payload["configurationHash"]?.stringValue,
                      configurationStore.confirms(hash: configurationHash),
                      let expectedIcons = stringDictionary(message.payload["iconHashes"]),
                      iconStore.contains(expectedIcons) else {
                    throw BluetoothPeripheralServiceError.invalidMessage
                }
                bootstrapReady = true
                delegate?.protocolSessionDidSynchronize(self)
            case "bootstrap.failed":
                bootstrapReady = false
                delegate?.protocolSession(
                    self,
                    didReceiveNotice: "同步失败",
                    message: message.payload["error"]?.stringValue ?? "Praxis Bluetooth 初始化未完成。",
                    isError: true
                )
            case "action.accepted", "action.progress", "action.completed", "action.failed":
                let event = PraxisActionEvent(message: message)
                delegate?.protocolSession(self, didReceiveAction: message.type, event: event)
                if message.type == "action.completed", event.actionID == "system.summary", let output = event.output {
                    delegate?.protocolSession(self, didReceiveSystemSummary: output)
                }
            case "pong":
                let started = pingStartedAt.removeValue(forKey: message.id)
                let latency = started.map { max(0, Int(Date().timeIntervalSince($0) * 1_000)) }
                delegate?.protocolSession(
                    self,
                    didReceiveNotice: "连接测试通过",
                    message: latency.map { "Praxis Bluetooth 已响应 · \($0) ms" } ?? "Praxis Bluetooth 已响应",
                    isError: false
                )
            case "protocol.error":
                delegate?.protocolSession(
                    self,
                    didReceiveNotice: "协议错误",
                    message: message.payload["error"]?.stringValue ?? "Praxis Bluetooth 无法处理该消息。",
                    isError: true
                )
            default:
                delegate?.protocolSession(
                    self,
                    didReceiveNotice: "未知消息",
                    message: message.type,
                    isError: true
                )
            }
        } catch { delegate?.protocolSession(self, didReceiveNotice: "消息解析失败", message: error.localizedDescription, isError: true) }
    }
}

extension PraxisProtocolSession: BluetoothPeripheralServiceDelegate {
    func peripheralService(_ service: BluetoothPeripheralService, didChange state: BluetoothConnectionState) {
        connectionState = state
        if !state.isConnected {
            invalidatePairingAttempt()
            bootstrapReady = false
            secureSession.reset()
            coreInfo = nil
            pingStartedAt.removeAll()
        }
        delegate?.protocolSession(self, didChange: state)
    }

    private func sendBootstrapPreparation() {
        var payload: [String: JSONValue] = [
            "iconHashes": .object(Dictionary(uniqueKeysWithValues: iconStore.cachedHashes().map {
                ($0.key, JSONValue.string($0.value))
            }))
        ]
        if let configurationHash = configurationStore.configurationHash {
            payload["configurationHash"] = .string(configurationHash)
        }
        do {
            try send(PraxisProtocolMessage(type: "bootstrap.prepare", payload: .object(payload)))
        } catch {
            delegate?.protocolSession(self, didReceiveNotice: "同步失败", message: error.localizedDescription, isError: true)
        }
    }

    private func stringDictionary(_ value: JSONValue?) -> [String: String]? {
        guard let object = value?.objectValue else { return nil }
        var result: [String: String] = [:]
        for (key, item) in object {
            guard let text = item.stringValue else { return nil }
            result[key] = text
        }
        return result
    }

    func peripheralService(_ service: BluetoothPeripheralService, didReceive message: Data) {
        handle(message)
    }

    func peripheralService(_ service: BluetoothPeripheralService, didRejectFrame error: Error) {
        delegate?.protocolSession(self, didReceiveNotice: "蓝牙帧已丢弃", message: error.localizedDescription, isError: true)
    }
}
