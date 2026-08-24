import Foundation

struct PraxisFrameHeader: Equatable {
    static let byteCount = 12
    static let version: UInt8 = 1
    static let startFlag: UInt8 = 1 << 0
    static let endFlag: UInt8 = 1 << 1

    let flags: UInt8
    let messageID: UInt16
    let sequence: UInt16
    let totalLength: Int

    var isStart: Bool { flags & Self.startFlag != 0 }
    var isEnd: Bool { flags & Self.endFlag != 0 }

    static func decode(_ frame: Data) throws -> PraxisFrameHeader {
        guard frame.count >= byteCount else {
            throw PraxisFrameError.headerTooShort(frame.count)
        }

        let bytes = [UInt8](frame.prefix(byteCount))
        guard bytes[0] == Character("P").asciiValue,
              bytes[1] == Character("X").asciiValue else {
            throw PraxisFrameError.invalidMagic
        }
        guard bytes[2] == version else {
            throw PraxisFrameError.unsupportedVersion(bytes[2])
        }
        guard bytes[3] & ~(startFlag | endFlag) == 0 else {
            throw PraxisFrameError.invalidFlags(bytes[3])
        }

        let messageID = UInt16(bytes[4]) | (UInt16(bytes[5]) << 8)
        let sequence = UInt16(bytes[6]) | (UInt16(bytes[7]) << 8)
        let total = UInt32(bytes[8])
            | (UInt32(bytes[9]) << 8)
            | (UInt32(bytes[10]) << 16)
            | (UInt32(bytes[11]) << 24)
        return PraxisFrameHeader(
            flags: bytes[3],
            messageID: messageID,
            sequence: sequence,
            totalLength: Int(total)
        )
    }
}

enum PraxisFrameEncoder {
    static let maximumMessageBytes = 65_536

    static func encode(
        message: Data,
        maximumFrameBytes: Int,
        messageID: UInt16
    ) throws -> [Data] {
        guard maximumFrameBytes > PraxisFrameHeader.byteCount else {
            throw PraxisFrameError.frameSizeTooSmall(maximumFrameBytes)
        }
        guard message.count <= maximumMessageBytes else {
            throw PraxisFrameError.messageTooLarge(message.count)
        }

        let maximumPayloadBytes = maximumFrameBytes - PraxisFrameHeader.byteCount
        let frameCount = max(1, (message.count + maximumPayloadBytes - 1) / maximumPayloadBytes)
        guard frameCount <= Int(UInt16.max) + 1 else {
            throw PraxisFrameError.tooManyFrames(frameCount)
        }

        var frames: [Data] = []
        frames.reserveCapacity(frameCount)
        var offset = 0

        for sequence in 0..<frameCount {
            let payloadCount = min(maximumPayloadBytes, message.count - offset)
            var bytes: [UInt8] = []
            bytes.reserveCapacity(PraxisFrameHeader.byteCount + payloadCount)
            bytes.append(Character("P").asciiValue!)
            bytes.append(Character("X").asciiValue!)
            bytes.append(PraxisFrameHeader.version)

            var flags: UInt8 = 0
            if sequence == 0 { flags |= PraxisFrameHeader.startFlag }
            if sequence == frameCount - 1 { flags |= PraxisFrameHeader.endFlag }
            bytes.append(flags)
            bytes.append(UInt8(truncatingIfNeeded: messageID))
            bytes.append(UInt8(truncatingIfNeeded: messageID >> 8))

            let sequenceValue = UInt16(sequence)
            bytes.append(UInt8(truncatingIfNeeded: sequenceValue))
            bytes.append(UInt8(truncatingIfNeeded: sequenceValue >> 8))

            let totalLength = UInt32(message.count)
            bytes.append(UInt8(truncatingIfNeeded: totalLength))
            bytes.append(UInt8(truncatingIfNeeded: totalLength >> 8))
            bytes.append(UInt8(truncatingIfNeeded: totalLength >> 16))
            bytes.append(UInt8(truncatingIfNeeded: totalLength >> 24))

            if payloadCount > 0 {
                bytes.append(contentsOf: message[offset..<(offset + payloadCount)])
                offset += payloadCount
            }
            frames.append(Data(bytes))
        }

        return frames
    }
}
