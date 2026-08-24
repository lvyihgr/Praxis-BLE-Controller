import Foundation

struct PraxisConfiguration: Codable {
    let schemaVersion: Int
    let activeProfileId: String
    let profiles: [PraxisProfile]
    let actions: [PraxisActionDefinition]
}

struct PraxisProfile: Codable {
    let id: String
    let name: String
    let pages: [PraxisPageDefinition]
}

struct PraxisPageDefinition: Codable {
    let id: String
    let name: String
    let columns: Int?
    let controls: [PraxisControlDefinition]?
    let groups: [PraxisControlGroupDefinition]?
}

struct PraxisControlGroupDefinition: Codable {
    let id: String
    let name: String
    let columns: Int
    let size: String
    let controls: [PraxisControlDefinition]
}

struct PraxisControlDefinition: Codable {
    let id: String
    let label: String
    let subtitle: String?
    let icon: String?
    let color: String
    let actionId: String
    let stateBinding: String?
}

struct PraxisActionDefinition: Codable {
    let id: String
    let name: String
    let type: String
    let parameters: [String: String]
    let timeoutSeconds: Int
    let requiresConfirmation: Bool?
}

protocol PraxisConfigurationStoreDelegate: AnyObject {
    func configurationStoreDidUpdate(_ store: PraxisConfigurationStore)
}

final class PraxisConfigurationStore {
    weak var delegate: PraxisConfigurationStoreDelegate?
    private(set) var configuration: PraxisConfiguration?
    private(set) var controlsByID: [String: PraxisControlDefinition] = [:]
    private(set) var actionsByID: [String: PraxisActionDefinition] = [:]
    private(set) var configurationHash: String?
    private let cacheURL: URL

    private let knownLabels: [String: String] = [
        "chatgpt": "ChatGPT", "zcode": "ZCode", "trae-solo": "TRAE SOLO", "trae": "Trae",
        "edge": "Edge", "feishu": "飞书", "vscode": "VS Code",
        "task-view": "任务视图", "screenshot": "截图工具", "explorer": "文件管理", "notepad": "记事本",
        "terminal": "终端", "calculator": "计算器", "desktop-view": "显示桌面",
        "voice-input": "语音输入", "play": "播放 / 暂停", "mute": "静音 / 取消静音",
        "volume-down": "音量降低", "volume-up": "音量提高", "copy": "复制", "paste": "粘贴"
    ]

    init(fileManager: FileManager = .default, cacheRoot: URL? = nil) {
        let directory = cacheRoot ?? (fileManager.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSTemporaryDirectory(), isDirectory: true))
            .appendingPathComponent("PraxisPanel", isDirectory: true)
        try? fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        var resourceValues = URLResourceValues()
        resourceValues.isExcludedFromBackup = true
        var excludedDirectory = directory
        try? excludedDirectory.setResourceValues(resourceValues)
        cacheURL = directory.appendingPathComponent("configuration-cache.json", isDirectory: false)
        loadCachedConfiguration()
    }

    func apply(snapshot: JSONValue) throws {
        let hash = snapshot["configurationHash"]?.stringValue
        let decoded = try snapshot.decoded(as: PraxisConfiguration.self)
        apply(decoded: decoded, hash: hash)
        if let data = try? JSONEncoder().encode(snapshot) {
            try? data.write(to: cacheURL, options: [.atomic])
        }
        delegate?.configurationStoreDidUpdate(self)
    }

    func confirms(hash: String) -> Bool {
        guard configuration != nil,
              let current = configurationHash else { return false }
        return current.caseInsensitiveCompare(hash) == .orderedSame
    }

    private func apply(decoded: PraxisConfiguration, hash: String?) {
        var controlMap: [String: PraxisControlDefinition] = [:]
        for profile in decoded.profiles {
            for page in profile.pages {
                for control in page.controls ?? [] { controlMap[control.id] = control }
                for group in page.groups ?? [] {
                    for control in group.controls { controlMap[control.id] = control }
                }
            }
        }
        configuration = decoded
        configurationHash = hash
        controlsByID = controlMap
        actionsByID = Dictionary(uniqueKeysWithValues: decoded.actions.map { ($0.id, $0) })
    }

    private func loadCachedConfiguration() {
        guard let data = try? Data(contentsOf: cacheURL),
              let snapshot = try? JSONDecoder().decode(JSONValue.self, from: data),
              let decoded = try? snapshot.decoded(as: PraxisConfiguration.self) else { return }
        apply(decoded: decoded, hash: snapshot["configurationHash"]?.stringValue)
    }

    func actionID(for controlID: String) -> String? {
        controlsByID[controlID]?.actionId
    }

    func stateBinding(for controlID: String) -> String? {
        controlsByID[controlID]?.stateBinding
    }

    func displayName(for controlID: String, fallback: String) -> String {
        knownLabels[controlID] ?? controlsByID[controlID]?.label ?? fallback
    }

    func hasAction(_ actionID: String) -> Bool {
        actionsByID[actionID] != nil
    }

    func timeoutSeconds(for actionID: String) -> Int? {
        actionsByID[actionID]?.timeoutSeconds
    }
}
