using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using RASharp.Core.Menus;
using RASharp.Core.Programs;
using RASharp.App.Programs;
using RASharp.App.Settings;
using RASharp.App.Theming;
using RASharp.Windows.Clipboard;
using RASharp.Windows.Everything;
using RASharp.Windows.Execution;
using RASharp.Windows.Input;
using RASharp.Windows.Menus;
using Forms = System.Windows.Forms;

namespace RASharp.App;

public partial class App : System.Windows.Application, IDisposable
{
    private readonly List<IDisposable> runtimeServices = [];
    private readonly object reloadSync = new();
    private Forms.NotifyIcon? trayIcon;
    private SystemThemeService? themeService;
    private System.Drawing.Icon? applicationIcon;
    private Forms.ToolStripMenuItem? menu2TrayItem;
    private SettingsWindow? activeSettingsWindow;
    private NativePopupMenuService? popupMenuService;
    private CachedProgramResolver? programResolver;
    private EverythingSearch? everythingSearch;
    private EverythingManager? everythingManager;
    private IconCacheService? iconCacheService;
    private MenuEntryExecutor? executor;
    private SelectedContentService? selectedContentService;
    private List<MenuDocument> documents = [];
    private SelectedContent currentSelection = SelectedContent.Empty;
    private string configDirectory = string.Empty;
    private string settingsPath = string.Empty;
    private string cachePath = string.Empty;
    private AppSettings settings = new();
    private FileSystemWatcher? configWatcher;
    private CancellationTokenSource? reloadDebounce;
    private bool menuShowing;
    private bool disposed;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        themeService = new SystemThemeService(this);
        Log("startup " + string.Join(' ', e.Args));
        try
        {
            configDirectory = ResolveConfigDirectory(e.Args);
            MigrateLegacyConfiguration(configDirectory);
            settingsPath = Path.Combine(configDirectory, "settings.json");
            cachePath = Path.Combine(configDirectory, "everything-cache.json");
            var hasSavedSettings = File.Exists(settingsPath);
            settings = AppSettingsStore.Load(settingsPath);
            themeService?.SetMode(settings.ThemeMode);
            Log("config " + configDirectory);
            EnsureStarterConfiguration(configDirectory);
            if (hasSavedSettings || settings.StartWithWindows)
            {
                StartupRegistration.Apply(settings.StartWithWindows, configDirectory);
            }
            CreateTrayIcon();
            everythingManager = new EverythingManager(
                Path.Combine(AppContext.BaseDirectory, "everything"),
                Log);
            iconCacheService = new IconCacheService(
                Path.Combine(AppContext.BaseDirectory, "Cache", "RunIcon"));
            await PrepareEverythingAsync().ConfigureAwait(true);
            await ReloadAsync().ConfigureAwait(true);
            ConfigureConfigWatcher();
            Log("reload complete");
            if (e.Args.Contains("--show", StringComparer.OrdinalIgnoreCase))
            {
                await ShowMenuAsync().ConfigureAwait(true);
            }

            if (e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
            {
                await ShowSettingsAsync().ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            Log("startup failed " + exception);
            Forms.MessageBox.Show(
                exception.ToString(),
                "RASharp 启动失败",
                Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        configWatcher?.Dispose();
        configWatcher = null;
        lock (reloadSync)
        {
            reloadDebounce?.Cancel();
            reloadDebounce?.Dispose();
            reloadDebounce = null;
        }

        DisposeRuntimeServices();
        themeService?.Dispose();
        themeService = null;
        trayIcon?.Dispose();
        trayIcon = null;
        applicationIcon?.Dispose();
        applicationIcon = null;
        GC.SuppressFinalize(this);
    }

    private async Task ReloadAsync()
    {
        var newDocuments = LoadDocuments(configDirectory);
        Log($"documents {newDocuments.Count}");
        var newEverythingSearch = settings.EnableEverything
            ? EverythingSearch.TryCreate(
                everythingManager?.InstallationDirectory ?? string.Empty,
                settings.EverythingSdkDirectory,
                configDirectory)
            : null;
        Log($"everything loaded={newEverythingSearch is not null} available={newEverythingSearch?.IsAvailable}");
        var search = (IProgramSearch?)newEverythingSearch ?? UnavailableProgramSearch.Instance;
        var candidateSelector = new ProgramCandidateSelector(Dispatcher);
        var newProgramResolver = new CachedProgramResolver(
            search,
            cachePath,
            settings.EnableProgramCache,
            candidateSelector);
        var variableExpander = new VariableExpander(configDirectory);
        try
        {
            var resolutionService = new MenuResolutionService(newProgramResolver);
            foreach (var document in newDocuments)
            {
                await resolutionService.ResolveAsync(document, variableExpander).ConfigureAwait(true);
            }
        }
        catch
        {
            newProgramResolver.Dispose();
            newEverythingSearch?.Dispose();
            throw;
        }

        var availableCount = newDocuments.Sum(document => document.Entries.Count(entry => entry.IsAvailable));
        var hiddenCount = newDocuments.Sum(document => document.Entries.Count(entry => !entry.IsAvailable));
        Log($"resolution complete available={availableCount} hidden={hiddenCount}");
        foreach (var hiddenEntry in newDocuments
            .SelectMany(document => document.Entries)
            .Where(entry => !entry.IsAvailable))
        {
            Log($"hidden item line={hiddenEntry.SourceLine} name={hiddenEntry.DisplayName} reason={hiddenEntry.UnavailableReason}");
        }

        DisposeRuntimeServices();
        documents = newDocuments;
        everythingSearch = newEverythingSearch;
        programResolver = newProgramResolver;
        executor = new MenuEntryExecutor(variableExpander, programResolver);
        selectedContentService = new SelectedContentService(Dispatcher);
        iconCacheService ??= new IconCacheService(
            Path.Combine(AppContext.BaseDirectory, "Cache", "RunIcon"));
        popupMenuService = new NativePopupMenuService(
            variableExpander,
            configDirectory,
            iconCacheService,
            Log,
            () => themeService?.IsDark == true);
        runtimeServices.Add(popupMenuService);
        foreach (var document in documents)
        {
            popupMenuService.WarmIcons(document);
        }

        if (menu2TrayItem is not null)
        {
            menu2TrayItem.Visible = documents.Count > 1;
        }

        Log("native popup ready");

        var globalHotKeys = new GlobalHotKeyService();
        runtimeServices.Add(globalHotKeys);
        var menuHotKey = settings.MenuHotKey;
        if (!globalHotKeys.Register(menuHotKey, () => ShowMenuAsync(0)))
        {
            ShowBalloon("热键注册失败", $"无法注册菜单热键：{menuHotKey}", Forms.ToolTipIcon.Warning);
        }

        var menu2HotKey = settings.Menu2HotKey;
        if (documents.Count > 1
            && !string.IsNullOrWhiteSpace(menu2HotKey)
            && !globalHotKeys.Register(menu2HotKey, () => ShowMenuAsync(1)))
        {
            ShowBalloon("热键注册失败", $"无法注册菜单2热键：{menu2HotKey}", Forms.ToolTipIcon.Warning);
        }

        var settingsHotKey = settings.SettingsHotKey;
        if (!string.IsNullOrWhiteSpace(settingsHotKey)
            && !globalHotKeys.Register(settingsHotKey, ShowSettingsAsync))
        {
            ShowBalloon("热键注册失败", $"无法注册打开设置热键：{settingsHotKey}", Forms.ToolTipIcon.Warning);
        }

        if (settings.EnableEverything
            && everythingManager?.IsInstalled == true
            && !string.IsNullOrWhiteSpace(settings.EverythingHotKey)
            && !globalHotKeys.Register(settings.EverythingHotKey, () =>
            {
                everythingManager.ShowWindow();
                return Task.CompletedTask;
            }))
        {
            ShowBalloon(
                "热键注册失败",
                $"无法注册 Everything 热键：{settings.EverythingHotKey}",
                Forms.ToolTipIcon.Warning);
        }

        foreach (var entry in documents
            .SelectMany(document => document.Entries)
            .Where(entry => entry.IsAvailable))
        {
            if (entry.HotKey is not null
                && !globalHotKeys.Register(entry.HotKey, () => ExecuteFromGlobalHotKeyAsync(entry)))
            {
                ShowBalloon("热键冲突", $"{entry.DisplayName}: {entry.HotKey}", Forms.ToolTipIcon.Warning);
            }
        }

        var hotstringEntries = documents
            .SelectMany(document => document.Entries)
            .Where(entry => entry.IsAvailable && entry.Hotstring is not null)
            .ToArray();
        if (hotstringEntries.Length > 0)
        {
            var hotstrings = new HotstringService(Dispatcher);
            runtimeServices.Add(hotstrings);
            foreach (var entry in hotstringEntries)
            {
                hotstrings.Register(
                    entry.Hotstring!,
                    () => executor.ExecuteAsync(entry),
                    entry.Kind is MenuEntryKind.Phrase or MenuEntryKind.RawPhrase);
            }
        }

        Log($"input services ready exclusiveHotKeys={globalHotKeys.ExclusiveRegistrationCount}");

    }

    private void ConfigureConfigWatcher()
    {
        configWatcher?.Dispose();
        var watcher = new FileSystemWatcher(configDirectory, "RunAny*.ini")
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.LastWrite
                | NotifyFilters.CreationTime
                | NotifyFilters.Size,
        };
        watcher.Changed += OnConfigChanged;
        watcher.Created += OnConfigChanged;
        watcher.Deleted += OnConfigChanged;
        watcher.Renamed += OnConfigRenamed;
        watcher.Error += OnConfigWatcherError;
        watcher.EnableRaisingEvents = true;
        configWatcher = watcher;
        Log("config watcher ready");
    }

    private void OnConfigChanged(object sender, FileSystemEventArgs e)
    {
        if (IsMenuConfigPath(e.FullPath))
        {
            ScheduleConfigReload($"{e.ChangeType}: {e.Name}");
        }
    }

    private void OnConfigRenamed(object sender, RenamedEventArgs e)
    {
        if (IsMenuConfigPath(e.FullPath) || IsMenuConfigPath(e.OldFullPath))
        {
            ScheduleConfigReload($"renamed: {e.OldName} -> {e.Name}");
        }
    }

    private void OnConfigWatcherError(object sender, ErrorEventArgs e)
    {
        Log("config watcher error " + e.GetException().Message);
        ScheduleConfigReload("watcher error");
    }

    private void ScheduleConfigReload(string reason)
    {
        if (disposed)
        {
            return;
        }

        CancellationTokenSource debounce;
        lock (reloadSync)
        {
            reloadDebounce?.Cancel();
            reloadDebounce?.Dispose();
            debounce = new CancellationTokenSource();
            reloadDebounce = debounce;
        }

        Log("config change " + reason);
        _ = ReloadAfterDebounceAsync(debounce.Token);
    }

    private async Task ReloadAfterDebounceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(450, cancellationToken).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(ReloadSafelyAsync).Task.Unwrap().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A later file-system event superseded this reload request.
        }
    }

    private static bool IsMenuConfigPath(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.Equals("RunAny.ini", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("RunAny2.ini", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ShowMenuAsync(int menuIndex = 0)
    {
        Log("show menu requested");
        if (menuShowing)
        {
            Log("show menu ignored because another menu is active");
            return;
        }

        if (popupMenuService is null || selectedContentService is null)
        {
            return;
        }

        if (menuIndex < 0 || menuIndex >= documents.Count)
        {
            ShowBalloon("菜单不存在", $"没有载入菜单 {menuIndex + 1}", Forms.ToolTipIcon.Warning);
            return;
        }

        menuShowing = true;
        try
        {
            currentSelection = await selectedContentService.CaptureAsync().ConfigureAwait(true);
            Log("selection captured");
            Log("native menu tracking");
            var entry = popupMenuService.Show(documents[menuIndex]);
            Log("native menu closed");
            if (entry is not null)
            {
                await ExecuteFromMenuAsync(entry).ConfigureAwait(true);
            }
        }
        finally
        {
            menuShowing = false;
        }
    }

    private async Task ExecuteFromMenuAsync(MenuEntry entry)
    {
        if (executor is null)
        {
            return;
        }

        try
        {
            await executor.ExecuteAsync(entry, currentSelection).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            ShowBalloon("执行失败", exception.Message, Forms.ToolTipIcon.Error);
        }
    }

    private async Task ExecuteFromGlobalHotKeyAsync(MenuEntry entry)
    {
        if (selectedContentService is null)
        {
            return;
        }

        currentSelection = await selectedContentService.CaptureAsync().ConfigureAwait(true);
        await ExecuteFromMenuAsync(entry).ConfigureAwait(true);
    }

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示菜单", null, async (_, _) => await ShowMenuAsync(0).ConfigureAwait(true));
        menu2TrayItem = new Forms.ToolStripMenuItem("显示菜单2") { Visible = false };
        menu2TrayItem.Click += async (_, _) => await ShowMenuAsync(1).ConfigureAwait(true);
        menu.Items.Add(menu2TrayItem);
        menu.Items.Add("显示 Everything", null, async (_, _) => await ShowEverythingAsync().ConfigureAwait(true));
        menu.Items.Add("重新加载", null, async (_, _) => await ReloadSafelyAsync().ConfigureAwait(true));
        menu.Items.Add("设置…", null, async (_, _) => await ShowSettingsAsync().ConfigureAwait(true));
        menu.Items.Add("打开 RunAny.ini", null, (_, _) => OpenConfiguration());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Shutdown());

        applicationIcon = LoadApplicationIcon();
        trayIcon = new Forms.NotifyIcon
        {
            Icon = applicationIcon,
            Text = "RASharp",
            Visible = true,
            ContextMenuStrip = menu,
        };
        trayIcon.DoubleClick += async (_, _) => await ShowMenuAsync(0).ConfigureAwait(true);
    }

    private static System.Drawing.Icon LoadApplicationIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("Assets/RASharp.ico", UriKind.Relative));
        if (resource is null)
        {
            return (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        }

        using var icon = new System.Drawing.Icon(resource.Stream);
        return (System.Drawing.Icon)icon.Clone();
    }

    private async Task ShowEverythingAsync()
    {
        if (everythingManager?.IsInstalled != true)
        {
            await PrepareEverythingAsync(forceUpdate: true).ConfigureAwait(true);
        }

        try
        {
            everythingManager?.ShowWindow();
        }
        catch (InvalidOperationException exception)
        {
            ShowBalloon("Everything 不可用", exception.Message, Forms.ToolTipIcon.Warning);
        }
    }

    private async Task ShowSettingsAsync()
    {
        if (activeSettingsWindow is not null)
        {
            _ = activeSettingsWindow.Activate();
            return;
        }

        var iconItems = documents
            .SelectMany(document => document.Entries
                .Where(entry => entry.IsAvailable && entry.Kind == MenuEntryKind.Command)
                .Select(entry => IconCacheService.ForEntry(document.MenuNumber, entry)))
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        iconCacheService ??= new IconCacheService(
            Path.Combine(AppContext.BaseDirectory, "Cache", "RunIcon"));
        var window = new SettingsWindow(
            settings,
            configDirectory,
            cachePath,
            everythingManager?.InstallationDirectory ?? Path.Combine(AppContext.BaseDirectory, "everything"),
            everythingManager?.StatusText ?? "管理器未初始化",
            iconCacheService,
            iconItems);
        bool accepted;
        activeSettingsWindow = window;
        try
        {
            accepted = window.ShowDialog() == true;
        }
        finally
        {
            activeSettingsWindow = null;
        }

        if (!accepted)
        {
            return;
        }

        try
        {
            AppSettingsStore.Save(settingsPath, window.Settings);
            StartupRegistration.Apply(window.Settings.StartWithWindows, configDirectory);
            if (window.ClearCacheRequested && File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }

            if (window.ClearIconCacheRequested)
            {
                iconCacheService.Clear();
                Log("icon cache cleared");
            }

            if (window.IconOverride is not null)
            {
                iconCacheService.ApplyOverride(
                    window.IconOverride.Item,
                    window.IconOverride.SourcePath,
                    iconSize: 64,
                    iconIndex: window.IconOverride.IconIndex);
                Log($"icon override saved name={window.IconOverride.Item.DisplayName}");
            }

            var everythingWasEnabled = settings.EnableEverything;
            settings = window.Settings;
            themeService?.SetMode(settings.ThemeMode);
            if (window.ForceEverythingUpdateRequested
                || (!everythingWasEnabled && settings.EnableEverything)
                || (settings.EnableEverything && everythingManager?.IsInstalled != true))
            {
                await PrepareEverythingAsync(window.ForceEverythingUpdateRequested).ConfigureAwait(true);
            }

            await ReloadAsync().ConfigureAwait(true);
            Log("settings saved and applied");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or OperationCanceledException)
        {
            Log("settings save failed " + exception);
            ShowBalloon("保存设置失败", exception.Message, Forms.ToolTipIcon.Error);
        }
    }

    private async Task PrepareEverythingAsync(bool forceUpdate = false)
    {
        if (!settings.EnableEverything || everythingManager is null)
        {
            return;
        }

        try
        {
            await everythingManager.EnsureReadyAsync(
                settings.AutomaticallyUpdateEverything,
                forceUpdate).ConfigureAwait(true);
            Log("managed everything ready " + everythingManager.StatusText);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException
            or InvalidDataException or InvalidOperationException or OperationCanceledException)
        {
            Log("managed everything preparation failed " + exception);
            ShowBalloon("Everything 管理失败", exception.Message, Forms.ToolTipIcon.Warning);
        }
    }

    private async Task ReloadSafelyAsync()
    {
        try
        {
            await ReloadAsync().ConfigureAwait(true);
            Log("automatic reload complete");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException)
        {
            Log("reload failed " + exception);
            ShowBalloon("重新加载失败", exception.Message, Forms.ToolTipIcon.Error);
        }
    }

    private void OpenConfiguration()
    {
        var path = Path.Combine(configDirectory, "RunAny.ini");
        _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void DisposeRuntimeServices()
    {
        foreach (var service in runtimeServices.AsEnumerable().Reverse())
        {
            service.Dispose();
        }

        runtimeServices.Clear();
        popupMenuService = null;
        programResolver?.Dispose();
        programResolver = null;
        everythingSearch?.Dispose();
        everythingSearch = null;
    }

    private void ShowBalloon(string title, string message, Forms.ToolTipIcon icon)
    {
        if (trayIcon is null)
        {
            return;
        }

        trayIcon.BalloonTipTitle = title;
        trayIcon.BalloonTipText = message;
        trayIcon.BalloonTipIcon = icon;
        trayIcon.ShowBalloonTip(3500);
    }

    private static List<MenuDocument> LoadDocuments(string directory)
    {
        var firstPath = Path.Combine(directory, "RunAny.ini");
        var result = new List<MenuDocument> { RunAnyMenuParser.ParseFile(firstPath, 1) };
        var secondPath = Path.Combine(directory, "RunAny2.ini");
        if (File.Exists(secondPath))
        {
            result.Add(RunAnyMenuParser.ParseFile(secondPath, 2));
        }

        return result;
    }

    private static string ResolveConfigDirectory(string[] arguments)
    {
        for (var index = 0; index < arguments.Length; index++)
        {
            if (arguments[index].StartsWith("--config-dir=", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(arguments[index]["--config-dir=".Length..].Trim('"'));
            }

            if (arguments[index].Equals("--config-dir", StringComparison.OrdinalIgnoreCase)
                && index + 1 < arguments.Length)
            {
                return Path.GetFullPath(arguments[index + 1].Trim('"'));
            }
        }

        var environmentPath = Environment.GetEnvironmentVariable("RASHARP_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            return Path.GetFullPath(environmentPath);
        }

        return Path.Combine(AppContext.BaseDirectory, "Config");
    }

    private static void MigrateLegacyConfiguration(string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        var targetFullPath = Path.GetFullPath(targetDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var legacyAppDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RASharp");
        var legacyDirectories = new[]
        {
            AppContext.BaseDirectory,
            legacyAppDataDirectory,
        };

        foreach (var fileName in new[]
                 {
                     "RunAny.ini",
                     "RunAny2.ini",
                     "settings.json",
                     "everything-cache.json",
                 })
        {
            var destinationPath = Path.Combine(targetDirectory, fileName);
            if (File.Exists(destinationPath))
            {
                continue;
            }

            foreach (var legacyDirectory in legacyDirectories)
            {
                var legacyFullPath = Path.GetFullPath(legacyDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (legacyFullPath.Equals(targetFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var sourcePath = Path.Combine(legacyDirectory, fileName);
                if (!File.Exists(sourcePath))
                {
                    continue;
                }

                File.Copy(sourcePath, destinationPath, overwrite: false);
                break;
            }
        }
    }

    private static void EnsureStarterConfiguration(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "RunAny.ini");
        if (File.Exists(path))
        {
            return;
        }

        File.WriteAllText(path, """
            ; RASharp starter configuration
            -常用(&App)
            记事本(&N)|notepad.exe
            计算器(&C)|calc.exe
            -网址(&Web)
            百度(&B)|https://www.baidu.com/s?wd=%s
            -输入(&Input)
            示例邮箱:*:mail|name@example.com;
            当前时间|%A_YYYY%-%A_MM%-%A_DD% %A_Hour%:%A_Min%:%A_Sec%;
            """);
    }

    private static void Log(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RASharp");
            Directory.CreateDirectory(directory);
            var timestamp = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
            File.AppendAllText(Path.Combine(directory, "RASharp.log"), $"{timestamp} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Logging must never prevent the launcher from starting.
        }
        catch (UnauthorizedAccessException)
        {
            // Logging must never prevent the launcher from starting.
        }
    }

    private sealed class UnavailableProgramSearch : IProgramSearch
    {
        public static UnavailableProgramSearch Instance { get; } = new();

        public bool IsAvailable => false;

        public Task<IReadOnlyList<ProgramCandidate>> FindExactFileNameAsync(
            string fileName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProgramCandidate>>([]);
    }
}
