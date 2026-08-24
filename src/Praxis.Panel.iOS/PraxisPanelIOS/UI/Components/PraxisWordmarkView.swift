import UIKit

final class PraxisWordmarkView: UIView {
    private let patterns: [[String]] = [
        ["11110", "10001", "10001", "11110", "10000", "10000", "10000"],
        ["11110", "10001", "10001", "11110", "10100", "10010", "10001"],
        ["01110", "10001", "10001", "11111", "10001", "10001", "10001"],
        ["10001", "01010", "00100", "00100", "00100", "01010", "10001"],
        ["11111", "00100", "00100", "00100", "00100", "00100", "11111"],
        ["01111", "10000", "10000", "01110", "00001", "00001", "11110"]
    ]

    override init(frame: CGRect) {
        super.init(frame: frame)
        backgroundColor = .clear
        contentMode = .redraw
        isUserInteractionEnabled = false
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func draw(_ rect: CGRect) {
        let columns = CGFloat(patterns.count * 5 + (patterns.count - 1) * 2)
        let dot = min(rect.height / 9.2, rect.width / (columns + 2))
        let gap = dot * 0.55
        let glyphWidth = dot * 5 + gap * 4
        let letterGap = dot * 1.5
        let totalWidth = glyphWidth * CGFloat(patterns.count) + letterGap * CGFloat(patterns.count - 1)
        var originX = max(0, (rect.width - totalWidth) / 2)
        let originY = max(0, (rect.height - (dot * 7 + gap * 6)) / 2)
        PraxisTheme.text.setFill()

        for pattern in patterns {
            for row in 0..<7 {
                let values = Array(pattern[row])
                for column in 0..<5 where values[column] == "1" {
                    let frame = CGRect(
                        x: originX + CGFloat(column) * (dot + gap),
                        y: originY + CGFloat(row) * (dot + gap),
                        width: dot,
                        height: dot
                    )
                    UIBezierPath(ovalIn: frame).fill()
                }
            }
            originX += glyphWidth + letterGap
        }
    }
}
