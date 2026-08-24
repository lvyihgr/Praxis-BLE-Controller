import UIKit

final class SystemSummaryViewController: UIViewController {
    private var output: String?
    private let content = UIScrollView()

    init(output: String? = nil) {
        self.output = output
        super.init(nibName: nil, bundle: nil)
        modalPresentationStyle = .overFullScreen
        modalTransitionStyle = .crossDissolve
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = UIColor.black.withAlphaComponent(0.72)
        let card = UIView()
        PraxisTheme.configureCard(card, radius: 20)
        card.backgroundColor = PraxisTheme.surface
        view.addSubview(card)
        card.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            card.centerXAnchor.constraint(equalTo: view.centerXAnchor),
            card.centerYAnchor.constraint(equalTo: view.centerYAnchor),
            card.widthAnchor.constraint(equalToConstant: 670),
            card.heightAnchor.constraint(equalToConstant: 420),
            card.heightAnchor.constraint(lessThanOrEqualTo: view.heightAnchor, constant: -54)
        ])

        let eyebrow = UILabel()
        eyebrow.text = "SYSTEM SUMMARY"
        eyebrow.font = UIFont.systemFont(ofSize: 9, weight: .bold)
        eyebrow.textColor = PraxisTheme.systemBlue
        let title = UILabel()
        title.text = "系统摘要"
        title.font = UIFont.systemFont(ofSize: 22, weight: .bold)
        title.textColor = PraxisTheme.text
        let headingCopy = UIStackView(arrangedSubviews: [eyebrow, title])
        headingCopy.axis = .vertical
        headingCopy.spacing = 7

        let close = HeaderActionButton(glyph: .close, accessibilityLabel: "关闭系统摘要")
        close.addTarget(self, action: #selector(closeView), for: .touchUpInside)
        let header = UIStackView(arrangedSubviews: [headingCopy, UIView(), close])
        header.axis = .horizontal
        header.alignment = .center
        card.addSubview(header)
        header.translatesAutoresizingMaskIntoConstraints = false

        content.alwaysBounceVertical = false
        card.addSubview(content)
        content.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            header.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 22),
            header.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -22),
            header.topAnchor.constraint(equalTo: card.topAnchor, constant: 18),
            header.heightAnchor.constraint(equalToConstant: 52),
            content.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 18),
            content.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -18),
            content.topAnchor.constraint(equalTo: header.bottomAnchor, constant: 10),
            content.bottomAnchor.constraint(equalTo: card.bottomAnchor, constant: -18)
        ])
        renderCurrentContent()
    }

    func update(output: String) {
        self.output = output
        if isViewLoaded { renderCurrentContent() }
    }

    func updateError(_ message: String) {
        output = message
        if isViewLoaded { renderCurrentContent() }
    }

    private func renderCurrentContent() {
        content.subviews.forEach { $0.removeFromSuperview() }
        guard let output = output else {
            renderLoading(in: content)
            return
        }
        render(output: output, in: content)
    }

    private func renderLoading(in scroll: UIScrollView) {
        let spinner = UIActivityIndicatorView(style: .white)
        spinner.color = PraxisTheme.systemBlue
        spinner.startAnimating()
        let label = UILabel()
        label.text = "正在读取 Praxis Bluetooth 系统状态…"
        label.font = UIFont.systemFont(ofSize: 13, weight: .medium)
        label.textColor = PraxisTheme.muted
        let stack = UIStackView(arrangedSubviews: [spinner, label])
        stack.axis = .horizontal
        stack.alignment = .center
        stack.spacing = 12
        scroll.addSubview(stack)
        stack.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            stack.centerXAnchor.constraint(equalTo: scroll.centerXAnchor),
            stack.centerYAnchor.constraint(equalTo: scroll.centerYAnchor),
            stack.leadingAnchor.constraint(greaterThanOrEqualTo: scroll.leadingAnchor, constant: 20),
            stack.trailingAnchor.constraint(lessThanOrEqualTo: scroll.trailingAnchor, constant: -20)
        ])
    }

    private func render(output: String, in scroll: UIScrollView) {
        guard let data = output.data(using: .utf8),
              let summary = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            let raw = UILabel()
            raw.numberOfLines = 0
            raw.text = output.isEmpty ? "没有返回内容。" : output
            raw.font = UIFont(name: "Menlo-Regular", size: 12) ?? UIFont.systemFont(ofSize: 12)
            raw.textColor = PraxisTheme.text.withAlphaComponent(0.82)
            scroll.addSubview(raw)
            raw.pinEdges(to: scroll)
            raw.widthAnchor.constraint(equalTo: scroll.widthAnchor).isActive = true
            return
        }

        let computer = String(describing: summary["Computer"] ?? "Windows PC")
        let detail = [summary["OS"], summary["Architecture"], summary["User"]]
            .compactMap { $0.map(String.init(describing:)) }
            .joined(separator: " · ")
        let hero = summaryCard(title: computer, value: detail, accent: true)
        let cpu = summaryCard(title: "CPU 当前负载", value: metric(summary["CpuUsagePercent"], suffix: "%"), accent: false)
        let gpu = summaryCard(title: "GPU / 显存", value: metric(summary["GpuUsagePercent"], suffix: "%"), accent: false)
        let memory = summaryCard(title: "内存", value: "\(summary["MemoryUsedGB"] ?? "—") GB / \(summary["MemoryTotalGB"] ?? "—") GB", accent: false)
        let disk = summaryCard(title: "系统磁盘剩余", value: "\(summary["DriveFreeGB"] ?? "—") GB", accent: false)
        let firstRow = UIStackView(arrangedSubviews: [cpu, gpu])
        firstRow.axis = .horizontal
        firstRow.distribution = .fillEqually
        firstRow.spacing = 10
        let secondRow = UIStackView(arrangedSubviews: [memory, disk])
        secondRow.axis = .horizontal
        secondRow.distribution = .fillEqually
        secondRow.spacing = 10
        let stack = UIStackView(arrangedSubviews: [hero, firstRow, secondRow])
        stack.axis = .vertical
        stack.spacing = 10
        scroll.addSubview(stack)
        stack.pinEdges(to: scroll)
        stack.widthAnchor.constraint(equalTo: scroll.widthAnchor).isActive = true
        hero.heightAnchor.constraint(equalToConstant: 82).isActive = true
        firstRow.heightAnchor.constraint(equalToConstant: 98).isActive = true
        secondRow.heightAnchor.constraint(equalToConstant: 98).isActive = true
    }

    private func summaryCard(title: String, value: String, accent: Bool) -> UIView {
        let card = UIView()
        PraxisTheme.configureCard(card, radius: 14)
        card.backgroundColor = accent ? PraxisTheme.systemBlue.withAlphaComponent(0.08) : UIColor.white.withAlphaComponent(0.024)
        if accent { card.layer.borderColor = PraxisTheme.systemBlue.withAlphaComponent(0.25).cgColor }
        let titleLabel = UILabel()
        titleLabel.text = title
        titleLabel.font = UIFont.systemFont(ofSize: accent ? 18 : 10, weight: .semibold)
        titleLabel.textColor = accent ? PraxisTheme.text : PraxisTheme.muted
        titleLabel.lineBreakMode = .byTruncatingTail
        let valueLabel = UILabel()
        valueLabel.text = value
        valueLabel.font = accent ? UIFont.systemFont(ofSize: 11, weight: .medium) : UIFont.monospacedDigitSystemFont(ofSize: 16, weight: .semibold)
        valueLabel.textColor = accent ? PraxisTheme.muted : PraxisTheme.text
        valueLabel.lineBreakMode = .byTruncatingTail
        let stack = UIStackView(arrangedSubviews: [titleLabel, valueLabel])
        stack.axis = .vertical
        stack.spacing = accent ? 8 : 10
        card.addSubview(stack)
        stack.pinEdges(to: card, insets: UIEdgeInsets(top: 14, left: 15, bottom: 14, right: 15))
        return card
    }

    private func metric(_ value: Any?, suffix: String) -> String {
        value.map { "\($0)\(suffix)" } ?? "不可用"
    }

    @objc private func closeView() { dismiss(animated: true) }
}
