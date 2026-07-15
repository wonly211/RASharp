using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using RASharp.Core.Menus;

namespace RASharp.Windows.Menus;

public sealed record IconCacheItem(
    int MenuNumber,
    string DisplayName,
    string Target,
    string Identity)
{
    public string Description => $"{DisplayName}  —  {Target}";
}

public sealed record IconChoice(int Index, BitmapSource Image)
{
    public string Label => $"#{Index}";
}

public sealed partial class IconCacheService
{
    private const uint ShellGetIcon = 0x000000100;
    private const uint ShellLargeIcon = 0x000000000;
    private const uint ShellUseFileAttributes = 0x000000010;

    public IconCacheService(string cacheRoot)
    {
        CacheRoot = Path.GetFullPath(cacheRoot);
        Directory.CreateDirectory(CacheRoot);
        MigrateLegacyDirectories();
    }

    public string CacheRoot { get; }

    public string GetCachePath(IconCacheItem item)
    {
        var prefix = SanitizeFileName(item.DisplayName);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.Identity)))[..16];
        return Path.Combine(CacheRoot, $"Menu{item.MenuNumber}-{prefix}-{hash}.png");
    }

    public BitmapSource? GetCachedPreview(IconCacheItem item) =>
        TryLoadImage(GetCachePath(item));

    public static BitmapSource GetSourcePreview(string sourcePath, int iconSize, int iconIndex = -1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var absolutePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(absolutePath) && !Directory.Exists(absolutePath))
        {
            throw new FileNotFoundException("指定的图标来源不存在。", absolutePath);
        }

        var extension = Path.GetExtension(absolutePath);
        var image = iconIndex >= 0
            ? ExtractIndexedIcon(absolutePath, iconIndex, iconSize)
            : extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ico", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            ? TryLoadImage(absolutePath)
            : ExtractShellIcon(
                absolutePath,
                Directory.Exists(absolutePath) ? 0x00000010u : 0x00000080u,
                iconSize);
        return image ?? throw new InvalidDataException("无法从指定文件读取图标。");
    }

    public BitmapSource? GetOrCreate(
        IconCacheItem item,
        string shellPath,
        uint fileAttributes,
        int iconSize)
    {
        var cachePath = GetCachePath(item);
        var cached = TryLoadImage(cachePath);
        if (cached is not null)
        {
            return cached;
        }

        var extracted = ExtractShellIcon(shellPath, fileAttributes, iconSize);
        if (extracted is null)
        {
            return null;
        }

        TrySaveImage(cachePath, extracted);
        return extracted;
    }

    public void ApplyOverride(IconCacheItem item, string sourcePath, int iconSize, int iconIndex = -1)
    {
        var image = GetSourcePreview(sourcePath, iconSize, iconIndex);
        SaveImage(GetCachePath(item), image);
    }

    public unsafe IReadOnlyList<IconChoice> GetIconChoices(string sourcePath, int iconSize)
    {
        var absolutePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException("程序或 DLL 不存在。", absolutePath);
        }

        var count = checked((int)NativeMethods.ExtractIconEx(absolutePath, -1, null, null, 0));
        if (count == 0)
        {
            return [];
        }

        count = Math.Min(count, 2048);
        var handles = new nint[count];
        uint extracted;
        unsafe
        {
            fixed (nint* largeIcons = handles)
            {
                extracted = NativeMethods.ExtractIconEx(
                    absolutePath,
                    0,
                    largeIcons,
                    null,
                    (uint)count);
            }
        }

        var result = new List<IconChoice>(checked((int)extracted));
        for (var index = 0; index < extracted; index++)
        {
            var icon = handles[index];
            if (icon == 0)
            {
                continue;
            }

            try
            {
                var image = CreateBitmapSource(icon, iconSize);
                result.Add(new IconChoice(index, image));
            }
            finally
            {
                _ = NativeMethods.DestroyIcon(icon);
            }
        }

        return result;
    }

    public void Clear()
    {
        if (Directory.Exists(CacheRoot))
        {
            Directory.Delete(CacheRoot, recursive: true);
        }

        Directory.CreateDirectory(CacheRoot);
    }

    public static IconCacheItem ForEntry(int menuNumber, MenuEntry entry) => new(
        menuNumber,
        entry.DisplayName,
        entry.EffectiveValue,
        $"entry|{entry.Kind}|{entry.DisplayName}|{entry.Value}");

    public static IconCacheItem ForCategory(int menuNumber, MenuCategory category) => new(
        menuNumber,
        category.Name,
        "分类",
        $"category|{category.RawText}");

    private static BitmapSource? ExtractShellIcon(string path, uint attributes, int iconSize)
    {
        var useFileAttributes = !File.Exists(path) && !Directory.Exists(path);
        var flags = ShellGetIcon | ShellLargeIcon | (useFileAttributes ? ShellUseFileAttributes : 0);
        var shellInfo = new ShellFileInfo();
        if (NativeMethods.SHGetFileInfo(
                path,
                attributes,
                ref shellInfo,
                (uint)Marshal.SizeOf<ShellFileInfo>(),
                flags) == 0
            || shellInfo.IconHandle == 0)
        {
            return null;
        }

        try
        {
            return CreateBitmapSource(shellInfo.IconHandle, iconSize);
        }
        finally
        {
            _ = NativeMethods.DestroyIcon(shellInfo.IconHandle);
        }
    }

    private static BitmapSource? ExtractIndexedIcon(string path, int iconIndex, int iconSize)
    {
        nint icon = 0;
        unsafe
        {
            var extracted = NativeMethods.ExtractIconEx(path, iconIndex, &icon, null, 1);
            if (extracted == 0 || icon == 0)
            {
                return null;
            }
        }

        try
        {
            return CreateBitmapSource(icon, iconSize);
        }
        finally
        {
            _ = NativeMethods.DestroyIcon(icon);
        }
    }

    private static BitmapSource CreateBitmapSource(nint icon, int iconSize)
    {
        var source = Imaging.CreateBitmapSourceFromHIcon(
            icon,
            Int32Rect.Empty,
            BitmapSizeOptions.FromWidthAndHeight(iconSize, iconSize));
        source.Freeze();
        return source;
    }

    private static BitmapFrame? TryLoadImage(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            foreach (var candidate in decoder.Frames)
            {
                if ((long)candidate.PixelWidth * candidate.PixelHeight
                    > (long)frame.PixelWidth * frame.PixelHeight)
                {
                    frame = candidate;
                }
            }

            frame.Freeze();
            return frame;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or FormatException)
        {
            return null;
        }
    }

    private static void TrySaveImage(string path, BitmapSource image)
    {
        try
        {
            SaveImage(path, image);
        }
        catch (IOException)
        {
            // A cache write failure must not prevent the menu from opening.
        }
        catch (UnauthorizedAccessException)
        {
            // A read-only application directory can still use in-memory icons.
        }
    }

    private static void SaveImage(string path, BitmapSource image)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(temporaryPath))
        {
            encoder.Save(stream);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var normalized = new string(value
            .Where(character => !invalid.Contains(character) && !char.IsControl(character))
            .Take(48)
            .ToArray())
            .Trim();
        return normalized.Length == 0 ? "icon" : normalized;
    }

    private void MigrateLegacyDirectories()
    {
        MigrateLegacyDirectory("MenuIcon", 1);
        MigrateLegacyDirectory("MenuIcon2", 2);
    }

    private void MigrateLegacyDirectory(string directoryName, int menuNumber)
    {
        var legacyDirectory = Path.Combine(CacheRoot, directoryName);
        if (!Directory.Exists(legacyDirectory))
        {
            return;
        }

        try
        {
            foreach (var sourcePath in Directory.EnumerateFiles(legacyDirectory))
            {
                var destinationPath = Path.Combine(
                    CacheRoot,
                    $"Menu{menuNumber}-{Path.GetFileName(sourcePath)}");
                if (File.Exists(destinationPath))
                {
                    File.Delete(sourcePath);
                }
                else
                {
                    File.Move(sourcePath, destinationPath);
                }
            }

            if (!Directory.EnumerateFileSystemEntries(legacyDirectory).Any())
            {
                Directory.Delete(legacyDirectory);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A legacy cache migration failure must not prevent RASharp from starting.
        }
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
        [LibraryImport(
            "shell32.dll",
            EntryPoint = "ExtractIconExW",
            StringMarshalling = StringMarshalling.Utf16)]
        internal static unsafe partial uint ExtractIconEx(
            string fileName,
            int iconIndex,
            nint* largeIcons,
            nint* smallIcons,
            uint iconCount);

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
    }
}
