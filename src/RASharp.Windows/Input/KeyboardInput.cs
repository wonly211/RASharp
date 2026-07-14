using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace RASharp.Windows.Input;

public static partial class KeyboardInput
{
    private const uint KeyboardInputType = 1;
    private const uint KeyUp = 0x0002;
    private const uint Unicode = 0x0004;
    private const ushort Backspace = 0x08;

    public static void SendCopy() => SendChord(new HotKeyGesture(HotKeyModifiers.Control, 0x43));

    public static void SendBackspaces(int count)
    {
        if (count <= 0)
        {
            return;
        }

        var inputs = new List<NativeInput>(count * 2);
        for (var index = 0; index < count; index++)
        {
            inputs.Add(CreateVirtualKeyInput(Backspace, keyUp: false));
            inputs.Add(CreateVirtualKeyInput(Backspace, keyUp: true));
        }

        Send(inputs);
    }

    public static void SendUnicodeText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var inputs = new List<NativeInput>(text.Length * 2);
        foreach (var character in text)
        {
            inputs.Add(CreateUnicodeInput(character, keyUp: false));
            inputs.Add(CreateUnicodeInput(character, keyUp: true));
        }

        Send(inputs);
    }

    public static void SendAhkSequence(string sequence)
    {
        if (string.IsNullOrEmpty(sequence))
        {
            return;
        }

        if (HotKeyGesture.TryParse(sequence, out var gesture) && gesture is not null)
        {
            SendChord(gesture);
            return;
        }

        var position = 0;
        foreach (Match match in BracedKeyRegex().Matches(sequence))
        {
            if (match.Index > position)
            {
                SendUnicodeText(sequence[position..match.Index]);
            }

            var keyName = match.Groups["key"].Value;
            if (HotKeyGesture.TryParse(keyName, out gesture) && gesture is not null)
            {
                SendChord(gesture);
            }

            position = match.Index + match.Length;
        }

        if (position < sequence.Length)
        {
            SendUnicodeText(sequence[position..]);
        }
    }

    public static void SendChord(HotKeyGesture gesture)
    {
        var modifiers = GetModifierKeys(gesture.Modifiers).ToArray();
        var inputs = new List<NativeInput>((modifiers.Length * 2) + 2);
        inputs.AddRange(modifiers.Select(key => CreateVirtualKeyInput(key, keyUp: false)));
        inputs.Add(CreateVirtualKeyInput((ushort)gesture.VirtualKey, keyUp: false));
        inputs.Add(CreateVirtualKeyInput((ushort)gesture.VirtualKey, keyUp: true));
        inputs.AddRange(modifiers.Reverse().Select(key => CreateVirtualKeyInput(key, keyUp: true)));
        Send(inputs);
    }

    private static IEnumerable<ushort> GetModifierKeys(HotKeyModifiers modifiers)
    {
        if (modifiers.HasFlag(HotKeyModifiers.Control))
        {
            yield return 0x11;
        }

        if (modifiers.HasFlag(HotKeyModifiers.Shift))
        {
            yield return 0x10;
        }

        if (modifiers.HasFlag(HotKeyModifiers.Alt))
        {
            yield return 0x12;
        }

        if (modifiers.HasFlag(HotKeyModifiers.Windows))
        {
            yield return 0x5B;
        }
    }

    private static NativeInput CreateVirtualKeyInput(ushort virtualKey, bool keyUp) => new()
    {
        Type = KeyboardInputType,
        Keyboard = new KeyboardInputData
        {
            VirtualKey = virtualKey,
            Flags = keyUp ? KeyUp : 0,
        },
    };

    private static NativeInput CreateUnicodeInput(char character, bool keyUp) => new()
    {
        Type = KeyboardInputType,
        Keyboard = new KeyboardInputData
        {
            ScanCode = character,
            Flags = Unicode | (keyUp ? KeyUp : 0),
        },
    };

    private static void Send(IReadOnlyCollection<NativeInput> inputs)
    {
        var array = inputs.ToArray();
        var sent = NativeMethods.SendInput((uint)array.Length, array, Marshal.SizeOf<NativeInput>());
        if (sent != array.Length)
        {
            throw new InvalidOperationException($"SendInput only accepted {sent} of {array.Length} input events.");
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct NativeInput
    {
        [FieldOffset(0)]
        public uint Type;

        [FieldOffset(8)]
        public KeyboardInputData Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll", SetLastError = true)]
        internal static partial uint SendInput(uint inputCount, [In] NativeInput[] inputs, int inputSize);
    }

    [GeneratedRegex("\\{(?<key>[^{}]+)\\}", RegexOptions.CultureInvariant)]
    private static partial Regex BracedKeyRegex();
}
