import Foundation

struct PraxisStateEntry: Codable, Equatable {
    let key: String
    let value: JSONValue
    let status: String
    let label: String?
    let updatedAt: String
}

struct PraxisRecentPresentation: Codable, Equatable {
    let id: String
    let name: String
    let folder: String
    let path: String
    let isAvailable: Bool
}

struct PraxisPresentationState: Codable, Equatable {
    let status: String
    let isInstalled: Bool
    let isRunning: Bool
    let hasPresentation: Bool
    let isSlideShow: Bool
    let presentationName: String?
    let currentSlide: Int
    let totalSlides: Int
    let notes: String
    let elapsedSeconds: Int
    let error: String?
    let recentPresentations: [PraxisRecentPresentation]
    let revision: Int?
    let commandId: String?

    static let unavailable = PraxisPresentationState(
        status: "starting",
        isInstalled: false,
        isRunning: false,
        hasPresentation: false,
        isSlideShow: false,
        presentationName: nil,
        currentSlide: 0,
        totalSlides: 0,
        notes: "",
        elapsedSeconds: 0,
        error: nil,
        recentPresentations: [],
        revision: nil,
        commandId: nil
    )
}

protocol PraxisStateStoreDelegate: AnyObject {
    func stateStore(_ store: PraxisStateStore, didChange keys: Set<String>)
}

final class PraxisStateStore {
    weak var delegate: PraxisStateStoreDelegate?
    private(set) var entries: [String: PraxisStateEntry] = [:]

    func apply(snapshot: JSONValue) throws {
        let values = try snapshot.decoded(as: [PraxisStateEntry].self)
        entries = Dictionary(uniqueKeysWithValues: values.map { ($0.key.lowercased(), $0) })
        delegate?.stateStore(self, didChange: Set(values.map { $0.key }))
    }

    func apply(change: JSONValue) throws {
        let entry = try change.decoded(as: PraxisStateEntry.self)
        entries[entry.key.lowercased()] = entry
        delegate?.stateStore(self, didChange: [entry.key])
    }

    func entry(for key: String) -> PraxisStateEntry? {
        entries[key.lowercased()]
    }

    func boolValue(for key: String) -> Bool {
        guard let entry = entry(for: key) else { return false }
        return entry.value.boolValue == true || entry.status.lowercased() == "active"
    }

    var foregroundApplication: String {
        entry(for: "system.foregroundApp")?.value.stringValue ?? "—"
    }

    var presentation: PraxisPresentationState {
        guard let value = entry(for: "presentation.state")?.value,
              let decoded = try? value.decoded(as: PraxisPresentationState.self) else {
            return .unavailable
        }
        return decoded
    }
}
