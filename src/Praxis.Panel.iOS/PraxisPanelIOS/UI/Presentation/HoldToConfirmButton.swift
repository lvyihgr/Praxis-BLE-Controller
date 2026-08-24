import UIKit

final class HoldToConfirmButton: UIControl {
    var onConfirmed: (() -> Void)?
    private let duration: TimeInterval = 0.65
    private let progress = UIView()
    private var progressWidthConstraint: NSLayoutConstraint!
    private var workItem: DispatchWorkItem?
    private var didConfirm = false
    private var isActionBusy = false
    private var isErrorState = false
    private var resultResetWorkItem: DispatchWorkItem?

    override init(frame: CGRect) {
        super.init(frame: frame)
        layer.cornerRadius = 13
        layer.borderWidth = 1
        layer.borderColor = PraxisTheme.danger.withAlphaComponent(0.32).cgColor
        backgroundColor = PraxisTheme.danger.withAlphaComponent(0.035)
        clipsToBounds = true

        progress.backgroundColor = PraxisTheme.danger.withAlphaComponent(0.20)
        progress.isUserInteractionEnabled = false
        addSubview(progress)
        progress.translatesAutoresizingMaskIntoConstraints = false
        progressWidthConstraint = progress.widthAnchor.constraint(equalToConstant: 0)
        NSLayoutConstraint.activate([
            progress.leadingAnchor.constraint(equalTo: leadingAnchor),
            progress.topAnchor.constraint(equalTo: topAnchor),
            progress.bottomAnchor.constraint(equalTo: bottomAnchor),
            progressWidthConstraint
        ])

        let glyph = PraxisGlyphView(glyph: .endPresentation)
        glyph.color = PraxisTheme.danger
        let label = UILabel()
        label.text = "长按结束放映"
        label.font = UIFont.systemFont(ofSize: 13, weight: .semibold)
        label.textColor = PraxisTheme.danger.withAlphaComponent(0.92)
        let stack = UIStackView(arrangedSubviews: [glyph, label])
        stack.axis = .horizontal
        stack.alignment = .center
        stack.spacing = 9
        stack.isUserInteractionEnabled = false
        addSubview(stack)
        stack.translatesAutoresizingMaskIntoConstraints = false
        glyph.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            heightAnchor.constraint(greaterThanOrEqualToConstant: 48),
            stack.centerXAnchor.constraint(equalTo: centerXAnchor),
            stack.centerYAnchor.constraint(equalTo: centerYAnchor),
            glyph.widthAnchor.constraint(equalToConstant: 20),
            glyph.heightAnchor.constraint(equalToConstant: 20)
        ])
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func hitTest(_ point: CGPoint, with event: UIEvent?) -> UIView? {
        guard isUserInteractionEnabled, !isHidden, alpha > 0.01,
              self.point(inside: point, with: event) else { return nil }
        return self
    }

    override var isEnabled: Bool {
        didSet {
            alpha = isEnabled ? 1 : 0.34
            if !isEnabled { cancelHold() }
        }
    }

    func setBusy(_ busy: Bool, error: Bool = false) {
        resultResetWorkItem?.cancel()
        isActionBusy = busy
        isErrorState = error
        let border = error ? PraxisTheme.danger.withAlphaComponent(0.72) : PraxisTheme.danger.withAlphaComponent(busy ? 0.56 : 0.32)
        UIView.animate(withDuration: 0.16, delay: 0, options: [.beginFromCurrentState, .allowUserInteraction], animations: {
            self.layer.borderColor = border.cgColor
            self.backgroundColor = PraxisTheme.danger.withAlphaComponent(busy || error ? 0.10 : 0.035)
        })
        if error {
            let item = DispatchWorkItem { [weak self] in self?.setBusy(false) }
            resultResetWorkItem = item
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.90, execute: item)
        }
    }

    override func beginTracking(_ touch: UITouch, with event: UIEvent?) -> Bool {
        guard isEnabled else { return false }
        didConfirm = false
        layer.borderColor = PraxisTheme.danger.withAlphaComponent(0.68).cgColor
        progressWidthConstraint.constant = 0
        layoutIfNeeded()
        progressWidthConstraint.constant = bounds.width
        UIView.animate(withDuration: duration, delay: 0, options: [.curveLinear, .beginFromCurrentState, .allowUserInteraction], animations: {
            self.layoutIfNeeded()
        })
        let item = DispatchWorkItem { [weak self] in
            guard let self = self, self.isTracking else { return }
            self.didConfirm = true
            self.onConfirmed?()
            UIView.animate(withDuration: 0.14) { self.transform = CGAffineTransform(scaleX: 0.98, y: 0.98) }
        }
        workItem = item
        DispatchQueue.main.asyncAfter(deadline: .now() + duration, execute: item)
        return true
    }

    override func continueTracking(_ touch: UITouch, with event: UIEvent?) -> Bool {
        if !bounds.insetBy(dx: -12, dy: -12).contains(touch.location(in: self)) {
            cancelHold()
            return false
        }
        return true
    }

    override func endTracking(_ touch: UITouch?, with event: UIEvent?) { finishHold() }
    override func cancelTracking(with event: UIEvent?) { cancelHold() }

    private func finishHold() {
        workItem?.cancel()
        workItem = nil
        progressWidthConstraint.constant = 0
        UIView.animate(withDuration: 0.16, animations: {
            self.transform = .identity
            self.layer.borderColor = PraxisTheme.danger.withAlphaComponent(self.isActionBusy ? 0.56 : 0.32).cgColor
            self.backgroundColor = PraxisTheme.danger.withAlphaComponent(self.isActionBusy ? 0.10 : 0.035)
            self.layoutIfNeeded()
        })
    }

    private func cancelHold() {
        didConfirm = false
        finishHold()
    }
}
