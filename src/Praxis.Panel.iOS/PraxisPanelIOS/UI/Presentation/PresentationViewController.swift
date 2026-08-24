import UIKit

protocol PresentationViewControllerDelegate: AnyObject {
    @discardableResult
    func presentationViewController(
        _ controller: PresentationViewController,
        invokeAction actionID: String,
        controlID: String,
        itemID: String?
    ) -> String?
    func presentationViewController(_ controller: PresentationViewController, showNotice title: String, message: String)
}

final class PresentationViewController: UIViewController {
    private enum LifecyclePhase: Equatable {
        case awaitingResult
        case awaitingState
        case recoveringFromFailure
    }

    private struct LifecycleTransition {
        let commandID: String
        let controlID: String
        let targetIsSlideShow: Bool
        let baselineRevision: Int
        var phase: LifecyclePhase
        var stateConfirmed: Bool
        var failureMessage: String?
    }

    weak var delegate: PresentationViewControllerDelegate?
    private let configurationStore: PraxisConfigurationStore
    private let stateStore: PraxisStateStore
    private let iconStore: PraxisIconStore
    private var connected = false
    private var presentation = PraxisPresentationState.unavailable

    private let workspaceTitle = UILabel()
    private let workspaceMeta = UILabel()
    private let workspaceIcon = PraxisGlyphView(glyph: .notes)
    private let notesView = UITextView()
    private let recentTable = UITableView(frame: .zero, style: .plain)
    private let emptyLabel = UILabel()
    private let statusDot = UIView()
    private let statusLabel = UILabel()
    private let titleMarquee = MarqueeLabelView()
    private let elapsedLabel = UILabel()
    private let slideLabel = UILabel()
    private let powerPointButton = UIControl()
    private let powerPointImageView = UIImageView()
    private let powerPointPlaceholder = PraxisGlyphView(glyph: .presentation)
    private let startBeginning = PresentationActionButton(title: "从头放映", glyph: .playBeginning)
    private let startCurrent = PresentationActionButton(title: "从当前页放映", glyph: .playCurrent)
    private let previousButton = PresentationActionButton(title: "上一页", glyph: .previous, vertical: true)
    private let nextButton = PresentationActionButton(title: "下一页", glyph: .next, vertical: true)
    private let endButton = HoldToConfirmButton()
    private var actionControls: [String: UIControl] = [:]
    private var powerPointBusy = false
    private var powerPointError = false
    private var powerPointResetWorkItem: DispatchWorkItem?
    private var lifecycleTransition: LifecycleTransition?
    private var lifecycleTimeoutWorkItem: DispatchWorkItem?
    private var retiredLifecycleCommandIDs: Set<String> = []
    private var lastStablePresentation = PraxisPresentationState.unavailable
    private var lastNavigationInvocation = Date.distantPast

    private static let minimumLifecycleResponseTimeout: TimeInterval = 20
    private static let lifecycleTransportGrace: TimeInterval = 10
    private static let navigationMinimumInterval: TimeInterval = 0.045

    private let actionByControlID: [String: String] = [
        "presentation-open": "presentation.open",
        "presentation-start-beginning": "presentation.startBeginning",
        "presentation-start-current": "presentation.startCurrent",
        "presentation-previous": "presentation.previous",
        "presentation-next": "presentation.next",
        "presentation-end": "presentation.end"
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
        refreshState()
    }

    func setConnected(_ connected: Bool) {
        self.connected = connected
        if !connected {
            resetLifecycleControlFeedback()
            finishLifecycleTransition(retireCommand: true)
        }
        render()
    }

    func refreshConfiguration() {
        render()
    }

    func refreshState() {
        presentation = stateStore.presentation
        if presentation.hasPresentation {
            lastStablePresentation = presentation
        }
        reconcileLifecycleTransition()
        render()
    }

    @discardableResult
    func handleAction(type: String, event: PraxisActionEvent) -> Bool {
        guard let controlID = event.controlID, let control = actionControls[controlID] else { return true }
        let isNavigation = controlID == "presentation-previous" || controlID == "presentation-next"
        let isLifecycle = controlID == "presentation-start-beginning" ||
            controlID == "presentation-start-current" || controlID == "presentation-end"
        if isLifecycle && retiredLifecycleCommandIDs.contains(event.commandID) {
            // A correlated state can arrive before action.completed. The state
            // has already updated the controls; allow only the late success
            // notification through without reopening the transition.
            return type == "action.completed"
        }
        if isLifecycle && shouldIgnoreLifecycleEvent(commandID: event.commandID) {
            return false
        }
        let busy = type == "action.accepted" || type == "action.progress"
        // A lifecycle failure can be a false negative while PowerPoint is
        // still completing the requested transition. Keep the controls locked
        // and wait for the correlated state before surfacing a final error.
        let error = type == "action.failed" && !isLifecycle
        if let button = control as? PresentationActionButton {
            // Paging is a real-time key pulse. Keeping a shared busy state for
            // overlapping taps makes a completed older command visually fight
            // with a newer one, so only surface actual failures here.
            if !isNavigation || error {
                button.setBusy(isNavigation ? false : busy, error: error)
            }
        } else if let button = control as? HoldToConfirmButton {
            button.setBusy(busy, error: error)
        } else if control === powerPointButton {
            setPowerPointBusy(busy, error: error)
        } else {
            if error {
                control.layer.borderColor = PraxisTheme.danger.withAlphaComponent(0.50).cgColor
            } else if busy {
                control.layer.borderColor = PraxisTheme.systemBlue.withAlphaComponent(0.50).cgColor
            } else {
                control.layer.borderColor = PraxisTheme.line.cgColor
            }
        }
        updateLifecycleTransition(type: type, event: event, controlID: controlID)
        return !(isLifecycle && type == "action.failed")
    }

    private func updateLifecycleTransition(type: String, event: PraxisActionEvent, controlID: String) {
        let requestedState: Bool
        switch controlID {
        case "presentation-start-beginning", "presentation-start-current": requestedState = true
        case "presentation-end": requestedState = false
        default: return
        }

        switch type {
        case "action.accepted":
            if lifecycleTransition == nil {
                beginLifecycleTransition(
                    commandID: event.commandID,
                    controlID: controlID,
                    requestedState: requestedState
                )
            }
        case "action.progress":
            if lifecycleTransition == nil {
                beginLifecycleTransition(
                    commandID: event.commandID,
                    controlID: controlID,
                    requestedState: requestedState
                )
            }
        case "action.completed":
            if lifecycleTransition == nil {
                beginLifecycleTransition(
                    commandID: event.commandID,
                    controlID: controlID,
                    requestedState: requestedState
                )
            }
            guard var transition = lifecycleTransition,
                  transition.commandID == event.commandID else { return }
            if transition.stateConfirmed {
                finishLifecycleTransition(retireCommand: true)
                render()
                return
            }
            transition.phase = .awaitingState
            lifecycleTransition = transition
            reconcileLifecycleTransition()
        case "action.failed":
            guard var transition = lifecycleTransition,
                  transition.commandID == event.commandID else { return }
            if transition.stateConfirmed {
                finishLifecycleTransition(retireCommand: true)
                render()
                return
            }
            transition.phase = .recoveringFromFailure
            transition.failureMessage = event.message
            lifecycleTransition = transition
        default:
            return
        }
        render()
    }

    private func beginLifecycleTransition(commandID: String, controlID: String, requestedState: Bool) {
        if let transition = lifecycleTransition {
            guard transition.commandID == commandID else { return }
            return
        }
        lifecycleTimeoutWorkItem?.cancel()
        lifecycleTransition = LifecycleTransition(
            commandID: commandID,
            controlID: controlID,
            targetIsSlideShow: requestedState,
            baselineRevision: presentation.revision ?? 0,
            phase: .awaitingResult,
            stateConfirmed: false,
            failureMessage: nil
        )
        render()

        let timeout = DispatchWorkItem { [weak self] in
            guard let self = self,
                  self.lifecycleTransition?.commandID == commandID else { return }
            let transition = self.lifecycleTransition
            let waitingForState = transition?.phase == .awaitingState
            let failureMessage = transition?.failureMessage
            self.resetLifecycleControlFeedback()
            self.finishLifecycleTransition(retireCommand: true)
            self.render()
            self.delegate?.presentationViewController(
                self,
                showNotice: failureMessage == nil
                    ? (waitingForState ? "状态同步超时" : "操作响应超时")
                    : "执行失败",
                message: failureMessage ?? (waitingForState
                    ? "PowerPoint 已执行操作，但最新演示状态尚未返回。"
                    : "Praxis Bluetooth 尚未返回这次演示操作的结果。")
            )
        }
        lifecycleTimeoutWorkItem = timeout
        let responseTimeout = lifecycleResponseTimeout(for: controlID)
        DispatchQueue.main.asyncAfter(
            deadline: .now() + responseTimeout,
            execute: timeout
        )
    }

    private func lifecycleResponseTimeout(for controlID: String) -> TimeInterval {
        let actionID = actionByControlID[controlID] ?? ""
        let configured = TimeInterval(configurationStore.timeoutSeconds(for: actionID) ?? 15)
        return max(
            Self.minimumLifecycleResponseTimeout,
            configured + Self.lifecycleTransportGrace
        )
    }

    private func shouldIgnoreLifecycleEvent(commandID: String) -> Bool {
        guard let transition = lifecycleTransition else { return false }
        return transition.commandID != commandID
    }

    private func reconcileLifecycleTransition() {
        guard var transition = lifecycleTransition,
              presentation.hasPresentation,
              presentation.isSlideShow == transition.targetIsSlideShow,
              isStateRelevant(to: transition) else { return }
        transition.stateConfirmed = true
        // The PC guarantees that a command-correlated presentation.state is
        // published before action.completed. It is therefore authoritative
        // enough to update availability immediately instead of waiting for a
        // terminal message that may be delayed by the BLE send queue.
        lifecycleTransition = transition
        resetLifecycleControlFeedback()
        finishLifecycleTransition(retireCommand: true)
    }

    private func isStateRelevant(to transition: LifecycleTransition) -> Bool {
        if let commandId = presentation.commandId {
            return commandId == transition.commandID
        }
        if let revision = presentation.revision {
            return revision > transition.baselineRevision
        }
        // Backward compatibility with a Praxis Bluetooth build that predates correlated
        // presentation revisions. Lifecycle actions are serialized locally,
        // so the opposite target state is still safe to use as confirmation.
        return presentation.commandId == nil
    }

    private func finishLifecycleTransition(retireCommand: Bool) {
        lifecycleTimeoutWorkItem?.cancel()
        lifecycleTimeoutWorkItem = nil
        if retireCommand, let commandID = lifecycleTransition?.commandID {
            retiredLifecycleCommandIDs.insert(commandID)
            if retiredLifecycleCommandIDs.count > 64 {
                retiredLifecycleCommandIDs.removeAll(keepingCapacity: true)
                retiredLifecycleCommandIDs.insert(commandID)
            }
        }
        lifecycleTransition = nil
    }

    private func resetLifecycleControlFeedback() {
        guard let controlID = lifecycleTransition?.controlID,
              let control = actionControls[controlID] else { return }
        if let button = control as? PresentationActionButton {
            button.setBusy(false)
        } else if let button = control as? HoldToConfirmButton {
            button.setBusy(false)
        }
    }

    private func buildLayout() {
        let workspace = buildWorkspace()
        let controls = buildControls()
        let root = UIStackView(arrangedSubviews: [workspace, controls])
        root.axis = .horizontal
        root.alignment = .fill
        root.spacing = 12
        view.addSubview(root)
        root.pinEdges(to: view, insets: UIEdgeInsets(top: 12, left: 18, bottom: 12, right: 18))
        controls.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            controls.widthAnchor.constraint(equalToConstant: 392),
            workspace.widthAnchor.constraint(greaterThanOrEqualToConstant: 520)
        ])
    }

    private func buildWorkspace() -> UIView {
        let card = UIView()
        PraxisTheme.configureCard(card, radius: 16)
        workspaceIcon.color = PraxisTheme.systemBlue.withAlphaComponent(0.80)
        workspaceTitle.text = "演讲者备注"
        workspaceTitle.font = UIFont.systemFont(ofSize: 13, weight: .semibold)
        workspaceTitle.textColor = PraxisTheme.text.withAlphaComponent(0.78)
        workspaceMeta.font = UIFont.monospacedDigitSystemFont(ofSize: 9, weight: .semibold)
        workspaceMeta.textColor = PraxisTheme.dim
        let headingLeft = UIStackView(arrangedSubviews: [workspaceIcon, workspaceTitle])
        headingLeft.axis = .horizontal
        headingLeft.alignment = .center
        headingLeft.spacing = 9
        let heading = UIStackView(arrangedSubviews: [headingLeft, UIView(), workspaceMeta])
        heading.axis = .horizontal
        heading.alignment = .center
        card.addSubview(heading)
        heading.translatesAutoresizingMaskIntoConstraints = false
        workspaceIcon.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            heading.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 18),
            heading.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -18),
            heading.topAnchor.constraint(equalTo: card.topAnchor),
            heading.heightAnchor.constraint(equalToConstant: 48),
            workspaceIcon.widthAnchor.constraint(equalToConstant: 20),
            workspaceIcon.heightAnchor.constraint(equalToConstant: 20)
        ])
        let divider = UIView()
        divider.backgroundColor = PraxisTheme.line
        card.addSubview(divider)
        divider.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            divider.leadingAnchor.constraint(equalTo: card.leadingAnchor),
            divider.trailingAnchor.constraint(equalTo: card.trailingAnchor),
            divider.topAnchor.constraint(equalTo: heading.bottomAnchor),
            divider.heightAnchor.constraint(equalToConstant: 1)
        ])

        notesView.backgroundColor = .clear
        notesView.textColor = PraxisTheme.text
        notesView.isEditable = false
        notesView.isSelectable = true
        notesView.alwaysBounceVertical = true
        notesView.textContainerInset = UIEdgeInsets(top: 21, left: 20, bottom: 24, right: 20)
        card.addSubview(notesView)
        notesView.translatesAutoresizingMaskIntoConstraints = false

        recentTable.backgroundColor = .clear
        recentTable.separatorStyle = .none
        recentTable.rowHeight = 49
        recentTable.showsVerticalScrollIndicator = true
        recentTable.contentInset = UIEdgeInsets(top: 6, left: 0, bottom: 8, right: 0)
        recentTable.dataSource = self
        recentTable.delegate = self
        recentTable.register(RecentPresentationCell.self, forCellReuseIdentifier: RecentPresentationCell.reuseIdentifier)
        card.addSubview(recentTable)
        recentTable.translatesAutoresizingMaskIntoConstraints = false

        emptyLabel.numberOfLines = 0
        emptyLabel.textAlignment = .center
        emptyLabel.font = UIFont.systemFont(ofSize: 14, weight: .regular)
        emptyLabel.textColor = PraxisTheme.muted
        card.addSubview(emptyLabel)
        emptyLabel.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            notesView.leadingAnchor.constraint(equalTo: card.leadingAnchor),
            notesView.trailingAnchor.constraint(equalTo: card.trailingAnchor),
            notesView.topAnchor.constraint(equalTo: divider.bottomAnchor),
            notesView.bottomAnchor.constraint(equalTo: card.bottomAnchor),
            recentTable.leadingAnchor.constraint(equalTo: card.leadingAnchor),
            recentTable.trailingAnchor.constraint(equalTo: card.trailingAnchor),
            recentTable.topAnchor.constraint(equalTo: divider.bottomAnchor),
            recentTable.bottomAnchor.constraint(equalTo: card.bottomAnchor),
            emptyLabel.leadingAnchor.constraint(equalTo: card.leadingAnchor, constant: 28),
            emptyLabel.trailingAnchor.constraint(equalTo: card.trailingAnchor, constant: -28),
            emptyLabel.centerYAnchor.constraint(equalTo: notesView.centerYAnchor)
        ])
        return card
    }

    private func buildControls() -> UIView {
        let container = UIView()
        let titleBar = UIView()
        PraxisTheme.configureCard(titleBar, radius: 13)
        statusDot.backgroundColor = PraxisTheme.dim
        statusDot.layer.cornerRadius = 4
        statusDot.translatesAutoresizingMaskIntoConstraints = false
        statusLabel.text = "正在检查"
        statusLabel.font = UIFont.systemFont(ofSize: 9, weight: .semibold)
        statusLabel.textColor = PraxisTheme.muted
        let status = UIStackView(arrangedSubviews: [statusDot, statusLabel])
        status.axis = .horizontal
        status.alignment = .center
        status.spacing = 8
        let divider = UIView()
        divider.backgroundColor = PraxisTheme.line
        divider.translatesAutoresizingMaskIntoConstraints = false
        let titleRow = UIStackView(arrangedSubviews: [status, divider, titleMarquee])
        titleRow.axis = .horizontal
        titleRow.alignment = .center
        titleRow.spacing = 11
        titleBar.addSubview(titleRow)
        titleRow.pinEdges(to: titleBar, insets: UIEdgeInsets(top: 0, left: 12, bottom: 0, right: 12))
        NSLayoutConstraint.activate([
            statusDot.widthAnchor.constraint(equalToConstant: 8),
            statusDot.heightAnchor.constraint(equalToConstant: 8),
            divider.widthAnchor.constraint(equalToConstant: 1),
            divider.heightAnchor.constraint(equalToConstant: 24),
            titleBar.heightAnchor.constraint(equalToConstant: 48)
        ])

        configurePowerPointButton()
        let elapsedMetric = metricView(title: "已用时间", valueLabel: elapsedLabel)
        let slideMetric = metricView(title: "幻灯片", valueLabel: slideLabel)
        let metrics = UIStackView(arrangedSubviews: [elapsedMetric, slideMetric])
        metrics.axis = .vertical
        metrics.spacing = 8
        metrics.distribution = .fillEqually
        let summary = UIStackView(arrangedSubviews: [powerPointButton, metrics])
        summary.axis = .horizontal
        summary.alignment = .fill
        summary.spacing = 8
        powerPointButton.translatesAutoresizingMaskIntoConstraints = false
        powerPointButton.widthAnchor.constraint(equalToConstant: 120).isActive = true
        summary.heightAnchor.constraint(equalToConstant: 104).isActive = true

        let startRow = row([startBeginning, startCurrent], spacing: 9)
        startRow.heightAnchor.constraint(equalToConstant: 48).isActive = true
        let navigation = row([previousButton, nextButton], spacing: 9)
        let stack = UIStackView(arrangedSubviews: [titleBar, summary, startRow, navigation, endButton])
        stack.axis = .vertical
        stack.alignment = .fill
        stack.spacing = 8
        container.addSubview(stack)
        stack.pinEdges(to: container)
        endButton.heightAnchor.constraint(equalToConstant: 48).isActive = true
        navigation.heightAnchor.constraint(greaterThanOrEqualToConstant: 188).isActive = true

        bind(startBeginning, controlID: "presentation-start-beginning")
        bind(startCurrent, controlID: "presentation-start-current")
        bind(previousButton, controlID: "presentation-previous")
        bind(nextButton, controlID: "presentation-next")
        actionControls["presentation-open"] = powerPointButton
        actionControls["presentation-end"] = endButton
        powerPointButton.accessibilityIdentifier = "presentation-open"
        endButton.accessibilityIdentifier = "presentation-end"
        endButton.onConfirmed = { [weak self] in self?.invoke(controlID: "presentation-end") }
        return container
    }

    private func configurePowerPointButton() {
        powerPointButton.backgroundColor = .clear
        powerPointButton.layer.cornerRadius = 14
        powerPointButton.layer.borderWidth = 1
        powerPointButton.layer.borderColor = UIColor.clear.cgColor
        powerPointImageView.contentMode = .scaleAspectFit
        powerPointImageView.isUserInteractionEnabled = false
        powerPointButton.addSubview(powerPointImageView)
        powerPointImageView.pinEdges(to: powerPointButton, insets: UIEdgeInsets(top: 7, left: 12, bottom: 7, right: 12))
        powerPointPlaceholder.color = PraxisTheme.systemBlue
        powerPointPlaceholder.isUserInteractionEnabled = false
        powerPointButton.addSubview(powerPointPlaceholder)
        powerPointPlaceholder.pinEdges(to: powerPointButton, insets: UIEdgeInsets(top: 22, left: 38, bottom: 22, right: 38))
        refreshIcon(for: "presentation.open")
        powerPointButton.addTarget(self, action: #selector(powerPointPressBegan), for: .touchDown)
        powerPointButton.addTarget(self, action: #selector(powerPointPressEnded), for: [.touchUpInside, .touchUpOutside, .touchCancel])
        powerPointButton.addTarget(self, action: #selector(openPowerPoint), for: .touchUpInside)
        powerPointButton.accessibilityLabel = "打开或显示 PowerPoint"
        powerPointButton.isAccessibilityElement = true
    }

    func refreshIcon(for actionID: String) {
        guard actionID == "presentation.open" else { return }
        let image = iconStore.image(for: actionID)
        powerPointImageView.image = image
        powerPointImageView.isHidden = image == nil
        powerPointPlaceholder.isHidden = image != nil
    }

    private func metricView(title: String, valueLabel: UILabel) -> UIView {
        let view = UIView()
        PraxisTheme.configureCard(view, radius: 11)
        view.backgroundColor = UIColor.white.withAlphaComponent(0.022)
        let titleLabel = UILabel()
        titleLabel.text = title
        titleLabel.font = UIFont.systemFont(ofSize: 10, weight: .semibold)
        titleLabel.textColor = PraxisTheme.muted
        valueLabel.font = UIFont.monospacedDigitSystemFont(ofSize: 13, weight: .semibold)
        valueLabel.textColor = PraxisTheme.text.withAlphaComponent(0.86)
        let row = UIStackView(arrangedSubviews: [titleLabel, UIView(), valueLabel])
        row.axis = .horizontal
        row.alignment = .center
        view.addSubview(row)
        row.pinEdges(to: view, insets: UIEdgeInsets(top: 0, left: 10, bottom: 0, right: 10))
        return view
    }

    private func row(_ views: [UIView], spacing: CGFloat) -> UIStackView {
        let stack = UIStackView(arrangedSubviews: views)
        stack.axis = .horizontal
        stack.distribution = .fillEqually
        stack.spacing = spacing
        return stack
    }

    private func bind(_ button: UIControl, controlID: String) {
        button.accessibilityIdentifier = controlID
        button.addTarget(self, action: #selector(invokeButton(_:)), for: .touchUpInside)
        actionControls[controlID] = button
    }

    private func render() {
        guard isViewLoaded else { return }
        let displayPresentation: PraxisPresentationState
        if lifecycleTransition != nil,
           !presentation.hasPresentation,
           lastStablePresentation.hasPresentation {
            displayPresentation = lastStablePresentation
        } else {
            displayPresentation = presentation
        }
        let effectiveSlideShow = lifecycleTransition?.targetIsSlideShow ?? displayPresentation.isSlideShow
        let lifecycleTransitionInFlight = lifecycleTransition != nil
        let statusNames = [
            "notInstalled": "未安装", "starting": "正在启动", "noPresentation": "等待文稿",
            "ready": "已就绪", "presenting": "放映中", "error": "状态异常"
        ]
        if !connected {
            statusLabel.text = "连接中断"
        } else if lifecycleTransitionInFlight {
            switch lifecycleTransition?.phase {
            case .awaitingState: statusLabel.text = "正在同步"
            case .recoveringFromFailure: statusLabel.text = "正在确认"
            default: statusLabel.text = "正在切换"
            }
        } else {
            statusLabel.text = statusNames[displayPresentation.status] ?? "未打开"
        }
        titleMarquee.text = displayPresentation.presentationName ?? "未打开演示文稿"
        elapsedLabel.text = formatElapsed(displayPresentation.elapsedSeconds)
        slideLabel.text = displayPresentation.currentSlide > 0 && displayPresentation.totalSlides > 0
            ? "\(displayPresentation.currentSlide) / \(displayPresentation.totalSlides)" : "— / —"

        if !connected || displayPresentation.status == "error" || displayPresentation.status == "notInstalled" {
            statusDot.backgroundColor = PraxisTheme.danger
        } else if effectiveSlideShow {
            statusDot.backgroundColor = PraxisTheme.green
        } else if displayPresentation.status == "ready" || displayPresentation.status == "noPresentation" {
            statusDot.backgroundColor = PraxisTheme.systemBlue
        } else {
            statusDot.backgroundColor = PraxisTheme.dim
        }

        if displayPresentation.hasPresentation {
            workspaceIcon.glyph = .notes
            workspaceTitle.text = "演讲者备注"
            workspaceMeta.text = displayPresentation.currentSlide > 0 ? "第 \(displayPresentation.currentSlide) 页" : "当前页"
            recentTable.isHidden = true
            notesView.isHidden = false
            emptyLabel.isHidden = !displayPresentation.notes.isEmpty
            emptyLabel.text = "当前幻灯片没有演讲者备注。"
            notesView.attributedText = notesText(displayPresentation.notes)
        } else {
            workspaceIcon.glyph = .recentPresentations
            workspaceTitle.text = "最近的演示文稿"
            workspaceMeta.text = presentation.status == "noPresentation" ? "\(min(10, presentation.recentPresentations.count)) 个文件" : "等待就绪"
            notesView.isHidden = true
            recentTable.isHidden = presentation.status != "noPresentation" || presentation.recentPresentations.isEmpty
            emptyLabel.isHidden = !recentTable.isHidden
            if presentation.status == "notInstalled" {
                emptyLabel.text = "这台电脑未安装 PowerPoint。"
            } else if presentation.status == "error" {
                emptyLabel.text = presentation.error ?? "暂时无法读取 PowerPoint 状态。"
            } else if presentation.status == "noPresentation" && presentation.recentPresentations.isEmpty {
                emptyLabel.text = "暂无最近使用的演示文稿。"
            } else {
                emptyLabel.text = "最近文件将在 PowerPoint 就绪后显示"
            }
            recentTable.reloadData()
        }

        let installed = displayPresentation.isInstalled
        setEnabled(powerPointButton, actionID: "presentation.open", capability: connected && installed && !lifecycleTransitionInFlight)
        setEnabled(startBeginning, actionID: "presentation.startBeginning", capability: connected && displayPresentation.hasPresentation && !effectiveSlideShow && !lifecycleTransitionInFlight)
        setEnabled(startCurrent, actionID: "presentation.startCurrent", capability: connected && displayPresentation.hasPresentation && !effectiveSlideShow && !lifecycleTransitionInFlight)
        setEnabled(previousButton, actionID: "presentation.previous", capability: connected && displayPresentation.hasPresentation && !lifecycleTransitionInFlight)
        setEnabled(nextButton, actionID: "presentation.next", capability: connected && displayPresentation.hasPresentation && !lifecycleTransitionInFlight)
        setEnabled(endButton, actionID: "presentation.end", capability: connected && effectiveSlideShow && !lifecycleTransitionInFlight)
    }

    private func setEnabled(_ control: UIControl, actionID: String, capability: Bool) {
        control.isEnabled = capability && configurationStore.hasAction(actionID)
    }

    private func notesText(_ notes: String) -> NSAttributedString {
        let paragraph = NSMutableParagraphStyle()
        paragraph.lineSpacing = 7
        paragraph.alignment = .justified
        return NSAttributedString(string: notes, attributes: [
            .font: UIFont.systemFont(ofSize: 22, weight: .regular),
            .foregroundColor: PraxisTheme.text,
            .paragraphStyle: paragraph
        ])
    }

    private func formatElapsed(_ totalSeconds: Int) -> String {
        let value = max(0, totalSeconds)
        let hours = value / 3_600
        let minutes = (value % 3_600) / 60
        let seconds = value % 60
        return hours > 0
            ? String(format: "%d:%02d:%02d", hours, minutes, seconds)
            : String(format: "%02d:%02d", minutes, seconds)
    }

    private func invoke(controlID: String, itemID: String? = nil) {
        guard let actionID = actionByControlID[controlID] else { return }
        guard configurationStore.hasAction(actionID) else {
            delegate?.presentationViewController(self, showNotice: "配置尚未就绪", message: "Praxis Bluetooth 尚未提供 \(actionID)。")
            return
        }
        let isLifecycle = controlID == "presentation-start-beginning" ||
            controlID == "presentation-start-current" || controlID == "presentation-end"
        guard !isLifecycle || lifecycleTransition == nil else { return }
        if controlID == "presentation-previous" || controlID == "presentation-next" {
            let now = Date()
            guard now.timeIntervalSince(lastNavigationInvocation) >= Self.navigationMinimumInterval else { return }
            lastNavigationInvocation = now
        }
        guard let delegate = delegate,
              let commandID = delegate.presentationViewController(
            self,
            invokeAction: actionID,
            controlID: controlID,
            itemID: itemID
        ) else { return }
        switch controlID {
        case "presentation-start-beginning", "presentation-start-current":
            beginLifecycleTransition(commandID: commandID, controlID: controlID, requestedState: true)
        case "presentation-end":
            beginLifecycleTransition(commandID: commandID, controlID: controlID, requestedState: false)
        default:
            break
        }
    }

    @objc private func invokeButton(_ sender: UIControl) {
        guard let controlID = sender.accessibilityIdentifier else { return }
        invoke(controlID: controlID)
    }

    @objc private func openPowerPoint() { invoke(controlID: "presentation-open") }

    @objc private func powerPointPressBegan() {
        guard powerPointButton.isEnabled else { return }
        UIView.animate(withDuration: 0.16) {
            self.powerPointButton.transform = CGAffineTransform(scaleX: 0.92, y: 0.92)
            self.powerPointButton.layer.borderColor = PraxisTheme.systemBlue.withAlphaComponent(0.46).cgColor
        }
    }

    @objc private func powerPointPressEnded() {
        UIView.animate(withDuration: 0.16) {
            self.powerPointButton.transform = .identity
            let color = self.powerPointError ? PraxisTheme.danger : PraxisTheme.systemBlue
            self.powerPointButton.layer.borderColor = color.withAlphaComponent(self.powerPointBusy || self.powerPointError ? 0.50 : 0).cgColor
            self.powerPointButton.backgroundColor = self.powerPointBusy ? PraxisTheme.systemBlue.withAlphaComponent(0.08) : .clear
        }
    }

    private func setPowerPointBusy(_ busy: Bool, error: Bool) {
        powerPointResetWorkItem?.cancel()
        powerPointBusy = busy
        powerPointError = error
        UIView.animate(withDuration: 0.16, delay: 0, options: [.beginFromCurrentState, .allowUserInteraction], animations: {
            let color = error ? PraxisTheme.danger : PraxisTheme.systemBlue
            self.powerPointButton.layer.borderColor = color.withAlphaComponent(busy || error ? 0.50 : 0).cgColor
            self.powerPointButton.backgroundColor = busy ? PraxisTheme.systemBlue.withAlphaComponent(0.08) : .clear
        })
        if error {
            let item = DispatchWorkItem { [weak self] in self?.setPowerPointBusy(false, error: false) }
            powerPointResetWorkItem = item
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.90, execute: item)
        }
    }
}

extension PresentationViewController: UITableViewDataSource, UITableViewDelegate {
    func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int {
        min(10, presentation.recentPresentations.count)
    }

    func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
        let cell = tableView.dequeueReusableCell(withIdentifier: RecentPresentationCell.reuseIdentifier, for: indexPath) as! RecentPresentationCell
        cell.configure(index: indexPath.row, item: presentation.recentPresentations[indexPath.row])
        return cell
    }

    func tableView(_ tableView: UITableView, shouldHighlightRowAt indexPath: IndexPath) -> Bool {
        connected && lifecycleTransition == nil && presentation.recentPresentations[indexPath.row].isAvailable && configurationStore.hasAction("presentation.openRecent")
    }

    func tableView(_ tableView: UITableView, didHighlightRowAt indexPath: IndexPath) {
        (tableView.cellForRow(at: indexPath) as? RecentPresentationCell)?.setPressed(true)
    }

    func tableView(_ tableView: UITableView, didUnhighlightRowAt indexPath: IndexPath) {
        (tableView.cellForRow(at: indexPath) as? RecentPresentationCell)?.setPressed(false)
    }

    func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        let item = presentation.recentPresentations[indexPath.row]
        _ = delegate?.presentationViewController(
            self,
            invokeAction: "presentation.openRecent",
            controlID: "presentation-recent-\(item.id)",
            itemID: item.id
        )
    }
}
