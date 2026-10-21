using WatchDog.Core.Storage;

namespace WatchDog.Core.Tests.Storage;

public sealed class ExplorerCommandLineTests
{
    [Fact]
    public void SelectFile_PlainPath_QuotesPath()
    {
        Assert.Equal("/select,\"D:\\Clips\\clip.mp4\"",
            ExplorerCommandLine.SelectFile(@"D:\Clips\clip.mp4"));
    }

    [Fact]
    public void SelectFile_PathWithSpaces_QuotesPath()
    {
        Assert.Equal("/select,\"D:\\My Clips\\clip one.mp4\"",
            ExplorerCommandLine.SelectFile(@"D:\My Clips\clip one.mp4"));
    }

    [Fact]
    public void SelectFile_CommaWithoutSpaces_QuotesPath()
    {
        // GitHub #155: .NET only quotes ArgumentList entries containing whitespace,
        // so this path reached Explorer unquoted and was split at the comma.
        Assert.Equal("/select,\"D:\\Clips,Test\\clip.mp4\"",
            ExplorerCommandLine.SelectFile(@"D:\Clips,Test\clip.mp4"));
    }

    [Fact]
    public void SelectFile_Unicode_PreservesCharacters()
    {
        Assert.Equal("/select,\"D:\\クリップ\\Ünïcödé 🎮.mp4\"",
            ExplorerCommandLine.SelectFile(@"D:\クリップ\Ünïcödé 🎮.mp4"));
    }

    [Fact]
    public void OpenFolder_PlainPath_QuotesPath()
    {
        Assert.Equal("\"D:\\Clips\"", ExplorerCommandLine.OpenFolder(@"D:\Clips"));
    }

    [Fact]
    public void OpenFolder_CommaWithoutSpaces_QuotesPath()
    {
        Assert.Equal("\"D:\\Clips,Test\"", ExplorerCommandLine.OpenFolder(@"D:\Clips,Test"));
    }

    // Explorer does not apply the CommandLineToArgvW backslash-escaping rules: it opens
    // "D:\Clips\" and "D:\" as written, and "D:\\" opens Documents instead of the drive.
    // So a trailing backslash is quoted as-is, never doubled.

    [Fact]
    public void OpenFolder_TrailingBackslash_IsQuotedAsIs()
    {
        Assert.Equal("\"D:\\Clips,Test\\\"", ExplorerCommandLine.OpenFolder(@"D:\Clips,Test\"));
    }

    [Fact]
    public void OpenFolder_DriveRoot_IsQuotedAsIs()
    {
        Assert.Equal("\"D:\\\"", ExplorerCommandLine.OpenFolder(@"D:\"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("D:\\Clips\\\"clip\".mp4")]
    public void SelectFile_InvalidPath_Throws(string filePath)
    {
        Assert.Throws<ArgumentException>(() => ExplorerCommandLine.SelectFile(filePath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("D:\\\"Clips\"")]
    public void OpenFolder_InvalidPath_Throws(string folderPath)
    {
        Assert.Throws<ArgumentException>(() => ExplorerCommandLine.OpenFolder(folderPath));
    }

    [Fact]
    public void SelectFile_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ExplorerCommandLine.SelectFile(null!));
    }

    [Fact]
    public void OpenFolder_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ExplorerCommandLine.OpenFolder(null!));
    }
}
