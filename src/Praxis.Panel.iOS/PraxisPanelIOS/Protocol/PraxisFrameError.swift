import Foundation

enum PraxisFrameError: Error, Equatable, LocalizedError {
    case frameSizeTooSmall(Int)
    case messageTooLarge(Int)
    case tooManyFrames(Int)
    case headerTooShort(Int)
    case invalidMagic
    case unsupportedVersion(UInt8)
    case invalidFlags(UInt8)
    case declaredLengthTooLarge(Int)
    case invalidStartSequence(UInt16)
    case unexpectedContinuation
    case duplicateStart(UInt16)
    case inconsistentFrame
    case emptyPayload
    case payloadExceedsDeclaredLength
    case incompleteFinalFrame(expected: Int, actual: Int)
    case missingEndFlag

    var errorDescription: String? {
        switch self {
        case .frameSizeTooSmall(let size):
            return "BLE frame size \(size) must exceed the 12-byte header."
        case .messageTooLarge(let size):
            return "Praxis Link message size \(size) exceeds 65,536 bytes."
        case .tooManyFrames(let count):
            return "Praxis Link message requires too many frames: \(count)."
        case .headerTooShort(let size):
            return "BLE frame size \(size) is shorter than its header."
        case .invalidMagic:
            return "BLE frame does not begin with PX."
        case .unsupportedVersion(let version):
            return "Unsupported BLE frame version \(version)."
        case .invalidFlags(let flags):
            return "BLE frame contains unsupported flags \(flags)."
        case .declaredLengthTooLarge(let size):
            return "BLE frame declares an invalid message length \(size)."
        case .invalidStartSequence(let sequence):
            return "Starting BLE frame has sequence \(sequence), not zero."
        case .unexpectedContinuation:
            return "BLE continuation arrived while no message was being assembled."
        case .duplicateStart(let messageID):
            return "BLE message \(messageID) repeated its start frame."
        case .inconsistentFrame:
            return "BLE frame order, message ID, or declared length is inconsistent."
        case .emptyPayload:
            return "A non-empty BLE message contains an empty frame payload."
        case .payloadExceedsDeclaredLength:
            return "BLE frame payload exceeds the declared message length."
        case .incompleteFinalFrame(let expected, let actual):
            return "Final BLE frame completed \(actual) of \(expected) declared bytes."
        case .missingEndFlag:
            return "BLE message reached its declared length without an end flag."
        }
    }
}
