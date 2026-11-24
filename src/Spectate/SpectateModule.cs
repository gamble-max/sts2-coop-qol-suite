using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CoopQolSuite.Core;
using CoopQolSuite.Terminal;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CoopQolSuite.Spectate;

public static class SpectateModule
{
    private static ulong? _spectatingNetId;
    private static readonly HashSet<ulong> EndedTurnPlayerIds = new();
    private static CombatManager? _subscribedCombatMgr;
    private static bool _autoSpectatedThisTurn;

    private static Control? _root;
    private static PanelContainer? _bannerPanel;
    private static Label? _bannerLabel;
    private static PanelContainer? _hudPanel;
    private static Label? _statsLabel;
    private static Label? _relicsPotionsLabel;
    private static Button? _drawPileBtn;
    private static Button? _discardPileBtn;
    private static Button? _exhaustPileBtn;
    private static Button? _deckRelicsBtn;
    private static HFlowContainer? _handContainer;

    private static double _refreshAccum;
    private static string _lastHandFingerprint = string.Empty;
    private static ulong? _lastHoveredCardHash;

    private static readonly StyleBoxFlat HoveredCardStyle = new()
    {
        BgColor = new Color(0.25f, 0.22f, 0.08f, 0.95f),
        BorderColor = new Color(1f, 0.85f, 0.2f, 1f),
        BorderWidthTop = 2,
        BorderWidthBottom = 2,
        BorderWidthLeft = 2,
        BorderWidthRight = 2,
        CornerRadiusTopLeft = 4,
        CornerRadiusTopRight = 4,
        CornerRadiusBottomLeft = 4,
        CornerRadiusBottomRight = 4,
        ShadowSize = 0,
    };

    public static bool IsSpectating => _spectatingNetId.HasValue;

    public static void Initialize(Node rootContainer)
    {
        if (_root != null && GodotObject.IsInstanceValid(_root))
            return;

        _root = new Control
        {
            Name = "SpectateOverlay",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        BuildUi();
        rootContainer.AddChild(_root);
        SubscribeCombatEvents();
    }

    public static void OnRunEntered()
    {
        SubscribeCombatEvents();
        ExitSpectate();
    }

    public static void OnRunExited()
    {
        ExitSpectate();
    }

    private static void SubscribeCombatEvents()
    {
        var cm = CombatManager.Instance;
        if (cm == null || ReferenceEquals(cm, _subscribedCombatMgr)) return;

        if (_subscribedCombatMgr != null)
        {
            _subscribedCombatMgr.CombatSetUp -= OnCombatSetUp;
            _subscribedCombatMgr.CombatEnded -= OnCombatEnded;
            _subscribedCombatMgr.TurnStarted -= OnTurnStarted;
            _subscribedCombatMgr.PlayerEndedTurn -= OnPlayerEndedTurn;
            _subscribedCombatMgr.PlayerUnendedTurn -= OnPlayerUnendedTurn;
        }

        _subscribedCombatMgr = cm;
        cm.CombatSetUp += OnCombatSetUp;
        cm.CombatEnded += OnCombatEnded;
        cm.TurnStarted += OnTurnStarted;
        cm.PlayerEndedTurn += OnPlayerEndedTurn;
        cm.PlayerUnendedTurn += OnPlayerUnendedTurn;
    }

    private static void OnCombatSetUp(CombatState _)
    {
        EndedTurnPlayerIds.Clear();
        _autoSpectatedThisTurn = false;
        ExitSpectate();
    }

    private static void OnCombatEnded(CombatRoom _)
    {
        EndedTurnPlayerIds.Clear();
        _autoSpectatedThisTurn = false;
        ExitSpectate();
    }

    private static void OnTurnStarted(CombatState _)
    {
        EndedTurnPlayerIds.Clear();
        _autoSpectatedThisTurn = false;
        if (IsSpectating)
        {
            ExitSpectate();
        }
    }

    private static void OnPlayerEndedTurn(Player player, bool _)
    {
        if (player == null) return;
        EndedTurnPlayerIds.Add(player.NetId);

        if (!CoopQolConfig.Current.AutoSpectateOnEndTurn || _autoSpectatedThisTurn)
            return;

        if (LocalContext.IsMe(player))
        {
            var nextTeammate = GetTeammates()
                .FirstOrDefault(p => p.Creature.IsAlive && !EndedTurnPlayerIds.Contains(p.NetId));
            if (nextTeammate != null)
            {
                _autoSpectatedThisTurn = true;
                SetSpectatingPlayer(nextTeammate.NetId);
            }
        }
    }

    private static void OnPlayerUnendedTurn(Player player)
    {
        if (player == null) return;
        EndedTurnPlayerIds.Remove(player.NetId);

        if (LocalContext.IsMe(player) && IsSpectating)
        {
            ExitSpectate();
        }
    }

    public static List<Player> GetTeammates()
    {
        var runState = PlayerNameHelper.GetRunState();
        if (runState == null) return new List<Player>();

        return runState.Players
            .Where(p => !LocalContext.IsMe(p))
            .ToList();
    }

    public static Player? GetSpectatingPlayer()
    {
        if (!_spectatingNetId.HasValue) return null;
        var runState = PlayerNameHelper.GetRunState();
        return runState?.Players.FirstOrDefault(p => p.NetId == _spectatingNetId.Value);
    }

    public static void ToggleSpectatePlayer(ulong netId)
    {
        if (_spectatingNetId == netId)
            ExitSpectate();
        else
            SetSpectatingPlayer(netId);
    }

    public static void SetSpectatingPlayer(ulong? netId)
    {
        _spectatingNetId = netId;
        Callable.From(RefreshAll).CallDeferred();
        TopBarQuickBar.Refresh();
    }

    public static void ExitSpectate()
    {
        if (!_spectatingNetId.HasValue) return;
        _spectatingNetId = null;
        Callable.From(RefreshAll).CallDeferred();
        TopBarQuickBar.Refresh();
    }

    public static void CycleSpectateNext()
    {
        var teammates = GetTeammates();
        if (teammates.Count == 0)
        {
            ExitSpectate();
            return;
        }

        if (!_spectatingNetId.HasValue)
        {
            SetSpectatingPlayer(teammates[0].NetId);
            return;
        }

        int idx = teammates.FindIndex(p => p.NetId == _spectatingNetId.Value);
        if (idx < 0 || idx + 1 >= teammates.Count)
        {
            ExitSpectate();
        }
        else
        {
            SetSpectatingPlayer(teammates[idx + 1].NetId);
        }
    }

    [HarmonyPatch(typeof(NMultiplayerPlayerState), nameof(NMultiplayerPlayerState._Ready))]
    private static class NMultiplayerPlayerStateReadyPatch
    {
        private static void Postfix(NMultiplayerPlayerState __instance)
        {
            try
            {
                if (__instance.Player == null || LocalContext.IsMe(__instance.Player))
                    return;

                if (__instance.GetNodeOrNull<Button>("CoopQolSpectateBtn") != null)
                    return;

                var targetNetId = __instance.Player.NetId;
                var btn = new Button
                {
                    Name = "CoopQolSpectateBtn",
                    Text = CoopQolI18n.T("观战", "Spectate"),
                    TooltipText = CoopQolI18n.T(
                        "只读切换查看该队友实时手牌与状态 (Tab 轮切 / Esc 返回)",
                        "Toggle read-only live hand & status view (Tab: Next / Esc: Back)"),
                    CustomMinimumSize = new Vector2(68f, 26f),
                };
                NativeUiHelper.InitSpireButton(btn, 12);
                btn.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
                btn.OffsetLeft = 192f;
                btn.OffsetTop = 6f;
                btn.OffsetRight = 260f;
                btn.OffsetBottom = 32f;
                btn.Pressed += () => ToggleSpectatePlayer(targetNetId);
                __instance.AddChild(btn);
            }
            catch (Exception ex)
            {
                CoopQolModEntry.Log.Error($"NMultiplayerPlayerStateReadyPatch error: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(NMultiplayerPlayerExpandedState), nameof(NMultiplayerPlayerExpandedState._Ready))]
    private static class NMultiplayerPlayerExpandedStateReadyPatch
    {
        private static readonly FieldInfo? PlayerField =
            AccessTools.Field(typeof(NMultiplayerPlayerExpandedState), "_player");

        private static void Postfix(NMultiplayerPlayerExpandedState __instance)
        {
            try
            {
                if (PlayerField?.GetValue(__instance) is not Player player || LocalContext.IsMe(player))
                    return;

                if (__instance.GetNodeOrNull<Button>("CoopQolLiveSpectateBtn") != null)
                    return;

                var targetNetId = player.NetId;
                var btn = new Button
                {
                    Name = "CoopQolLiveSpectateBtn",
                    Text = CoopQolI18n.T("查看实时战斗手牌 (观战)", "Live Combat Hand (Spectate)"),
                    TooltipText = CoopQolI18n.T(
                        "关闭此界面并进入该队友的实时只读战斗观战视角",
                        "Close this screen and enter read-only live combat spectate mode"),
                    CustomMinimumSize = new Vector2(224f, 38f),
                };
                NativeUiHelper.InitSpireButton(btn, 14, tone: SpireButtonTone.GoldAccent);
                btn.SetAnchorsPreset(Control.LayoutPreset.TopRight);
                btn.OffsetLeft = -284f;
                btn.OffsetTop = 76f;
                btn.OffsetRight = -60f;
                btn.OffsetBottom = 114f;
                btn.Pressed += () =>
                {
                    NCapstoneContainer.Instance?.Close();
                    SetSpectatingPlayer(targetNetId);
                };
                __instance.AddChild(btn);
            }
            catch (Exception ex)
            {
                CoopQolModEntry.Log.Error($"NMultiplayerPlayerExpandedStateReadyPatch error: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(NPlayerHand), "CanPlayCards")]
    private static class NPlayerHandCanPlayCardsPatch
    {
        private static bool Prefix(ref bool __result)
        {
            if (IsSpectating)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    private static void BuildUi()
    {
        if (_root == null) return;

        // Top read-only banner
        _bannerPanel = new PanelContainer
        {
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        _bannerPanel.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        _bannerPanel.OffsetTop = 66f;
        _bannerPanel.OffsetBottom = 104f;
        _bannerPanel.OffsetLeft = 360f;
        _bannerPanel.OffsetRight = -360f;
        _bannerPanel.AddThemeStyleboxOverride("panel", NativeUiHelper.CreateSpirePanelStyle(0.90f, new Color(0.35f, 0.68f, 0.95f, 0.9f), 6));

        var bannerRow = new HBoxContainer();
        bannerRow.AddThemeConstantOverride("separation", 10);

        _bannerLabel = new Label
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
        };
        NativeUiHelper.ApplyFont(_bannerLabel, 14);
        _bannerLabel.AddThemeColorOverride("font_color", new Color(0.85f, 0.95f, 1f));
        bannerRow.AddChild(_bannerLabel);

        var nextBtn = new Button
        {
            Text = CoopQolI18n.T("切换队友", "Next Ally"),
            TooltipText = CoopQolI18n.T("切换到下一位队友 (Tab)", "Switch to next teammate (Tab)"),
            CustomMinimumSize = new Vector2(96f, 28f),
        };
        NativeUiHelper.InitSpireButton(nextBtn, 12);
        nextBtn.Pressed += CycleSpectateNext;
        bannerRow.AddChild(nextBtn);

        var returnBtn = new Button
        {
            Text = CoopQolI18n.T("返回自己 (Esc)", "Back (Esc)"),
            CustomMinimumSize = new Vector2(118f, 28f),
        };
        NativeUiHelper.InitSpireButton(returnBtn, 12, tone: SpireButtonTone.GoldAccent);
        returnBtn.Pressed += ExitSpectate;
        bannerRow.AddChild(returnBtn);

        _bannerPanel.AddChild(bannerRow);
        _root.AddChild(_bannerPanel);

        // Bottom-center read-only combat & hand mirror panel
        _hudPanel = new PanelContainer
        {
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        _hudPanel.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        _hudPanel.OffsetLeft = 260f;
        _hudPanel.OffsetRight = -260f;
        _hudPanel.OffsetTop = -250f;
        _hudPanel.OffsetBottom = -16f;
        _hudPanel.AddThemeStyleboxOverride("panel", NativeUiHelper.CreateSpirePanelStyle());

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 6);

        // Row 1: Stats + Pile buttons
        var topRow = new HBoxContainer();
        topRow.AddThemeConstantOverride("separation", 8);

        _statsLabel = new Label
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
        };
        NativeUiHelper.ApplyFont(_statsLabel, 14);
        _statsLabel.AddThemeColorOverride("font_color", Colors.White);
        topRow.AddChild(_statsLabel);

        _drawPileBtn = CreatePileButton(CoopQolI18n.T("抽牌堆(0)", "Draw (0)"), () => OpenTeammatePile(PileType.Draw));
        _discardPileBtn = CreatePileButton(CoopQolI18n.T("弃牌堆(0)", "Discard (0)"), () => OpenTeammatePile(PileType.Discard));
        _exhaustPileBtn = CreatePileButton(CoopQolI18n.T("消耗堆(0)", "Exhaust (0)"), () => OpenTeammatePile(PileType.Exhaust));
        _deckRelicsBtn = CreatePileButton(CoopQolI18n.T("卡组/遗物", "Deck/Relics"), OpenTeammateExpandedState);

        topRow.AddChild(_drawPileBtn);
        topRow.AddChild(_discardPileBtn);
        topRow.AddChild(_exhaustPileBtn);
        topRow.AddChild(_deckRelicsBtn);
        vbox.AddChild(topRow);

        // Row 2: Relics, Potions & Powers summary
        _relicsPotionsLabel = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        NativeUiHelper.ApplyFont(_relicsPotionsLabel, 12);
        _relicsPotionsLabel.AddThemeColorOverride("font_color", new Color(0.85f, 0.88f, 0.94f));
        vbox.AddChild(_relicsPotionsLabel);

        // Row 3: Scrollable hand mirror
        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _handContainer = new HFlowContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _handContainer.AddThemeConstantOverride("h_separation", 8);
        _handContainer.AddThemeConstantOverride("v_separation", 8);
        scroll.AddChild(_handContainer);
        vbox.AddChild(scroll);

        _hudPanel.AddChild(vbox);
        _root.AddChild(_hudPanel);
    }

    private static Button CreatePileButton(string text, Action onPressed)
    {
        var btn = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(98f, 26f),
        };
        NativeUiHelper.StyleSpireButton(btn, 12);
        btn.Pressed += onPressed;
        return btn;
    }

    public static void Relocalize(Node rootContainer)
    {
        var savedTarget = _spectatingNetId;
        if (_root != null && GodotObject.IsInstanceValid(_root))
        {
            _root.QueueFree();
            _root = null;
        }
        _lastHandFingerprint = string.Empty;
        Initialize(rootContainer);
        _spectatingNetId = savedTarget;
        RefreshAll();

        if (NGame.Instance != null && GodotObject.IsInstanceValid(NGame.Instance))
            RelocalizeInjectedButtonsRecursive(NGame.Instance);
    }

    private static void RelocalizeInjectedButtonsRecursive(Node node)
    {
        if (node is Button btn)
        {
            if (btn.Name == "CoopQolSpectateBtn")
            {
                NativeUiHelper.InitSpireButton(btn, 12);
                NativeUiHelper.SetButtonText(btn, CoopQolI18n.T("观战", "Spectate"));
                btn.TooltipText = CoopQolI18n.T(
                    "只读切换查看该队友实时手牌与状态 (Tab 轮切 / Esc 返回)",
                    "Toggle read-only live hand & status view (Tab: Next / Esc: Back)");
            }
            else if (btn.Name == "CoopQolLiveSpectateBtn")
            {
                NativeUiHelper.InitSpireButton(btn, 14, tone: SpireButtonTone.GoldAccent);
                NativeUiHelper.SetButtonText(btn, CoopQolI18n.T("查看实时战斗手牌 (观战)", "Live Combat Hand (Spectate)"));
                btn.TooltipText = CoopQolI18n.T(
                    "关闭此界面并进入该队友的实时只读战斗观战视角",
                    "Close this screen and enter read-only live combat spectate mode");
            }
        }
        foreach (Node child in node.GetChildren())
            RelocalizeInjectedButtonsRecursive(child);
    }

    public static void HandleKeyPress(InputEventKey key)
    {
        if (_root == null || !CoopQolConfig.Current.EnableSpectate)
            return;

        if (key.Keycode != Key.Tab && !(key.Keycode == Key.Escape && _spectatingNetId.HasValue))
            return;

        if (MegaCrit.Sts2.Core.Nodes.Debug.NDevConsole.IsConsoleVisible) return;
        var vp = NGame.Instance?.GetViewport();
        var focusOwner = vp?.GuiGetFocusOwner();
        if (focusOwner is LineEdit { } le && le.IsEditing()) return;

        if (key.Keycode == Key.Tab && GetTeammates().Count > 0 && PlayerNameHelper.GetRunState() != null)
        {
            CycleSpectateNext();
            vp?.SetInputAsHandled();
        }
        else if (key.Keycode == Key.Escape && IsSpectating)
        {
            ExitSpectate();
            vp?.SetInputAsHandled();
        }
    }

    public static void Tick(double delta)
    {
        if (!_spectatingNetId.HasValue || _root == null) return;

        _refreshAccum += delta;
        if (_refreshAccum >= 0.25)
        {
            _refreshAccum = 0;
            RefreshAll();
        }
    }

    public static void RefreshAll()
    {
        if (_root == null || !GodotObject.IsInstanceValid(_root))
            return;

        var player = GetSpectatingPlayer();
        if (player == null)
        {
            _root.Visible = false;
            _lastHandFingerprint = string.Empty;
            return;
        }

        _root.Visible = true;
            bool isZh = CoopQolI18n.IsZh;
            var name = PlayerNameHelper.GetPlayerName(player);
            if (_bannerLabel != null)
            {
                _bannerLabel.Text = isZh
                    ? $"正在观战：{name}（只读预览，按 Tab 切人 / Esc 返回）"
                    : $"Spectating: {name} (Read-Only · Tab: Next / Esc: Back)";
            }

            var pcs = player.PlayerCombatState;
            var creature = player.Creature;
            var orbsText = pcs != null && pcs.OrbQueue.Capacity > 0
                ? (isZh
                    ? $" | 充能球: {pcs.OrbQueue.Orbs.Count}/{pcs.OrbQueue.Capacity} [{string.Join(", ", pcs.OrbQueue.Orbs.Select(o => o.Title.GetFormattedText()))}]"
                    : $" | Orbs: {pcs.OrbQueue.Orbs.Count}/{pcs.OrbQueue.Capacity} [{string.Join(", ", pcs.OrbQueue.Orbs.Select(o => o.Title.GetFormattedText()))}]")
                : string.Empty;

            if (_statsLabel != null)
            {
                if (pcs != null)
                {
                    _statsLabel.Text = isZh
                        ? $"HP: {creature.CurrentHp}/{creature.MaxHp}  格挡: {creature.Block}  能量: {pcs.Energy}/{pcs.MaxEnergy}  星数: {pcs.Stars}{orbsText}"
                        : $"HP: {creature.CurrentHp}/{creature.MaxHp}  Block: {creature.Block}  Energy: {pcs.Energy}/{pcs.MaxEnergy}  Stars: {pcs.Stars}{orbsText}";
                }
                else
                {
                    _statsLabel.Text = isZh
                        ? $"HP: {creature.CurrentHp}/{creature.MaxHp}（非战斗状态）"
                        : $"HP: {creature.CurrentHp}/{creature.MaxHp} (Out of Combat)";
                }
            }

            if (_drawPileBtn != null)
                NativeUiHelper.SetButtonText(_drawPileBtn, isZh ? $"抽牌堆({pcs?.DrawPile.Cards.Count ?? 0})" : $"Draw ({pcs?.DrawPile.Cards.Count ?? 0})");
            if (_discardPileBtn != null)
                NativeUiHelper.SetButtonText(_discardPileBtn, isZh ? $"弃牌堆({pcs?.DiscardPile.Cards.Count ?? 0})" : $"Discard ({pcs?.DiscardPile.Cards.Count ?? 0})");
            if (_exhaustPileBtn != null)
                NativeUiHelper.SetButtonText(_exhaustPileBtn, isZh ? $"消耗堆({pcs?.ExhaustPile.Cards.Count ?? 0})" : $"Exhaust ({pcs?.ExhaustPile.Cards.Count ?? 0})");

            if (_relicsPotionsLabel != null)
            {
                var powers = creature.Powers.Where(p => p.IsVisible)
                    .Select(p => $"{p.Title.GetFormattedText()}({p.Amount})");
                var potions = player.Potions.Select(p => p.Title.GetFormattedText());
                var relicsCount = player.Relics.Count;
                string noneStr = isZh ? "无" : "None";
                _relicsPotionsLabel.Text = isZh
                    ? $"遗物({relicsCount}) | 药水: [{(potions.Any() ? string.Join(", ", potions) : noneStr)}] | 状态: [{(powers.Any() ? string.Join(", ", powers) : noneStr)}]"
                    : $"Relics ({relicsCount}) | Potions: [{(potions.Any() ? string.Join(", ", potions) : noneStr)}] | Powers: [{(powers.Any() ? string.Join(", ", powers) : noneStr)}]";
            }

            RefreshHandCards(player);
        }

        private static void RefreshHandCards(Player player)
        {
            if (_handContainer == null) return;

            var cards = player.PlayerCombatState?.Hand.Cards.ToList() ?? new List<CardModel>();
            var hoveredModel = RunManager.Instance?.HoveredModelTracker?.GetHoveredModel(player.NetId);
            ulong? hoveredId = hoveredModel is CardModel hc ? (ulong)hc.GetHashCode() : null;

            string fingerprint = string.Join("|", cards.Select(c =>
                $"{c.GetHashCode()}:{c.Title}:{c.EnergyCost.GetAmountToSpend()}:{c.CurrentStarCost}:{c.IsUpgraded}:{c.Enchantment?.Id.Entry}"));

            if (fingerprint == _lastHandFingerprint && hoveredId == _lastHoveredCardHash)
                return;

            _lastHandFingerprint = fingerprint;
            _lastHoveredCardHash = hoveredId;

            foreach (Node child in _handContainer.GetChildren())
            {
                child.QueueFree();
            }

            if (cards.Count == 0)
            {
                var empty = new Label { Text = CoopQolI18n.T("（当前手牌为空）", "(Hand is empty)") };
                NativeUiHelper.ApplyFont(empty, 13);
                empty.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.75f));
                _handContainer.AddChild(empty);
                return;
            }

            for (int i = 0; i < cards.Count; i++)
            {
                var card = cards[i];
                int index = i;
                bool isHoveredByTeammate = hoveredModel != null && ReferenceEquals(hoveredModel, card);

                string costStr = card.EnergyCost.CostsX ? "X" : card.EnergyCost.GetAmountToSpend().ToString();
                string starStr = card.CurrentStarCost > 0
                    ? (CoopQolI18n.IsZh ? $" [星{card.CurrentStarCost}]" : $" [★{card.CurrentStarCost}]")
                    : string.Empty;
                string upgStr = card.IsUpgraded ? "+" : string.Empty;
                string enchStr = card.Enchantment != null ? $" [{card.Enchantment.Title.GetFormattedText()}]" : string.Empty;

                var cardBtn = new Button
                {
                    Text = $"[{costStr}{starStr}] {card.Title}{upgStr}{enchStr}",
                    CustomMinimumSize = new Vector2(160f, 44f),
                    TooltipText = card.Description.GetFormattedText(),
                };
                NativeUiHelper.InitSpireButton(cardBtn, 13);
                NativeUiHelper.SetSpireButtonActive(cardBtn, isHoveredByTeammate);

                cardBtn.Pressed += () => OpenInspectCard(cards, index);
                cardBtn.GuiInput += evt => QuickDeclareModule.NotifyCardPointerPress(card, cardBtn, evt);
                cardBtn.MouseEntered += () =>
                {
                    QuickDeclareModule.SetHoveredCard(card);
                    try
                    {
                        NHoverTipSet.CreateAndShow(cardBtn, card.HoverTips, HoverTipAlignment.Right);
                    }
                    catch
                    {
                        // Ignore hover tip errors if card has no tips
                    }
                };
                cardBtn.MouseExited += () =>
                {
                    QuickDeclareModule.ClearHoveredCard(card);
                    NHoverTipSet.Remove(cardBtn);
                };

                _handContainer.AddChild(cardBtn);
            }
        }

        private static void OpenInspectCard(List<CardModel> cards, int index)
        {
            try
            {
                var screen = NGame.Instance?.InspectCardScreen;
                if (screen == null)
                {
                    screen = NInspectCardScreen.Create();
                    if (screen == null) return;
                    NGame.Instance?.AddChild(screen);
                }
                screen.Open(cards, index, false);
            }
            catch (Exception ex)
            {
                CoopQolModEntry.Log.Error($"Failed to open NInspectCardScreen: {ex}");
            }
        }

        private static void OpenTeammatePile(PileType pileType)
        {
            var player = GetSpectatingPlayer();
            var pcs = player?.PlayerCombatState;
            if (pcs == null) return;

            CardPile? pile = pileType switch
            {
                PileType.Draw => pcs.DrawPile,
                PileType.Discard => pcs.DiscardPile,
                PileType.Exhaust => pcs.ExhaustPile,
                _ => null,
            };

            if (pile == null) return;
            NCardPileScreen.ShowScreen(pile, Array.Empty<string>());
        }

        public static void OpenFirstTeammateDeck()
        {
            var player = GetSpectatingPlayer() ?? GetTeammates().FirstOrDefault();
            if (player == null) return;
            NCapstoneContainer.Instance?.Open(NMultiplayerPlayerExpandedState.Create(player));
        }

        private static void OpenTeammateExpandedState()
        {
            var player = GetSpectatingPlayer();
            if (player == null) return;
            NCapstoneContainer.Instance?.Open(NMultiplayerPlayerExpandedState.Create(player));
        }
}
