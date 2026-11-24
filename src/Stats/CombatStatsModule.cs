using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CoopQolSuite.Core;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;
using Sts2Mods.Shared;

namespace CoopQolSuite.Stats;

public static class CombatStatsModule
{
    private const string StatsSaveKey = "CoopQolSuite_RunStats";

    private sealed class PlayerStatsData
    {
        public string PlayerName { get; set; } = CoopQolI18n.T("未知玩家", "Unknown Player");

        // Section 1: 总伤害（含被格挡）
        public long TotalDamageDealt { get; set; }
        public long DamageBlockedByEnemy { get; set; }
        public long DamageFromVulnerable { get; set; }

        // Section 2: 总承伤（含被格挡与缓冲/无实体/遗物减免）
        public long TotalDamageTaken { get; set; }
        public long DamageTakenMitigated { get; set; }
        public long DamageAffectedByWeak { get; set; }
        public int Deaths { get; set; }
        public long HpHealed { get; set; }

        // Section 3: 总负面（每只怪物每层单独计算）
        public int VulnApplied { get; set; }
        public int WeakShrinkApplied { get; set; }
        public int StrengthReduced { get; set; }

        // Section 4: 总团队
        public int MultiplayerCardsDrafted { get; set; }
        public int MultiplayerCardsPlayed { get; set; }
        public int PotionsToAllies { get; set; }
        public int TeammatesMended { get; set; }

        public PlayerStatsData Clone()
        {
            return new PlayerStatsData
            {
                PlayerName = PlayerName,
                TotalDamageDealt = TotalDamageDealt,
                DamageBlockedByEnemy = DamageBlockedByEnemy,
                DamageFromVulnerable = DamageFromVulnerable,
                TotalDamageTaken = TotalDamageTaken,
                DamageTakenMitigated = DamageTakenMitigated,
                DamageAffectedByWeak = DamageAffectedByWeak,
                Deaths = Deaths,
                HpHealed = HpHealed,
                VulnApplied = VulnApplied,
                WeakShrinkApplied = WeakShrinkApplied,
                StrengthReduced = StrengthReduced,
                MultiplayerCardsDrafted = MultiplayerCardsDrafted,
                MultiplayerCardsPlayed = MultiplayerCardsPlayed,
                PotionsToAllies = PotionsToAllies,
                TeammatesMended = TeammatesMended,
            };
        }

        public void MergeFrom(PlayerStatsData other)
        {
            if (!string.IsNullOrWhiteSpace(other.PlayerName))
                PlayerName = other.PlayerName;
            TotalDamageDealt += other.TotalDamageDealt;
            DamageBlockedByEnemy += other.DamageBlockedByEnemy;
            DamageFromVulnerable += other.DamageFromVulnerable;
            TotalDamageTaken += other.TotalDamageTaken;
            DamageTakenMitigated += other.DamageTakenMitigated;
            DamageAffectedByWeak += other.DamageAffectedByWeak;
            Deaths += other.Deaths;
            HpHealed += other.HpHealed;
            VulnApplied += other.VulnApplied;
            WeakShrinkApplied += other.WeakShrinkApplied;
            StrengthReduced += other.StrengthReduced;
            MultiplayerCardsDrafted += other.MultiplayerCardsDrafted;
            MultiplayerCardsPlayed += other.MultiplayerCardsPlayed;
            PotionsToAllies += other.PotionsToAllies;
            TeammatesMended += other.TeammatesMended;
        }
    }

    private sealed class FloorCommit
    {
        public int Floor { get; set; }
        public Dictionary<ulong, PlayerStatsData> Players { get; set; } = new();
    }

    private sealed class RunStatsPersistence
    {
        public string Seed { get; set; } = string.Empty;
        public List<FloorCommit> Commits { get; set; } = new();
    }

    private static readonly Dictionary<ulong, PlayerStatsData> CurrentCombatStats = new();
    private static readonly Dictionary<ulong, PlayerStatsData> CachedCommittedTotals = new();
    private static readonly Dictionary<Creature, int> LastUncappedIncoming = new();
    private static readonly Dictionary<(Creature Applier, Creature Target), int> SuppressInternalNegStr = new();
    private static readonly Dictionary<(ulong NetId, Creature Monster), int> PermStrOnMonster = new();
    private static readonly Stack<Player> ActiveCardPlayPlayers = new();
    private static readonly HashSet<CardModel> CountedDraftedCards = new(ReferenceEqualityComparer.Instance);

    [ThreadStatic]
    private static bool _inModifyDamageReentry;

    private static RunStatsPersistence _runPersistence = new();
    private static int _processedEntryCount;
    private static bool _combatCommitted;
    private static int _currentFloor = -1;
    private static string _currentSeed = string.Empty;
    private static bool _initialized;
    private static CombatManager? _subscribedCombatMgr;
    private static bool _uiDirty = true;

    public static bool HasDirtyStats => _uiDirty;

    public static bool ConsumeDirtyFlag()
    {
        if (!_uiDirty) return false;
        _uiDirty = false;
        return true;
    }

    public static void MarkDirty() => _uiDirty = true;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        _runPersistence = ModConfigBridge.LoadJsonConfig(StatsSaveKey, new RunStatsPersistence());
        EnsureSubscribed();
    }

    public static void EnsureSubscribed()
    {
        var cm = CombatManager.Instance;
        if (cm != null && !ReferenceEquals(cm, _subscribedCombatMgr))
        {
            if (_subscribedCombatMgr != null)
            {
                _subscribedCombatMgr.CombatSetUp -= OnCombatSetUp;
                _subscribedCombatMgr.CombatEnded -= OnCombatEnded;
            }
            _subscribedCombatMgr = cm;
            cm.CombatSetUp += OnCombatSetUp;
            cm.CombatEnded += OnCombatEnded;
        }
    }

    public static void OnRunEntered()
    {
        EnsureSubscribed();
        CountedDraftedCards.Clear();
        ActiveCardPlayPlayers.Clear();
        ClearCombatTransients();

        var runState = PlayerNameHelper.GetRunState();
        if (runState != null)
        {
            _currentSeed = runState.Rng?.StringSeed ?? string.Empty;
            _currentFloor = runState.TotalFloor;
            if (!string.IsNullOrEmpty(_currentSeed) && _runPersistence.Seed != _currentSeed)
            {
                _runPersistence = new RunStatsPersistence { Seed = _currentSeed };
                ModConfigBridge.SaveJsonConfigDeferred(StatsSaveKey, _runPersistence);
            }
            RebuildCommittedCache();

            if (CurrentCombatStats.Count == 0)
            {
                foreach (var p in runState.Players)
                    GetOrCreatePlayerStats(CurrentCombatStats, p);
            }
        }
        _uiDirty = true;
    }

    public static void RollbackFloorCommits(int targetFloor)
    {
        CurrentCombatStats.Clear();
        ClearCombatTransients();
        _processedEntryCount = 0;
        _combatCommitted = false;
        _runPersistence.Commits.RemoveAll(c => c.Floor >= targetFloor);
        RebuildCommittedCache();
        ModConfigBridge.SaveJsonConfigDeferred(StatsSaveKey, _runPersistence);
        _uiDirty = true;
    }

    private static void ClearCombatTransients()
    {
        LastUncappedIncoming.Clear();
        SuppressInternalNegStr.Clear();
        PermStrOnMonster.Clear();
    }

    private static bool IsInActiveCombat()
    {
        return CombatManager.Instance?.IsInProgress == true && !_combatCommitted;
    }

    private static void RecordCombatStat(Player player, Action<PlayerStatsData> update)
    {
        var stats = GetOrCreatePlayerStats(CurrentCombatStats, player);
        update(stats);
        _uiDirty = true;
    }

    private static void RecordStat(Player player, Action<PlayerStatsData> update)
    {
        if (IsInActiveCombat())
        {
            RecordCombatStat(player, update);
            return;
        }

        var runState = PlayerNameHelper.GetRunState();
        if (runState != null)
        {
            var seed = runState.Rng?.StringSeed ?? string.Empty;
            if (!string.IsNullOrEmpty(seed))
            {
                if (_runPersistence.Seed != seed)
                    _runPersistence = new RunStatsPersistence { Seed = seed };
                _currentSeed = seed;
            }
            _currentFloor = runState.TotalFloor;
        }

        if (string.IsNullOrEmpty(_currentSeed) || _currentFloor < 0)
        {
            RecordCombatStat(player, update);
            return;
        }

        var commit = _runPersistence.Commits.FirstOrDefault(c => c.Floor == _currentFloor);
        if (commit == null)
        {
            commit = new FloorCommit { Floor = _currentFloor };
            _runPersistence.Commits.Add(commit);
        }

        var stats = GetOrCreatePlayerStats(commit.Players, player);
        update(stats);
        RebuildCommittedCache();
        ModConfigBridge.SaveJsonConfigDeferred(StatsSaveKey, _runPersistence);
        _uiDirty = true;
    }

    private static void OnCombatSetUp(CombatState combatState)
    {
        try
        {
            CurrentCombatStats.Clear();
            ClearCombatTransients();
            _processedEntryCount = 0;
            _combatCommitted = false;

            var runState = combatState.RunState;
            _currentSeed = runState?.Rng?.StringSeed ?? string.Empty;
            _currentFloor = runState?.TotalFloor ?? 0;

            if (!string.IsNullOrEmpty(_currentSeed))
            {
                if (_runPersistence.Seed != _currentSeed)
                    _runPersistence = new RunStatsPersistence { Seed = _currentSeed };
                else
                    _runPersistence.Commits.RemoveAll(c => c.Floor >= _currentFloor);

                RebuildCommittedCache();
                ModConfigBridge.SaveJsonConfigDeferred(StatsSaveKey, _runPersistence);
            }

            if (runState != null)
            {
                foreach (var p in runState.Players)
                    GetOrCreatePlayerStats(CurrentCombatStats, p);
            }

            var history = CombatManager.Instance?.History;
            if (history != null)
            {
                history.Changed -= OnHistoryChanged;
                history.Changed += OnHistoryChanged;
                ProcessNewEntries(history);
            }

            _uiDirty = true;
        }
        catch (Exception ex)
        {
            CoopQolModEntry.Log.Error($"Stats OnCombatSetUp error: {ex}");
        }
    }

    private static void OnHistoryChanged()
    {
        try
        {
            var history = CombatManager.Instance?.History;
            if (history == null) return;
            if (ProcessNewEntries(history))
                _uiDirty = true;
        }
        catch (Exception ex)
        {
            CoopQolModEntry.Log.Error($"Stats OnHistoryChanged error: {ex}");
        }
    }

    private static void OnCombatEnded(CombatRoom _)
    {
        try
        {
            var history = CombatManager.Instance?.History;
            if (history != null)
            {
                history.Changed -= OnHistoryChanged;
                ProcessNewEntries(history);
            }

            if (!_combatCommitted && !string.IsNullOrEmpty(_currentSeed))
            {
                _combatCommitted = true;
                var commit = new FloorCommit { Floor = _currentFloor };
                foreach (var kv in CurrentCombatStats)
                    commit.Players[kv.Key] = kv.Value.Clone();

                _runPersistence.Commits.RemoveAll(c => c.Floor == _currentFloor);
                _runPersistence.Commits.Add(commit);
                RebuildCommittedCache();
                ModConfigBridge.SaveJsonConfigDeferred(StatsSaveKey, _runPersistence);
            }

            ClearCombatTransients();
            _uiDirty = true;
        }
        catch (Exception ex)
        {
            CoopQolModEntry.Log.Error($"Stats OnCombatEnded error: {ex}");
        }
    }

    private static bool ProcessNewEntries(CombatHistory history)
    {
        bool anyChanged = false;
        if (history.Entries is IList<CombatHistoryEntry> list)
        {
            for (int i = _processedEntryCount; i < list.Count; i++)
            {
                if (ProcessEntry(list[i]))
                    anyChanged = true;
            }
            _processedEntryCount = list.Count;
        }
        else
        {
            int idx = 0;
            foreach (var entry in history.Entries)
            {
                if (idx++ < _processedEntryCount) continue;
                _processedEntryCount = idx;
                if (ProcessEntry(entry))
                    anyChanged = true;
            }
        }
        return anyChanged;
    }

    private static bool IsMultiplayerCard(CardModel? c)
    {
        return c != null && (c.MultiplayerConstraint == CardMultiplayerConstraint.MultiplayerOnly
                             || c.TargetType == TargetType.AnyAlly
                             || c.TargetType == TargetType.AllAllies);
    }

    private static bool ProcessEntry(CombatHistoryEntry entry)
    {
        switch (entry)
        {
            case PowerReceivedEntry pwr:
            {
                var applierPlayer = PlayerNameHelper.ResolveOwnerPlayer(pwr.Applier);
                if (applierPlayer != null && pwr.Power?.Owner != null && pwr.Power.Owner.Side != applierPlayer.Creature.Side)
                {
                    int amount = (int)Math.Round(pwr.Amount);
                    if (amount == 0) return false;

                    var stats = GetOrCreatePlayerStats(CurrentCombatStats, applierPlayer);
                    if (pwr.Power is VulnerablePower && amount > 0)
                    {
                        stats.VulnApplied += amount;
                        return true;
                    }
                    if ((pwr.Power is WeakPower or ShrinkPower) && amount > 0)
                    {
                        stats.WeakShrinkApplied += amount;
                        return true;
                    }
                    if (pwr.Power is TemporaryStrengthPower && pwr.Power.Type == PowerType.Debuff && amount > 0)
                    {
                        stats.StrengthReduced += amount;
                        return true;
                    }
                    if (pwr.Power is StrengthPower && amount < 0 && pwr.Applier != null)
                    {
                        int negAmt = -amount;
                        var key = (pwr.Applier, pwr.Power.Owner);
                        if (SuppressInternalNegStr.TryGetValue(key, out int suppressed) && suppressed > 0)
                        {
                            int used = Math.Min(suppressed, negAmt);
                            negAmt -= used;
                            int rem = suppressed - used;
                            if (rem > 0) SuppressInternalNegStr[key] = rem;
                            else SuppressInternalNegStr.Remove(key);
                        }
                        if (negAmt > 0)
                        {
                            stats.StrengthReduced += negAmt;
                            var permKey = (applierPlayer.NetId, pwr.Power.Owner);
                            PermStrOnMonster[permKey] = PermStrOnMonster.GetValueOrDefault(permKey) + negAmt;
                            return true;
                        }
                    }
                }
                return false;
            }
            case CardPlayFinishedEntry card:
            {
                var actor = PlayerNameHelper.ResolveOwnerPlayer(card.Actor);
                if (actor != null && IsMultiplayerCard(card.CardPlay?.Card))
                {
                    GetOrCreatePlayerStats(CurrentCombatStats, actor).MultiplayerCardsPlayed++;
                    return true;
                }
                return false;
            }
            default:
                return false;
        }
    }

    private static int ConsumeUncapped(Creature target, int fallback)
    {
        if (LastUncappedIncoming.TryGetValue(target, out int val))
        {
            LastUncappedIncoming.Remove(target);
            return val;
        }
        return fallback;
    }

    private static void RebuildCommittedCache()
    {
        CachedCommittedTotals.Clear();
        var runState = PlayerNameHelper.GetRunState();
        var seed = runState?.Rng?.StringSeed ?? _currentSeed;

        if (!string.IsNullOrEmpty(seed) && _runPersistence.Seed == seed)
        {
            foreach (var commit in _runPersistence.Commits)
            {
                foreach (var kv in commit.Players)
                {
                    if (!CachedCommittedTotals.TryGetValue(kv.Key, out var agg))
                    {
                        agg = kv.Value.Clone();
                        CachedCommittedTotals[kv.Key] = agg;
                    }
                    else
                    {
                        agg.MergeFrom(kv.Value);
                    }
                }
            }
        }
    }

    private static PlayerStatsData GetOrCreatePlayerStats(Dictionary<ulong, PlayerStatsData> map, Player player)
    {
        if (!map.TryGetValue(player.NetId, out var data))
        {
            data = new PlayerStatsData
            {
                PlayerName = PlayerNameHelper.GetPlayerName(player),
            };
            map[player.NetId] = data;
        }
        return data;
    }

    private static Dictionary<ulong, PlayerStatsData> BuildRunStatsSnapshot()
    {
        if (_combatCommitted)
            return CachedCommittedTotals;

        var result = new Dictionary<ulong, PlayerStatsData>(CachedCommittedTotals.Count + CurrentCombatStats.Count);
        foreach (var kv in CachedCommittedTotals)
            result[kv.Key] = kv.Value.Clone();

        foreach (var kv in CurrentCombatStats)
        {
            if (!result.TryGetValue(kv.Key, out var agg))
            {
                agg = kv.Value.Clone();
                result[kv.Key] = agg;
            }
            else
            {
                agg.MergeFrom(kv.Value);
            }
        }

        return result;
    }

    public static string BuildStatsBbCode(int viewMode, int displayState)
    {
        var data = viewMode == 0 ? CurrentCombatStats : BuildRunStatsSnapshot();
        if (data.Count == 0)
            return CoopQolI18n.T("[color=#9aa0a6]暂无战斗数据[/color]", "[color=#9aa0a6]No combat data yet[/color]");

        bool isZh = CoopQolI18n.IsZh;
        var sb = new StringBuilder();
        var sorted = data.Values
            .OrderByDescending(x => x.TotalDamageDealt)
            .ThenByDescending(x => x.TotalDamageTaken);

        foreach (var p in sorted)
        {
            string safeName = PlayerNameHelper.EscapeBbCode(p.PlayerName);
            if (displayState == 1)
            {
                int debuffSum = p.VulnApplied + p.WeakShrinkApplied + p.StrengthReduced;
                int teamSum = p.MultiplayerCardsDrafted + p.MultiplayerCardsPlayed + p.PotionsToAllies + p.TeammatesMended;
                sb.AppendLine(isZh
                    ? $"[b]{safeName}[/b] | 伤:[color=#ff8a65]{p.TotalDamageDealt}[/color] | 承:[color=#ef9a9a]{p.TotalDamageTaken}[/color] | 负:{debuffSum} | 团:{teamSum}"
                    : $"[b]{safeName}[/b] | Dmg:[color=#ff8a65]{p.TotalDamageDealt}[/color] | Tkn:[color=#ef9a9a]{p.TotalDamageTaken}[/color] | Deb:{debuffSum} | Team:{teamSum}");
            }
            else
            {
                sb.AppendLine($"[b][color=#ffd54f]{safeName}[/color][/b]");
                sb.AppendLine(isZh
                    ? $"  总伤害 [color=#ff8a65]{p.TotalDamageDealt}[/color]：易伤提供 {p.DamageFromVulnerable} · 被格挡 {p.DamageBlockedByEnemy}"
                    : $"  Total Dmg [color=#ff8a65]{p.TotalDamageDealt}[/color]: Vuln +{p.DamageFromVulnerable} · Blocked {p.DamageBlockedByEnemy}");
                sb.AppendLine(isZh
                    ? $"  总承伤 [color=#ef9a9a]{p.TotalDamageTaken}[/color]：受虚弱影响 {p.DamageAffectedByWeak} · 被格挡 {p.DamageTakenMitigated} · 死亡 {p.Deaths}次 · 回复 [color=#a5d6a7]{p.HpHealed}[/color]"
                    : $"  Dmg Taken [color=#ef9a9a]{p.TotalDamageTaken}[/color]: Weak -{p.DamageAffectedByWeak} · Blocked {p.DamageTakenMitigated} · Deaths {p.Deaths} · Healed [color=#a5d6a7]{p.HpHealed}[/color]");
                sb.AppendLine(isZh
                    ? $"  总负面：[color=#ce93d8]易伤 {p.VulnApplied}次 · 虚弱/缩小 {p.WeakShrinkApplied}次 · 减力量 {p.StrengthReduced}点[/color]"
                    : $"  Debuffs: [color=#ce93d8]Vuln {p.VulnApplied} · Weak/Shrink {p.WeakShrinkApplied} · Str- {p.StrengthReduced}[/color]");
                sb.AppendLine(isZh
                    ? $"  总团队：[color=#64b5f6]抓多人牌 {p.MultiplayerCardsDrafted}张 · 使用多人牌 {p.MultiplayerCardsPlayed}张 · 供药水 {p.PotionsToAllies}瓶 · 帮队友愈合 {p.TeammatesMended}次[/color]"
                    : $"  Teamwork: [color=#64b5f6]Drafted {p.MultiplayerCardsDrafted} · Played {p.MultiplayerCardsPlayed} · Potions {p.PotionsToAllies} · Mended {p.TeammatesMended}[/color]");
            }
        }
        return sb.ToString().TrimEnd();
    }

    // --- Harmony Patches ---

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyDamage))]
    private static class ModifyDamagePatch
    {
        private static void Postfix(
            IRunState runState,
            CombatState? combatState,
            Creature? target,
            Creature? dealer,
            decimal damage,
            ValueProp props,
            CardModel? cardSource,
            CardPlay? cardPlay,
            ModifyDamageHookType modifyDamageHookType,
            CardPreviewMode previewMode)
        {
            if (_inModifyDamageReentry) return;
            if (previewMode != CardPreviewMode.None) return;
            if (target?.Player == null) return;
            if ((modifyDamageHookType & ModifyDamageHookType.Cap) == 0) return;

            try
            {
                _inModifyDamageReentry = true;
                decimal uncapped = Hook.ModifyDamage(
                    runState,
                    combatState,
                    target,
                    dealer,
                    damage,
                    props,
                    cardSource,
                    cardPlay,
                    modifyDamageHookType & ~ModifyDamageHookType.Cap,
                    CardPreviewMode.None,
                    out _);
                LastUncappedIncoming[target] = (int)Math.Floor(Math.Max(0m, uncapped));
            }
            catch
            {
                // Ignore edge cases during preview/re-entrancy
            }
            finally
            {
                _inModifyDamageReentry = false;
            }
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterDamageReceived))]
    private static class AfterDamageReceivedPatch
    {
        private static void Postfix(
            Creature? dealer,
            Creature? target,
            DamageResult result,
            ValueProp props,
            CardModel? cardSource)
        {
            var dealerPlayer = PlayerNameHelper.ResolveOwnerPlayer(dealer);
            if (dealerPlayer != null && target != null && target.Side != dealerPlayer.Creature.Side)
            {
                int hitTotal = Math.Max(result.TotalDamage, result.BlockedDamage + result.IntendedHpLoss);
                int blocked = Math.Max(0, hitTotal - result.UnblockedDamage);
                long vulnBonus = 0;

                if (props.IsPoweredAttack() && target.HasPower<VulnerablePower>())
                {
                    var vulnPower = target.GetPower<VulnerablePower>();
                    if (vulnPower != null)
                    {
                        decimal mult = vulnPower.ModifyDamageMultiplicative(target, 1m, props, dealer, cardSource, null);
                        if (mult > 1m)
                            vulnBonus = (long)Math.Round(hitTotal * (1m - 1m / mult), MidpointRounding.AwayFromZero);
                    }
                }

                RecordCombatStat(dealerPlayer, stats =>
                {
                    stats.TotalDamageDealt += hitTotal;
                    stats.DamageBlockedByEnemy += blocked;
                    stats.DamageFromVulnerable += vulnBonus;
                });
            }

            if (target?.Player != null)
            {
                int actualHpLost = Math.Max(0, result.UnblockedDamage);
                int postCap = Math.Max(result.TotalDamage, result.BlockedDamage + result.IntendedHpLoss);
                int rawIncoming = Math.Max(postCap, ConsumeUncapped(target, postCap));
                int mitigated = Math.Max(0, rawIncoming - actualHpLost);
                long weakSaved = 0;

                if (dealer != null && props.IsPoweredAttack() && dealer.HasPower<WeakPower>())
                {
                    var weakPower = dealer.GetPower<WeakPower>();
                    if (weakPower != null)
                    {
                        decimal wMult = weakPower.ModifyDamageMultiplicative(target, 1m, props, dealer, cardSource, null);
                        if (wMult > 0m && wMult < 1m)
                            weakSaved = (long)Math.Round(rawIncoming * (1m / wMult - 1m), MidpointRounding.AwayFromZero);
                    }
                }

                RecordCombatStat(target.Player, stats =>
                {
                    stats.TotalDamageTaken += rawIncoming;
                    stats.DamageTakenMitigated += mitigated;
                    stats.DamageAffectedByWeak += weakSaved;
                });
            }
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterDeath))]
    private static class AfterDeathPatch
    {
        private static void Postfix(Creature? creature)
        {
            if (creature?.Player == null) return;
            RecordStat(creature.Player, stats => stats.Deaths++);
        }
    }

    [HarmonyPatch(typeof(Creature), "HealInternal")]
    private static class HealInternalPatch
    {
        private static void Prefix(Creature __instance, out int __state)
        {
            __state = __instance?.CurrentHp ?? 0;
        }

        private static void Postfix(Creature __instance, int __state)
        {
            if (__instance?.Player == null) return;
            int actualHealed = Math.Max(0, __instance.CurrentHp - __state);
            if (actualHealed <= 0) return;
            RecordStat(__instance.Player, stats => stats.HpHealed += actualHealed);
        }
    }

    [HarmonyPatch(typeof(TemporaryStrengthPower), nameof(TemporaryStrengthPower.BeforeApplied))]
    private static class TempStrBeforeAppliedPatch
    {
        private static void Prefix(TemporaryStrengthPower __instance, Creature? applier, Creature? target, decimal amount)
        {
            if (applier != null && target != null && __instance.Type == PowerType.Debuff && amount > 0m)
            {
                int rounded = (int)Math.Round(amount);
                if (rounded > 0)
                {
                    var key = (applier, target);
                    SuppressInternalNegStr[key] = SuppressInternalNegStr.GetValueOrDefault(key) + rounded;
                }
            }
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterSideTurnStart))]
    private static class AfterSideTurnStartPatch
    {
        private static void Postfix(CombatSide side, CombatState combatState)
        {
            if (side != CombatSide.Player || combatState == null || combatState.RoundNumber < 2)
                return;
            if (PermStrOnMonster.Count == 0)
                return;

            bool changed = false;
            foreach (var kv in PermStrOnMonster)
            {
                var (netId, monster) = kv.Key;
                int permPts = kv.Value;
                if (monster != null && monster.IsAlive && permPts > 0)
                {
                    if (CurrentCombatStats.TryGetValue(netId, out var stats))
                    {
                        stats.StrengthReduced += permPts;
                        changed = true;
                    }
                }
            }
            if (changed)
                _uiDirty = true;
        }
    }

    [HarmonyPatch(typeof(CardPile), "AddInternal")]
    private static class CardPileAddInternalPatch
    {
        private static void Postfix(CardPile __instance, CardModel card, bool silent)
        {
            if (__instance?.Type != PileType.Deck) return;
            if (card?.Owner == null || silent) return;
            if ((RunManager.Instance?.DebugOnlyGetState()?.TotalFloor ?? 0) < 1) return;
            if (!IsMultiplayerCard(card)) return;
            if (!CountedDraftedCards.Add(card)) return;

            RecordStat(card.Owner, stats => stats.MultiplayerCardsDrafted++);
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterPotionUsed))]
    private static class AfterPotionUsedPatch
    {
        private static void Postfix(PotionModel? potion, Creature? target)
        {
            if (potion?.Owner == null || target == null) return;
            var tp = PlayerNameHelper.ResolveOwnerPlayer(target);
            if (tp != null && tp.NetId != potion.Owner.NetId)
                RecordStat(potion.Owner, stats => stats.PotionsToAllies++);
        }
    }

    [HarmonyPatch(typeof(CardModel), "OnPlayWrapper")]
    private static class CardOnPlayWrapperPatch
    {
        private static void Prefix(CardModel __instance, out bool __state)
        {
            if (__instance?.Owner != null)
            {
                ActiveCardPlayPlayers.Push(__instance.Owner);
                __state = true;
            }
            else
            {
                __state = false;
            }
        }

        private static void Postfix(bool __state)
        {
            if (__state && ActiveCardPlayPlayers.Count > 0)
                ActiveCardPlayPlayers.Pop();
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterPotionProcured))]
    private static class AfterPotionProcuredPatch
    {
        private static void Postfix(PotionModel? potion)
        {
            if (potion?.Owner == null || ActiveCardPlayPlayers.Count == 0) return;
            var activeCardPlayer = ActiveCardPlayPlayers.Peek();
            if (activeCardPlayer != null && potion.Owner.NetId != activeCardPlayer.NetId)
                RecordStat(activeCardPlayer, stats => stats.PotionsToAllies++);
        }
    }

    [HarmonyPatch(typeof(MendRestSiteOption), nameof(MendRestSiteOption.OnSelect))]
    private static class MendRestSiteOptionPatch
    {
        private static readonly System.Reflection.PropertyInfo? OwnerProp =
            AccessTools.Property(typeof(RestSiteOption), "Owner");

        private static void Postfix(MendRestSiteOption __instance, ref Task<bool> __result)
        {
            if (__instance == null || __result == null) return;
            var owner = OwnerProp?.GetValue(__instance) as Player;
            if (owner == null) return;
            __result = TrackMendAsync(__result, owner);
        }

        private static async Task<bool> TrackMendAsync(Task<bool> inner, Player owner)
        {
            bool ok = await inner;
            if (ok)
                RecordStat(owner, stats => stats.TeammatesMended++);
            return ok;
        }
    }
}
