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
