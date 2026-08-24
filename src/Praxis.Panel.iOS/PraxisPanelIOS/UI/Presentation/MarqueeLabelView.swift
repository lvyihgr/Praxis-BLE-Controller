import UIKit

final class MarqueeLabelView: UIView {
    private let primary = UILabel()
    private let duplicate = UILabel()
    private let content = UIView()
    private let gap: CGFloat = 48

    var text: String = "" {
        didSet {
            guard oldValue != text else { return }
            primary.text = text
            duplicate.text = text
            setNeedsLayout()
        }
    }

    override init(frame: CGRect) {
        super.init(frame: frame)
        clipsToBounds = true
        for label in [primary, duplicate] {
            label.font = UIFont.systemFont(ofSize: 12, weight: .semibold)
            label.textColor = PraxisTheme.text.withAlphaComponent(0.90)
            label.numberOfLines = 1
            content.addSubview(label)
        }
        addSubview(content)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func layoutSubviews() {
        super.layoutSubviews()
        content.layer.removeAnimation(forKey: "praxis.marquee")
        content.transform = .identity
        let measured = ceil(primary.sizeThatFits(CGSize(width: .greatestFiniteMagnitude, height: bounds.height)).width)
        let width = max(1, measured)
        primary.frame = CGRect(x: 0, y: 0, width: width, height: bounds.height)
        duplicate.frame = CGRect(x: width + gap, y: 0, width: width, height: bounds.height)
        let distance = width + gap
        content.frame = CGRect(x: 0, y: 0, width: distance * 2, height: bounds.height)
        duplicate.isHidden = width <= bounds.width
        guard width > bounds.width else { return }

        let animation = CABasicAnimation(keyPath: "transform.translation.x")
        animation.fromValue = 0
        animation.toValue = -distance
        animation.duration = CFTimeInterval(max(9, min(30, 6 + distance / 24)))
        animation.timingFunction = CAMediaTimingFunction(name: .linear)
        animation.repeatCount = .infinity
        animation.isRemovedOnCompletion = false
        content.layer.add(animation, forKey: "praxis.marquee")
    }
}
