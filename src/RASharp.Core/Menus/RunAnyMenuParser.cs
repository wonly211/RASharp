using System.Text.RegularExpressions;
using System.Text;

namespace RASharp.Core.Menus;

public static partial class RunAnyMenuParser
{
    private static readonly string[] ExecutableExtensions =
        [".exe", ".lnk", ".bat", ".cmd", ".vbs", ".ps1", ".ahk"];

    public static MenuDocument ParseFile(string path, int menuNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(ReadConfigurationText(path), path, menuNumber);
    }

    private static string ReadConfigurationText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
        {
            return Encoding.UTF8.GetString(bytes.AsSpan(Encoding.UTF8.Preamble.Length));
        }

        if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble))
        {
            return Encoding.Unicode.GetString(bytes.AsSpan(Encoding.Unicode.Preamble.Length));
        }

        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble))
        {
            return Encoding.BigEndianUnicode.GetString(bytes.AsSpan(Encoding.BigEndianUnicode.Preamble.Length));
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(
                    936,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ReplacementFallback)
                .GetString(bytes);
        }
    }

    public static MenuDocument Parse(string content, string sourcePath, int menuNumber)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfLessThan(menuNumber, 1);

        var root = new MenuCategory(
            menuNumber == 1 ? "RunAny" : $"RunAny{menuNumber}",
            0,
            [],
            null,
            0,
            string.Empty);
        var categories = new Dictionary<int, MenuCategory> { [0] = root };
        var current = root;

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var rawLine = lines[index];
            var line = rawLine.Trim();
            var lineNumber = index + 1;

            if (line.Length == 0 || line.StartsWith(';'))
            {
                continue;
            }

            if (line is "|" or "||")
            {
                current.Children.Add(new MenuSeparator(current.Depth + 1, lineNumber, rawLine));
                continue;
            }

            var categoryMatch = CategoryLineRegex().Match(line);
            if (categoryMatch.Success)
            {
                var depth = categoryMatch.Groups["prefix"].Length;
                var body = categoryMatch.Groups["body"].Value.Trim();
                RemoveDeeperCategories(categories, depth);

                if (body.Length == 0)
                {
                    current = FindParent(categories, depth - 1);
                    current.Children.Add(new MenuSeparator(depth, lineNumber, rawLine));
                    continue;
                }

                var (categoryLabel, selectorText) = SplitOnce(body, '|');
                var (categoryName, categoryHotKey) = SplitHotKey(categoryLabel);
                var selectors = selectorText is null
                    ? Array.Empty<string>()
                    : selectorText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var parent = FindParent(categories, depth - 1);
                var category = new MenuCategory(
                    CleanDisplayName(categoryName),
                    depth,
                    selectors,
                    categoryHotKey,
                    lineNumber,
                    rawLine);
                parent.Children.Add(category);
                categories[depth] = category;
                current = category;
                continue;
            }

            var (labelText, valueText) = SplitOnce(line, '|');
            var (label, hotKey) = SplitHotKey(labelText);
            var (labelWithoutAdministratorMarker, runAsAdministrator) = ParseRunAsAdministrator(label);
            var value = valueText ?? labelWithoutAdministratorMarker;
            var (displayName, hotstring) = ParseHotstring(labelWithoutAdministratorMarker);
            var (kind, normalizedValue) = ParseKind(value);

            if (valueText is null)
            {
                displayName = DeriveDisplayName(normalizedValue);
            }

            current.Children.Add(new MenuEntry(
                CleanDisplayName(displayName),
                normalizedValue,
                kind,
                hotKey,
                hotstring,
                lineNumber,
                rawLine,
                runAsAdministrator));
        }

        return new MenuDocument(sourcePath, menuNumber, root);
    }

    private static (MenuEntryKind Kind, string Value) ParseKind(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.EndsWith(";;", StringComparison.Ordinal))
        {
            return (MenuEntryKind.RawPhrase, trimmed[..^2]);
        }

        if (trimmed.EndsWith(';'))
        {
            return (MenuEntryKind.Phrase, trimmed[..^1]);
        }

        if (trimmed.EndsWith(":::", StringComparison.Ordinal))
        {
            return (MenuEntryKind.RawKeySequence, trimmed[..^3]);
        }

        if (trimmed.EndsWith("::", StringComparison.Ordinal))
        {
            return (MenuEntryKind.KeySequence, trimmed[..^2]);
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
        {
            return (MenuEntryKind.Web, trimmed);
        }

        return (MenuEntryKind.Command, trimmed);
    }

    private static (string DisplayName, HotstringSpec? Hotstring) ParseHotstring(string label)
    {
        var match = HotstringRegex().Match(label);
        if (!match.Success)
        {
            return (label, null);
        }

        var displayName = label[..match.Index].Trim();
        var trigger = match.Groups["trigger"].Value;
        var options = match.Groups["options"].Value;
        return (displayName.Length == 0 ? trigger : displayName, new HotstringSpec(trigger, options));
    }

    private static (string Label, bool RunAsAdministrator) ParseRunAsAdministrator(string label)
    {
        var match = AdministratorMarkerRegex().Match(label);
        return match.Success
            ? (label.Remove(match.Index, match.Length), true)
            : (label, false);
    }

    private static (string Label, string? HotKey) SplitHotKey(string input)
    {
        var tab = input.IndexOf('\t');
        return tab < 0
            ? (input.Trim(), null)
            : (input[..tab].Trim(), NullIfWhiteSpace(input[(tab + 1)..]));
    }

    private static (string Left, string? Right) SplitOnce(string input, char separator)
    {
        var separatorIndex = input.IndexOf(separator);
        return separatorIndex < 0
            ? (input.Trim(), null)
            : (input[..separatorIndex].Trim(), input[(separatorIndex + 1)..].Trim());
    }

    private static MenuCategory FindParent(Dictionary<int, MenuCategory> categories, int requestedDepth)
    {
        for (var depth = Math.Max(0, requestedDepth); depth >= 0; depth--)
        {
            if (categories.TryGetValue(depth, out var category))
            {
                return category;
            }
        }

        throw new InvalidOperationException("The synthetic menu root is missing.");
    }

    private static void RemoveDeeperCategories(IDictionary<int, MenuCategory> categories, int depth)
    {
        foreach (var key in categories.Keys.Where(key => key >= depth).ToArray())
        {
            categories.Remove(key);
        }
    }

    private static string DeriveDisplayName(string value)
    {
        var parsed = ParsedCommand.TryParse(value);
        if (parsed is null)
        {
            return value;
        }

        var fileName = Path.GetFileName(parsed.Executable);
        var extension = Path.GetExtension(fileName);
        return ExecutableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(fileName)
            : fileName;
    }

    private static string CleanDisplayName(string value) =>
        TransparencySuffixRegex().Replace(value.Trim(), string.Empty);

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex("^(?<prefix>-+)(?<body>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex CategoryLineRegex();

    [GeneratedRegex(":(?<options>[*?A-Za-z0-9]*):(?<trigger>[^:]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex HotstringRegex();

    [GeneratedRegex("_:\\d{1,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex TransparencySuffixRegex();

    [GeneratedRegex("\\[#\\](?=(?::[*?A-Za-z0-9]*:[^:]*)?(?:_:\\d{1,3})?$)", RegexOptions.CultureInvariant)]
    private static partial Regex AdministratorMarkerRegex();
}
