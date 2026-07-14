using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using RASharp.Core.Programs;

namespace RASharp.Windows.Everything;

public sealed class EverythingSearch : IProgramSearch, IDisposable
{
    private const int MaximumResults = 4096;
    private readonly object queryLock = new();
    private readonly nint module;
    private readonly SetSearchDelegate setSearch;
    private readonly SetBooleanDelegate setMatchCase;
    private readonly SetBooleanDelegate setMatchWholeWord;
    private readonly SetMaximumDelegate setMaximum;
    private readonly QueryDelegate query;
    private readonly GetCountDelegate getFileResultCount;
    private readonly GetFullPathDelegate getFullPath;
    private readonly GetBooleanDelegate isDatabaseLoaded;
    private bool disposed;

    private EverythingSearch(nint module)
    {
        this.module = module;
        setSearch = GetExport<SetSearchDelegate>("Everything_SetSearchW");
        setMatchCase = GetExport<SetBooleanDelegate>("Everything_SetMatchCase");
        setMatchWholeWord = GetExport<SetBooleanDelegate>("Everything_SetMatchWholeWord");
        setMaximum = GetExport<SetMaximumDelegate>("Everything_SetMax");
        query = GetExport<QueryDelegate>("Everything_QueryW");
        getFileResultCount = GetExport<GetCountDelegate>("Everything_GetNumFileResults");
        getFullPath = GetExport<GetFullPathDelegate>("Everything_GetResultFullPathNameW");
        isDatabaseLoaded = GetExport<GetBooleanDelegate>("Everything_IsDBLoaded");
    }

    public bool IsAvailable => !disposed && module != 0 && isDatabaseLoaded();

    public string LibraryPath { get; private init; } = string.Empty;

    public static EverythingSearch? TryCreate(params string[] searchDirectories)
    {
        var fileName = Environment.Is64BitProcess ? "Everything64.dll" : "Everything.dll";
        var directories = searchDirectories
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Append(AppContext.BaseDirectory)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in directories)
        {
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var module))
            {
                try
                {
                    return new EverythingSearch(module) { LibraryPath = path };
                }
                catch
                {
                    NativeLibrary.Free(module);
                    throw;
                }
            }
        }

        return null;
    }

    public Task<IReadOnlyList<ProgramCandidate>> FindExactFileNameAsync(
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run<IReadOnlyList<ProgramCandidate>>(() =>
        {
            lock (queryLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var escapedName = Regex.Escape(Path.GetFileName(fileName));
                setMatchCase(false);
                setMatchWholeWord(false);
                setMaximum(MaximumResults);
                setSearch($"file: regex:\"^{escapedName}$\"");
                if (!query(true))
                {
                    return [];
                }

                var results = new List<ProgramCandidate>();
                var resultCount = Math.Min(getFileResultCount(), MaximumResults);
                for (uint index = 0; index < resultCount; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var buffer = new StringBuilder(32768);
                    if (getFullPath(index, buffer, (uint)buffer.Capacity) == 0)
                    {
                        continue;
                    }

                    var path = buffer.ToString();
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    results.Add(new ProgramCandidate(
                        path,
                        TryReadVersion(path),
                        File.GetLastWriteTimeUtc(path)));
                }

                return results;
            }
        }, cancellationToken);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        NativeLibrary.Free(module);
        disposed = true;
    }

    private TDelegate GetExport<TDelegate>(string name)
        where TDelegate : Delegate =>
        Marshal.GetDelegateForFunctionPointer<TDelegate>(NativeLibrary.GetExport(module, name));

    private static Version? TryReadVersion(string path)
    {
        try
        {
            var rawVersion = FileVersionInfo.GetVersionInfo(path).FileVersion;
            var normalized = rawVersion?.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]
                .Replace(',', '.');
            return Version.TryParse(normalized, out var version) ? version : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate void SetSearchDelegate([MarshalAs(UnmanagedType.LPWStr)] string value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void SetBooleanDelegate([MarshalAs(UnmanagedType.Bool)] bool value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void SetMaximumDelegate(uint value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool QueryDelegate([MarshalAs(UnmanagedType.Bool)] bool wait);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetCountDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool GetBooleanDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate uint GetFullPathDelegate(uint index, StringBuilder buffer, uint bufferLength);
}
