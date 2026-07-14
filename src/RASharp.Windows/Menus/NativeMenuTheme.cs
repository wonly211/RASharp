using System.Runtime.InteropServices;

namespace RASharp.Windows.Menus;

internal static partial class NativeMenuTheme
{
    private const int PreferredAppModeForceDark = 2;
    private const int PreferredAppModeForceLight = 3;
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeBefore20H1 = 19;
    private static readonly SetPreferredAppModeDelegate? SetPreferredAppMode;
    private static readonly FlushMenuThemesDelegate? FlushMenuThemes;

    static NativeMenuTheme()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
        {
            return;
        }

        var module = NativeMethods.LoadLibrary("uxtheme.dll");
        if (module == 0)
        {
            return;
        }

        var setPreferredAddress = NativeMethods.GetProcAddress(module, 135);
        var flushAddress = NativeMethods.GetProcAddress(module, 136);
        if (setPreferredAddress != 0)
        {
            SetPreferredAppMode = Marshal.GetDelegateForFunctionPointer<SetPreferredAppModeDelegate>(
                setPreferredAddress);
        }

        if (flushAddress != 0)
        {
            FlushMenuThemes = Marshal.GetDelegateForFunctionPointer<FlushMenuThemesDelegate>(flushAddress);
        }
    }

    public static void Apply(bool dark, nint ownerWindow)
    {
        _ = SetPreferredAppMode?.Invoke(dark ? PreferredAppModeForceDark : PreferredAppModeForceLight);
        _ = NativeMethods.SetWindowTheme(ownerWindow, dark ? "DarkMode_Explorer" : "Explorer", null);

        var enabled = dark ? 1 : 0;
        if (NativeMethods.DwmSetWindowAttribute(
                ownerWindow,
                DwmUseImmersiveDarkMode,
                ref enabled,
                sizeof(int)) != 0)
        {
            _ = NativeMethods.DwmSetWindowAttribute(
                ownerWindow,
                DwmUseImmersiveDarkModeBefore20H1,
                ref enabled,
                sizeof(int));
        }

        FlushMenuThemes?.Invoke();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int SetPreferredAppModeDelegate(int preferredAppMode);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void FlushMenuThemesDelegate();

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryW", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial nint LoadLibrary(string fileName);

        [LibraryImport("kernel32.dll", EntryPoint = "GetProcAddress")]
        internal static partial nint GetProcAddress(nint module, nint ordinal);

        [LibraryImport("uxtheme.dll", EntryPoint = "SetWindowTheme", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int SetWindowTheme(nint window, string? subAppName, string? subIdList);

        [LibraryImport("dwmapi.dll")]
        internal static partial int DwmSetWindowAttribute(
            nint window,
            int attribute,
            ref int value,
            int valueSize);
    }
}
