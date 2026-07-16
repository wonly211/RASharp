namespace RASharp.Core.Everything;

public static class EverythingSearchQueryBuilder
{
    public static string Build(
        string? text,
        IReadOnlyList<string>? files,
        bool includeFileExtension,
        bool searchFolderContents)
    {
        var values = files is { Count: > 0 } ? files : SplitLines(text);
        return string.Join(
            " | ",
            values
                .Select(value => BuildTerm(value, includeFileExtension, searchFolderContents))
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string[] SplitLines(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string BuildTerm(
        string rawValue,
        bool includeFileExtension,
        bool searchFolderContents)
    {
        var value = rawValue.Trim().Trim('"');
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (Directory.Exists(value))
        {
            var directoryPath = NormalizeDirectoryPath(value);
            return searchFolderContents
                ? $"path:{QuoteAlways(directoryPath)}"
                : Quote(Path.GetFileName(directoryPath) is { Length: > 0 } directoryName
                    ? directoryName
                    : directoryPath);
        }

        if (File.Exists(value) || Path.IsPathRooted(value))
        {
            var fileName = Path.GetFileName(value);
            if (!includeFileExtension)
            {
                fileName = Path.GetFileNameWithoutExtension(fileName);
            }

            return Quote(fileName);
        }

        return Quote(value);
    }

    private static string Quote(string value)
    {
        var escaped = value.Replace("\"", "\\\"", StringComparison.Ordinal);
        return escaped.Any(char.IsWhiteSpace) || escaped.Contains('|')
            ? $"\"{escaped}\""
            : escaped;
    }

    private static string QuoteAlways(string value) =>
        $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string NormalizeDirectoryPath(string value)
    {
        var fullPath = Path.GetFullPath(value);
        var root = Path.GetPathRoot(fullPath);
        return root is not null && fullPath.Equals(root, StringComparison.OrdinalIgnoreCase)
            ? root
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
