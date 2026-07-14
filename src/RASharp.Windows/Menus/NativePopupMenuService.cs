using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RASharp.Core.Menus;

namespace RASharp.Windows.Menus;

public sealed partial class NativePopupMenuService : IDisposable
{
    private const uint MenuString = 0x0000;
    private const uint MenuPopup = 0x0010;
    private const uint MenuFlagSeparator = 0x0800;
    private const uint MenuItemBitmap = 0x0080;
    private const uint TrackLeftAlign = 0x0000;
    private const uint TrackTopAlign = 0x0000;
    private const uint TrackRightButton = 0x0002;
    private const uint TrackReturnCommand = 0x0100;
    private const uint TrackNoNotify = 0x0080;
    private const uint WindowNullMessage = 0x0000;
    private const uint SystemGetKeyboardCues = 0x100A;
    private const uint SystemSetKeyboardCues = 0x100B;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint DibRgbColors = 0;
    private const uint BitmapCompressionRgb = 0;
    private const int PopupWindowStyle = unchecked((int)0x80000000);

    private readonly HwndSource ownerWindow;
    private readonly VariableExpander variableExpander;
    private readonly string configDirectory;
    private readonly IconCacheService iconCache;
    private readonly Action<string>? diagnosticLog;
    private readonly Func<bool>? isDarkTheme;
    private readonly Dictionary<string, nint> iconBitmaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly int iconSize;
    private bool disposed;
    private uint nextCommandId;

    public NativePopupMenuService(
        VariableExpander variableExpander,
        string configDirectory,
        IconCacheService iconCache,
        Action<string>? diagnosticLog = null,
        Func<bool>? isDarkTheme = null)
    {
        this.variableExpander = variableExpander;
        this.configDirectory = configDirectory;
        this.iconCache = iconCache;
        this.diagnosticLog = diagnosticLog;
        this.isDarkTheme = isDarkTheme;
        ownerWindow = new HwndSource(new HwndSourceParameters("RASharp.NativePopupMenu")
        {
            WindowStyle = PopupWindowStyle,
            Width = 1,
            Height = 1,
        });
        var dpi = NativeMethods.GetDpiForSystem();
        iconSize = Math.Clamp((int)Math.Round(32 * dpi / 96d), 24, 64);
    }

    public MenuEntry? Show(MenuDocument document)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(document);

        var dark = isDarkTheme?.Invoke() == true;
        NativeMenuTheme.Apply(dark, ownerWindow.Handle);
        diagnosticLog?.Invoke($"native menu theme dark={dark}");
        var rootMenu = NativeMethods.CreatePopupMenu();
        if (rootMenu == 0)
        {
            throw new InvalidOperationException("CreatePopupMenu failed.");
        }

        var restoreKeyboardCues = EnableKeyboardCuesForPopup();
        diagnosticLog?.Invoke($"keyboard cues forced={restoreKeyboardCues}");
        try
        {
            nextCommandId = 100;
            var commands = new Dictionary<uint, MenuEntry>();
            AddElements(rootMenu, document.Root.Children, commands, document.MenuNumber);
            diagnosticLog?.Invoke($"native menu built commands={commands.Count}");
            if (!NativeMethods.GetCursorPos(out var cursor))
            {
                cursor = new NativePoint(0, 0);
            }

            var previousForegroundWindow = NativeMethods.GetForegroundWindow();
            _ = NativeMethods.SetForegroundWindow(ownerWindow.Handle);
            diagnosticLog?.Invoke($"track popup begin x={cursor.X} y={cursor.Y}");
            var commandId = NativeMethods.TrackPopupMenuEx(
                rootMenu,
                TrackLeftAlign | TrackTopAlign | TrackRightButton | TrackReturnCommand | TrackNoNotify,
                cursor.X,
                cursor.Y,
                ownerWindow.Handle,
                0);
            diagnosticLog?.Invoke($"track popup end command={commandId}");
            _ = NativeMethods.PostMessage(ownerWindow.Handle, WindowNullMessage, 0, 0);
            if (previousForegroundWindow != 0)
            {
                _ = NativeMethods.SetForegroundWindow(previousForegroundWindow);
            }

            return commandId != 0 && commands.TryGetValue(commandId, out var entry) ? entry : null;
        }
        finally
        {
            if (restoreKeyboardCues)
            {
                _ = NativeMethods.SetSystemParameter(SystemSetKeyboardCues, 0, 0, 0);
            }

            _ = NativeMethods.DestroyMenu(rootMenu);
        }
    }

    private static bool EnableKeyboardCuesForPopup()
    {
        var currentValue = 0;
        if (!NativeMethods.GetSystemParameter(SystemGetKeyboardCues, 0, ref currentValue, 0)
            || currentValue != 0)
        {
            return false;
        }

        return NativeMethods.SetSystemParameter(SystemSetKeyboardCues, 0, 1, 0);
    }

    public void WarmIcons(MenuDocument document)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(document);
        WarmElementIcons(document.Root.Children, document.MenuNumber);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        foreach (var bitmap in iconBitmaps.Values)
        {
            _ = NativeMethods.DeleteObject(bitmap);
        }

        iconBitmaps.Clear();
        ownerWindow.Dispose();
        disposed = true;
    }

    private void AddElements(
        nint menu,
        IEnumerable<MenuElement> elements,
        IDictionary<uint, MenuEntry> commands,
        int menuNumber)
    {
        var hasItem = false;
        var separatorPending = false;
        foreach (var element in elements)
        {
            if (element is MenuSeparator)
            {
                separatorPending = hasItem;
                continue;
            }

            if (!IsVisible(element))
            {
                continue;
            }

            if (separatorPending)
            {
                _ = NativeMethods.AppendMenu(menu, MenuFlagSeparator, 0, null);
                separatorPending = false;
            }

            switch (element)
            {
                case MenuCategory category:
                    AddCategory(menu, category, commands, menuNumber);
                    break;

                case MenuEntry entry:
                    AddEntry(menu, entry, commands, menuNumber);
                    break;
            }

            hasItem = true;
        }
    }

    private static bool IsVisible(MenuElement element) => element switch
    {
        MenuEntry entry => entry.IsAvailable,
        MenuCategory category => category.Children.Any(IsVisible),
        _ => false,
    };

    private void WarmElementIcons(IEnumerable<MenuElement> elements, int menuNumber)
    {
        foreach (var element in elements)
        {
            switch (element)
            {
                case MenuCategory category:
                    if (IsVisible(category))
                    {
                        _ = GetBitmap(
                            GetCategoryIconKey(category, menuNumber),
                            FileAttributeDirectory,
                            IconCacheService.ForCategory(menuNumber, category));
                        WarmElementIcons(category.Children, menuNumber);
                    }

                    break;

                case MenuEntry entry when entry.IsAvailable:
                    var (iconKey, attributes) = GetIconKey(entry);
                    _ = GetBitmap(
                        iconKey,
                        attributes,
                        IconCacheService.ForEntry(menuNumber, entry));
                    break;
            }
        }
    }

    private void AddCategory(
        nint menu,
        MenuCategory category,
        IDictionary<uint, MenuEntry> commands,
        int menuNumber)
    {
        var submenu = NativeMethods.CreatePopupMenu();
        if (submenu == 0)
        {
            return;
        }

        AddElements(submenu, category.Children, commands, menuNumber);
        var position = NativeMethods.GetMenuItemCount(menu);
        if (!NativeMethods.AppendMenu(menu, MenuPopup | MenuString, (nuint)submenu, category.Name))
        {
            _ = NativeMethods.DestroyMenu(submenu);
            return;
        }

        var categoryIcon = GetCategoryIconKey(category, menuNumber);
        SetItemBitmap(
            menu,
            (uint)position,
            byPosition: true,
            GetBitmap(
                categoryIcon,
                FileAttributeDirectory,
                IconCacheService.ForCategory(menuNumber, category)));
    }

    private string GetCategoryIconKey(MenuCategory category, int menuNumber)
    {
        var rawName = category.RawText.Trim().Split('|', 2)[0].Split('\t', 2)[0];
        if (rawName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return "::folder";
        }

        var iconFolder = menuNumber == 1 ? "MenuIcon" : "MenuIcon2";
        var iconPath = Path.Combine(configDirectory, "RunIcon", iconFolder, rawName + ".ico");
        return File.Exists(iconPath) ? iconPath : "::folder";
    }

    private void AddEntry(
        nint menu,
        MenuEntry entry,
        IDictionary<uint, MenuEntry> commands,
        int menuNumber)
    {
        var commandId = nextCommandId++;
        if (!NativeMethods.AppendMenu(menu, MenuString, commandId, entry.DisplayName))
        {
            return;
        }

        commands[commandId] = entry;
        var (iconKey, attributes) = GetIconKey(entry);
        SetItemBitmap(
            menu,
            commandId,
            byPosition: false,
            GetBitmap(iconKey, attributes, IconCacheService.ForEntry(menuNumber, entry)));
    }

    private (string Key, uint Attributes) GetIconKey(MenuEntry entry)
    {
        if (entry.Kind == MenuEntryKind.Web)
        {
            return ("::web.url", FileAttributeNormal);
        }

        if (entry.Kind is MenuEntryKind.Phrase or MenuEntryKind.RawPhrase)
        {
            return ("::phrase.txt", FileAttributeNormal);
        }

        if (entry.Kind is MenuEntryKind.KeySequence or MenuEntryKind.RawKeySequence)
        {
            return ("::keyboard.cmd", FileAttributeNormal);
        }

        var expanded = variableExpander.Expand(entry.EffectiveValue);
        var parsed = ParsedCommand.TryParse(expanded);
        var path = parsed?.Executable ?? expanded;
        if (Directory.Exists(path))
        {
            return (path, FileAttributeDirectory);
        }

        return (path, FileAttributeNormal);
    }

    private nint GetBitmap(string key, uint attributes, IconCacheItem cacheItem)
    {
        var bitmapKey = $"{cacheItem.MenuNumber}|{cacheItem.Identity}";
        if (iconBitmaps.TryGetValue(bitmapKey, out var cached))
        {
            return cached;
        }

        var path = key switch
        {
            "::folder" => "folder",
            "::web.url" => "RASharp.url",
            "::phrase.txt" => "RASharp.txt",
            "::keyboard.cmd" => "RASharp.cmd",
            _ => key,
        };
        var source = iconCache.GetOrCreate(cacheItem, path, attributes, iconSize);
        if (source is null)
        {
            iconBitmaps[bitmapKey] = 0;
            return 0;
        }

        var bitmap = CreateBitmapFromImage(source);
        iconBitmaps[bitmapKey] = bitmap;
        return bitmap;
    }

    private static void SetItemBitmap(nint menu, uint item, bool byPosition, nint bitmap)
    {
        if (bitmap == 0)
        {
            return;
        }

        var information = new MenuItemInformation
        {
            Size = (uint)Marshal.SizeOf<MenuItemInformation>(),
            Mask = MenuItemBitmap,
            ItemBitmap = bitmap,
        };
        _ = NativeMethods.SetMenuItemInfo(menu, item, byPosition, ref information);
    }

    private nint CreateBitmapFromImage(BitmapSource source)
    {
        var screenDc = NativeMethods.GetDC(0);
        if (screenDc == 0)
        {
            return 0;
        }

        var bitmapInfo = new BitmapInformation
        {
            Header = new BitmapInformationHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInformationHeader>(),
                Width = iconSize,
                Height = -iconSize,
                Planes = 1,
                BitCount = 32,
                Compression = BitmapCompressionRgb,
            },
        };
        var bitmap = NativeMethods.CreateDIBSection(
            screenDc,
            ref bitmapInfo,
            DibRgbColors,
            out var pixels,
            0,
            0);
        if (bitmap == 0 || pixels == 0)
        {
            _ = NativeMethods.ReleaseDC(0, screenDc);
            return 0;
        }

        _ = NativeMethods.ReleaseDC(0, screenDc);
        BitmapSource scaled = source;
        if (source.PixelWidth != iconSize || source.PixelHeight != iconSize)
        {
            scaled = new TransformedBitmap(
                source,
                new ScaleTransform(
                    iconSize / (double)source.PixelWidth,
                    iconSize / (double)source.PixelHeight));
        }

        var formatted = new FormatConvertedBitmap(scaled, PixelFormats.Pbgra32, null, 0);
        var stride = iconSize * 4;
        formatted.CopyPixels(Int32Rect.Empty, pixels, stride * iconSize, stride);
        return bitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private struct MenuItemInformation
    {
        public uint Size;
        public uint Mask;
        public uint Type;
        public uint State;
        public uint Id;
        public nint SubMenu;
        public nint CheckedBitmap;
        public nint UncheckedBitmap;
        public nuint ItemData;
        public nint TypeData;
        public uint CharacterCount;
        public nint ItemBitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInformationHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int PixelsPerMeterX;
        public int PixelsPerMeterY;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInformation
    {
        public BitmapInformationHeader Header;
        public uint Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ShellFileInfo
    {
        public nint IconHandle;
        public int IconIndex;
        public uint Attributes;
        public fixed char DisplayName[260];
        public fixed char TypeName[80];
    }

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll", SetLastError = true)]
        internal static partial nint CreatePopupMenu();

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DestroyMenu(nint menu);

        [LibraryImport(
            "user32.dll",
            EntryPoint = "AppendMenuW",
            SetLastError = true,
            StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool AppendMenu(nint menu, uint flags, nuint item, string? text);

        [LibraryImport("user32.dll", EntryPoint = "SetMenuItemInfoW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetMenuItemInfo(
            nint menu,
            uint item,
            [MarshalAs(UnmanagedType.Bool)] bool byPosition,
            ref MenuItemInformation information);

        [LibraryImport("user32.dll")]
        internal static partial int GetMenuItemCount(nint menu);

        [LibraryImport("user32.dll", SetLastError = true)]
        internal static partial uint TrackPopupMenuEx(
            nint menu,
            uint flags,
            int x,
            int y,
            nint owner,
            nint parameters);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetCursorPos(out NativePoint point);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetForegroundWindow(nint window);

        [LibraryImport("user32.dll")]
        internal static partial nint GetForegroundWindow();

        [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool PostMessage(nint window, uint message, nint wParam, nint lParam);

        [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetSystemParameter(
            uint action,
            uint parameter,
            ref int value,
            uint flags);

        [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetSystemParameter(
            uint action,
            uint parameter,
            nint value,
            uint flags);

        [LibraryImport("user32.dll")]
        internal static partial uint GetDpiForSystem();

        [LibraryImport(
            "shell32.dll",
            EntryPoint = "SHGetFileInfoW",
            StringMarshalling = StringMarshalling.Utf16)]
        internal static partial nuint SHGetFileInfo(
            string path,
            uint fileAttributes,
            ref ShellFileInfo fileInfo,
            uint fileInfoSize,
            uint flags);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DestroyIcon(nint icon);

        [LibraryImport("user32.dll")]
        internal static partial nint GetDC(nint window);

        [LibraryImport("user32.dll")]
        internal static partial int ReleaseDC(nint window, nint deviceContext);

        [LibraryImport("gdi32.dll")]
        internal static partial nint CreateCompatibleDC(nint deviceContext);

        [LibraryImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DeleteDC(nint deviceContext);

        [LibraryImport("gdi32.dll", SetLastError = true)]
        internal static partial nint CreateDIBSection(
            nint deviceContext,
            ref BitmapInformation bitmapInformation,
            uint usage,
            out nint pixels,
            nint section,
            uint offset);

        [LibraryImport("gdi32.dll")]
        internal static partial nint SelectObject(nint deviceContext, nint graphicObject);

        [LibraryImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DeleteObject(nint graphicObject);

        [LibraryImport("user32.dll", EntryPoint = "DrawIconEx")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DrawIconEx(
            nint deviceContext,
            int x,
            int y,
            nint icon,
            int width,
            int height,
            uint animationStep,
            nint flickerFreeBrush,
            uint flags);
    }
}
