using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RASharp.Core.Menus;

public enum MenuEditorNodeKind
{
    Category,
    Entry,
    Separator,
}

public sealed class MenuEditorNode : INotifyPropertyChanged
{
    private string name = string.Empty;
    private string value = string.Empty;

    private MenuEditorNode(MenuEditorNodeKind kind)
    {
        Kind = kind;
    }

    public MenuEditorNodeKind Kind { get; }

    public string Name
    {
        get => name;
        set
        {
            if (name.Equals(value, StringComparison.Ordinal))
            {
                return;
            }

            name = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayText));
        }
    }

    public string Value
    {
        get => this.value;
        set
        {
            if (this.value.Equals(value, StringComparison.Ordinal))
            {
                return;
            }

            this.value = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayText));
        }
    }

    public MenuEntryKind EntryKind { get; set; } = MenuEntryKind.Command;

    public string HotKey { get; set; } = string.Empty;

    public string HotstringTrigger { get; set; } = string.Empty;

    public string HotstringOptions { get; set; } = string.Empty;

    public string Selectors { get; set; } = string.Empty;

    public bool RunAsAdministrator { get; set; }

    public int? Transparency { get; set; }

    public IList<MenuEditorNode> Children { get; } = new ObservableCollection<MenuEditorNode>();

    public MenuEditorNode? Parent { get; internal set; }

    internal IList<string> LeadingLines { get; } = new List<string>();

    internal string OriginalLine { get; private set; } = string.Empty;

    internal int OriginalDepth { get; private set; }

    internal bool IsModified { get; set; }

    public string DisplayText => Kind switch
    {
        MenuEditorNodeKind.Category => Name,
        MenuEditorNodeKind.Entry => Name,
        _ => "──────── 分隔线 ────────",
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    public static MenuEditorNode CreateCategory(string name = "新分类") => new(MenuEditorNodeKind.Category)
    {
        Name = name,
        IsModified = true,
    };

    public static MenuEditorNode CreateEntry(string name = "新菜单项", string value = "notepad.exe") => new(MenuEditorNodeKind.Entry)
    {
        Name = name,
        Value = value,
        IsModified = true,
    };

    public static MenuEditorNode CreateSeparator() => new(MenuEditorNodeKind.Separator)
    {
        IsModified = true,
    };

    internal static MenuEditorNode FromElement(MenuElement element)
    {
        var node = element switch
        {
            MenuCategory category => new MenuEditorNode(MenuEditorNodeKind.Category)
            {
                Name = category.Name,
                HotKey = category.HotKey ?? string.Empty,
                Selectors = string.Join(' ', category.Selectors),
                OriginalDepth = category.Depth,
            },
            MenuEntry entry => new MenuEditorNode(MenuEditorNodeKind.Entry)
            {
                Name = entry.DisplayName,
                Value = entry.Value,
                EntryKind = entry.Kind,
                HotKey = entry.HotKey ?? string.Empty,
                HotstringTrigger = entry.Hotstring?.Trigger ?? string.Empty,
                HotstringOptions = entry.Hotstring?.Options ?? string.Empty,
                RunAsAdministrator = entry.RunAsAdministrator,
                Transparency = ParseTransparency(entry.RawText),
            },
            MenuSeparator => new MenuEditorNode(MenuEditorNodeKind.Separator),
            _ => throw new ArgumentOutOfRangeException(nameof(element)),
        };
        node.OriginalLine = element.RawText;
        return node;
    }

    internal string BuildLine(int depth)
    {
        if (!IsModified && (Kind != MenuEditorNodeKind.Category || OriginalDepth == depth))
        {
            return OriginalLine;
        }

        if (Kind == MenuEditorNodeKind.Separator)
        {
            return "|";
        }

        var label = Name.Trim();
        if (Kind == MenuEditorNodeKind.Entry)
        {
            if (RunAsAdministrator)
            {
                label += "[#]";
            }

            if (HotstringTrigger.Length > 0)
            {
                label += $":{HotstringOptions}:{HotstringTrigger}";
            }

            if (Transparency is not null)
            {
                label += $"_:{Transparency.Value}";
            }
        }

        if (HotKey.Length > 0)
        {
            label += '\t' + HotKey.Trim();
        }

        if (Kind == MenuEditorNodeKind.Category)
        {
            var selectorSuffix = Selectors.Length > 0 ? '|' + Selectors.Trim() : string.Empty;
            return new string('-', depth) + label + selectorSuffix;
        }

        return label + '|' + Value.Trim() + GetValueSuffix(EntryKind);
    }

    private static string GetValueSuffix(MenuEntryKind kind) => kind switch
    {
        MenuEntryKind.Phrase => ";",
        MenuEntryKind.RawPhrase => ";;",
        MenuEntryKind.KeySequence => "::",
        MenuEntryKind.RawKeySequence => ":::",
        _ => string.Empty,
    };

    private static int? ParseTransparency(string rawLine)
    {
        var label = rawLine.Split('|', 2)[0].Split('\t', 2)[0];
        var marker = label.LastIndexOf("_:", StringComparison.Ordinal);
        return marker >= 0 && int.TryParse(label[(marker + 2)..], out var value)
            ? value
            : null;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class MenuEditorDocument
{
    private readonly List<string> trailingLines = [];

    private MenuEditorDocument(string sourcePath, int menuNumber, string newLine, bool endsWithNewLine)
    {
        SourcePath = sourcePath;
        MenuNumber = menuNumber;
        NewLine = newLine;
        EndsWithNewLine = endsWithNewLine;
    }

    public string SourcePath { get; }

    public int MenuNumber { get; }

    public string NewLine { get; }

    public bool EndsWithNewLine { get; }

    public IList<MenuEditorNode> Children { get; } = new ObservableCollection<MenuEditorNode>();

    public bool IsDirty { get; private set; }

    public static MenuEditorDocument Parse(string text, string sourcePath, int menuNumber)
    {
        ArgumentNullException.ThrowIfNull(text);
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var endsWithNewLine = normalized.EndsWith('\n');
        var lines = normalized.Split('\n').ToList();
        if (endsWithNewLine)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var document = new MenuEditorDocument(sourcePath, menuNumber, newLine, endsWithNewLine);
        var parsed = RunAnyMenuParser.Parse(text, sourcePath, menuNumber);
        var previousSourceLine = 0;
        foreach (var element in parsed.Root.Children)
        {
            document.Children.Add(ConvertElement(element, null, lines, ref previousSourceLine));
        }

        foreach (var line in lines.Skip(previousSourceLine))
        {
            document.trailingLines.Add(line);
        }

        return document;
    }

    public void Add(MenuEditorNode node, MenuEditorNode? parent = null, int? index = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (parent is not null && parent.Kind != MenuEditorNodeKind.Category)
        {
            parent = parent.Parent;
        }

        var target = parent?.Children ?? Children;
        node.Parent = parent;
        var insertionIndex = Math.Clamp(index ?? target.Count, 0, target.Count);
        if (parent is null)
        {
            var firstCategoryIndex = GetFirstCategoryIndex(target);
            insertionIndex = node.Kind == MenuEditorNodeKind.Category
                ? Math.Max(insertionIndex, firstCategoryIndex)
                : Math.Min(insertionIndex, firstCategoryIndex);
        }

        target.Insert(insertionIndex, node);
        IsDirty = true;
    }

    public bool Remove(MenuEditorNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var siblings = node.Parent?.Children ?? Children;
        var removed = siblings.Remove(node);
        if (removed)
        {
            node.Parent = null;
            IsDirty = true;
        }

        return removed;
    }

    public bool Move(MenuEditorNode node, int offset)
    {
        ArgumentNullException.ThrowIfNull(node);
        var siblings = node.Parent?.Children ?? Children;
        var index = siblings.IndexOf(node);
        return index >= 0 && MoveWithinSiblings(node, index + offset);
    }

    public bool MoveWithinSiblings(MenuEditorNode node, int destinationIndex)
    {
        ArgumentNullException.ThrowIfNull(node);
        var siblings = node.Parent?.Children ?? Children;
        var index = siblings.IndexOf(node);
        if (index < 0)
        {
            return false;
        }

        var minimumIndex = 0;
        var maximumIndex = siblings.Count - 1;
        if (node.Parent is null)
        {
            var rootEntryCount = siblings.Count(item => item.Kind != MenuEditorNodeKind.Category);
            if (node.Kind == MenuEditorNodeKind.Category)
            {
                minimumIndex = rootEntryCount;
            }
            else
            {
                maximumIndex = rootEntryCount - 1;
            }
        }

        var destination = Math.Clamp(destinationIndex, minimumIndex, maximumIndex);
        if (destination == index)
        {
            return false;
        }

        siblings.RemoveAt(index);
        siblings.Insert(destination, node);
        IsDirty = true;
        return true;
    }

    public bool MoveTo(MenuEditorNode node, MenuEditorNode? newParent, int index)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (newParent is not null && newParent.Kind != MenuEditorNodeKind.Category)
        {
            return false;
        }

        for (var ancestor = newParent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ReferenceEquals(ancestor, node))
            {
                return false;
            }
        }

        var oldSiblings = node.Parent?.Children ?? Children;
        var oldIndex = oldSiblings.IndexOf(node);
        if (oldIndex < 0)
        {
            return false;
        }

        var newSiblings = newParent?.Children ?? Children;
        var insertionIndex = Math.Clamp(index, 0, newSiblings.Count);
        if (ReferenceEquals(oldSiblings, newSiblings) && insertionIndex > oldIndex)
        {
            insertionIndex--;
        }

        oldSiblings.RemoveAt(oldIndex);
        if (newParent is null)
        {
            var firstCategoryIndex = GetFirstCategoryIndex(newSiblings);
            insertionIndex = node.Kind == MenuEditorNodeKind.Category
                ? Math.Max(insertionIndex, firstCategoryIndex)
                : Math.Min(insertionIndex, firstCategoryIndex);
        }

        insertionIndex = Math.Clamp(insertionIndex, 0, newSiblings.Count);
        newSiblings.Insert(insertionIndex, node);
        node.Parent = newParent;
        IsDirty = true;
        return true;
    }

    public void MarkDirty(MenuEditorNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        node.IsModified = true;
        IsDirty = true;
    }

    public string Serialize()
    {
        var lines = new List<string>();
        foreach (var node in Children)
        {
            AppendNode(lines, node, 0);
        }

        lines.AddRange(trailingLines);
        var text = string.Join(NewLine, lines);
        return EndsWithNewLine || IsDirty ? text + NewLine : text;
    }

    public void AcceptChanges()
    {
        IsDirty = false;
    }

    private static MenuEditorNode ConvertElement(
        MenuElement element,
        MenuEditorNode? parent,
        IReadOnlyList<string> sourceLines,
        ref int previousSourceLine)
    {
        var node = MenuEditorNode.FromElement(element);
        node.Parent = parent;
        foreach (var line in sourceLines
                     .Skip(previousSourceLine)
                     .Take(Math.Max(0, element.SourceLine - previousSourceLine - 1)))
        {
            node.LeadingLines.Add(line);
        }

        previousSourceLine = element.SourceLine;
        if (element is MenuCategory category)
        {
            foreach (var child in category.Children)
            {
                node.Children.Add(ConvertElement(child, node, sourceLines, ref previousSourceLine));
            }
        }

        return node;
    }

    private static void AppendNode(ICollection<string> lines, MenuEditorNode node, int parentDepth)
    {
        foreach (var leadingLine in node.LeadingLines)
        {
            lines.Add(leadingLine);
        }

        var depth = node.Kind == MenuEditorNodeKind.Category ? parentDepth + 1 : parentDepth;
        lines.Add(node.BuildLine(depth));
        foreach (var child in node.Children)
        {
            AppendNode(lines, child, depth);
        }
    }

    private static int GetFirstCategoryIndex(IList<MenuEditorNode> nodes)
    {
        for (var index = 0; index < nodes.Count; index++)
        {
            if (nodes[index].Kind == MenuEditorNodeKind.Category)
            {
                return index;
            }
        }

        return nodes.Count;
    }
}
