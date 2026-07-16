using System.IO;
using System.Windows;
using RASharp.Core.Menus;
using RASharp.Windows.Everything;
using RASharp.Windows.Input;
using RASharp.Windows.Menus;
using Forms = System.Windows.Forms;

namespace RASharp.App.Settings;

public partial class SettingsWindow : Window
{
    private readonly string configDirectory;
    private readonly IconCacheService iconCacheService;
    private string? selectedIconSource;
    private int selectedIconIndex = -1;

    public SettingsWindow(
        AppSettings current,
        string configDirectory,
        string cachePath,
        string everythingDirectory,
        string everythingStatus,
        IconCacheService iconCacheService,
        IReadOnlyList<IconCacheItem> iconItems)
    {
        InitializeComponent();
        this.configDirectory = configDirectory;
        this.iconCacheService = iconCacheService;
        VersionTextBlock.Text = $"版本 v{typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "未知"}";
        Settings = current.Copy();
        StartWithWindowsCheckBox.IsChecked = Settings.StartWithWindows;
        ThemeModeComboBox.SelectedIndex = (int)Settings.ThemeMode;
        MenuHotKeyTextBox.Text = Settings.MenuHotKey;
        Menu2HotKeyTextBox.Text = Settings.Menu2HotKey;
        SettingsHotKeyTextBox.Text = Settings.SettingsHotKey;
        EnableCacheCheckBox.IsChecked = Settings.EnableProgramCache;
        EnableEverythingCheckBox.IsChecked = Settings.EnableEverything;
        AutoUpdateEverythingCheckBox.IsChecked = Settings.AutomaticallyUpdateEverything;
        EverythingHotKeyTextBox.Text = Settings.EverythingHotKey;
        EverythingIncludeFileExtensionCheckBox.IsChecked = Settings.EverythingIncludeFileExtension;
        EverythingSearchFolderContentsCheckBox.IsChecked = Settings.EverythingSearchFolderContents;
        EverythingDirectoryTextBox.Text = everythingDirectory;
        EverythingManagedStatusTextBlock.Text = everythingStatus;
        ConfigDirectoryTextBox.Text = configDirectory;
        CachePathTextBox.Text = cachePath;
        CacheInfoTextBlock.Text = BuildCacheInfo(cachePath);
        IconCacheDirectoryTextBox.Text = iconCacheService.CacheRoot;
        IconItemComboBox.ItemsSource = iconItems;
        IconItemComboBox.SelectedIndex = iconItems.Count > 0 ? 0 : -1;
    }

    public AppSettings Settings { get; }

    public bool ClearCacheRequested { get; private set; }

    public bool ForceEverythingUpdateRequested { get; private set; }

    public bool ClearIconCacheRequested { get; private set; }

    public IconOverrideRequest? IconOverride { get; private set; }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        ClearCacheRequested = true;
        ClearCacheStatusTextBlock.Text = "将在保存后清空并重建";
    }

    private void UpdateEverything_Click(object sender, RoutedEventArgs e)
    {
        EnableEverythingCheckBox.IsChecked = true;
        ForceEverythingUpdateRequested = true;
        EverythingStatusTextBlock.Foreground = System.Windows.Media.Brushes.ForestGreen;
        EverythingStatusTextBlock.Text = "将在保存后检查、安装或升级；服务配置时 Windows 会显示 UAC。";
    }

    private void BrowseIcon_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.OpenFileDialog
        {
            Title = "选择图标来源",
            Filter = "图标和程序|*.png;*.ico;*.jpg;*.jpeg;*.bmp;*.exe;*.dll|所有文件|*.*",
            CheckFileExists = true,
            RestoreDirectory = true,
        };
        var initialDirectory = GetIconBrowseInitialDirectory(
            IconItemComboBox.SelectedItem as IconCacheItem);
        if (initialDirectory is not null)
        {
            dialog.InitialDirectory = initialDirectory;
        }

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            var sourcePath = dialog.FileName;
            var extension = Path.GetExtension(sourcePath);
            if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var choices = iconCacheService.GetIconChoices(sourcePath, iconSize: 64);
                    if (choices.Count == 0)
                    {
                        ShowValidationError("该文件中没有可选择的图标资源。", IconSourceTextBox);
                        return;
                    }

                    var picker = new IconPickerWindow(sourcePath, choices) { Owner = this };
                    if (picker.ShowDialog() != true || picker.SelectedChoice is null)
                    {
                        return;
                    }

                    selectedIconSource = sourcePath;
                    selectedIconIndex = picker.SelectedChoice.Index;
                    SetIconPreview(
                        picker.SelectedChoice.Image,
                        $"待指定图标：资源 #{selectedIconIndex}");
                    IconCacheStatusTextBlock.Text = $"已选择资源 #{selectedIconIndex}，请点击“指定所选图标”。";
                }
                catch (Exception exception) when (exception is IOException
                    or InvalidOperationException or NotSupportedException)
                {
                    ShowValidationError(exception.Message, IconSourceTextBox);
                    return;
                }
            }
            else
            {
                selectedIconSource = null;
                selectedIconIndex = -1;
                try
                {
                    SetIconPreview(
                        IconCacheService.GetSourcePreview(sourcePath, iconSize: 96),
                        "待指定图标");
                }
                catch (Exception exception) when (exception is IOException
                    or InvalidOperationException or NotSupportedException)
                {
                    ShowValidationError(exception.Message, IconSourceTextBox);
                    return;
                }
            }

            IconSourceTextBox.Text = sourcePath;
        }
    }

    private void ApplyIconOverride_Click(object sender, RoutedEventArgs e)
    {
        if (IconItemComboBox.SelectedItem is not IconCacheItem item)
        {
            ShowValidationError("请先选择一个菜单项。", IconItemComboBox);
            return;
        }

        var sourcePath = IconSourceTextBox.Text.Trim();
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
        {
            ShowValidationError("指定的图标来源不存在。", IconSourceTextBox);
            return;
        }

        var iconIndex = sourcePath.Equals(selectedIconSource, StringComparison.OrdinalIgnoreCase)
            ? selectedIconIndex
            : -1;
        try
        {
            SetIconPreview(
                IconCacheService.GetSourcePreview(sourcePath, iconSize: 96, iconIndex),
                iconIndex >= 0 ? $"待指定图标：资源 #{iconIndex}" : "待指定图标");
        }
        catch (Exception exception) when (exception is IOException
            or InvalidOperationException or NotSupportedException)
        {
            ShowValidationError(exception.Message, IconSourceTextBox);
            return;
        }

        IconOverride = new IconOverrideRequest(item, sourcePath, iconIndex);
        IconCacheStatusTextBlock.Text = iconIndex >= 0
            ? $"保存后将用资源 #{iconIndex} 替换：{item.DisplayName}"
            : $"保存后将替换：{item.DisplayName}";
    }

    private void ClearIconCache_Click(object sender, RoutedEventArgs e)
    {
        ClearIconCacheRequested = true;
        IconCacheStatusTextBlock.Text = "保存后将清空全部图标缓存并重新提取。";
    }

    private void IconItemComboBox_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IconItemComboBox.SelectedItem is not IconCacheItem item)
        {
            SetIconPreview(null, "请选择菜单项目");
            return;
        }

        var preview = iconCacheService.GetCachedPreview(item);
        SetIconPreview(
            preview,
            preview is null
                ? $"{item.DisplayName} 尚未生成图标缓存"
                : $"当前图标：{item.DisplayName}");
    }

    private void SetIconPreview(System.Windows.Media.ImageSource? image, string caption)
    {
        IconPreviewImage.Source = image;
        IconPreviewPlaceholderTextBlock.Visibility = image is null
            ? Visibility.Visible
            : Visibility.Collapsed;
        IconPreviewCaptionTextBlock.Text = caption;
    }

    private static string? GetIconBrowseInitialDirectory(IconCacheItem? item)
    {
        if (item is null)
        {
            return null;
        }

        var targetPath = ParsedCommand.TryParse(item.Target)?.Executable ?? item.Target;
        if (File.Exists(targetPath))
        {
            return Path.GetDirectoryName(Path.GetFullPath(targetPath));
        }

        return Directory.Exists(targetPath)
            ? Path.GetFullPath(targetPath)
            : null;
    }

    private void TestEverything_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var everything = EverythingSearch.TryCreate(
                EverythingDirectoryTextBox.Text,
                configDirectory);
            EverythingStatusTextBlock.Foreground = System.Windows.Media.Brushes.ForestGreen;
            EverythingStatusTextBlock.Text = everything switch
            {
                null => "未找到或无法加载 SDK DLL",
                { IsAvailable: false } => "SDK 已加载，但 Everything 未运行或数据库未就绪",
                _ => $"连接正常：{everything.LibraryPath}",
            };
            if (everything is null || !everything.IsAvailable)
            {
                EverythingStatusTextBlock.Foreground = System.Windows.Media.Brushes.DarkOrange;
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException or BadImageFormatException)
        {
            EverythingStatusTextBlock.Foreground = System.Windows.Media.Brushes.Firebrick;
            EverythingStatusTextBlock.Text = exception.Message;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var menuHotKey = MenuHotKeyTextBox.Text.Trim();
        var menu2HotKey = Menu2HotKeyTextBox.Text.Trim();
        var settingsHotKey = SettingsHotKeyTextBox.Text.Trim();
        var everythingHotKey = EverythingHotKeyTextBox.Text.Trim();
        if (!HotKeyGesture.TryParse(menuHotKey, out _))
        {
            ShowValidationError("主菜单热键格式无效。", MenuHotKeyTextBox);
            return;
        }

        if (menu2HotKey.Length > 0 && !HotKeyGesture.TryParse(menu2HotKey, out _))
        {
            ShowValidationError("菜单2热键格式无效。", Menu2HotKeyTextBox);
            return;
        }

        if (settingsHotKey.Length > 0 && !HotKeyGesture.TryParse(settingsHotKey, out _))
        {
            ShowValidationError("打开设置热键格式无效。", SettingsHotKeyTextBox);
            return;
        }

        if (everythingHotKey.Length > 0 && !HotKeyGesture.TryParse(everythingHotKey, out _))
        {
            ShowValidationError("Everything 热键格式无效。", EverythingHotKeyTextBox);
            return;
        }

        Settings.StartWithWindows = StartWithWindowsCheckBox.IsChecked == true;
        Settings.ThemeMode = ThemeModeComboBox.SelectedIndex switch
        {
            1 => AppThemeMode.Light,
            2 => AppThemeMode.Dark,
            _ => AppThemeMode.System,
        };
        Settings.MenuHotKey = menuHotKey;
        Settings.Menu2HotKey = menu2HotKey;
        Settings.SettingsHotKey = settingsHotKey;
        Settings.EnableProgramCache = EnableCacheCheckBox.IsChecked == true;
        Settings.EnableEverything = EnableEverythingCheckBox.IsChecked == true;
        Settings.AutomaticallyUpdateEverything = AutoUpdateEverythingCheckBox.IsChecked == true;
        Settings.EverythingHotKey = everythingHotKey;
        Settings.EverythingIncludeFileExtension = EverythingIncludeFileExtensionCheckBox.IsChecked == true;
        Settings.EverythingSearchFolderContents = EverythingSearchFolderContentsCheckBox.IsChecked == true;
        Settings.EverythingSdkDirectory = string.Empty;
        DialogResult = true;
    }

    private static void ShowValidationError(string message, System.Windows.Controls.Control control)
    {
        _ = System.Windows.MessageBox.Show(
            message,
            "RASharp 设置",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        control.Focus();
    }

    private static string BuildCacheInfo(string path)
    {
        if (!File.Exists(path))
        {
            return "缓存尚未创建";
        }

        var information = new FileInfo(path);
        return $"大小：{information.Length:N0} 字节；更新时间：{information.LastWriteTime:yyyy-MM-dd HH:mm:ss}";
    }
}

public sealed record IconOverrideRequest(IconCacheItem Item, string SourcePath, int IconIndex);
