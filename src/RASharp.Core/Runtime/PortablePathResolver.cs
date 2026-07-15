namespace RASharp.Core.Runtime;

public static class PortablePathResolver
{
    public static string GetExecutableDirectory() =>
        ResolveExecutableDirectory(Environment.ProcessPath, Environment.GetCommandLineArgs());

    public static string ResolveExecutableDirectory(
        string? processPath,
        IReadOnlyList<string> commandLineArguments)
    {
        var executablePath = processPath;
        if (string.IsNullOrWhiteSpace(executablePath)
            || Path.GetFileNameWithoutExtension(executablePath)
                .Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var entryPath = commandLineArguments.Count > 0
                ? commandLineArguments[0]
                : null;
            if (!string.IsNullOrWhiteSpace(entryPath))
            {
                executablePath = entryPath;
            }
        }

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("无法确定 RASharp 程序路径。");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
        return !string.IsNullOrWhiteSpace(directory)
            ? directory
            : throw new InvalidOperationException("无法确定 RASharp 程序目录。");
    }
}
