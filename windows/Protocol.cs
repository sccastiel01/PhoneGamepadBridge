using System.Buffers.Binary;

namespace PhoneGamepadBridge;

/// <summary>Bit positions in the wire protocol's button mask. See PROTOCOL.md.</summary>
[Flags]
internal enum PadButtons : ushort
{
    None = 0,
    A = 1 << 0,
    B = 1 << 1,
    X = 1 << 2,
    Y = 1 << 3,
    LeftShoulder = 1 << 4,
    RightShoulder = 1 << 5,
    Back = 1 << 6,
    Start = 1 << 7,
    LeftThumb = 1 << 8,
    RightThumb = 1 << 9,
    DpadUp = 1 << 10,
    DpadDown = 1 << 11,
    DpadLeft = 1 << 12,
    DpadRight = 1 << 13,
    Guide = 1 << 14,
}

internal readonly record struct GamepadFrame(
    uint Seq,
    bool ControllerAttached,
    PadButtons Buttons,
    short LeftX,
    short LeftY,
    short RightX,
    short RightY,
    byte LeftTrigger,
    byte RightTrigger)
{
    public const int Size = 20;
    public const byte Magic0 = 0x47; // 'G'
    public const byte Magic1 = 0x50; // 'P'
    public const byte Version = 1;

    /// <summary>
    /// Parses one frame from a 20-byte span. The caller is responsible for
    /// having already aligned <paramref name="src"/> on the magic pair.
    /// </summary>
    public static GamepadFrame Parse(ReadOnlySpan<byte> src) => new(
        Seq: BinaryPrimitives.ReadUInt32LittleEndian(src[4..]),
        ControllerAttached: (src[3] & 0x01) != 0,
        Buttons: (PadButtons)BinaryPrimitives.ReadUInt16LittleEndian(src[8..]),
        LeftX: BinaryPrimitives.ReadInt16LittleEndian(src[10..]),
        LeftY: BinaryPrimitives.ReadInt16LittleEndian(src[12..]),
        RightX: BinaryPrimitives.ReadInt16LittleEndian(src[14..]),
        RightY: BinaryPrimitives.ReadInt16LittleEndian(src[16..]),
        LeftTrigger: src[18],
        RightTrigger: src[19]);
}

/// <summary>
/// Accumulates bytes off the socket and yields whole frames, resynchronising on
/// the magic pair if the stream ever gets out of step.
/// </summary>
internal sealed class FrameReader
{
    private readonly byte[] _buffer = new byte[GamepadFrame.Size * 64];
    private int _length;

    public int Capacity => _buffer.Length - _length;

    /// <summary>Memory, not Span — async reads cannot take a Span.</summary>
    public Memory<byte> FreeMemory => _buffer.AsMemory(_length);

    public void Commit(int bytesRead) => _length += bytesRead;

    /// <summary>
    /// Pulls the next complete, magic-aligned frame out of the buffer.
    /// Returns false when more data is needed.
    /// </summary>
    public bool TryRead(out GamepadFrame frame)
    {
        frame = default;

        while (true)
        {
            // Discard leading bytes until the buffer starts on a magic pair. A
            // lone trailing Magic0 is kept — the pair may straddle two reads.
            var start = 0;
            while (start + 1 < _length &&
                   !(_buffer[start] == GamepadFrame.Magic0 && _buffer[start + 1] == GamepadFrame.Magic1))
            {
                start++;
            }

            if (start > 0) Discard(start);
            if (_length < GamepadFrame.Size) return false;

            var candidate = _buffer.AsSpan(0, GamepadFrame.Size);
            if (candidate[2] != GamepadFrame.Version)
            {
                // Unknown version, or a false-positive magic hit inside garbage.
                // Drop a byte and rescan rather than trusting the length.
                Discard(1);
                continue;
            }

            frame = GamepadFrame.Parse(candidate);
            Discard(GamepadFrame.Size);
            return true;
        }
    }

    public void Reset() => _length = 0;

    private void Discard(int count)
    {
        var remaining = _length - count;
        if (remaining > 0)
            _buffer.AsSpan(count, remaining).CopyTo(_buffer);
        _length = remaining;
    }
}
