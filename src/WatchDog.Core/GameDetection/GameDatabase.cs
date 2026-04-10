using System.Text.Json;
using WatchDog.Core.Settings;

namespace WatchDog.Core.GameDetection;

/// <summary>
/// Maintains a lookup of known game executables.
/// Built-in games are loaded from an embedded list; users can add custom entries.
/// </summary>
public sealed class GameDatabase
{
    private readonly Dictionary<string, (string DisplayName, GameGenre Genre)> _games;
    private readonly HashSet<string> _blacklist;

    public GameDatabase(IEnumerable<CustomGameEntry>? customGames = null)
    {
        _games = new Dictionary<string, (string, GameGenre)>(StringComparer.OrdinalIgnoreCase);
        _blacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        LoadBuiltInGames();
        LoadBlacklist();

        if (customGames is not null)
        {
            foreach (var entry in customGames)
                _games[entry.ExecutableName.ToLowerInvariant()] = (entry.DisplayName, GameGenre.Unknown);
        }
    }

    public bool IsKnownGame(string executableName)
        => _games.ContainsKey(NormalizeExeName(executableName));

    public bool IsBlacklisted(string executableName)
        => _blacklist.Contains(NormalizeExeName(executableName));

    public string? GetDisplayName(string executableName)
        => _games.TryGetValue(NormalizeExeName(executableName), out var entry) ? entry.DisplayName : null;

    public GameGenre GetGenre(string executableName)
        => _games.TryGetValue(NormalizeExeName(executableName), out var entry) ? entry.Genre : GameGenre.Unknown;

    public GameInfo? TryMatch(string executableName, int processId, string? windowTitle = null)
    {
        var normalized = NormalizeExeName(executableName);

        if (IsBlacklisted(normalized))
            return null;

        if (!_games.TryGetValue(normalized, out var entry))
            return null;

        return new GameInfo
        {
            ExecutableName = executableName,
            DisplayName = entry.DisplayName,
            ProcessId = processId,
            WindowTitle = windowTitle,
            Genre = entry.Genre
        };
    }

    public void AddCustomGame(string executableName, string displayName)
        => _games[NormalizeExeName(executableName)] = (displayName, GameGenre.Unknown);

    /// <summary>Returns all known games for the profiles UI.</summary>
    public IReadOnlyList<(string ExecutableName, string DisplayName, GameGenre Genre)> GetAllGames()
        => _games.Select(kv => (kv.Key, kv.Value.DisplayName, kv.Value.Genre))
            .OrderBy(g => g.DisplayName)
            .ToList();

    public int GameCount => _games.Count;

    private static string NormalizeExeName(string name)
    {
        var normalized = Path.GetFileName(name).ToLowerInvariant();
        return normalized.EndsWith(".exe") ? normalized : normalized + ".exe";
    }

    private void LoadBlacklist()
    {
        var blacklisted = new[]
        {
            "explorer.exe", "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe",
            "brave.exe", "discord.exe", "slack.exe", "teams.exe", "code.exe",
            "steam.exe", "steamwebhelper.exe", "epicgameslauncher.exe",
            "unrealcefsubprocess.exe", "origin.exe", "eadesktop.exe",
            "battle.net.exe", "riotclientservices.exe", "riotclientux.exe",
            "overwolf.exe", "outplayed.exe", "spotify.exe", "obs64.exe",
            "devenv.exe", "rider64.exe", "idea64.exe", "windowsterminal.exe",
            "powershell.exe", "cmd.exe", "taskmgr.exe", "searchhost.exe",
            "startmenuexperiencehost.exe", "shellexperiencehost.exe",
            "applicationframehost.exe", "systemsettings.exe", "svchost.exe",
            "dwm.exe", "csrss.exe", "lsass.exe", "winlogon.exe",
            "nvidia share.exe", "nvcontainer.exe", "nvdisplay.container.exe",
            "msiafterburner.exe", "rtss.exe", "logioverlay.exe",
        };

        foreach (var exe in blacklisted)
            _blacklist.Add(exe);
    }

    private void LoadBuiltInGames()
    {
        // FPS / Shooters
        AddGame("valorant-win64-shipping.exe", "Valorant", GameGenre.FPS);
        AddGame("valorant.exe", "Valorant", GameGenre.FPS);
        AddGame("cs2.exe", "Counter-Strike 2", GameGenre.FPS);
        AddGame("csgo.exe", "Counter-Strike: Global Offensive", GameGenre.FPS);
        AddGame("overwatch.exe", "Overwatch 2", GameGenre.FPS);
        AddGame("r5apex.exe", "Apex Legends", GameGenre.FPS);
        AddGame("cod.exe", "Call of Duty", GameGenre.FPS);
        AddGame("modernwarfare.exe", "Call of Duty: Modern Warfare", GameGenre.FPS);
        AddGame("blackops6.exe", "Call of Duty: Black Ops 6", GameGenre.FPS);
        AddGame("fortniteclient-win64-shipping.exe", "Fortnite", GameGenre.BattleRoyale);
        AddGame("fortniteclient-win64-shipping_be.exe", "Fortnite", GameGenre.BattleRoyale);
        AddGame("rainbowsix.exe", "Rainbow Six Siege", GameGenre.FPS);
        AddGame("r6-siege.exe", "Rainbow Six Siege", GameGenre.FPS);
        AddGame("bf2042.exe", "Battlefield 2042", GameGenre.FPS);
        AddGame("bf1.exe", "Battlefield 1", GameGenre.FPS);
        AddGame("bfv.exe", "Battlefield V", GameGenre.FPS);
        AddGame("destiny2.exe", "Destiny 2", GameGenre.FPS);
        AddGame("escapefromtarkov.exe", "Escape from Tarkov", GameGenre.FPS);
        AddGame("halo infinite.exe", "Halo Infinite", GameGenre.FPS);
        AddGame("haloinfinite.exe", "Halo Infinite", GameGenre.FPS);
        AddGame("pubg.exe", "PUBG", GameGenre.BattleRoyale);
        AddGame("tslgame.exe", "PUBG", GameGenre.BattleRoyale);
        AddGame("deadlock.exe", "Deadlock", GameGenre.FPS);
        AddGame("marvel_rivals.exe", "Marvel Rivals", GameGenre.FPS);
        AddGame("spectre_divide.exe", "Spectre Divide", GameGenre.FPS);
        AddGame("thefinals.exe", "The Finals", GameGenre.FPS);
        AddGame("xdefiant.exe", "XDefiant", GameGenre.FPS);
        AddGame("helldivers2.exe", "Helldivers 2", GameGenre.FPS);
        AddGame("paladins.exe", "Paladins", GameGenre.FPS);
        AddGame("tf2_win64.exe", "Team Fortress 2", GameGenre.FPS);
        AddGame("hl2.exe", "Half-Life 2", GameGenre.FPS);

        // Battle Royale
        AddGame("warzone.exe", "Call of Duty: Warzone", GameGenre.BattleRoyale);
        AddGame("spellbreak.exe", "Spellbreak", GameGenre.BattleRoyale);
        AddGame("darwin_project.exe", "Darwin Project", GameGenre.BattleRoyale);

        // MOBA
        AddGame("league of legends.exe", "League of Legends", GameGenre.MOBA);
        AddGame("leagueclient.exe", "League of Legends", GameGenre.MOBA);
        AddGame("dota2.exe", "Dota 2", GameGenre.MOBA);
        AddGame("smite.exe", "SMITE", GameGenre.MOBA);

        // RPG / Action
        AddGame("eldenring.exe", "Elden Ring", GameGenre.RPG);
        AddGame("darksoulsiii.exe", "Dark Souls III", GameGenre.RPG);
        AddGame("baldursgate3.exe", "Baldur's Gate 3", GameGenre.RPG);
        AddGame("bg3.exe", "Baldur's Gate 3", GameGenre.RPG);
        AddGame("cyberpunk2077.exe", "Cyberpunk 2077", GameGenre.RPG);
        AddGame("witcher3.exe", "The Witcher 3", GameGenre.RPG);
        AddGame("diablo iv.exe", "Diablo IV", GameGenre.RPG);
        AddGame("diablo4.exe", "Diablo IV", GameGenre.RPG);
        AddGame("pathofexile_x64.exe", "Path of Exile", GameGenre.RPG);
        AddGame("pathofexile2.exe", "Path of Exile 2", GameGenre.RPG);
        AddGame("gta5.exe", "Grand Theft Auto V", GameGenre.Sandbox);
        AddGame("rdr2.exe", "Red Dead Redemption 2", GameGenre.RPG);
        AddGame("starfield.exe", "Starfield", GameGenre.RPG);
        AddGame("hogwartslegacy.exe", "Hogwarts Legacy", GameGenre.RPG);

        // Survival / Sandbox
        AddGame("minecraft.exe", "Minecraft", GameGenre.Sandbox);
        AddGame("javaw.exe", "Minecraft (Java)", GameGenre.Sandbox);
        AddGame("rustclient.exe", "Rust", GameGenre.Survival);
        AddGame("valheim.exe", "Valheim", GameGenre.Survival);
        AddGame("palworld-win64-shipping.exe", "Palworld", GameGenre.Survival);
        AddGame("subnautica.exe", "Subnautica", GameGenre.Survival);
        AddGame("arkascended.exe", "ARK: Survival Ascended", GameGenre.Survival);
        AddGame("shootergame.exe", "ARK: Survival Evolved", GameGenre.Survival);
        AddGame("terraria.exe", "Terraria", GameGenre.Sandbox);
        AddGame("satisfactoryearliaccess-win64-shipping.exe", "Satisfactory", GameGenre.Sandbox);

        // Strategy
        AddGame("civ6.exe", "Civilization VI", GameGenre.Strategy);
        AddGame("civ7.exe", "Civilization VII", GameGenre.Strategy);
        AddGame("stellaris.exe", "Stellaris", GameGenre.Strategy);
        AddGame("totalwarhammer3.exe", "Total War: Warhammer III", GameGenre.Strategy);
        AddGame("aoe2de_s.exe", "Age of Empires II: DE", GameGenre.Strategy);

        // Racing / Sports
        AddGame("rocketleague.exe", "Rocket League", GameGenre.Sports);
        AddGame("forza horizon 5.exe", "Forza Horizon 5", GameGenre.Racing);
        AddGame("forzamotorsport.exe", "Forza Motorsport", GameGenre.Racing);
        AddGame("nba2k25.exe", "NBA 2K25", GameGenre.Sports);
        AddGame("fc25.exe", "EA FC 25", GameGenre.Sports);

        // Horror / Co-op
        AddGame("phasmophobia.exe", "Phasmophobia", GameGenre.Horror);
        AddGame("lethal company.exe", "Lethal Company", GameGenre.Horror);
        AddGame("deadbydaylight-win64-shipping.exe", "Dead by Daylight", GameGenre.Horror);
        AddGame("contentwarning.exe", "Content Warning", GameGenre.Horror);

        // MMO
        AddGame("ffxiv_dx11.exe", "Final Fantasy XIV", GameGenre.MMO);
        AddGame("gw2-64.exe", "Guild Wars 2", GameGenre.MMO);
        AddGame("wow.exe", "World of Warcraft", GameGenre.MMO);
        AddGame("newworld.exe", "New World", GameGenre.MMO);
        AddGame("throneandliberty.exe", "Throne and Liberty", GameGenre.MMO);

        // Fighting
        AddGame("streetfighter6.exe", "Street Fighter 6", GameGenre.Fighting);
        AddGame("tekken8.exe", "Tekken 8", GameGenre.Fighting);

        // Other Popular
        AddGame("among us.exe", "Among Us", GameGenre.Sandbox);
        AddGame("fall guys.exe", "Fall Guys", GameGenre.Platformer);
        AddGame("fallguys_client.exe", "Fall Guys", GameGenre.Platformer);
        AddGame("naraka-win64-shipping.exe", "NARAKA: BLADEPOINT", GameGenre.BattleRoyale);
        AddGame("genshinimpact.exe", "Genshin Impact", GameGenre.RPG);
        AddGame("zenlesszonezero.exe", "Zenless Zone Zero", GameGenre.RPG);
        AddGame("honkaistarrail.exe", "Honkai: Star Rail", GameGenre.RPG);
        AddGame("warframe.x64.exe", "Warframe", GameGenre.FPS);
    }

    private void AddGame(string exe, string displayName, GameGenre genre = GameGenre.Unknown)
        => _games[exe.ToLowerInvariant()] = (displayName, genre);
}
