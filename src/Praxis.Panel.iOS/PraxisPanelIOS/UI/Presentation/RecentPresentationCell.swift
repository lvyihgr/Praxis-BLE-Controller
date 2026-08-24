import UIKit

final class RecentPresentationCell: UITableViewCell {
    static let reuseIdentifier = "RecentPresentationCell"
    private let rankLabel = UILabel()
    private let nameLabel = UILabel()
    private let pathLabel = UILabel()
    private let arrow = PraxisGlyphView(glyph: .next)
    private let cardView = UIView()

    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: style, reuseIdentifier: reuseIdentifier)
        backgroundColor = .clear
        selectionStyle = .none
        contentView.backgroundColor = .clear
        cardView.backgroundColor = UIColor.white.withAlphaComponent(0.024)
        cardView.layer.cornerRadius = 10
        cardView.layer.borderWidth = 1
        cardView.layer.borderColor = PraxisTheme.line.cgColor
        cardView.isUserInteractionEnabled = false
        contentView.addSubview(cardView)
        cardView.pinEdges(to: contentView, insets: UIEdgeInsets(top: 2, left: 10, bottom: 2, right: 10))
        rankLabel.font = UIFont.monospacedDigitSystemFont(ofSize: 9, weight: .semibold)
        rankLabel.textColor = PraxisTheme.dim
        nameLabel.font = UIFont.systemFont(ofSize: 12, weight: .semibold)
        nameLabel.textColor = PraxisTheme.text.withAlphaComponent(0.90)
        nameLabel.lineBreakMode = .byTruncatingTail
        pathLabel.font = UIFont.systemFont(ofSize: 9, weight: .medium)
        pathLabel.textColor = PraxisTheme.muted
        pathLabel.lineBreakMode = .byTruncatingMiddle
        let copy = UIStackView(arrangedSubviews: [nameLabel, pathLabel])
        copy.axis = .vertical
        copy.spacing = 3
        arrow.color = PraxisTheme.muted
        cardView.addSubview(rankLabel)
        cardView.addSubview(copy)
        cardView.addSubview(arrow)
        rankLabel.translatesAutoresizingMaskIntoConstraints = false
        copy.translatesAutoresizingMaskIntoConstraints = false
        arrow.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            rankLabel.leadingAnchor.constraint(equalTo: cardView.leadingAnchor, constant: 11),
            rankLabel.centerYAnchor.constraint(equalTo: cardView.centerYAnchor),
            rankLabel.widthAnchor.constraint(equalToConstant: 24),
            copy.leadingAnchor.constraint(equalTo: rankLabel.trailingAnchor, constant: 4),
            copy.trailingAnchor.constraint(equalTo: arrow.leadingAnchor, constant: -8),
            copy.centerYAnchor.constraint(equalTo: cardView.centerYAnchor),
            arrow.trailingAnchor.constraint(equalTo: cardView.trailingAnchor, constant: -10),
            arrow.centerYAnchor.constraint(equalTo: cardView.centerYAnchor),
            arrow.widthAnchor.constraint(equalToConstant: 14),
            arrow.heightAnchor.constraint(equalToConstant: 14)
        ])
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(index: Int, item: PraxisRecentPresentation) {
        rankLabel.text = String(format: "%02d", index + 1)
        nameLabel.text = item.name
        pathLabel.text = item.path + (item.isAvailable ? "" : " · 文件当前不可用")
        alpha = item.isAvailable ? 1 : 0.45
    }

    func setPressed(_ pressed: Bool) {
        UIView.animate(withDuration: 0.16) {
            self.cardView.transform = pressed ? CGAffineTransform(scaleX: 0.985, y: 0.985) : .identity
            self.cardView.layer.borderColor = (pressed ? PraxisTheme.systemBlue.withAlphaComponent(0.46) : PraxisTheme.line).cgColor
            self.cardView.backgroundColor = pressed ? PraxisTheme.systemBlue.withAlphaComponent(0.10) : UIColor.white.withAlphaComponent(0.024)
        }
    }
}
