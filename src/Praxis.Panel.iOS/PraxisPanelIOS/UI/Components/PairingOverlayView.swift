import UIKit

final class PairingCodeTextField: UITextField {
    override func caretRect(for position: UITextPosition) -> CGRect { .zero }
    override func selectionRects(for range: UITextRange) -> [UITextSelectionRect] { [] }
}

final class PairingOverlayView: UIView, UITextFieldDelegate {
    var onSubmit: ((String) -> Void)?
    private let serverLabel = UILabel()
    private let detailLabel = UILabel()
    private let input = PairingCodeTextField()
    private let codeRow = UIStackView()
    private let submitButton = UIButton(type: .system)
    private var slots: [UILabel] = []
    private let submitTitle = "建立安全连接"
    private var isSubmitting = false

    override init(frame: CGRect) {
        super.init(frame: frame)
        backgroundColor = PraxisTheme.background
        isHidden = true
        buildLayout()
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func show(serverName: String) {
        serverLabel.text = serverName
        detailLabel.text = "请在 Windows 通知区打开 Praxis Bluetooth，输入其中显示的六位配对码。"
        detailLabel.textColor = PraxisTheme.muted
        input.text = ""
        input.isEnabled = true
        isSubmitting = false
        submitButton.setTitle(submitTitle, for: .normal)
        updateSlots()
        isHidden = false
        superview?.bringSubviewToFront(self)
        DispatchQueue.main.async { [weak self] in self?.input.becomeFirstResponder() }
    }

    func hide() {
        input.resignFirstResponder()
        isSubmitting = false
        submitButton.setTitle(submitTitle, for: .normal)
        isHidden = true
    }

    func showError(_ message: String) {
        detailLabel.text = message
        detailLabel.textColor = PraxisTheme.danger
        input.text = ""
        input.isEnabled = true
        isSubmitting = false
        submitButton.setTitle(submitTitle, for: .normal)
        updateSlots()
        input.becomeFirstResponder()
    }

    private func showConnecting() {
        input.resignFirstResponder()
        input.isEnabled = false
        isSubmitting = true
        detailLabel.text = "正在验证配对码并建立加密会话，请稍候。"
        detailLabel.textColor = PraxisTheme.systemBlue
        submitButton.setTitle("正在建立连接…", for: .normal)
        updateSlots()
    }

    private func buildLayout() {
        let icon = UIImageView(image: UIImage(named: "PraxisIcon"))
        icon.contentMode = .scaleAspectFit
        icon.translatesAutoresizingMaskIntoConstraints = false
        let eyebrow = UILabel()
        eyebrow.text = "SECURE LOCAL PAIRING"
        eyebrow.font = UIFont(name: "Menlo-Bold", size: 11)
            ?? UIFont.systemFont(ofSize: 11, weight: .semibold)
        eyebrow.textColor = PraxisTheme.systemBlue
        eyebrow.textAlignment = .center
        let title = UILabel()
        title.text = "连接 Praxis Bluetooth"
        title.font = UIFont.systemFont(ofSize: 25, weight: .semibold)
        title.textColor = PraxisTheme.text
        title.textAlignment = .center
        serverLabel.font = UIFont.systemFont(ofSize: 14, weight: .medium)
        serverLabel.textColor = PraxisTheme.muted
        serverLabel.textAlignment = .center
        detailLabel.font = UIFont.systemFont(ofSize: 12)
        detailLabel.textColor = PraxisTheme.muted
        detailLabel.textAlignment = .center
        detailLabel.numberOfLines = 2

        codeRow.axis = .horizontal
        codeRow.alignment = .center
        codeRow.spacing = 8
        for index in 0..<6 {
            if index == 3 {
                let gap = UIView(); gap.translatesAutoresizingMaskIntoConstraints = false
                gap.widthAnchor.constraint(equalToConstant: 10).isActive = true
                codeRow.addArrangedSubview(gap)
            }
            let label = UILabel()
            label.textAlignment = .center
            label.font = UIFont.monospacedDigitSystemFont(ofSize: 24, weight: .semibold)
            label.textColor = PraxisTheme.text
            label.backgroundColor = UIColor.white.withAlphaComponent(0.035)
            label.layer.cornerRadius = 9
            label.layer.borderWidth = 1
            label.layer.borderColor = PraxisTheme.line.cgColor
            label.clipsToBounds = true
            label.translatesAutoresizingMaskIntoConstraints = false
            label.widthAnchor.constraint(equalToConstant: 42).isActive = true
            label.heightAnchor.constraint(equalToConstant: 50).isActive = true
            slots.append(label); codeRow.addArrangedSubview(label)
        }

        input.keyboardType = .numberPad
        input.textContentType = .oneTimeCode
        input.tintColor = .clear
        input.textColor = .clear
        input.backgroundColor = .clear
        input.delegate = self
        input.addTarget(self, action: #selector(codeChanged), for: .editingChanged)
        input.translatesAutoresizingMaskIntoConstraints = false

        submitButton.setTitle(submitTitle, for: .normal)
        submitButton.titleLabel?.font = UIFont.systemFont(ofSize: 15, weight: .semibold)
        submitButton.setTitleColor(.white, for: .normal)
        submitButton.backgroundColor = PraxisTheme.systemBlue
        submitButton.layer.cornerRadius = 11
        submitButton.isEnabled = false
        submitButton.alpha = 0.45
        submitButton.addTarget(self, action: #selector(submit), for: .touchUpInside)
        submitButton.translatesAutoresizingMaskIntoConstraints = false

        let card = UIView()
        PraxisTheme.configureCard(card, radius: 20)
        card.backgroundColor = UIColor(red: 0.035, green: 0.055, blue: 0.085, alpha: 1)
        addSubview(card)
        card.translatesAutoresizingMaskIntoConstraints = false
        for item in [icon, eyebrow, title, serverLabel, detailLabel, codeRow, input, submitButton] {
            card.addSubview(item); item.translatesAutoresizingMaskIntoConstraints = false
        }
        NSLayoutConstraint.activate([
            card.centerXAnchor.constraint(equalTo: centerXAnchor), card.centerYAnchor.constraint(equalTo: centerYAnchor),
            card.widthAnchor.constraint(equalToConstant: 460), card.heightAnchor.constraint(equalToConstant: 430),
            icon.topAnchor.constraint(equalTo: card.topAnchor, constant: 34), icon.centerXAnchor.constraint(equalTo: card.centerXAnchor),
            icon.widthAnchor.constraint(equalToConstant: 58), icon.heightAnchor.constraint(equalToConstant: 58),
            eyebrow.topAnchor.constraint(equalTo: icon.bottomAnchor, constant: 18), eyebrow.centerXAnchor.constraint(equalTo: card.centerXAnchor),
            title.topAnchor.constraint(equalTo: eyebrow.bottomAnchor, constant: 10), title.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 24), title.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -24),
            serverLabel.topAnchor.constraint(equalTo: title.bottomAnchor, constant: 8), serverLabel.leadingAnchor.constraint(equalTo: title.leadingAnchor), serverLabel.trailingAnchor.constraint(equalTo: title.trailingAnchor),
            detailLabel.topAnchor.constraint(equalTo: serverLabel.bottomAnchor, constant: 14), detailLabel.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 38), detailLabel.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -38),
            codeRow.topAnchor.constraint(equalTo: detailLabel.bottomAnchor, constant: 22), codeRow.centerXAnchor.constraint(equalTo: card.centerXAnchor),
            input.centerXAnchor.constraint(equalTo: codeRow.centerXAnchor), input.centerYAnchor.constraint(equalTo: codeRow.centerYAnchor), input.widthAnchor.constraint(equalTo: codeRow.widthAnchor), input.heightAnchor.constraint(equalTo: codeRow.heightAnchor),
            submitButton.topAnchor.constraint(equalTo: codeRow.bottomAnchor, constant: 24), submitButton.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 68), submitButton.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -68), submitButton.heightAnchor.constraint(equalToConstant: 48)
        ])
        updateSlots()
    }

    @objc private func codeChanged() {
        let digits = (input.text ?? "").filter { $0.isNumber }
        input.text = String(digits.prefix(6))
        detailLabel.textColor = PraxisTheme.muted
        isSubmitting = false
        submitButton.setTitle(submitTitle, for: .normal)
        updateSlots()
    }

    private func updateSlots() {
        let characters = Array(input.text ?? "")
        for (index, label) in slots.enumerated() {
            label.text = index < characters.count ? String(characters[index]) : "—"
            label.layer.borderColor = (input.isFirstResponder ? PraxisTheme.systemBlue.withAlphaComponent(0.50) : PraxisTheme.line).cgColor
        }
        let ready = characters.count == 6
        submitButton.isEnabled = ready && !isSubmitting
        submitButton.alpha = isSubmitting ? 0.72 : (ready ? 1 : 0.45)
    }

    @objc private func submit() {
        guard let code = input.text, code.count == 6 else { return }
        showConnecting()
        onSubmit?(code)
    }

    func textField(_ textField: UITextField, shouldChangeCharactersIn range: NSRange, replacementString string: String) -> Bool {
        let current = textField.text ?? ""
        guard let swiftRange = Range(range, in: current) else { return false }
        let next = current.replacingCharacters(in: swiftRange, with: string)
        return next.filter { $0.isNumber }.count <= 6
    }
}
