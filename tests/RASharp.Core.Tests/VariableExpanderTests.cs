using RASharp.Core.Menus;
using System.Globalization;

namespace RASharp.Core.Tests;

public sealed class VariableExpanderTests
{
    [Fact]
    public void ExpandHandlesRunAnyVariablesAndKeepsUnknownVariables()
    {
        var expander = new VariableExpander(@"D:\Config");

        var result = expander.Expand(@"%A_ScriptDir%\app.exe %A_YYYY% %DOES_NOT_EXIST_RASHARP%");

        Assert.StartsWith(@"D:\Config\app.exe ", result, StringComparison.Ordinal);
        Assert.Contains(DateTime.Now.ToString("yyyy", CultureInfo.InvariantCulture), result, StringComparison.Ordinal);
        Assert.EndsWith("%DOES_NOT_EXIST_RASHARP%", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpandCanUrlEncodeSelectedText()
    {
        var expander = new VariableExpander(@"D:\Config");

        var result = expander.Expand("https://example.test/?q=%s", "中文 test", encodeSelectedText: true);

        Assert.Equal("https://example.test/?q=%E4%B8%AD%E6%96%87%20test", result);
    }

    [Fact]
    public void ExpandDoesNotExposeApplicationDataDirectories()
    {
        var expander = new VariableExpander(@"D:\Config");

        var result = expander.Expand(@"%APPDATA%\file.txt|%LOCALAPPDATA%\file.txt");

        Assert.Equal(@"%APPDATA%\file.txt|%LOCALAPPDATA%\file.txt", result);
    }
}
