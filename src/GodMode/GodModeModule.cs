using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using CoopQolSuite.Core;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Debug;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace CoopQolSuite.GodMode;

public static class GodModeModule
{
    private const int MaxPooledRows = 36;

    private static bool _initialized;
    private static MegaCrit.Sts2.Core.DevConsole.DevConsole? _devConsole;
    private static bool _godModeActive;
    private static bool _isBlockingHotkeys;

    // Root modal nodes
    private static Control? _backdrop;
    private static PanelContainer? _modalPanel;
    private static Label _modeBadgeLabel = null!;
    private static Label _statusFooterLabel = null!;
    private static bool _dragging;
    private static Vector2 _dragOffset;

    // 4 sub-tabs & Target Member Selector
    private static readonly Button[] SubTabBtns = new Button[4];
    private static readonly Control[] SubPages = new Control[4];
    private static int _activeSubTab;

    private static HBoxContainer _memberSelectorRow = null!;
    private static readonly List<Button> MemberBtns = new();
    private static ulong _selectedPlayerNetId;
    private static readonly MethodInfo? HostEnqueueActionMethod =
        AccessTools.Method(typeof(ActionQueueSynchronizer), "EnqueueAction", new[] { typeof(GameAction), typeof(ulong) });

    // Sub-tab 0: Stats & Combat
    private static Label _liveStatsLabel = null!;
    private static Button _godToggleBtn = null!;

    // Sub-tab 1: Powers / Buffs
    private static HBoxContainer _powerTargetRow = null!;
    private static readonly List<Button> PowerTargetBtns = new();
    private static int _powerTargetMode; // 0 = Self, 1 = AllEnemies, 2+ = Specific Creature index
    private static int _powerStackAmount = 3;
    private static readonly int[] PowerAmounts = { -10, -3, -1, 1, 3, 5, 10, 99 };
    private static readonly Button[] PowerAmountBtns = new Button[PowerAmounts.Length];
    private static int _powerFilterMode; // 0 = All, 1 = Buff, 2 = Debuff
    private static readonly Button[] PowerFilterBtns = new Button[3];
    private static LineEdit _powerSearchEdit = null!;
    private static readonly Button[] PowerPoolRows = new Button[MaxPooledRows];
    private static readonly Action[] PowerPoolActions = new Action[MaxPooledRows];
    private static List<PowerModel>? _cachedAllPowers;

    // Sub-tab 2: Cards
    private static int _cardMode; // 0 = Obtain from Library, 1 = Manage Current Hand/Deck
    private static bool _cardUpgraded;
    private static bool _cardToHand = true;
    private static int _cardFilter; // 0=All, 1=Class, 2=Attack, 3=Skill, 4=Power, 5=Curse/Status
    private static Button _cardModeLibBtn = null!;
    private static Button _cardModeDeckBtn = null!;
    private static Button _cardUpgradeCheckBtn = null!;
    private static Button _cardDestHandBtn = null!;
    private static Button _cardDestDeckBtn = null!;
    private static readonly Button[] CardFilterBtns = new Button[6];
    private static LineEdit _cardSearchEdit = null!;
    private static readonly Button[] CardPoolRows = new Button[MaxPooledRows];
    private static readonly Action[] CardPoolActions = new Action[MaxPooledRows];
    private static List<CardModel>? _cachedAllCards;

    // Sub-tab 3: Relics & Potions
    private static int _itemMode; // 0 = Relics, 1 = Potions, 2 = Owned Relics (Remove)
    private static int _itemRarityFilter; // 0=All, 1=Common, 2=Uncommon, 3=Rare, 4=Boss/Ancient/Event/Shop
    private static Button _itemModeRelicBtn = null!;
    private static Button _itemModePotionBtn = null!;
    private static Button _itemModeOwnedRelicBtn = null!;
    private static Button _potionSlotPlusBtn = null!;
    private static readonly Button[] ItemRarityBtns = new Button[5];
    private static LineEdit _itemSearchEdit = null!;
    private static readonly Button[] ItemPoolRows = new Button[MaxPooledRows];
    private static readonly Action[] ItemPoolActions = new Action[MaxPooledRows];
    private static List<RelicModel>? _cachedAllRelics;
    private static List<PotionModel>? _cachedAllPotions;

    public static bool IsOpen => _backdrop != null && GodotObject.IsInstanceValid(_backdrop) && _backdrop.Visible;

    private static MegaCrit.Sts2.Core.DevConsole.DevConsole Console =>
        _devConsole ??= new MegaCrit.Sts2.Core.DevConsole.DevConsole(shouldAllowDebugCommands: true);

    public static void Initialize(Node rootContainer)
    {
        if (_initialized) return;
        _initialized = true;
        BuildModalUi(rootContainer);
    }

    public static void OnRunEntered()
    {
        _godModeActive = false;
        if (IsOpen)
            RefreshAllActiveViews();
    }

    public static void OnRunExited()
    {
        Close();
    }

    public static void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    public static void Open()
    {
        if (_backdrop == null || !GodotObject.IsInstanceValid(_backdrop))
            return;

        if (!NativeUiHelper.IsInActiveRun())
        {
            SetStatus(CoopQolI18n.T("请先进入一局游戏后再打开创造面板。", "Start or enter a run before opening Creative Panel."), isError: true);
            return;
        }

        _backdrop.Visible = true;
        CenterModalIfNeeded();
        SwitchSubTab(_activeSubTab);
    }

    public static void Close()
    {
        SetHotkeyBlocking(false);
        if (_backdrop != null && GodotObject.IsInstanceValid(_backdrop))
            _backdrop.Visible = false;
    }

    public static bool HandleKeyPress(InputEventKey key)
    {
        if (key.Keycode == Key.F8 && NativeUiHelper.IsInActiveRun())
        {
            Toggle();
            NGame.Instance?.GetViewport()?.SetInputAsHandled();
            return true;
        }

        if (IsOpen && key.Keycode == Key.Escape)
        {
            Close();
            NGame.Instance?.GetViewport()?.SetInputAsHandled();
            return true;
        }

        return false;
    }

    public static void Relocalize(Node rootContainer)
    {
        _cachedAllPowers = null;
        _cachedAllCards = null;
        _cachedAllRelics = null;
        _cachedAllPotions = null;

        bool wasOpen = IsOpen;
        Vector2 savedPos = _modalPanel != null && GodotObject.IsInstanceValid(_modalPanel)
            ? _modalPanel.Position
            : Vector2.Zero;

        SetHotkeyBlocking(false);
        if (_backdrop != null && GodotObject.IsInstanceValid(_backdrop))
            _backdrop.QueueFree();

        _backdrop = null;
        _modalPanel = null;
        _initialized = false;
        MemberBtns.Clear();
        PowerTargetBtns.Clear();

        Initialize(rootContainer);
        if (savedPos != Vector2.Zero && _modalPanel != null && GodotObject.IsInstanceValid(_modalPanel))
            _modalPanel.Position = savedPos;

        if (wasOpen)
            Open();
    }

    private static void BuildModalUi(Node rootContainer)
    {
        _backdrop = new ColorRect
        {
            Name = "CoopQolGodModeBackdrop",
            Color = new Color(0.01f, 0.02f, 0.03f, 0.45f),
            MouseFilter = Control.MouseFilterEnum.Stop,
            Visible = false,
        };
        _backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _backdrop.TreeExiting += () => SetHotkeyBlocking(false);

        _modalPanel = new PanelContainer
        {
            Name = "CoopQolGodModePanel",
            CustomMinimumSize = new Vector2(860, 560),
            Size = new Vector2(860, 560),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        _modalPanel.AddThemeStyleboxOverride("panel", NativeUiHelper.GetModalPanelStyle());
        _backdrop.AddChild(_modalPanel);

        var rootVBox = new VBoxContainer();
        rootVBox.AddThemeConstantOverride("separation", 9);
        _modalPanel.AddChild(rootVBox);

        // Header bar with genuine STS2 parchment banner (draggable)
        var bannerTex = NativeUiHelper.GetRewardBannerTexture();
        if (bannerTex != null)
        {
            var bannerHost = new Control
            {
                CustomMinimumSize = new Vector2(0, 56),
                MouseFilter = Control.MouseFilterEnum.Stop,
            };
            bannerHost.GuiInput += OnHeaderGuiInput;
            rootVBox.AddChild(bannerHost);

            var bannerRect = new TextureRect
            {
                Texture = bannerTex,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            bannerRect.SetAnchorsPreset(Control.LayoutPreset.Center);
            bannerRect.OffsetLeft = -260f;
            bannerRect.OffsetRight = 260f;
            bannerRect.OffsetTop = -44f;
            bannerRect.OffsetBottom = 38f;
            bannerHost.AddChild(bannerRect);

            var titleLbl = NativeUiHelper.CreateMegaLabel(
                CoopQolI18n.T("上帝模式 · 创造面板", "God Mode · Creative Panel"),
                22,
                NativeUiHelper.SpireCream,
                HorizontalAlignment.Center,
                outlineSize: 7,
                outlineColor: NativeUiHelper.SpireBannerOutline);
            titleLbl.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            titleLbl.OffsetTop = -6f;
            titleLbl.OffsetBottom = -12f;
            bannerRect.AddChild(titleLbl);

            _modeBadgeLabel = NativeUiHelper.CreateMegaLabel(
                CoopQolI18n.T("单人模式", "Singleplayer"),
                12,
                new Color(0.62f, 0.90f, 0.70f),
                HorizontalAlignment.Left,
                outlineSize: 3);
            _modeBadgeLabel.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
            _modeBadgeLabel.OffsetLeft = 4f;
            _modeBadgeLabel.OffsetTop = -20f;
            _modeBadgeLabel.OffsetRight = 240f;
            bannerHost.AddChild(_modeBadgeLabel);

            var closeBtn = new Button
            {
                Text = CoopQolI18n.T("关闭", "Close"),
                CustomMinimumSize = new Vector2(68, 30),
                TooltipText = CoopQolI18n.T("关闭创造面板 (Esc)", "Close Creative Panel (Esc)"),
            };
            closeBtn.SetAnchorsPreset(Control.LayoutPreset.TopRight);
            closeBtn.OffsetLeft = -72f;
            closeBtn.OffsetTop = 2f;
            NativeUiHelper.InitSpireButton(closeBtn, 13, tone: SpireButtonTone.Danger);
            closeBtn.Pressed += Close;
            bannerHost.AddChild(closeBtn);
        }
        else
        {
            var headerPanel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
            headerPanel.AddThemeStyleboxOverride("panel", NativeUiHelper.GetHeaderBarStyle());
            headerPanel.GuiInput += OnHeaderGuiInput;
            rootVBox.AddChild(headerPanel);

            var headerRow = new HBoxContainer();
            headerRow.AddThemeConstantOverride("separation", 10);
            headerPanel.AddChild(headerRow);

            var titleLbl = NativeUiHelper.CreateMegaLabel(
                CoopQolI18n.T("上帝模式 · 创造面板", "God Mode · Creative Panel"),
                19, NativeUiHelper.SpireGold, HorizontalAlignment.Left, outlineSize: 5);
            titleLbl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            headerRow.AddChild(titleLbl);

            _modeBadgeLabel = NativeUiHelper.CreateMegaLabel(
                CoopQolI18n.T("单人模式", "Singleplayer"),
                13, new Color(0.62f, 0.90f, 0.70f), HorizontalAlignment.Right, outlineSize: 3);
            headerRow.AddChild(_modeBadgeLabel);

            var closeBtn = new Button
            {
                Text = CoopQolI18n.T("关闭", "Close"),
                CustomMinimumSize = new Vector2(68, 30),
                TooltipText = CoopQolI18n.T("关闭创造面板 (Esc)", "Close Creative Panel (Esc)"),
            };
            NativeUiHelper.InitSpireButton(closeBtn, 13, tone: SpireButtonTone.Danger);
            closeBtn.Pressed += Close;
            headerRow.AddChild(closeBtn);
        }

        // 4 Centered Sub-Tabs
        var tabRow = new HBoxContainer();
        tabRow.AddThemeConstantOverride("separation", 8);
        rootVBox.AddChild(tabRow);

        string[] tabTitles =
        {
            CoopQolI18n.T("属性与战斗", "Stats & Combat"),
            CoopQolI18n.T("状态 / Buff", "Powers / Buffs"),
            CoopQolI18n.T("卡牌库", "Cards"),
            CoopQolI18n.T("遗物与药水", "Relics & Potions"),
        };
        for (int i = 0; i < 4; i++)
        {
            int idx = i;
            var btn = new Button
            {
                Text = tabTitles[i],
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 38),
            };
            NativeUiHelper.InitSpireTabButton(btn, 15);
            btn.Pressed += () => SwitchSubTab(idx);
            SubTabBtns[i] = btn;
            tabRow.AddChild(btn);
        }

        // Target Member Selector Row (Singleplayer: Self; Multiplayer Host: Any member; Client: Self)
        var memberPanel = new PanelContainer();
        memberPanel.AddThemeStyleboxOverride("panel", NativeUiHelper.GetSubCardStyle());
        rootVBox.AddChild(memberPanel);

        _memberSelectorRow = new HBoxContainer();
        _memberSelectorRow.AddThemeConstantOverride("separation", 6);
        memberPanel.AddChild(_memberSelectorRow);

        // Content stack
        var contentHost = new VBoxContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        rootVBox.AddChild(contentHost);

        BuildStatsPage(contentHost);
        BuildPowersPage(contentHost);
        BuildCardsPage(contentHost);
        BuildItemsPage(contentHost);

        // Footer status bar
        var footerPanel = new PanelContainer();
        footerPanel.AddThemeStyleboxOverride("panel", NativeUiHelper.GetLogPanelStyle());
        rootVBox.AddChild(footerPanel);

        var footerRow = new HBoxContainer();
        footerPanel.AddChild(footerRow);

        _statusFooterLabel = new Label
        {
            Text = CoopQolI18n.T("就绪：点击任意按钮即可实时生效。", "Ready: click any button to apply immediately."),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipText = true,
        };
        NativeUiHelper.ApplyFont(_statusFooterLabel, 13, outlineSize: 3);
        _statusFooterLabel.AddThemeColorOverride("font_color", new Color(0.62f, 0.90f, 0.70f));
        footerRow.AddChild(_statusFooterLabel);

        var escHint = new Label { Text = CoopQolI18n.T("按 Esc 或右上角关闭", "Esc or Close") };
        NativeUiHelper.ApplyFont(escHint, 12, outlineSize: 3);
        escHint.AddThemeColorOverride("font_color", NativeUiHelper.SpireMutedText);
        footerRow.AddChild(escHint);

        rootContainer.AddChild(_backdrop);
    }

    private static void BuildStatsPage(Control parent)
    {
        var page = new VBoxContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            Visible = false,
        };
        page.AddThemeConstantOverride("separation", 10);
        SubPages[0] = page;
        parent.AddChild(page);

        // Live stats strip
        var stripPanel = new PanelContainer();
        stripPanel.AddThemeStyleboxOverride("panel", NativeUiHelper.GetSubCardStyle());
        page.AddChild(stripPanel);

        var stripRow = new HBoxContainer();
        stripRow.AddThemeConstantOverride("separation", 10);
        stripPanel.AddChild(stripRow);

        _liveStatsLabel = new Label
        {
            Text = CoopQolI18n.T("生命: --/--   金币: --   能量: --   格挡: --", "HP: --/--   Gold: --   Energy: --   Block: --"),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
        };
        NativeUiHelper.ApplyFont(_liveStatsLabel, 15, outlineSize: 4);
        _liveStatsLabel.AddThemeColorOverride("font_color", NativeUiHelper.SpireCream);
        stripRow.AddChild(_liveStatsLabel);

        var refreshBtn = new Button
        {
            Text = CoopQolI18n.T("刷新", "Refresh"),
            CustomMinimumSize = new Vector2(76, 30),
        };
        NativeUiHelper.InitSpireButton(refreshBtn, 13);
        refreshBtn.Pressed += RefreshStatsStrip;
        stripRow.AddChild(refreshBtn);

        // 2-column grid
        var grid = new GridContainer
        {
            Columns = 2,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 10);
        page.AddChild(grid);

        // Left card: HP & Gold
        var hpCard = CreateGroupCard(CoopQolI18n.T("生命与金币修改", "HP & Gold"), grid);
        var hpRow1 = CreateRow(hpCard);
        AddActionBtn(hpRow1, CoopQolI18n.T("生命+10", "HP +10"), () => AdjustHp(10));
        AddActionBtn(hpRow1, CoopQolI18n.T("生命+50", "HP +50"), () => AdjustHp(50));
        AddActionBtn(hpRow1, CoopQolI18n.T("生命-10", "HP -10"), () => AdjustHp(-10));
        AddActionBtn(hpRow1, CoopQolI18n.T("回满血", "Full HP"), HealFull, SpireButtonTone.GoldAccent);

        var hpRow2 = CreateRow(hpCard);
        AddActionBtn(hpRow2, CoopQolI18n.T("上限+10", "Max +10"), () => AdjustMaxHp(10));
        AddActionBtn(hpRow2, CoopQolI18n.T("上限+50", "Max +50"), () => AdjustMaxHp(50));
        AddActionBtn(hpRow2, CoopQolI18n.T("上限-10", "Max -10"), () => AdjustMaxHp(-10));

        var goldRow = CreateRow(hpCard);
        AddActionBtn(goldRow, CoopQolI18n.T("金币+100", "Gold +100"), () => AdjustGold(100));
        AddActionBtn(goldRow, CoopQolI18n.T("金币+500", "Gold +500"), () => AdjustGold(500));
        AddActionBtn(goldRow, CoopQolI18n.T("金币+999", "Gold +999"), () => AdjustGold(999));
        AddActionBtn(goldRow, CoopQolI18n.T("清零", "0 Gold"), SetGoldZero);

        // Right card: Combat resources & Hand
        var combatCard = CreateGroupCard(CoopQolI18n.T("战斗内即时资源与手牌", "Combat Resources & Hand"), grid);
        var resRow1 = CreateRow(combatCard);
        AddActionBtn(resRow1, CoopQolI18n.T("能量+3", "Energy +3"), () => RunConsoleCmd("energy 3", CoopQolI18n.T("能量 +3", "Energy +3")));
        AddActionBtn(resRow1, CoopQolI18n.T("能量+10", "Energy +10"), () => RunConsoleCmd("energy 10", CoopQolI18n.T("能量 +10", "Energy +10")));
        AddActionBtn(resRow1, CoopQolI18n.T("辉星+3", "Stars +3"), () => RunConsoleCmd("stars 3", CoopQolI18n.T("辉星 +3", "Stars +3")));
        AddActionBtn(resRow1, CoopQolI18n.T("辉星+10", "Stars +10"), () => RunConsoleCmd("stars 10", CoopQolI18n.T("辉星 +10", "Stars +10")));

        var resRow2 = CreateRow(combatCard);
        AddActionBtn(resRow2, CoopQolI18n.T("格挡+20", "Block +20"), () => AdjustBlockForSelected(20));
        AddActionBtn(resRow2, CoopQolI18n.T("格挡+99", "Block +99"), () => AdjustBlockForSelected(99));
        AddActionBtn(resRow2, CoopQolI18n.T("摸1张", "Draw 1"), () => RunConsoleCmd("draw 1", CoopQolI18n.T("已摸 1 张牌", "Drew 1 card")));
        AddActionBtn(resRow2, CoopQolI18n.T("摸5张", "Draw 5"), () => RunConsoleCmd("draw 5", CoopQolI18n.T("已摸 5 张牌", "Drew 5 cards")));

        var upgRow = CreateRow(combatCard);
        AddActionBtn(upgRow, CoopQolI18n.T("升级全部手牌", "Upgrade Hand"), UpgradeAllHand);
        AddActionBtn(upgRow, CoopQolI18n.T("升级全部牌组", "Upgrade Deck"), UpgradeAllDeck);

        // Bottom full-width card: God mode & Combat shortcuts
        var godCard = CreateGroupCard(CoopQolI18n.T("上帝开关与战斗快捷控制", "God Mode & Combat Shortcuts"), page);
        var godRow = CreateRow(godCard);
        _godToggleBtn = AddActionBtn(godRow, CoopQolI18n.T("原生上帝模式", "Native God Mode"), ToggleNativeGodMode, SpireButtonTone.GoldAccent, height: 40);
        AddActionBtn(godRow, CoopQolI18n.T("不死守护(无加攻)", "Survival Guard"), ApplySurvivalGuard, SpireButtonTone.Normal, height: 40);
        AddActionBtn(godRow, CoopQolI18n.T("净化负面状态", "Cleanse Debuffs"), ClearSelfDebuffs, SpireButtonTone.Normal, height: 40);
        AddActionBtn(godRow, CoopQolI18n.T("秒杀全敌获胜", "Win Combat"), WinCurrentCombat, SpireButtonTone.SlAccent, height: 40);
    }

    private static void BuildPowersPage(Control parent)
    {
        var page = new VBoxContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            Visible = false,
        };
        page.AddThemeConstantOverride("separation", 6);
        SubPages[1] = page;
        parent.AddChild(page);

        // Target selector row
        _powerTargetRow = new HBoxContainer();
        _powerTargetRow.AddThemeConstantOverride("separation", 5);
        page.AddChild(_powerTargetRow);

        // Stack amount stepper row
        var amtRow = new HBoxContainer();
        amtRow.AddThemeConstantOverride("separation", 4);
        page.AddChild(amtRow);

        var amtLbl = new Label { Text = CoopQolI18n.T("层数:", "Stacks:"), VerticalAlignment = VerticalAlignment.Center };
        NativeUiHelper.ApplyFont(amtLbl, 13, bold: true);
        amtLbl.AddThemeColorOverride("font_color", NativeUiHelper.SpireGold);
        amtRow.AddChild(amtLbl);

        for (int i = 0; i < PowerAmounts.Length; i++)
        {
            int val = PowerAmounts[i];
            var btn = new Button
            {
                Text = val > 0 ? $"+{val}" : val.ToString(),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 28),
            };
            NativeUiHelper.InitSpireButton(btn, 12);
            btn.Pressed += () =>
            {
                _powerStackAmount = val;
                RefreshPowerAmountButtons();
            };
            PowerAmountBtns[i] = btn;
            amtRow.AddChild(btn);
        }
        RefreshPowerAmountButtons();

        // 12 One-Click Common Buff/Debuff Chips (2 rows of 6)
        (string Id, string Label)[] presets =
        {
            ("STRENGTH", CoopQolI18n.T("力量", "Strength")),
            ("DEXTERITY", CoopQolI18n.T("敏捷", "Dexterity")),
            ("FOCUS", CoopQolI18n.T("集中", "Focus")),
            ("INTANGIBLE", CoopQolI18n.T("无实体", "Intangible")),
            ("BUFFER", CoopQolI18n.T("缓冲", "Buffer")),
            ("REGEN", CoopQolI18n.T("再生", "Regen")),
            ("PLATED_ARMOR", CoopQolI18n.T("多层护甲", "Plated Armor")),
            ("ARTIFACT", CoopQolI18n.T("人工制品", "Artifact")),
            ("THORNS", CoopQolI18n.T("荆棘", "Thorns")),
            ("VULNERABLE", CoopQolI18n.T("易伤", "Vulnerable")),
            ("WEAK", CoopQolI18n.T("虚弱", "Weak")),
            ("POISON", CoopQolI18n.T("中毒", "Poison")),
        };

        for (int r = 0; r < 2; r++)
        {
            var presetRow = new HBoxContainer();
            presetRow.AddThemeConstantOverride("separation", 5);
            page.AddChild(presetRow);
            for (int c = 0; c < 6; c++)
            {
                var p = presets[r * 6 + c];
                AddActionBtn(presetRow, p.Label, () => ApplyPowerById(p.Id, p.Label), height: 30);
            }
        }

        // Filter + Search row
        var filterRow = new HBoxContainer();
        filterRow.AddThemeConstantOverride("separation", 6);
        page.AddChild(filterRow);

        string[] fNames =
        {
            CoopQolI18n.T("全部状态", "All Powers"),
            CoopQolI18n.T("增益 Buff", "Buffs"),
            CoopQolI18n.T("负面 Debuff", "Debuffs"),
        };
        for (int i = 0; i < 3; i++)
        {
            int fm = i;
            var fBtn = new Button { Text = fNames[i], CustomMinimumSize = new Vector2(96, 32) };
            NativeUiHelper.InitSpireButton(fBtn, 12);
            fBtn.Pressed += () =>
            {
                _powerFilterMode = fm;
                RefreshPowerSearchResults();
            };
            PowerFilterBtns[i] = fBtn;
            filterRow.AddChild(fBtn);
        }

        _powerSearchEdit = CreateSearchEdit(
            CoopQolI18n.T("搜索全部状态/Buff (中文名或英文ID)...", "Search all Powers/Buffs (Name or ID)..."),
            _ => RefreshPowerSearchResults());
        filterRow.AddChild(_powerSearchEdit);

        var clearDebuffBtn = new Button { Text = CoopQolI18n.T("清负面", "Cleanse"), CustomMinimumSize = new Vector2(82, 32) };
        NativeUiHelper.InitSpireButton(clearDebuffBtn, 12, tone: SpireButtonTone.SlAccent);
        clearDebuffBtn.Pressed += ClearSelfDebuffs;
        filterRow.AddChild(clearDebuffBtn);

        BuildPooledList(page, PowerPoolRows, PowerPoolActions);
    }

    private static void BuildCardsPage(Control parent)
    {
        var page = new VBoxContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            Visible = false,
        };
        page.AddThemeConstantOverride("separation", 6);
        SubPages[2] = page;
        parent.AddChild(page);

        var topRow = new HBoxContainer();
        topRow.AddThemeConstantOverride("separation", 6);
        page.AddChild(topRow);

        _cardModeLibBtn = new Button { Text = CoopQolI18n.T("获取全库卡牌", "Card Library"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 32) };
        NativeUiHelper.InitSpireButton(_cardModeLibBtn, 13);
        _cardModeLibBtn.Pressed += () => { _cardMode = 0; RefreshCardsView(); };
        topRow.AddChild(_cardModeLibBtn);

        _cardModeDeckBtn = new Button { Text = CoopQolI18n.T("管理当前手牌/牌组", "Manage Hand / Deck"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 32) };
        NativeUiHelper.InitSpireButton(_cardModeDeckBtn, 13);
        _cardModeDeckBtn.Pressed += () => { _cardMode = 1; RefreshCardsView(); };
        topRow.AddChild(_cardModeDeckBtn);

        _cardUpgradeCheckBtn = new Button { Text = CoopQolI18n.T("升级版:关", "Upgrade: OFF"), CustomMinimumSize = new Vector2(114, 32) };
        NativeUiHelper.InitSpireButton(_cardUpgradeCheckBtn, 12);
        _cardUpgradeCheckBtn.Pressed += () => { _cardUpgraded = !_cardUpgraded; RefreshCardsView(); };
        topRow.AddChild(_cardUpgradeCheckBtn);

        _cardDestHandBtn = new Button { Text = CoopQolI18n.T("加入手牌", "To Hand"), CustomMinimumSize = new Vector2(96, 32) };
        NativeUiHelper.InitSpireButton(_cardDestHandBtn, 12);
        _cardDestHandBtn.Pressed += () => { _cardToHand = true; RefreshCardsView(); };
        topRow.AddChild(_cardDestHandBtn);

        _cardDestDeckBtn = new Button { Text = CoopQolI18n.T("加入牌组", "To Deck"), CustomMinimumSize = new Vector2(96, 32) };
        NativeUiHelper.InitSpireButton(_cardDestDeckBtn, 12);
        _cardDestDeckBtn.Pressed += () => { _cardToHand = false; RefreshCardsView(); };
        topRow.AddChild(_cardDestDeckBtn);

        var filterRow = new HBoxContainer();
        filterRow.AddThemeConstantOverride("separation", 5);
        page.AddChild(filterRow);

        string[] fNames =
        {
            CoopQolI18n.T("全部", "All"),
            CoopQolI18n.T("当前角色", "Class"),
            CoopQolI18n.T("攻击", "Attack"),
            CoopQolI18n.T("技能", "Skill"),
            CoopQolI18n.T("能力", "Power"),
            CoopQolI18n.T("诅咒/状态", "Curse/Status"),
        };
        for (int i = 0; i < fNames.Length; i++)
        {
            int fi = i;
            var btn = new Button { Text = fNames[i], CustomMinimumSize = new Vector2(74, 30) };
            NativeUiHelper.InitSpireButton(btn, 12);
            btn.Pressed += () => { _cardFilter = fi; RefreshCardsView(); };
            CardFilterBtns[i] = btn;
            filterRow.AddChild(btn);
        }

        _cardSearchEdit = CreateSearchEdit(
            CoopQolI18n.T("搜索卡牌中文名 / 描述 / ID...", "Search cards (Name / Desc / ID)..."),
            _ => RefreshCardsView());
        filterRow.AddChild(_cardSearchEdit);

        BuildPooledList(page, CardPoolRows, CardPoolActions);
    }

    private static void BuildItemsPage(Control parent)
    {
        var page = new VBoxContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            Visible = false,
        };
        page.AddThemeConstantOverride("separation", 6);
        SubPages[3] = page;
        parent.AddChild(page);

        var topRow = new HBoxContainer();
        topRow.AddThemeConstantOverride("separation", 6);
        page.AddChild(topRow);

        _itemModeRelicBtn = new Button { Text = CoopQolI18n.T("获取遗物", "Spawn Relics"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 32) };
        NativeUiHelper.InitSpireButton(_itemModeRelicBtn, 13);
        _itemModeRelicBtn.Pressed += () => { _itemMode = 0; RefreshItemsView(); };
        topRow.AddChild(_itemModeRelicBtn);

        _itemModePotionBtn = new Button { Text = CoopQolI18n.T("获取药水", "Spawn Potions"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 32) };
        NativeUiHelper.InitSpireButton(_itemModePotionBtn, 13);
        _itemModePotionBtn.Pressed += () => { _itemMode = 1; RefreshItemsView(); };
        topRow.AddChild(_itemModePotionBtn);

        _itemModeOwnedRelicBtn = new Button { Text = CoopQolI18n.T("已拥有遗物(移除)", "Owned Relics (Remove)"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 32) };
        NativeUiHelper.InitSpireButton(_itemModeOwnedRelicBtn, 13);
        _itemModeOwnedRelicBtn.Pressed += () => { _itemMode = 2; RefreshItemsView(); };
        topRow.AddChild(_itemModeOwnedRelicBtn);

        _potionSlotPlusBtn = new Button { Text = CoopQolI18n.T("药水栏 +1", "Potion Slot +1"), CustomMinimumSize = new Vector2(118, 32) };
        NativeUiHelper.InitSpireButton(_potionSlotPlusBtn, 12, tone: SpireButtonTone.GoldAccent);
        _potionSlotPlusBtn.Pressed += GainPotionSlot;
        topRow.AddChild(_potionSlotPlusBtn);

        var filterRow = new HBoxContainer();
        filterRow.AddThemeConstantOverride("separation", 5);
        page.AddChild(filterRow);

        string[] rNames =
        {
            CoopQolI18n.T("全部稀有度", "All Rarities"),
            CoopQolI18n.T("普通", "Common"),
            CoopQolI18n.T("罕见", "Uncommon"),
            CoopQolI18n.T("稀有", "Rare"),
            CoopQolI18n.T("首领/商店/事件", "Special"),
        };
        for (int i = 0; i < rNames.Length; i++)
        {
            int ri = i;
            var btn = new Button { Text = rNames[i], CustomMinimumSize = new Vector2(82, 30) };
            NativeUiHelper.InitSpireButton(btn, 12);
            btn.Pressed += () => { _itemRarityFilter = ri; RefreshItemsView(); };
            ItemRarityBtns[i] = btn;
            filterRow.AddChild(btn);
        }

        _itemSearchEdit = CreateSearchEdit(
            CoopQolI18n.T("搜索遗物或药水中文名 / 效果 / ID...", "Search Relics / Potions (Name / Effect / ID)..."),
            _ => RefreshItemsView());
        filterRow.AddChild(_itemSearchEdit);

        BuildPooledList(page, ItemPoolRows, ItemPoolActions);
    }

    private static void BuildPooledList(Control parent, Button[] rowButtons, Action[] rowActions)
    {
        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        parent.AddChild(scroll);

        var listBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        listBox.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(listBox);

        for (int i = 0; i < MaxPooledRows; i++)
        {
            int idx = i;
            var btn = new Button
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 32),
                ClipText = true,
                Visible = false,
            };
            NativeUiHelper.InitSpireButton(btn, 13, tone: SpireButtonTone.ListItem, bold: false);
            btn.Pressed += () => rowActions[idx]?.Invoke();
            rowButtons[i] = btn;
            listBox.AddChild(btn);
        }
    }

    private static LineEdit CreateSearchEdit(string placeholder, Action<string> onChanged)
    {
        var edit = NativeUiHelper.CreateSpireLineEdit(placeholder, 13);
        edit.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        edit.CustomMinimumSize = new Vector2(180, 32);
        edit.FocusEntered += () => SetHotkeyBlocking(true);
        edit.FocusExited += () => SetHotkeyBlocking(edit.HasFocus() || edit.IsEditing());
        edit.EditingToggled += _ => SetHotkeyBlocking(edit.HasFocus() || edit.IsEditing());
        edit.TextChanged += text => onChanged(text);
        return edit;
    }

    private static VBoxContainer CreateGroupCard(string title, Control parent)
    {
        var panel = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        panel.AddThemeStyleboxOverride("panel", NativeUiHelper.GetSubCardStyle());
        parent.AddChild(panel);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 7);
        panel.AddChild(vbox);

        var titleLbl = new Label
        {
            Text = title,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        NativeUiHelper.ApplyFont(titleLbl, 14, bold: true, outlineSize: 3);
        titleLbl.AddThemeColorOverride("font_color", NativeUiHelper.SpireGold);
        vbox.AddChild(titleLbl);

        var sep = new HSeparator();
        vbox.AddChild(sep);

        return vbox;
    }

    private static HBoxContainer CreateRow(Control parent)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        parent.AddChild(row);
        return row;
    }

    private static Button AddActionBtn(
        Control parent,
        string text,
        Action onClick,
        SpireButtonTone tone = SpireButtonTone.Normal,
        int height = 34)
    {
        var btn = new Button
        {
            Text = text,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, height),
        };
        NativeUiHelper.InitSpireButton(btn, 13, tone: tone, bold: true);
        btn.Pressed += onClick;
        parent.AddChild(btn);
        return btn;
    }

    private static void SwitchSubTab(int idx)
    {
        _activeSubTab = idx;
        for (int i = 0; i < 4; i++)
        {
            NativeUiHelper.SetSpireTabActive(SubTabBtns[i], i == idx);
            SubPages[i].Visible = i == idx;
        }
        RefreshAllActiveViews();
    }

    private static void RefreshAllActiveViews()
    {
        var netType = RunManager.Instance?.NetService?.Type ?? NetGameType.Singleplayer;
        bool isSingle = RunManager.Instance?.IsSingleplayerOrFakeMultiplayer ?? true;

        _modeBadgeLabel.Text = isSingle
            ? CoopQolI18n.T("单人模式（全功能可用）", "Singleplayer (All features)")
            : (netType == NetGameType.Host
                ? CoopQolI18n.T("联机房主（可切换修改任意成员）", "Co-op Host (Can edit any ally)")
                : CoopQolI18n.T("联机客机（仅可修改自身）", "Co-op Client (Self only)"));
        _modeBadgeLabel.AddThemeColorOverride(
            "font_color",
            isSingle ? new Color(0.62f, 0.90f, 0.70f) : new Color(0.49f, 0.91f, 1.0f));

        RefreshMemberSelector();

        if (_activeSubTab == 0) RefreshStatsStrip();
        else if (_activeSubTab == 1)
        {
            RefreshPowerTargets();
            RefreshPowerSearchResults();
        }
        else if (_activeSubTab == 2) RefreshCardsView();
        else if (_activeSubTab == 3) RefreshItemsView();
    }

    private static void RefreshMemberSelector()
    {
        if (_memberSelectorRow == null) return;

        foreach (Node child in _memberSelectorRow.GetChildren())
            child.QueueFree();
        MemberBtns.Clear();

        var titleLbl = NativeUiHelper.CreateMegaLabel(
            CoopQolI18n.T("操作对象:", "Target:"),
            13, NativeUiHelper.SpireGold, HorizontalAlignment.Left, outlineSize: 3);
        titleLbl.CustomMinimumSize = new Vector2(68, 28);
        _memberSelectorRow.AddChild(titleLbl);

        var runState = PlayerNameHelper.GetRunState();
        var local = GetLocalPlayer();
        if (runState == null || runState.Players.Count == 0)
        {
            var emptyLbl = NativeUiHelper.CreateMegaLabel(
                CoopQolI18n.T("未检测到角色", "No players found"),
                13, NativeUiHelper.SpireMutedText, HorizontalAlignment.Left, outlineSize: 2);
            _memberSelectorRow.AddChild(emptyLbl);
            return;
        }

        if (_selectedPlayerNetId == 0 || runState.Players.All(p => p.NetId != _selectedPlayerNetId))
            _selectedPlayerNetId = local?.NetId ?? runState.Players[0].NetId;

        var netType = RunManager.Instance?.NetService?.Type ?? NetGameType.Singleplayer;
        bool canSelectOthers = netType == NetGameType.Host || IsSingleplayer();
        if (!canSelectOthers && local != null)
            _selectedPlayerNetId = local.NetId;

        for (int i = 0; i < runState.Players.Count; i++)
        {
            var p = runState.Players[i];
            if (!canSelectOthers && !LocalContext.IsMe(p))
                continue;

            bool isMe = LocalContext.IsMe(p);
            string pName = PlayerNameHelper.GetPlayerName(p);
            if (pName.Length > 8) pName = pName.Substring(0, 8);
            string tag = isMe ? CoopQolI18n.T("我", "Me") : CoopQolI18n.T("队友", "Ally");

            var btn = new Button
            {
                Text = CoopQolI18n.IsZh
                    ? $"{tag}: {pName} ({p.Creature.CurrentHp}/{p.Creature.MaxHp}血)"
                    : $"{tag}: {pName} ({p.Creature.CurrentHp}/{p.Creature.MaxHp} HP)",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 30),
            };
            NativeUiHelper.InitSpireButton(btn, 12);
            NativeUiHelper.SetSpireButtonActive(btn, p.NetId == _selectedPlayerNetId);

            var capturedNetId = p.NetId;
            btn.Pressed += () =>
            {
                _selectedPlayerNetId = capturedNetId;
                RefreshAllActiveViews();
            };
            MemberBtns.Add(btn);
            _memberSelectorRow.AddChild(btn);
        }
    }

    private static void RefreshStatsStrip()
    {
        var target = GetSelectedPlayer();
        if (target == null)
        {
            _liveStatsLabel.Text = CoopQolI18n.T("未检测到当前角色状态", "No character state detected");
            return;
        }

        var c = target.Creature;
        int energy = target.PlayerCombatState?.Energy ?? 0;
        int maxEnergy = target.PlayerCombatState?.MaxEnergy ?? 0;
        int handCount = PileType.Hand.GetPile(target)?.Cards.Count ?? 0;
        int deckCount = PileType.Deck.GetPile(target)?.Cards.Count ?? 0;
        string targetName = PlayerNameHelper.GetPlayerName(target);

        _liveStatsLabel.Text = CoopQolI18n.IsZh
            ? $"对象: {targetName}   生命: {c.CurrentHp}/{c.MaxHp}   金币: {target.Gold}   能量: {energy}/{maxEnergy}   格挡: {c.Block}   手牌/牌组: {handCount}/{deckCount}"
            : $"Target: {targetName}   HP: {c.CurrentHp}/{c.MaxHp}   Gold: {target.Gold}   Energy: {energy}/{maxEnergy}   Block: {c.Block}   Hand/Deck: {handCount}/{deckCount}";

        NativeUiHelper.SetSpireButtonActive(_godToggleBtn, _godModeActive);
        NativeUiHelper.SetButtonText(_godToggleBtn, _godModeActive
            ? CoopQolI18n.T("原生上帝模式(开)", "Native God Mode (ON)")
            : CoopQolI18n.T("原生上帝模式", "Native God Mode"));
    }

    // --- Stats & Combat Actions ---

    private static void AdjustHp(int delta)
    {
        var target = GetSelectedPlayer();
        if (target == null) return;

        if (delta > 0)
        {
            string pName = PlayerNameHelper.GetPlayerName(target);
            if (IsSingleplayer() && LocalContext.IsMe(target))
            {
                TaskHelper.RunSafely(CreatureCmd.Heal(target.Creature, delta, true));
                SetStatus(CoopQolI18n.IsZh ? $"已为 {pName} 恢复 {delta} 点生命。" : $"Healed {pName} for {delta} HP.");
                ScheduleRefreshStats();
            }
            else
            {
                string cmd = BuildHealCmdForPlayer(target, delta);
                DispatchPlayerConsoleCmd(target, cmd, CoopQolI18n.IsZh ? $"已为 {pName} 恢复 {delta} 点生命" : $"Healed {pName} for {delta} HP");
            }
            return;
        }

        if (!RequireSingleplayer(CoopQolI18n.T("扣减生命", "Reduce HP"))) return;
        int targetHp = Math.Max(1, target.Creature.CurrentHp + delta);
        TaskHelper.RunSafely(CreatureCmd.SetCurrentHp(target.Creature, targetHp));
        SetStatus(CoopQolI18n.IsZh
            ? $"{PlayerNameHelper.GetPlayerName(target)} 生命已设为 {targetHp}/{target.Creature.MaxHp}。"
            : $"{PlayerNameHelper.GetPlayerName(target)} HP set to {targetHp}/{target.Creature.MaxHp}.");
        ScheduleRefreshStats();
    }

    private static void HealFull()
    {
        var target = GetSelectedPlayer();
        if (target == null) return;
        int missing = Math.Max(0, target.Creature.MaxHp - target.Creature.CurrentHp);
        string pName = PlayerNameHelper.GetPlayerName(target);
        if (missing == 0)
        {
            SetStatus(CoopQolI18n.IsZh ? $"{pName} 当前已是满血状态。" : $"{pName} is already at full HP.");
            return;
        }
        if (IsSingleplayer() && LocalContext.IsMe(target))
        {
            TaskHelper.RunSafely(CreatureCmd.Heal(target.Creature, missing, true));
            SetStatus(CoopQolI18n.IsZh ? $"已为 {pName} 回满生命 (+{missing})。" : $"Healed {pName} to full (+{missing} HP).");
            ScheduleRefreshStats();
        }
        else
        {
            string cmd = BuildHealCmdForPlayer(target, missing);
            DispatchPlayerConsoleCmd(target, cmd, CoopQolI18n.IsZh ? $"已为 {pName} 回满生命 (+{missing})" : $"Healed {pName} to full (+{missing} HP)");
        }
    }

    private static string BuildHealCmdForPlayer(Player target, int amount)
    {
        if (CombatManager.Instance?.IsInProgress == true)
        {
            var combat = CombatManager.Instance.DebugOnlyGetState();
            if (combat != null)
            {
                for (int i = 0; i < combat.Allies.Count; i++)
                {
                    if (ReferenceEquals(combat.Allies[i], target.Creature))
                        return $"heal {amount} {i}";
                }
            }
        }
        return $"heal {amount}";
    }

    private static void AdjustMaxHp(int delta)
    {
        if (!RequireSingleplayer(CoopQolI18n.T("修改最大生命", "Edit Max HP"))) return;
        var target = GetSelectedPlayer();
        if (target == null) return;

        if (delta > 0)
        {
            TaskHelper.RunSafely(GainMaxHpAndHealAsync(target.Creature, delta));
            SetStatus(CoopQolI18n.IsZh
                ? $"{PlayerNameHelper.GetPlayerName(target)} 最大生命 +{delta}。"
                : $"{PlayerNameHelper.GetPlayerName(target)} Max HP +{delta}.");
        }
        else
        {
            int nextMax = Math.Max(1, target.Creature.MaxHp + delta);
            TaskHelper.RunSafely(CreatureCmd.SetMaxHp(target.Creature, nextMax));
            SetStatus(CoopQolI18n.IsZh
                ? $"{PlayerNameHelper.GetPlayerName(target)} 最大生命已设为 {nextMax}。"
                : $"{PlayerNameHelper.GetPlayerName(target)} Max HP set to {nextMax}.");
        }
        ScheduleRefreshStats();
    }

    private static async Task GainMaxHpAndHealAsync(Creature c, int amount)
    {
        await CreatureCmd.GainMaxHp(c, amount);
        await CreatureCmd.Heal(c, amount, false);
    }

    private static void AdjustGold(int delta)
    {
        var target = GetSelectedPlayer();
        if (target == null) return;
        DispatchPlayerConsoleCmd(target, $"gold {delta}", CoopQolI18n.IsZh
            ? $"{PlayerNameHelper.GetPlayerName(target)} 金币 +{delta}"
            : $"{PlayerNameHelper.GetPlayerName(target)} Gold +{delta}");
    }

    private static void SetGoldZero()
    {
        if (!RequireSingleplayer(CoopQolI18n.T("清零金币", "Zero Gold"))) return;
        var target = GetSelectedPlayer();
        if (target == null) return;
        TaskHelper.RunSafely(PlayerCmd.SetGold(0, target));
        SetStatus(CoopQolI18n.IsZh
            ? $"{PlayerNameHelper.GetPlayerName(target)} 金币已清零。"
            : $"{PlayerNameHelper.GetPlayerName(target)} Gold set to 0.");
        ScheduleRefreshStats();
    }

    private static void AdjustBlockForSelected(int amount)
    {
        if (!RequireInCombat(CoopQolI18n.T("增加格挡", "Gain Block"))) return;
        var target = GetSelectedPlayer();
        var combat = CombatManager.Instance.DebugOnlyGetState();
        if (target == null || combat == null) return;

        int idx = -1;
        for (int i = 0; i < combat.Creatures.Count; i++)
        {
            if (ReferenceEquals(combat.Creatures[i], target.Creature)) { idx = i; break; }
        }
        string cmd = idx >= 0 ? $"block {amount} {idx}" : $"block {amount}";
        DispatchPlayerConsoleCmd(target, cmd, CoopQolI18n.IsZh
            ? $"{PlayerNameHelper.GetPlayerName(target)} 格挡 +{amount}"
            : $"{PlayerNameHelper.GetPlayerName(target)} Block +{amount}");
    }

    private static void ToggleNativeGodMode()
    {
        var target = GetSelectedPlayer();
        if (target == null) return;
        string pName = PlayerNameHelper.GetPlayerName(target);
        string msg = _godModeActive
            ? CoopQolI18n.T("已切换关闭原生上帝模式", "Native God Mode toggled OFF")
            : (CoopQolI18n.IsZh ? $"已为 {pName} 开启原生上帝模式" : $"Native God Mode enabled for {pName}");
        if (DispatchPlayerConsoleCmd(target, "godmode", msg))
        {
            _godModeActive = !_godModeActive;
            RefreshStatsStrip();
        }
    }

    private static void ApplySurvivalGuard()
    {
        if (!RequireInCombat(CoopQolI18n.T("纯生存守护", "Survival Guard"))) return;
        var target = GetSelectedPlayer();
        var combat = CombatManager.Instance.DebugOnlyGetState();
        if (target == null || combat == null) return;

        int idx = 0;
        for (int i = 0; i < combat.Creatures.Count; i++)
        {
            if (ReferenceEquals(combat.Creatures[i], target.Creature)) { idx = i; break; }
        }
        string pName = PlayerNameHelper.GetPlayerName(target);
        DispatchPlayerConsoleCmd(target, $"power BUFFER 99 {idx}", CoopQolI18n.IsZh ? $"已为 {pName} 施加 +99 缓冲" : $"Applied +99 Buffer to {pName}");
        DispatchPlayerConsoleCmd(target, $"power REGEN 99 {idx}", CoopQolI18n.IsZh ? $"已为 {pName} 施加 +99 缓冲 与 +99 再生" : $"Applied +99 Buffer & +99 Regen to {pName}");
    }

    private static void WinCurrentCombat()
    {
        if (!RequireInCombat(CoopQolI18n.T("秒杀全敌", "Win Combat"))) return;
        var target = GetSelectedPlayer() ?? GetLocalPlayer();
        if (target == null) return;
        DispatchPlayerConsoleCmd(target, "win", CoopQolI18n.T("已秒杀全场敌人，战斗胜利！", "All enemies defeated — Combat won!"));
    }

    private static void UpgradeAllHand()
    {
        var target = GetSelectedPlayer();
        if (target == null) return;
        var hand = PileType.Hand.GetPile(target)?.Cards;
        if (hand == null || hand.Count == 0)
        {
            SetStatus(CoopQolI18n.IsZh
                ? $"{PlayerNameHelper.GetPlayerName(target)} 当前手牌为空。"
                : $"{PlayerNameHelper.GetPlayerName(target)} hand is empty.", isError: true);
            return;
        }

        if (IsSingleplayer() && LocalContext.IsMe(target))
        {
            var upgradable = hand.Where(c => c.CurrentUpgradeLevel < c.MaxUpgradeLevel).ToList();
            if (upgradable.Count == 0)
            {
                SetStatus(CoopQolI18n.T("手牌已全部处于最高升级状态。", "All cards in hand are already upgraded."));
                return;
            }
            CardCmd.Upgrade(upgradable, CardPreviewStyle.HorizontalLayout);
            SetStatus(CoopQolI18n.IsZh ? $"已升级 {upgradable.Count} 张手牌！" : $"Upgraded {upgradable.Count} hand card(s)!");
        }
        else
        {
            int count = 0;
            for (int i = 0; i < hand.Count; i++)
            {
                if (hand[i].CurrentUpgradeLevel < hand[i].MaxUpgradeLevel)
                {
                    DispatchPlayerConsoleCmd(target, $"upgradecard {i}", string.Empty);
                    count++;
                }
            }
            SetStatus(CoopQolI18n.IsZh
                ? $"已为 {PlayerNameHelper.GetPlayerName(target)} 发送升级 {count} 张手牌指令。"
                : $"Sent upgrade command for {count} hand card(s) of {PlayerNameHelper.GetPlayerName(target)}.");
        }
        ScheduleRefreshStats();
    }

    private static void UpgradeAllDeck()
    {
        if (!RequireSingleplayer(CoopQolI18n.T("升级全部牌组", "Upgrade Deck"))) return;
        var target = GetSelectedPlayer();
        if (target == null) return;
        var deck = PileType.Deck.GetPile(target)?.Cards;
        if (deck == null || deck.Count == 0) return;

        var upgradable = deck.Where(c => c.CurrentUpgradeLevel < c.MaxUpgradeLevel).ToList();
        if (upgradable.Count == 0)
        {
            SetStatus(CoopQolI18n.T("牌组内没有可继续升级的卡牌。", "No upgradable cards in deck."));
            return;
        }
        CardCmd.Upgrade(upgradable, CardPreviewStyle.GridLayout);
        SetStatus(CoopQolI18n.IsZh ? $"已升级牌组内 {upgradable.Count} 张卡牌！" : $"Upgraded {upgradable.Count} deck card(s)!");
    }

    private static void ClearSelfDebuffs()
    {
        if (!RequireInCombat(CoopQolI18n.T("净化负面", "Cleanse Debuffs"))) return;
        if (!RequireSingleplayer(CoopQolI18n.T("一键净化", "Cleanse Debuffs"))) return;
        var target = GetSelectedPlayer();
        if (target == null) return;

        var debuffs = target.Creature.Powers.Where(p => p.TypeForCurrentAmount == PowerType.Debuff).ToList();
        if (debuffs.Count == 0)
        {
            SetStatus(CoopQolI18n.IsZh
                ? $"{PlayerNameHelper.GetPlayerName(target)} 当前没有负面状态。"
                : $"{PlayerNameHelper.GetPlayerName(target)} has no debuffs.");
            return;
        }
        TaskHelper.RunSafely(RemovePowersAsync(debuffs));
        SetStatus(CoopQolI18n.IsZh ? $"已净化 {debuffs.Count} 个负面状态。" : $"Cleansed {debuffs.Count} debuff(s).");
    }

    private static async Task RemovePowersAsync(List<PowerModel> powers)
    {
        foreach (var p in powers)
            await PowerCmd.Remove(p);
    }

    // --- Powers / Buffs ---

    private static void RefreshPowerTargets()
    {
        foreach (var b in PowerTargetBtns)
        {
            if (GodotObject.IsInstanceValid(b))
                b.QueueFree();
        }
        PowerTargetBtns.Clear();

        void AddTargetBtn(string label, int mode)
        {
            var btn = new Button
            {
                Text = label,
                CustomMinimumSize = new Vector2(0, 30),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            NativeUiHelper.InitSpireButton(btn, 12);
            NativeUiHelper.SetSpireButtonActive(btn, _powerTargetMode == mode);
            btn.Pressed += () =>
            {
                _powerTargetMode = mode;
                RefreshPowerTargets();
            };
            PowerTargetBtns.Add(btn);
            _powerTargetRow.AddChild(btn);
        }

        var combat = CombatManager.Instance?.IsInProgress == true ? CombatManager.Instance.DebugOnlyGetState() : null;
        if (combat != null)
        {
            var creatures = combat.Creatures;
            for (int i = 0; i < creatures.Count; i++)
            {
                var c = creatures[i];
                if (!c.IsAlive || !c.IsPlayer) continue;
                string name = c.Player != null ? PlayerNameHelper.GetPlayerName(c.Player) : CoopQolI18n.T("队友", "Ally");
                if (name.Length > 7) name = name.Substring(0, 7);
                string tag = c.Player != null && LocalContext.IsMe(c.Player)
                    ? CoopQolI18n.T("我", "Me")
                    : CoopQolI18n.T("友", "Ally");
                AddTargetBtn($"{tag}:{name}", 2 + i);
            }

            AddTargetBtn(CoopQolI18n.T("敌方全体", "All Enemies"), 1);

            for (int i = 0; i < creatures.Count; i++)
            {
                var c = creatures[i];
                if (!c.IsAlive || c.IsPlayer) continue;
                string name = c.Monster?.Title?.GetFormattedText() ?? c.Monster?.Id.Entry ?? (CoopQolI18n.IsZh ? $"敌人{i}" : $"Enemy{i}");
                if (name.Length > 7) name = name.Substring(0, 7);
                AddTargetBtn($"{name}({c.CurrentHp})", 2 + i);
            }
        }
        else
        {
            AddTargetBtn(CoopQolI18n.T("自己", "Self"), 0);
            AddTargetBtn(CoopQolI18n.T("敌方全体", "All Enemies"), 1);
        }
    }

    private static void RefreshPowerAmountButtons()
    {
        for (int i = 0; i < PowerAmounts.Length; i++)
            NativeUiHelper.SetSpireButtonActive(PowerAmountBtns[i], PowerAmounts[i] == _powerStackAmount);
    }

    private static void RefreshPowerSearchResults()
    {
        for (int i = 0; i < 3; i++)
            NativeUiHelper.SetSpireButtonActive(PowerFilterBtns[i], _powerFilterMode == i);

        var all = _cachedAllPowers ??= BuildAllPowersCache();
        string q = (_powerSearchEdit?.Text ?? string.Empty).Trim();

        int row = 0;
        foreach (var p in all)
        {
            if (_powerFilterMode == 1 && p.Type != PowerType.Buff) continue;
            if (_powerFilterMode == 2 && p.Type != PowerType.Debuff) continue;

            string title = SafeLocText(p.Title, p.Id.Entry);
            string desc = CleanText(SafeLocText(p.Description, string.Empty), 42);
            if (!string.IsNullOrEmpty(q)
                && !title.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !p.Id.Entry.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !desc.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string tag = p.Type == PowerType.Debuff
                ? CoopQolI18n.T("[负面]", "[Debuff]")
                : CoopQolI18n.T("[增益]", "[Buff]");
            var btn = PowerPoolRows[row];
            NativeUiHelper.SetButtonText(btn, $"{tag} {title} ({p.Id.Entry}) — {desc}");
            btn.Visible = true;
            var captured = p;
            PowerPoolActions[row] = () => ApplyPowerModel(captured, title);
            row++;
            if (row >= MaxPooledRows) break;
        }

        for (int i = row; i < MaxPooledRows; i++)
            PowerPoolRows[i].Visible = false;
    }

    private static void ApplyPowerById(string idEntry, string displayName)
    {
        var all = _cachedAllPowers ??= BuildAllPowersCache();
        var model = all.FirstOrDefault(p => string.Equals(p.Id.Entry, idEntry, StringComparison.OrdinalIgnoreCase));
        if (model == null)
        {
            RunConsoleCmd($"power {idEntry} {_powerStackAmount} 0", CoopQolI18n.IsZh
                ? $"施加 {displayName} {_powerStackAmount:+#;-#;0}"
                : $"Applied {displayName} {_powerStackAmount:+#;-#;0}");
            return;
        }
        ApplyPowerModel(model, displayName);
    }

    private static void ApplyPowerModel(PowerModel powerModel, string displayName)
    {
        if (!RequireInCombat(CoopQolI18n.T("施加状态", "Apply Power"))) return;
        var combat = CombatManager.Instance.DebugOnlyGetState();
        var me = GetSelectedPlayer();
        if (combat == null || me == null) return;

        var targets = new List<Creature>();
        if (_powerTargetMode == 0)
        {
            targets.Add(me.Creature);
        }
        else if (_powerTargetMode == 1)
        {
            foreach (var e in combat.Enemies)
                if (e.IsAlive) targets.Add(e);
        }
        else
        {
            int cIdx = _powerTargetMode - 2;
            if (cIdx >= 0 && cIdx < combat.Creatures.Count && combat.Creatures[cIdx].IsAlive)
                targets.Add(combat.Creatures[cIdx]);
        }

        if (targets.Count == 0)
        {
            SetStatus(CoopQolI18n.T("当前没有有效目标。", "No valid targets."), isError: true);
            return;
        }

        if (IsSingleplayer())
        {
            TaskHelper.RunSafely(ApplyPowerSingleplayerAsync(powerModel, targets, me.Creature, _powerStackAmount));
            SetStatus(CoopQolI18n.IsZh
                ? $"已对 {targets.Count} 个目标施加 [{displayName}] {(_powerStackAmount > 0 ? "+" : "")}{_powerStackAmount}。"
                : $"Applied [{displayName}] {(_powerStackAmount > 0 ? "+" : "")}{_powerStackAmount} to {targets.Count} target(s).");
        }
        else
        {
            foreach (var t in targets)
            {
                int idx = -1;
                for (int i = 0; i < combat.Creatures.Count; i++)
                {
                    if (ReferenceEquals(combat.Creatures[i], t)) { idx = i; break; }
                }
                if (idx >= 0)
                    Console.ProcessCommand($"power {powerModel.Id.Entry} {_powerStackAmount} {idx}");
            }
            SetStatus(CoopQolI18n.IsZh
                ? $"已向联机队列发送 [{displayName}] {(_powerStackAmount > 0 ? "+" : "")}{_powerStackAmount}。"
                : $"Queued [{displayName}] {(_powerStackAmount > 0 ? "+" : "")}{_powerStackAmount} for multiplayer.");
        }
    }

    private static async Task ApplyPowerSingleplayerAsync(
        PowerModel baseModel,
        List<Creature> targets,
        Creature applier,
        int amount)
    {
        var ctx = new BlockingPlayerChoiceContext();
        foreach (var target in targets)
        {
            var existing = target.Powers.FirstOrDefault(p => p.GetType() == baseModel.GetType());
            if (existing != null)
            {
                await PowerCmd.ModifyAmount(ctx, existing, amount, applier, null, silent: false);
            }
            else if (amount != 0)
            {
                await PowerCmd.Apply(ctx, baseModel.ToMutable(amount), target, amount, applier, null, silent: false);
            }
        }
    }

    // --- Cards ---

    private static void RefreshCardsView()
    {
        NativeUiHelper.SetSpireButtonActive(_cardModeLibBtn, _cardMode == 0);
        NativeUiHelper.SetSpireButtonActive(_cardModeDeckBtn, _cardMode == 1);
        NativeUiHelper.SetSpireButtonActive(_cardUpgradeCheckBtn, _cardUpgraded);
        NativeUiHelper.SetButtonText(_cardUpgradeCheckBtn, _cardUpgraded
            ? CoopQolI18n.T("升级版:开", "Upgrade: ON")
            : CoopQolI18n.T("升级版:关", "Upgrade: OFF"));
        _cardUpgradeCheckBtn.Visible = _cardMode == 0;

        NativeUiHelper.SetSpireButtonActive(_cardDestHandBtn, _cardToHand);
        NativeUiHelper.SetSpireButtonActive(_cardDestDeckBtn, !_cardToHand);
        NativeUiHelper.SetButtonText(_cardDestHandBtn, _cardMode == 0
            ? CoopQolI18n.T("加入手牌", "To Hand")
            : CoopQolI18n.T("当前手牌", "In Hand"));
        NativeUiHelper.SetButtonText(_cardDestDeckBtn, _cardMode == 0
            ? CoopQolI18n.T("加入牌组", "To Deck")
            : CoopQolI18n.T("当前牌组", "In Deck"));

        for (int i = 0; i < CardFilterBtns.Length; i++)
            NativeUiHelper.SetSpireButtonActive(CardFilterBtns[i], _cardFilter == i);

        var me = GetSelectedPlayer();
        string q = (_cardSearchEdit?.Text ?? string.Empty).Trim();
        int row = 0;

        if (_cardMode == 0)
        {
            var all = _cachedAllCards ??= ModelDb.AllCards.OrderBy(c => c.Title ?? c.Id.Entry, StringComparer.CurrentCulture).ToList();
            var myPoolId = me?.Character?.CardPool?.Id;

            foreach (var card in all)
            {
                if (!MatchesCardFilter(card, myPoolId)) continue;

                string title = card.Title ?? card.Id.Entry;
                string desc = CleanText(SafeLocText(card.Description, string.Empty), 40);
                if (!string.IsNullOrEmpty(q)
                    && !title.Contains(q, StringComparison.OrdinalIgnoreCase)
                    && !card.Id.Entry.Contains(q, StringComparison.OrdinalIgnoreCase)
                    && !desc.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string cost = card.EnergyCost == null
                    ? "?"
                    : (card.EnergyCost.CostsX ? "X" : card.EnergyCost.Canonical.ToString());
                string plus = _cardUpgraded ? "+" : "";
                string destTag = _cardToHand ? CoopQolI18n.T("[+手牌]", "[+Hand]") : CoopQolI18n.T("[+牌组]", "[+Deck]");
                string costText = CoopQolI18n.IsZh ? $"{cost}费" : $"{cost}E";
                var btn = CardPoolRows[row];
                NativeUiHelper.SetButtonText(btn, $"{destTag} [{costText}] {title}{plus} ({CardTypeName(card.Type)}) — {desc}");
                btn.Visible = true;
                var captured = card;
                CardPoolActions[row] = () => ObtainCard(captured, title);
                row++;
                if (row >= MaxPooledRows) break;
            }
        }
        else if (me != null)
        {
            var pileType = _cardToHand ? PileType.Hand : PileType.Deck;
            var pile = pileType.GetPile(me);
            if (pile != null)
            {
                var cards = pile.Cards;
                for (int i = 0; i < cards.Count; i++)
                {
                    var card = cards[i];
                    string title = card.Title ?? card.Id.Entry;
                    if (!string.IsNullOrEmpty(q)
                        && !title.Contains(q, StringComparison.OrdinalIgnoreCase)
                        && !card.Id.Entry.Contains(q, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var btn = CardPoolRows[row];
                    NativeUiHelper.SetButtonText(btn, CoopQolI18n.IsZh
                        ? $"[移除] {title} ({CardTypeName(card.Type)}) — 点击从{(_cardToHand ? "手牌" : "牌组")}中移除"
                        : $"[Remove] {title} ({CardTypeName(card.Type)}) — Click to remove from {(_cardToHand ? "Hand" : "Deck")}");
                    btn.Visible = true;
                    var captured = card;
                    bool fromHand = _cardToHand;
                    CardPoolActions[row] = () => RemoveOwnedCard(captured, title, fromHand);
                    row++;
                    if (row >= MaxPooledRows) break;
                }
            }
        }

        for (int i = row; i < MaxPooledRows; i++)
            CardPoolRows[i].Visible = false;
    }

    private static bool MatchesCardFilter(CardModel card, ModelId? myPoolId)
    {
        return _cardFilter switch
        {
            1 => myPoolId != null && card.Pool?.Id == myPoolId,
            2 => card.Type == CardType.Attack,
            3 => card.Type == CardType.Skill,
            4 => card.Type == CardType.Power,
            5 => card.Type == CardType.Curse || card.Type == CardType.Status,
            _ => true,
        };
    }

    private static void ObtainCard(CardModel canonicalCard, string title)
    {
        var target = GetSelectedPlayer();
        if (target == null) return;

        if (_cardToHand && CombatManager.Instance?.IsInProgress != true)
        {
            SetStatus(CoopQolI18n.T(
                "当前不在战斗中，已为您改选【加入牌组】或请进入战斗后加入手牌。",
                "Not in combat — switch to [To Deck] or enter combat to add to hand."), isError: true);
            return;
        }

        string pName = PlayerNameHelper.GetPlayerName(target);
        if (IsSingleplayer() && LocalContext.IsMe(target))
        {
            ICardScope? scope = _cardToHand
                ? CombatManager.Instance.DebugOnlyGetState()
                : RunManager.Instance.DebugOnlyGetState();
            if (scope == null) return;

            var instance = scope.CreateCard(canonicalCard, target);
            if (_cardUpgraded && instance.CurrentUpgradeLevel < instance.MaxUpgradeLevel)
                CardCmd.Upgrade(instance, CardPreviewStyle.None);

            var pile = _cardToHand ? PileType.Hand : PileType.Deck;
            TaskHelper.RunSafely(CardPileCmd.Add(instance, pile, CardPilePosition.Bottom, null, skipVisuals: false));
            SetStatus(CoopQolI18n.IsZh
                ? $"已为 {pName} 获取卡牌：{title}{(_cardUpgraded ? "+" : "")} -> {(_cardToHand ? "手牌" : "牌组")}。"
                : $"Added {title}{(_cardUpgraded ? "+" : "")} to {pName}'s {(_cardToHand ? "Hand" : "Deck")}.");
        }
        else
        {
            string pileArg = _cardToHand ? "hand" : "deck";
            DispatchPlayerConsoleCmd(target, $"card {canonicalCard.Id.Entry} {pileArg}", CoopQolI18n.IsZh
                ? $"已为 {pName} 获取卡牌 {title}"
                : $"Added {title} to {pName}");
        }
        ScheduleRefreshStats();
    }

    private static void RemoveOwnedCard(CardModel card, string title, bool fromHand)
    {
        var target = GetSelectedPlayer();
        if (target == null) return;

        if (IsSingleplayer() && LocalContext.IsMe(target))
        {
            if (fromHand)
                TaskHelper.RunSafely(CardPileCmd.RemoveFromCombat(card, skipVisuals: false));
            else
                TaskHelper.RunSafely(CardPileCmd.RemoveFromDeck(card, showPreview: true));
            SetStatus(CoopQolI18n.IsZh
                ? $"已从{(fromHand ? "手牌" : "牌组")}移除：{title}。"
                : $"Removed {title} from {(fromHand ? "Hand" : "Deck")}.");
        }
        else
        {
            string pileName = fromHand ? "Hand" : "Deck";
            DispatchPlayerConsoleCmd(target, $"removecard {card.Id.Entry} {pileName}", CoopQolI18n.IsZh
                ? $"已为 {PlayerNameHelper.GetPlayerName(target)} 移除 {title}"
                : $"Removed {title} from {PlayerNameHelper.GetPlayerName(target)}");
        }

        Callable.From(RefreshCardsView).CallDeferred();
        ScheduleRefreshStats();
    }

    // --- Relics & Potions ---

    private static void RefreshItemsView()
    {
        NativeUiHelper.SetSpireButtonActive(_itemModeRelicBtn, _itemMode == 0);
        NativeUiHelper.SetSpireButtonActive(_itemModePotionBtn, _itemMode == 1);
        NativeUiHelper.SetSpireButtonActive(_itemModeOwnedRelicBtn, _itemMode == 2);
        _potionSlotPlusBtn.Visible = _itemMode == 1;

        for (int i = 0; i < ItemRarityBtns.Length; i++)
        {
            ItemRarityBtns[i].Visible = _itemMode != 2;
            NativeUiHelper.SetSpireButtonActive(ItemRarityBtns[i], _itemRarityFilter == i);
        }

        string q = (_itemSearchEdit?.Text ?? string.Empty).Trim();
        int row = 0;

        if (_itemMode == 0)
        {
            var all = _cachedAllRelics ??= ModelDb.AllRelics
                .OrderBy(r => SafeLocText(r.Title, r.Id.Entry), StringComparer.CurrentCulture)
                .ToList();

            foreach (var relic in all)
            {
                if (!MatchesRelicRarity(relic.Rarity)) continue;
                string title = SafeLocText(relic.Title, relic.Id.Entry);
                string desc = CleanText(SafeLocText(relic.DynamicDescription, string.Empty), 42);
                if (!string.IsNullOrEmpty(q)
                    && !title.Contains(q, StringComparison.OrdinalIgnoreCase)
                    && !relic.Id.Entry.Contains(q, StringComparison.OrdinalIgnoreCase)
                    && !desc.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var btn = ItemPoolRows[row];
                NativeUiHelper.SetButtonText(btn, CoopQolI18n.IsZh
                    ? $"[获取·{RelicRarityName(relic.Rarity)}] {title} — {desc}"
                    : $"[Add · {RelicRarityName(relic.Rarity)}] {title} — {desc}");
                btn.Visible = true;
                var captured = relic;
                ItemPoolActions[row] = () => RunConsoleCmd($"relic add {captured.Id.Entry}", CoopQolI18n.IsZh ? $"已获取遗物：{title}" : $"Added Relic: {title}");
                row++;
                if (row >= MaxPooledRows) break;
            }
        }
        else if (_itemMode == 1)
        {
            var all = _cachedAllPotions ??= ModelDb.AllPotions
                .OrderBy(p => SafeLocText(p.Title, p.Id.Entry), StringComparer.CurrentCulture)
                .ToList();

            foreach (var potion in all)
            {
                if (!MatchesPotionRarity(potion.Rarity)) continue;
                string title = SafeLocText(potion.Title, potion.Id.Entry);
                string desc = CleanText(SafeLocText(potion.DynamicDescription, string.Empty), 42);
                if (!string.IsNullOrEmpty(q)
                    && !title.Contains(q, StringComparison.OrdinalIgnoreCase)
                    && !potion.Id.Entry.Contains(q, StringComparison.OrdinalIgnoreCase)
                    && !desc.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var btn = ItemPoolRows[row];
                NativeUiHelper.SetButtonText(btn, CoopQolI18n.IsZh
                    ? $"[获取·{PotionRarityName(potion.Rarity)}] {title} — {desc}"
                    : $"[Add · {PotionRarityName(potion.Rarity)}] {title} — {desc}");
                btn.Visible = true;
                var captured = potion;
                ItemPoolActions[row] = () => RunConsoleCmd($"potion {captured.Id.Entry}", CoopQolI18n.IsZh ? $"已获取药水：{title}" : $"Added Potion: {title}");
                row++;
                if (row >= MaxPooledRows) break;
            }
        }
        else
        {
            var me = GetSelectedPlayer();
            if (me != null)
            {
                foreach (var relic in me.Relics)
                {
                    string title = SafeLocText(relic.Title, relic.Id.Entry);
                    if (!string.IsNullOrEmpty(q)
                        && !title.Contains(q, StringComparison.OrdinalIgnoreCase)
                        && !relic.Id.Entry.Contains(q, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var btn = ItemPoolRows[row];
                    NativeUiHelper.SetButtonText(btn, CoopQolI18n.IsZh
                        ? $"[移除] {title} ({RelicRarityName(relic.Rarity)}) — 点击移除该遗物"
                        : $"[Remove] {title} ({RelicRarityName(relic.Rarity)}) — Click to remove");
                    btn.Visible = true;
                    var captured = relic;
                    ItemPoolActions[row] = () =>
                    {
                        RunConsoleCmd($"relic remove {captured.Id.Entry}", CoopQolI18n.IsZh ? $"已移除遗物：{title}" : $"Removed Relic: {title}");
                        Callable.From(RefreshItemsView).CallDeferred();
                    };
                    row++;
                    if (row >= MaxPooledRows) break;
                }
            }
        }

        for (int i = row; i < MaxPooledRows; i++)
            ItemPoolRows[i].Visible = false;
    }

    private static void GainPotionSlot()
    {
        if (!RequireSingleplayer(CoopQolI18n.T("扩展药水栏", "Expand Potion Slots"))) return;
        var me = GetSelectedPlayer();
        if (me == null) return;
        TaskHelper.RunSafely(PlayerCmd.GainMaxPotionCount(1, me));
        SetStatus(CoopQolI18n.T("药水栏位上限 +1！", "Max Potion Slots +1!"));
    }

    private static bool MatchesRelicRarity(RelicRarity r) => _itemRarityFilter switch
    {
        1 => r == RelicRarity.Common,
        2 => r == RelicRarity.Uncommon,
        3 => r == RelicRarity.Rare,
        4 => r is RelicRarity.Shop or RelicRarity.Event or RelicRarity.Ancient or RelicRarity.Starter,
        _ => true,
    };

    private static bool MatchesPotionRarity(PotionRarity r) => _itemRarityFilter switch
    {
        1 => r == PotionRarity.Common,
        2 => r == PotionRarity.Uncommon,
        3 => r == PotionRarity.Rare,
        4 => r is PotionRarity.Event or PotionRarity.Token,
        _ => true,
    };

    // --- Helpers & Execution Guards ---

    private static bool IsSingleplayer() => RunManager.Instance?.IsSingleplayerOrFakeMultiplayer ?? true;

    private static bool RequireSingleplayer(string feature)
    {
        if (IsSingleplayer()) return true;
        SetStatus(CoopQolI18n.IsZh
            ? $"联机模式下暂不支持【{feature}】，请使用支持动作队列同步的命令。"
            : $"[{feature}] is not supported in Multiplayer; use action-synced commands.", isError: true);
        return false;
    }

    private static bool RequireInCombat(string feature)
    {
        if (CombatManager.Instance?.IsInProgress == true) return true;
        SetStatus(CoopQolI18n.IsZh ? $"【{feature}】仅在战斗中可用。" : $"[{feature}] is only available in combat.", isError: true);
        return false;
    }

    private static bool RunConsoleCmd(string cmd, string successMsg)
    {
        var target = GetSelectedPlayer() ?? GetLocalPlayer();
        if (target == null) return false;
        return DispatchPlayerConsoleCmd(target, cmd, successMsg);
    }

    private static bool DispatchPlayerConsoleCmd(Player targetPlayer, string cmd, string successMsg)
    {
        try
        {
            var netType = RunManager.Instance?.NetService?.Type ?? NetGameType.Singleplayer;
            if (netType != NetGameType.Singleplayer && NDevConsole.InstanceOrNull == null)
            {
                SetStatus(CoopQolI18n.T("联机同步控制台实例未就绪。", "Multiplayer console instance not ready."), isError: true);
                return false;
            }

            if (netType == NetGameType.Host)
            {
                var sync = RunManager.Instance?.ActionQueueSynchronizer;
                if (sync != null && HostEnqueueActionMethod != null)
                {
                    var action = new ConsoleCmdGameAction(targetPlayer, cmd, CombatManager.Instance?.IsInProgress == true);
                    HostEnqueueActionMethod.Invoke(sync, new object[] { action, targetPlayer.NetId });
                    if (!string.IsNullOrEmpty(successMsg))
                        SetStatus(successMsg);
                    ScheduleRefreshStats();
                    return true;
                }
            }

            if (LocalContext.IsMe(targetPlayer))
            {
                var res = Console.ProcessCommand(cmd);
                if (!res.success)
                {
                    SetStatus(string.IsNullOrWhiteSpace(res.msg)
                        ? (CoopQolI18n.IsZh ? $"指令执行失败: {cmd}" : $"Command failed: {cmd}")
                        : res.msg, isError: true);
                    return false;
                }
            }
            else if (NDevConsole.InstanceOrNull != null)
            {
                TaskHelper.RunSafely(NDevConsole.InstanceOrNull.ProcessNetCommand(targetPlayer, cmd));
            }
            else
            {
                SetStatus(CoopQolI18n.T("无法跨成员执行该指令。", "Cannot execute command for this player."), isError: true);
                return false;
            }

            if (!string.IsNullOrEmpty(successMsg))
                SetStatus(successMsg);
            ScheduleRefreshStats();
            return true;
        }
        catch (Exception ex)
        {
            CoopQolModEntry.Log.Warn($"GodMode DispatchPlayerConsoleCmd '{cmd}' failed: {ex.Message}");
            SetStatus(CoopQolI18n.IsZh ? $"执行异常: {ex.Message}" : $"Execution error: {ex.Message}", isError: true);
            return false;
        }
    }

    private static void ScheduleRefreshStats()
    {
        Callable.From(() =>
        {
            if (IsOpen && _activeSubTab == 0)
                RefreshStatsStrip();
            if (IsOpen)
                RefreshMemberSelector();
        }).CallDeferred();
    }

    private static Player? GetSelectedPlayer()
    {
        var state = PlayerNameHelper.GetRunState();
        if (state == null || state.Players.Count == 0) return null;
        if (_selectedPlayerNetId != 0)
        {
            var found = state.Players.FirstOrDefault(p => p.NetId == _selectedPlayerNetId);
            if (found != null) return found;
        }
        return GetLocalPlayer();
    }

    private static Player? GetLocalPlayer()
    {
        var state = PlayerNameHelper.GetRunState();
        if (state == null) return null;
        return LocalContext.GetMe(state) ?? (state.Players.Count > 0 ? state.Players[0] : null);
    }

    private static List<PowerModel> BuildAllPowersCache()
    {
        var list = new List<PowerModel>();
        foreach (var t in ModelDb.AllAbstractModelSubtypes)
        {
            if (!t.IsSubclassOf(typeof(PowerModel)) || t.IsAbstract) continue;
            try
            {
                var p = ModelDb.DebugPower(t);
                if (p != null) list.Add(p);
            }
            catch
            {
                // Skip uninstantiable abstract/internal types
            }
        }
        list.Sort((a, b) => string.Compare(
            SafeLocText(a.Title, a.Id.Entry),
            SafeLocText(b.Title, b.Id.Entry),
            StringComparison.CurrentCulture));
        return list;
    }

    private static string SafeLocText(MegaCrit.Sts2.Core.Localization.LocString? loc, string fallback)
    {
        try
        {
            var s = loc?.GetFormattedText();
            return string.IsNullOrWhiteSpace(s) ? fallback : s;
        }
        catch
        {
            return fallback;
        }
    }

    private static string CleanText(string? raw, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.Replace("\r", " ").Replace("\n", " ");
        var sb = new StringBuilder(s.Length);
        bool inTag = false;
        foreach (var ch in s)
        {
            if (ch == '[') { inTag = true; continue; }
            if (ch == ']') { inTag = false; continue; }
            if (!inTag) sb.Append(ch);
        }
        var cleaned = sb.ToString().Trim();
        return cleaned.Length > maxLen ? cleaned.Substring(0, maxLen - 3) + "..." : cleaned;
    }

    private static string CardTypeName(CardType t) => t switch
    {
        CardType.Attack => CoopQolI18n.T("攻击", "Attack"),
        CardType.Skill => CoopQolI18n.T("技能", "Skill"),
        CardType.Power => CoopQolI18n.T("能力", "Power"),
        CardType.Status => CoopQolI18n.T("状态", "Status"),
        CardType.Curse => CoopQolI18n.T("诅咒", "Curse"),
        CardType.Quest => CoopQolI18n.T("任务", "Quest"),
        _ => CoopQolI18n.T("其他", "Other"),
    };

    private static string RelicRarityName(RelicRarity r) => r switch
    {
        RelicRarity.Starter => CoopQolI18n.T("初始", "Starter"),
        RelicRarity.Common => CoopQolI18n.T("普通", "Common"),
        RelicRarity.Uncommon => CoopQolI18n.T("罕见", "Uncommon"),
        RelicRarity.Rare => CoopQolI18n.T("稀有", "Rare"),
        RelicRarity.Shop => CoopQolI18n.T("商店", "Shop"),
        RelicRarity.Event => CoopQolI18n.T("事件", "Event"),
        RelicRarity.Ancient => CoopQolI18n.T("远古", "Ancient"),
        _ => CoopQolI18n.T("特殊", "Special"),
    };

    private static string PotionRarityName(PotionRarity r) => r switch
    {
        PotionRarity.Common => CoopQolI18n.T("普通", "Common"),
        PotionRarity.Uncommon => CoopQolI18n.T("罕见", "Uncommon"),
        PotionRarity.Rare => CoopQolI18n.T("稀有", "Rare"),
        PotionRarity.Event => CoopQolI18n.T("事件", "Event"),
        PotionRarity.Token => CoopQolI18n.T("衍生", "Token"),
        _ => CoopQolI18n.T("特殊", "Special"),
    };

    private static void SetStatus(string msg, bool isError = false)
    {
        if (_statusFooterLabel == null || !GodotObject.IsInstanceValid(_statusFooterLabel))
            return;

        _statusFooterLabel.Text = isError
            ? (CoopQolI18n.IsZh ? $"[错误] {msg}" : $"[Error] {msg}")
            : (CoopQolI18n.IsZh ? $"[完成] {msg}" : $"[OK] {msg}");
        _statusFooterLabel.AddThemeColorOverride(
            "font_color",
            isError ? new Color(1f, 0.58f, 0.52f) : new Color(0.62f, 0.90f, 0.70f));
    }

    private static void CenterModalIfNeeded()
    {
        if (_backdrop == null || _modalPanel == null) return;
        var vp = _backdrop.GetViewportRect().Size;
        if (vp.X <= 0) vp = new Vector2(1920, 1080);
        if (_modalPanel.Position == Vector2.Zero)
        {
            _modalPanel.Position = new Vector2(
                Mathf.Max(20f, (vp.X - _modalPanel.Size.X) * 0.5f),
                Mathf.Max(68f, (vp.Y - _modalPanel.Size.Y) * 0.5f));
        }
    }

    private static void OnHeaderGuiInput(InputEvent @event)
    {
        if (_modalPanel == null || _backdrop == null) return;

        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            if (mb.Pressed)
            {
                _dragging = true;
                _dragOffset = _backdrop.GetGlobalMousePosition() - _modalPanel.Position;
            }
            else
            {
                _dragging = false;
            }
        }
        else if (@event is InputEventMouseMotion && _dragging)
        {
            var vp = _backdrop.GetViewportRect().Size;
            if (vp.X <= 0) vp = new Vector2(1920, 1080);
            var target = _backdrop.GetGlobalMousePosition() - _dragOffset;
            target.X = Mathf.Clamp(target.X, 10f, Math.Max(10f, vp.X - _modalPanel.Size.X - 10f));
            target.Y = Mathf.Clamp(target.Y, 64f, Math.Max(64f, vp.Y - _modalPanel.Size.Y - 10f));
            _modalPanel.Position = target;
        }
    }

    private static void SetHotkeyBlocking(bool blocking)
    {
        if (blocking == _isBlockingHotkeys) return;
        _isBlockingHotkeys = blocking;

        try
        {
            var mgr = NHotkeyManager.Instance;
            if (mgr == null || _modalPanel == null) return;
            if (blocking)
                mgr.AddBlockingScreen(_modalPanel);
            else
                mgr.RemoveBlockingScreen(_modalPanel);
        }
        catch (Exception ex)
        {
            CoopQolModEntry.Log.Warn($"GodMode hotkey blocking failed: {ex.Message}");
        }
    }
}
