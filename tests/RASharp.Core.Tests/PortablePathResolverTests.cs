using RASharp.Core.Runtime;

namespace RASharp.Core.Tests;

public sealed class PortablePathResolverTests
{
    [Fact]
    public void ResolveUsesRealAppHostDirectoryForSingleFileApplication()
    {
        var result = PortablePathResolver.ResolveExecutableDirectory(
            @"D:\Tools\RASharp\RASharp.exe",
            [@"D:\Tools\RASharp\RASharp.exe"]);

        Assert.Equal(@"D:\Tools\RASharp", result);
    }

    [Fact]
    public void ResolveUsesEntryAssemblyDirectoryWhenHostedByDotnet()
    {
        var result = PortablePathResolver.ResolveExecutableDirectory(
            @"C:\Program Files\dotnet\dotnet.exe",
            [@"D:\Tools\RASharp\RASharp.dll"]);

        Assert.Equal(@"D:\Tools\RASharp", result);
    }

    [Fact]
    public void ResolveFallsBackToCommandLineWhenProcessPathIsUnavailable()
    {
        var result = PortablePathResolver.ResolveExecutableDirectory(
            null,
            [@"D:\Portable\RASharp.exe"]);

        Assert.Equal(@"D:\Portable", result);
    }
}
