using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using System.Linq;
using System.Reflection;

namespace SlayTheStats;

/// <summary>
/// Extracts "CATEGORY.ENTRY" from a CharacterModel instance via reflection.
/// Returns null if the chain is missing or throws.
/// </summary>
internal static class CharacterIdHelper
{
    internal static string? Extract(object? character)
    {
        if (character == null) return null;
        var charId = character.GetType()
            .GetProperty("Id", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(character);
        if (charId == null) return null;
        var category = charId.GetType()
            .GetProperty("Category", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(charId) as string;
        var entry = charId.GetType()
            .GetProperty("Entry", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(charId) as string;
        return category != null && entry != null ? $"{category}.{entry}".ToUpper() : null;
    }
}

// In multiplayer, NGame.StartRun(RunState) is called with a player list that's identical on
// every client, so picking players[0] would tag joiners with the host's character. The lobby
// knows each instance's local seat, and the multiplayer entry point NGame.StartNewMultiplayerRun
// receives the StartRunLobby — so we hook it one level up, capture the local LobbyPlayer's
// character, and stash it for the StartRun patch to consume. Singleplayer leaves this null and
// falls back to players[0], which is correct because there's only one player.
internal static class LocalCharacterCapture
{
    // For fresh MP: StartNewMultiplayerRun pulls the character string straight off lobby.LocalPlayer.
    internal static string? PendingCharacterId;
    // For MP load: LoadRunLobby exposes the local NetService.NetId but not a LobbyPlayer, so we
    // stash the id and let the LoadRun patch filter runState.Players by it.
    internal static ulong? PendingLocalNetId;

    internal static string? ConsumeCharacterId()
    {
        var id = PendingCharacterId;
        PendingCharacterId = null;
        return id;
    }

    internal static ulong? ConsumeLocalNetId()
    {
        var id = PendingLocalNetId;
        PendingLocalNetId = null;
        return id;
    }
}

[HarmonyPatch(typeof(NGame), "StartNewMultiplayerRun")]
public static class StartNewMultiplayerRunPatch
{
    static void Prefix(StartRunLobby lobby)
    {
        try
        {
            var character = lobby?.LocalPlayer.character;
            LocalCharacterCapture.PendingCharacterId = CharacterIdHelper.Extract(character);
            if (SlayTheStatsConfig.DebugMode) MainFile.Logger.Info($"[SlayTheStats] StartNewMultiplayerRun: captured local character '{LocalCharacterCapture.PendingCharacterId}'");
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[SlayTheStats] StartNewMultiplayerRun capture failed: {e.Message}");
        }
    }
}

// HandleLobbyBeginRunMessage is private — attribute-based resolution failed last time
// (the async TryBeginRun returned null in Harmony's lookup), so we resolve explicitly
// via AccessTools and target it through TargetMethod. Fires on every instance when the
// host broadcasts begin-loaded-run.
[HarmonyPatch]
public static class LoadRunLobbyBeginRunPatch
{
    static MethodBase? TargetMethod() => AccessTools.Method(typeof(LoadRunLobby), "HandleLobbyBeginRunMessage");

    static void Prefix(LoadRunLobby __instance)
    {
        try
        {
            var netId = __instance.NetService.NetId;
            LocalCharacterCapture.PendingLocalNetId = netId;
            if (SlayTheStatsConfig.DebugMode) MainFile.Logger.Info($"[SlayTheStats] LoadRunLobby.HandleLobbyBeginRunMessage: captured local NetId={netId}");
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[SlayTheStats] LoadRunLobby.HandleLobbyBeginRunMessage capture failed: {e.Message}");
        }
    }
}

/// <summary>
/// Sets run state as soon as a run starts. Reads character from the RunState parameter
/// (fully initialized before the method is called) rather than from the NGame instance
/// (which may not be fully set up yet).
/// </summary>
[HarmonyPatch(typeof(NGame), "StartRun")]
public static class StartRunPatch
{
    static void Prefix(RunState runState)
    {
        CardHoverShowPatch.IsInRun = true;
        if (SlayTheStatsConfig.DebugMode) MainFile.Logger.Info("[SlayTheStats] StartRun fired: IsInRun=true");
        try
        {
            var players = runState?.Players;
            if (players == null || players.Count == 0) { if (SlayTheStatsConfig.DebugMode) MainFile.Logger.Info("[SlayTheStats] StartRun: no players in runState"); return; }
            var id = LocalCharacterCapture.ConsumeCharacterId() ?? CharacterIdHelper.Extract(players[0].Character);
            if (id == null) { if (SlayTheStatsConfig.DebugMode) MainFile.Logger.Info("[SlayTheStats] StartRun: character id was null"); return; }
            CardHoverShowPatch.RunCharacter = id;
            if (SlayTheStatsConfig.DebugMode) MainFile.Logger.Info($"[SlayTheStats] StartRun: character set to '{id}'");
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[SlayTheStats] StartRun character extraction failed: {e.Message}");
        }
    }
}

/// <summary>
/// Sets the current run character when a saved run is loaded (Continue button, or returning
/// from a boss chest screen). LoadRun fires in all cases where StartRun does not — the two
/// together cover every path into a run.
/// </summary>
[HarmonyPatch(typeof(NGame), "LoadRun")]
public static class LoadRunPatch
{
    static void Prefix(RunState runState)
    {
        CardHoverShowPatch.IsInRun = true;
        if (SlayTheStatsConfig.DebugMode) MainFile.Logger.Info("[SlayTheStats] LoadRun fired: IsInRun=true");
        try
        {
            var players = runState?.Players;
            if (players == null || players.Count == 0) { if (SlayTheStatsConfig.DebugMode) MainFile.Logger.Info("[SlayTheStats] LoadRun: no players in runState"); return; }
            string? id = LocalCharacterCapture.ConsumeCharacterId();
            if (id == null)
            {
                var localNetId = LocalCharacterCapture.ConsumeLocalNetId();
                var localPlayer = localNetId.HasValue ? players.FirstOrDefault(p => p.NetId == localNetId.Value) : null;
                id = CharacterIdHelper.Extract((localPlayer ?? players[0]).Character);
            }
            if (id == null) { if (SlayTheStatsConfig.DebugMode) MainFile.Logger.Info("[SlayTheStats] LoadRun: character id was null"); return; }
            CardHoverShowPatch.RunCharacter = id;
            if (SlayTheStatsConfig.DebugMode) MainFile.Logger.Info($"[SlayTheStats] LoadRun: character set to '{id}'");
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"[SlayTheStats] LoadRun reflection failed: {e.Message}");
        }
    }
}

/// <summary>
/// Single cleanup and data refresh point. Fires on game boot and after every run ends
/// (returning to the main menu always loads this node). Clears run state and triggers
/// RunParser to process any new .run files (the just-completed run, or runs from before
/// the mod was installed on first boot).
/// </summary>
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
public static class MainMenuReadyPatch
{
    private static bool _devBannerShown;

    static void Postfix(NMainMenu __instance)
    {
        // Deferred here (not MainFile.Initialize) because LocManager.Instance
        // is null at mod-init; any LocString subscribe or GetTable call NREs.
        // By NMainMenu._Ready the locale system is up, so the font-fallback
        // subscription binds cleanly.
        TooltipHelper.InitLocaleSubscription();

        CardHoverShowPatch.RunCharacter = null;
        CardHoverShowPatch.IsInRun = false;
        if (SlayTheStatsConfig.DebugMode) MainFile.Logger.Info("[SlayTheStats] MainMenuReady: RunCharacter cleared, IsInRun=false");
        RunParser.ProcessNewRuns(MainFile.Db, MainFile.SavePath, msg => { if (SlayTheStatsConfig.DebugMode) MainFile.Logger.Info(msg); }, msg => MainFile.Logger.Warn(msg));

        if (!BuildInfo.IsRelease && !_devBannerShown)
        {
            _devBannerShown = true;
            ShowDevBuildBanner(__instance);
        }
    }

    private static string FormatBuildAge()
    {
        var age = DateTime.UtcNow - DateTime.Parse(BuildInfo.BuildUtc, null, System.Globalization.DateTimeStyles.RoundtripKind);
        var parts = new System.Collections.Generic.List<string>();
        if (age.Days > 0) parts.Add($"{age.Days}d");
        if (age.Hours > 0) parts.Add($"{age.Hours}h");
        if (age.Minutes > 0) parts.Add($"{age.Minutes}m");
        parts.Add($"{age.Seconds}s");
        return string.Join(" ", parts);
    }

    private static void ShowDevBuildBanner(NMainMenu menu)
    {
        var ageText = FormatBuildAge();
        var label = new Godot.RichTextLabel
        {
            BbcodeEnabled = true,
            Text = $"[center][outline_size=3][outline_color=#000000][color=#ffd700]SlayTheStats DEV {StatsDb.CurrentModVersion}[/color] [color=#ffffff]—[/color] [color=#66ff66]{BuildInfo.BuildDate}[/color] [color=#ffffff]—[/color] [color=#3388ff]{BuildInfo.BuildTime}[/color] [color=#ffffff]({ageText} ago)[/color][/outline_color][/outline_size][/center]",
            FitContent = true,
            ScrollActive = false,
            HorizontalAlignment = Godot.HorizontalAlignment.Center,
            AnchorLeft = 0f,
            AnchorRight = 1f,
            AnchorTop = 0f,
            AnchorBottom = 0f,
            OffsetTop = 12f,
            GrowHorizontal = Godot.Control.GrowDirection.Both,
        };
        label.AddThemeColorOverride("default_color", new Godot.Color(1f, 1f, 1f, 1f));
        label.AddThemeFontSizeOverride("normal_font_size", 18);
        menu.AddChild(label);

        var tween = label.CreateTween();
        tween.TweenInterval(10.0);
        tween.TweenProperty(label, "modulate:a", 0.0f, 1.0);
        tween.TweenCallback(Godot.Callable.From(label.QueueFree));
    }
}
