import Foundation

final class PraxisFrameReassembler {
    private let maximumMessageBytes: Int
    private var buffer: Data?
    private var messageID: UInt16 = 0
    private var nextSequence = 0
    private var totalLength = 0

    init(maximumMessageBytes: Int = PraxisFrameEncoder.maximumMessageBytes) {
        self.maximumMessageBytes = maximumMessageBytes
    }

    func accept(_ frame: Data) throws -> Data? {
        do {
            return try acceptValidated(frame)
        } catch {
            reset()
            throw error
        }
    }

    func reset() {
        buffer = nil
        messageID = 0
        nextSequence = 0
        totalLength = 0
    }

    private func acceptValidated(_ frame: Data) throws -> Data? {
        let header = try PraxisFrameHeader.decode(frame)
        guard header.totalLength <= maximumMessageBytes else {
            throw PraxisFrameError.declaredLengthTooLarge(header.totalLength)
        }

        if header.isStart {
            guard header.sequence == 0 else {
                throw PraxisFrameError.invalidStartSequence(header.sequence)
            }
            if buffer != nil && header.messageID == messageID {
                throw PraxisFrameError.duplicateStart(header.messageID)
            }
            reset()
            messageID = header.messageID
            nextSequence = 0
            totalLength = header.totalLength
            buffer = Data()
            buffer?.reserveCapacity(max(1, totalLength))
        }

        guard var current = buffer else {
            throw PraxisFrameError.unexpectedContinuation
        }
        guard header.messageID == messageID,
              Int(header.sequence) == nextSequence,
              header.totalLength == totalLength else {
            throw PraxisFrameError.inconsistentFrame
        }

        let payload = frame.dropFirst(PraxisFrameHeader.byteCount)
        if totalLength > 0 && payload.isEmpty {
            throw PraxisFrameError.emptyPayload
        }
        guard current.count + payload.count <= totalLength else {
            throw PraxisFrameError.payloadExceedsDeclaredLength
        }

        current.append(contentsOf: payload)
        buffer = current
        nextSequence += 1

        if header.isEnd {
            guard current.count == totalLength else {
                throw PraxisFrameError.incompleteFinalFrame(expected: totalLength, actual: current.count)
            }
            reset()
            return current
        }

        if current.count == totalLength {
            throw PraxisFrameError.missingEndFlag
        }
        return nil
    }
}
