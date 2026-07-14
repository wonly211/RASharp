using RASharp.Core.Programs;
using RASharp.Core.Menus;

namespace RASharp.Core.Tests;

public sealed class ProgramResolverTests
{
    [Fact]
    public async Task ResolveAsyncPrefersVersionThenModificationTimeAndUsesCache()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RASharp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var older = Path.Combine(directory, "older.exe");
            var newer = Path.Combine(directory, "newer.exe");
            await File.WriteAllTextAsync(older, string.Empty);
            await File.WriteAllTextAsync(newer, string.Empty);
            var search = new FakeSearch([
                new ProgramCandidate(older, new Version(1, 0), DateTime.UtcNow),
                new ProgramCandidate(newer, new Version(2, 0), DateTime.UtcNow.AddDays(-1)),
            ]);
            var resolver = new CachedProgramResolver(search, Path.Combine(directory, "cache.json"));

            var first = await resolver.ResolveAsync("tool.exe");
            var second = await resolver.ResolveAsync("tool.exe");

            Assert.Equal(newer, first);
            Assert.Equal(newer, second);
            Assert.Equal(1, search.CallCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MenuResolutionMarksMissingCommandsUnavailable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RASharp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var existing = Path.Combine(directory, "existing.exe");
            await File.WriteAllTextAsync(existing, string.Empty);
            var document = RunAnyMenuParser.Parse(
                $"-工具{Environment.NewLine}存在|.\\existing.exe{Environment.NewLine}缺失|missing-rasharp-test.exe{Environment.NewLine}设置|ms-settings:display",
                "RunAny.ini",
                1);
            var search = new FakeSearch([]);
            using var resolver = new CachedProgramResolver(search, Path.Combine(directory, "cache.json"));

            await new MenuResolutionService(resolver)
                .ResolveAsync(document, new VariableExpander(directory));

            var entries = document.Entries.ToArray();
            Assert.True(entries[0].IsAvailable);
            Assert.Equal(existing, ParsedCommand.TryParse(entries[0].EffectiveValue)?.Executable);
            Assert.False(entries[1].IsAvailable);
            Assert.NotNull(entries[1].UnavailableReason);
            Assert.True(entries[2].IsAvailable);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResolverCanDisablePersistentCache()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RASharp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Combine(directory, "tool.exe");
            await File.WriteAllTextAsync(executable, string.Empty);
            var search = new FakeSearch([new ProgramCandidate(executable, null, DateTime.UtcNow)]);
            var cachePath = Path.Combine(directory, "cache.json");
            using var resolver = new CachedProgramResolver(search, cachePath, enableCache: false);

            _ = await resolver.ResolveAsync("tool.exe");
            _ = await resolver.ResolveAsync("tool.exe");

            Assert.Equal(2, search.CallCount);
            Assert.False(File.Exists(cachePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsyncLetsUserChooseWhenMultipleCandidatesExistAndCachesChoice()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RASharp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var first = Path.Combine(directory, "first.exe");
            var chosen = Path.Combine(directory, "chosen.exe");
            await File.WriteAllTextAsync(first, string.Empty);
            await File.WriteAllTextAsync(chosen, string.Empty);
            var search = new FakeSearch([
                new ProgramCandidate(first, new Version(2, 0), DateTime.UtcNow),
                new ProgramCandidate(chosen, new Version(1, 0), DateTime.UtcNow.AddDays(-1)),
            ]);
            var selector = new FakeSelector(candidate => candidate.Single(item => item.Path == chosen));
            using var resolver = new CachedProgramResolver(
                search,
                Path.Combine(directory, "cache.json"),
                candidateSelector: selector);

            var firstResult = await resolver.ResolveAsync("tool.exe");
            var cachedResult = await resolver.ResolveAsync("tool.exe");

            Assert.Equal(chosen, firstResult);
            Assert.Equal(chosen, cachedResult);
            Assert.Equal(1, selector.CallCount);
            Assert.Equal(1, search.CallCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveAsyncDoesNotPromptAgainAfterUserCancelsDuringSameReload()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RASharp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var first = Path.Combine(directory, "first.exe");
            var second = Path.Combine(directory, "second.exe");
            await File.WriteAllTextAsync(first, string.Empty);
            await File.WriteAllTextAsync(second, string.Empty);
            var search = new FakeSearch([
                new ProgramCandidate(first, null, DateTime.UtcNow),
                new ProgramCandidate(second, null, DateTime.UtcNow),
            ]);
            var selector = new FakeSelector(_ => null);
            using var resolver = new CachedProgramResolver(
                search,
                Path.Combine(directory, "cache.json"),
                candidateSelector: selector);

            Assert.Null(await resolver.ResolveAsync("tool.exe"));
            Assert.Null(await resolver.ResolveAsync("tool.exe"));
            Assert.Equal(1, selector.CallCount);
            Assert.Equal(1, search.CallCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FakeSearch(IReadOnlyList<ProgramCandidate> candidates) : IProgramSearch
    {
        public bool IsAvailable => true;

        public int CallCount { get; private set; }

        public Task<IReadOnlyList<ProgramCandidate>> FindExactFileNameAsync(
            string fileName,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(candidates);
        }
    }

    private sealed class FakeSelector(Func<IReadOnlyList<ProgramCandidate>, ProgramCandidate?> select)
        : IProgramCandidateSelector
    {
        public int CallCount { get; private set; }

        public Task<ProgramCandidate?> SelectAsync(
            string executable,
            IReadOnlyList<ProgramCandidate> candidates,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(select(candidates));
        }
    }
}
