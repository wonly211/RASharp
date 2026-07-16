using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace RASharp.App.Settings;

public enum AppThemeMode
{
    System,
    Light,
    Dark,
}

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }

    public string MenuHotKey { get; set; } = "``";

    public string Menu2HotKey { get; set; } = string.Empty;

    public string SettingsHotKey { get; set; } = string.Empty;

    public bool EnableProgramCache { get; set; } = true;

    public bool EnableEverything { get; set; } = true;

    public bool AutomaticallyUpdateEverything { get; set; } = true;

    public string EverythingHotKey { get; set; } = string.Empty;

    public string EverythingSdkDirectory { get; set; } = string.Empty;

    public AppThemeMode ThemeMode { get; set; } = AppThemeMode.System;

    public AppSettings Copy() => new()
    {
        StartWithWindows = StartWithWindows,
        MenuHotKey = MenuHotKey,
        Menu2HotKey = Menu2HotKey,
        SettingsHotKey = SettingsHotKey,
        EnableProgramCache = EnableProgramCache,
        EnableEverything = EnableEverything,
        AutomaticallyUpdateEverything = AutomaticallyUpdateEverything,
        EverythingHotKey = EverythingHotKey,
        EverythingSdkDirectory = EverythingSdkDirectory,
        ThemeMode = ThemeMode,
    };
}

public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new AppSettings
            {
                MenuHotKey = Environment.GetEnvironmentVariable("RASHARP_MENU_HOTKEY") ?? "``",
                Menu2HotKey = Environment.GetEnvironmentVariable("RASHARP_MENU2_HOTKEY") ?? string.Empty,
                SettingsHotKey = Environment.GetEnvironmentVariable("RASHARP_SETTINGS_HOTKEY") ?? string.Empty,
            };
        }

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), SerializerOptions)
                ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public static void Save(string path, AppSettings settings)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, SerializerOptions));
        File.Move(temporaryPath, path, true);
    }
}

public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RASharp";

    public static void Apply(bool enabled, string configDirectory)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户启动项注册表。");
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        key.SetValue(ValueName, BuildCommand(configDirectory), RegistryValueKind.String);
    }

    private static string BuildCommand(string configDirectory)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定 RASharp 程序路径。");
        var command = Quote(processPath);
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var entryPath = Environment.GetCommandLineArgs().FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(entryPath)
                && Path.GetExtension(entryPath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                command += " " + Quote(Path.GetFullPath(entryPath));
            }
        }

        return command + " --config-dir " + Quote(configDirectory);
    }

    private static string Quote(string value) => '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"';
}
