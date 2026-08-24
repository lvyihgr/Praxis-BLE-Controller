import Foundation

struct PraxisProtocolMessage: Codable, Equatable {
    let v: Int
    let id: String
    let type: String
    let timestamp: String
    let payload: JSONValue

    init(id: String = UUID().uuidString.lowercased(), type: String, payload: JSONValue) {
        self.v = 1
        self.id = id
        self.type = type
        self.timestamp = PraxisProtocolMessage.timestampFormatter.string(from: Date())
        self.payload = payload
    }

    var isValid: Bool {
        v == 1 && !id.isEmpty && id.count <= 128 && !type.isEmpty && type.count <= 128
    }

    private static let timestampFormatter: ISO8601DateFormatter = {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter
    }()
}

struct PraxisCoreInfo: Equatable {
    let serverName: String
    let version: String
    let protocolVersion: Int
    let transport: String
    let maximumPDUSize: Int?

    init(payload: JSONValue) {
        serverName = payload["serverName"]?.stringValue ?? "Praxis Bluetooth"
        version = payload["version"]?.stringValue ?? "—"
        protocolVersion = payload["protocolVersion"]?.intValue ?? 1
        transport = payload["transport"]?.stringValue ?? "bluetooth"
        maximumPDUSize = payload["maximumPduSize"]?.intValue
    }
}

struct PraxisActionEvent: Equatable {
    let commandID: String
    let actionID: String
    let controlID: String?
    let name: String?
    let message: String?
    let output: String?
    let percent: Int?
    let success: Bool?

    init(message: PraxisProtocolMessage) {
        let payload = message.payload.objectValue ?? [:]
        commandID = payload["commandId"]?.stringValue ?? message.id
        actionID = payload["actionId"]?.stringValue ?? ""
        controlID = payload["controlId"]?.stringValue
        name = payload["name"]?.stringValue
        self.message = payload["message"]?.stringValue
        output = payload["output"]?.stringValue
        percent = payload["percent"]?.intValue
        success = payload["success"]?.boolValue
    }
}
