using System.Text.Json;
using RASharp.Core.Menus;

namespace RASharp.Core.Programs;

public sealed record ProgramCandidate(string Path, Version? Version, DateTime LastWriteTimeUtc);

public interface IProgramSearch
{
    bool IsAvailable { get; }

    Task<IReadOnlyList<ProgramCandidate>> FindExactFileNameAsync(
        string fileName,
        CancellationToken cancellationToken = default);
}

public interface IProgramResolver
{
    Task<string?> ResolveAsync(string executable, CancellationToken cancellationToken = default);
}

public interface IProgramCandidateSelector
{
    Task<ProgramCandidate?> SelectAsync(
        string executable,
        IReadOnlyList<ProgramCandidate> candidates,
        CancellationToken cancellationToken = default);
}

public sealed class CachedProgramResolver(
    IProgramSearch search,
    string cachePath,
    bool enableCache = true,
    IProgramCandidateSelector? candidateSelector = null) : IProgramResolver, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim cacheLock = new(1, 1);
    private readonly HashSet<string> declinedSelections = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, CacheEntry>? cache;

    public async Task<string?> ResolveAsync(string executable, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return null;
        }

        if (Path.IsPathRooted(executable) || File.Exists(executable))
        {
            return executable;
        }

        var knownLocation = TryResolveKnownLocation(executable);
        if (knownLocation is not null)
        {
            return knownLocation;
        }

        var key = executable.Trim().ToUpperInvariant();
        if (declinedSelections.Contains(key))
        {
            return null;
        }

        if (enableCache)
        {
            await EnsureCacheLoadedAsync(cancellationToken).ConfigureAwait(false);
            if (cache!.TryGetValue(key, out var cached) && File.Exists(cached.Path))
            {
                return cached.Path;
            }
        }

        if (!search.IsAvailable)
        {
            return null;
        }

        var candidates = await search.FindExactFileNameAsync(executable, cancellationToken).ConfigureAwait(false);
        var validCandidates = candidates
            .Where(candidate => File.Exists(candidate.Path))
            .OrderByDescending(candidate => candidate.Version, VersionComparer.Instance)
            .ThenByDescending(candidate => candidate.LastWriteTimeUtc)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var selected = validCandidates.Length switch
        {
            0 => null,
            1 => validCandidates[0],
            _ when candidateSelector is not null => await candidateSelector
                .SelectAsync(executable, validCandidates, cancellationToken)
                .ConfigureAwait(false),
            _ => validCandidates[0],
        };

        if (selected is null)
        {
            if (validCandidates.Length > 1 && candidateSelector is not null)
            {
                declinedSelections.Add(key);
            }

            return null;
        }

        if (enableCache)
        {
            cache![key] = new CacheEntry(selected.Path, DateTime.UtcNow);
            await SaveCacheAsync(cancellationToken).ConfigureAwait(false);
        }

        return selected.Path;
    }

    private async Task EnsureCacheLoadedAsync(CancellationToken cancellationToken)
    {
        if (cache is not null)
        {
            return;
        }

        await cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cache is not null)
            {
                return;
            }

            if (!File.Exists(cachePath))
            {
                cache = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
                return;
            }

            await using var stream = File.OpenRead(cachePath);
            cache = await JsonSerializer.DeserializeAsync<Dictionary<string, CacheEntry>>(
                    stream,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false)
                ?? new Dictionary<string, CacheEntry>();
            cache = new Dictionary<string, CacheEntry>(cache, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            cache = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            cacheLock.Release();
        }
    }

    private static string? TryResolveKnownLocation(string executable)
    {
        if (!string.IsNullOrWhiteSpace(Path.GetDirectoryName(executable)))
        {
            return null;
        }

        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var directories = new List<string>
        {
            Environment.SystemDirectory,
            windowsDirectory,
        };
        var pathDirectories = Environment.GetEnvironmentVariable("PATH")
            ?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [];
        directories.AddRange(pathDirectories);

        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(directory.Trim('"'), executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task SaveCacheAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var temporaryPath = cachePath + ".tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        cache,
                        SerializerOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporaryPath, cachePath, true);
        }
        finally
        {
            cacheLock.Release();
        }
    }

    public void Dispose() => cacheLock.Dispose();

    private sealed record CacheEntry(string Path, DateTime CachedAtUtc);

    private sealed class VersionComparer : IComparer<Version?>
    {
        public static VersionComparer Instance { get; } = new();

        public int Compare(Version? left, Version? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            return right is null ? 1 : left.CompareTo(right);
        }
    }
}

public sealed class MenuResolutionService(IProgramResolver resolver)
{
    public async Task ResolveAsync(
        MenuDocument document,
        VariableExpander variableExpander,
        CancellationToken cancellationToken = default)
    {
        foreach (var entry in document.Entries.Where(entry => entry.Kind == MenuEntryKind.Command))
        {
            entry.IsAvailable = true;
            entry.UnavailableReason = null;
            var expanded = variableExpander.Expand(entry.Value);
            var parsed = ParsedCommand.TryParse(expanded);
            if (parsed is null)
            {
                MarkUnavailable(entry, "命令格式无效");
                continue;
            }

            if (IsShellCommand(parsed.Executable))
            {
                entry.ResolvedValue = expanded;
                continue;
            }

            if (Path.IsPathRooted(parsed.Executable))
            {
                if (File.Exists(parsed.Executable) || Directory.Exists(parsed.Executable))
                {
                    entry.ResolvedValue = parsed.WithExecutable(parsed.Executable);
                }
                else
                {
                    MarkUnavailable(entry, "路径不存在");
                }

                continue;
            }

            if (!string.IsNullOrWhiteSpace(Path.GetDirectoryName(parsed.Executable)))
            {
                var absolutePath = Path.GetFullPath(parsed.Executable, variableExpander.ConfigDirectory);
                if (File.Exists(absolutePath) || Directory.Exists(absolutePath))
                {
                    entry.ResolvedValue = parsed.WithExecutable(absolutePath);
                }
                else
                {
                    MarkUnavailable(entry, "相对路径不存在");
                }

                continue;
            }

            var resolved = await resolver.ResolveAsync(parsed.Executable, cancellationToken).ConfigureAwait(false);
            if (resolved is not null)
            {
                entry.ResolvedValue = parsed.WithExecutable(resolved);
            }
            else
            {
                MarkUnavailable(entry, "Windows PATH 和 Everything 均未找到");
            }
        }
    }

    private static bool IsShellCommand(string executable) =>
        executable.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
        || executable.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)
        || executable.StartsWith("::{", StringComparison.Ordinal);

    private static void MarkUnavailable(MenuEntry entry, string reason)
    {
        entry.IsAvailable = false;
        entry.UnavailableReason = reason;
        entry.ResolvedValue = null;
    }
}
