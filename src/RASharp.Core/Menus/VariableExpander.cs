using System.Text.RegularExpressions;
using System.Globalization;

namespace RASharp.Core.Menus;

public sealed partial class VariableExpander(string configDirectory)
{
    public string ConfigDirectory { get; } = configDirectory;

    public string Expand(string input, string? selectedText = null, bool encodeSelectedText = false)
    {
        ArgumentNullException.ThrowIfNull(input);

        var selectedValue = selectedText is null
            ? null
            : encodeSelectedText ? Uri.EscapeDataString(selectedText) : selectedText;
        var value = selectedValue is null
            ? input
            : input.Replace("%s", selectedValue, StringComparison.OrdinalIgnoreCase);

        return VariableRegex().Replace(value, match =>
        {
            var name = match.Groups["name"].Value;
            if (name.Equals("s", StringComparison.OrdinalIgnoreCase)
                || name.Equals("getZz", StringComparison.OrdinalIgnoreCase))
            {
                if (selectedText is null)
                {
                    return match.Value;
                }

                return selectedValue ?? match.Value;
            }

            return ResolveVariable(name) ?? match.Value;
        });
    }

    private string? ResolveVariable(string name)
    {
        var now = DateTime.Now;
        return name.ToUpperInvariant() switch
        {
            "A_SCRIPTDIR" => ConfigDirectory,
            "A_DESKTOP" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "A_MYDOCUMENTS" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "A_WINDIR" or "WINDIR" => Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "PROGRAMFILES" => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PROGRAMW6432" => Environment.GetEnvironmentVariable("ProgramW6432"),
            "APPDATA" => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LOCALAPPDATA" => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "USERPROFILE" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "USERNAME" => Environment.UserName,
            "COMPUTERNAME" => Environment.MachineName,
            "A_YYYY" => now.ToString("yyyy", CultureInfo.InvariantCulture),
            "A_MM" => now.ToString("MM", CultureInfo.InvariantCulture),
            "A_DD" => now.ToString("dd", CultureInfo.InvariantCulture),
            "A_HOUR" => now.ToString("HH", CultureInfo.InvariantCulture),
            "A_MIN" => now.ToString("mm", CultureInfo.InvariantCulture),
            "A_SEC" => now.ToString("ss", CultureInfo.InvariantCulture),
            _ => Environment.GetEnvironmentVariable(name),
        };
    }

    [GeneratedRegex("%(?<name>[^%]+)%", RegexOptions.CultureInvariant)]
    private static partial Regex VariableRegex();
}
