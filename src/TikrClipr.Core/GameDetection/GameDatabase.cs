using System.Text.Json;
using TikrClipr.Core.Settings;

namespace TikrClipr.Core.GameDetection;

/// <summary>
/// Maintains a lookup of known game executables.
/// Built-in games are loaded from an embedded list; users can add custom entries.
/// </summary>
public sealed class GameDatabase
{
    private readonly Dictionary<string, string> _games; // exe name (lowercase) → display name
    private readonly HashSet<string> _blacklist;

    public GameDatabase(IEnumerable<CustomGameEntry>? customGames = null)
    {
        _games = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _blacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        LoadBuiltInGames();
        LoadBlacklist();

        if (customGames is not null)
        {
            foreach (var entry in customGames)
                _games[entry.ExecutableName.ToLowerInvariant()] = entry.DisplayName;
        }
    }

    public bool IsKnownGame(string executableName)
        => _games.ContainsKey(NormalizeExeName(executableName));

    public bool IsBlacklisted(string executableName)
        => _blacklist.Contains(NormalizeExeName(executableName));

    public string? GetDisplayName(string executableName)
        => _games.GetValueOrDefault(NormalizeExeName(executableName));

    public GameInfo? TryMatch(string executableName, int processId, string? windowTitle = null)
    {
        var normalized = NormalizeExeName(executableName);

        if (IsBlacklisted(normalized))
            return null;

        if (!_games.TryGetValue(normalized, out var displayName))
            return null;

        return new GameInfo
        {
            ExecutableName = executableName,
            DisplayName = displayName,
            ProcessId = processId,
            WindowTitle = windowTitle
        };
    }

    public void AddCustomGame(string executableName, string displayName)
        => _games[NormalizeExeName(executableName)] = displayName;

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
        AddGame("valorant-win64-shipping.exe", "Valorant");
        AddGame("valorant.exe", "Valorant");
        AddGame("cs2.exe", "Counter-Strike 2");
        AddGame("csgo.exe", "Counter-Strike: Global Offensive");
        AddGame("overwatch.exe", "Overwatch 2");
        AddGame("r5apex.exe", "Apex Legends");
        AddGame("cod.exe", "Call of Duty");
        AddGame("modernwarfare.exe", "Call of Duty: Modern Warfare");
        AddGame("blackops6.exe", "Call of Duty: Black Ops 6");
        AddGame("fortniteclient-win64-shipping.exe", "Fortnite");
        AddGame("fortniteclient-win64-shipping_be.exe", "Fortnite");
        AddGame("rainbowsix.exe", "Rainbow Six Siege");
        AddGame("r6-siege.exe", "Rainbow Six Siege");
        AddGame("bf2042.exe", "Battlefield 2042");
        AddGame("bf1.exe", "Battlefield 1");
        AddGame("bfv.exe", "Battlefield V");
        AddGame("destiny2.exe", "Destiny 2");
        AddGame("escapefromtarkov.exe", "Escape from Tarkov");
        AddGame("halo infinite.exe", "Halo Infinite");
        AddGame("haloinfinite.exe", "Halo Infinite");
        AddGame("pubg.exe", "PUBG");
        AddGame("tslgame.exe", "PUBG");
        AddGame("deadlock.exe", "Deadlock");
        AddGame("marvel_rivals.exe", "Marvel Rivals");
        AddGame("spectre_divide.exe", "Spectre Divide");
        AddGame("thefinals.exe", "The Finals");
        AddGame("xdefiant.exe", "XDefiant");
        AddGame("helldivers2.exe", "Helldivers 2");
        AddGame("paladins.exe", "Paladins");
        AddGame("tf2_win64.exe", "Team Fortress 2");
        AddGame("hl2.exe", "Half-Life 2");

        // Battle Royale
        AddGame("warzone.exe", "Call of Duty: Warzone");
        AddGame("spellbreak.exe", "Spellbreak");
        AddGame("darwin_project.exe", "Darwin Project");

        // MOBA
        AddGame("league of legends.exe", "League of Legends");
        AddGame("leagueclient.exe", "League of Legends");
        AddGame("dota2.exe", "Dota 2");
        AddGame("smite.exe", "SMITE");

        // RPG / Action
        AddGame("eldenring.exe", "Elden Ring");
        AddGame("darksoulsiii.exe", "Dark Souls III");
        AddGame("baldursgate3.exe", "Baldur's Gate 3");
        AddGame("bg3.exe", "Baldur's Gate 3");
        AddGame("cyberpunk2077.exe", "Cyberpunk 2077");
        AddGame("witcher3.exe", "The Witcher 3");
        AddGame("diablo iv.exe", "Diablo IV");
        AddGame("diablo4.exe", "Diablo IV");
        AddGame("pathofexile_x64.exe", "Path of Exile");
        AddGame("pathofexile2.exe", "Path of Exile 2");
        AddGame("gta5.exe", "Grand Theft Auto V");
        AddGame("rdr2.exe", "Red Dead Redemption 2");
        AddGame("starfield.exe", "Starfield");
        AddGame("hogwartslegacy.exe", "Hogwarts Legacy");

        // Survival / Sandbox
        AddGame("minecraft.exe", "Minecraft");
        AddGame("javaw.exe", "Minecraft (Java)");
        AddGame("rustclient.exe", "Rust");
        AddGame("valheim.exe", "Valheim");
        AddGame("palworld-win64-shipping.exe", "Palworld");
        AddGame("subnautica.exe", "Subnautica");
        AddGame("arkascended.exe", "ARK: Survival Ascended");
        AddGame("shootergame.exe", "ARK: Survival Evolved");
        AddGame("terraria.exe", "Terraria");
        AddGame("satisfactoryearliaccess-win64-shipping.exe", "Satisfactory");

        // Strategy
        AddGame("civ6.exe", "Civilization VI");
        AddGame("civ7.exe", "Civilization VII");
        AddGame("stellaris.exe", "Stellaris");
        AddGame("totalwarhammer3.exe", "Total War: Warhammer III");
        AddGame("aoe2de_s.exe", "Age of Empires II: DE");

        // Racing / Sports
        AddGame("rocketleague.exe", "Rocket League");
        AddGame("forza horizon 5.exe", "Forza Horizon 5");
        AddGame("forzamotorsport.exe", "Forza Motorsport");
        AddGame("nba2k25.exe", "NBA 2K25");
        AddGame("fc25.exe", "EA FC 25");

        // Horror / Co-op
        AddGame("phasmophobia.exe", "Phasmophobia");
        AddGame("lethal company.exe", "Lethal Company");
        AddGame("deadbydaylight-win64-shipping.exe", "Dead by Daylight");
        AddGame("contentwarning.exe", "Content Warning");

        // MMO
        AddGame("ffxiv_dx11.exe", "Final Fantasy XIV");
        AddGame("gw2-64.exe", "Guild Wars 2");
        AddGame("wow.exe", "World of Warcraft");
        AddGame("newworld.exe", "New World");
        AddGame("throneandliberty.exe", "Throne and Liberty");

        // Fighting
        AddGame("streetfighter6.exe", "Street Fighter 6");
        AddGame("tekken8.exe", "Tekken 8");

        // Other Popular
        AddGame("among us.exe", "Among Us");
        AddGame("fall guys.exe", "Fall Guys");
        AddGame("fallguys_client.exe", "Fall Guys");
        AddGame("naraka-win64-shipping.exe", "NARAKA: BLADEPOINT");
        AddGame("genshinimpact.exe", "Genshin Impact");
        AddGame("zenlesszonezero.exe", "Zenless Zone Zero");
        AddGame("honkaistarrail.exe", "Honkai: Star Rail");
        AddGame("warframe.x64.exe", "Warframe");
    }

    private void AddGame(string exe, string displayName)
        => _games[exe.ToLowerInvariant()] = displayName;
}
