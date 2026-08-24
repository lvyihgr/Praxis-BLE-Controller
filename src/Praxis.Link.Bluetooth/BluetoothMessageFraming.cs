using System.Buffers.Binary;

namespace Praxis.Link.Bluetooth;

[Flags]
internal enum BluetoothFrameFlags : byte
{
    None = 0,
    Start = 1,
    End = 2
}

public static class BluetoothMessageFraming
{
    public const int HeaderBytes = 12;
    private const byte Magic0 = (byte)'P';
    private const byte Magic1 = (byte)'X';
    private const byte FrameVersion = 1;

    public static IReadOnlyList<byte[]> Encode(
        ReadOnlySpan<byte> message,
        int maximumFrameBytes,
        ushort messageId)
    {
        if (maximumFrameBytes <= HeaderBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumFrameBytes),
                $"A Bluetooth frame must exceed the {HeaderBytes}-byte header.");
        }

        if (message.Length > BluetoothProtocol.MaximumMessageBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(message),
                $"A Praxis Link message cannot exceed {BluetoothProtocol.MaximumMessageBytes} bytes.");
        }

        var maximumPayloadBytes = maximumFrameBytes - HeaderBytes;
        var frameCount = Math.Max(1, (message.Length + maximumPayloadBytes - 1) / maximumPayloadBytes);
        var frames = new List<byte[]>(frameCount);
        var offset = 0;

        for (var sequence = 0; sequence < frameCount; sequence++)
        {
            var payloadLength = Math.Min(maximumPayloadBytes, message.Length - offset);
            var frame = new byte[HeaderBytes + payloadLength];
            frame[0] = Magic0;
            frame[1] = Magic1;
            frame[2] = FrameVersion;

            var flags = BluetoothFrameFlags.None;
            if (sequence == 0)
            {
                flags |= BluetoothFrameFlags.Start;
            }

            if (sequence == frameCount - 1)
            {
                flags |= BluetoothFrameFlags.End;
            }

            frame[3] = (byte)flags;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4, 2), messageId);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6, 2), checked((ushort)sequence));
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8, 4), checked((uint)message.Length));
            message.Slice(offset, payloadLength).CopyTo(frame.AsSpan(HeaderBytes));
            frames.Add(frame);
            offset += payloadLength;
        }

        return frames;
    }

    internal static BluetoothFrameHeader DecodeHeader(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < HeaderBytes)
        {
            throw new InvalidDataException("Bluetooth frame is shorter than its header.");
        }

        if (frame[0] != Magic0 || frame[1] != Magic1)
        {
            throw new InvalidDataException("Bluetooth frame has an invalid Praxis magic value.");
        }

        if (frame[2] != FrameVersion)
        {
            throw new InvalidDataException($"Unsupported Bluetooth frame version: {frame[2]}.");
        }

        return new BluetoothFrameHeader(
            (BluetoothFrameFlags)frame[3],
            BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(4, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6, 2)),
            checked((int)BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(8, 4))));
    }

    internal sealed record BluetoothFrameHeader(
        BluetoothFrameFlags Flags,
        ushort MessageId,
        ushort Sequence,
        int TotalLength);
}

public sealed class BluetoothMessageReassembler(int maximumMessageBytes = BluetoothProtocol.MaximumMessageBytes)
    : IDisposable
{
    private MemoryStream? _buffer;
    private ushort _messageId;
    private ushort _nextSequence;
    private int _totalLength;

    public byte[]? Accept(ReadOnlySpan<byte> frame)
    {
        try
        {
            var header = BluetoothMessageFraming.DecodeHeader(frame);
            var isStart = header.Flags.HasFlag(BluetoothFrameFlags.Start);
            var isEnd = header.Flags.HasFlag(BluetoothFrameFlags.End);

            if (header.TotalLength < 0 || header.TotalLength > maximumMessageBytes)
            {
                throw new InvalidDataException($"Bluetooth message length {header.TotalLength} is outside the allowed range.");
            }

            if (isStart)
            {
                if (header.Sequence != 0)
                {
                    throw new InvalidDataException("A starting Bluetooth frame must have sequence zero.");
                }

                Reset();
                _messageId = header.MessageId;
                _nextSequence = 0;
                _totalLength = header.TotalLength;
                _buffer = new MemoryStream(Math.Max(1, header.TotalLength));
            }

            if (_buffer is null)
            {
                throw new InvalidDataException("Bluetooth continuation frame arrived without a starting frame.");
            }

            if (header.MessageId != _messageId || header.Sequence != _nextSequence || header.TotalLength != _totalLength)
            {
                throw new InvalidDataException("Bluetooth frame order or message identity is invalid.");
            }

            var payload = frame[BluetoothMessageFraming.HeaderBytes..];
            if (_buffer.Length + payload.Length > _totalLength)
            {
                throw new InvalidDataException("Bluetooth frame payload exceeds the declared message length.");
            }

            _buffer.Write(payload);
            _nextSequence++;

            if (!isEnd)
            {
                return null;
            }

            if (_buffer.Length != _totalLength)
            {
                throw new InvalidDataException("Final Bluetooth frame does not complete the declared message length.");
            }

            var completed = _buffer.ToArray();
            Reset();
            return completed;
        }
        catch
        {
            Reset();
            throw;
        }
    }

    public void Reset()
    {
        _buffer?.Dispose();
        _buffer = null;
        _messageId = 0;
        _nextSequence = 0;
        _totalLength = 0;
    }

    public void Dispose() => Reset();
}
