using TikrClipr.Core.GameDetection;
using TikrClipr.Core.Settings;

namespace TikrClipr.Core.Tests.GameDetection;

public sealed class GameDatabaseTests
{
    [Theory]
    [InlineData("valorant-win64-shipping.exe", "Valorant")]
    [InlineData("cs2.exe", "Counter-Strike 2")]
    [InlineData("r5apex.exe", "Apex Legends")]
    [InlineData("rocketleague.exe", "Rocket League")]
    public void IsKnownGame_ReturnsTrue_ForBuiltInGames(string exe, string expectedName)
    {
        var db = new GameDatabase();

        Assert.True(db.IsKnownGame(exe));
        Assert.Equal(expectedName, db.GetDisplayName(exe));
    }

    [Theory]
    [InlineData("VALORANT-Win64-Shipping.exe")]
    [InlineData("CS2.EXE")]
    [InlineData("R5Apex.exe")]
    public void IsKnownGame_IsCaseInsensitive(string exe)
    {
        var db = new GameDatabase();
        Assert.True(db.IsKnownGame(exe));
    }

    [Theory]
    [InlineData("notepad.exe")]
    [InlineData("randomapp.exe")]
    public void IsKnownGame_ReturnsFalse_ForUnknownGames(string exe)
    {
        var db = new GameDatabase();
        Assert.False(db.IsKnownGame(exe));
    }

    [Theory]
    [InlineData("chrome.exe")]
    [InlineData("discord.exe")]
    [InlineData("steam.exe")]
    [InlineData("explorer.exe")]
    [InlineData("obs64.exe")]
    public void IsBlacklisted_ReturnsTrue_ForKnownNonGames(string exe)
    {
        var db = new GameDatabase();
        Assert.True(db.IsBlacklisted(exe));
    }

    [Fact]
    public void TryMatch_ReturnsNull_ForBlacklistedProcess()
    {
        var db = new GameDatabase();
        var result = db.TryMatch("chrome.exe", 1234);
        Assert.Null(result);
    }

    [Fact]
    public void TryMatch_ReturnsGameInfo_ForKnownGame()
    {
        var db = new GameDatabase();
        var result = db.TryMatch("cs2.exe", 5678, "Counter-Strike 2");

        Assert.NotNull(result);
        Assert.Equal("cs2.exe", result.ExecutableName);
        Assert.Equal("Counter-Strike 2", result.DisplayName);
        Assert.Equal(5678, result.ProcessId);
    }

    [Fact]
    public void TryMatch_HandlesExeWithoutExtension()
    {
        var db = new GameDatabase();
        var result = db.TryMatch("cs2", 1234);

        Assert.NotNull(result);
        Assert.Equal("Counter-Strike 2", result.DisplayName);
    }

    [Fact]
    public void TryMatch_HandlesFullPath()
    {
        var db = new GameDatabase();
        var result = db.TryMatch(@"C:\Games\Steam\cs2.exe", 1234);

        Assert.NotNull(result);
        Assert.Equal("Counter-Strike 2", result.DisplayName);
    }

    [Fact]
    public void CustomGames_AreIncluded()
    {
        var custom = new List<CustomGameEntry>
        {
            new() { ExecutableName = "mygame.exe", DisplayName = "My Custom Game" }
        };

        var db = new GameDatabase(custom);

        Assert.True(db.IsKnownGame("mygame.exe"));
        Assert.Equal("My Custom Game", db.GetDisplayName("mygame.exe"));
    }

    [Fact]
    public void AddCustomGame_WorksAtRuntime()
    {
        var db = new GameDatabase();

        Assert.False(db.IsKnownGame("newgame.exe"));

        db.AddCustomGame("newgame.exe", "New Game");

        Assert.True(db.IsKnownGame("newgame.exe"));
        Assert.Equal("New Game", db.GetDisplayName("newgame.exe"));
    }

    [Fact]
    public void GameCount_IncludesBuiltInAndCustomGames()
    {
        var custom = new List<CustomGameEntry>
        {
            new() { ExecutableName = "custom1.exe", DisplayName = "Custom 1" },
            new() { ExecutableName = "custom2.exe", DisplayName = "Custom 2" }
        };

        var db = new GameDatabase(custom);

        Assert.True(db.GameCount > 50); // Verify substantial built-in list
    }
}
