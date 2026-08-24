import UIKit

final class PanelRootViewController: UIViewController {
    private let session: PraxisProtocolSession
    private let header = PraxisHeaderView()
    private let tabBar = CompactTabBarView()
    private let content = UIView()
    private let toastStack = PraxisToastStackView()
    private let pairingOverlay = PairingOverlayView()
    private let controlController: ControlViewController
    private let presentationPageController: PresentationViewController
    private weak var systemSummaryController: SystemSummaryViewController?

    init(session: PraxisProtocolSession) {
        self.session = session
        controlController = ControlViewController(
            configurationStore: session.configurationStore,
            stateStore: session.stateStore,
            iconStore: session.iconStore
        )
        presentationPageController = PresentationViewController(
            configurationStore: session.configurationStore,
            stateStore: session.stateStore,
            iconStore: session.iconStore
        )
        super.init(nibName: nil, bundle: nil)
        session.delegate = self
        session.configurationStore.delegate = self
        session.stateStore.delegate = self
        controlController.delegate = self
        presentationPageController.delegate = self
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override var preferredStatusBarStyle: UIStatusBarStyle { .lightContent }

    override var prefersStatusBarHidden: Bool { false }

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = PraxisTheme.background
        setNeedsStatusBarAppearanceUpdate()
        buildLayout()
        bindActions()
        showController(at: 0)
        header.update(connection: session.connectionState)
        session.start()
        pairingOverlay.onSubmit = { [weak self] code in self?.session.submitPairingCode(code) }
    }

    private func buildLayout() {
        view.addSubview(header)
        view.addSubview(content)
        view.addSubview(tabBar)
        header.translatesAutoresizingMaskIntoConstraints = false
        content.translatesAutoresizingMaskIntoConstraints = false
        tabBar.translatesAutoresizingMaskIntoConstraints = false
        let guide = view.safeAreaLayoutGuide
        NSLayoutConstraint.activate([
            header.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            header.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            header.topAnchor.constraint(equalTo: guide.topAnchor),
            header.heightAnchor.constraint(equalToConstant: 78),
            content.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            content.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            content.topAnchor.constraint(equalTo: header.bottomAnchor),
            content.bottomAnchor.constraint(equalTo: tabBar.topAnchor),
            tabBar.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            tabBar.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            tabBar.bottomAnchor.constraint(equalTo: guide.bottomAnchor),
            tabBar.heightAnchor.constraint(equalToConstant: 52)
        ])

        addChild(controlController)
        content.addSubview(controlController.view)
        controlController.view.pinEdges(to: content)
        controlController.didMove(toParent: self)
        addChild(presentationPageController)
        content.addSubview(presentationPageController.view)
        presentationPageController.view.pinEdges(to: content)
        presentationPageController.didMove(toParent: self)

        view.addSubview(toastStack)
        toastStack.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            toastStack.topAnchor.constraint(equalTo: header.bottomAnchor, constant: 8),
            toastStack.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -24),
            toastStack.widthAnchor.constraint(equalToConstant: 340),
            toastStack.heightAnchor.constraint(equalToConstant: 180)
        ])
        view.addSubview(pairingOverlay)
        pairingOverlay.translatesAutoresizingMaskIntoConstraints = false
        pairingOverlay.pinEdges(to: view)
    }

    private func bindActions() {
        tabBar.onSelectionChanged = { [weak self] index in self?.showController(at: index) }
        header.connectionTestButton.addTarget(self, action: #selector(testConnection), for: .touchUpInside)
        header.systemSummaryButton.addTarget(self, action: #selector(showSystemSummary), for: .touchUpInside)
        header.refreshButton.addTarget(self, action: #selector(refreshConfiguration), for: .touchUpInside)
    }

    private func showController(at index: Int) {
        controlController.view.isHidden = index != 0
        presentationPageController.view.isHidden = index != 1
    }

    @objc private func testConnection() {
        if session.ping() != nil {
            toastStack.show(title: "连接测试", message: "正在等待 Praxis Bluetooth 响应", isError: false, duration: 1.8)
        }
    }

    @objc private func showSystemSummary() {
        guard session.configurationStore.hasAction("system.summary") else {
            toastStack.show(title: "配置尚未就绪", message: "缺少 system.summary 动作", isError: true)
            return
        }
        if systemSummaryController == nil {
            let controller = SystemSummaryViewController()
            systemSummaryController = controller
            present(controller, animated: true)
        }
        if session.invoke(actionID: "system.summary", controlID: "system-summary-header") == nil {
            systemSummaryController?.updateError("无法向 Praxis Bluetooth 请求系统摘要。")
        }
    }

    @objc private func refreshConfiguration() {
        session.refreshConfiguration()
        toastStack.show(title: "正在刷新", message: "已请求最新配置", isError: false, duration: 1.6)
    }
}

extension PanelRootViewController: PraxisProtocolSessionDelegate {
    func protocolSession(_ session: PraxisProtocolSession, didChange connection: BluetoothConnectionState) {
        header.update(connection: connection)
        header.update(coreInfo: session.coreInfo)
        controlController.setConnected(session.isReady)
        presentationPageController.setConnected(session.isReady)
        if !connection.isConnected { pairingOverlay.hide() }
    }

    func protocolSession(_ session: PraxisProtocolSession, didReceive coreInfo: PraxisCoreInfo) {
        header.update(coreInfo: coreInfo)
    }

    func protocolSession(_ session: PraxisProtocolSession, didReceiveAction type: String, event: PraxisActionEvent) {
        controlController.handleAction(type: type, event: event)
        let shouldShowPresentationNotice = presentationPageController.handleAction(type: type, event: event)
        let isPresentationNavigation = event.actionID == "presentation.previous" || event.actionID == "presentation.next"
        switch type {
        case "action.completed":
            if !isPresentationNavigation && shouldShowPresentationNotice {
                toastStack.show(title: "执行完成", message: event.message ?? event.actionID, isError: false)
            }
            if event.controlID == "connection-test-header" { header.connectionTestButton.showResult(error: false) }
            if event.controlID == "system-summary-header" { header.systemSummaryButton.showResult(error: false) }
        case "action.failed":
            if shouldShowPresentationNotice {
                toastStack.show(title: "执行失败", message: event.message ?? event.actionID, isError: true)
            }
            if event.controlID == "connection-test-header" { header.connectionTestButton.showResult(error: true) }
            if event.controlID == "system-summary-header" {
                header.systemSummaryButton.showResult(error: true)
                systemSummaryController?.updateError(event.message ?? "Praxis Bluetooth 未能生成系统摘要。")
            }
        default: break
        }
    }

    func protocolSession(_ session: PraxisProtocolSession, didReceiveNotice title: String, message: String, isError: Bool) {
        toastStack.show(title: title, message: message, isError: isError)
        if title == "配对失败" { pairingOverlay.showError(message) }
        if title.hasPrefix("连接测试") { header.connectionTestButton.showResult(error: isError) }
    }

    func protocolSession(_ session: PraxisProtocolSession, didReceiveSystemSummary output: String) {
        if let controller = systemSummaryController {
            controller.update(output: output)
        } else {
            let controller = SystemSummaryViewController(output: output)
            systemSummaryController = controller
            present(controller, animated: true)
        }
    }

    func protocolSession(_ session: PraxisProtocolSession, didReceiveIconFor actionID: String) {
        controlController.refreshIcon(for: actionID)
        presentationPageController.refreshIcon(for: actionID)
    }

    func protocolSession(_ session: PraxisProtocolSession, requiresPairingWith serverName: String) {
        pairingOverlay.show(serverName: serverName)
        controlController.setConnected(false)
        presentationPageController.setConnected(false)
    }

    func protocolSessionDidAuthenticate(_ session: PraxisProtocolSession) {
        pairingOverlay.hide()
        controlController.setConnected(false)
        presentationPageController.setConnected(false)
        header.update(connection: session.connectionState)
        toastStack.show(
            title: "正在同步",
            message: "正在核对控制配置和应用图标",
            isError: false,
            duration: 2.2
        )
    }

    func protocolSession(_ session: PraxisProtocolSession, didPlanBootstrapTotal total: Int, missing: Int) {
        let message = missing == 0
            ? "配置与 \(total) 个图标均已从本地缓存恢复"
            : "正在更新 \(missing) / \(total) 个应用图标"
        toastStack.show(title: "正在同步", message: message, isError: false, duration: 2.2)
    }

    func protocolSessionDidSynchronize(_ session: PraxisProtocolSession) {
        controlController.setConnected(true)
        presentationPageController.setConnected(true)
        controlController.refreshConfiguration()
        presentationPageController.refreshConfiguration()
        toastStack.show(title: "连接完成", message: "Praxis Bluetooth 已可以使用", isError: false, duration: 1.6)
    }
}

extension PanelRootViewController: PraxisConfigurationStoreDelegate {
    func configurationStoreDidUpdate(_ store: PraxisConfigurationStore) {
        controlController.refreshConfiguration()
        presentationPageController.refreshConfiguration()
        header.refreshButton.showResult(error: false)
    }
}

extension PanelRootViewController: PraxisStateStoreDelegate {
    func stateStore(_ store: PraxisStateStore, didChange keys: Set<String>) {
        if keys.contains(where: { $0.caseInsensitiveCompare("system.foregroundApp") == .orderedSame }) {
            header.update(activeApplication: store.foregroundApplication)
        }
        controlController.refreshState()
        if keys.contains(where: { $0.caseInsensitiveCompare("presentation.state") == .orderedSame }) {
            presentationPageController.refreshState()
        }
    }
}

extension PanelRootViewController: ControlViewControllerDelegate {
    func controlViewController(_ controller: ControlViewController, invokeAction actionID: String, controlID: String) {
        _ = session.invoke(actionID: actionID, controlID: controlID)
    }

    func controlViewController(_ controller: ControlViewController, showNotice title: String, message: String) {
        toastStack.show(title: title, message: message, isError: true)
    }
}

extension PanelRootViewController: PresentationViewControllerDelegate {
    @discardableResult
    func presentationViewController(
        _ controller: PresentationViewController,
        invokeAction actionID: String,
        controlID: String,
        itemID: String?
    ) -> String? {
        let isNavigation = actionID == "presentation.previous" || actionID == "presentation.next"
        return session.invoke(
            actionID: actionID,
            controlID: controlID,
            itemID: itemID,
            suppressFeedback: isNavigation
        )
    }

    func presentationViewController(_ controller: PresentationViewController, showNotice title: String, message: String) {
        toastStack.show(title: title, message: message, isError: true)
    }
}
