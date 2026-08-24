import UIKit

final class PraxisHeaderView: UIView {
    let connectionTestButton = HeaderActionButton(glyph: .link, accessibilityLabel: "连接测试")
    let systemSummaryButton = HeaderActionButton(glyph: .summary, accessibilityLabel: "系统摘要")
    let refreshButton = HeaderActionButton(glyph: .refresh, accessibilityLabel: "刷新配置")

    private let activeApplicationLabel = UILabel()
    private let connectionTitleLabel = UILabel()
    private let coreNameLabel = UILabel()
    private let connectionIcon = PraxisGlyphView(glyph: .connectionStatus)

    override init(frame: CGRect) {
        super.init(frame: frame)
        backgroundColor = UIColor(red: 0.035, green: 0.043, blue: 0.058, alpha: 0.98)
        layer.borderColor = PraxisTheme.line.cgColor
        layer.borderWidth = 0.5

        let icon = UIImageView(image: UIImage(named: "PraxisIcon"))
        icon.contentMode = .scaleAspectFit
        icon.layer.cornerRadius = 11
        icon.clipsToBounds = true
        icon.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            icon.widthAnchor.constraint(equalToConstant: 42),
            icon.heightAnchor.constraint(equalToConstant: 42)
        ])

        let wordmark = PraxisWordmarkView()
        wordmark.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            wordmark.widthAnchor.constraint(equalToConstant: 105),
            wordmark.heightAnchor.constraint(equalToConstant: 29)
        ])
        let brand = UIStackView(arrangedSubviews: [icon, wordmark])
        brand.axis = .horizontal
        brand.alignment = .center
        brand.spacing = 12

        let contextCaption = UILabel()
        contextCaption.text = "当前活动应用"
        contextCaption.font = UIFont.systemFont(ofSize: 9, weight: .semibold)
        contextCaption.textColor = PraxisTheme.dim
        activeApplicationLabel.text = "—"
        activeApplicationLabel.font = UIFont.systemFont(ofSize: 13, weight: .semibold)
        activeApplicationLabel.textColor = PraxisTheme.text.withAlphaComponent(0.88)
        activeApplicationLabel.lineBreakMode = .byTruncatingTail
        let contextCopy = UIStackView(arrangedSubviews: [contextCaption, activeApplicationLabel])
        contextCopy.axis = .vertical
        contextCopy.spacing = 4

        let contextCard = UIView()
        PraxisTheme.configureCard(contextCard, radius: 10)
        contextCard.backgroundColor = UIColor.white.withAlphaComponent(0.025)
        contextCard.addSubview(contextCopy)
        contextCopy.pinEdges(to: contextCard, insets: UIEdgeInsets(top: 8, left: 12, bottom: 8, right: 12))
        contextCard.translatesAutoresizingMaskIntoConstraints = false
        contextCard.widthAnchor.constraint(greaterThanOrEqualToConstant: 185).isActive = true

        connectionIcon.color = PraxisTheme.dim
        connectionIcon.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            connectionIcon.widthAnchor.constraint(equalToConstant: 19),
            connectionIcon.heightAnchor.constraint(equalToConstant: 19)
        ])
        connectionTitleLabel.text = "正在启动"
        connectionTitleLabel.font = UIFont.systemFont(ofSize: 12, weight: .semibold)
        connectionTitleLabel.textColor = PraxisTheme.text.withAlphaComponent(0.88)
        coreNameLabel.text = "Praxis Bluetooth"
        coreNameLabel.font = UIFont.systemFont(ofSize: 9, weight: .medium)
        coreNameLabel.textColor = PraxisTheme.muted
        let connectionCopy = UIStackView(arrangedSubviews: [connectionTitleLabel, coreNameLabel])
        connectionCopy.axis = .vertical
        connectionCopy.spacing = 4
        let connection = UIStackView(arrangedSubviews: [connectionIcon, connectionCopy])
        connection.axis = .horizontal
        connection.alignment = .center
        connection.spacing = 10

        let actions = UIStackView(arrangedSubviews: [connectionTestButton, systemSummaryButton, refreshButton])
        actions.axis = .horizontal
        actions.alignment = .center
        actions.spacing = 8

        let root = UIStackView(arrangedSubviews: [brand, contextCard, UIView(), connection, actions])
        root.axis = .horizontal
        root.alignment = .center
        root.spacing = 18
        addSubview(root)
        root.pinEdges(to: self, insets: UIEdgeInsets(top: 10, left: 18, bottom: 10, right: 18))

        setActionsEnabled(false)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func update(connection state: BluetoothConnectionState) {
        connectionTitleLabel.text = state.title
        switch state {
        case .connected, .backpressured:
            connectionIcon.color = PraxisTheme.systemBlue
            connectionIcon.layer.shadowColor = PraxisTheme.systemBlue.cgColor
            connectionIcon.layer.shadowRadius = 6
            connectionIcon.layer.shadowOpacity = 0.56
        case .error, .bluetoothUnavailable:
            connectionIcon.color = PraxisTheme.danger
            connectionIcon.layer.shadowOpacity = 0
        default:
            connectionIcon.color = PraxisTheme.dim
            connectionIcon.layer.shadowOpacity = 0
        }
        setActionsEnabled(state.isConnected)
    }

    func update(coreInfo: PraxisCoreInfo?) {
        coreNameLabel.text = coreInfo.map { "\($0.serverName) · \($0.transport.uppercased())" } ?? "Praxis Bluetooth"
    }

    func update(activeApplication: String) {
        activeApplicationLabel.text = activeApplication.isEmpty ? "—" : activeApplication
    }

    func setActionsEnabled(_ enabled: Bool) {
        connectionTestButton.isEnabled = enabled
        systemSummaryButton.isEnabled = enabled
        refreshButton.isEnabled = enabled
    }
}
