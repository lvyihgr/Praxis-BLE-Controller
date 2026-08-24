import UIKit

final class PraxisToastStackView: UIView {
    private let stack = UIStackView()

    override init(frame: CGRect) {
        super.init(frame: frame)
        isUserInteractionEnabled = false
        backgroundColor = .clear
        stack.axis = .vertical
        stack.alignment = .fill
        stack.spacing = 6
        addSubview(stack)
        stack.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            stack.topAnchor.constraint(equalTo: topAnchor),
            stack.leadingAnchor.constraint(equalTo: leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: trailingAnchor),
            stack.bottomAnchor.constraint(lessThanOrEqualTo: bottomAnchor)
        ])
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func show(title: String, message: String, isError: Bool, duration: TimeInterval = 3.2) {
        let toast = UIView()
        toast.backgroundColor = PraxisTheme.surfaceRaised.withAlphaComponent(0.98)
        toast.layer.cornerRadius = 9
        toast.layer.borderWidth = 1
        toast.layer.borderColor = (isError ? PraxisTheme.danger : PraxisTheme.systemBlue).withAlphaComponent(0.48).cgColor

        let titleLabel = UILabel()
        titleLabel.text = title
        titleLabel.font = UIFont.systemFont(ofSize: 11, weight: .bold)
        titleLabel.textColor = PraxisTheme.text
        titleLabel.setContentCompressionResistancePriority(.required, for: .horizontal)
        let messageLabel = UILabel()
        messageLabel.text = message
        messageLabel.font = UIFont.systemFont(ofSize: 10, weight: .medium)
        messageLabel.textColor = PraxisTheme.text.withAlphaComponent(0.72)
        messageLabel.lineBreakMode = .byTruncatingTail
        let row = UIStackView(arrangedSubviews: [titleLabel, messageLabel])
        row.axis = .horizontal
        row.alignment = .center
        row.spacing = 8
        toast.addSubview(row)
        row.pinEdges(to: toast, insets: UIEdgeInsets(top: 0, left: 11, bottom: 0, right: 11))
        toast.heightAnchor.constraint(equalToConstant: 34).isActive = true
        toast.setContentHuggingPriority(.required, for: .vertical)
        toast.setContentCompressionResistancePriority(.required, for: .vertical)

        toast.alpha = 0
        toast.transform = CGAffineTransform(translationX: 0, y: -6)
        stack.insertArrangedSubview(toast, at: 0)
        UIView.animate(withDuration: 0.16) {
            toast.alpha = 1
            toast.transform = .identity
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + duration) { [weak self, weak toast] in
            guard let self = self, let toast = toast else { return }
            UIView.animate(withDuration: 0.18, animations: {
                toast.alpha = 0
                toast.transform = CGAffineTransform(translationX: 0, y: -4)
            }, completion: { _ in
                self.stack.removeArrangedSubview(toast)
                toast.removeFromSuperview()
            })
        }
    }
}
