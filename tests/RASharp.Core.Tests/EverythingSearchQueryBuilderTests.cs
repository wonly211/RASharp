using RASharp.Core.Everything;

namespace RASharp.Core.Tests;

public sealed class EverythingSearchQueryBuilderTests
{
    [Fact]
    public void BuildUsesTrimmedTextLinesAsOrTerms()
    {
        var query = EverythingSearchQueryBuilder.Build(
            " first phrase\r\nsecond\r\nfirst phrase ",
            [],
            includeFileExtension: true,
            searchFolderContents: true);

        Assert.Equal("\"first phrase\" | second", query);
    }

    [Fact]
    public void BuildHandlesFilesAndFolderContents()
    {
        var root = Path.Combine(Path.GetTempPath(), $"RASharp-query-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "report final.pdf");
            File.WriteAllText(file, string.Empty);

            Assert.Equal(
                "\"report final\"",
                EverythingSearchQueryBuilder.Build(file, [file], false, true));
            Assert.Equal(
                $"path:\"{root}\"",
                EverythingSearchQueryBuilder.Build(root, [root], true, true));
            var driveRoot = Path.GetPathRoot(root)!;
            Assert.Equal(
                $"path:\"{driveRoot}\"",
                EverythingSearchQueryBuilder.Build(driveRoot, [driveRoot], true, true));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
