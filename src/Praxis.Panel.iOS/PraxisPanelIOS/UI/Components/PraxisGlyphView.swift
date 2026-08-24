import UIKit

enum PraxisGlyph: Equatable {
    case taskView, screenshot, folder, note, terminal, calculator, desktop
    case microphone, playPause, mute, volumeDown, volumeUp, copy, paste
    case link, connectionStatus, summary, refresh, presentation, playBeginning, playCurrent
    case previous, next, endPresentation, notes, recentPresentations, clock, close

    var assetName: String {
        switch self {
        case .taskView: return "PraxisGlyphTaskView"
        case .screenshot: return "PraxisGlyphScreenshot"
        case .folder: return "PraxisGlyphFolder"
        case .note: return "PraxisGlyphNote"
        case .terminal: return "PraxisGlyphTerminal"
        case .calculator: return "PraxisGlyphCalculator"
        case .desktop: return "PraxisGlyphDesktop"
        case .microphone: return "PraxisGlyphMicrophone"
        case .playPause: return "PraxisGlyphPlayPause"
        case .mute: return "PraxisGlyphMute"
        case .volumeDown: return "PraxisGlyphVolumeDown"
        case .volumeUp: return "PraxisGlyphVolumeUp"
        case .copy: return "PraxisGlyphCopy"
        case .paste: return "PraxisGlyphPaste"
        case .link: return "PraxisGlyphLink"
        case .connectionStatus: return "PraxisGlyphConnectionStatus"
        case .summary: return "PraxisGlyphSummary"
        case .refresh: return "PraxisGlyphRefresh"
        case .presentation: return "PraxisGlyphPresentation"
        case .playBeginning: return "PraxisGlyphPlayBeginning"
        case .playCurrent: return "PraxisGlyphPlayCurrent"
        case .previous: return "PraxisGlyphPrevious"
        case .next: return "PraxisGlyphNext"
        case .endPresentation: return "PraxisGlyphEndPresentation"
        case .notes: return "PraxisGlyphNotes"
        case .recentPresentations: return "PraxisGlyphRecentPresentations"
        case .clock: return "PraxisGlyphClock"
        case .close: return "PraxisGlyphClose"
        }
    }
}

/// Local Lucide template assets keep every functional glyph available on iOS
/// 12 without depending on SF Symbols or an active Bluetooth connection.
final class PraxisGlyphView: UIImageView {
    var glyph: PraxisGlyph { didSet { updateImage() } }
    var color: UIColor = PraxisTheme.systemBlue { didSet { tintColor = color } }

    init(glyph: PraxisGlyph) {
        self.glyph = glyph
        super.init(frame: .zero)
        backgroundColor = .clear
        contentMode = .scaleAspectFit
        isUserInteractionEnabled = false
        tintColor = color
        updateImage()
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    private func updateImage() {
        image = UIImage(named: glyph.assetName)?.withRenderingMode(.alwaysTemplate)
    }
}
