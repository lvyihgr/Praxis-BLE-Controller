import UIKit

final class HeaderActionButton: UIControl {
    private let glyphView: PraxisGlyphView
    private let feedbackView = UIView()
    private var resultResetWorkItem: DispatchWorkItem?

    init(glyph: PraxisGlyph, accessibilityLabel: String) {
        glyphView = PraxisGlyphView(glyph: glyph)
        super.init(frame: .zero)
        isAccessibilityElement = true
        self.accessibilityLabel = accessibilityLabel
        layer.cornerRadius = 11
        layer.borderWidth = 1
        layer.borderColor = PraxisTheme.line.cgColor
        backgroundColor = .clear

        feedbackView.backgroundColor = PraxisTheme.systemBlue.withAlphaComponent(0.12)
        feedbackView.layer.cornerRadius = 10
        feedbackView.alpha = 0
        addSubview(feedbackView)
        feedbackView.pinEdges(to: self)

        glyphView.color = PraxisTheme.text.withAlphaComponent(0.82)
        addSubview(glyphView)
        glyphView.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            widthAnchor.constraint(equalToConstant: 44),
            heightAnchor.constraint(equalToConstant: 44),
            glyphView.centerXAnchor.constraint(equalTo: centerXAnchor),
            glyphView.centerYAnchor.constraint(equalTo: centerYAnchor),
            glyphView.widthAnchor.constraint(equalToConstant: 19),
            glyphView.heightAnchor.constraint(equalToConstant: 19)
        ])

        addTarget(self, action: #selector(pressBegan), for: .touchDown)
        addTarget(self, action: #selector(pressEnded), for: [.touchUpInside, .touchUpOutside, .touchCancel])
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func hitTest(_ point: CGPoint, with event: UIEvent?) -> UIView? {
        guard isUserInteractionEnabled, !isHidden, alpha > 0.01,
              self.point(inside: point, with: event) else { return nil }
        return self
    }

    override var isEnabled: Bool {
        didSet { alpha = isEnabled ? 1 : 0.38 }
    }

    func showResult(error: Bool) {
        resultResetWorkItem?.cancel()
        layer.removeAllAnimations()
        glyphView.layer.removeAllAnimations()
        layer.borderColor = (error ? PraxisTheme.danger : PraxisTheme.systemBlue).withAlphaComponent(0.58).cgColor
        glyphView.color = error ? PraxisTheme.danger : PraxisTheme.systemBlue
        feedbackView.backgroundColor = (error ? PraxisTheme.danger : PraxisTheme.systemBlue).withAlphaComponent(0.12)
        feedbackView.alpha = 1
        let item = DispatchWorkItem { [weak self] in
            guard let self = self else { return }
            UIView.animate(withDuration: 0.20, delay: 0, options: [.beginFromCurrentState, .allowUserInteraction], animations: {
                self.layer.borderColor = PraxisTheme.line.cgColor
                self.glyphView.color = PraxisTheme.text.withAlphaComponent(0.82)
                self.feedbackView.alpha = 0
            })
        }
        resultResetWorkItem = item
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.72, execute: item)
    }

    @objc private func pressBegan() {
        guard isEnabled else { return }
        UIView.animate(withDuration: 0.16, delay: 0, options: [.beginFromCurrentState, .allowUserInteraction], animations: {
            self.transform = CGAffineTransform(scaleX: 0.95, y: 0.95)
            self.feedbackView.alpha = 1
            self.layer.borderColor = PraxisTheme.systemBlue.withAlphaComponent(0.55).cgColor
        })
    }

    @objc private func pressEnded() {
        UIView.animate(withDuration: 0.16, delay: 0, options: [.beginFromCurrentState, .allowUserInteraction], animations: {
            self.transform = .identity
            self.feedbackView.alpha = 0
            self.layer.borderColor = PraxisTheme.line.cgColor
        })
    }
}
