import XCTest

#if canImport(PraxisPanelIOS)
@testable import PraxisPanelIOS
#else
@testable import PraxisFrameCodec
#endif

final class PraxisFrameCodecTests: XCTestCase {
    func testSingleFrameHeaderUsesLittleEndianLayout() throws {
        let message = Data([0x7B, 0x7D])
        let frame = try XCTUnwrap(
            PraxisFrameEncoder.encode(message: message, maximumFrameBytes: 64, messageID: 0x1234).first
        )

        XCTAssertEqual(Array(frame.prefix(12)), [
            0x50, 0x58, 0x01, 0x03,
            0x34, 0x12,
            0x00, 0x00,
            0x02, 0x00, 0x00, 0x00
        ])
        XCTAssertEqual(frame.dropFirst(12), message)
    }

    func testFragmentedMessageRoundTrips() throws {
        let message = Data((0..<4_096).map { UInt8($0 % 251) })
        let frames = try PraxisFrameEncoder.encode(message: message, maximumFrameBytes: 47, messageID: 19)
        XCTAssertGreaterThan(frames.count, 1)
        XCTAssertTrue(frames.allSatisfy { $0.count <= 47 })

        let reassembler = PraxisFrameReassembler()
        var completed: Data?
        for frame in frames {
            if let value = try reassembler.accept(frame) {
                XCTAssertNil(completed)
                completed = value
            }
        }
        XCTAssertEqual(completed, message)
    }

    func testUTF8JSONIsReassembledOnlyAfterFinalFrame() throws {
        let message = try XCTUnwrap("{\"type\":\"ping\",\"payload\":\"你好\"}".data(using: .utf8))
        let frames = try PraxisFrameEncoder.encode(message: message, maximumFrameBytes: 19, messageID: 77)
        let reassembler = PraxisFrameReassembler()

        for frame in frames.dropLast() {
            XCTAssertNil(try reassembler.accept(frame))
        }
        XCTAssertEqual(try reassembler.accept(try XCTUnwrap(frames.last)), message)
    }

    func testOutOfOrderFrameDropsAssemblyAndRecoversAtNextStart() throws {
        let frames = try PraxisFrameEncoder.encode(message: Data(repeating: 7, count: 80), maximumFrameBytes: 24, messageID: 41)
        let reassembler = PraxisFrameReassembler()
        XCTAssertNil(try reassembler.accept(frames[0]))
        XCTAssertThrowsError(try reassembler.accept(frames[2]))
        XCTAssertThrowsError(try reassembler.accept(frames[1]))

        let recovery = Data("recovered".utf8)
        let recoveryFrame = try XCTUnwrap(
            PraxisFrameEncoder.encode(message: recovery, maximumFrameBytes: 64, messageID: 42).first
        )
        XCTAssertEqual(try reassembler.accept(recoveryFrame), recovery)
    }

    func testDuplicateFrameDropsCurrentMessage() throws {
        let frames = try PraxisFrameEncoder.encode(message: Data(repeating: 1, count: 72), maximumFrameBytes: 24, messageID: 90)
        let reassembler = PraxisFrameReassembler()
        XCTAssertNil(try reassembler.accept(frames[0]))
        XCTAssertNil(try reassembler.accept(frames[1]))
        XCTAssertThrowsError(try reassembler.accept(frames[1]))
        XCTAssertThrowsError(try reassembler.accept(frames[2]))
    }

    func testDuplicateStartIsRejected() throws {
        let frames = try PraxisFrameEncoder.encode(message: Data(repeating: 3, count: 40), maximumFrameBytes: 24, messageID: 12)
        let reassembler = PraxisFrameReassembler()
        XCTAssertNil(try reassembler.accept(frames[0]))
        XCTAssertThrowsError(try reassembler.accept(frames[0])) { error in
            XCTAssertEqual(error as? PraxisFrameError, .duplicateStart(12))
        }
    }

    func testMessageIDMismatchDropsAssembly() throws {
        let frames = try PraxisFrameEncoder.encode(message: Data(repeating: 4, count: 50), maximumFrameBytes: 24, messageID: 11)
        var mismatched = frames[1]
        mismatched[4] = 0xEF
        mismatched[5] = 0xBE

        let reassembler = PraxisFrameReassembler()
        XCTAssertNil(try reassembler.accept(frames[0]))
        XCTAssertThrowsError(try reassembler.accept(mismatched)) { error in
            XCTAssertEqual(error as? PraxisFrameError, .inconsistentFrame)
        }
    }

    func testDeclaredOversizeAndEncoderOversizeAreRejected() throws {
        XCTAssertThrowsError(
            try PraxisFrameEncoder.encode(
                message: Data(repeating: 0, count: PraxisFrameEncoder.maximumMessageBytes + 1),
                maximumFrameBytes: 64,
                messageID: 1
            )
        )

        var frame = try XCTUnwrap(
            PraxisFrameEncoder.encode(message: Data([1]), maximumFrameBytes: 64, messageID: 2).first
        )
        let oversized = UInt32(PraxisFrameEncoder.maximumMessageBytes + 1)
        frame[8] = UInt8(truncatingIfNeeded: oversized)
        frame[9] = UInt8(truncatingIfNeeded: oversized >> 8)
        frame[10] = UInt8(truncatingIfNeeded: oversized >> 16)
        frame[11] = UInt8(truncatingIfNeeded: oversized >> 24)
        XCTAssertThrowsError(try PraxisFrameReassembler().accept(frame))
    }

    func testInvalidHeaderAndLengthFailureDoNotPoisonRecovery() throws {
        let reassembler = PraxisFrameReassembler()
        XCTAssertThrowsError(try reassembler.accept(Data(repeating: 0, count: 12)))

        var first = try PraxisFrameEncoder.encode(
            message: Data(repeating: 8, count: 30),
            maximumFrameBytes: 22,
            messageID: 61
        )[0]
        first[3] |= PraxisFrameHeader.endFlag
        XCTAssertThrowsError(try reassembler.accept(first))

        let recovery = Data("ok".utf8)
        let recoveryFrame = try XCTUnwrap(
            PraxisFrameEncoder.encode(message: recovery, maximumFrameBytes: 64, messageID: 62).first
        )
        XCTAssertEqual(try reassembler.accept(recoveryFrame), recovery)
    }

    func testMaximumFrameMustExceedHeader() {
        XCTAssertThrowsError(
            try PraxisFrameEncoder.encode(message: Data([1]), maximumFrameBytes: 12, messageID: 1)
        ) { error in
            XCTAssertEqual(error as? PraxisFrameError, .frameSizeTooSmall(12))
        }
    }
}

#if canImport(PraxisPanelIOS)
import UIKit

final class PraxisControlInteractionTests: XCTestCase {
    func testPairingCryptographyMatchesCrossPlatformVectors() throws {
        XCTAssertEqual(
            PraxisCrypto.sha256(Data("abc".utf8)).map { String(format: "%02x", $0) }.joined(),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        )
        XCTAssertEqual(
            PraxisCrypto.hmac(key: Data("key".utf8), data: Data("The quick brown fox jumps over the lazy dog".utf8))
                .map { String(format: "%02x", $0) }.joined(),
            "f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8"
        )
        XCTAssertEqual(
            try PraxisCrypto.deriveCodeKey(code: "123456", salt: Data("salt".utf8), iterations: 1)
                .map { String(format: "%02x", $0) }.joined(),
            "b61448ae9b42f589af9f949199d9403dd1332ef8fafc6d33f2ede59a15f99d14"
        )
        XCTAssertEqual(
            try PraxisCrypto.deriveCodeKey(code: "123456", salt: Data("salt".utf8), iterations: 50_000)
                .map { String(format: "%02x", $0) }.joined(),
            "9b62b95641491f824b447bf3e4f6555233ace33dd259f91cc3098b88de1dacdb"
        )
    }

    func testPairingKeyDerivationDoesNotBlockBluetoothMainQueue() throws {
        let secureSession = PraxisSecureSession()
        let salt = Data(repeating: 0x31, count: 16)
        let serverNonce = Data(repeating: 0x42, count: 24).base64EncodedString()
        _ = secureSession.handlePairing(PraxisProtocolMessage(
            type: "pairing.challenge",
            payload: .object([
                "serverId": .string("server-test"),
                "serverName": .string("Praxis Test"),
                "deviceId": .string("ipad-test"),
                "serverNonce": .string(serverNonce),
                "salt": .string(salt.base64EncodedString()),
                "iterations": .number(50_000)
            ])
        ))

        let derivationStarted = expectation(description: "derivation started off main queue")
        let completionReceived = expectation(description: "pairing request returned to main queue")
        let allowDerivationToFinish = DispatchSemaphore(value: 0)
        var completed = false

        secureSession.submit(
            code: "123456",
            workQueue: DispatchQueue(label: "com.praxis.tests.pairing"),
            deriveKey: { _, _, _ in
                XCTAssertFalse(Thread.isMainThread)
                derivationStarted.fulfill()
                XCTAssertEqual(allowDerivationToFinish.wait(timeout: .now() + 2), .success)
                return Data(repeating: 0x55, count: 32)
            },
            completion: { result in
                XCTAssertTrue(Thread.isMainThread)
                XCTAssertEqual(try? result.get().type, "pairing.request")
                completed = true
                completionReceived.fulfill()
            }
        )

        wait(for: [derivationStarted], timeout: 1)
        XCTAssertFalse(completed)
        allowDerivationToFinish.signal()
        wait(for: [completionReceived], timeout: 2)
        XCTAssertTrue(completed)
    }

    func testRealtimeBluetoothQueueKeepsOnlyNewestUnsentNavigationPulse() {
        var queue = BluetoothTransmissionQueue()
        queue.enqueue(frames: [Data([1])], coalescingKey: "presentation.navigation")
        queue.enqueue(frames: [Data([2])], coalescingKey: "presentation.navigation")

        XCTAssertEqual(queue.pendingFrameCount, 1)
        XCTAssertEqual(queue.nextFrame, Data([2]))
    }

    func testRealtimeBluetoothQueueFinishesStartedMessageBeforeNewestPulse() {
        var queue = BluetoothTransmissionQueue()
        queue.enqueue(frames: [Data([1]), Data([2])], coalescingKey: "presentation.navigation")
        queue.markNextFrameSent()
        queue.enqueue(frames: [Data([3])], coalescingKey: "presentation.navigation")

        XCTAssertEqual(queue.pendingFrameCount, 2)
        XCTAssertEqual(queue.nextFrame, Data([2]))
        queue.markNextFrameSent()
        XCTAssertEqual(queue.nextFrame, Data([3]))
    }

    func testApplicationArtworkHitRoutesToControl() {
        let button = PraxisControlButton(
            controlID: "chatgpt",
            title: "ChatGPT",
            colorName: "green",
            style: .application(placeholder: "C")
        )
        button.frame = CGRect(x: 0, y: 0, width: 132, height: 236)
        button.layoutIfNeeded()

        XCTAssertTrue(button.hitTest(CGPoint(x: 66, y: 100), with: nil) === button)
    }

    func testCompletedActionRestoresTranslucentRestingBorder() {
        let button = PraxisControlButton(
            controlID: "task-view",
            title: "任务视图",
            colorName: "blue",
            style: .system(glyph: .taskView, subtitle: "Win + Tab")
        )

        button.setActionState(busy: true, error: false)
        button.setActionState(busy: false, error: false)

        XCTAssertEqual(borderAlpha(of: button), 0.10, accuracy: 0.015)
    }

    func testActiveApplicationKeepsSurfaceBorderClear() throws {
        let button = PraxisControlButton(
            controlID: "chatgpt",
            title: "ChatGPT",
            colorName: "green",
            style: .application(placeholder: "C")
        )
        UIView.setAnimationsEnabled(false)
        defer { UIView.setAnimationsEnabled(true) }

        button.setApplicationActive(true, animated: true)
        let surface = try XCTUnwrap(button.subviews.first)

        XCTAssertEqual(borderAlpha(of: surface), 0, accuracy: 0.001)
    }

    func testControlRowsAreVerticallyBalancedAtIPadMiniWidth() {
        let controller = ControlViewController(
            configurationStore: PraxisConfigurationStore(),
            stateStore: PraxisStateStore()
        )
        controller.loadViewIfNeeded()
        controller.view.frame = CGRect(x: 0, y: 0, width: 1_024, height: 618)
        controller.view.layoutIfNeeded()

        let buttons: [PraxisControlButton] = descendants(of: controller.view)
        XCTAssertEqual(buttons.count, 21)
        let frames = buttons.map { $0.convert($0.bounds, to: controller.view) }
        let topGap = frames.map(\.minY).min() ?? 0
        let bottomGap = controller.view.bounds.height - (frames.map(\.maxY).max() ?? 0)

        XCTAssertEqual(topGap, bottomGap, accuracy: 1)
        XCTAssertGreaterThanOrEqual(topGap, 12)
    }

    func testControlOrderCompactHeightAndApplicationStatusLength() throws {
        let controller = ControlViewController(
            configurationStore: PraxisConfigurationStore(),
            stateStore: PraxisStateStore()
        )
        controller.loadViewIfNeeded()
        controller.view.frame = CGRect(x: 0, y: 0, width: 1_024, height: 618)
        controller.view.layoutIfNeeded()

        let buttons: [PraxisControlButton] = descendants(of: controller.view)
        let byID = Dictionary(uniqueKeysWithValues: buttons.map { ($0.controlID, $0) })
        let systemIDs = ["desktop-view", "screenshot", "explorer", "notepad", "terminal", "calculator", "task-view"]
        let shortcutIDs = ["voice-input", "copy", "paste", "play", "mute", "volume-down", "volume-up"]

        XCTAssertEqual(
            systemIDs.compactMap { byID[$0] }.sorted { $0.frame.minX < $1.frame.minX }.map(\.controlID),
            systemIDs
        )
        XCTAssertEqual(
            shortcutIDs.compactMap { byID[$0] }.sorted { $0.frame.minX < $1.frame.minX }.map(\.controlID),
            shortcutIDs
        )
        for controlID in shortcutIDs {
            XCTAssertEqual(try XCTUnwrap(byID[controlID]).bounds.height, 48, accuracy: 0.5)
        }

        let appButton = try XCTUnwrap(byID["chatgpt"])
        let statusTrack = try XCTUnwrap(appButton.subviews.first { abs($0.bounds.height - 3) < 0.5 })
        XCTAssertEqual(statusTrack.bounds.width, 52, accuracy: 0.5)
        XCTAssertGreaterThan(statusTrack.bounds.width, statusTrack.bounds.height * 10)
    }

    func testPresentationControlsUseAvailableVerticalSpace() {
        let controller = PresentationViewController(
            configurationStore: PraxisConfigurationStore(),
            stateStore: PraxisStateStore()
        )
        controller.loadViewIfNeeded()
        controller.view.frame = CGRect(x: 0, y: 0, width: 1_024, height: 618)
        controller.view.layoutIfNeeded()

        let actionButtons: [PresentationActionButton] = descendants(of: controller.view)
        let navigationButtons = actionButtons.filter { $0.bounds.height > 100 }
        let endButtons: [HoldToConfirmButton] = descendants(of: controller.view)

        XCTAssertEqual(actionButtons.count, 4)
        XCTAssertEqual(navigationButtons.count, 2)
        XCTAssertGreaterThan(navigationButtons[0].bounds.height, 250)
        XCTAssertEqual(endButtons.first?.bounds.width ?? 0, 392, accuracy: 1)
    }

    func testRecentPresentationCardHasVisibleHorizontalInsets() throws {
        let cell = RecentPresentationCell(style: .default, reuseIdentifier: nil)
        cell.frame = CGRect(x: 0, y: 0, width: 584, height: 49)
        cell.layoutIfNeeded()
        let card = try XCTUnwrap(cell.contentView.subviews.first)

        XCTAssertEqual(card.frame.minX, 10, accuracy: 0.5)
        XCTAssertEqual(cell.contentView.bounds.maxX - card.frame.maxX, 10, accuracy: 0.5)
    }

    func testPresentationLifecycleWaitsForMatchingStateBeforeUnlocking() throws {
        let fixture = try makePresentationFixture(isSlideShow: true)
        let controller = fixture.controller
        let actionButtons: [PresentationActionButton] = descendants(of: controller.view)
        let controls = Dictionary(uniqueKeysWithValues: actionButtons.compactMap { button in
            button.accessibilityIdentifier.map { ($0, button as UIControl) }
        })
        let endButton = try XCTUnwrap((descendants(of: controller.view) as [HoldToConfirmButton]).first)

        XCTAssertFalse(try XCTUnwrap(controls["presentation-start-beginning"]).isEnabled)
        XCTAssertTrue(endButton.isEnabled)

        let endEvent = actionEvent(actionID: "presentation.end", controlID: "presentation-end")
        controller.handleAction(type: "action.accepted", event: endEvent)
        XCTAssertFalse(endButton.isEnabled)
        XCTAssertFalse(try XCTUnwrap(controls["presentation-start-beginning"]).isEnabled)

        controller.handleAction(type: "action.completed", event: endEvent)
        XCTAssertFalse(endButton.isEnabled)
        XCTAssertFalse(try XCTUnwrap(controls["presentation-start-beginning"]).isEnabled)
        XCTAssertFalse(try XCTUnwrap(controls["presentation-previous"]).isEnabled)
        XCTAssertFalse(try XCTUnwrap(controls["presentation-next"]).isEnabled)

        try applyPresentationState(isSlideShow: false, to: fixture.stateStore)
        controller.refreshState()
        XCTAssertFalse(endButton.isEnabled)
        XCTAssertTrue(try XCTUnwrap(controls["presentation-start-beginning"]).isEnabled)
        XCTAssertTrue(try XCTUnwrap(controls["presentation-start-current"]).isEnabled)
        XCTAssertTrue(try XCTUnwrap(controls["presentation-previous"]).isEnabled)
        XCTAssertTrue(try XCTUnwrap(controls["presentation-next"]).isEnabled)

        let startEvent = actionEvent(actionID: "presentation.startBeginning", controlID: "presentation-start-beginning")
        controller.handleAction(type: "action.accepted", event: startEvent)
        controller.handleAction(type: "action.completed", event: startEvent)
        XCTAssertFalse(endButton.isEnabled)
        XCTAssertFalse(try XCTUnwrap(controls["presentation-start-beginning"]).isEnabled)
        XCTAssertFalse(try XCTUnwrap(controls["presentation-next"]).isEnabled)

        try applyPresentationState(isSlideShow: true, to: fixture.stateStore)
        controller.refreshState()
        XCTAssertTrue(endButton.isEnabled)
        XCTAssertFalse(try XCTUnwrap(controls["presentation-start-beginning"]).isEnabled)
        XCTAssertTrue(try XCTUnwrap(controls["presentation-next"]).isEnabled)
    }

    func testPresentationPagingIsAvailableBeforeSlideShowStarts() throws {
        let controller = try makePresentationController(isSlideShow: false)
        let actionButtons: [PresentationActionButton] = descendants(of: controller.view)
        let controls = Dictionary(uniqueKeysWithValues: actionButtons.compactMap { button in
            button.accessibilityIdentifier.map { ($0, button as UIControl) }
        })
        let endButton = try XCTUnwrap((descendants(of: controller.view) as [HoldToConfirmButton]).first)

        XCTAssertTrue(try XCTUnwrap(controls["presentation-previous"]).isEnabled)
        XCTAssertTrue(try XCTUnwrap(controls["presentation-next"]).isEnabled)
        XCTAssertTrue(try XCTUnwrap(controls["presentation-start-beginning"]).isEnabled)
        XCTAssertTrue(try XCTUnwrap(controls["presentation-start-current"]).isEnabled)
        XCTAssertFalse(endButton.isEnabled)
    }

    func testSecondLifecycleCommandCannotReplaceInFlightTransition() throws {
        let fixture = try makePresentationFixture(isSlideShow: true)
        let controller = fixture.controller
        let actionButtons: [PresentationActionButton] = descendants(of: controller.view)
        let controls = Dictionary(uniqueKeysWithValues: actionButtons.compactMap { button in
            button.accessibilityIdentifier.map { ($0, button as UIControl) }
        })
        let endButton = try XCTUnwrap((descendants(of: controller.view) as [HoldToConfirmButton]).first)
        let endEvent = actionEvent(commandID: "older-end", actionID: "presentation.end", controlID: "presentation-end")
        let startEvent = actionEvent(commandID: "newer-start", actionID: "presentation.startCurrent", controlID: "presentation-start-current")

        controller.handleAction(type: "action.accepted", event: endEvent)
        controller.handleAction(type: "action.accepted", event: startEvent)
        controller.handleAction(type: "action.completed", event: startEvent)
        controller.handleAction(type: "action.completed", event: endEvent)

        XCTAssertFalse(endButton.isEnabled)
        XCTAssertFalse(try XCTUnwrap(controls["presentation-start-beginning"]).isEnabled)
        XCTAssertFalse(try XCTUnwrap(controls["presentation-next"]).isEnabled)

        try applyPresentationState(isSlideShow: false, to: fixture.stateStore)
        controller.refreshState()
        XCTAssertFalse(endButton.isEnabled)
        XCTAssertTrue(try XCTUnwrap(controls["presentation-start-beginning"]).isEnabled)
        XCTAssertTrue(try XCTUnwrap(controls["presentation-next"]).isEnabled)
    }

    func testLifecycleControlsStayLockedAcrossStaleSnapshot() throws {
        let fixture = try makePresentationFixture(isSlideShow: false)
        let controller = fixture.controller
        let delegate = PresentationDelegateSpy(commandIDs: ["start-command"])
        controller.delegate = delegate
        let startButton = try XCTUnwrap(
            (descendants(of: controller.view) as [PresentationActionButton]).first {
                $0.accessibilityIdentifier == "presentation-start-current"
            }
        )
        let endButton = try XCTUnwrap((descendants(of: controller.view) as [HoldToConfirmButton]).first)

        startButton.sendActions(for: .touchUpInside)
        XCTAssertFalse(startButton.isEnabled)

        let startEvent = actionEvent(
            commandID: "start-command",
            actionID: "presentation.startCurrent",
            controlID: "presentation-start-current"
        )
        controller.handleAction(type: "action.completed", event: startEvent)
        try applyPresentationState(isSlideShow: false, to: fixture.stateStore)
        controller.refreshState()
        XCTAssertFalse(startButton.isEnabled)
        XCTAssertFalse(endButton.isEnabled)

        try applyPresentationState(isSlideShow: true, to: fixture.stateStore)
        controller.refreshState()
        XCTAssertFalse(startButton.isEnabled)
        XCTAssertTrue(endButton.isEnabled)
    }

    func testLifecycleFailureWaitsForLateSuccessfulState() throws {
        let fixture = try makePresentationFixture(isSlideShow: true, revision: 10)
        let controller = fixture.controller
        let actionButtons: [PresentationActionButton] = descendants(of: controller.view)
        let controls = Dictionary(uniqueKeysWithValues: actionButtons.compactMap { button in
            button.accessibilityIdentifier.map { ($0, button as UIControl) }
        })
        let endButton = try XCTUnwrap((descendants(of: controller.view) as [HoldToConfirmButton]).first)
        let endEvent = actionEvent(
            commandID: "slow-end",
            actionID: "presentation.end",
            controlID: "presentation-end",
            message: "未能及时确认放映已结束"
        )

        controller.handleAction(type: "action.accepted", event: endEvent)
        XCTAssertFalse(controller.handleAction(type: "action.failed", event: endEvent))
        XCTAssertFalse(endButton.isEnabled)
        XCTAssertFalse(try XCTUnwrap(controls["presentation-start-current"]).isEnabled)
        XCTAssertFalse(try XCTUnwrap(controls["presentation-next"]).isEnabled)

        try applyPresentationState(
            isSlideShow: false,
            to: fixture.stateStore,
            revision: 12,
            commandId: nil
        )
        controller.refreshState()
        XCTAssertFalse(endButton.isEnabled)
        XCTAssertTrue(try XCTUnwrap(controls["presentation-start-current"]).isEnabled)
        XCTAssertTrue(try XCTUnwrap(controls["presentation-next"]).isEnabled)
    }

    func testUnrelatedCorrelatedStateCannotUnlockLifecycleControls() throws {
        let fixture = try makePresentationFixture(isSlideShow: false, revision: 20)
        let controller = fixture.controller
        let startButton = try XCTUnwrap(
            (descendants(of: controller.view) as [PresentationActionButton]).first {
                $0.accessibilityIdentifier == "presentation-start-current"
            }
        )
        let endButton = try XCTUnwrap((descendants(of: controller.view) as [HoldToConfirmButton]).first)
        let startEvent = actionEvent(
            commandID: "expected-start",
            actionID: "presentation.startCurrent",
            controlID: "presentation-start-current"
        )

        controller.handleAction(type: "action.accepted", event: startEvent)
        controller.handleAction(type: "action.completed", event: startEvent)
        try applyPresentationState(
            isSlideShow: true,
            to: fixture.stateStore,
            revision: 21,
            commandId: "different-command"
        )
        controller.refreshState()
        XCTAssertFalse(startButton.isEnabled)
        XCTAssertFalse(endButton.isEnabled)

        try applyPresentationState(
            isSlideShow: true,
            to: fixture.stateStore,
            revision: 22,
            commandId: "expected-start"
        )
        controller.refreshState()
        XCTAssertFalse(startButton.isEnabled)
        XCTAssertTrue(endButton.isEnabled)
    }

    func testCorrelatedStateUnlocksBeforeLateCompletion() throws {
        let fixture = try makePresentationFixture(isSlideShow: true, revision: 30)
        let controller = fixture.controller
        let actionButtons: [PresentationActionButton] = descendants(of: controller.view)
        let controls = Dictionary(uniqueKeysWithValues: actionButtons.compactMap { button in
            button.accessibilityIdentifier.map { ($0, button as UIControl) }
        })
        let endButton = try XCTUnwrap((descendants(of: controller.view) as [HoldToConfirmButton]).first)
        let endEvent = actionEvent(
            commandID: "correlated-end",
            actionID: "presentation.end",
            controlID: "presentation-end"
        )

        controller.handleAction(type: "action.accepted", event: endEvent)
        try applyPresentationState(
            isSlideShow: false,
            to: fixture.stateStore,
            revision: 31,
            commandId: "correlated-end"
        )
        controller.refreshState()

        XCTAssertFalse(endButton.isEnabled)
        XCTAssertTrue(try XCTUnwrap(controls["presentation-start-current"]).isEnabled)
        XCTAssertTrue(try XCTUnwrap(controls["presentation-next"]).isEnabled)
        XCTAssertTrue(controller.handleAction(type: "action.completed", event: endEvent))
        XCTAssertFalse(endButton.isEnabled)
        XCTAssertTrue(try XCTUnwrap(controls["presentation-start-current"]).isEnabled)
    }

    func testRootControllerUsesReadableDarkThemeStatusBar() {
        let controller = PanelRootViewController(session: PraxisProtocolSession())
        XCTAssertEqual(controller.preferredStatusBarStyle, .lightContent)
        XCTAssertFalse(controller.prefersStatusBarHidden)
    }

    private func borderAlpha(of view: UIView) -> CGFloat {
        guard let color = view.layer.borderColor else { return 0 }
        return UIColor(cgColor: color).cgColor.alpha
    }

    private func descendants<T>(of view: UIView) -> [T] {
        var values: [T] = view is T ? [view as! T] : []
        for subview in view.subviews {
            values.append(contentsOf: descendants(of: subview))
        }
        return values
    }

    private func makePresentationController(isSlideShow: Bool) throws -> PresentationViewController {
        try makePresentationFixture(isSlideShow: isSlideShow).controller
    }

    private func makePresentationFixture(
        isSlideShow: Bool,
        revision: Int? = nil
    ) throws -> (controller: PresentationViewController, stateStore: PraxisStateStore) {
        let configurationStore = PraxisConfigurationStore()
        let actionIDs = [
            "presentation.open", "presentation.startBeginning", "presentation.startCurrent",
            "presentation.previous", "presentation.next", "presentation.end"
        ]
        let actions: [JSONValue] = actionIDs.map { actionID in
            .object([
                "id": .string(actionID), "name": .string(actionID), "type": .string("test"),
                "parameters": .object([:]), "timeoutSeconds": .number(10)
            ])
        }
        try configurationStore.apply(snapshot: .object([
            "schemaVersion": .number(1), "activeProfileId": .string("default"),
            "profiles": .array([]), "actions": .array(actions)
        ]))

        let stateStore = PraxisStateStore()
        try applyPresentationState(isSlideShow: isSlideShow, to: stateStore, revision: revision)

        let controller = PresentationViewController(configurationStore: configurationStore, stateStore: stateStore)
        controller.loadViewIfNeeded()
        controller.view.frame = CGRect(x: 0, y: 0, width: 1_024, height: 618)
        controller.view.layoutIfNeeded()
        controller.setConnected(true)
        controller.refreshState()
        return (controller, stateStore)
    }

    private func applyPresentationState(
        isSlideShow: Bool,
        to stateStore: PraxisStateStore,
        revision: Int? = nil,
        commandId: String? = nil
    ) throws {
        let presentationStatus = isSlideShow ? "presenting" : "ready"
        var presentationObject: [String: JSONValue] = [
            "status": .string(presentationStatus), "isInstalled": .bool(true),
            "isRunning": .bool(true), "hasPresentation": .bool(true),
            "isSlideShow": .bool(isSlideShow), "presentationName": .string("Demo.pptx"),
            "currentSlide": .number(1), "totalSlides": .number(5), "notes": .string(""),
            "elapsedSeconds": .number(0), "error": .null, "recentPresentations": .array([])
        ]
        presentationObject["revision"] = revision.map { .number(Double($0)) } ?? .null
        presentationObject["commandId"] = commandId.map(JSONValue.string) ?? .null
        let presentationValue: JSONValue = .object(presentationObject)
        try stateStore.apply(snapshot: .array([.object([
            "key": .string("presentation.state"), "value": presentationValue,
            "status": .string(presentationStatus), "label": .string("PowerPoint 演示"),
            "updatedAt": .string("2026-08-11T00:00:00Z")
        ])]))
    }

    private func actionEvent(
        commandID: String = UUID().uuidString,
        actionID: String,
        controlID: String,
        message: String? = nil
    ) -> PraxisActionEvent {
        var payload: [String: JSONValue] = [
            "commandId": .string(commandID), "actionId": .string(actionID),
            "controlId": .string(controlID), "success": .bool(true)
        ]
        payload["message"] = message.map(JSONValue.string) ?? .null
        return PraxisActionEvent(message: PraxisProtocolMessage(type: "action.completed", payload: .object(payload)))
    }
}

private final class PresentationDelegateSpy: PresentationViewControllerDelegate {
    private var commandIDs: [String]

    init(commandIDs: [String]) {
        self.commandIDs = commandIDs
    }

    func presentationViewController(
        _ controller: PresentationViewController,
        invokeAction actionID: String,
        controlID: String,
        itemID: String?
    ) -> String? {
        commandIDs.isEmpty ? nil : commandIDs.removeFirst()
    }

    func presentationViewController(
        _ controller: PresentationViewController,
        showNotice title: String,
        message: String
    ) {}
}

final class PraxisBootstrapCacheTests: XCTestCase {
    func testConfigurationCacheRestoresHashAndBindings() throws {
        let root = temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let snapshot: JSONValue = .object([
            "schemaVersion": .number(1),
            "activeProfileId": .string("default"),
            "configurationHash": .string("config-hash"),
            "profiles": .array([]),
            "actions": .array([.object([
                "id": .string("system.summary"), "name": .string("摘要"),
                "type": .string("noop"), "parameters": .object([:]),
                "timeoutSeconds": .number(5), "requiresConfirmation": .bool(false)
            ])])
        ])

        let first = PraxisConfigurationStore(cacheRoot: root)
        try first.apply(snapshot: snapshot)
        let restored = PraxisConfigurationStore(cacheRoot: root)

        XCTAssertTrue(restored.confirms(hash: "CONFIG-HASH"))
        XCTAssertTrue(restored.hasAction("system.summary"))
    }

    func testIconCacheValidatesHashAndRestoresManifest() throws {
        let root = temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let renderer = UIGraphicsImageRenderer(size: CGSize(width: 8, height: 8))
        let data = try XCTUnwrap(renderer.image { context in
            UIColor(red: 0.0, green: 0.48, blue: 1.0, alpha: 1.0).setFill()
            context.cgContext.fill(CGRect(x: 0, y: 0, width: 8, height: 8))
        }.pngData())
        let hash = PraxisCrypto.sha256(data).map { String(format: "%02x", $0) }.joined()
        let first = PraxisIconStore(cacheRoot: root)

        XCTAssertNil(first.apply(
            actionID: "chatgpt",
            base64: data.base64EncodedString(),
            expectedHash: "incorrect"
        ))
        XCTAssertNotNil(first.apply(
            actionID: "chatgpt",
            base64: data.base64EncodedString(),
            expectedHash: hash
        ))

        let restored = PraxisIconStore(cacheRoot: root)
        XCTAssertTrue(restored.contains(["chatgpt": hash]))
        XCTAssertNotNil(restored.image(for: "chatgpt"))

        let replacement = try XCTUnwrap(renderer.image { context in
            UIColor.red.setFill()
            context.cgContext.fill(CGRect(x: 0, y: 0, width: 8, height: 8))
        }.pngData())
        let iconURL = root.appendingPathComponent("AppIcons/chatgpt.png", isDirectory: false)
        try replacement.write(to: iconURL, options: [.atomic])

        let corrupted = PraxisIconStore(cacheRoot: root)
        XCTAssertFalse(corrupted.contains(["chatgpt": hash]))
        XCTAssertNil(corrupted.image(for: "chatgpt"))
    }

    private func temporaryDirectory() -> URL {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("praxis-bootstrap-tests-\(UUID().uuidString)", isDirectory: true)
        try? FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        return root
    }
}
#endif
