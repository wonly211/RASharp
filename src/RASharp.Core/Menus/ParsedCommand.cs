using System.Text.RegularExpressions;

namespace RASharp.Core.Menus;

public sealed partial record ParsedCommand(string Executable, string Arguments)
{
    public static ParsedCommand? TryParse(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var value = commandLine.Trim();
        if (value.StartsWith('"'))
        {
            var closingQuote = value.IndexOf('"', 1);
            if (closingQuote > 1)
            {
                return new ParsedCommand(
                    value[1..closingQuote],
                    value[(closingQuote + 1)..].TrimStart());
            }
        }

        var executableMatch = ExecutableRegex().Match(value);
        if (executableMatch.Success)
        {
            return new ParsedCommand(
                executableMatch.Groups["executable"].Value,
                executableMatch.Groups["arguments"].Value.TrimStart());
        }

        var firstSpace = value.IndexOf(' ');
        return firstSpace < 0
            ? new ParsedCommand(value, string.Empty)
            : new ParsedCommand(value[..firstSpace], value[(firstSpace + 1)..].TrimStart());
    }

    public string WithExecutable(string executable)
    {
        var quotedExecutable = executable.Contains(' ', StringComparison.Ordinal)
            ? $"\"{executable}\""
            : executable;
        return string.IsNullOrWhiteSpace(Arguments)
            ? quotedExecutable
            : $"{quotedExecutable} {Arguments}";
    }

    [GeneratedRegex(
        "^(?<executable>.+?\\.(?:exe|lnk|bat|cmd|vbs|ps1|ahk))(?<arguments>\\s+.*|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExecutableRegex();
}
