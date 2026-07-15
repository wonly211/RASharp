using System.Diagnostics;
using System.IO;
using RASharp.Core.Menus;
using RASharp.Core.Programs;
using RASharp.Windows.Clipboard;
using RASharp.Windows.Input;

namespace RASharp.Windows.Execution;

public sealed class MenuEntryExecutor(
    VariableExpander variableExpander,
    IProgramResolver programResolver)
{
    public async Task ExecuteAsync(
        MenuEntry entry,
        SelectedContent? selectedContent = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        selectedContent ??= SelectedContent.Empty;

        switch (entry.Kind)
        {
            case MenuEntryKind.Phrase:
            case MenuEntryKind.RawPhrase:
                KeyboardInput.SendUnicodeText(variableExpander.Expand(entry.Value));
                return;

            case MenuEntryKind.KeySequence:
            case MenuEntryKind.RawKeySequence:
                KeyboardInput.SendAhkSequence(variableExpander.Expand(entry.Value));
                return;

            case MenuEntryKind.Web:
                LaunchWeb(entry.Value, selectedContent);
                return;

            case MenuEntryKind.Command:
                await LaunchCommandAsync(entry, selectedContent, cancellationToken).ConfigureAwait(false);
                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(entry), entry.Kind, "Unsupported menu entry kind.");
        }
    }

    private void LaunchWeb(string rawUrl, SelectedContent selectedContent)
    {
        var hasPlaceholder = ContainsSelectionPlaceholder(rawUrl);
        var selected = selectedContent.PrimaryText;
        var url = variableExpander.Expand(rawUrl, selected, encodeSelectedText: true);
        if (!hasPlaceholder && !string.IsNullOrWhiteSpace(selected))
        {
            url += Uri.EscapeDataString(selected);
        }

        _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async Task LaunchCommandAsync(
        MenuEntry entry,
        SelectedContent selectedContent,
        CancellationToken cancellationToken)
    {
        var rawCommand = entry.EffectiveValue;
        var hasPlaceholder = ContainsSelectionPlaceholder(rawCommand);
        var selectedValue = selectedContent.HasFiles
            ? string.Join(' ', selectedContent.Files.Select(QuoteArgument))
            : selectedContent.Text;
        var expanded = variableExpander.Expand(rawCommand, selectedValue);
        var parsed = ParsedCommand.TryParse(expanded)
            ?? throw new InvalidOperationException($"Cannot parse command: {expanded}");

        var executable = parsed.Executable;
        if (!Path.IsPathRooted(executable) && !File.Exists(executable))
        {
            executable = await programResolver.ResolveAsync(executable, cancellationToken).ConfigureAwait(false)
                ?? executable;
        }

        var arguments = parsed.Arguments;
        if (!hasPlaceholder && selectedContent.HasFiles)
        {
            arguments = string.Join(' ', new[] { arguments }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Append(string.Join(' ', selectedContent.Files.Select(QuoteArgument))));
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            Arguments = arguments,
            UseShellExecute = true,
            Verb = entry.RunAsAdministrator ? "runas" : string.Empty,
        };
        var directory = Path.GetDirectoryName(executable);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            startInfo.WorkingDirectory = directory;
        }

        _ = Process.Start(startInfo);
    }

    private static bool ContainsSelectionPlaceholder(string value) =>
        value.Contains("%s", StringComparison.OrdinalIgnoreCase)
        || value.Contains("%getZz%", StringComparison.OrdinalIgnoreCase);

    private static string QuoteArgument(string value) =>
        $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
