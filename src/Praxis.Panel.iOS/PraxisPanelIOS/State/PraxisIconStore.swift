import Foundation
import UIKit

final class PraxisIconStore {
    private var memory: [String: UIImage] = [:]
    private let directory: URL
    private let manifestURL: URL
    private let fileManager: FileManager
    private var hashes: [String: String] = [:]

    init(fileManager: FileManager = .default, cacheRoot: URL? = nil) {
        self.fileManager = fileManager
        let root = cacheRoot ?? (fileManager.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSTemporaryDirectory(), isDirectory: true))
            .appendingPathComponent("PraxisPanel", isDirectory: true)
        directory = root.appendingPathComponent("AppIcons", isDirectory: true)
        manifestURL = root.appendingPathComponent("icon-manifest.json", isDirectory: false)
        try? fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        var resourceValues = URLResourceValues()
        resourceValues.isExcludedFromBackup = true
        var excludedRoot = root
        try? excludedRoot.setResourceValues(resourceValues)
        if let data = try? Data(contentsOf: manifestURL),
           let stored = try? JSONDecoder().decode([String: String].self, from: data) {
            hashes = stored
        }
    }

    func image(for actionID: String) -> UIImage? {
        if let cached = memory[actionID] { return cached }
        guard let expectedHash = hashes[actionID] else { return nil }
        let url = fileURL(for: actionID)
        guard let data = try? Data(contentsOf: url),
              hash(of: data).caseInsensitiveCompare(expectedHash) == .orderedSame,
              let image = UIImage(data: data) else { return nil }
        memory[actionID] = image
        return image
    }

    func cachedHashes() -> [String: String] {
        var available: [String: String] = [:]
        for (actionID, expectedHash) in hashes {
            guard let data = try? Data(contentsOf: fileURL(for: actionID)),
                  hash(of: data).caseInsensitiveCompare(expectedHash) == .orderedSame else { continue }
            available[actionID] = expectedHash
        }
        return available
    }

    func contains(_ expected: [String: String]) -> Bool {
        let available = cachedHashes()
        return expected.allSatisfy { actionID, hash in
            available[actionID]?.caseInsensitiveCompare(hash) == .orderedSame
        }
    }

    @discardableResult
    func apply(actionID: String, base64: String, expectedHash: String? = nil) -> UIImage? {
        guard !actionID.isEmpty,
              let data = Data(base64Encoded: base64, options: [.ignoreUnknownCharacters]),
              data.count <= 256 * 1024,
              let image = UIImage(data: data) else { return nil }
        let actualHash = hash(of: data)
        if let expectedHash = expectedHash,
           actualHash.caseInsensitiveCompare(expectedHash) != .orderedSame {
            return nil
        }
        memory[actionID] = image
        try? data.write(to: fileURL(for: actionID), options: [.atomic])
        hashes[actionID] = actualHash
        persistManifest()
        return image
    }

    private func persistManifest() {
        guard let data = try? JSONEncoder().encode(hashes) else { return }
        try? data.write(to: manifestURL, options: [.atomic])
    }

    private func hash(of data: Data) -> String {
        PraxisCrypto.sha256(data).map { String(format: "%02x", $0) }.joined()
    }

    private func fileURL(for actionID: String) -> URL {
        let safeName = actionID.unicodeScalars.map { scalar -> Character in
            CharacterSet.alphanumerics.contains(scalar) ? Character(String(scalar)) : "_"
        }
        return directory.appendingPathComponent(String(safeName) + ".png", isDirectory: false)
    }
}
