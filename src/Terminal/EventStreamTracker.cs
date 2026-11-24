using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoopQolSuite.Core;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CoopQolSuite.Terminal;

public enum EventCategory
{
    Major,
    Minor,
    DamageStatus,
    Negative
}

public sealed record StreamEventItem(
    long Seq,
    EventCategory Category,
    string FormattedText
);

public static class EventStreamTracker
{
    private const int MaxEntries = 150;
    private static readonly object SyncLock = new();
    private static readonly List<StreamEventItem> Items = new();

    private static long _nextSeq;
    private static RunManager? _subscribedRunMgr;
    private static CombatManager? _subscribedCombatMgr;
    private static int _lastFloor = -1;
    private static string _lastSeed = string.Empty;
    private static int _processedCombatHistoryCount;
    private static CombatHistory? _subscribedHistory;
    private static readonly Dictionary<int, long> RelicFlashCooldownTicks = new();

    public static event Action<StreamEventItem>? EventAdded;

    public static void Initialize()
    {
        EnsureSubscribed();
    }

    public static void EnsureSubscribed()
    {
        try
        {
            var runMgr = RunManager.Instance;
            if (runMgr != null && !ReferenceEquals(runMgr, _subscribedRunMgr))
            {
                if (_subscribedRunMgr != null)
                {
                    _subscribedRunMgr.RunStarted -= OnRunStarted;
                    _subscribedRunMgr.RoomEntered -= OnRoomEntered;
                }
                _subscribedRunMgr = runMgr;
                runMgr.RunStarted += OnRunStarted;
                runMgr.RoomEntered += OnRoomEntered;
            }

            var combatMgr = CombatManager.Instance;
            if (combatMgr != null && !ReferenceEquals(combatMgr, _subscribedCombatMgr))
            {
                if (_subscribedCombatMgr != null)
                {
                    _subscribedCombatMgr.CombatSetUp -= OnCombatSetUp;
                    _subscribedCombatMgr.CombatWon -= OnCombatWon;
                    _subscribedCombatMgr.PlayerEndedTurn -= OnPlayerEndedTurn;
                }
                _subscribedCombatMgr = combatMgr;
                combatMgr.CombatSetUp += OnCombatSetUp;
                combatMgr.CombatWon += OnCombatWon;
                combatMgr.PlayerEndedTurn += OnPlayerEndedTurn;
            }
        }
        catch (Exception ex)
        {
            CoopQolModEntry.Log.Warn($"EventStreamTracker hook subscription error: {ex.Message}");
        }
    }

    public static void OnRunEntered()
    {
        EnsureSubscribed();
        var state = PlayerNameHelper.GetRunState();
        if (state == null) return;

        var seed = state.Rng?.StringSeed ?? "?";
        if (_lastSeed != seed)
        {
            _lastSeed = seed;
            _lastFloor = -1;
            _processedCombatHistoryCount = 0;
            var safeSeed = PlayerNameHelper.EscapeBbCode(seed);
            AddItem(EventCategory.Major, CoopQolI18n.IsZh
                ? $"[color=#ffd166]🚩 踏入尖塔 (种子: {safeSeed}) — 事件流已就绪[/color]"
                : $"[color=#ffd166]🚩 Entered the Spire (Seed: {safeSeed}) — Event Stream ready[/color]");
        }
    }

    public static IReadOnlyList<StreamEventItem> GetSnapshot()
    {
        lock (SyncLock)
        {
            return Items.ToArray();
        }
    }

    public static void Clear()
    {
        lock (SyncLock)
        {
            Items.Clear();
        }
    }

    private static void AddItem(EventCategory category, string bbCodeText)
    {
        var seq = Interlocked.Increment(ref _nextSeq);
        var item = new StreamEventItem(seq, category, bbCodeText);
        lock (SyncLock)
        {
            Items.Add(item);
            if (Items.Count > MaxEntries)
                Items.RemoveAt(0);
        }

        EventAdded?.Invoke(item);
    }

    private static void OnRunStarted(RunState state)
    {
        _lastFloor = -1;
        _processedCombatHistoryCount = 0;
        var rawSeed = state.Rng?.StringSeed ?? "?";
        _lastSeed = rawSeed;
        var seed = PlayerNameHelper.EscapeBbCode(rawSeed);
        AddItem(EventCategory.Major, CoopQolI18n.IsZh
            ? $"[color=#ffd166]=== 新历程开始 (种子: {seed}) ===[/color]"
            : $"[color=#ffd166]=== New Run Started (Seed: {seed}) ===[/color]");
    }

    private static void OnRoomEntered()
    {
        var state = PlayerNameHelper.GetRunState();
        if (state == null) return;

        if (state.TotalFloor != _lastFloor)
        {
            _lastFloor = state.TotalFloor;
            AddItem(EventCategory.Major, CoopQolI18n.IsZh
                ? $"[color=#ffd166]=== 第 {_lastFloor} 层 ===[/color]"
                : $"[color=#ffd166]=== Floor {_lastFloor} ===[/color]");
        }

        var actorLabel = BuildPartyLabel(state);
        var room = state.CurrentRoom;
        if (room == null) return;

        if (room is EventRoom eventRoom)
        {
            var title = PlayerNameHelper.EscapeBbCode(
                eventRoom.LocalMutableEvent?.Title?.GetFormattedText()
                ?? eventRoom.CanonicalEvent?.Title?.GetFormattedText()
                ?? CoopQolI18n.T("未知事件", "Unknown Event"));
            AddItem(EventCategory.Major, CoopQolI18n.IsZh
                ? $"[color=#c77dff]{actorLabel} 在？房遭遇了【{title}】[/color]"
                : $"[color=#c77dff]{actorLabel} encountered ? Event [{title}][/color]");
        }
        else
        {
            var roomLabel = room.RoomType switch
            {
                RoomType.Monster => CoopQolI18n.T("普通战斗", "Combat"),
                RoomType.Elite => CoopQolI18n.T("精英战斗", "Elite Combat"),
                RoomType.Boss => CoopQolI18n.T("Boss 战斗", "Boss Combat"),
                RoomType.Shop => CoopQolI18n.T("商店", "Shop"),
                RoomType.Treasure => CoopQolI18n.T("宝箱房", "Treasure"),
                RoomType.RestSite => CoopQolI18n.T("篝火休息处", "Rest Site"),
                RoomType.Map => CoopQolI18n.T("地图", "Map"),
                _ => room.RoomType.ToString()
            };
            AddItem(EventCategory.Major, CoopQolI18n.IsZh
                ? $"[color=#90e0ef]{actorLabel} 进入了【{roomLabel}】[/color]"
                : $"[color=#90e0ef]{actorLabel} entered [{roomLabel}][/color]");
        }
    }

    private static void OnCombatSetUp(CombatState state)
    {
        UnsubscribeCombatHistory();
        _processedCombatHistoryCount = 0;
        var history = CombatManager.Instance?.History;
        if (history != null)
        {
            _subscribedHistory = history;
            _subscribedHistory.Changed += OnCombatHistoryChanged;
            OnCombatHistoryChanged();
        }
    }

    private static void OnCombatWon(CombatRoom room)
    {
        OnCombatHistoryChanged();
        AddItem(EventCategory.Major, CoopQolI18n.T(
            "[color=#80ed99]🏆 战斗胜利！[/color]",
            "[color=#80ed99]🏆 Victory![/color]"));
        UnsubscribeCombatHistory();
    }

    private static void OnPlayerEndedTurn(Player player, bool canBackOut)
    {
        var name = PlayerNameHelper.EscapeBbCode(PlayerNameHelper.GetPlayerName(player));
        AddItem(EventCategory.Minor, CoopQolI18n.IsZh
            ? $"[color=#ced4da]{name} 结束了回合[/color]"
            : $"[color=#ced4da]{name} ended turn[/color]");
    }

    private static void UnsubscribeCombatHistory()
    {
        if (_subscribedHistory != null)
        {
            _subscribedHistory.Changed -= OnCombatHistoryChanged;
            _subscribedHistory = null;
        }
    }

    private static void OnCombatHistoryChanged()
    {
        var history = _subscribedHistory ?? CombatManager.Instance?.History;
        if (history == null) return;

        if (history.Entries is IList<MegaCrit.Sts2.Core.Combat.History.CombatHistoryEntry> list)
        {
            for (int i = _processedCombatHistoryCount; i < list.Count; i++)
            {
                ProcessCombatHistoryEntry(list[i]);
            }
            _processedCombatHistoryCount = list.Count;
        }
        else
        {
            int index = 0;
            foreach (var entry in history.Entries)
            {
                if (index++ < _processedCombatHistoryCount)
                    continue;

                _processedCombatHistoryCount = index;
                ProcessCombatHistoryEntry(entry);
            }
        }
    }

    private static void ProcessCombatHistoryEntry(MegaCrit.Sts2.Core.Combat.History.CombatHistoryEntry entry)
    {
        bool isZh = CoopQolI18n.IsZh;
        switch (entry)
        {
            case DamageReceivedEntry dre:
            {
                var dealer = PlayerNameHelper.EscapeBbCode(
                    dre.Dealer != null ? PlayerNameHelper.GetCreatureName(dre.Dealer) : CoopQolI18n.T("环境/效果", "Effect"));
                var receiver = PlayerNameHelper.EscapeBbCode(PlayerNameHelper.GetCreatureName(dre.Receiver));
                var total = dre.Result?.TotalDamage ?? 0;
                var unblocked = dre.Result?.UnblockedDamage ?? 0;
                var extra = unblocked != total
                    ? (isZh ? $" ({unblocked}点有效)" : $" ({unblocked} eff)")
                    : string.Empty;
                AddItem(EventCategory.DamageStatus, isZh
                    ? $"[color=#ff8fa3]{dealer} 给予 {receiver} {total}点伤害{extra}[/color]"
                    : $"[color=#ff8fa3]{dealer} dealt {total} dmg{extra} to {receiver}[/color]");
                break;
            }
            case PowerReceivedEntry pre:
            {
                var owner = PlayerNameHelper.EscapeBbCode(PlayerNameHelper.GetCreatureName(pre.Power?.Owner));
                var powerName = PlayerNameHelper.EscapeBbCode(pre.Power?.Title?.GetFormattedText() ?? CoopQolI18n.T("状态", "Power"));
                var amount = (int)pre.Amount;
                if (pre.Applier != null && pre.Applier != pre.Power?.Owner)
                {
                    var applier = PlayerNameHelper.EscapeBbCode(PlayerNameHelper.GetCreatureName(pre.Applier));
                    AddItem(EventCategory.DamageStatus, isZh
                        ? $"[color=#ffccd5]{applier} 给予 {owner} {amount}层【{powerName}】[/color]"
                        : $"[color=#ffccd5]{applier} applied {amount} [{powerName}] to {owner}[/color]");
                }
                else
                {
                    AddItem(EventCategory.DamageStatus, isZh
                        ? $"[color=#bde0fe]{owner} 获得了 {amount}层【{powerName}】[/color]"
                        : $"[color=#bde0fe]{owner} gained {amount} [{powerName}][/color]");
                }
                break;
            }
            case BlockGainedEntry bge:
            {
                if (bge.Amount <= 0) break;
                var receiver = PlayerNameHelper.EscapeBbCode(PlayerNameHelper.GetCreatureName(bge.Receiver));
                AddItem(EventCategory.DamageStatus, isZh
                    ? $"[color=#8ecae6]{receiver} 获得了 {bge.Amount}点格挡[/color]"
                    : $"[color=#8ecae6]{receiver} gained {bge.Amount} Block[/color]");
                break;
            }
            case CardPlayFinishedEntry cpfe:
            {
                var player = PlayerNameHelper.EscapeBbCode(PlayerNameHelper.GetPlayerName(cpfe.CardPlay?.Player));
                var cardTitle = PlayerNameHelper.EscapeBbCode(cpfe.CardPlay?.Card?.Title ?? CoopQolI18n.T("卡牌", "Card"));
                AddItem(EventCategory.Minor, isZh
                    ? $"[color=#ced4da]{player} 打出了【{cardTitle}】[/color]"
                    : $"[color=#ced4da]{player} played [{cardTitle}][/color]");
                break;
            }
            case PotionUsedEntry pue:
            {
                var owner = PlayerNameHelper.EscapeBbCode(PlayerNameHelper.GetPlayerName(pue.Potion?.Owner));
                var potionTitle = PlayerNameHelper.EscapeBbCode(pue.Potion?.Title?.GetFormattedText() ?? CoopQolI18n.T("药水", "Potion"));
                AddItem(EventCategory.Minor, isZh
                    ? $"[color=#a3f7bf]{owner} 使用了药水【{potionTitle}】[/color]"
                    : $"[color=#a3f7bf]{owner} used Potion [{potionTitle}][/color]");
                break;
            }
            case CardDiscardedEntry cde:
            {
                var owner = PlayerNameHelper.EscapeBbCode(PlayerNameHelper.GetPlayerName(cde.Card?.Owner));
                var cardTitle = FormatCardTitle(cde.Card);
                AddItem(EventCategory.Minor, isZh
                    ? $"[color=#ced4da]{owner} 因效果弃置了【{cardTitle}】[/color]"
                    : $"[color=#ced4da]{owner} discarded [{cardTitle}] (card effect)[/color]");
                break;
            }
        }
    }

    public static string FormatCardTitle(CardModel? card)
    {
        if (card == null) return CoopQolI18n.T("卡牌", "Card");
        var title = card.Title ?? card.Id.Entry;
        if (card.IsUpgraded && !title.EndsWith("+", StringComparison.Ordinal))
            title += "+";
        return PlayerNameHelper.EscapeBbCode(title);
    }

    public static string FormatQuotedList(IReadOnlyList<string> rawItems)
    {
        if (rawItems == null || rawItems.Count == 0) return string.Empty;
        if (CoopQolI18n.IsZh)
        {
            if (rawItems.Count == 1)
                return $"“{PlayerNameHelper.EscapeBbCode(rawItems[0])}”";

            var sb = new StringBuilder();
            for (int i = 0; i < rawItems.Count; i++)
            {
                if (i > 0)
                    sb.Append(i == rawItems.Count - 1 ? "和" : "、");
                sb.Append('“').Append(PlayerNameHelper.EscapeBbCode(rawItems[i])).Append('”');
            }
            return sb.ToString();
        }
        else
        {
            if (rawItems.Count == 1)
                return $"\"{PlayerNameHelper.EscapeBbCode(rawItems[0])}\"";
            if (rawItems.Count == 2)
                return $"\"{PlayerNameHelper.EscapeBbCode(rawItems[0])}\" and \"{PlayerNameHelper.EscapeBbCode(rawItems[1])}\"";

            var sb = new StringBuilder();
            for (int i = 0; i < rawItems.Count; i++)
            {
                if (i > 0)
                    sb.Append(i == rawItems.Count - 1 ? ", and " : ", ");
                sb.Append('"').Append(PlayerNameHelper.EscapeBbCode(rawItems[i])).Append('"');
            }
            return sb.ToString();
        }
    }

    private static string GetEventOptionTitle(EventOption opt)
    {
        string? raw = null;
        try { raw = opt.Title?.GetFormattedText(); } catch { }
        if (string.IsNullOrWhiteSpace(raw))
        {
            try { raw = opt.HistoryName?.GetFormattedText(); } catch { }
        }
        if (string.IsNullOrWhiteSpace(raw))
            raw = opt.TextKey ?? CoopQolI18n.T("选项", "Option");
        var cleaned = InGameChatNet.CleanDesc(raw, 40);
        return string.IsNullOrWhiteSpace(cleaned) ? CoopQolI18n.T("选项", "Option") : cleaned;
    }

    private static string BuildPartyLabel(RunState state)
    {
        if (state.Players.Count <= 1 && state.Players.Count > 0)
            return PlayerNameHelper.EscapeBbCode(PlayerNameHelper.GetPlayerName(state.Players[0]));
        return CoopQolI18n.T("全队", "Party");
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterFlush))]
    private static class AfterFlushNegativeDiscardPatch
    {
        private static void Prefix(Player player, IReadOnlyCollection<CardModel> flushedCards)
        {
            if (flushedCards == null || flushedCards.Count == 0)
                return;

            var titles = new List<string>(flushedCards.Count);
            foreach (var c in flushedCards)
            {
                if (c == null) continue;
                var title = c.Title ?? c.Id.Entry;
                if (c.IsUpgraded && !title.EndsWith("+", StringComparison.Ordinal))
                    title += "+";
                titles.Add(title);
            }
            if (titles.Count == 0) return;

            var playerName = PlayerNameHelper.EscapeBbCode(PlayerNameHelper.GetPlayerName(player));
            var listStr = FormatQuotedList(titles);
            AddItem(EventCategory.Negative, CoopQolI18n.IsZh
                ? $"[color=#adb5bd]{playerName} 回合结束未打出而弃置了 {listStr}[/color]"
                : $"[color=#adb5bd]{playerName} discarded unplayed {listStr} at end of turn[/color]");
        }
    }

    [HarmonyPatch(typeof(EventSynchronizer), "ChooseOptionForEvent")]
    private static class EventOptionChosenPatch
    {
        private static void Prefix(EventSynchronizer __instance, Player player, int optionIndex)
        {
            try
            {
                if (__instance.IsShared)
                {
                    var runState = PlayerNameHelper.GetRunState();
                    if (runState != null && runState.Players.Count > 0 && runState.Players[0].NetId != player.NetId)
                        return;
                }

                var ev = __instance.GetEventForPlayer(player);
                var options = ev?.CurrentOptions;
                if (options == null || optionIndex < 0 || optionIndex >= options.Count)
                    return;

                var actor = __instance.IsShared
                    ? CoopQolI18n.T("全队", "Party")
                    : PlayerNameHelper.EscapeBbCode(PlayerNameHelper.GetPlayerName(player));

                var chosen = options[optionIndex];
                if (chosen != null && !chosen.IsProceed)
                {
                    var chosenTitle = PlayerNameHelper.EscapeBbCode(GetEventOptionTitle(chosen));
                    AddItem(EventCategory.Minor, CoopQolI18n.IsZh
                        ? $"[color=#e0aaff]{actor} 选择了事件选项“{chosenTitle}”[/color]"
                        : $"[color=#e0aaff]{actor} chose event option \"{chosenTitle}\"[/color]");
                }

                var unchosen = new List<string>();
                for (int i = 0; i < options.Count; i++)
                {
                    if (i == optionIndex) continue;
                    var opt = options[i];
                    if (opt == null || opt.IsLocked || opt.IsProceed) continue;
                    unchosen.Add(GetEventOptionTitle(opt));
                }

                if (unchosen.Count > 0)
                {
                    var listStr = FormatQuotedList(unchosen);
                    AddItem(EventCategory.Negative, CoopQolI18n.IsZh
                        ? $"[color=#adb5bd]{actor} 放弃了 {listStr}[/color]"
                        : $"[color=#adb5bd]{actor} passed on {listStr}[/color]");
                }
            }
            catch (Exception ex)
            {
                CoopQolModEntry.Log.Warn($"EventOptionChosenPatch error: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(RelicModel), nameof(RelicModel.Flash), new Type[0])]
    private static class RelicFlashPatch
    {
        private static void Postfix(RelicModel __instance)
        {
            int key = RuntimeHelpers.GetHashCode(__instance);
            long now = Environment.TickCount64;
            lock (RelicFlashCooldownTicks)
            {
                if (RelicFlashCooldownTicks.TryGetValue(key, out long last) && now - last < 150)
                    return;
                RelicFlashCooldownTicks[key] = now;
            }

            var owner = PlayerNameHelper.EscapeBbCode(PlayerNameHelper.GetPlayerName(__instance.Owner));
            var relicName = PlayerNameHelper.EscapeBbCode(__instance.Title?.GetFormattedText() ?? __instance.Id.Entry);
            AddItem(EventCategory.Minor, CoopQolI18n.IsZh
                ? $"[color=#ffe5b4]{owner} 触发了遗物【{relicName}】[/color]"
                : $"[color=#ffe5b4]{owner} triggered Relic [{relicName}][/color]");
        }
    }

    [HarmonyPatch(typeof(CardReward), "OnSelect")]
    private static class CardRewardSelectPatch
    {
        private static void Postfix(CardReward __instance, ref Task<bool> __result)
        {
            if (__result != null)
                __result = ObserveCardRewardSelectAsync(__instance, __result);
        }

        private static async Task<bool> ObserveCardRewardSelectAsync(CardReward reward, Task<bool> inner)
        {
            bool completed = await inner;
            try
            {
                if (completed)
                {
                    var unpicked = reward.Cards?
                        .Where(c => c != null)
                        .Select(c => c.IsUpgraded && !(c.Title ?? c.Id.Entry).EndsWith("+", StringComparison.Ordinal)
                            ? (c.Title ?? c.Id.Entry) + "+"
                            : (c.Title ?? c.Id.Entry))
                        .ToList();

                    if (unpicked != null && unpicked.Count > 0)
                    {
                        var player = PlayerNameHelper.EscapeBbCode(PlayerNameHelper.GetPlayerName(reward.Player));
                        var listStr = FormatQuotedList(unpicked);
                        AddItem(EventCategory.Negative, CoopQolI18n.IsZh
                            ? $"[color=#adb5bd]{player} 放弃了 {listStr}[/color]"
                            : $"[color=#adb5bd]{player} passed on {listStr}[/color]");
                    }
                }
            }
            catch (Exception ex)
            {
                CoopQolModEntry.Log.Warn($"ObserveCardRewardSelectAsync error: {ex.Message}");
            }
            return completed;
        }
    }

    [HarmonyPatch(typeof(CardReward), nameof(CardReward.OnSkipped))]
    private static class CardRewardSkippedPatch
    {
        private static void Postfix(CardReward __instance)
        {
            var player = PlayerNameHelper.EscapeBbCode(PlayerNameHelper.GetPlayerName(__instance.Player));
            var unpicked = __instance.Cards?
                .Where(c => c != null)
                .Select(c => c.IsUpgraded && !(c.Title ?? c.Id.Entry).EndsWith("+", StringComparison.Ordinal)
                    ? (c.Title ?? c.Id.Entry) + "+"
                    : (c.Title ?? c.Id.Entry))
                .ToList();

            if (unpicked != null && unpicked.Count > 0)
            {
                var listStr = FormatQuotedList(unpicked);
                AddItem(EventCategory.Negative, CoopQolI18n.IsZh
                    ? $"[color=#adb5bd]{player} 放弃了 {listStr}[/color]"
                    : $"[color=#adb5bd]{player} skipped {listStr}[/color]");
            }
            else
            {
                AddItem(EventCategory.Negative, CoopQolI18n.IsZh
                    ? $"[color=#adb5bd]{player} 放弃了选牌[/color]"
                    : $"[color=#adb5bd]{player} skipped card reward[/color]");
            }
        }
    }

    [HarmonyPatch(typeof(SpecialCardReward), nameof(SpecialCardReward.OnSkipped))]
    private static class SpecialCardRewardSkippedPatch
    {
        private static readonly FieldInfo? WasTakenField = AccessTools.Field(typeof(SpecialCardReward), "_wasTaken");
        private static readonly FieldInfo? CardField = AccessTools.Field(typeof(SpecialCardReward), "_card");

        private static void Postfix(SpecialCardReward __instance)
        {
            try
            {
                if (WasTakenField?.GetValue(__instance) is true)
                    return;

                if (CardField?.GetValue(__instance) is CardModel card)
                {
                    var player = PlayerNameHelper.EscapeBbCode(PlayerNameHelper.GetPlayerName(__instance.Player));
                    var title = card.Title ?? card.Id.Entry;
                    if (card.IsUpgraded && !title.EndsWith("+", StringComparison.Ordinal))
                        title += "+";
                    AddItem(EventCategory.Negative, CoopQolI18n.IsZh
                        ? $"[color=#adb5bd]{player} 放弃了 {FormatQuotedList(new[] { title })}[/color]"
                        : $"[color=#adb5bd]{player} skipped {FormatQuotedList(new[] { title })}[/color]");
                }
            }
            catch (Exception ex)
            {
                CoopQolModEntry.Log.Warn($"SpecialCardRewardSkippedPatch error: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(EventModel), "SelectCardsToAddToDeckFromGrid")]
    private static class EventSelectCardsGridPatch
    {
        private static void Postfix(EventModel __instance, List<CardCreationResult> cards, ref Task __result)
        {
            if (__result != null && cards != null)
                __result = ObserveGridSelectionAsync(__instance, cards, __result);
        }

        private static async Task ObserveGridSelectionAsync(EventModel ev, List<CardCreationResult> cards, Task inner)
        {
            await inner;
            try
            {
                var unpicked = cards
                    .Where(r => r?.Card != null && r.Card.Pile == null)
                    .Select(r =>
                    {
                        var c = r.Card;
                        var t = c.Title ?? c.Id.Entry;
                        return c.IsUpgraded && !t.EndsWith("+", StringComparison.Ordinal) ? t + "+" : t;
                    })
                    .ToList();

                if (unpicked.Count > 0)
                {
                    var owner = PlayerNameHelper.EscapeBbCode(PlayerNameHelper.GetPlayerName(ev.Owner));
                    var listStr = FormatQuotedList(unpicked);
                    AddItem(EventCategory.Negative, CoopQolI18n.IsZh
                        ? $"[color=#adb5bd]{owner} 放弃了 {listStr}[/color]"
                        : $"[color=#adb5bd]{owner} passed on {listStr}[/color]");
                }
            }
            catch (Exception ex)
            {
                CoopQolModEntry.Log.Warn($"ObserveGridSelectionAsync error: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(RelicReward), nameof(RelicReward.OnSkipped))]
    private static class RelicRewardSkippedPatch
    {
        private static void Postfix(RelicReward __instance)
        {
            var player = PlayerNameHelper.EscapeBbCode(PlayerNameHelper.GetPlayerName(__instance.Player));
            var relic = PlayerNameHelper.EscapeBbCode(__instance.Relic?.Title?.GetFormattedText() ?? CoopQolI18n.T("遗物", "Relic"));
            AddItem(EventCategory.Negative, CoopQolI18n.IsZh
                ? $"[color=#adb5bd]{player} 放弃了遗物【{relic}】[/color]"
                : $"[color=#adb5bd]{player} skipped Relic [{relic}][/color]");
        }
    }

    [HarmonyPatch(typeof(PotionReward), nameof(PotionReward.OnSkipped))]
    private static class PotionRewardSkippedPatch
    {
        private static void Postfix(PotionReward __instance)
        {
            var player = PlayerNameHelper.EscapeBbCode(PlayerNameHelper.GetPlayerName(__instance.Player));
            var potion = PlayerNameHelper.EscapeBbCode(__instance.Potion?.Title?.GetFormattedText() ?? CoopQolI18n.T("药水", "Potion"));
            AddItem(EventCategory.Negative, CoopQolI18n.IsZh
                ? $"[color=#adb5bd]{player} 放弃了药水【{potion}】[/color]"
                : $"[color=#adb5bd]{player} skipped Potion [{potion}][/color]");
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.DiscardPotionInternal))]
    private static class PlayerDiscardPotionPatch
    {
        private static void Postfix(Player __instance, PotionModel potion)
        {
            var player = PlayerNameHelper.EscapeBbCode(PlayerNameHelper.GetPlayerName(__instance));
            var potionName = PlayerNameHelper.EscapeBbCode(potion?.Title?.GetFormattedText() ?? CoopQolI18n.T("药水", "Potion"));
            AddItem(EventCategory.Negative, CoopQolI18n.IsZh
                ? $"[color=#adb5bd]{player} 丢弃了药水【{potionName}】[/color]"
                : $"[color=#adb5bd]{player} discarded Potion [{potionName}][/color]");
        }
    }
}
