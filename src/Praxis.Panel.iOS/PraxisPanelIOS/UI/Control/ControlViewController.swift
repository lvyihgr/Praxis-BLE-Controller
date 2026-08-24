import UIKit

protocol ControlViewControllerDelegate: AnyObject {
    func controlViewController(_ controller: ControlViewController, invokeAction actionID: String, controlID: String)
    func controlViewController(_ controller: ControlViewController, showNotice title: String, message: String)
}

final class ControlViewController: UIViewController {
    weak var delegate: ControlViewControllerDelegate?
    private let configurationStore: PraxisConfigurationStore
    private let stateStore: PraxisStateStore
    private let iconStore: PraxisIconStore
    private var buttons: [String: PraxisControlButton] = [:]
    private var connected = false

    private struct Spec {
        let id: String
        let title: String
        let color: String
        let style: PraxisControlButtonStyle
    }

    private let applicationSpecs: [Spec] = [
        Spec(id: "chatgpt", title: "ChatGPT", color: "green", style: .application(placeholder: "C")),
        Spec(id: "zcode", title: "ZCode", color: "cyan", style: .application(placeholder: "Z")),
        Spec(id: "trae-solo", title: "TRAE SOLO", color: "violet", style: .application(placeholder: "TS")),
        Spec(id: "trae", title: "Trae", color: "blue", style: .application(placeholder: "T")),
        Spec(id: "edge", title: "Edge", color: "cyan", style: .application(placeholder: "E")),
        Spec(id: "feishu", title: "飞书", color: "blue", style: .application(placeholder: "飞")),
        Spec(id: "vscode", title: "VS Code", color: "blue", style: .application(placeholder: "VS"))
    ]

    private let systemSpecs: [Spec] = [
        Spec(id: "desktop-view", title: "显示桌面", color: "blue", style: .system(glyph: .desktop, subtitle: "Win + D")),
        Spec(id: "screenshot", title: "截图工具", color: "green", style: .system(glyph: .screenshot, subtitle: "系统应用")),
        Spec(id: "explorer", title: "文件管理", color: "amber", style: .system(glyph: .folder, subtitle: "资源管理器")),
        Spec(id: "notepad", title: "记事本", color: "violet", style: .system(glyph: .note, subtitle: "系统应用")),
        Spec(id: "terminal", title: "终端", color: "cyan", style: .system(glyph: .terminal, subtitle: "Windows Terminal")),
        Spec(id: "calculator", title: "计算器", color: "blue", style: .system(glyph: .calculator, subtitle: "系统应用")),
        Spec(id: "task-view", title: "任务视图", color: "slate", style: .system(glyph: .taskView, subtitle: "Win + Tab"))
    ]

    private let shortcutSpecs: [Spec] = [
        Spec(id: "voice-input", title: "语音输入", color: "blue", style: .compact(glyph: .microphone)),
        Spec(id: "copy", title: "复制", color: "slate", style: .compact(glyph: .copy)),
        Spec(id: "paste", title: "粘贴", color: "slate", style: .compact(glyph: .paste)),
        Spec(id: "play", title: "播放 / 暂停", color: "slate", style: .compact(glyph: .playPause)),
        Spec(id: "mute", title: "静音 / 取消静音", color: "slate", style: .compact(glyph: .mute)),
        Spec(id: "volume-down", title: "音量降低", color: "slate", style: .compact(glyph: .volumeDown)),
        Spec(id: "volume-up", title: "音量提高", color: "slate", style: .compact(glyph: .volumeUp))
    ]

    init(configurationStore: PraxisConfigurationStore, stateStore: PraxisStateStore, iconStore: PraxisIconStore = PraxisIconStore()) {
        self.configurationStore = configurationStore
        self.stateStore = stateStore
        self.iconStore = iconStore
        super.init(nibName: nil, bundle: nil)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = PraxisTheme.background
        buildLayout()
        refreshConfiguration()
        refreshState()
    }

    func setConnected(_ connected: Bool) {
        self.connected = connected
        refreshAvailability()
    }

    func refreshConfiguration() {
        for spec in applicationSpecs + systemSpecs + shortcutSpecs {
            buttons[spec.id]?.setTitle(configurationStore.displayName(for: spec.id, fallback: spec.title))
        }
        for spec in applicationSpecs { refreshIcon(forControlID: spec.id) }
        refreshAvailability()
        refreshState()
    }

    func refreshIcon(for actionID: String) {
        for spec in applicationSpecs where configurationStore.actionID(for: spec.id) == actionID {
            refreshIcon(forControlID: spec.id)
        }
    }

    private func refreshIcon(forControlID controlID: String) {
        guard let actionID = configurationStore.actionID(for: controlID) else { return }
        buttons[controlID]?.setApplicationIcon(iconStore.image(for: actionID))
    }

    func refreshState() {
        for spec in applicationSpecs {
            let binding = configurationStore.stateBinding(for: spec.id)
            buttons[spec.id]?.setApplicationActive(binding.map(stateStore.boolValue(for:)) ?? false, animated: true)
        }
    }

    func handleAction(type: String, event: PraxisActionEvent) {
        guard let controlID = event.controlID, let button = buttons[controlID] else { return }
        switch type {
        case "action.accepted", "action.progress": button.setActionState(busy: true, error: false)
        case "action.completed": button.setActionState(busy: false, error: false)
        case "action.failed": button.setActionState(busy: false, error: true)
        default: break
        }
    }

    private func buildLayout() {
        let applicationRow = makeRow(applicationSpecs)
        let systemRow = makeRow(systemSpecs)
        let shortcutRow = makeRow(shortcutSpecs)
        let stack = UIStackView(arrangedSubviews: [applicationRow, systemRow, shortcutRow])
        stack.axis = .vertical
        stack.alignment = .fill
        stack.spacing = 14
        view.addSubview(stack)
        stack.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 18),
            stack.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -18),
            stack.centerYAnchor.constraint(equalTo: view.centerYAnchor),
            stack.topAnchor.constraint(greaterThanOrEqualTo: view.topAnchor, constant: 12),
            stack.bottomAnchor.constraint(lessThanOrEqualTo: view.bottomAnchor, constant: -12),
            applicationRow.heightAnchor.constraint(equalToConstant: 236),
            systemRow.heightAnchor.constraint(equalToConstant: 190),
            shortcutRow.heightAnchor.constraint(equalToConstant: 48)
        ])
    }

    private func makeRow(_ specs: [Spec]) -> UIView {
        let row = UIStackView()
        row.axis = .horizontal
        row.alignment = .fill
        row.distribution = .fillEqually
        row.spacing = 10
        for spec in specs {
            let button = PraxisControlButton(controlID: spec.id, title: spec.title, colorName: spec.color, style: spec.style)
            button.addTarget(self, action: #selector(invoke(_:)), for: .touchUpInside)
            button.isEnabled = false
            buttons[spec.id] = button
            row.addArrangedSubview(button)
        }
        return row
    }

    private func refreshAvailability() {
        for (controlID, button) in buttons {
            button.isEnabled = connected && configurationStore.actionID(for: controlID) != nil
        }
    }

    @objc private func invoke(_ sender: PraxisControlButton) {
        guard let actionID = configurationStore.actionID(for: sender.controlID) else {
            delegate?.controlViewController(self, showNotice: "配置尚未就绪", message: "Praxis Bluetooth 尚未提供 \(sender.controlID) 的 Action ID。")
            return
        }
        delegate?.controlViewController(self, invokeAction: actionID, controlID: sender.controlID)
    }
}
