using RASharp.Core.Menus;
using System.Text;

namespace RASharp.Core.Tests;

public sealed class MenuEditorModelTests
{
    [Fact]
    public void SerializeWithoutChangesPreservesCommentsBlankLinesAndDsl()
    {
        const string content = "; comment\r\n\r\n-Tools|exe lnk\r\nAdmin[#]|cmd.exe\r\n--Editors\r\nNotepad|notepad.exe\r\n";

        var document = MenuEditorDocument.Parse(content, "RunAny.ini", 1);

        Assert.Equal(content, document.Serialize());
    }

    [Fact]
    public void SerializeEditedEntryPreservesTriviaAndWritesSupportedMetadata()
    {
        const string content = "; tools\n-Tools\n; command\nTerminal|cmd.exe\n";
        var document = MenuEditorDocument.Parse(content, "RunAny.ini", 1);
        var category = Assert.Single(document.Children);
        var entry = Assert.Single(category.Children);
        entry.Name = "PowerShell";
        entry.Value = "powershell.exe";
        entry.RunAsAdministrator = true;
        entry.HotKey = "#P";
        document.MarkDirty(entry);

        Assert.Equal(
            "; tools\n-Tools\n; command\nPowerShell[#]\t#P|powershell.exe\n",
            document.Serialize());
    }

    [Fact]
    public void AddMoveAndRemoveUpdateTree()
    {
        var document = MenuEditorDocument.Parse("-Tools\n", "RunAny.ini", 1);
        var category = Assert.Single(document.Children);
        var first = MenuEditorNode.CreateEntry("First", "first.exe");
        var second = MenuEditorNode.CreateEntry("Second", "second.exe");
        document.Add(first, category);
        document.Add(second, category);

        Assert.True(document.Move(second, -1));
        Assert.True(document.Remove(first));
        Assert.Equal("-Tools\nSecond|second.exe\n", document.Serialize());
    }

    [Fact]
    public void DisplayTextContainsOnlyNodeName()
    {
        var document = MenuEditorDocument.Parse("-Tools\nNotepad|notepad.exe\n", "RunAny.ini", 1);
        var category = Assert.Single(document.Children);
        var entry = Assert.Single(category.Children);

        Assert.Equal("Tools", category.DisplayText);
        Assert.Equal("Notepad", entry.DisplayText);
        Assert.DoesNotContain("notepad.exe", entry.DisplayText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MoveToChangesOrderAndHierarchyAndRejectsCycles()
    {
        var document = MenuEditorDocument.Parse("-One\nFirst|first.exe\n-Two\nSecond|second.exe\n", "RunAny.ini", 1);
        var firstCategory = document.Children[0];
        var secondCategory = document.Children[1];
        var firstEntry = Assert.Single(firstCategory.Children);

        Assert.True(document.MoveTo(firstEntry, secondCategory, 1));
        Assert.Empty(firstCategory.Children);
        Assert.Equal(["Second", "First"], secondCategory.Children.Select(node => node.Name));
        Assert.False(document.MoveTo(secondCategory, secondCategory, 0));
        Assert.False(document.MoveTo(secondCategory, firstEntry, 0));
        Assert.Equal("-One\n-Two\nSecond|second.exe\nFirst|first.exe\n", document.Serialize());
    }

    [Fact]
    public void MoveToNestsCategoryAndUpdatesDslDepth()
    {
        var document = MenuEditorDocument.Parse("-Parent\n-Child\nApp|app.exe\n", "RunAny.ini", 1);
        var parent = document.Children[0];
        var child = document.Children[1];

        Assert.True(document.MoveTo(child, parent, 0));
        Assert.Same(parent, child.Parent);
        Assert.Equal("-Parent\n--Child\nApp|app.exe\n", document.Serialize());
    }

    [Fact]
    public void MoveWithinSiblingsSupportsBoundariesWithoutBreakingRootDslGroups()
    {
        var document = MenuEditorDocument.Parse(
            "First|first.exe\nSecond|second.exe\n-One\n-Two\n",
            "RunAny.ini",
            1);
        var firstEntry = document.Children[0];
        var secondEntry = document.Children[1];
        var firstCategory = document.Children[2];
        var secondCategory = document.Children[3];

        Assert.True(document.MoveWithinSiblings(secondEntry, 0));
        Assert.True(document.MoveWithinSiblings(secondCategory, 0));
        Assert.False(document.MoveWithinSiblings(firstEntry, int.MaxValue));
        Assert.Equal(
            "Second|second.exe\nFirst|first.exe\n-Two\n-One\n",
            document.Serialize());
        Assert.Equal(["Second", "First", "Two", "One"], document.Children.Select(node => node.Name));
        Assert.All(document.Children.Take(2), node => Assert.NotEqual(MenuEditorNodeKind.Category, node.Kind));
        Assert.All(document.Children.Skip(2), node => Assert.Equal(MenuEditorNodeKind.Category, node.Kind));
        Assert.Same(firstCategory, document.Children[3]);
    }

    [Fact]
    public void RootInsertionKeepsApplicationsBeforeCategories()
    {
        var document = MenuEditorDocument.Parse("Existing|existing.exe\n-Tools\n", "RunAny.ini", 1);
        var category = MenuEditorNode.CreateCategory("New category");
        var entry = MenuEditorNode.CreateEntry("New app", "new.exe");

        document.Add(category, index: 0);
        document.Add(entry, index: int.MaxValue);

        Assert.Equal(
            "Existing|existing.exe\nNew app|new.exe\n-New category\n-Tools\n",
            document.Serialize());
    }

    [Fact]
    public void NestedInsertionKeepsApplicationsBeforeChildCategories()
    {
        var document = MenuEditorDocument.Parse(
            "-Parent\nExisting|existing.exe\n--Child\nChild app|child.exe\n",
            "RunAny.ini",
            1);
        var parent = Assert.Single(document.Children);
        var childCategory = parent.Children[1];
        document.Add(MenuEditorNode.CreateEntry("New app", "new.exe"), parent, int.MaxValue);

        Assert.False(document.MoveWithinSiblings(childCategory, 0));
        Assert.Equal(
            "-Parent\nExisting|existing.exe\nNew app|new.exe\n--Child\nChild app|child.exe\n",
            document.Serialize());
    }

    [Fact]
    public void LevelSeparatorReturnsFollowingEntriesToTheParentMenu()
    {
        const string content = "-Category\nChild app|child.exe\n-\nRoot app|root.exe\n";

        var document = MenuEditorDocument.Parse(content, "RunAny.ini", 1);

        Assert.Equal(3, document.Children.Count);
        Assert.Equal(MenuEditorNodeKind.Category, document.Children[0].Kind);
        Assert.Equal(MenuEditorNodeKind.LevelSeparator, document.Children[1].Kind);
        Assert.Equal("──────── 返回当前菜单层级（-）────────", document.Children[1].DisplayText);
        Assert.Equal(MenuEditorNodeKind.Entry, document.Children[2].Kind);
        Assert.Null(document.Children[2].Parent);
        Assert.Equal(content, document.Serialize());
    }

    [Fact]
    public void AddedLevelSeparatorAllowsApplicationsAfterCategoriesAtTheSameLevel()
    {
        var document = MenuEditorDocument.Parse(
            "-Category\nChild app|child.exe\n",
            "RunAny.ini",
            1);
        var category = Assert.Single(document.Children);
        var levelSeparator = MenuEditorNode.CreateLevelSeparator();
        var rootEntry = MenuEditorNode.CreateEntry("Root app", "root.exe");

        document.Add(levelSeparator, index: int.MaxValue);
        document.Add(rootEntry, index: int.MaxValue);

        Assert.Equal(
            "-Category\nChild app|child.exe\n-\nRoot app|root.exe\n",
            document.Serialize());
        var reparsed = MenuEditorDocument.Parse(document.Serialize(), "RunAny.ini", 1);
        Assert.Equal(
            [MenuEditorNodeKind.Category, MenuEditorNodeKind.LevelSeparator, MenuEditorNodeKind.Entry],
            reparsed.Children.Select(node => node.Kind));
    }

    [Fact]
    public void MovingLevelSeparatorRewritesItsDslDepth()
    {
        var document = MenuEditorDocument.Parse(
            "-Parent\n--Child\nChild app|child.exe\n--\nParent app|parent.exe\n",
            "RunAny.ini",
            1);
        var parent = Assert.Single(document.Children);
        var levelSeparator = parent.Children[1];

        Assert.Equal(MenuEditorNodeKind.LevelSeparator, levelSeparator.Kind);
        Assert.True(document.MoveTo(levelSeparator, null, int.MaxValue));
        Assert.Contains("\n-\n", document.Serialize(), StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotRestoresTreeContentAndDirtyState()
    {
        var document = MenuEditorDocument.Parse("-Tools\nFirst|first.exe\n", "RunAny.ini", 1);
        var cleanSnapshot = document.CreateSnapshot();
        var category = Assert.Single(document.Children);
        document.Add(MenuEditorNode.CreateEntry("Second", "second.exe"), category);
        var dirtySnapshot = document.CreateSnapshot();

        document.RestoreSnapshot(cleanSnapshot);

        Assert.False(document.IsDirty);
        Assert.Equal("-Tools\nFirst|first.exe\n", document.Serialize());

        document.RestoreSnapshot(dirtySnapshot);

        Assert.True(document.IsDirty);
        Assert.Equal("-Tools\nFirst|first.exe\nSecond|second.exe\n", document.Serialize());
    }

    [Fact]
    public void MoveToSamePositionDoesNotCreateAChange()
    {
        var document = MenuEditorDocument.Parse("-Tools\nFirst|first.exe\n", "RunAny.ini", 1);
        var category = Assert.Single(document.Children);
        var entry = Assert.Single(category.Children);

        Assert.False(document.MoveTo(entry, category, 0));
        Assert.False(document.IsDirty);
        Assert.Equal("-Tools\nFirst|first.exe\n", document.Serialize());
    }

    [Fact]
    public void ConfigurationFilePreservesLegacyGbkEncodingWhenSaved()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gbk = Encoding.GetEncoding(936);
        var directory = Path.Combine(Path.GetTempPath(), $"RASharp-editor-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "RunAny.ini");
        Directory.CreateDirectory(directory);
        try
        {
            const string content = "; 中文注释\r\n-工具\r\n记事本|notepad.exe\r\n";
            File.WriteAllBytes(path, gbk.GetBytes(content));
            var file = MenuConfigurationFile.Load(path, 1);
            var category = Assert.Single(file.Document.Children);
            var entry = Assert.Single(category.Children);
            entry.Name = "文本编辑器";
            file.Document.MarkDirty(entry);

            file.Save();

            var saved = File.ReadAllBytes(path);
            Assert.Equal(-1, saved.AsSpan().IndexOf(Encoding.UTF8.Preamble));
            Assert.Contains("文本编辑器|notepad.exe", gbk.GetString(saved), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
