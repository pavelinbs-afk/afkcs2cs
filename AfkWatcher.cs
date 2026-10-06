using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace AFK;

public sealed partial class AfkPlugin
{
    private HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        Server.NextFrame(CaptureAfkRoundActivityBaseline);
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        StopAfkTrackingForPhaseChange();
        Server.NextFrame(TryApplySmartBalanceAtRoundEnd);
        return HookResult.Continue;
    }

    private void ClearAfkState()
    {
        _afkTrackingActive = false;
        _afkRoundStartedAtUnix = 0;
        _afkLastActivityUnixBySteam.Clear();
        _afkLastOriginBySteam.Clear();
        _afkLastLookBySteam.Clear();
        _afkWarnedSoonBySteam.Clear();
        _afkChatWarnedBySteam.Clear();
        _afkWarnDeadlineUnixBySteam.Clear();
        _afkWarnLastShownSecondsBySteam.Clear();
        _afkWarningHudsBySteam.Clear();
    }

    private void StopAfkTrackingForPhaseChange()
    {
        _afkTrackingActive = false;
        _afkRoundStartedAtUnix = 0;
        foreach (ulong sid in _afkWarningHudsBySteam.Keys.ToList())
        {
            StopAfkWarningHudBySid(sid);
        }

        _afkLastActivityUnixBySteam.Clear();
        _afkLastOriginBySteam.Clear();
        _afkLastLookBySteam.Clear();
        _afkWarnedSoonBySteam.Clear();
        _afkChatWarnedBySteam.Clear();
        _afkWarnDeadlineUnixBySteam.Clear();
        _afkWarnLastShownSecondsBySteam.Clear();
    }

    private void CaptureAfkRoundActivityBaseline()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _afkRoundStartedAtUnix = now;
        _afkTrackingActive = true;

        _afkLastActivityUnixBySteam.Clear();
        _afkLastOriginBySteam.Clear();
        _afkLastLookBySteam.Clear();
        _afkWarnedSoonBySteam.Clear();
        _afkChatWarnedBySteam.Clear();
        _afkWarnDeadlineUnixBySteam.Clear();
        _afkWarnLastShownSecondsBySteam.Clear();

        foreach (CCSPlayerController p in Utilities.GetPlayers())
        {
            if (p == null || !p.IsValid || p.IsBot)
            {
                continue;
            }

            ulong sid = p.AuthorizedSteamID?.SteamId64 ?? 0UL;
            if (sid == 0UL || !IsAfkTrackablePlayer(p, sid))
            {
                continue;
            }

            SeedAfkActivityBaseline(p, sid, now);
        }
    }

    private void SeedAfkActivityBaseline(CCSPlayerController p, ulong sid, long now)
    {
        Vector? origin = TryGetPlayerPawnOrigin(p);
        if (origin != null)
        {
            _afkLastOriginBySteam[sid] = new Vector(origin.X, origin.Y, origin.Z);
        }

        if (TryGetPlayerLookAngles(p, out float pitch, out float yaw))
        {
            _afkLastLookBySteam[sid] = (pitch, yaw);
        }

        _afkLastActivityUnixBySteam[sid] = now;
        _afkWarnedSoonBySteam.Remove(sid);
        _afkChatWarnedBySteam.Remove(sid);
        _afkWarnDeadlineUnixBySteam.Remove(sid);
        _afkWarnLastShownSecondsBySteam.Remove(sid);
        StopAfkWarningHudBySid(sid);
    }

    private void PollAfkWatchdog()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        float moveSqThreshold = AfkMoveUnitsThreshold * AfkMoveUnitsThreshold;
        var activeSteamIds = new HashSet<ulong>();

        foreach (CCSPlayerController p in Utilities.GetPlayers())
        {
            if (p == null || !p.IsValid || p.IsBot)
            {
                continue;
            }

            ulong sid = p.AuthorizedSteamID?.SteamId64 ?? 0UL;
            if (sid == 0UL || !IsAfkTrackablePlayer(p, sid))
            {
                continue;
            }

            activeSteamIds.Add(sid);
            if (!_afkLastActivityUnixBySteam.ContainsKey(sid))
            {
                SeedAfkActivityBaseline(p, sid, now);
            }

            bool activityDetected = false;
            Vector? nowOrigin = TryGetPlayerPawnOrigin(p);
            if (nowOrigin != null)
            {
                if (_afkLastOriginBySteam.TryGetValue(sid, out Vector? prevOrigin) && prevOrigin != null)
                {
                    if (DistanceSquared(prevOrigin, nowOrigin) > moveSqThreshold)
                    {
                        activityDetected = true;
                    }
                }

                _afkLastOriginBySteam[sid] = new Vector(nowOrigin.X, nowOrigin.Y, nowOrigin.Z);
            }

            if (TryGetPlayerLookAngles(p, out float pitch, out float yaw))
            {
                if (_afkLastLookBySteam.TryGetValue(sid, out (float Pitch, float Yaw) prevLook))
                {
                    float pitchDelta = MathF.Abs(pitch - prevLook.Pitch);
                    float yawDelta = MathF.Abs(NormalizeYawDelta(yaw - prevLook.Yaw));
                    if (pitchDelta >= AfkMouseLookDegreesThreshold || yawDelta >= AfkMouseLookDegreesThreshold)
                    {
                        activityDetected = true;
                    }
                }

                _afkLastLookBySteam[sid] = (pitch, yaw);
            }

            if (activityDetected)
            {
                _afkLastActivityUnixBySteam[sid] = now;
                _afkWarnedSoonBySteam.Remove(sid);
                _afkChatWarnedBySteam.Remove(sid);
                _afkWarnDeadlineUnixBySteam.Remove(sid);
                _afkWarnLastShownSecondsBySteam.Remove(sid);
                StopAfkWarningHudBySid(sid);
            }

            long lastActivity = _afkLastActivityUnixBySteam.TryGetValue(sid, out long act) ? act : now;
            long idleSeconds = now - lastActivity;
            long secondsLeft = AfkIdleSecondsBeforeSpectator - idleSeconds;

            if (secondsLeft <= AfkChatWarnBeforeSpectatorSeconds &&
                _afkChatWarnedBySteam.Add(sid))
            {
                p.PrintToChat(
                    $" {ChatColors.Red}[Сервер] →{ChatColors.Default} Вы будете переведены в наблюдатели через {ChatColors.Red}{AfkChatWarnBeforeSpectatorSeconds}{ChatColors.Default} сек, если не начнёте двигаться.");
            }

            bool hasAtWarnDeadline = _afkWarnDeadlineUnixBySteam.TryGetValue(sid, out long warnDeadlineUnix);
            if (!hasAtWarnDeadline && secondsLeft <= AfkWarnBeforeSpectatorSeconds)
            {
                _afkWarnedSoonBySteam.Add(sid);
                warnDeadlineUnix = now + AfkWarnBeforeSpectatorSeconds;
                _afkWarnDeadlineUnixBySteam[sid] = warnDeadlineUnix;
                _afkWarnLastShownSecondsBySteam[sid] = AfkWarnBeforeSpectatorSeconds;
                ShowAfkWarningHud(p, sid, warnDeadlineUnix);
                hasAtWarnDeadline = true;
            }

            if (hasAtWarnDeadline)
            {
                if (!_afkWarningHudsBySteam.ContainsKey(sid))
                {
                    ShowAfkWarningHud(p, sid, warnDeadlineUnix);
                }

                int countdownLeft = Math.Max(0, (int)(warnDeadlineUnix - now));
                if (countdownLeft > 0 &&
                    (!_afkWarnLastShownSecondsBySteam.TryGetValue(sid, out int lastShown) || lastShown != countdownLeft))
                {
                    _afkWarnLastShownSecondsBySteam[sid] = countdownLeft;
                }

                if (now < warnDeadlineUnix)
                {
                    continue;
                }
            }
            else if (now - lastActivity < AfkIdleSecondsBeforeSpectator)
            {
                continue;
            }

            _afkLastActivityUnixBySteam[sid] = now;
            _afkLastOriginBySteam.Remove(sid);
            _afkLastLookBySteam.Remove(sid);
            _afkWarnedSoonBySteam.Remove(sid);
            _afkChatWarnedBySteam.Remove(sid);
            _afkWarnDeadlineUnixBySteam.Remove(sid);
            _afkWarnLastShownSecondsBySteam.Remove(sid);
            StopAfkWarningHudBySid(sid);

            string displayName = string.IsNullOrWhiteSpace(p.PlayerName) ? "Игрок" : p.PlayerName.Trim();
            ApplyAfkSpectatorTransfer(p, sid, displayName);
        }

        foreach (ulong sid in _afkLastActivityUnixBySteam.Keys.ToList())
        {
            if (activeSteamIds.Contains(sid))
            {
                continue;
            }

            _afkLastActivityUnixBySteam.Remove(sid);
            _afkLastOriginBySteam.Remove(sid);
            _afkLastLookBySteam.Remove(sid);
            _afkWarnedSoonBySteam.Remove(sid);
            _afkChatWarnedBySteam.Remove(sid);
            _afkWarnDeadlineUnixBySteam.Remove(sid);
            _afkWarnLastShownSecondsBySteam.Remove(sid);
            StopAfkWarningHudBySid(sid);
        }
    }

    private bool IsAfkTrackablePlayer(CCSPlayerController player, ulong sid)
    {
        _ = sid;
        if (!_afkTrackingActive || _afkRoundStartedAtUnix <= 0)
        {
            return false;
        }

        CCSGameRules? rules = TryGetGameRules();
        if (rules != null && (rules.WarmupPeriod || rules.FreezePeriod))
        {
            return false;
        }

        // Ботов уже отфильтровали; без флага cheat-check — только живые T/CT (не spectator).
        if (player.Team != CsTeam.Terrorist && player.Team != CsTeam.CounterTerrorist)
        {
            return false;
        }

        return IsControllerAliveOnTeam(player);
    }

    private void MovePlayerToSpectatorsSafely(CCSPlayerController player, ulong sid64)
    {
        if (player == null || !player.IsValid || player.IsBot || sid64 == 0UL)
        {
            return;
        }

        TrySuicideBeforeSpectatorMove(player);
        Server.NextFrame(() => ApplySpectatorTeamAfterSuicide(sid64));
        ScheduleEnsureSpectatorTabVisible(sid64);
    }

    private static void TrySuicideBeforeSpectatorMove(CCSPlayerController player)
    {
        if (!player.IsValid || player.IsBot || !player.PawnIsAlive)
        {
            return;
        }

        try
        {
            player.CommitSuicide(explode: false, force: true);
        }
        catch
        {
            try
            {
                player.PlayerPawn.Value?.CommitSuicide(explode: false, force: true);
            }
            catch
            {
                // ignore
            }
        }
    }

    private void ApplySpectatorTeamAfterSuicide(ulong sid64)
    {
        try
        {
            CCSPlayerController? pl = Utilities.GetPlayerFromSteamId64(sid64);
            if (pl == null || !pl.IsValid || pl.IsBot)
            {
                return;
            }

            ForcePlayerToSpectatorKeepInTab(pl);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[AFK] ApplySpectatorTeamAfterSuicide sid={Sid}", sid64);
        }
    }

    /// <summary>
    /// Spectator так, чтобы остаться в табе. НИКОГДА не ChangeTeam(None).
    /// </summary>
    private void ForcePlayerToSpectatorKeepInTab(CCSPlayerController player)
    {
        if (player == null || !player.IsValid || player.IsBot)
        {
            return;
        }

        try
        {
            if (player.Team != CsTeam.Spectator)
            {
                player.ChangeTeam(CsTeam.Spectator);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[AFK] ChangeTeam Spectator failed");
        }

        if (player.Team != CsTeam.Spectator)
        {
            ForceNativeJoinTeam(player, CsTeam.Spectator);
        }

        if (player.Team == CsTeam.None)
        {
            ForceNativeJoinTeam(player, CsTeam.Spectator);
            try
            {
                player.ChangeTeam(CsTeam.Spectator);
            }
            catch
            {
                // ignore
            }
        }
    }

    private void ApplyAfkSpectatorTransfer(CCSPlayerController player, ulong sid64, string displayName)
    {
        MovePlayerToSpectatorsSafely(player, sid64);

        void VerifyAndAnnounce(int attempt)
        {
            try
            {
                CCSPlayerController? pl = Utilities.GetPlayerFromSteamId64(sid64);
                if (pl == null || !pl.IsValid || pl.IsBot)
                {
                    return;
                }

                if (pl.Team == CsTeam.Spectator)
                {
                    PrintChatToAllPlayers(
                        $" {ChatColors.Red}[Сервер] →{ChatColors.Default} Игрок {ChatColors.LightPurple}{displayName}{ChatColors.Default} переведён в наблюдатели из-за бездействия (АФК).");
                    pl.PrintToChat(
                        $" {ChatColors.Red}[Сервер] →{ChatColors.Default} Вы переведены в наблюдатели из-за бездействия (АФК).");
                    return;
                }

                if (attempt >= 6)
                {
                    Logger.LogWarning(
                        "[AFK] AFK transfer failed for {Name} (team={Team}, attempt={Attempt})",
                        displayName,
                        pl.Team,
                        attempt);
                    ForcePlayerToSpectatorKeepInTab(pl);
                    return;
                }

                if (pl.Team is CsTeam.None or CsTeam.Terrorist or CsTeam.CounterTerrorist)
                {
                    if (pl.PawnIsAlive)
                    {
                        TrySuicideBeforeSpectatorMove(pl);
                    }

                    ForcePlayerToSpectatorKeepInTab(pl);
                }

                float delay = attempt switch
                {
                    0 => 0.2f,
                    1 => 0.4f,
                    2 => 0.7f,
                    3 => 1.0f,
                    _ => 1.25f
                };
                AddTimer(delay, () => VerifyAndAnnounce(attempt + 1));
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[AFK] ApplyAfkSpectatorTransfer verify sid={Sid}", sid64);
            }
        }

        AddTimer(0.15f, () => VerifyAndAnnounce(0));
    }

    private void ScheduleEnsureSpectatorTabVisible(ulong sid64)
    {
        void Verify()
        {
            try
            {
                CCSPlayerController? pl = Utilities.GetPlayerFromSteamId64(sid64);
                if (pl == null || !pl.IsValid || pl.IsBot || pl.Team == CsTeam.Spectator)
                {
                    return;
                }

                if (pl.PawnIsAlive &&
                    (pl.Team == CsTeam.Terrorist || pl.Team == CsTeam.CounterTerrorist))
                {
                    TrySuicideBeforeSpectatorMove(pl);
                }

                ForcePlayerToSpectatorKeepInTab(pl);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[AFK] EnsureSpectatorTabVisible sid={Sid}", sid64);
            }
        }

        Server.NextFrame(Verify);
        AddTimer(0.2f, Verify);
        AddTimer(0.5f, Verify);
        AddTimer(1.0f, Verify);
        AddTimer(2.0f, Verify);
        AddTimer(3.0f, Verify);
    }

    private static string BuildAfkWarningCenterHtml(int secondsLeft)
    {
        int safeSeconds = Math.Max(0, secondsLeft);
        return
            "<font color='#ffcc33'>AFK предупреждение</font><br/>" +
            "<font color='#ffffff'>Перевод в наблюдатели через </font>" +
            $"<font color='#ff6666'>{safeSeconds}</font><font color='#ffffff'> сек.</font>";
    }

    private void ShowAfkWarningHud(CCSPlayerController player, ulong sid, long warnDeadlineUnix)
    {
        StopAfkWarningHudBySid(sid);

        const int afkHudChunkMs = 2200;
        player.PrintToCenterHtml(
            BuildAfkWarningCenterHtml((int)Math.Max(0, warnDeadlineUnix - DateTimeOffset.UtcNow.ToUnixTimeSeconds())),
            afkHudChunkMs);

        CounterStrikeSharp.API.Modules.Timers.Timer? repeat = null;
        repeat = AddTimer(
            0.08f,
            () =>
            {
                try
                {
                    if (!_afkWarnDeadlineUnixBySteam.TryGetValue(sid, out long deadline))
                    {
                        StopAfkWarningHudBySid(sid);
                        return;
                    }

                    CCSPlayerController? pl = Utilities.GetPlayerFromSteamId64(sid);
                    if (pl == null || !pl.IsValid || pl.IsBot)
                    {
                        return;
                    }

                    int left = Math.Max(0, (int)(deadline - DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                    pl.PrintToCenterHtml(BuildAfkWarningCenterHtml(left), afkHudChunkMs);
                }
                catch
                {
                    StopAfkWarningHudBySid(sid);
                }
            },
            TimerFlags.REPEAT);

        CounterStrikeSharp.API.Modules.Timers.Timer expire = AddTimer(
            AfkWarnBeforeSpectatorSeconds + 2f,
            () => StopAfkWarningHudBySid(sid));

        _afkWarningHudsBySteam[sid] = new SustainedCenterHud { Repeat = repeat!, Expire = expire };
    }

    private void StopAfkWarningHudBySid(ulong sid)
    {
        if (!_afkWarningHudsBySteam.Remove(sid, out SustainedCenterHud? hud))
        {
            return;
        }

        try
        {
            hud.Repeat?.Kill();
        }
        catch
        {
        }

        try
        {
            hud.Expire?.Kill();
        }
        catch
        {
        }
    }

    private HookResult OnAfkInputActivity(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid || player.IsBot)
        {
            return HookResult.Continue;
        }

        ulong sid = player.AuthorizedSteamID?.SteamId64 ?? 0UL;
        if (sid == 0UL || !IsAfkTrackablePlayer(player, sid))
        {
            return HookResult.Continue;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _afkLastActivityUnixBySteam[sid] = now;
        _afkWarnedSoonBySteam.Remove(sid);
        _afkChatWarnedBySteam.Remove(sid);
        _afkWarnDeadlineUnixBySteam.Remove(sid);
        _afkWarnLastShownSecondsBySteam.Remove(sid);
        StopAfkWarningHudBySid(sid);

        Vector? origin = TryGetPlayerPawnOrigin(player);
        if (origin != null)
        {
            _afkLastOriginBySteam[sid] = new Vector(origin.X, origin.Y, origin.Z);
        }

        if (TryGetPlayerLookAngles(player, out float pitch, out float yaw))
        {
            _afkLastLookBySteam[sid] = (pitch, yaw);
        }

        return HookResult.Continue;
    }
}
