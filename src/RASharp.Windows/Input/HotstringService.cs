using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using RASharp.Core.Menus;

namespace RASharp.Windows.Input;

public sealed partial class HotstringService : IDisposable
{
    private const int LowLevelKeyboardHook = 13;
    private const int KeyDownMessage = 0x0100;
    private const int SystemKeyDownMessage = 0x0104;
    private const uint InjectedFlag = 0x10;
    private readonly Dispatcher dispatcher;
    private readonly HookProcedure hookProcedure;
    private readonly List<Registration> registrations = [];
    private readonly StringBuilder buffer = new();
    private nint hook;
    private bool disposed;
    private bool activationPending;

    public HotstringService(Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
        hookProcedure = KeyboardHook;
        hook = NativeMethods.SetWindowsHookEx(LowLevelKeyboardHook, hookProcedure, 0, 0);
        if (hook == 0)
        {
            throw new InvalidOperationException("Unable to install the global keyboard hook.");
        }
    }

    public void Register(HotstringSpec hotstring, Func<Task> action, bool preserveEndingCharacter)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(hotstring);
        ArgumentNullException.ThrowIfNull(action);
        registrations.Add(new Registration(hotstring, action, preserveEndingCharacter));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        if (hook != 0)
        {
            _ = NativeMethods.UnhookWindowsHookEx(hook);
            hook = 0;
        }

        disposed = true;
    }

    private nint KeyboardHook(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && !activationPending && wParam.ToInt32() is KeyDownMessage or SystemKeyDownMessage)
        {
            var data = Marshal.PtrToStructure<LowLevelKeyboardData>(lParam);
            if ((data.Flags & InjectedFlag) == 0)
            {
                ProcessKey(data.VirtualKey, data.ScanCode);
            }
        }

        return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
    }

    private void ProcessKey(uint virtualKey, uint scanCode)
    {
        if (virtualKey == 0x08)
        {
            if (buffer.Length > 0)
            {
                buffer.Length--;
            }

            return;
        }

        if (virtualKey is 0x1B or 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28
            or 0x2D or 0x2E)
        {
            buffer.Clear();
            return;
        }

        var character = TranslateKey(virtualKey, scanCode);
        if (character is null)
        {
            return;
        }

        var endingCharacter = character is ' ' or '\t' or '\r' or '\n' ? character.Value.ToString() : null;
        if (endingCharacter is not null)
        {
            var registration = registrations.FirstOrDefault(item =>
                !item.Spec.TriggerImmediately
                && buffer.ToString().EndsWith(item.Spec.Trigger, StringComparison.OrdinalIgnoreCase));
            if (registration is not null)
            {
                Activate(registration, registration.Spec.Trigger.Length + 1, endingCharacter);
                return;
            }

            buffer.Clear();
            return;
        }

        buffer.Append(character.Value);
        if (buffer.Length > 128)
        {
            buffer.Remove(0, buffer.Length - 128);
        }

        var immediate = registrations.FirstOrDefault(item =>
            item.Spec.TriggerImmediately
            && buffer.ToString().EndsWith(item.Spec.Trigger, StringComparison.OrdinalIgnoreCase));
        if (immediate is not null)
        {
            Activate(immediate, immediate.Spec.Trigger.Length, null);
        }
    }

    private void Activate(Registration registration, int eraseCount, string? endingCharacter)
    {
        activationPending = true;
        buffer.Clear();
        _ = dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await Task.Delay(15).ConfigureAwait(true);
                KeyboardInput.SendBackspaces(eraseCount);
                await registration.Action().ConfigureAwait(true);
                if (registration.PreserveEndingCharacter && endingCharacter is not null)
                {
                    KeyboardInput.SendUnicodeText(endingCharacter);
                }
            }
            finally
            {
                activationPending = false;
            }
        }).Task.Unwrap();
    }

    private static unsafe char? TranslateKey(uint virtualKey, uint scanCode)
    {
        var keyboardState = new byte[256];
        if (!NativeMethods.GetKeyboardState(keyboardState))
        {
            return null;
        }

        Span<char> text = stackalloc char[8];
        fixed (char* textPointer = text)
        {
            var result = NativeMethods.ToUnicodeEx(
                virtualKey,
                scanCode,
                keyboardState,
                textPointer,
                text.Length,
                0,
                NativeMethods.GetKeyboardLayout(0));
            return result == 1 ? text[0] : null;
        }
    }

    private sealed record Registration(
        HotstringSpec Spec,
        Func<Task> Action,
        bool PreserveEndingCharacter);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint HookProcedure(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelKeyboardData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInformation;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
        internal static partial nint SetWindowsHookEx(
            int hookId,
            HookProcedure procedure,
            nint module,
            uint threadId);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool UnhookWindowsHookEx(nint hook);

        [LibraryImport("user32.dll")]
        internal static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetKeyboardState([Out] byte[] keyboardState);

        [LibraryImport("user32.dll", EntryPoint = "ToUnicodeEx")]
        internal static unsafe partial int ToUnicodeEx(
            uint virtualKey,
            uint scanCode,
            byte[] keyboardState,
            char* buffer,
            int bufferLength,
            uint flags,
            nint keyboardLayout);

        [LibraryImport("user32.dll")]
        internal static partial nint GetKeyboardLayout(uint threadId);
    }
}
