import Foundation
import Security

enum PraxisPairingEvent {
    case send(PraxisProtocolMessage)
    case requiresCode(serverName: String)
    case activated
    case failed(String)
    case ignored
}

enum PraxisSecureSessionError: Error, LocalizedError {
    case inactive
    case invalidEnvelope
    case authenticationFailed
    case replayedMessage
    case noChallenge
    case pairingSessionChanged

    var errorDescription: String? {
        switch self {
        case .inactive: return "尚未与 Praxis Bluetooth 完成安全配对。"
        case .invalidEnvelope: return "收到的安全消息格式无效。"
        case .authenticationFailed: return "无法验证 Praxis Bluetooth 消息。"
        case .replayedMessage: return "已拒绝重复或过期的 Praxis 消息。"
        case .noChallenge: return "当前没有等待输入的配对会话。"
        case .pairingSessionChanged: return "配对会话已更新，请重新输入配对码。"
        }
    }
}

final class PraxisSecureSession {
    private struct Handshake {
        let token: UUID
        let serverID: String
        let serverName: String
        let deviceID: String
        let serverNonce: String
        let salt: Data?
        let iterations: Int
        let root: Data?
        var clientNonce: String?
        var candidateRoot: Data?
    }

    private struct PreparedPairing {
        let token: UUID
        let clientNonce: String
        let candidateRoot: Data
        let request: PraxisProtocolMessage
    }

    private var handshake: Handshake?
    private var key: Data?
    private var sessionID: String?
    private var outboundSequence: UInt64 = 0
    private var inboundSequence: UInt64 = 0

    var isActive: Bool { key != nil && sessionID != nil }

    func reset() {
        handshake = nil; key = nil; sessionID = nil; outboundSequence = 0; inboundSequence = 0
    }

    func handlePairing(_ message: PraxisProtocolMessage) -> PraxisPairingEvent {
        switch message.type {
        case "pairing.challenge":
            guard let serverID = message.payload["serverId"]?.stringValue,
                  let deviceID = message.payload["deviceId"]?.stringValue,
                  let serverNonce = message.payload["serverNonce"]?.stringValue,
                  let saltText = message.payload["salt"]?.stringValue,
                  let salt = Data(base64Encoded: saltText),
                  let iterations = message.payload["iterations"]?.intValue else {
                return .failed(PraxisSecureSessionError.invalidEnvelope.localizedDescription)
            }
            handshake = Handshake(
                token: UUID(),
                serverID: serverID,
                serverName: message.payload["serverName"]?.stringValue ?? "Praxis Bluetooth",
                deviceID: deviceID,
                serverNonce: serverNonce,
                salt: salt,
                iterations: iterations,
                root: nil,
                clientNonce: nil,
                candidateRoot: nil
            )
            return .requiresCode(serverName: handshake!.serverName)

        case "pairing.resume":
            guard let serverID = message.payload["serverId"]?.stringValue,
                  let deviceID = message.payload["deviceId"]?.stringValue,
                  let serverNonce = message.payload["serverNonce"]?.stringValue else {
                return .failed(PraxisSecureSessionError.invalidEnvelope.localizedDescription)
            }
            guard let root = PairingKeychain.load(serverID: serverID) else {
                return .send(PraxisProtocolMessage(type: "pairing.reset", payload: .object(["deviceId": .string(deviceID)])))
            }
            do {
                let clientNonce = try PraxisCrypto.randomData(count: 24).base64EncodedString()
                handshake = Handshake(token: UUID(), serverID: serverID, serverName: "Praxis Bluetooth", deviceID: deviceID,
                                      serverNonce: serverNonce, salt: nil, iterations: 0, root: root,
                                      clientNonce: clientNonce, candidateRoot: nil)
                let proof = PraxisCrypto.hmac(key: root, fields: ["resume-client-v1", serverID, deviceID, serverNonce, clientNonce])
                return .send(PraxisProtocolMessage(type: "pairing.resumeProof", payload: .object([
                    "deviceId": .string(deviceID), "clientNonce": .string(clientNonce), "proof": .string(proof.base64EncodedString())
                ])))
            } catch { return .failed(error.localizedDescription) }

        case "pairing.completed":
            return complete(message, resumed: false)
        case "pairing.resumed":
            return complete(message, resumed: true)
        case "pairing.failed":
            return .failed(message.payload["error"]?.stringValue ?? "Praxis Bluetooth 拒绝了配对请求。")
        default:
            return .ignored
        }
    }

    func submit(
        code: String,
        workQueue: DispatchQueue,
        deriveKey: @escaping (String, Data, Int) throws -> Data = { code, salt, iterations in
            try PraxisCrypto.deriveCodeKey(code: code, salt: salt, iterations: iterations)
        },
        completion: @escaping (Result<PraxisProtocolMessage, Error>) -> Void
    ) {
        guard let current = handshake, let salt = current.salt else {
            completion(.failure(PraxisSecureSessionError.noChallenge))
            return
        }

        // PBKDF2 deliberately performs substantial work. Running it on the
        // Core Bluetooth/main queue stalls GATT callbacks on older iPads and
        // can leave the pairing screen waiting even though the code is valid.
        workQueue.async {
            let prepared = Result<PreparedPairing, Error> {
                let clientNonce = try PraxisCrypto.randomData(count: 24).base64EncodedString()
                let codeKey = try deriveKey(code, salt, current.iterations)
                let proof = PraxisCrypto.hmac(
                    key: codeKey,
                    fields: ["pair-v1", current.serverID, current.deviceID, current.serverNonce, clientNonce]
                )
                let root = PraxisCrypto.hmac(
                    key: codeKey,
                    fields: ["root-v1", current.serverID, current.deviceID, current.serverNonce, clientNonce]
                )
                return PreparedPairing(
                    token: current.token,
                    clientNonce: clientNonce,
                    candidateRoot: root,
                    request: PraxisProtocolMessage(type: "pairing.request", payload: .object([
                        "deviceId": .string(current.deviceID),
                        "clientNonce": .string(clientNonce),
                        "proof": .string(proof.base64EncodedString())
                    ]))
                )
            }

            DispatchQueue.main.async {
                switch prepared {
                case .success(let value):
                    guard var latest = self.handshake, latest.token == value.token else {
                        completion(.failure(PraxisSecureSessionError.pairingSessionChanged))
                        return
                    }
                    latest.clientNonce = value.clientNonce
                    latest.candidateRoot = value.candidateRoot
                    self.handshake = latest
                    completion(.success(value.request))
                case .failure(let error):
                    completion(.failure(error))
                }
            }
        }
    }

    func wrap(_ message: PraxisProtocolMessage, encoder: JSONEncoder) throws -> PraxisProtocolMessage {
        guard let key = key, let sessionID = sessionID else { throw PraxisSecureSessionError.inactive }
        outboundSequence &+= 1
        let body = try encoder.encode(message)
        let mac = authenticationCode(key: key, sessionID: sessionID, sequence: outboundSequence, body: body)
        guard let bodyText = String(data: body, encoding: .utf8) else { throw PraxisSecureSessionError.invalidEnvelope }
        return PraxisProtocolMessage(type: "secure.message", payload: .object([
            "sessionId": .string(sessionID), "sequence": .number(Double(outboundSequence)),
            "body": .string(bodyText), "mac": .string(mac.base64EncodedString())
        ]))
    }

    func unwrap(_ envelope: PraxisProtocolMessage, decoder: JSONDecoder) throws -> PraxisProtocolMessage {
        guard envelope.type == "secure.message", let key = key, let sessionID = sessionID,
              envelope.payload["sessionId"]?.stringValue == sessionID,
              let sequenceValue = envelope.payload["sequence"]?.intValue, sequenceValue > 0,
              let bodyText = envelope.payload["body"]?.stringValue, let body = bodyText.data(using: .utf8),
              let macText = envelope.payload["mac"]?.stringValue, let mac = Data(base64Encoded: macText) else {
            throw PraxisSecureSessionError.invalidEnvelope
        }
        let sequence = UInt64(sequenceValue)
        guard sequence > inboundSequence else { throw PraxisSecureSessionError.replayedMessage }
        let expected = authenticationCode(key: key, sessionID: sessionID, sequence: sequence, body: body)
        guard PraxisCrypto.timingSafeEqual(mac, expected) else { throw PraxisSecureSessionError.authenticationFailed }
        let message = try decoder.decode(PraxisProtocolMessage.self, from: body)
        guard message.isValid else { throw PraxisSecureSessionError.invalidEnvelope }
        inboundSequence = sequence
        return message
    }

    private func complete(_ message: PraxisProtocolMessage, resumed: Bool) -> PraxisPairingEvent {
        guard let current = handshake, let clientNonce = current.clientNonce,
              let sessionID = message.payload["sessionId"]?.stringValue,
              let serverProofText = message.payload["serverProof"]?.stringValue,
              let serverProof = Data(base64Encoded: serverProofText),
              let root = resumed ? current.root : current.candidateRoot else {
            return .failed(PraxisSecureSessionError.invalidEnvelope.localizedDescription)
        }
        let label = resumed ? "resume-server-v1" : "server-v1"
        let expected = PraxisCrypto.hmac(key: root, fields: [label, current.serverID, current.deviceID, current.serverNonce, clientNonce])
        guard PraxisCrypto.timingSafeEqual(serverProof, expected) else {
            return .failed(PraxisSecureSessionError.authenticationFailed.localizedDescription)
        }
        let expectedSessionID = PraxisCrypto.sessionID(["session-id-v1", current.deviceID, current.serverNonce, clientNonce])
        guard sessionID == expectedSessionID else { return .failed(PraxisSecureSessionError.authenticationFailed.localizedDescription) }
        key = PraxisCrypto.hmac(key: root, fields: ["session-key-v1", sessionID, current.deviceID, current.serverNonce, clientNonce])
        self.sessionID = sessionID; outboundSequence = 0; inboundSequence = 0
        if !resumed { PairingKeychain.save(root, serverID: current.serverID) }
        handshake = nil
        return .activated
    }

    private func authenticationCode(key: Data, sessionID: String, sequence: UInt64, body: Data) -> Data {
        var input = Data(sessionID.utf8); input.append(0)
        var sequence = sequence.bigEndian
        Swift.withUnsafeBytes(of: &sequence) { input.append(contentsOf: $0) }
        input.append(body)
        return PraxisCrypto.hmac(key: key, data: input)
    }
}

private enum PairingKeychain {
    private static let service = "com.praxis.panel.pairing"

    static func load(serverID: String) -> Data? {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                    kSecAttrService as String: service,
                                    kSecAttrAccount as String: serverID,
                                    kSecReturnData as String: true,
                                    kSecMatchLimit as String: kSecMatchLimitOne]
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess else { return nil }
        return result as? Data
    }

    static func save(_ data: Data, serverID: String) {
        let identity: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                       kSecAttrService as String: service,
                                       kSecAttrAccount as String: serverID]
        let update = [kSecValueData as String: data]
        if SecItemUpdate(identity as CFDictionary, update as CFDictionary) == errSecItemNotFound {
            var item = identity; item[kSecValueData as String] = data
            SecItemAdd(item as CFDictionary, nil)
        }
    }
}
