import UIKit

final class PresentationActionButton: UIControl {
    private let glyphView: PraxisGlyphView
    private let titleLabel = UILabel()
    private let vertical: Bool
    private let danger: Bool
    private var isBusy = false
    private var isErrorState = false
    private var resultResetWorkItem: DispatchWorkItem?

    init(title: String, glyph: PraxisGlyph, vertical: Bool = false, danger: Bool = false) {
        self.glyphView = PraxisGlyphView(glyph: glyph)
        self.vertical = vertical
        self.danger = danger
        super.init(frame: .zero)
        PraxisTheme.configureCard(self, radius: vertical ? 16 : 13)
        if danger {
            layer.borderColor = PraxisTheme.danger.withAlphaComponent(0.32).cgColor
            backgroundColor = PraxisTheme.danger.withAlphaComponent(0.035)
        }
        glyphView.color = danger ? PraxisTheme.danger : PraxisTheme.systemBlue.withAlphaComponent(0.82)
        titleLabel.text = title
        titleLabel.textColor = danger ? PraxisTheme.danger.withAlphaComponent(0.92) : PraxisTheme.text.withAlphaComponent(0.88)
        titleLabel.font = UIFont.systemFont(ofSize: vertical ? 14 : 13, weight: .semibold)
        titleLabel.textAlignment = .center
        let stack = UIStackView(arrangedSubviews: [glyphView, titleLabel])
        stack.axis = vertical ? .vertical : .horizontal
        stack.alignment = .center
        stack.spacing = vertical ? 12 : 9
        addSubview(stack)
        stack.translatesAutoresizingMaskIntoConstraints = false
        glyphView.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            stack.centerXAnchor.constraint(equalTo: centerXAnchor),
            stack.centerYAnchor.constraint(equalTo: centerYAnchor),
            glyphView.widthAnchor.constraint(equalToConstant: vertical ? 38 : 20),
            glyphView.heightAnchor.constraint(equalToConstant: vertical ? 38 : 20),
            heightAnchor.constraint(greaterThanOrEqualToConstant: 48)
        ])
        addTarget(self, action: #selector(pressBegan), for: .touchDown)
        addTarget(self, action: #selector(pressEnded), for: [.touchUpInside, .touchUpOutside, .touchCancel, .touchDragExit])
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func hitTest(_ point: CGPoint, with event: UIEvent?) -> UIView? {
        guard isUserInteractionEnabled, !isHidden, alpha > 0.01,
              self.point(inside: point, with: event) else { return nil }
        return self
    }

    override var isEnabled: Bool {
        didSet { alpha = isEnabled ? 1 : 0.34 }
    }

    func setBusy(_ busy: Bool, error: Bool = false) {
        resultResetWorkItem?.cancel()
        isBusy = busy
        isErrorState = error
        layer.borderColor = actionBorderColor().cgColor
        backgroundColor = busy ? PraxisTheme.systemBlue.withAlphaComponent(0.10) : (danger ? PraxisTheme.danger.withAlphaComponent(0.035) : PraxisTheme.surface)
        if error {
            let item = DispatchWorkItem { [weak self] in self?.setBusy(false) }
            resultResetWorkItem = item
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.90, execute: item)
        }
    }

    @objc private func pressBegan() {
        guard isEnabled else { return }
        UIView.animate(withDuration: 0.16, delay: 0, options: [.beginFromCurrentState, .allowUserInteraction], animations: {
            self.transform = CGAffineTransform(scaleX: 0.97, y: 0.97)
            self.layer.borderColor = PraxisTheme.systemBlue.withAlphaComponent(0.52).cgColor
            self.backgroundColor = PraxisTheme.systemBlue.withAlphaComponent(0.10)
        })
    }

    @objc private func pressEnded() {
        UIView.animate(withDuration: 0.16, delay: 0, options: [.beginFromCurrentState, .allowUserInteraction], animations: {
            self.transform = .identity
            self.layer.borderColor = self.actionBorderColor().cgColor
            self.backgroundColor = self.isBusy ? PraxisTheme.systemBlue.withAlphaComponent(0.10) : (self.danger ? PraxisTheme.danger.withAlphaComponent(0.035) : PraxisTheme.surface)
        })
    }

    private func actionBorderColor() -> UIColor {
        if isErrorState { return PraxisTheme.danger.withAlphaComponent(0.48) }
        if isBusy { return PraxisTheme.systemBlue.withAlphaComponent(0.48) }
        if danger { return PraxisTheme.danger.withAlphaComponent(0.32) }
        return PraxisTheme.line
    }
}
