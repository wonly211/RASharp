using RASharp.Core.Menus;
using System.Text;

namespace RASharp.Core.Tests;

public sealed class RunAnyMenuParserTests
{
    [Fact]
    public void ParsePreservesHierarchySelectorsSeparatorsAndHotKeys()
    {
        const string content = """
            ; comment
            -常用(&App)|exe lnk
            Chrome浏览器	!c|chrome.exe
            --办公
            Word|winword.exe
            --
            计算器|calc.exe
            -
            根命令|cmd.exe
            """;

        var document = RunAnyMenuParser.Parse(content, "RunAny.ini", 1);

        var common = Assert.IsType<MenuCategory>(document.Root.Children[0]);
        Assert.Equal("常用(&App)", common.Name);
        Assert.Equal(["exe", "lnk"], common.Selectors);

        var chrome = Assert.IsType<MenuEntry>(common.Children[0]);
        Assert.Equal("Chrome浏览器", chrome.DisplayName);
        Assert.Equal("!c", chrome.HotKey);
        Assert.Equal("chrome.exe", chrome.Value);

        var office = Assert.IsType<MenuCategory>(common.Children[1]);
        Assert.Equal("办公", office.Name);
        Assert.Equal("winword.exe", Assert.IsType<MenuEntry>(office.Children[0]).Value);
        Assert.IsType<MenuSeparator>(common.Children[2]);
        Assert.Equal("calc.exe", Assert.IsType<MenuEntry>(common.Children[3]).Value);

        Assert.IsType<MenuSeparator>(document.Root.Children[1]);
        Assert.Equal("cmd.exe", Assert.IsType<MenuEntry>(document.Root.Children[2]).Value);
    }

    [Fact]
    public void ParseRecognizesPhrasesHotstringsWebAndKeySequences()
    {
        const string content = """
            -输入
            邮箱:*X:mail|person@example.com;
            当前时间|%A_YYYY%-%A_MM%-%A_DD%;;
            左手回车	<+Space|{Enter}::
            搜索|https://example.test/?q=%s
            """;

        var category = Assert.IsType<MenuCategory>(RunAnyMenuParser.Parse(content, "RunAny.ini", 1).Root.Children[0]);
        var email = Assert.IsType<MenuEntry>(category.Children[0]);
        Assert.Equal(MenuEntryKind.Phrase, email.Kind);
        Assert.Equal("person@example.com", email.Value);
        Assert.Equal("mail", email.Hotstring?.Trigger);
        Assert.True(email.Hotstring?.TriggerImmediately);
        Assert.True(email.Hotstring?.ExecuteAction);

        Assert.Equal(MenuEntryKind.RawPhrase, Assert.IsType<MenuEntry>(category.Children[1]).Kind);
        Assert.Equal(MenuEntryKind.KeySequence, Assert.IsType<MenuEntry>(category.Children[2]).Kind);
        Assert.Equal(MenuEntryKind.Web, Assert.IsType<MenuEntry>(category.Children[3]).Kind);
    }

    [Fact]
    public void ParseDerivesDisplayNameForEntriesWithoutAlias()
    {
        var document = RunAnyMenuParser.Parse("-工具\nnotepad.exe\n", "RunAny.ini", 1);
        var category = Assert.IsType<MenuCategory>(document.Root.Children[0]);
        var entry = Assert.IsType<MenuEntry>(category.Children[0]);

        Assert.Equal("notepad", entry.DisplayName);
    }

    [Fact]
    public void ParseCleansTransparencySuffixFromDisplayName()
    {
        var document = RunAnyMenuParser.Parse("-工具\n记事本_:88|notepad.exe\n", "RunAny.ini", 1);
        var category = Assert.IsType<MenuCategory>(document.Root.Children[0]);

        Assert.Equal("记事本", Assert.IsType<MenuEntry>(category.Children[0]).DisplayName);
    }

    [Fact]
    public void ParseFileReadsLegacyGbkConfiguration()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var path = Path.Combine(Path.GetTempPath(), $"RASharp-{Guid.NewGuid():N}.ini");
        try
        {
            File.WriteAllBytes(path, Encoding.GetEncoding(936).GetBytes("-常用\r\n记事本|notepad.exe\r\n"));

            var document = RunAnyMenuParser.ParseFile(path, 1);

            var category = Assert.IsType<MenuCategory>(document.Root.Children[0]);
            Assert.Equal("常用", category.Name);
            Assert.Equal("记事本", Assert.IsType<MenuEntry>(category.Children[0]).DisplayName);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
