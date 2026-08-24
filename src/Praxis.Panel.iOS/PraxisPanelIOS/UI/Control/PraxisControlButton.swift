import UIKit

enum PraxisControlButtonStyle {
    case application(placeholder: String)
    case system(glyph: PraxisGlyph, subtitle: String)
    case compact(glyph: PraxisGlyph)
}

final class PraxisControlButton: UIControl {
    let controlID: String
    private let style: PraxisControlButtonStyle
    private let accent: UIColor
    private let surface = UIView()
    private let feedback = UIView()
    private let titleLabel = UILabel()
    private let subtitleLabel = UILabel()
    private let statusTrack = UIView()
    private let statusFill = UIView()
    private var applicationImageView: UIImageView?
    private var applicationPlaceholder: UILabel?
    private var isApplicationActive = false
    private var isBusy = false
    private var isErrorState = false
    private var resultResetWorkItem: DispatchWorkItem?

    init(controlID: String, title: String, colorName: String, style: PraxisControlButtonStyle) {
        self.controlID = controlID
        self.style = style
        self.accent = PraxisTheme.accent(named: colorName)
        super.init(frame: .zero)
        isAccessibilityElement = true
        accessibilityLabel = title
        clipsToBounds = false
        titleLabel.text = title
        titleLabel.textColor = PraxisTheme.text
        titleLabel.textAlignment = .center
        titleLabel.lineBreakMode = .byTruncatingTail
        titleLabel.adjustsFontSizeToFitWidth = true
        titleLabel.minimumScaleFactor = 0.78

        switch style {
        case .application(let placeholder): configureApplication(placeholder: placeholder)
        case .system(let glyph, let subtitle): configureSystem(glyph: glyph, subtitle: subtitle)
        case .compact(let glyph): configureCompact(glyph: glyph)
        }

        addTarget(self, action: #selector(pressBegan), for: .touchDown)
        addTarget(self, action: #selector(pressEnded), for: [.touchUpInside, .touchUpOutside, .touchCancel, .touchDragExit])
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func hitTest(_ point: CGPoint, with event: UIEvent?) -> UIView? {
        guard isUserInteractionEnabled, !isHidden, alpha > 0.01,
              self.point(inside: point, with: event) else { return nil }
        // Every visible element inside a control is decorative. Returning the
        // control itself keeps taps on the app artwork and labels reliable.
        return self
    }

    override var isEnabled: Bool {
        didSet {
            alpha = isEnabled ? 1 : 0.38
            accessibilityTraits = isEnabled ? [.button] : [.button, .notEnabled]
        }
    }

    func setTitle(_ title: String) {
        titleLabel.text = title
        accessibilityLabel = title
    }

    func setApplicationIcon(_ image: UIImage?) {
        guard case .application = style else { return }
        applicationImageView?.image = image
        applicationImageView?.isHidden = image == nil
        applicationPlaceholder?.isHidden = image != nil
    }

    func setApplicationActive(_ active: Bool, animated: Bool) {
        guard case .application = style else { return }
        guard isApplicationActive != active else { return }
        isApplicationActive = active
        updateApplicationStatus(animated: animated)
    }

    func setActionState(busy: Bool, error: Bool) {
        resultResetWorkItem?.cancel()
        isBusy = busy
        isErrorState = error
        if case .application = style {
            updateApplicationStatus(animated: true)
        } else {
            layer.borderColor = actionBorderColor(busy: busy, error: error).cgColor
            backgroundColor = busy ? accent.withAlphaComponent(0.08) : PraxisTheme.surface
        }
        if error {
            let item = DispatchWorkItem { [weak self] in
                self?.setActionState(busy: false, error: false)
            }
            resultResetWorkItem = item
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.90, execute: item)
        }
    }

    private func configureApplication(placeholder: String) {
        backgroundColor = .clear
        surface.backgroundColor = .clear
        surface.layer.cornerRadius = 16
        surface.layer.borderWidth = 1
        surface.layer.borderColor = UIColor.clear.cgColor
        addSubview(surface)
        surface.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            surface.centerXAnchor.constraint(equalTo: centerXAnchor),
            surface.centerYAnchor.constraint(equalTo: centerYAnchor, constant: -5),
            surface.topAnchor.constraint(greaterThanOrEqualTo: topAnchor),
            surface.widthAnchor.constraint(equalTo: widthAnchor, constant: -8),
            surface.heightAnchor.constraint(equalTo: surface.widthAnchor),
            surface.heightAnchor.constraint(lessThanOrEqualToConstant: 132)
        ])

        feedback.backgroundColor = accent.withAlphaComponent(0.10)
        feedback.alpha = 0
        feedback.layer.cornerRadius = 16
        surface.addSubview(feedback)
        feedback.pinEdges(to: surface)

        let imageView = UIImageView()
        imageView.contentMode = .scaleAspectFit
        imageView.isHidden = true
        imageView.translatesAutoresizingMaskIntoConstraints = false
        surface.addSubview(imageView)
        applicationImageView = imageView

        let fallback = UILabel()
        fallback.text = placeholder
        fallback.textAlignment = .center
        fallback.font = UIFont.systemFont(ofSize: placeholder.count > 1 ? 17 : 22, weight: .bold)
        fallback.textColor = accent
        fallback.backgroundColor = accent.withAlphaComponent(0.10)
        fallback.layer.cornerRadius = 14
        fallback.layer.borderWidth = 1
        fallback.layer.borderColor = accent.withAlphaComponent(0.25).cgColor
        fallback.clipsToBounds = true
        fallback.translatesAutoresizingMaskIntoConstraints = false
        surface.addSubview(fallback)
        applicationPlaceholder = fallback
        NSLayoutConstraint.activate([
            imageView.centerXAnchor.constraint(equalTo: surface.centerXAnchor),
            imageView.centerYAnchor.constraint(equalTo: surface.centerYAnchor, constant: -10),
            imageView.widthAnchor.constraint(equalTo: surface.widthAnchor, multiplier: 0.49),
            imageView.heightAnchor.constraint(equalTo: imageView.widthAnchor),
            fallback.centerXAnchor.constraint(equalTo: imageView.centerXAnchor),
            fallback.centerYAnchor.constraint(equalTo: imageView.centerYAnchor),
            fallback.widthAnchor.constraint(equalToConstant: 58),
            fallback.heightAnchor.constraint(equalToConstant: 58)
        ])

        titleLabel.font = UIFont.systemFont(ofSize: 14, weight: .semibold)
        surface.addSubview(titleLabel)
        titleLabel.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            titleLabel.leadingAnchor.constraint(equalTo: surface.leadingAnchor, constant: 8),
            titleLabel.trailingAnchor.constraint(equalTo: surface.trailingAnchor, constant: -8),
            titleLabel.bottomAnchor.constraint(equalTo: surface.bottomAnchor, constant: -13)
        ])

        statusTrack.backgroundColor = .clear
        statusTrack.layer.cornerRadius = 1.5
        addSubview(statusTrack)
        statusTrack.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            statusTrack.topAnchor.constraint(equalTo: surface.bottomAnchor, constant: 8),
            statusTrack.centerXAnchor.constraint(equalTo: centerXAnchor),
            statusTrack.widthAnchor.constraint(equalToConstant: 52),
            statusTrack.heightAnchor.constraint(equalToConstant: 3),
            statusTrack.bottomAnchor.constraint(lessThanOrEqualTo: bottomAnchor)
        ])
        statusFill.backgroundColor = accent
        statusFill.layer.cornerRadius = 1.5
        statusFill.alpha = 0
        statusFill.transform = CGAffineTransform(scaleX: 0.001, y: 1)
        statusTrack.addSubview(statusFill)
        statusFill.pinEdges(to: statusTrack)
    }

    private func configureSystem(glyph: PraxisGlyph, subtitle: String) {
        PraxisTheme.configureCard(self, radius: 14)
        feedback.backgroundColor = accent.withAlphaComponent(0.10)
        feedback.alpha = 0
        feedback.layer.cornerRadius = 13
        addSubview(feedback)
        feedback.pinEdges(to: self)

        let iconContainer = UIView()
        iconContainer.backgroundColor = accent.withAlphaComponent(0.09)
        iconContainer.layer.cornerRadius = 11
        iconContainer.layer.borderWidth = 1
        iconContainer.layer.borderColor = accent.withAlphaComponent(0.22).cgColor
        let glyphView = PraxisGlyphView(glyph: glyph)
        glyphView.color = accent
        iconContainer.addSubview(glyphView)
        glyphView.pinEdges(to: iconContainer, insets: UIEdgeInsets(top: 8, left: 8, bottom: 8, right: 8))
        iconContainer.translatesAutoresizingMaskIntoConstraints = false
        addSubview(iconContainer)

        titleLabel.font = UIFont.systemFont(ofSize: 13, weight: .semibold)
        titleLabel.textAlignment = .left
        subtitleLabel.text = subtitle
        subtitleLabel.font = UIFont.systemFont(ofSize: 9, weight: .medium)
        subtitleLabel.textColor = PraxisTheme.muted
        subtitleLabel.lineBreakMode = .byTruncatingTail
        let copy = UIStackView(arrangedSubviews: [titleLabel, subtitleLabel])
        copy.axis = .vertical
        copy.spacing = 5
        copy.alignment = .fill
        addSubview(copy)
        copy.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            iconContainer.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 13),
            iconContainer.topAnchor.constraint(equalTo: topAnchor, constant: 13),
            iconContainer.widthAnchor.constraint(equalToConstant: 38),
            iconContainer.heightAnchor.constraint(equalToConstant: 38),
            copy.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 13),
            copy.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -10),
            copy.bottomAnchor.constraint(equalTo: bottomAnchor, constant: -13)
        ])
    }

    private func configureCompact(glyph: PraxisGlyph) {
        PraxisTheme.configureCard(self, radius: 11)
        feedback.backgroundColor = accent.withAlphaComponent(0.10)
        feedback.alpha = 0
        feedback.layer.cornerRadius = 10
        addSubview(feedback)
        feedback.pinEdges(to: self)

        let glyphView = PraxisGlyphView(glyph: glyph)
        glyphView.color = accent
        addSubview(glyphView)
        glyphView.translatesAutoresizingMaskIntoConstraints = false
        titleLabel.font = UIFont.systemFont(ofSize: 12, weight: .semibold)
        titleLabel.textAlignment = .left
        addSubview(titleLabel)
        titleLabel.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            heightAnchor.constraint(greaterThanOrEqualToConstant: 48),
            glyphView.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 9),
            glyphView.centerYAnchor.constraint(equalTo: centerYAnchor),
            glyphView.widthAnchor.constraint(equalToConstant: 24),
            glyphView.heightAnchor.constraint(equalToConstant: 24),
            titleLabel.leadingAnchor.constraint(equalTo: glyphView.trailingAnchor, constant: 7),
            titleLabel.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -7),
            titleLabel.centerYAnchor.constraint(equalTo: centerYAnchor)
        ])
    }

    private func updateApplicationStatus(animated: Bool) {
        let visible = isApplicationActive || isBusy || isErrorState
        statusFill.layer.removeAnimation(forKey: "praxis.breathe")
        let changes = {
            self.statusFill.alpha = visible ? 1 : 0
            self.statusFill.transform = visible ? .identity : CGAffineTransform(scaleX: 0.001, y: 1)
            self.statusFill.backgroundColor = self.isErrorState ? PraxisTheme.danger : self.accent
            if self.isErrorState {
                self.surface.layer.borderColor = PraxisTheme.danger.withAlphaComponent(0.42).cgColor
            } else if self.isBusy {
                self.surface.layer.borderColor = self.accent.withAlphaComponent(0.42).cgColor
            } else {
                // Running state is represented only by the status bar.
                self.surface.layer.borderColor = UIColor.clear.cgColor
            }
            self.feedback.alpha = self.isBusy || self.isErrorState ? 1 : 0
        }
        if animated {
            UIView.animate(withDuration: 0.44, delay: 0, options: [.beginFromCurrentState, .allowUserInteraction], animations: changes)
        } else {
            changes()
        }
        if visible && !isErrorState {
            let breathe = CABasicAnimation(keyPath: "opacity")
            breathe.fromValue = 1
            breathe.toValue = 0.52
            breathe.duration = 1.6
            breathe.autoreverses = true
            breathe.repeatCount = .infinity
            statusFill.layer.add(breathe, forKey: "praxis.breathe")
        }
    }

    private func actionBorderColor(busy: Bool? = nil, error: Bool? = nil) -> UIColor {
        let busy = busy ?? isBusy
        let error = error ?? isErrorState
        if error { return PraxisTheme.danger.withAlphaComponent(0.55) }
        if busy { return accent.withAlphaComponent(0.55) }
        return PraxisTheme.line
    }

    @objc private func pressBegan() {
        guard isEnabled else { return }
        let target = surface.superview == self && !surface.frame.isEmpty ? surface : self
        UIView.animate(withDuration: 0.16, delay: 0, options: [.beginFromCurrentState, .allowUserInteraction], animations: {
            target.transform = CGAffineTransform(scaleX: 0.965, y: 0.965)
            self.feedback.alpha = 1
            target.layer.borderColor = self.accent.withAlphaComponent(0.52).cgColor
        })
    }

    @objc private func pressEnded() {
        let target = surface.superview == self && !surface.frame.isEmpty ? surface : self
        UIView.animate(withDuration: 0.16, delay: 0, options: [.beginFromCurrentState, .allowUserInteraction], animations: {
            target.transform = .identity
            self.feedback.alpha = self.isBusy || self.isErrorState ? 1 : 0
            if target === self.surface {
                if self.isErrorState {
                    self.surface.layer.borderColor = PraxisTheme.danger.withAlphaComponent(0.42).cgColor
                } else if self.isBusy {
                    self.surface.layer.borderColor = self.accent.withAlphaComponent(0.42).cgColor
                } else {
                    self.surface.layer.borderColor = UIColor.clear.cgColor
                }
            } else {
                self.layer.borderColor = self.actionBorderColor().cgColor
            }
        })
    }
}
