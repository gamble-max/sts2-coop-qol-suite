using System;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CoopQolSuite.Core;
using CoopQolSuite.Stats;
using CoopQolSuite.Terminal;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using Sts2Mods.Shared;

namespace CoopQolSuite.QuickSl;

public static class QuickSlModule
{
    private const float HoldDurationSeconds = 0.8f;
    private const string FadeMatPath = "res://materials/transitions/fade_transition_mat.tres";
    private const string SaveSlotsKey = "CoopQolSuite_SaveSlots";

    private static readonly FieldInfo? RunSaveMgrField =
        AccessTools.Field(typeof(SaveManager), "_runSaveManager");

    public sealed class SaveSlotEntry
    {
        public string Summary { get; set; } = string.Empty;
        public int Floor { get; set; }
        public int Hp { get; set; }
        public int MaxHp { get; set; }
        public int Gold { get; set; }
        public bool IsMultiplayer { get; set; }
        public string RunJson { get; set; } = string.Empty;
    }

    public sealed class SaveSlotsPersistence
    {
        public SaveSlotEntry?[] Slots { get; set; } = new SaveSlotEntry?[3];
    }

    private static bool _initialized;
    private static bool _isReloading;
    private static RunManager? _subscribedRunMgr;
    private static SaveSlotsPersistence _slotsData = new();

    private static string _lastCapturedSeed = string.Empty;
    private static int _lastCapturedFloor = -1;
    private static int _lastCapturedHp;
    private static int _lastCapturedMaxHp;
    private static int _lastCapturedGold;
    private static string _lastCapturedSummary = string.Empty;
    private static string _lastCapturedJson = string.Empty;

    private static string _prevFloorJson = string.Empty;
    private static string _prevFloorSummary = string.Empty;
    private static int _prevFloorNum = -1;
    private static int _prevFloorHp;
    private static int _prevFloorMaxHp;
    private static int _prevFloorGold;
    private static bool _prevFloorIsMulti;

    private static Control? _overlayRoot;
    private static PanelContainer? _holdPanel;
    private static ProgressBar? _holdBar;
    private static Label? _holdLabel;
    private static PanelContainer? _toastPanel;
    private static Label? _toastLabel;
    private static float _toastTimer;
    private static bool _holdingF5;
    private static float _holdElapsed;
    private static bool _triggeredThisHold;

    public static event Action? SlotsChanged;

    public static bool HasPrevFloorRollback => !string.IsNullOrEmpty(_prevFloorJson);
    public static string PrevFloorSummary => FormatSummary(_prevFloorNum, _prevFloorHp, _prevFloorMaxHp, _prevFloorGold, _prevFloorSummary);

    public static void Initialize(Node rootContainer)
    {
        if (_initialized) return;
        _initialized = true;

        _slotsData = ModConfigBridge.LoadJsonConfig(SaveSlotsKey, new SaveSlotsPersistence());
        if (_slotsData.Slots == null || _slotsData.Slots.Length != 3)
            _slotsData.Slots = new SaveSlotEntry?[3];

        InGameChatNet.MessageReceived += OnChatMessageForSl;
        EnsureSubscribed();
        BuildOverlayUi(rootContainer);
    }

    public static void OnRunEntered()
    {
        EnsureSubscribed();
        TaskHelper.RunSafely(CaptureFloorCheckpointAsync());
    }

    private static void EnsureSubscribed()
    {
        var runMgr = RunManager.Instance;
        if (runMgr != null && !ReferenceEquals(runMgr, _subscribedRunMgr))
        {
            if (_subscribedRunMgr != null)
                _subscribedRunMgr.RoomEntered -= OnRoomEntered;

            _subscribedRunMgr = runMgr;
            runMgr.RoomEntered += OnRoomEntered;
        }
    }

    private static void OnRoomEntered()
    {
        TaskHelper.RunSafely(CaptureFloorCheckpointAsync());
    }

    private static async Task CaptureFloorCheckpointAsync()
    {
        try
        {
            var currentTask = SaveManager.Instance?.CurrentRunSaveTask;
            if (currentTask != null)
                await currentTask;

            await Task.Delay(150);

            var runMgr = RunManager.Instance;
            if (runMgr == null || !runMgr.IsInProgress) return;
            var netType = runMgr.NetService?.Type ?? NetGameType.None;
            if (netType != NetGameType.Singleplayer && netType != NetGameType.Host) return;

            if (!TryReadCurrentOfficialSave(out var save, out bool isMulti) || save == null)
                return;

            var state = PlayerNameHelper.GetRunState();
            string seed = state?.Rng?.StringSeed ?? save.SerializableRng?.ToString() ?? "?";
            int floor = state?.TotalFloor ?? save.FloorReached;
            string json = SaveManager.ToJson(save);
            ExtractPlayerSnapshot(state, out int hp, out int maxHp, out int gold);
            string summary = FormatSummary(floor, hp, maxHp, gold, string.Empty);

            if (_lastCapturedSeed != seed)
            {
                _lastCapturedSeed = seed;
                _prevFloorJson = string.Empty;
                _prevFloorSummary = string.Empty;
                _prevFloorNum = -1;
                _prevFloorHp = 0;
                _prevFloorMaxHp = 0;
                _prevFloorGold = 0;
                _lastCapturedFloor = floor;
                _lastCapturedHp = hp;
                _lastCapturedMaxHp = maxHp;
                _lastCapturedGold = gold;
                _lastCapturedSummary = summary;
                _lastCapturedJson = json;
            }
            else if (floor > _lastCapturedFloor && !string.IsNullOrEmpty(_lastCapturedJson))
            {
                _prevFloorJson = _lastCapturedJson;
                _prevFloorSummary = _lastCapturedSummary;
                _prevFloorNum = _lastCapturedFloor;
                _prevFloorHp = _lastCapturedHp;
                _prevFloorMaxHp = _lastCapturedMaxHp;
                _prevFloorGold = _lastCapturedGold;
                _prevFloorIsMulti = isMulti;

                _lastCapturedFloor = floor;
                _lastCapturedHp = hp;
                _lastCapturedMaxHp = maxHp;
                _lastCapturedGold = gold;
                _lastCapturedSummary = summary;
                _lastCapturedJson = json;
            }
            else
            {
                _lastCapturedFloor = floor;
                _lastCapturedHp = hp;
                _lastCapturedMaxHp = maxHp;
                _lastCapturedGold = gold;
                _lastCapturedSummary = summary;
                _lastCapturedJson = json;
            }

            Callable.From(() => SlotsChanged?.Invoke()).CallDeferred();
        }
        catch (Exception ex)
        {
            CoopQolModEntry.Log.Warn($"CaptureFloorCheckpointAsync failed: {ex.Message}");
        }
    }

    private static void ExtractPlayerSnapshot(RunState? state, out int hp, out int maxHp, out int gold)
    {
        hp = 0;
        maxHp = 0;
        gold = 0;
        if (state == null) return;
        var me = LocalContext.GetMe(state) ?? (state.Players.Count > 0 ? state.Players[0] : null);
        if (me == null) return;
        hp = me.Creature.CurrentHp;
        maxHp = me.Creature.MaxHp;
        gold = me.Gold;
    }

    private static string FormatSummary(int floor, int hp, int maxHp, int gold, string legacySummary)
    {
        if (maxHp > 0)
        {
            return CoopQolI18n.IsZh
                ? $"第{floor}层 · {hp}/{maxHp}血 {gold}金"
                : $"F{floor} · {hp}/{maxHp} HP {gold}g";
        }

        if (!CoopQolI18n.IsZh && !string.IsNullOrEmpty(legacySummary))
        {
            var m = Regex.Match(legacySummary, @"第(\d+)层\s*·\s*(\d+)/(\d+)血\s*(\d+)金");
            if (m.Success)
                return $"F{m.Groups[1].Value} · {m.Groups[2].Value}/{m.Groups[3].Value} HP {m.Groups[4].Value}g";
        }

        if (!string.IsNullOrEmpty(legacySummary))
            return legacySummary;

        return CoopQolI18n.IsZh ? $"第{floor}层" : $"Floor {floor}";
    }

    private static bool TryReadCurrentOfficialSave(out SerializableRun? save, out bool isMultiplayer)
    {
        save = null;
        isMultiplayer = false;

        var saveMgr = SaveManager.Instance;
        var netType = RunManager.Instance?.NetService?.Type ?? NetGameType.None;
        if (saveMgr == null) return false;

        if (netType == NetGameType.Singleplayer)
        {
            if (!saveMgr.HasRunSave) return false;
            var read = saveMgr.LoadRunSave();
            if (!read.Success || read.SaveData == null) return false;
            save = read.SaveData;
            isMultiplayer = false;
            return true;
        }

        if (netType == NetGameType.Host)
        {
            var rsm = RunSaveMgrField?.GetValue(saveMgr) as RunSaveManager;
            if (rsm == null || !rsm.HasMultiplayerRunSave) return false;
            var read = rsm.LoadMultiplayerRunSave();
            if (!read.Success || read.SaveData == null) return false;
            save = read.SaveData;
            isMultiplayer = true;
            return true;
        }

        return false;
    }

    public static string GetSlotDisplayText(int index)
    {
        if (index < 0 || index >= 3) return CoopQolI18n.T("无效槽位", "Invalid Slot");
        var entry = _slotsData.Slots[index];
        if (entry == null || string.IsNullOrEmpty(entry.RunJson))
            return CoopQolI18n.IsZh ? $"槽{index + 1}: (空)" : $"Slot {index + 1}: (Empty)";
        string modeTag = entry.IsMultiplayer
            ? CoopQolI18n.T("[联]", "[Co-op] ")
            : CoopQolI18n.T("[单]", "[Solo] ");
        string summary = FormatSummary(entry.Floor, entry.Hp, entry.MaxHp, entry.Gold, entry.Summary);
        return CoopQolI18n.IsZh
            ? $"槽{index + 1}: {modeTag}{summary}"
            : $"Slot {index + 1}: {modeTag}{summary}";
    }

    public static bool CanLoadSlot(int index)
    {
        if (index < 0 || index >= 3) return false;
        var entry = _slotsData.Slots[index];
        if (entry == null || string.IsNullOrEmpty(entry.RunJson)) return false;

        var netType = RunManager.Instance?.NetService?.Type ?? NetGameType.None;
        if (netType == NetGameType.Singleplayer) return !entry.IsMultiplayer;
        if (netType == NetGameType.Host) return entry.IsMultiplayer;
        return false;
    }

    public static void SaveToSlot(int index)
    {
        if (index < 0 || index >= 3) return;
        var netType = RunManager.Instance?.NetService?.Type ?? NetGameType.None;
        if (netType == NetGameType.Client)
        {
            ShowToast(CoopQolI18n.T(
                "联机客机由房主统一管理存档",
                "Saves are managed by the lobby Host"), new Color(1f, 0.75f, 0.4f));
            return;
        }
        TaskHelper.RunSafely(SaveToSlotAsync(index));
    }

    private static async Task SaveToSlotAsync(int index)
    {
        try
        {
            var currentTask = SaveManager.Instance?.CurrentRunSaveTask;
            if (currentTask != null)
                await currentTask;

            if (!TryReadCurrentOfficialSave(out var save, out bool isMulti) || save == null)
            {
                ShowToast(CoopQolI18n.T(
                    "当前没有可保存的对局存档",
                    "No active run save found"), new Color(1f, 0.5f, 0.5f));
                return;
            }

            var state = PlayerNameHelper.GetRunState();
            int floor = state?.TotalFloor ?? save.FloorReached;
            ExtractPlayerSnapshot(state, out int hp, out int maxHp, out int gold);
            string summary = FormatSummary(floor, hp, maxHp, gold, string.Empty);
            string json = SaveManager.ToJson(save);

            _slotsData.Slots[index] = new SaveSlotEntry
            {
                Summary = summary,
                Floor = floor,
                Hp = hp,
                MaxHp = maxHp,
                Gold = gold,
                IsMultiplayer = isMulti,
                RunJson = json,
            };

            ModConfigBridge.SaveJsonConfigDeferred(SaveSlotsKey, _slotsData);
            Callable.From(() => SlotsChanged?.Invoke()).CallDeferred();
            ShowToast(CoopQolI18n.IsZh
                ? $"已存入槽位 {index + 1}：{summary}"
                : $"Saved to Slot {index + 1}: {summary}", new Color(0.55f, 0.95f, 0.65f));
        }
        catch (Exception ex)
        {
            CoopQolModEntry.Log.Error($"SaveToSlotAsync error: {ex}");
            ShowToast(CoopQolI18n.IsZh ? $"存档失败: {ex.Message}" : $"Save failed: {ex.Message}", new Color(1f, 0.45f, 0.45f));
        }
    }

    public static void RequestLoadSlot(int index, bool skipConfirm = false)
    {
        if (_isReloading || !CanLoadSlot(index)) return;
        var entry = _slotsData.Slots[index]!;
        var read = SaveManager.FromJson<SerializableRun>(entry.RunJson);
        if (!read.Success || read.SaveData == null)
        {
            ShowToast(CoopQolI18n.IsZh
                ? $"槽位 {index + 1} 存档损坏或版本不兼容"
                : $"Slot {index + 1} save is corrupted or incompatible", new Color(1f, 0.45f, 0.45f));
            return;
        }

        string displaySummary = FormatSummary(entry.Floor, entry.Hp, entry.MaxHp, entry.Gold, entry.Summary);
        if (!skipConfirm && CoopQolConfig.Current.ConfirmBeforeQuickSl)
        {
            NativeUiHelper.ShowNativeConfirmPopup(
                CoopQolI18n.IsZh ? $"回档至槽位 {index + 1}" : $"Load Slot {index + 1}",
                CoopQolI18n.IsZh
                    ? $"确定回档至【{displaySummary}】吗？\n当前房间及后续进度将被该存档覆盖。"
                    : $"Load save [{displaySummary}]?\nCurrent room progress will be overwritten.",
                CoopQolI18n.T("确认回档", "Load Save"),
                CoopQolI18n.T("取消", "Cancel"),
                () => TaskHelper.RunSafely(ExecuteRollbackSaveAsync(read.SaveData, entry.IsMultiplayer, entry.Floor)));
        }
        else
        {
            TaskHelper.RunSafely(ExecuteRollbackSaveAsync(read.SaveData, entry.IsMultiplayer, entry.Floor));
        }
    }

    public static void RequestRollbackPrevFloor(bool skipConfirm = false)
    {
        if (_isReloading || !HasPrevFloorRollback) return;
        var netType = RunManager.Instance?.NetService?.Type ?? NetGameType.None;
        bool wantMulti = netType == NetGameType.Host;
        if (_prevFloorIsMulti != wantMulti) return;

        var read = SaveManager.FromJson<SerializableRun>(_prevFloorJson);
        if (!read.Success || read.SaveData == null)
        {
            ShowToast(CoopQolI18n.T(
                "上一层自动快照解析失败",
                "Failed to parse previous floor snapshot"), new Color(1f, 0.45f, 0.45f));
            return;
        }

        string prevDisplay = PrevFloorSummary;
        if (!skipConfirm && CoopQolConfig.Current.ConfirmBeforeQuickSl)
        {
            NativeUiHelper.ShowNativeConfirmPopup(
                CoopQolI18n.T("回退上一层", "Rollback Previous Floor"),
                CoopQolI18n.IsZh
                    ? $"确定回档至上一层自动快照【{prevDisplay}】吗？"
                    : $"Rollback to the previous floor snapshot [{prevDisplay}]?",
                CoopQolI18n.T("确认回退", "Rollback"),
                CoopQolI18n.T("取消", "Cancel"),
                () => TaskHelper.RunSafely(ExecuteRollbackSaveAsync(read.SaveData, _prevFloorIsMulti, _prevFloorNum)));
        }
        else
        {
            TaskHelper.RunSafely(ExecuteRollbackSaveAsync(read.SaveData, _prevFloorIsMulti, _prevFloorNum));
        }
    }

    private static async Task ExecuteRollbackSaveAsync(SerializableRun targetSave, bool isMultiplayer, int targetFloor)
    {
        if (_isReloading) return;
        _isReloading = true;

        try
        {
            var rsm = RunSaveMgrField?.GetValue(SaveManager.Instance) as RunSaveManager;
            if (rsm == null)
            {
                ShowToast(CoopQolI18n.T("无法访问 RunSaveManager", "RunSaveManager unavailable"), new Color(1f, 0.45f, 0.45f));
                return;
            }

            await rsm.SaveRun(targetSave, isMultiplayer);
            CombatStatsModule.RollbackFloorCommits(targetFloor);

            if (!isMultiplayer)
            {
                var runState = RunState.FromSerializable(targetSave);

                NRunMusicController.Instance?.StopMusic();
                if (NGame.Instance?.Transition != null)
                    await NGame.Instance.Transition.FadeOut(0.35f, FadeMatPath, null);

                RunManager.Instance.CleanUp();
                await RunManager.Instance.SetUpSavedSingleplayer(runState, targetSave);

                NGame.Instance?.ReactionContainer?.InitializeNetworking(new NetSingleplayerGameService());
                if (NGame.Instance != null)
                    await NGame.Instance.LoadRun(runState, targetSave.PreFinishedRoom);

                if (NGame.Instance?.Transition != null)
                    await NGame.Instance.Transition.FadeIn(0.35f, FadeMatPath, null);
            }
            else
            {
                UnifiedTerminalModule.SendChatMessage(CoopQolI18n.T(
                    "【房主已回档】已载入历史存档点，请等待房主从主菜单重新拉起房间！",
                    "[Host Rollback] Historical save loaded — please wait for Host to reopen the lobby!"), true);
                NRunMusicController.Instance?.StopMusic();
                if (NGame.Instance != null)
                    await NGame.Instance.ReturnToMainMenu();
            }
        }
        catch (Exception ex)
        {
            CoopQolModEntry.Log.Error($"ExecuteRollbackSaveAsync failed: {ex}");
            ShowToast(CoopQolI18n.IsZh ? $"回档失败: {ex.Message}" : $"Rollback failed: {ex.Message}", new Color(1f, 0.45f, 0.45f));
        }
        finally
        {
            _isReloading = false;
        }
    }

    private static void OnChatMessageForSl(string sender, string text, bool bigScreen, bool isSelf)
    {
        if (isSelf || string.IsNullOrWhiteSpace(text))
            return;
        if (!text.Contains("【请求 SL】", StringComparison.Ordinal)
            && !text.Contains("[SL Request]", StringComparison.OrdinalIgnoreCase))
            return;

        if (RunManager.Instance?.NetService?.Type != NetGameType.Host)
            return;

        Callable.From(() =>
        {
            if (_isReloading || RunManager.Instance?.IsInProgress != true)
                return;

            NativeUiHelper.ShowNativeConfirmPopup(
                CoopQolI18n.T("收到队友 SL 请求", "Teammate Requested SL"),
                CoopQolI18n.IsZh
                    ? $"{sender} 请求执行【快速 SL】重开本房。\n确认作为房主退回主菜单以重开多人存档？"
                    : $"{sender} requested a Quick SL to restart this room.\nReturn to Main Menu as Host to reload the co-op save?",
                CoopQolI18n.T("确认退回主菜单", "Return to Menu"),
                CoopQolI18n.T("暂不重开", "Not Now"),
                () => TaskHelper.RunSafely(ExecuteHostReturnToMenuAsync()));
        }).CallDeferred();
    }

    public static void TriggerQuickSl() => RequestQuickSl(skipConfirm: true);

    public static void RequestQuickSl(bool skipConfirm = false)
    {
        if (_isReloading) return;

        var runMgr = RunManager.Instance;
        if (runMgr == null || !runMgr.IsInProgress)
        {
            ShowToast(CoopQolI18n.T("当前未在对局中，无法 SL", "Not in an active run"), new Color(1f, 0.7f, 0.4f));
            return;
        }

        var netType = runMgr.NetService?.Type ?? NetGameType.None;
        switch (netType)
        {
            case NetGameType.Singleplayer:
                if (!skipConfirm && CoopQolConfig.Current.ConfirmBeforeQuickSl)
                {
                    NativeUiHelper.ShowNativeConfirmPopup(
                        CoopQolI18n.T("快速 SL（重开本房）", "Quick SL (Restart Room)"),
                        CoopQolI18n.T(
                            "确定要放弃当前房间未保存的操作，重新载入本房间起点存档吗？\n（提示：按住 Shift 点击或长按 F5 可免确认秒重开）",
                            "Discard unsaved progress in this room and reload the room start save?\n(Tip: Shift+Click or Hold F5 skips confirmation)"),
                        CoopQolI18n.T("确认重开", "Restart"),
                        CoopQolI18n.T("取消", "Cancel"),
                        () => TaskHelper.RunSafely(ExecuteSingleplayerSlAsync()));
                }
                else
                {
                    TaskHelper.RunSafely(ExecuteSingleplayerSlAsync());
                }
                break;

            case NetGameType.Host:
                NativeUiHelper.ShowNativeConfirmPopup(
                    CoopQolI18n.T("联机房主一键 SL", "Host Quick SL"),
                    CoopQolI18n.T(
                        "确认作为房主退回主菜单以重开当前多人存档？\n（退回后请在多人界面点击「继续」重新拉起房间）",
                        "Return to Main Menu as Host to restart this co-op room?\n(Click Continue in Multiplayer to reopen the lobby)"),
                    CoopQolI18n.T("确认退回主菜单", "Return to Menu"),
                    CoopQolI18n.T("取消", "Cancel"),
                    () => TaskHelper.RunSafely(ExecuteHostReturnToMenuAsync()));
                break;

            case NetGameType.Client:
                if (!skipConfirm && CoopQolConfig.Current.ConfirmBeforeQuickSl)
                {
                    NativeUiHelper.ShowNativeConfirmPopup(
                        CoopQolI18n.T("请求房主 SL", "Request Host SL"),
                        CoopQolI18n.T(
                            "联机客机无法直接本地读档。\n是否向房主和队友发送【请求 SL 重开本房】大屏提示？",
                            "Clients cannot reload saves locally.\nSend an [SL Request] banner to the Host and party?"),
                        CoopQolI18n.T("发送请求", "Send Request"),
                        CoopQolI18n.T("取消", "Cancel"),
                        SendClientSlRequest);
                }
                else
                {
                    SendClientSlRequest();
                }
                break;

            default:
                ShowToast(CoopQolI18n.T("当前模式不支持一键 SL", "Quick SL not supported in this mode"), new Color(1f, 0.7f, 0.4f));
                break;
        }
    }

    private static void SendClientSlRequest()
    {
        NCapstoneContainer.Instance?.Close();
        UnifiedTerminalModule.SendChatMessage(CoopQolI18n.T(
            "【请求 SL】求房主重开本房！",
            "[SL Request] Please restart this room, Host!"), true);
        ShowToast(CoopQolI18n.T(
            "已发送 SL 请求给房主（客机禁止本地读档）",
            "SL request sent to Host"), new Color(0.6f, 0.85f, 1f));
    }

    private static async Task ExecuteSingleplayerSlAsync()
    {
        if (_isReloading) return;
        _isReloading = true;

        try
        {
            if (!SaveManager.Instance.HasRunSave)
            {
                ShowToast(CoopQolI18n.T("未找到单机存档！", "Singleplayer save not found!"), new Color(1f, 0.45f, 0.45f));
                return;
            }

            var read = SaveManager.Instance.LoadRunSave();
            if (!read.Success || read.SaveData == null)
            {
                ShowToast(CoopQolI18n.IsZh ? $"读取存档失败: {read.Status}" : $"Failed to load save: {read.Status}", new Color(1f, 0.45f, 0.45f));
                return;
            }

            var serializableRun = read.SaveData;
            var runState = RunState.FromSerializable(serializableRun);

            NRunMusicController.Instance?.StopMusic();
            if (NGame.Instance?.Transition != null)
                await NGame.Instance.Transition.FadeOut(0.35f, FadeMatPath, null);

            RunManager.Instance.CleanUp();
            await RunManager.Instance.SetUpSavedSingleplayer(runState, serializableRun);

            NGame.Instance?.ReactionContainer?.InitializeNetworking(new NetSingleplayerGameService());
            if (NGame.Instance != null)
                await NGame.Instance.LoadRun(runState, serializableRun.PreFinishedRoom);

            if (NGame.Instance?.Transition != null)
                await NGame.Instance.Transition.FadeIn(0.35f, FadeMatPath, null);
        }
        catch (Exception ex)
        {
            CoopQolModEntry.Log.Error($"Singleplayer QuickSL failed: {ex}");
            ShowToast(CoopQolI18n.IsZh ? $"一键 SL 失败: {ex.Message}" : $"Quick SL failed: {ex.Message}", new Color(1f, 0.45f, 0.45f));
        }
        finally
        {
            _isReloading = false;
        }
    }

    private static async Task ExecuteHostReturnToMenuAsync()
    {
        if (_isReloading) return;
        _isReloading = true;

        try
        {
            NRunMusicController.Instance?.StopMusic();
            if (NGame.Instance != null)
                await NGame.Instance.ReturnToMainMenu();
        }
        catch (Exception ex)
        {
            CoopQolModEntry.Log.Error($"Host QuickSL ReturnToMainMenu failed: {ex}");
        }
        finally
        {
            _isReloading = false;
        }
    }

    [HarmonyPatch(typeof(NPauseMenu), nameof(NPauseMenu._Ready))]
    private static class PauseMenuReadyPatch
    {
        private static readonly FieldInfo? ButtonContainerField =
            AccessTools.Field(typeof(NPauseMenu), "_buttonContainer");
        private static readonly FieldInfo? SaveAndQuitButtonField =
            AccessTools.Field(typeof(NPauseMenu), "_saveAndQuitButton");

        private static void Postfix(NPauseMenu __instance)
        {
            try
            {
                if (!CoopQolConfig.Current.EnableQuickSl) return;
                if (ButtonContainerField?.GetValue(__instance) is not Control container) return;
                if (container.GetNodeOrNull("CoopQolQuickSlButton") != null) return;

                var netType = RunManager.Instance?.NetService?.Type ?? NetGameType.None;
                string labelText = netType == NetGameType.Client
                    ? CoopQolI18n.T("请求房主 SL", "Request Host SL")
                    : CoopQolI18n.T("快速 SL (重开本房)", "Quick SL (Restart)");

                if (SaveAndQuitButtonField?.GetValue(__instance) is NPauseMenuButton templateBtn)
                {
                    var clonedNode = templateBtn.Duplicate(
                        (int)(Node.DuplicateFlags.Groups | Node.DuplicateFlags.Scripts | Node.DuplicateFlags.UseInstantiation));
                    if (clonedNode is NPauseMenuButton clonedBtn)
                    {
                        clonedBtn.Name = "CoopQolQuickSlButton";
                        if (clonedBtn.GetNodeOrNull<CanvasItem>("ButtonImage") is { Material: ShaderMaterial sm } img)
                            img.Material = (Material)sm.Duplicate();

                        container.AddChild(clonedBtn);
                        container.MoveChild(clonedBtn, templateBtn.GetIndex());

                        var megaLabel = clonedBtn.GetNodeOrNull<MegaLabel>("Label");
                        megaLabel?.SetTextAutoSize(labelText);

                        clonedBtn.Connect(
                            NClickableControl.SignalName.Released,
                            Callable.From<NButton>(_ => RequestQuickSl(skipConfirm: false)));
                        return;
                    }
                }

                var fallbackBtn = new Button
                {
                    Name = "CoopQolQuickSlButton",
                    Text = labelText,
                    CustomMinimumSize = new Vector2(220, 40),
                };
                NativeUiHelper.StyleSpireButton(fallbackBtn, 18, isActive: true);
                fallbackBtn.Pressed += () => RequestQuickSl(skipConfirm: false);
                container.AddChild(fallbackBtn);
            }
            catch (Exception ex)
            {
                CoopQolModEntry.Log.Error($"NPauseMenu QuickSL inject error: {ex}");
            }
        }
    }

    private static void BuildOverlayUi(Node rootContainer)
    {
        _overlayRoot = new Control
        {
            Name = "CoopQolQuickSlOverlay",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _overlayRoot.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        _holdPanel = new PanelContainer
        {
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(330, 56),
        };
        _holdPanel.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        _holdPanel.OffsetLeft = -165f;
        _holdPanel.OffsetRight = 165f;
        _holdPanel.OffsetTop = 110f;
        _holdPanel.OffsetBottom = 166f;
        _holdPanel.AddThemeStyleboxOverride("panel", NativeUiHelper.CreateSpirePanelStyle(0.92f, NativeUiHelper.SpireGold, 4));

        var holdBox = new VBoxContainer();
        _holdLabel = new Label
        {
            Text = CoopQolI18n.T("长按 F5 一键 SL 重开本房...", "Hold F5 to Quick SL (Restart Room)..."),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        NativeUiHelper.ApplyFont(_holdLabel, 14);
        _holdLabel.AddThemeColorOverride("font_color", NativeUiHelper.SpireGold);
        _holdBar = new ProgressBar
        {
            MinValue = 0,
            MaxValue = HoldDurationSeconds,
            Value = 0,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(260, 12),
        };
        holdBox.AddChild(_holdLabel);
        holdBox.AddChild(_holdBar);
        _holdPanel.AddChild(holdBox);
        _overlayRoot.AddChild(_holdPanel);

        _toastPanel = new PanelContainer
        {
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _toastPanel.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        _toastPanel.OffsetLeft = -240f;
        _toastPanel.OffsetRight = 240f;
        _toastPanel.OffsetTop = 115f;
        _toastPanel.OffsetBottom = 155f;
        _toastPanel.AddThemeStyleboxOverride("panel", NativeUiHelper.CreateSpirePanelStyle(0.92f, NativeUiHelper.SpireBrass, 4));

        _toastLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        NativeUiHelper.ApplyFont(_toastLabel, 14);
        _toastPanel.AddChild(_toastLabel);
        _overlayRoot.AddChild(_toastPanel);

        rootContainer.AddChild(_overlayRoot);
    }

    public static void Tick(double delta)
    {
        if (!_holdingF5 && _toastTimer <= 0f) return;

        if (_toastTimer > 0f && _toastPanel != null)
        {
            _toastTimer -= (float)delta;
            if (_toastTimer <= 0f)
                _toastPanel.Visible = false;
        }

        if (_holdingF5 && !_triggeredThisHold)
        {
            if (RunManager.Instance?.IsInProgress != true)
            {
                ResetHold();
                return;
            }

            _holdElapsed += (float)delta;
            if (_holdBar != null)
                _holdBar.Value = Math.Min(_holdElapsed, HoldDurationSeconds);
            if (_holdElapsed >= HoldDurationSeconds)
            {
                _triggeredThisHold = true;
                if (_holdPanel != null)
                    _holdPanel.Visible = false;
                RequestQuickSl(skipConfirm: true);
            }
        }
    }

    public static void HandleKeyPress(InputEventKey key)
    {
        if (!_initialized || !CoopQolConfig.Current.EnableQuickSl) return;

        if (key.Keycode == Key.F5 && RunManager.Instance?.IsInProgress == true && !_isReloading)
        {
            _holdingF5 = true;
            _holdElapsed = 0f;
            _triggeredThisHold = false;
            if (_holdBar != null) _holdBar.Value = 0f;
            if (_holdPanel != null) _holdPanel.Visible = true;
            NGame.Instance?.GetViewport()?.SetInputAsHandled();
        }
    }

    public static void HandleKeyReleaseF5()
    {
        if (_holdingF5)
            ResetHold();
    }

    private static void ResetHold()
    {
        _holdingF5 = false;
        _holdElapsed = 0f;
        _triggeredThisHold = false;
        if (_holdBar != null) _holdBar.Value = 0f;
        if (_holdPanel != null) _holdPanel.Visible = false;
    }

    public static void Relocalize()
    {
        if (_holdLabel != null && GodotObject.IsInstanceValid(_holdLabel))
        {
            _holdLabel.Text = CoopQolI18n.T("长按 F5 一键 SL 重开本房...", "Hold F5 to Quick SL (Restart Room)...");
            NativeUiHelper.ApplyFont(_holdLabel, 14);
        }
        if (_toastLabel != null && GodotObject.IsInstanceValid(_toastLabel))
            NativeUiHelper.ApplyFont(_toastLabel, 14);

        SlotsChanged?.Invoke();
    }

    public static void ShowToast(string message, Color color)
    {
        if (_toastLabel == null || _toastPanel == null) return;
        _toastLabel.Text = message;
        _toastLabel.AddThemeColorOverride("font_color", color);
        _toastPanel.Visible = true;
        _toastTimer = 3.0f;
    }
}
