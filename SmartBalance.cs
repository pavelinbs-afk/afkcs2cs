using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace AFK;

public sealed partial class AfkPlugin
{
    private sealed record BalancePlayer(CCSPlayerController Controller, ulong SteamId64, CsTeam Team, int Level);

    private void TryApplySmartBalanceAtRoundEnd()
    {
        List<BalancePlayer> roster = BuildActiveRoster();
        if (roster.Count < 2)
        {
            return;
        }

        int tCount = roster.Count(p => p.Team == CsTeam.Terrorist);
        int ctCount = roster.Count(p => p.Team == CsTeam.CounterTerrorist);
        int sumT = roster.Where(p => p.Team == CsTeam.Terrorist).Sum(p => p.Level);
        int sumCt = roster.Where(p => p.Team == CsTeam.CounterTerrorist).Sum(p => p.Level);
        int countDiff = Math.Abs(tCount - ctCount);
        int sumSkew = Math.Abs(sumT - sumCt);
        int skewThreshold = ComputeLevelSkewThreshold(sumT + sumCt);

        bool needCount = countDiff >= 2;
        bool needSkill = sumSkew > skewThreshold;
        if (!needCount && !needSkill)
        {
            return;
        }

        // 1) Выровнять число: переносить игрока, чьей перенос сильнее уменьшает |sumT−sumCT|.
        int guard = 0;
        while (Math.Abs(tCount - ctCount) >= 2 && guard++ < 8)
        {
            CsTeam from = tCount > ctCount ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
            CsTeam to = from == CsTeam.Terrorist ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
            BalancePlayer? pick = PickBestMoveCandidate(roster, from, to, sumT, sumCt, preferMidImpact: true);
            if (pick == null)
            {
                break;
            }

            ApplyBalanceMove(pick, to);
            roster = BuildActiveRoster();
            tCount = roster.Count(p => p.Team == CsTeam.Terrorist);
            ctCount = roster.Count(p => p.Team == CsTeam.CounterTerrorist);
            sumT = roster.Where(p => p.Team == CsTeam.Terrorist).Sum(p => p.Level);
            sumCt = roster.Where(p => p.Team == CsTeam.CounterTerrorist).Sum(p => p.Level);
        }

        // 2) Выровнять силу при |countDiff| <= 1.
        skewThreshold = ComputeLevelSkewThreshold(sumT + sumCt);
        guard = 0;
        while (Math.Abs(sumT - sumCt) > skewThreshold && Math.Abs(tCount - ctCount) <= BalanceMaxCountDiff && guard++ < 6)
        {
            CsTeam strong = sumT >= sumCt ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
            CsTeam weak = strong == CsTeam.Terrorist ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
            int strongCount = strong == CsTeam.Terrorist ? tCount : ctCount;
            int weakCount = strong == CsTeam.Terrorist ? ctCount : tCount;

            if (strongCount <= weakCount)
            {
                if (!TrySwapForSkillBalance(roster, sumT, sumCt))
                {
                    break;
                }
            }
            else
            {
                BalancePlayer? pick = PickBestMoveCandidate(roster, strong, weak, sumT, sumCt, preferMidImpact: true);
                if (pick == null)
                {
                    if (!TrySwapForSkillBalance(roster, sumT, sumCt))
                    {
                        break;
                    }
                }
                else
                {
                    ApplyBalanceMove(pick, weak);
                }
            }

            roster = BuildActiveRoster();
            tCount = roster.Count(p => p.Team == CsTeam.Terrorist);
            ctCount = roster.Count(p => p.Team == CsTeam.CounterTerrorist);
            sumT = roster.Where(p => p.Team == CsTeam.Terrorist).Sum(p => p.Level);
            sumCt = roster.Where(p => p.Team == CsTeam.CounterTerrorist).Sum(p => p.Level);
            skewThreshold = ComputeLevelSkewThreshold(sumT + sumCt);
        }

        Logger.LogInformation(
            "[AFK] Smart balance done: T={TCount}/{TSum} CT={CtCount}/{CtSum}",
            tCount, sumT, ctCount, sumCt);
    }

    private static int ComputeLevelSkewThreshold(int totalLevelSum)
    {
        int relative = (int)MathF.Ceiling(totalLevelSum * BalanceLevelSumSkewRatio);
        return Math.Max(BalanceMinLevelSumSkew, relative);
    }

    private List<BalancePlayer> BuildActiveRoster()
    {
        var list = new List<BalancePlayer>();
        foreach (CCSPlayerController p in Utilities.GetPlayers())
        {
            if (p == null || !p.IsValid || p.IsBot || p.Connected != PlayerConnectedState.Connected)
            {
                continue;
            }

            if (p.Team != CsTeam.Terrorist && p.Team != CsTeam.CounterTerrorist)
            {
                continue;
            }

            ulong sid = p.AuthorizedSteamID?.SteamId64 ?? 0UL;
            if (sid == 0UL)
            {
                continue;
            }

            list.Add(new BalancePlayer(p, sid, p.Team, _levels.GetLevel(sid)));
        }

        return list;
    }

    /// <summary>
    /// Кандидат на перенос: минимизирует |sum после|, предпочитая mid/low impact.
    /// </summary>
    private static BalancePlayer? PickBestMoveCandidate(
        List<BalancePlayer> roster,
        CsTeam from,
        CsTeam to,
        int sumT,
        int sumCt,
        bool preferMidImpact)
    {
        _ = to;
        List<BalancePlayer> fromPlayers = roster.Where(p => p.Team == from).ToList();
        if (fromPlayers.Count == 0)
        {
            return null;
        }

        int median = fromPlayers.OrderBy(p => p.Level).Select(p => p.Level).ElementAt(fromPlayers.Count / 2);
        int maxLevel = fromPlayers.Max(p => p.Level);

        BalancePlayer? best = null;
        long bestScore = long.MaxValue;

        foreach (BalancePlayer candidate in fromPlayers)
        {
            int newSumT = sumT;
            int newSumCt = sumCt;
            if (from == CsTeam.Terrorist)
            {
                newSumT -= candidate.Level;
                newSumCt += candidate.Level;
            }
            else
            {
                newSumCt -= candidate.Level;
                newSumT += candidate.Level;
            }

            long skew = Math.Abs(newSumT - newSumCt);
            long impactPenalty = 0;
            if (preferMidImpact)
            {
                impactPenalty = Math.Abs(candidate.Level - median) * 2L;
                if (candidate.Level == maxLevel && fromPlayers.Count > 1)
                {
                    impactPenalty += 50;
                }
            }

            long alivePenalty = candidate.Controller.PawnIsAlive ? 3 : 0;
            long score = skew * 100 + impactPenalty + alivePenalty;
            if (score < bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    private bool TrySwapForSkillBalance(List<BalancePlayer> roster, int sumT, int sumCt)
    {
        List<BalancePlayer> t = roster.Where(p => p.Team == CsTeam.Terrorist).ToList();
        List<BalancePlayer> ct = roster.Where(p => p.Team == CsTeam.CounterTerrorist).ToList();
        if (t.Count == 0 || ct.Count == 0)
        {
            return false;
        }

        int currentSkew = Math.Abs(sumT - sumCt);
        BalancePlayer? bestA = null;
        BalancePlayer? bestB = null;
        int bestSkew = currentSkew;

        foreach (BalancePlayer a in t)
        {
            foreach (BalancePlayer b in ct)
            {
                int newSumT = sumT - a.Level + b.Level;
                int newSumCt = sumCt - b.Level + a.Level;
                int skew = Math.Abs(newSumT - newSumCt);
                if (skew + 1 < bestSkew)
                {
                    bestSkew = skew;
                    bestA = a;
                    bestB = b;
                }
            }
        }

        if (bestA == null || bestB == null || bestSkew >= currentSkew)
        {
            return false;
        }

        ApplyBalanceMove(bestA, CsTeam.CounterTerrorist);
        ApplyBalanceMove(bestB, CsTeam.Terrorist);
        return true;
    }

    private void ApplyBalanceMove(BalancePlayer player, CsTeam toTeam)
    {
        if (!player.Controller.IsValid)
        {
            return;
        }

        string name = string.IsNullOrWhiteSpace(player.Controller.PlayerName)
            ? "Игрок"
            : player.Controller.PlayerName.Trim();
        MovePlayerToTeamForBalance(player.Controller, toTeam);
        NotifyTeamBalanceMoved(player.Controller);
        Logger.LogInformation(
            "[AFK] Balance move: {Name} (lvl {Lvl}) → {Team}",
            name,
            player.Level,
            toTeam == CsTeam.CounterTerrorist ? "CT" : "T");
    }

    /// <summary>
    /// Блок входа в переполненную по числу команду; при равенстве —
    /// направляем туда, где сумма уровней после входа ближе.
    /// </summary>
    private bool IsTeamJoinBlockedBySmartBalance(
        CsTeam desiredTeam,
        CCSPlayerController player,
        out CsTeam suggestedTeam)
    {
        suggestedTeam = desiredTeam;
        if (desiredTeam != CsTeam.Terrorist && desiredTeam != CsTeam.CounterTerrorist)
        {
            return false;
        }

        if (player.Team == desiredTeam)
        {
            return false;
        }

        int t = CountHumanPlayersOnTeamExcluding(CsTeam.Terrorist, player);
        int ct = CountHumanPlayersOnTeamExcluding(CsTeam.CounterTerrorist, player);
        int playerLevel = _levels.GetLevel(player);

        if (desiredTeam == CsTeam.Terrorist && t > ct)
        {
            suggestedTeam = CsTeam.CounterTerrorist;
            return true;
        }

        if (desiredTeam == CsTeam.CounterTerrorist && ct > t)
        {
            suggestedTeam = CsTeam.Terrorist;
            return true;
        }

        if (t == ct)
        {
            int sumT = SumTeamLevelsExcluding(CsTeam.Terrorist, player);
            int sumCt = SumTeamLevelsExcluding(CsTeam.CounterTerrorist, player);
            int skewIfT = Math.Abs((sumT + playerLevel) - sumCt);
            int skewIfCt = Math.Abs(sumT - (sumCt + playerLevel));

            CsTeam better = skewIfT <= skewIfCt ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
            if (better != desiredTeam && Math.Abs(skewIfT - skewIfCt) >= 2)
            {
                suggestedTeam = better;
                return true;
            }
        }

        return false;
    }

    private int SumTeamLevelsExcluding(CsTeam team, CCSPlayerController exclude)
    {
        int sum = 0;
        foreach (CCSPlayerController p in Utilities.GetPlayers())
        {
            if (p == null || !p.IsValid || p.IsBot || p.Connected != PlayerConnectedState.Connected)
            {
                continue;
            }

            if (p.Slot == exclude.Slot || p.Team != team)
            {
                continue;
            }

            sum += _levels.GetLevel(p);
        }

        return sum;
    }

    private static void NotifyTeamBalanceBlocked(CCSPlayerController player)
    {
        try
        {
            player.PrintToCenter(TeamFullNotifyText);
        }
        catch
        {
            // ignore
        }

        player.PrintToChat(
            $" {ChatColors.Red}[Сервер] →{ChatColors.Default} {TeamFullNotifyText}");
    }

    private static void NotifyTeamBalanceMoved(CCSPlayerController player)
    {
        try
        {
            player.PrintToCenter(TeamBalanceMovedNotifyText);
        }
        catch
        {
            // ignore
        }

        player.PrintToChat(
            $" {ChatColors.Red}[Сервер] →{ChatColors.Default} {TeamBalanceMovedNotifyText}");
    }

    private void MovePlayerToTeamForBalance(CCSPlayerController player, CsTeam toTeam)
    {
        if (toTeam != CsTeam.Terrorist && toTeam != CsTeam.CounterTerrorist)
        {
            return;
        }

        bool aliveOnSide = player.PawnIsAlive &&
                           (player.Team == CsTeam.Terrorist || player.Team == CsTeam.CounterTerrorist);
        if (aliveOnSide)
        {
            try
            {
                player.SwitchTeam(toTeam);
                return;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[AFK] SwitchTeam balance fallback");
            }
        }

        ForceNativeJoinTeam(player, toTeam);
    }

    private void ForceNativeJoinTeam(CCSPlayerController player, CsTeam team)
    {
        if (team != CsTeam.Terrorist && team != CsTeam.CounterTerrorist && team != CsTeam.Spectator)
        {
            return;
        }

        int slot = player.Slot;
        _bypassTeamBalanceJoinSlots.Add(slot);
        try
        {
            player.ExecuteClientCommandFromServer($"jointeam {(int)team}");
        }
        catch
        {
            _bypassTeamBalanceJoinSlots.Remove(slot);
            try
            {
                player.ChangeTeam(team);
            }
            catch
            {
            }
        }
    }

    private HookResult OnJoinTeamBalanceLimit(CCSPlayerController? player, CommandInfo info)
    {
        try
        {
            if (player == null || !player.IsValid || player.IsBot)
            {
                return HookResult.Continue;
            }

            if (_bypassTeamBalanceJoinSlots.Remove(player.Slot))
            {
                return HookResult.Continue;
            }

            if (!TryParseJoinTeamArg(info, out int teamArg) || teamArg is 0 or 1)
            {
                return HookResult.Continue;
            }

            CsTeam desired = teamArg switch
            {
                2 => CsTeam.Terrorist,
                3 => CsTeam.CounterTerrorist,
                _ => CsTeam.None
            };
            if (desired == CsTeam.None)
            {
                return HookResult.Continue;
            }

            if (!IsTeamJoinBlockedBySmartBalance(desired, player, out CsTeam suggested))
            {
                return HookResult.Continue;
            }

            NotifyTeamBalanceBlocked(player);
            if (suggested != desired &&
                (suggested == CsTeam.Terrorist || suggested == CsTeam.CounterTerrorist))
            {
                Server.NextFrame(() =>
                {
                    if (!player.IsValid || player.IsBot)
                    {
                        return;
                    }

                    MovePlayerToTeamForBalance(player, suggested);
                    NotifyTeamBalanceMoved(player);
                });
            }

            return HookResult.Stop;
        }
        catch (Exception ex)
        {
            Logger.LogError("[AFK] OnJoinTeamBalanceLimit error: {Message}", ex.Message);
            return HookResult.Continue;
        }
    }

    private HookResult OnPlayerTeamBalanceLimit(EventPlayerTeam @event, GameEventInfo info)
    {
        try
        {
            CCSPlayerController? player = @event.Userid;
            if (player == null || !player.IsValid || player.IsBot)
            {
                return HookResult.Continue;
            }

            CsTeam desired = @event.Team switch
            {
                (int)CsTeam.Terrorist => CsTeam.Terrorist,
                (int)CsTeam.CounterTerrorist => CsTeam.CounterTerrorist,
                _ => CsTeam.None
            };
            if (desired == CsTeam.None)
            {
                return HookResult.Continue;
            }

            CsTeam suggested;
            bool blocked;
            if (player.Team == desired)
            {
                int tEx = CountHumanPlayersOnTeamExcluding(CsTeam.Terrorist, player);
                int ctEx = CountHumanPlayersOnTeamExcluding(CsTeam.CounterTerrorist, player);
                blocked = desired == CsTeam.Terrorist ? tEx > ctEx : ctEx > tEx;
                suggested = desired == CsTeam.Terrorist ? CsTeam.CounterTerrorist : CsTeam.Terrorist;

                if (!blocked && tEx == ctEx)
                {
                    int playerLevel = _levels.GetLevel(player);
                    int sumT = SumTeamLevelsExcluding(CsTeam.Terrorist, player);
                    int sumCt = SumTeamLevelsExcluding(CsTeam.CounterTerrorist, player);
                    int skewIfStay = desired == CsTeam.Terrorist
                        ? Math.Abs((sumT + playerLevel) - sumCt)
                        : Math.Abs(sumT - (sumCt + playerLevel));
                    int skewIfOther = desired == CsTeam.Terrorist
                        ? Math.Abs(sumT - (sumCt + playerLevel))
                        : Math.Abs((sumT + playerLevel) - sumCt);
                    if (skewIfOther + 1 < skewIfStay)
                    {
                        blocked = true;
                        suggested = desired == CsTeam.Terrorist ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
                    }
                }
            }
            else if (!IsTeamJoinBlockedBySmartBalance(desired, player, out suggested))
            {
                return HookResult.Continue;
            }
            else
            {
                blocked = true;
            }

            if (!blocked)
            {
                return HookResult.Continue;
            }

            info.DontBroadcast = true;
            Server.NextFrame(() =>
            {
                if (!player.IsValid || player.IsBot || player.Team != desired)
                {
                    return;
                }

                MovePlayerToTeamForBalance(player, suggested);
                NotifyTeamBalanceMoved(player);
            });
            return HookResult.Continue;
        }
        catch (Exception ex)
        {
            Logger.LogError("[AFK] OnPlayerTeamBalanceLimit error: {Message}", ex.Message);
            return HookResult.Continue;
        }
    }
}
