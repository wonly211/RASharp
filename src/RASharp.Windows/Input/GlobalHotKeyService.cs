using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace RASharp.Windows.Input;

public sealed partial class GlobalHotKeyService : IDisposable
{
    private const int HotKeyMessage = 0x0312;
    private static readonly nint MessageOnlyWindow = new(-3);
    private readonly HwndSource window;
    private readonly Dictionary<int, Func<Task>> actions = [];
    private ExclusiveHotKeyService? exclusiveHotKeys;
    private int nextId = 1000;
    private bool disposed;

    public int ExclusiveRegistrationCount { get; private set; }

    public GlobalHotKeyService()
    {
        var parameters = new HwndSourceParameters("RASharp.GlobalHotKeys")
        {
            ParentWindow = MessageOnlyWindow,
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        };
        window = new HwndSource(parameters);
        window.AddHook(WindowProcedure);
    }

    public bool Register(string hotKey, Func<Task> action)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(action);
        if (!HotKeyGesture.TryParse(hotKey, out var gesture) || gesture is null)
        {
            return false;
        }

        var id = nextId++;
        if (!NativeMethods.RegisterHotKey(window.Handle, id, gesture.Modifiers, gesture.VirtualKey))
        {
            if ((gesture.Modifiers & HotKeyModifiers.Windows) == 0)
            {
                return false;
            }

            exclusiveHotKeys ??= new ExclusiveHotKeyService(window.Dispatcher);
            var registered = exclusiveHotKeys.Register(gesture, action);
            if (registered)
            {
                ExclusiveRegistrationCount++;
            }

            return registered;
        }

        actions[id] = action;
        return true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        foreach (var id in actions.Keys)
        {
            _ = NativeMethods.UnregisterHotKey(window.Handle, id);
        }

        actions.Clear();
        exclusiveHotKeys?.Dispose();
        exclusiveHotKeys = null;
        window.RemoveHook(WindowProcedure);
        window.Dispose();
        disposed = true;
    }

    private nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == HotKeyMessage && actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            _ = window.Dispatcher.InvokeAsync(action).Task.Unwrap();
        }

        return 0;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool RegisterHotKey(
            nint windowHandle,
            int id,
            HotKeyModifiers modifiers,
            uint virtualKey);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool UnregisterHotKey(nint windowHandle, int id);
    }
}
