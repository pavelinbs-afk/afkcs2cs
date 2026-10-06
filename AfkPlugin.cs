using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace AFK;

public sealed partial class AfkPlugin : BasePlugin
{
    public override string ModuleName => "AFK";
    public override string ModuleAuthor => "pRfect";
    public override string ModuleVersion => "1.0.1";
    public override string ModuleDescription => "AFK → spectator + smart level-based team balance";

    /// <summary>AFK: перевод в наблюдатели после N секунд бездействия (только после конца freeze текущего раунда).</summary>
    private const int AfkIdleSecondsBeforeSpectator = 60;
    private const float AfkMoveUnitsThreshold = 3f;
    private const float AfkMouseLookDegreesThreshold = 0.2f;
    private const float AfkWatchdogIntervalSeconds = 1f;
    private const int AfkWarnBeforeSpectatorSeconds = 10;
    private const int AfkChatWarnBeforeSpectatorSeconds = 30;

    /// <summary>Разрешённая разница |T−CT| по числу игроков.</summary>
    private const int BalanceMaxCountDiff = 1;
    /// <summary>Порог перебаланса по сумме уровней (абсолютный минимум).</summary>
    private const int BalanceMinLevelSumSkew = 4;
    /// <summary>Относительный порог: доля от суммарного уровня обеих команд.</summary>
    private const float BalanceLevelSumSkewRatio = 0.18f;

    private const string TeamFullNotifyText = "Команда переполнена.";
    private const string TeamBalanceMovedNotifyText =
        "Вас перевели в другую команду для сохранения игрового баланса.";

    private LevelResolver _levels = null!;
    private readonly HashSet<int> _bypassTeamBalanceJoinSlots = new();

    private readonly Dictionary<ulong, Vector> _afkLastOriginBySteam = new();
    private readonly Dictionary<ulong, (float Pitch, float Yaw)> _afkLastLookBySteam = new();
    private readonly Dictionary<ulong, long> _afkLastActivityUnixBySteam = new();
    private readonly HashSet<ulong> _afkWarnedSoonBySteam = new();
    private readonly HashSet<ulong> _afkChatWarnedBySteam = new();
    private readonly Dictionary<ulong, long> _afkWarnDeadlineUnixBySteam = new();
    private readonly Dictionary<ulong, int> _afkWarnLastShownSecondsBySteam = new();
    private readonly Dictionary<ulong, SustainedCenterHud> _afkWarningHudsBySteam = new();

    private long _afkRoundStartedAtUnix;
    private bool _afkTrackingActive;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _afkWatchdogTimer;

    private static readonly string[] AfkActivityCommands =
    [
        "+forward", "+back", "+moveleft", "+moveright", "+left", "+right",
        "+jump", "+duck", "+attack", "+attack2", "+reload", "+use",
        "invnext", "invprev", "lastinv",
        "slot1", "slot2", "slot3", "slot4", "slot5",
        "slot6", "slot7", "slot8", "slot9", "slot10",
        "drop", "buy", "buymenu"
    ];

    public override void Load(bool hotReload)
    {
        _levels = new LevelResolver(Logger, ModuleDirectory);
        _levels.TryBindLevelsRanks();

        RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventPlayerTeam>(OnPlayerTeamBalanceLimit, HookMode.Pre);

        foreach (string cmd in AfkActivityCommands)
        {
            AddCommandListener(cmd, OnAfkInputActivity);
        }

        AddCommandListener("jointeam", OnJoinTeamBalanceLimit, HookMode.Pre);

        // Опциональный мост: css_afk_setlevel <steamid64> <level>
        AddCommand("css_afk_setlevel", "Cache player level for smart balance", OnSetLevelCommand);

        _afkWatchdogTimer?.Kill();
        _afkWatchdogTimer = AddTimer(AfkWatchdogIntervalSeconds, PollAfkWatchdog, TimerFlags.REPEAT);

        // LR может подняться после нас.
        AddTimer(2f, () => _levels.TryBindLevelsRanks());
        AddTimer(8f, () => _levels.TryBindLevelsRanks());

        Logger.LogInformation("[AFK] Loaded (AFK spectator + smart balance). hotReload={Hot}", hotReload);
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _levels.TryBindLevelsRanks();
    }

    public override void Unload(bool hotReload)
    {
        _afkWatchdogTimer?.Kill();
        _afkWatchdogTimer = null;

        foreach (ulong sid in _afkWarningHudsBySteam.Keys.ToList())
        {
            StopAfkWarningHudBySid(sid);
        }

        ClearAfkState();
        Logger.LogInformation("[AFK] Unloaded hotReload={Hot}", hotReload);
    }

    private void OnSetLevelCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (info.ArgCount < 3 ||
            !ulong.TryParse(info.GetArg(1), out ulong sid) || sid == 0UL ||
            !int.TryParse(info.GetArg(2), out int level) || level < 1)
        {
            info.ReplyToCommand("Usage: css_afk_setlevel <steamid64> <level>");
            return;
        }

        _levels.SetCachedLevel(sid, level, persist: true);
        info.ReplyToCommand($"[AFK] Cached level {level} for {sid}");
    }

    private static void PrintChatToAllPlayers(string message)
    {
        foreach (CCSPlayerController p in Utilities.GetPlayers())
        {
            if (p.IsBot || !p.IsValid)
            {
                continue;
            }

            p.PrintToChat(message);
        }
    }

    private static CCSGameRules? TryGetGameRules()
    {
        return Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault()
            ?.GameRules;
    }

    private static bool IsControllerAliveOnTeam(CCSPlayerController p)
    {
        if (p.Team != CsTeam.Terrorist && p.Team != CsTeam.CounterTerrorist)
        {
            return false;
        }

        return p.PawnIsAlive;
    }

    private static Vector? TryGetPlayerPawnOrigin(CCSPlayerController player)
    {
        CCSPlayerPawn? pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
        {
            return null;
        }

        CGameSceneNode? node = pawn.CBodyComponent?.SceneNode;
        return node?.AbsOrigin;
    }

    private static float DistanceSquared(Vector a, Vector b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    private static float NormalizeYawDelta(float delta)
    {
        while (delta > 180f)
        {
            delta -= 360f;
        }

        while (delta < -180f)
        {
            delta += 360f;
        }

        return delta;
    }

    private static bool TryGetPlayerLookAngles(CCSPlayerController player, out float pitch, out float yaw)
    {
        pitch = 0f;
        yaw = 0f;
        CCSPlayerPawn? pawn = player.PlayerPawn.Value;
        if (pawn == null)
        {
            return false;
        }

        static bool TryReadAnglesObject(object obj, out float p, out float y)
        {
            p = 0f;
            y = 0f;
            Type t = obj.GetType();
            var pitchProp = t.GetProperty("X") ?? t.GetProperty("Pitch");
            var yawProp = t.GetProperty("Y") ?? t.GetProperty("Yaw");
            if (pitchProp == null || yawProp == null)
            {
                return false;
            }

            object? pitchObj = pitchProp.GetValue(obj);
            object? yawObj = yawProp.GetValue(obj);
            if (pitchObj == null || yawObj == null)
            {
                return false;
            }

            try
            {
                p = Convert.ToSingle(pitchObj, CultureInfo.InvariantCulture);
                y = Convert.ToSingle(yawObj, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        string[] props = ["EyeAngles", "V_angle", "AbsRotation"];
        foreach (string propName in props)
        {
            var prop = pawn.GetType().GetProperty(propName);
            if (prop == null)
            {
                continue;
            }

            object? val = prop.GetValue(pawn);
            if (val == null)
            {
                continue;
            }

            if (TryReadAnglesObject(val, out pitch, out yaw))
            {
                return true;
            }
        }

        return false;
    }

    private static int CountHumanPlayersOnTeamExcluding(CsTeam team, CCSPlayerController? exclude)
    {
        int count = 0;
        foreach (CCSPlayerController p in Utilities.GetPlayers())
        {
            if (p == null || !p.IsValid || p.IsBot || p.Connected != PlayerConnectedState.Connected)
            {
                continue;
            }

            if (exclude != null && p.Slot == exclude.Slot)
            {
                continue;
            }

            if (p.Team == team)
            {
                count++;
            }
        }

        return count;
    }

    private static bool TryParseJoinTeamArg(CommandInfo info, out int teamArg)
    {
        teamArg = -1;
        if (info.ArgCount < 2)
        {
            return false;
        }

        return int.TryParse(info.GetArg(1), out teamArg);
    }

    private sealed class SustainedCenterHud
    {
        public CounterStrikeSharp.API.Modules.Timers.Timer Repeat = null!;
        public CounterStrikeSharp.API.Modules.Timers.Timer Expire = null!;
    }
}
