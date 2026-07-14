using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace RASharp.Windows.Input;

internal sealed partial class ExclusiveHotKeyService : IDisposable
{
    private const int LowLevelKeyboardHook = 13;
    private const int KeyDownMessage = 0x0100;
    private const int KeyUpMessage = 0x0101;
    private const int SystemKeyDownMessage = 0x0104;
    private const int SystemKeyUpMessage = 0x0105;
    private const uint InjectedFlag = 0x10;
    private const int VirtualKeyShift = 0x10;
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyMenu = 0x12;
    private const int VirtualKeyLeftWindows = 0x5B;
    private const int VirtualKeyRightWindows = 0x5C;
    private const byte VirtualKeyMenuMask = 0x07;
    private const uint KeyEventKeyUp = 0x0002;

    private readonly Dispatcher dispatcher;
    private readonly HookProcedure hookProcedure;
    private readonly Dictionary<HotKeyGesture, Func<Task>> actions = [];
    private readonly Dictionary<uint, HotKeyGesture> activeKeys = [];
    private readonly nint hook;
    private bool disposed;

    public ExclusiveHotKeyService(Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
        hookProcedure = ProcessKeyboardEvent;
        hook = NativeMethods.SetWindowsHookEx(
            LowLevelKeyboardHook,
            hookProcedure,
            NativeMethods.GetModuleHandle(null),
            0);
        if (hook == 0)
        {
            throw new InvalidOperationException("无法安装抢占式全局热键钩子。");
        }
    }

    public bool Register(HotKeyGesture gesture, Func<Task> action)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var normalized = gesture with { Modifiers = gesture.Modifiers & ~HotKeyModifiers.NoRepeat };
        return actions.TryAdd(normalized, action);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        _ = NativeMethods.UnhookWindowsHookEx(hook);
        actions.Clear();
        activeKeys.Clear();
        disposed = true;
    }

    private nint ProcessKeyboardEvent(int code, nint message, nint data)
    {
        if (code < 0 || disposed)
        {
            return NativeMethods.CallNextHookEx(hook, code, message, data);
        }

        var keyboardEvent = Marshal.PtrToStructure<LowLevelKeyboardEvent>(data);
        if ((keyboardEvent.Flags & InjectedFlag) != 0)
        {
            return NativeMethods.CallNextHookEx(hook, code, message, data);
        }

        var messageValue = message.ToInt32();
        if (messageValue is KeyUpMessage or SystemKeyUpMessage)
        {
            if (activeKeys.Remove(keyboardEvent.VirtualKey, out _))
            {
                return 1;
            }

            return NativeMethods.CallNextHookEx(hook, code, message, data);
        }

        if (messageValue is not (KeyDownMessage or SystemKeyDownMessage))
        {
            return NativeMethods.CallNextHookEx(hook, code, message, data);
        }

        if (activeKeys.ContainsKey(keyboardEvent.VirtualKey))
        {
            return 1;
        }

        var gesture = new HotKeyGesture(GetCurrentModifiers(), keyboardEvent.VirtualKey);
        if (!actions.TryGetValue(gesture, out var action))
        {
            return NativeMethods.CallNextHookEx(hook, code, message, data);
        }

        activeKeys[keyboardEvent.VirtualKey] = gesture;
        SendMenuMaskKey();
        _ = dispatcher.InvokeAsync(action).Task.Unwrap();
        return 1;
    }

    private static void SendMenuMaskKey()
    {
        NativeMethods.KeybdEvent(VirtualKeyMenuMask, 0, 0, 0);
        NativeMethods.KeybdEvent(VirtualKeyMenuMask, 0, KeyEventKeyUp, 0);
    }

    private static HotKeyModifiers GetCurrentModifiers()
    {
        var modifiers = HotKeyModifiers.None;
        if (IsKeyDown(VirtualKeyShift))
        {
            modifiers |= HotKeyModifiers.Shift;
        }

        if (IsKeyDown(VirtualKeyControl))
        {
            modifiers |= HotKeyModifiers.Control;
        }

        if (IsKeyDown(VirtualKeyMenu))
        {
            modifiers |= HotKeyModifiers.Alt;
        }

        if (IsKeyDown(VirtualKeyLeftWindows) || IsKeyDown(VirtualKeyRightWindows))
        {
            modifiers |= HotKeyModifiers.Windows;
        }

        return modifiers;
    }

    private static bool IsKeyDown(int virtualKey) => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct LowLevelKeyboardEvent(
        uint VirtualKey,
        uint ScanCode,
        uint Flags,
        uint Time,
        nuint ExtraInformation);

    private delegate nint HookProcedure(int code, nint message, nint data);

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
        internal static partial nint SetWindowsHookEx(
            int hookType,
            HookProcedure hookProcedure,
            nint module,
            uint threadId);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool UnhookWindowsHookEx(nint hook);

        [LibraryImport("user32.dll")]
        internal static partial nint CallNextHookEx(nint hook, int code, nint message, nint data);

        [LibraryImport("user32.dll")]
        internal static partial short GetAsyncKeyState(int virtualKey);

        [LibraryImport("user32.dll", EntryPoint = "keybd_event")]
        internal static partial void KeybdEvent(byte virtualKey, byte scanCode, uint flags, nuint extraInformation);

        [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial nint GetModuleHandle(string? moduleName);
    }
}
