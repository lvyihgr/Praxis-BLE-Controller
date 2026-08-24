using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Praxis.Core.Actions;

public sealed class HotkeySender
{
    private const uint InputKeyboard = 1;
    private const uint ExtendedKey = 0x0001;
    private const uint KeyUp = 0x0002;
    private const ushort RightAlt = 0x00A5;

    public int NativeInputSize => Marshal.SizeOf<Input>();

    public IReadOnlyList<ushort> Parse(string combination)
    {
        if (string.IsNullOrWhiteSpace(combination))
            throw new InvalidOperationException("快捷键不能为空。");

        var keys = combination.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseKey)
            .ToArray();
        if (keys.Length == 0)
            throw new InvalidOperationException("快捷键不能为空。");
        return keys;
    }

    public void Send(string combination)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("快捷键动作只能在 Windows 上执行。");

        var keys = Parse(combination);
        var inputs = new List<Input>(keys.Count * 2);
        inputs.AddRange(keys.Select(key => CreateInput(key, keyUp: false)));
        inputs.AddRange(keys.Reverse().Select(key => CreateInput(key, keyUp: true)));
        var sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>());
        if (sent != inputs.Count)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 未能发送完整的快捷键。");
    }

    public void SendVirtualKey(ushort key)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("媒体动作只能在 Windows 上执行。");
        var inputs = new[] { CreateInput(key, false), CreateInput(key, true) };
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 未能发送媒体按键。");
    }

    private static ushort ParseKey(string raw)
    {
        var key = raw.Trim();
        if (key.Length == 1)
        {
            var character = char.ToUpperInvariant(key[0]);
            if (character is >= 'A' and <= 'Z' or >= '0' and <= '9')
                return character;
        }

        if (key.Length is 2 or 3 && key.StartsWith('F') && int.TryParse(key[1..], out var function) && function is >= 1 and <= 24)
            return (ushort)(0x70 + function - 1);

        return key.ToUpperInvariant() switch
        {
            "CTRL" or "CONTROL" => 0x11,
            "SHIFT" => 0x10,
            "ALT" => 0x12,
            "RIGHTALT" or "RALT" => RightAlt,
            "WIN" or "WINDOWS" => 0x5B,
            "ENTER" or "RETURN" => 0x0D,
            "ESC" or "ESCAPE" => 0x1B,
            "SPACE" => 0x20,
            "TAB" => 0x09,
            "BACKSPACE" => 0x08,
            "DELETE" or "DEL" => 0x2E,
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            _ => throw new InvalidOperationException($"不支持的按键 '{raw}'。")
        };
    }

    private static Input CreateInput(ushort key, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = key,
                Flags = (IsExtendedKey(key) ? ExtendedKey : 0) | (keyUp ? KeyUp : 0)
            }
        }
    };

    private static bool IsExtendedKey(ushort key) => key is
        RightAlt or 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }
}
