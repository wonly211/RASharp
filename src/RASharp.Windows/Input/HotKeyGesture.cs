using System.Globalization;
using System.Runtime.InteropServices;

namespace RASharp.Windows.Input;

[Flags]
public enum HotKeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
    NoRepeat = 0x4000,
}

public sealed partial record HotKeyGesture(HotKeyModifiers Modifiers, uint VirtualKey)
{
    private static readonly Dictionary<string, uint> NamedKeys =
        new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["Backspace"] = 0x08,
            ["Tab"] = 0x09,
            ["Enter"] = 0x0D,
            ["Return"] = 0x0D,
            ["Escape"] = 0x1B,
            ["Esc"] = 0x1B,
            ["Space"] = 0x20,
            ["PageUp"] = 0x21,
            ["PgUp"] = 0x21,
            ["PageDown"] = 0x22,
            ["PgDn"] = 0x22,
            ["End"] = 0x23,
            ["Home"] = 0x24,
            ["Left"] = 0x25,
            ["Up"] = 0x26,
            ["Right"] = 0x27,
            ["Down"] = 0x28,
            ["Insert"] = 0x2D,
            ["Delete"] = 0x2E,
            ["CapsLock"] = 0x14,
            ["Oem3"] = 0xC0,
            ["`"] = 0xC0,
        };

    public static bool TryParse(string? text, out HotKeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text) || text.Contains('&', StringComparison.Ordinal))
        {
            return false;
        }

        var value = text.Trim().Replace("``", "`", StringComparison.Ordinal);
        var modifiers = HotKeyModifiers.NoRepeat;
        var position = 0;
        while (position < value.Length)
        {
            if (value[position] is '<' or '>')
            {
                position++;
                continue;
            }

            var modifier = value[position] switch
            {
                '^' => HotKeyModifiers.Control,
                '!' => HotKeyModifiers.Alt,
                '+' => HotKeyModifiers.Shift,
                '#' => HotKeyModifiers.Windows,
                '~' or '*' or '$' => HotKeyModifiers.None,
                _ => (HotKeyModifiers?)null,
            };
            if (modifier is null)
            {
                break;
            }

            modifiers |= modifier.Value;
            position++;
        }

        var keyText = value[position..].Trim();
        if (NamedKeys.TryGetValue(keyText, out var namedKey))
        {
            gesture = new HotKeyGesture(modifiers, namedKey);
            return true;
        }

        if (keyText.Length == 1)
        {
            var scan = NativeMethods.VkKeyScan(keyText[0]);
            if (scan == -1)
            {
                return false;
            }

            if ((scan & 0x0100) != 0)
            {
                modifiers |= HotKeyModifiers.Shift;
            }

            gesture = new HotKeyGesture(modifiers, (uint)(scan & 0xFF));
            return true;
        }

        if (keyText.StartsWith('F')
            && int.TryParse(keyText.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var function)
            && function is >= 1 and <= 24)
        {
            gesture = new HotKeyGesture(modifiers, (uint)(0x70 + function - 1));
            return true;
        }

        return false;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll", EntryPoint = "VkKeyScanW")]
        internal static partial short VkKeyScan(ushort character);
    }
}
