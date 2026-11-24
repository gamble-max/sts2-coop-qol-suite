using System;
using System.Collections.Generic;
using System.Text;
using CoopQolSuite.Actions;
using CoopQolSuite.GodMode;
using CoopQolSuite.QuickSl;
using CoopQolSuite.Spectate;
using CoopQolSuite.Stats;
using CoopQolSuite.Terminal;
using Godot;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Runs;

namespace CoopQolSuite.Core;

public static class TopBarQuickBar
{
    private static Control? _root;
    private static PanelContainer? _panel;
    private static Button _minimizedBtn = null!;

    private static Button _tabQuickBtn = null!;
    private static Button _tabChatBtn = null!;
    private static Button _tabEventsBtn = null!;
    private static Button _tabStatsBtn = null!;
    private static Button _collapseBtn = null!;

    private static VBoxContainer _drawer = null!;
    private static VBoxContainer _quickPage = null!;
    private static VBoxContainer _chatPage = null!;
    private static VBoxContainer _eventsPage = null!;
    private static VBoxContainer _statsPage = null!;

    // Quick Commands page controls
    private static Button _godModeLaunchBtn = null!;
    private static Button _restartRoomBtn = null!;
    private static Button _prevFloorBtn = null!;
    private static VBoxContainer _slotsBox = null!;
    private static Label _clientNoticeLabel = null!;
    private static readonly Label[] SlotLabels = new Label[3];
    private static readonly Button[] SlotLoadBtns = new Button[3];
    private static VBoxContainer _coopActionBox = null!;
    private static Button _spectateBtn = null!;

    // Reserved Character Action Pack container (100% hidden when Actions.Count == 0)
    private static HBoxContainer _actionPackBox = null!;
    private static HBoxContainer _chatActionPackBox = null!;
    private static readonly List<Control> ActionPackControls = new();

    // Danmaku page controls
    private static RichTextLabel _chatLog = null!;
    private static Button _danmakuToggleBtn = null!;
    private static LineEdit _inputEdit = null!;
    private static bool _isBlockingHotkeys;

    // Events page controls
    private static Button _filterMajorBtn = null!;
    private static Button _filterMinorBtn = null!;
    private static Button _filterDmgBtn = null!;
    private static Button _filterNegBtn = null!;
    private static RichTextLabel _eventsLog = null!;
    private static bool _eventsDirty = true;
    private static long _lastRenderedEventSeq;
    private static int _renderedEventLines;
    private static double _eventFlushAccum;

    // Stats page controls
    private static Button _statsCombatBtn = null!;
    private static Button _statsRunBtn = null!;
    private static Button _statsModeBtn = null!;
    private static RichTextLabel _statsLabel = null!;
    private static string _lastStatsText = string.Empty;
    private static double _statsFlushAccum;

    private static int _activeTab = -1;
    private static bool _minimized;
    private static int _unreadChatCount;
    private static string _lastChatBtnText = string.Empty;
    private static string _lastMinBtnText = string.Empty;
    private static bool _dragging;
    private static Vector2 _dragOffset;
    private static bool _minDragging;
    private static bool _minDraggedMoved;
    private static Vector2 _minDragStartMouse;
    private static Vector2 _minDragOffset;
    private static bool _lastRootVisible;

    public static int UnreadCount => _unreadChatCount;
    public static bool IsPanelVisible => !_minimized && _activeTab >= 0;
    public static bool IsEditingInput => _inputEdit != null && GodotObject.IsInstanceValid(_inputEdit) && (_inputEdit.HasFocus() || _inputEdit.IsEditing());

    public static void Initialize(Node rootContainer)
    {
        if (_root != null && GodotObject.IsInstanceValid(_root))
            return;

        _activeTab = CoopQolConfig.Current.ActiveHudTab;
        _minimized = CoopQolConfig.Current.HudMinimized;

        _root = new Control
        {
            Name = "CoopQolUnifiedBar",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        // Single compact minimized badge button
        _minimizedBtn = new Button
        {
            Name = "CoopQolMinimizedBtn",
            Text = CoopQolI18n.T("工具", "QoL"),
            CustomMinimumSize = new Vector2(96, 42),
            Size = new Vector2(96, 42),
            Position = new Vector2(16f, 112f),
            TooltipText = CoopQolI18n.T(
                "左键点击展开工具栏，按住左键拖拽位置",
                "Left-click to expand toolbar; hold & drag to move"),
            Visible = _minimized,
        };
        NativeUiHelper.InitSpireButton(_minimizedBtn, 15, tone: SpireButtonTone.GoldAccent);
        _minimizedBtn.GuiInput += OnMinimizedGuiInput;
        _root.AddChild(_minimizedBtn);

        _panel = new PanelContainer
        {
            Name = "UnifiedBarPanel",
            MouseFilter = Control.MouseFilterEnum.Stop,
            Position = new Vector2(16f, 112f),
            Visible = !_minimized,
        };
        _panel.AddThemeStyleboxOverride("panel", NativeUiHelper.CreateSpirePanelStyle());
        _root.AddChild(_panel);

        var mainVBox = new VBoxContainer();
        mainVBox.AddThemeConstantOverride("separation", 7);
        _panel.AddChild(mainVBox);

        // Row 1: 4 centered Spire tabs + single-button minimize
        var tabRow = new HBoxContainer();
        tabRow.AddThemeConstantOverride("separation", 5);
        mainVBox.AddChild(tabRow);

        var dragHandle = NativeUiHelper.CreateMegaLabel("::", 14, new Color(0.65f, 0.78f, 0.84f, 0.90f), HorizontalAlignment.Center, outlineSize: 3);
        dragHandle.TooltipText = CoopQolI18n.T("按住左键拖拽集成栏位置", "Hold left mouse button to drag bar");
        dragHandle.MouseFilter = Control.MouseFilterEnum.Stop;
        dragHandle.CustomMinimumSize = new Vector2(18, 36);
        dragHandle.GuiInput += OnDragGuiInput;
        tabRow.AddChild(dragHandle);

        _tabQuickBtn = MakeTabBtn(
            CoopQolI18n.T("快捷", "Quick"),
            CoopQolI18n.T("快捷命令：上帝模式 / 重打本房 / 3槽存档回档 / 回退上层", "Quick Commands: God Mode / Restart Room / 3 Save Slots / Prev Floor"),
            () => ToggleTab(0));
        _tabChatBtn = MakeTabBtn(
            CoopQolI18n.T("弹幕", "Chat"),
            CoopQolI18n.T("发送大屏飞行弹幕与快捷信号 (快捷键 Enter)", "Send flying screen messages & quick signals (Enter)"),
            () => ToggleTab(1));
        _tabEventsBtn = MakeTabBtn(
            CoopQolI18n.T("事件", "Events"),
            CoopQolI18n.T("四分类时序事件流 (快捷键 F7)", "4-category chronological event log (F7)"),
            () => ToggleTab(2));
        _tabStatsBtn = MakeTabBtn(
            CoopQolI18n.T("统计", "Stats"),
            CoopQolI18n.T("本场与历程伤害/效果统计 (快捷键 F6)", "Combat & Full-Run damage/effect stats (F6)"),
            () => ToggleTab(3));

        _collapseBtn = new Button
        {
            Text = CoopQolI18n.T("收起", "Hide"),
            CustomMinimumSize = new Vector2(58, 36),
            TooltipText = CoopQolI18n.T("收起为单个悬浮按钮", "Collapse into a single floating button"),
        };
        NativeUiHelper.InitSpireButton(_collapseBtn, 13);
        _collapseBtn.Pressed += () => SetMinimized(true);

        tabRow.AddChild(_tabQuickBtn);
        tabRow.AddChild(_tabChatBtn);
        tabRow.AddChild(_tabEventsBtn);
        tabRow.AddChild(_tabStatsBtn);
        tabRow.AddChild(_collapseBtn);

        // Row 2: Expandable Drawer (hidden when _activeTab == -1)
        _drawer = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(420, 0),
            Visible = false,
        };
        _drawer.AddThemeConstantOverride("separation", 6);
        mainVBox.AddChild(_drawer);

        BuildQuickPage();
        BuildDanmakuPage();
        BuildEventsPage();
        BuildStatsPage();

        EventStreamTracker.EventAdded += OnStreamEventAdded;
        QuickSlModule.SlotsChanged += OnSlotsChanged;
        CharacterActionRegistry.ActionsChanged += RebuildActionPackButtons;
        _root.TreeExiting += () =>
        {
            EventStreamTracker.EventAdded -= OnStreamEventAdded;
            QuickSlModule.SlotsChanged -= OnSlotsChanged;
            CharacterActionRegistry.ActionsChanged -= RebuildActionPackButtons;
            SetHotkeyBlocking(false);
        };

        rootContainer.AddChild(_root);
        RebuildActionPackButtons();
        ApplyTabVisibility();
        UpdateChatTabText();
        RefreshState();
    }

    public static void Relocalize(Node rootContainer)
    {
        if (_root == null || !GodotObject.IsInstanceValid(_root))
            return;

        string savedChatBbCode = _chatLog?.Text ?? string.Empty;

        EventStreamTracker.EventAdded -= OnStreamEventAdded;
        QuickSlModule.SlotsChanged -= OnSlotsChanged;
        CharacterActionRegistry.ActionsChanged -= RebuildActionPackButtons;
        SetHotkeyBlocking(false);

        _root.QueueFree();
        _root = null;
        _panel = null;
        _lastChatBtnText = string.Empty;
        _lastMinBtnText = string.Empty;
        _lastStatsText = string.Empty;
        _lastRenderedEventSeq = 0;
        _renderedEventLines = 0;
        _eventsDirty = true;
        _lastRootVisible = false;

        Initialize(rootContainer);
        if (_chatLog != null && GodotObject.IsInstanceValid(_chatLog) && !string.IsNullOrEmpty(savedChatBbCode))
            _chatLog.Text = savedChatBbCode;

        RebuildEventsView();
        RefreshState();
    }

    private static Button MakeTabBtn(string text, string tooltip, Action onClick)
    {
        var btn = new Button
        {
            Text = text,
            TooltipText = tooltip,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(78, 36),
        };
        NativeUiHelper.InitSpireTabButton(btn, 14);
        btn.Pressed += onClick;
        return btn;
    }

    private static void BuildQuickPage()
    {
        _quickPage = new VBoxContainer { Visible = false };
        _quickPage.AddThemeConstantOverride("separation", 6);
        _drawer.AddChild(_quickPage);

        // God Mode / Creative Panel launcher
        _godModeLaunchBtn = new Button
        {
            Text = CoopQolI18n.T("上帝模式 · 创造面板 (F8)", "God Mode · Creative Panel (F8)"),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 40),
            TooltipText = CoopQolI18n.T(
                "打开创造面板：修改血量/金币/能量、增减状态Buff、获取卡牌/遗物/药水、秒杀清场",
                "Open Creative Panel: edit HP/Gold/Energy, add/remove Powers, spawn Cards/Relics/Potions"),
        };
        NativeUiHelper.InitSpireButton(_godModeLaunchBtn, 15, tone: SpireButtonTone.GoldAccent);
        _godModeLaunchBtn.Pressed += GodModeModule.Toggle;
        _quickPage.AddChild(_godModeLaunchBtn);

        // Restart current room & Auto-rollback previous floor
        var actionRow = new HBoxContainer();
        actionRow.AddThemeConstantOverride("separation", 6);
        _quickPage.AddChild(actionRow);

        _restartRoomBtn = new Button
        {
            Text = CoopQolI18n.T("重打本房", "Restart Room"),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 34),
            TooltipText = CoopQolI18n.T(
                "重新载入本房间起点存档（Shift+点击免确认，长按 F5 同样触发）",
                "Reload room start save (Shift+Click or Hold F5 skips confirm)"),
        };
        NativeUiHelper.InitSpireButton(_restartRoomBtn, 13, tone: SpireButtonTone.SlAccent);
        _restartRoomBtn.Pressed += () => QuickSlModule.RequestQuickSl(skipConfirm: Input.IsKeyPressed(Key.Shift));
        actionRow.AddChild(_restartRoomBtn);

        _prevFloorBtn = new Button
        {
            Text = CoopQolI18n.T("回退上层", "Prev Floor"),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 34),
            TooltipText = CoopQolI18n.T(
                "自动记录上一层起点存档，可一键回退到上一层",
                "Auto-snapshots previous floor start for one-click rollback"),
        };
        NativeUiHelper.InitSpireButton(_prevFloorBtn, 13);
        _prevFloorBtn.Pressed += () => QuickSlModule.RequestRollbackPrevFloor(skipConfirm: Input.IsKeyPressed(Key.Shift));
        actionRow.AddChild(_prevFloorBtn);

        // 3 Styled Save / Rollback slots
        _slotsBox = new VBoxContainer();
        _slotsBox.AddThemeConstantOverride("separation", 4);
        _quickPage.AddChild(_slotsBox);

        for (int i = 0; i < 3; i++)
        {
            int slotIdx = i;
            var slotCard = new PanelContainer();
            slotCard.AddThemeStyleboxOverride("panel", NativeUiHelper.GetSubCardStyle());
            _slotsBox.AddChild(slotCard);

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 5);
            slotCard.AddChild(row);

            var lbl = new Label
            {
                Text = QuickSlModule.GetSlotDisplayText(slotIdx),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                VerticalAlignment = VerticalAlignment.Center,
                ClipText = true,
            };
            NativeUiHelper.ApplyFont(lbl, 13, outlineSize: 3);
            lbl.AddThemeColorOverride("font_color", NativeUiHelper.SpireCream);
            SlotLabels[slotIdx] = lbl;
            row.AddChild(lbl);

            var saveBtn = new Button
            {
                Text = CoopQolI18n.T("存", "Save"),
                CustomMinimumSize = new Vector2(56, 28),
                TooltipText = CoopQolI18n.IsZh
                    ? $"将当前房间存档写入槽位 {slotIdx + 1}"
                    : $"Save current room to Slot {slotIdx + 1}",
            };
            NativeUiHelper.InitSpireButton(saveBtn, 13);
            saveBtn.Pressed += () => QuickSlModule.SaveToSlot(slotIdx);
            row.AddChild(saveBtn);

            var loadBtn = new Button
            {
                Text = CoopQolI18n.T("回", "Load"),
                CustomMinimumSize = new Vector2(56, 28),
                TooltipText = CoopQolI18n.IsZh
                    ? $"回档至槽位 {slotIdx + 1} 记录的层数与状态"
                    : $"Rollback to Slot {slotIdx + 1}",
            };
            NativeUiHelper.InitSpireButton(loadBtn, 13);
            loadBtn.Pressed += () => QuickSlModule.RequestLoadSlot(slotIdx, skipConfirm: Input.IsKeyPressed(Key.Shift));
            SlotLoadBtns[slotIdx] = loadBtn;
            row.AddChild(loadBtn);
        }

        _clientNoticeLabel = new Label
        {
            Text = CoopQolI18n.T(
                "联机客机存档由房主托管，可点击上方【请求房主重打】提醒房主。",
                "Saves are hosted by the lobby leader. Click [Request Restart] above to notify the Host."),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Visible = false,
        };
        NativeUiHelper.ApplyFont(_clientNoticeLabel, 12, outlineSize: 3);
        _clientNoticeLabel.AddThemeColorOverride("font_color", new Color(0.75f, 0.85f, 0.90f));
        _quickPage.AddChild(_clientNoticeLabel);

        // Multiplayer-only quick actions (2 compact rows)
        _coopActionBox = new VBoxContainer { Visible = false };
        _coopActionBox.AddThemeConstantOverride("separation", 4);
        _quickPage.AddChild(_coopActionBox);

        var coopRow1 = new HBoxContainer();
        coopRow1.AddThemeConstantOverride("separation", 5);
        _coopActionBox.AddChild(coopRow1);

        _spectateBtn = new Button
        {
            Text = CoopQolI18n.T("观战", "Spectate"),
            CustomMinimumSize = new Vector2(0, 30),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        NativeUiHelper.InitSpireButton(_spectateBtn, 12);
        _spectateBtn.Pressed += () =>
        {
            SpectateModule.CycleSpectateNext();
            RefreshQuickPage();
        };
        coopRow1.AddChild(_spectateBtn);

        var deckBtn = new Button
        {
            Text = CoopQolI18n.T("队友牌组", "Ally Deck"),
            CustomMinimumSize = new Vector2(0, 30),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        NativeUiHelper.InitSpireButton(deckBtn, 12);
        deckBtn.Pressed += SpectateModule.OpenFirstTeammateDeck;
        coopRow1.AddChild(deckBtn);

        var coopRow2 = new HBoxContainer();
        coopRow2.AddThemeConstantOverride("separation", 5);
        _coopActionBox.AddChild(coopRow2);

        coopRow2.AddChild(CreateQuickSignalBtn(
            CoopQolI18n.T("留怪", "Spare 1"),
            () => CoopQolI18n.T("请留一个残血怪让我触发效果！", "Please leave a low-HP enemy for my trigger!")));
        coopRow2.AddChild(CreateQuickSignalBtn(
            CoopQolI18n.T("免死", "Fairy"),
            () => CoopQolI18n.T("我有瓶中精灵/蜥蜴尾巴", "I have Fairy in a Bottle / Lizard Tail!")));
        coopRow2.AddChild(CreateQuickSignalBtn(
            CoopQolI18n.T("增伤", "Vuln"),
            () => CoopQolI18n.T("等我上易伤/增伤", "Wait for me to apply Vulnerable / damage amp!")));
        coopRow2.AddChild(CreateQuickSignalBtn(
            CoopQolI18n.T("求重开", "Req SL"),
            () => CoopQolI18n.T("【请求 SL】求房主重开本房！", "[SL Request] Please restart this room, Host!")));

        // Reserved for external ActionPack mods (always hidden when 0 actions registered)
        _actionPackBox = new HBoxContainer { Visible = false };
        _actionPackBox.AddThemeConstantOverride("separation", 5);
        _quickPage.AddChild(_actionPackBox);
    }

    private static void RebuildActionPackButtons()
    {
        if (_actionPackBox == null || !GodotObject.IsInstanceValid(_actionPackBox))
            return;

        foreach (var ctrl in ActionPackControls)
        {
            if (GodotObject.IsInstanceValid(ctrl))
                ctrl.QueueFree();
        }
        ActionPackControls.Clear();

        var actions = CharacterActionRegistry.Actions;
        bool hasActions = actions.Count > 0;
        _actionPackBox.Visible = hasActions;

        var chatBox = _chatActionPackBox;
        bool hasChatBox = chatBox != null && GodotObject.IsInstanceValid(chatBox);
        if (hasChatBox)
            chatBox!.Visible = hasActions;

        if (!hasActions)
            return;

        if (hasChatBox)
        {
            var actLbl = NativeUiHelper.CreateMegaLabel(
                CoopQolI18n.T("动作:", "Act:"),
                12,
                NativeUiHelper.SpireGold,
                HorizontalAlignment.Left,
                outlineSize: 3);
            actLbl.VerticalAlignment = VerticalAlignment.Center;
            ActionPackControls.Add(actLbl);
            chatBox!.AddChild(actLbl);
        }

        foreach (var act in actions)
        {
            var captured = act;
            string label = string.IsNullOrWhiteSpace(captured.Icon)
                ? captured.Title
                : $"{captured.Icon} {captured.Title}";

            var quickBtn = CreateActionPackButton(captured, label);
            ActionPackControls.Add(quickBtn);
            _actionPackBox.AddChild(quickBtn);

            if (hasChatBox)
            {
                var chatBtn = CreateActionPackButton(captured, label);
                ActionPackControls.Add(chatBtn);
                chatBox!.AddChild(chatBtn);
            }
        }
    }

    private static Button CreateActionPackButton(CharacterActionDef captured, string label)
    {
        var btn = new Button
        {
            Text = label,
            TooltipText = captured.Tooltip,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 30),
        };
        NativeUiHelper.InitSpireButton(btn, 12);
        btn.Pressed += () => CharacterActionRegistry.Trigger(captured.Id);
        return btn;
    }

    private static Button CreateQuickSignalBtn(string label, Func<string> payloadFactory)
    {
        var btn = new Button
        {
            Text = label,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 30),
        };
        NativeUiHelper.InitSpireButton(btn, 12);
        btn.Pressed += () => UnifiedTerminalModule.SendChatMessage(payloadFactory(), true);
        return btn;
    }

    private static void BuildDanmakuPage()
    {
        _chatPage = new VBoxContainer { Visible = false };
        _chatPage.AddThemeConstantOverride("separation", 5);
        _drawer.AddChild(_chatPage);

        // Quick danmaku preset chips
        var chipRow = new HBoxContainer();
        chipRow.AddThemeConstantOverride("separation", 5);
        _chatPage.AddChild(chipRow);

        (string Label, Func<string> Payload)[] chips =
        {
            (CoopQolI18n.T("好牌", "Nice!"), () => CoopQolI18n.T("这手打得漂亮！", "Nice play!")),
            (CoopQolI18n.T("留怪", "Spare 1"), () => CoopQolI18n.T("请留一个残血怪让我触发效果！", "Please leave a low-HP enemy for my trigger!")),
            (CoopQolI18n.T("免死", "Fairy"), () => CoopQolI18n.T("我有瓶中精灵/蜥蜴尾巴", "I have Fairy in a Bottle / Lizard Tail!")),
            (CoopQolI18n.T("增伤", "Vuln"), () => CoopQolI18n.T("等我上易伤/增伤", "Wait for me to apply Vulnerable / damage amp!")),
            (CoopQolI18n.T("求重开", "Req SL"), () => CoopQolI18n.T("【请求 SL】求房主重开本房！", "[SL Request] Please restart this room, Host!")),
        };
        foreach (var c in chips)
        {
            var payloadFn = c.Payload;
            var btn = new Button
            {
                Text = c.Label,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 30),
            };
            NativeUiHelper.InitSpireButton(btn, 12);
            btn.Pressed += () => UnifiedTerminalModule.SendChatMessage(payloadFn(), true);
            chipRow.AddChild(btn);
        }

        _chatActionPackBox = new HBoxContainer { Visible = false };
        _chatActionPackBox.AddThemeConstantOverride("separation", 5);
        _chatPage.AddChild(_chatActionPackBox);

        // Compact recent danmaku history box
        var logFrame = new PanelContainer();
        logFrame.AddThemeStyleboxOverride("panel", NativeUiHelper.GetLogPanelStyle());
        _chatPage.AddChild(logFrame);

        _chatLog = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollFollowing = true,
            SelectionEnabled = true,
            CustomMinimumSize = new Vector2(390, 138),
        };
        NativeUiHelper.ApplyRichTextFont(_chatLog, 13, outlineSize: 3);
        logFrame.AddChild(_chatLog);

        // Controls row
        var toolRow = new HBoxContainer();
        toolRow.AddThemeConstantOverride("separation", 5);
        _chatPage.AddChild(toolRow);

        _danmakuToggleBtn = new Button
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 30),
        };
        NativeUiHelper.InitSpireButton(_danmakuToggleBtn, 12);
        _danmakuToggleBtn.Pressed += ToggleBigScreenDanmaku;
        UpdateDanmakuToggleButtonText();
        toolRow.AddChild(_danmakuToggleBtn);

        var clearChatBtn = new Button
        {
            Text = CoopQolI18n.T("清空记录", "Clear"),
            CustomMinimumSize = new Vector2(84, 30),
        };
        NativeUiHelper.InitSpireButton(clearChatBtn, 12);
        clearChatBtn.Pressed += () => _chatLog.Text = string.Empty;
        toolRow.AddChild(clearChatBtn);

        // Input + Launch button
        var inputRow = new HBoxContainer();
        inputRow.AddThemeConstantOverride("separation", 5);
        _chatPage.AddChild(inputRow);

        _inputEdit = NativeUiHelper.CreateSpireLineEdit(
            CoopQolI18n.T("输入弹幕 (回车发送，悬停按G声明卡牌/遗物/状态)...", "Type message (Enter to send, Hover+G to declare)..."),
            13);
        _inputEdit.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _inputEdit.CustomMinimumSize = new Vector2(0, 34);
        _inputEdit.KeepEditingOnTextSubmit = false;
        _inputEdit.FocusEntered += UpdateInputHotkeyBlocking;
        _inputEdit.FocusExited += UpdateInputHotkeyBlocking;
        _inputEdit.EditingToggled += _ => UpdateInputHotkeyBlocking();
        _inputEdit.TextSubmitted += OnInputSubmitted;
        inputRow.AddChild(_inputEdit);

        var sendBtn = new Button
        {
            Text = CoopQolI18n.T("发射", "Send"),
            CustomMinimumSize = new Vector2(72, 34),
        };
        NativeUiHelper.InitSpireButton(sendBtn, 13, tone: SpireButtonTone.GoldAccent);
        sendBtn.Pressed += SubmitCurrentInput;
        inputRow.AddChild(sendBtn);
    }

    private static void BuildEventsPage()
    {
        _eventsPage = new VBoxContainer { Visible = false };
        _eventsPage.AddThemeConstantOverride("separation", 5);
        _drawer.AddChild(_eventsPage);

        var filterRow = new HBoxContainer();
        filterRow.AddThemeConstantOverride("separation", 4);
        _eventsPage.AddChild(filterRow);

        _filterMajorBtn = CreateFilterButton(() =>
        {
            CoopQolConfig.Current.FilterMajor = !CoopQolConfig.Current.FilterMajor;
            OnFilterChanged();
        });
        _filterMinorBtn = CreateFilterButton(() =>
        {
            CoopQolConfig.Current.FilterMinor = !CoopQolConfig.Current.FilterMinor;
            OnFilterChanged();
        });
        _filterDmgBtn = CreateFilterButton(() =>
        {
            CoopQolConfig.Current.FilterDamageStatus = !CoopQolConfig.Current.FilterDamageStatus;
            OnFilterChanged();
        });
        _filterNegBtn = CreateFilterButton(() =>
        {
            CoopQolConfig.Current.FilterNegative = !CoopQolConfig.Current.FilterNegative;
            OnFilterChanged();
        });

        filterRow.AddChild(_filterMajorBtn);
        filterRow.AddChild(_filterMinorBtn);
        filterRow.AddChild(_filterDmgBtn);
        filterRow.AddChild(_filterNegBtn);

        var clearEvBtn = new Button { Text = CoopQolI18n.T("清空", "Clear"), CustomMinimumSize = new Vector2(54, 30) };
        NativeUiHelper.InitSpireButton(clearEvBtn, 12);
        clearEvBtn.Pressed += () =>
        {
            EventStreamTracker.Clear();
            RebuildEventsView();
        };
        filterRow.AddChild(clearEvBtn);

        var logFrame = new PanelContainer();
        logFrame.AddThemeStyleboxOverride("panel", NativeUiHelper.GetLogPanelStyle());
        _eventsPage.AddChild(logFrame);

        _eventsLog = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollFollowing = true,
            SelectionEnabled = true,
            CustomMinimumSize = new Vector2(390, 170),
        };
        NativeUiHelper.ApplyRichTextFont(_eventsLog, 13, outlineSize: 3);
        logFrame.AddChild(_eventsLog);

        RefreshFilterButtons();
    }

    private static void BuildStatsPage()
    {
        _statsPage = new VBoxContainer { Visible = false };
        _statsPage.AddThemeConstantOverride("separation", 5);
        _drawer.AddChild(_statsPage);

        var modeRow = new HBoxContainer();
        modeRow.AddThemeConstantOverride("separation", 5);
        _statsPage.AddChild(modeRow);

        _statsCombatBtn = new Button { Text = CoopQolI18n.T("本场战斗", "This Combat"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 30) };
        NativeUiHelper.InitSpireButton(_statsCombatBtn, 12);
        _statsCombatBtn.Pressed += () =>
        {
            CoopQolConfig.Current.StatsViewMode = 0;
            CoopQolConfig.Save();
            RefreshStatsPage(force: true);
        };
        modeRow.AddChild(_statsCombatBtn);

        _statsRunBtn = new Button { Text = CoopQolI18n.T("本次历程", "Full Run"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 30) };
        NativeUiHelper.InitSpireButton(_statsRunBtn, 12);
        _statsRunBtn.Pressed += () =>
        {
            CoopQolConfig.Current.StatsViewMode = 1;
            CoopQolConfig.Save();
            RefreshStatsPage(force: true);
        };
        modeRow.AddChild(_statsRunBtn);

        _statsModeBtn = new Button { Text = CoopQolI18n.T("紧凑", "Compact"), CustomMinimumSize = new Vector2(74, 30) };
        NativeUiHelper.InitSpireButton(_statsModeBtn, 12);
        _statsModeBtn.Pressed += () =>
        {
            CoopQolConfig.Current.StatsDisplayState = CoopQolConfig.Current.StatsDisplayState == 0 ? 1 : 0;
            CoopQolConfig.Save();
            RefreshStatsPage(force: true);
        };
        modeRow.AddChild(_statsModeBtn);

        var logFrame = new PanelContainer();
        logFrame.AddThemeStyleboxOverride("panel", NativeUiHelper.GetLogPanelStyle());
        _statsPage.AddChild(logFrame);

        _statsLabel = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            CustomMinimumSize = new Vector2(390, 44),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        NativeUiHelper.ApplyRichTextFont(_statsLabel, 13, outlineSize: 3);
        logFrame.AddChild(_statsLabel);
    }

    private static Button CreateFilterButton(Action onClick)
    {
        var btn = new Button
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 30),
        };
        NativeUiHelper.InitSpireButton(btn, 12);
        btn.Pressed += onClick;
        return btn;
    }

    private static void SetMinimized(bool minimized)
    {
        _minimized = minimized;
        CoopQolConfig.Current.HudMinimized = minimized;
        if (!minimized && _activeTab < 0)
        {
            _activeTab = 0;
            CoopQolConfig.Current.ActiveHudTab = 0;
        }
        CoopQolConfig.Save();

        if (minimized)
            SetHotkeyBlocking(false);

        ApplyTabVisibility();

        if (!minimized)
        {
            if (_activeTab == 0) RefreshQuickPage();
            else if (_activeTab == 1) _unreadChatCount = 0;
            else if (_activeTab == 2 && _eventsDirty) FlushNewEventsIncremental();
            else if (_activeTab == 3) RefreshStatsPage(force: true);
        }

        UpdateChatTabText();
    }

    private static void ToggleTab(int tab)
    {
        if (_minimized)
        {
            _minimized = false;
            CoopQolConfig.Current.HudMinimized = false;
            SetActiveTab(tab);
            return;
        }
        SetActiveTab(_activeTab == tab ? -1 : tab);
    }

    public static void SetActiveTab(int tab)
    {
        _minimized = false;
        CoopQolConfig.Current.HudMinimized = false;
        _activeTab = tab;
        CoopQolConfig.Current.ActiveHudTab = tab;
        CoopQolConfig.Save();

        if (tab != 1)
            SetHotkeyBlocking(false);
        else
            _unreadChatCount = 0;

        ApplyTabVisibility();

        if (tab == 0) RefreshQuickPage();
        else if (tab == 2 && _eventsDirty) FlushNewEventsIncremental();
        else if (tab == 3) RefreshStatsPage(force: true);

        UpdateChatTabText();
    }

    private static void ApplyTabVisibility()
    {
        if (_panel == null || _minimizedBtn == null || _drawer == null) return;

        _minimizedBtn.Visible = _minimized;
        _panel.Visible = !_minimized;

        bool open = !_minimized && _activeTab >= 0;
        _drawer.Visible = open;

        _quickPage.Visible = _activeTab == 0;
        _chatPage.Visible = _activeTab == 1;
        _eventsPage.Visible = _activeTab == 2;
        _statsPage.Visible = _activeTab == 3;

        NativeUiHelper.SetSpireTabActive(_tabQuickBtn, _activeTab == 0);
        NativeUiHelper.SetSpireTabActive(_tabChatBtn, _activeTab == 1);
        NativeUiHelper.SetSpireTabActive(_tabEventsBtn, _activeTab == 2);
        NativeUiHelper.SetSpireTabActive(_tabStatsBtn, _activeTab == 3);
    }

    private static void OnSlotsChanged()
    {
        if (!_minimized && _activeTab == 0)
            RefreshQuickPage();
    }

    private static void RefreshQuickPage()
    {
        if (_quickPage == null || !_quickPage.Visible) return;

        var runState = PlayerNameHelper.GetRunState();
        bool isMulti = runState != null && runState.Players.Count > 1;
        var netType = RunManager.Instance?.NetService?.Type ?? NetGameType.Singleplayer;
        bool isClient = netType == NetGameType.Client;

        NativeUiHelper.SetButtonText(_restartRoomBtn, isClient
            ? CoopQolI18n.T("请求房主重打", "Request Restart")
            : CoopQolI18n.T("重打本房", "Restart Room"));

        _prevFloorBtn.Visible = !isClient;
        _prevFloorBtn.Disabled = !QuickSlModule.HasPrevFloorRollback;
        NativeUiHelper.SetButtonText(
            _prevFloorBtn,
            QuickSlModule.HasPrevFloorRollback
                ? (CoopQolI18n.IsZh ? $"回退({QuickSlModule.PrevFloorSummary})" : $"Prev ({QuickSlModule.PrevFloorSummary})")
                : CoopQolI18n.T("回退上层(无)", "Prev Floor (None)"));

        _slotsBox.Visible = !isClient;
        _clientNoticeLabel.Visible = isClient;

        if (!isClient)
        {
            for (int i = 0; i < 3; i++)
            {
                SlotLabels[i].Text = QuickSlModule.GetSlotDisplayText(i);
                SlotLoadBtns[i].Disabled = !QuickSlModule.CanLoadSlot(i);
            }
        }

        _coopActionBox.Visible = isMulti;
        if (isMulti)
        {
            var target = SpectateModule.GetSpectatingPlayer();
            NativeUiHelper.SetButtonText(
                _spectateBtn,
                target != null
                    ? (CoopQolI18n.IsZh ? $"观战:{PlayerNameHelper.GetPlayerName(target)}" : $"Spectate: {PlayerNameHelper.GetPlayerName(target)}")
                    : CoopQolI18n.T("观战", "Spectate"));
            NativeUiHelper.SetSpireButtonActive(_spectateBtn, target != null);
        }
    }

    private static void RefreshStatsPage(bool force = false)
    {
        if (_statsPage == null) return;
        if (!force && !CombatStatsModule.ConsumeDirtyFlag()) return;
        CombatStatsModule.ConsumeDirtyFlag();

        int view = CoopQolConfig.Current.StatsViewMode;
        int state = CoopQolConfig.Current.StatsDisplayState;

        NativeUiHelper.SetSpireButtonActive(_statsCombatBtn, view == 0);
        NativeUiHelper.SetSpireButtonActive(_statsRunBtn, view == 1);
        NativeUiHelper.SetButtonText(_statsModeBtn, state == 0
            ? CoopQolI18n.T("详细", "Detail")
            : CoopQolI18n.T("紧凑", "Compact"));

        var text = CombatStatsModule.BuildStatsBbCode(view, state);
        if (text != _lastStatsText)
        {
            _lastStatsText = text;
            _statsLabel.Text = text;
        }
    }

    private static void OnStreamEventAdded(StreamEventItem item)
    {
        _eventsDirty = true;
    }

    private static void OnFilterChanged()
    {
        CoopQolConfig.Save();
        RefreshFilterButtons();
        _eventsDirty = true;
        RebuildEventsView();
    }

    private static void RefreshFilterButtons()
    {
        var cfg = CoopQolConfig.Current;
        NativeUiHelper.SetButtonText(_filterMajorBtn, cfg.FilterMajor ? CoopQolI18n.T("大事件[开]", "Major[ON]") : CoopQolI18n.T("大事件", "Major"));
        NativeUiHelper.SetButtonText(_filterMinorBtn, cfg.FilterMinor ? CoopQolI18n.T("小事件[开]", "Minor[ON]") : CoopQolI18n.T("小事件", "Minor"));
        NativeUiHelper.SetButtonText(_filterDmgBtn, cfg.FilterDamageStatus ? CoopQolI18n.T("伤害[开]", "Dmg[ON]") : CoopQolI18n.T("伤害", "Dmg"));
        NativeUiHelper.SetButtonText(_filterNegBtn, cfg.FilterNegative ? CoopQolI18n.T("否定[开]", "Skip[ON]") : CoopQolI18n.T("否定", "Skip"));

        NativeUiHelper.SetSpireButtonActive(_filterMajorBtn, cfg.FilterMajor);
        NativeUiHelper.SetSpireButtonActive(_filterMinorBtn, cfg.FilterMinor);
        NativeUiHelper.SetSpireButtonActive(_filterDmgBtn, cfg.FilterDamageStatus);
        NativeUiHelper.SetSpireButtonActive(_filterNegBtn, cfg.FilterNegative);
    }

    private static void FlushNewEventsIncremental()
    {
        _eventsDirty = false;
        if (_renderedEventLines > 160)
        {
            RebuildEventsView();
            return;
        }

        var cfg = CoopQolConfig.Current;
        var snapshot = EventStreamTracker.GetSnapshot();
        StringBuilder? sb = null;
        long maxSeq = _lastRenderedEventSeq;

        foreach (var item in snapshot)
        {
            if (item.Seq <= _lastRenderedEventSeq) continue;
            if (item.Seq > maxSeq) maxSeq = item.Seq;

            bool include = item.Category switch
            {
                EventCategory.Major => cfg.FilterMajor,
                EventCategory.Minor => cfg.FilterMinor,
                EventCategory.DamageStatus => cfg.FilterDamageStatus,
                EventCategory.Negative => cfg.FilterNegative,
                _ => false
            };
            if (!include) continue;

            sb ??= new StringBuilder(128);
            sb.Append("[color=#6c757d]#")
              .Append(item.Seq)
              .Append("[/color] ")
              .Append(item.FormattedText)
              .Append('\n');
            _renderedEventLines++;
        }

        _lastRenderedEventSeq = maxSeq;
        if (sb != null && sb.Length > 0)
            _eventsLog.AppendText(sb.ToString());
    }

    private static void RebuildEventsView()
    {
        _eventsDirty = false;
        var cfg = CoopQolConfig.Current;
        var snapshot = EventStreamTracker.GetSnapshot();
        var sb = new StringBuilder(snapshot.Count * 48);
        long maxSeq = 0;
        int lines = 0;

        foreach (var item in snapshot)
        {
            if (item.Seq > maxSeq) maxSeq = item.Seq;

            bool include = item.Category switch
            {
                EventCategory.Major => cfg.FilterMajor,
                EventCategory.Minor => cfg.FilterMinor,
                EventCategory.DamageStatus => cfg.FilterDamageStatus,
                EventCategory.Negative => cfg.FilterNegative,
                _ => false
            };
            if (!include) continue;

            sb.Append("[color=#6c757d]#")
              .Append(item.Seq)
              .Append("[/color] ")
              .Append(item.FormattedText)
              .Append('\n');
            lines++;
        }

        _lastRenderedEventSeq = maxSeq;
        _renderedEventLines = lines;
        _eventsLog.Text = sb.ToString();
    }

    private static void ToggleBigScreenDanmaku()
    {
        CoopQolConfig.Current.ShowBigScreenDanmaku = !CoopQolConfig.Current.ShowBigScreenDanmaku;
        CoopQolConfig.Save();
        UpdateDanmakuToggleButtonText();
    }

    private static void UpdateDanmakuToggleButtonText()
    {
        bool on = CoopQolConfig.Current.ShowBigScreenDanmaku;
        NativeUiHelper.SetButtonText(_danmakuToggleBtn, on
            ? CoopQolI18n.T("大屏飞行弹幕：开启", "Flying Barrage: ON")
            : CoopQolI18n.T("大屏飞行弹幕：关闭", "Flying Barrage: OFF"));
        NativeUiHelper.SetSpireButtonActive(_danmakuToggleBtn, on);
    }

    private static void UpdateInputHotkeyBlocking()
    {
        SetHotkeyBlocking(IsEditingInput);
    }

    private static void SubmitCurrentInput()
    {
        if (_inputEdit == null || !GodotObject.IsInstanceValid(_inputEdit))
            return;

        if (_inputEdit.HasImeText())
        {
            try { _inputEdit.ApplyIme(); } catch { }
        }

        OnInputSubmitted(_inputEdit.Text);
    }

    private static void OnInputSubmitted(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
            UnifiedTerminalModule.SendChatMessage(text, true);

        if (_inputEdit != null && GodotObject.IsInstanceValid(_inputEdit))
        {
            _inputEdit.Text = string.Empty;
            _inputEdit.Unedit();
            _inputEdit.ReleaseFocus();
            NativeUiHelper.ApplyImeState(_inputEdit, false);
        }

        try
        {
            MegaCrit.Sts2.Core.Platform.PlatformUtil.CloseVirtualKeyboard();
            DisplayServer.VirtualKeyboardHide();
        }
        catch { }

        SetHotkeyBlocking(false);
    }

    private static void SetHotkeyBlocking(bool blocking)
    {
        if (blocking == _isBlockingHotkeys) return;
        _isBlockingHotkeys = blocking;

        try
        {
            var mgr = NHotkeyManager.Instance;
            if (mgr == null || _panel == null) return;
            if (blocking)
                mgr.AddBlockingScreen(_panel);
            else
                mgr.RemoveBlockingScreen(_panel);
        }
        catch (Exception ex)
        {
            CoopQolModEntry.Log.Warn($"Hotkey blocking toggle failed: {ex.Message}");
        }
    }

    private static void OnMinimizedGuiInput(InputEvent @event)
    {
        if (_minimizedBtn == null || _root == null || _panel == null) return;

        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            if (mb.Pressed)
            {
                _minDragging = true;
                _minDraggedMoved = false;
                _minDragStartMouse = _root.GetGlobalMousePosition();
                _minDragOffset = _minDragStartMouse - _minimizedBtn.Position;
            }
            else if (_minDragging)
            {
                _minDragging = false;
                if (_minDraggedMoved)
                {
                    _panel.Position = _minimizedBtn.Position;
                    CoopQolConfig.Current.QuickBarPosX = _minimizedBtn.Position.X;
                    CoopQolConfig.Current.QuickBarPosY = _minimizedBtn.Position.Y;
                    CoopQolConfig.Save();
                }
                else
                {
                    SetMinimized(false);
                }
                _minimizedBtn.AcceptEvent();
            }
        }
        else if (@event is InputEventMouseMotion && _minDragging)
        {
            var mouse = _root.GetGlobalMousePosition();
            if (!_minDraggedMoved && mouse.DistanceTo(_minDragStartMouse) > 6f)
                _minDraggedMoved = true;

            if (_minDraggedMoved)
            {
                var vp = _root.GetViewportRect().Size;
                if (vp.X <= 0) vp = new Vector2(1920, 1080);
                var target = mouse - _minDragOffset;
                target.X = Mathf.Clamp(target.X, 8f, Math.Max(8f, vp.X - _minimizedBtn.Size.X - 8f));
                target.Y = Mathf.Clamp(target.Y, 68f, Math.Max(68f, vp.Y - _minimizedBtn.Size.Y - 8f));
                _minimizedBtn.Position = target;
            }
        }
    }

    private static void OnDragGuiInput(InputEvent @event)
    {
        if (_panel == null || _root == null) return;

        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            if (mb.Pressed)
            {
                _dragging = true;
                _dragOffset = _root.GetGlobalMousePosition() - _panel.Position;
            }
            else if (_dragging)
            {
                _dragging = false;
                _minimizedBtn.Position = _panel.Position;
                CoopQolConfig.Current.QuickBarPosX = _panel.Position.X;
                CoopQolConfig.Current.QuickBarPosY = _panel.Position.Y;
                CoopQolConfig.Save();
            }
        }
        else if (@event is InputEventMouseMotion && _dragging)
        {
            var vp = _root.GetViewportRect().Size;
            if (vp.X <= 0) vp = new Vector2(1920, 1080);
            var target = _root.GetGlobalMousePosition() - _dragOffset;
            target.X = Mathf.Clamp(target.X, 8f, Math.Max(8f, vp.X - _panel.Size.X - 8f));
            target.Y = Mathf.Clamp(target.Y, 68f, Math.Max(68f, vp.Y - _panel.Size.Y - 8f));
            _panel.Position = target;
        }
    }

    public static void Tick(double delta)
    {
        bool needChat = UnifiedTerminalModule.HasPendingChat;
        bool needEvents = !_minimized && _activeTab == 2 && _eventsDirty;
        bool needStats = !_minimized && _activeTab == 3 && CombatStatsModule.HasDirtyStats;

        if (!needChat && !needEvents && !needStats)
            return;

        if (needChat)
        {
            while (UnifiedTerminalModule.TryDequeueChat(out var msg))
            {
                var safeSender = PlayerNameHelper.EscapeBbCode(msg.Sender);
                var safeText = PlayerNameHelper.EscapeBbCode(msg.Text);
                var color = msg.IsSelf ? "#80ed99" : "#72efdd";
                var time = DateTime.Now.ToString("HH:mm");

                _chatLog.AppendText($"[color=#8d99ae][{time}][/color] [b][color={color}]{safeSender}[/color][/b]: {safeText}\n");

                if ((_minimized || _activeTab != 1) && !msg.IsSelf)
                    _unreadChatCount++;

                if (msg.BigScreen)
                    UnifiedTerminalModule.TriggerBigScreen(safeSender, safeText, color);
            }
            UpdateChatTabText();
        }

        if (needEvents)
        {
            _eventFlushAccum += delta;
            if (_eventFlushAccum >= 0.25)
            {
                _eventFlushAccum = 0;
                FlushNewEventsIncremental();
            }
        }

        if (needStats)
        {
            _statsFlushAccum += delta;
            if (_statsFlushAccum >= 0.25)
            {
                _statsFlushAccum = 0;
                RefreshStatsPage();
            }
        }
    }

    public static void HandleKeyPress(InputEventKey key)
    {
        if (_root == null || !NativeUiHelper.IsInActiveRun())
            return;

        if (_inputEdit != null && (_inputEdit.HasFocus() || _inputEdit.IsEditing()))
            return;

        if (GodModeModule.IsOpen)
            return;

        if (key.Keycode == Key.F6)
        {
            ToggleTab(3);
            NGame.Instance?.GetViewport()?.SetInputAsHandled();
        }
        else if (key.Keycode == Key.F7)
        {
            ToggleTab(2);
            NGame.Instance?.GetViewport()?.SetInputAsHandled();
        }
        else if (key.Keycode is Key.Enter or Key.KpEnter)
        {
            SetActiveTab(1);
            Callable.From(() =>
            {
                if (_inputEdit != null && GodotObject.IsInstanceValid(_inputEdit) && _inputEdit.IsVisibleInTree())
                    NativeUiHelper.ActivateLineEdit(_inputEdit);
            }).CallDeferred();
            NGame.Instance?.GetViewport()?.SetInputAsHandled();
        }
    }

    private static void UpdateChatTabText()
    {
        bool showBadge = _unreadChatCount > 0 && (_minimized || _activeTab != 1);
        string chatBase = CoopQolI18n.T("弹幕", "Chat");
        string target = showBadge ? $"{chatBase}({_unreadChatCount})" : chatBase;
        if (target != _lastChatBtnText)
        {
            _lastChatBtnText = target;
            NativeUiHelper.SetButtonText(_tabChatBtn, target);
        }

        string minBase = CoopQolI18n.T("工具", "QoL");
        string minTarget = showBadge ? $"{minBase}({_unreadChatCount})" : minBase;
        if (minTarget != _lastMinBtnText)
        {
            _lastMinBtnText = minTarget;
            NativeUiHelper.SetButtonText(_minimizedBtn, minTarget);
        }
    }

    public static void Refresh()
    {
        Callable.From(RefreshState).CallDeferred();
    }

    public static void RefreshState()
    {
        if (_root == null || !GodotObject.IsInstanceValid(_root) || _panel == null || _minimizedBtn == null)
            return;

        bool inRun = NativeUiHelper.IsInActiveRun() && CoopQolConfig.Current.ShowTopBarButtons;
        if (inRun != _lastRootVisible)
        {
            _lastRootVisible = inRun;
            _root.Visible = inRun;
        }
        if (!inRun) return;

        var runState = PlayerNameHelper.GetRunState();
        bool isMulti = runState != null && runState.Players.Count > 1;

        if (!_dragging && !_minDragging)
        {
            var vp = _root.GetViewportRect().Size;
            if (vp.X <= 0) vp = new Vector2(1920, 1080);
            float defaultX = isMulti ? 390f : 16f;
            float defaultY = isMulti ? 88f : 112f;
            float x = CoopQolConfig.Current.QuickBarPosX >= 0f ? CoopQolConfig.Current.QuickBarPosX : defaultX;
            float y = CoopQolConfig.Current.QuickBarPosY >= 0f ? CoopQolConfig.Current.QuickBarPosY : defaultY;
            x = Mathf.Clamp(x, 8f, Math.Max(8f, vp.X - Math.Max(_panel.Size.X, 280f) - 8f));
            y = Mathf.Clamp(y, 68f, Math.Max(68f, vp.Y - Math.Max(_panel.Size.Y, 44f) - 8f));
            var targetPos = new Vector2(x, y);
            if (_panel.Position != targetPos)
                _panel.Position = targetPos;
            if (_minimizedBtn.Position != targetPos)
                _minimizedBtn.Position = targetPos;
        }

        if (!_minimized && _activeTab == 0)
            RefreshQuickPage();
        else if (!_minimized && _activeTab == 3)
            RefreshStatsPage(force: true);
    }
}
