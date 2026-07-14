namespace RASharp.Core.Menus;

public enum MenuEntryKind
{
    Command,
    Web,
    Phrase,
    RawPhrase,
    KeySequence,
    RawKeySequence,
}

public sealed record HotstringSpec(string Trigger, string Options)
{
    public bool TriggerImmediately => Options.Contains('*', StringComparison.Ordinal);

    public bool ExecuteAction => Options.Contains('X', StringComparison.OrdinalIgnoreCase);
}

public abstract class MenuElement(int sourceLine, string rawText)
{
    public int SourceLine { get; } = sourceLine;

    public string RawText { get; } = rawText;
}

public sealed class MenuCategory(
    string name,
    int depth,
    IReadOnlyList<string> selectors,
    string? hotKey,
    int sourceLine,
    string rawText) : MenuElement(sourceLine, rawText)
{
    public string Name { get; } = name;

    public int Depth { get; } = depth;

    public IReadOnlyList<string> Selectors { get; } = selectors;

    public string? HotKey { get; } = hotKey;

    public IList<MenuElement> Children { get; } = [];
}

public sealed class MenuEntry(
    string displayName,
    string value,
    MenuEntryKind kind,
    string? hotKey,
    HotstringSpec? hotstring,
    int sourceLine,
    string rawText) : MenuElement(sourceLine, rawText)
{
    public string DisplayName { get; } = displayName;

    public string Value { get; } = value;

    public MenuEntryKind Kind { get; } = kind;

    public string? HotKey { get; } = hotKey;

    public HotstringSpec? Hotstring { get; } = hotstring;

    public string? ResolvedValue { get; set; }

    public bool IsAvailable { get; set; } = true;

    public string? UnavailableReason { get; set; }

    public string EffectiveValue => ResolvedValue ?? Value;
}

public sealed class MenuSeparator(int depth, int sourceLine, string rawText)
    : MenuElement(sourceLine, rawText)
{
    public int Depth { get; } = depth;
}

public sealed record MenuDocument(string SourcePath, int MenuNumber, MenuCategory Root)
{
    public IEnumerable<MenuEntry> Entries => EnumerateEntries(Root);

    private static IEnumerable<MenuEntry> EnumerateEntries(MenuCategory category)
    {
        foreach (var child in category.Children)
        {
            if (child is MenuEntry entry)
            {
                yield return entry;
            }
            else if (child is MenuCategory nested)
            {
                foreach (var nestedEntry in EnumerateEntries(nested))
                {
                    yield return nestedEntry;
                }
            }
        }
    }
}
