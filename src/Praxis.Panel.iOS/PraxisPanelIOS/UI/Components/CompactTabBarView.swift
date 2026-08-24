import UIKit

final class CompactTabBarView: UIView {
    var onSelectionChanged: ((Int) -> Void)?
    private let buttons: [UIButton]
    private(set) var selectedIndex = 0

    override init(frame: CGRect) {
        let control = UIButton(type: .custom)
        control.setTitle("控制", for: .normal)
        control.tag = 0
        let presentation = UIButton(type: .custom)
        presentation.setTitle("演示", for: .normal)
        presentation.tag = 1
        buttons = [control, presentation]
        super.init(frame: frame)
        backgroundColor = UIColor(red: 0.035, green: 0.043, blue: 0.058, alpha: 0.98)
        layer.borderWidth = 0.5
        layer.borderColor = PraxisTheme.line.cgColor

        for button in buttons {
            button.titleLabel?.font = UIFont.systemFont(ofSize: 12, weight: .semibold)
            button.layer.cornerRadius = 9
            button.addTarget(self, action: #selector(selectButton(_:)), for: .touchUpInside)
            button.translatesAutoresizingMaskIntoConstraints = false
            NSLayoutConstraint.activate([
                button.widthAnchor.constraint(equalToConstant: 112),
                button.heightAnchor.constraint(greaterThanOrEqualToConstant: 44)
            ])
        }
        let stack = UIStackView(arrangedSubviews: buttons)
        stack.axis = .horizontal
        stack.alignment = .center
        stack.spacing = 6
        addSubview(stack)
        stack.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            stack.centerXAnchor.constraint(equalTo: centerXAnchor),
            stack.centerYAnchor.constraint(equalTo: centerYAnchor)
        ])
        updateButtons()
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    @objc private func selectButton(_ sender: UIButton) {
        guard selectedIndex != sender.tag else { return }
        selectedIndex = sender.tag
        updateButtons()
        onSelectionChanged?(selectedIndex)
    }

    private func updateButtons() {
        for button in buttons {
            let selected = button.tag == selectedIndex
            button.backgroundColor = selected ? UIColor.white.withAlphaComponent(0.065) : .clear
            button.setTitleColor(selected ? PraxisTheme.text : PraxisTheme.muted, for: .normal)
            button.layer.borderWidth = selected ? 1 : 0
            button.layer.borderColor = selected ? PraxisTheme.systemBlue.withAlphaComponent(0.42).cgColor : UIColor.clear.cgColor
        }
    }
}
