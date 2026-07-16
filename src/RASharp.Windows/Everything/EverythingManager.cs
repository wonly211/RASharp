using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RASharp.Windows.Everything;

public sealed partial class EverythingManager(string installationDirectory, Action<string>? diagnosticLog = null)
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromMinutes(3) };
    private static readonly Uri DownloadsPage = new("https://www.voidtools.com/downloads/");
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromDays(1);

    public string InstallationDirectory { get; } = Path.GetFullPath(installationDirectory);

    public string ExecutablePath => Path.Combine(InstallationDirectory, "Everything.exe");

    public string SdkPath => Path.Combine(InstallationDirectory, "Everything64.dll");

    public bool IsInstalled => File.Exists(ExecutablePath);

    public Version? InstalledVersion => IsInstalled
        ? ParseFileVersion(FileVersionInfo.GetVersionInfo(ExecutablePath).FileVersion)
        : null;

    public bool IsManagedServiceInstalled
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Everything");
            var imagePath = key?.GetValue("ImagePath") as string;
            return imagePath?.Contains(ExecutablePath, StringComparison.OrdinalIgnoreCase) == true;
        }
    }

    public string StatusText => !IsInstalled
        ? "尚未安装"
        : $"已安装 {InstalledVersion?.ToString() ?? "未知版本"}；服务{(IsManagedServiceInstalled ? "已配置" : "未配置")}";

    public async Task EnsureReadyAsync(
        bool automaticallyUpdate,
        bool forceUpdate = false,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(InstallationDirectory);
        CopyBundledSdk();

        var installedOrUpdated = false;
        var shouldCheck = forceUpdate || !IsInstalled || (automaticallyUpdate && IsUpdateCheckDue());
        if (shouldCheck)
        {
            var release = await GetLatestStableReleaseAsync(cancellationToken).ConfigureAwait(false);
            if (!IsInstalled || InstalledVersion is null || InstalledVersion < release.Version)
            {
                await InstallReleaseAsync(release, cancellationToken).ConfigureAwait(false);
                installedOrUpdated = true;
            }

            File.WriteAllText(GetUpdateCheckPath(), DateTimeOffset.UtcNow.ToString("O"));
        }

        if (IsInstalled && (installedOrUpdated || !IsManagedServiceInstalled))
        {
            await ConfigureServiceAsync(cancellationToken).ConfigureAwait(false);
        }

        await EnsureManagedClientAsync(cancellationToken).ConfigureAwait(false);
        await WaitForDatabaseAsync(cancellationToken).ConfigureAwait(false);
    }

    public void ShowWindow(string? searchQuery = null)
    {
        if (!IsInstalled)
        {
            throw new InvalidOperationException("Everything 尚未安装。");
        }

        if (string.IsNullOrWhiteSpace(searchQuery) && TryToggleExistingWindow())
        {
            return;
        }

        var startInfo = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = true,
            WorkingDirectory = InstallationDirectory,
        };
        startInfo.ArgumentList.Add("-nonewwindow");
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            startInfo.ArgumentList.Add("-search");
            startInfo.ArgumentList.Add(searchQuery);
        }

        _ = Process.Start(startInfo);
    }

    private static bool TryToggleExistingWindow()
    {
        var window = NativeMethods.FindWindow("EVERYTHING", null);
        if (window == 0)
        {
            return false;
        }

        if (!NativeMethods.IsIconic(window) && NativeMethods.GetForegroundWindow() == window)
        {
            _ = NativeMethods.ShowWindow(window, ShowWindowMinimize);
            return true;
        }

        _ = NativeMethods.ShowWindow(window, ShowWindowRestore);
        _ = NativeMethods.SetForegroundWindow(window);
        return true;
    }

    private const int ShowWindowMinimize = 6;
    private const int ShowWindowRestore = 9;

    public async Task ConfigureServiceAsync(CancellationToken cancellationToken = default)
    {
        if (!IsInstalled)
        {
            throw new InvalidOperationException("Everything 尚未安装。");
        }

        diagnosticLog?.Invoke("everything service configuration requested");
        await RunElevatedAsync(
            ExecutablePath,
            "-install-service -disable-update-notification -uninstall-run-on-system-startup",
            cancellationToken).ConfigureAwait(false);
        diagnosticLog?.Invoke("everything service configuration complete");
    }

    private async Task InstallReleaseAsync(EverythingRelease release, CancellationToken cancellationToken)
    {
        diagnosticLog?.Invoke($"everything download begin version={release.Version}");
        var temporaryDirectory = Path.Combine(
            InstallationDirectory,
            ".update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var archivePath = Path.Combine(temporaryDirectory, release.FileName);
            await using (var output = File.Create(archivePath))
            await using (var input = await HttpClient.GetStreamAsync(release.DownloadUri, cancellationToken).ConfigureAwait(false))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            var expectedHash = await GetExpectedHashAsync(release, cancellationToken).ConfigureAwait(false);
            await using (var stream = File.OpenRead(archivePath))
            {
                var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Everything 下载文件的 SHA-256 校验失败。");
                }
            }

            var stagingDirectory = Path.Combine(temporaryDirectory, "content");
            ZipFile.ExtractToDirectory(archivePath, stagingDirectory);
            var stagedExecutable = Directory.EnumerateFiles(stagingDirectory, "Everything.exe", SearchOption.AllDirectories)
                .SingleOrDefault()
                ?? throw new InvalidDataException("Everything 官方压缩包中缺少 Everything.exe。");

            if (IsInstalled)
            {
                await StopForUpgradeAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var sourcePath in Directory.EnumerateFiles(stagingDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(stagingDirectory, sourcePath);
                var destinationPath = Path.GetFullPath(relativePath, InstallationDirectory);
                if (!destinationPath.StartsWith(InstallationDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Everything 压缩包包含不安全路径。");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                File.Copy(sourcePath, destinationPath, overwrite: true);
            }

            if (!File.Exists(ExecutablePath) || !File.Exists(stagedExecutable))
            {
                throw new InvalidDataException("Everything 安装未完成。");
            }

            CopyBundledSdk();
            diagnosticLog?.Invoke($"everything installed version={InstalledVersion}");
        }
        finally
        {
            try
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch (IOException)
            {
                // A temporary antivirus scan can briefly retain the archive.
            }
            catch (UnauthorizedAccessException)
            {
                // Cleanup failure must not invalidate a successful installation.
            }
        }
    }

    private async Task StopForUpgradeAsync(CancellationToken cancellationToken)
    {
        _ = Process.Start(new ProcessStartInfo(ExecutablePath, "-quit")
        {
            UseShellExecute = true,
            WorkingDirectory = InstallationDirectory,
        });
        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        if (IsManagedServiceInstalled)
        {
            await RunElevatedAsync(ExecutablePath, "-stop-service", cancellationToken).ConfigureAwait(false);
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsureManagedClientAsync(CancellationToken cancellationToken)
    {
        if (!IsInstalled)
        {
            return;
        }

        using var currentProcess = Process.GetCurrentProcess();
        var everythingProcesses = Process.GetProcessesByName("Everything");
        bool hasForeignClient;
        try
        {
            hasForeignClient = everythingProcesses
                .Where(process => process.SessionId == currentProcess.SessionId)
                .Any(process => !IsManagedProcess(process));
        }
        finally
        {
            foreach (var process in everythingProcesses)
            {
                process.Dispose();
            }
        }
        if (hasForeignClient)
        {
            diagnosticLog?.Invoke("foreign everything client detected; migrating to managed client");
            _ = Process.Start(new ProcessStartInfo(ExecutablePath, "-quit")
            {
                UseShellExecute = true,
                WorkingDirectory = InstallationDirectory,
            });
            await Task.Delay(700, cancellationToken).ConfigureAwait(false);
        }

        _ = Process.Start(new ProcessStartInfo(ExecutablePath, "-startup -first-instance")
        {
            UseShellExecute = true,
            WorkingDirectory = InstallationDirectory,
        });
    }

    private bool IsManagedProcess(Process process)
    {
        try
        {
            return process.MainModule?.FileName.Equals(ExecutablePath, StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Win32Exception)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private async Task WaitForDatabaseAsync(CancellationToken cancellationToken)
    {
        using var search = EverythingSearch.TryCreate(InstallationDirectory);
        if (search is null)
        {
            return;
        }

        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (search.IsAvailable)
            {
                diagnosticLog?.Invoke("managed everything database ready");
                return;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        diagnosticLog?.Invoke("managed everything database readiness timed out");
    }

    private void CopyBundledSdk()
    {
        var bundledSdk = Path.Combine(AppContext.BaseDirectory, "Everything64.dll");
        if (!File.Exists(bundledSdk) || bundledSdk.Equals(SdkPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (File.Exists(SdkPath) && FilesHaveSameContent(bundledSdk, SdkPath))
        {
            return;
        }

        try
        {
            File.Copy(bundledSdk, SdkPath, overwrite: true);
        }
        catch (IOException) when (File.Exists(SdkPath))
        {
            diagnosticLog?.Invoke("everything SDK update deferred until restart because the DLL is in use");
        }
    }

    private static bool FilesHaveSameContent(string leftPath, string rightPath)
    {
        if (new FileInfo(leftPath).Length != new FileInfo(rightPath).Length)
        {
            return false;
        }

        using var left = File.OpenRead(leftPath);
        using var right = File.OpenRead(rightPath);
        return SHA256.HashData(left).AsSpan().SequenceEqual(SHA256.HashData(right));
    }

    private bool IsUpdateCheckDue()
    {
        var path = GetUpdateCheckPath();
        if (!File.Exists(path))
        {
            return true;
        }

        return DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(path) >= UpdateInterval;
    }

    private string GetUpdateCheckPath() => Path.Combine(InstallationDirectory, ".last-update-check");

    private static async Task<EverythingRelease> GetLatestStableReleaseAsync(CancellationToken cancellationToken)
    {
        var html = await HttpClient.GetStringAsync(DownloadsPage, cancellationToken).ConfigureAwait(false);
        var releases = StableReleaseRegex().Matches(html)
            .Select(match =>
            {
                var fileName = match.Groups["file"].Value;
                return new EverythingRelease(
                    Version.Parse(match.Groups["version"].Value),
                    new Uri(DownloadsPage, "/" + fileName),
                    fileName);
            })
            .DistinctBy(release => release.Version)
            .OrderByDescending(release => release.Version)
            .ToArray();
        return releases.FirstOrDefault()
            ?? throw new InvalidDataException("无法从 voidtools 官方下载页识别 Everything 稳定版。");
    }

    private static async Task<string> GetExpectedHashAsync(
        EverythingRelease release,
        CancellationToken cancellationToken)
    {
        var hashUri = new Uri(DownloadsPage, $"/Everything-{release.Version}.sha256");
        var content = await HttpClient.GetStringAsync(hashUri, cancellationToken).ConfigureAwait(false);
        var match = Regex.Match(
            content,
            $"(?im)^(?<hash>[a-f0-9]{{64}})\\s+\\*?{Regex.Escape(release.FileName)}\\s*$",
            RegexOptions.CultureInvariant);
        return match.Success
            ? match.Groups["hash"].Value
            : throw new InvalidDataException("无法从 voidtools 官方清单读取 Everything SHA-256。");
    }

    private static async Task RunElevatedAsync(
        string executable,
        string arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(executable),
            }) ?? throw new InvalidOperationException("无法启动 Everything 管理命令。");
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Everything 管理命令失败，退出代码：{process.ExitCode}。");
            }
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("用户取消了 Everything 服务配置的管理员授权。", exception);
        }
    }

    private static Version? ParseFileVersion(string? value)
    {
        var normalized = value?.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Replace(',', '.');
        return Version.TryParse(normalized, out var version) ? version : null;
    }

    [GeneratedRegex(
        "(?<file>Everything-(?<version>\\d+\\.\\d+\\.\\d+\\.\\d+)\\.x64\\.zip)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StableReleaseRegex();

    private sealed record EverythingRelease(Version Version, Uri DownloadUri, string FileName);

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial nint FindWindow(string className, string? windowName);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsIconic(nint window);

        [LibraryImport("user32.dll")]
        internal static partial nint GetForegroundWindow();

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ShowWindow(nint window, int command);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetForegroundWindow(nint window);
    }
}
