using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CoopQolSuite.Core;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.InspectScreens;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Runs;

namespace CoopQolSuite.Terminal;

/// <summary>
/// Provides native-styled potion popup declaration ("声明"), double-click/double-tap
/// declaration for cards/relics/statuses, and Hover + G key one-key declaration.
/// </summary>
public static class QuickDeclareModule
{
    private const ulong DoubleClickWindowMs = 450;
    private const float DoubleClickMaxDistance = 64f;
    private const ulong SendDebounceMs = 250;

    private static bool _initialized;
    private static QuickDeclareInputNode? _inputNode;
    private static ImageTexture? _whiteTex;

    // Last card pointer press state (Click #1 -> Click #2 intercepted in _Input)
    private static CardModel? _lastCardModel;
    private static Control? _lastCardNode;
    private static ulong _lastCardClickMs;
    private static Vector2 _lastCardClickPos;
    private static bool _inspectCardWasOpenBeforeClick1;

    // Last relic pointer press state
    private static RelicModel? _lastRelicModel;
    private static Control? _lastRelicNode;
    private static ulong _lastRelicClickMs;
    private static Vector2 _lastRelicClickPos;
    private static bool _inspectRelicWasOpenBeforeClick1;

    // Last power pointer press state
    private static NPower? _lastPowerNode;
    private static ulong _lastPowerClickMs;
    private static Vector2 _lastPowerClickPos;

    // Currently hovered targets for Hover + Key (G) declaration
    private static NPower? _hoveredPower;
    private static CardModel? _hoveredCard;
    private static RelicModel? _hoveredRelic;
    private static PotionModel? _hoveredPotion;

    // Duplicate send suppression
    private static string _lastSentKey = string.Empty;
    private static ulong _lastSentMs;

    public static void Initialize(Node rootContainer)
    {
        if (_initialized) return;
        _initialized = true;

        _inputNode = new QuickDeclareInputNode
        {
            Name = "CoopQolQuickDeclareInput",
        };
        rootContainer.AddChild(_inputNode);
    }

    public static Key GetConfiguredHoverKey()
    {
        var raw = CoopQolConfig.Current.DeclareHoverKey;
        if (!string.IsNullOrWhiteSpace(raw) && Enum.TryParse<Key>(raw.Trim(), true, out var parsed) && parsed != Key.None)
            return parsed;
        return Key.G;
    }

    public static void SetHoveredCard(CardModel? card) => _hoveredCard = card;
    public static void ClearHoveredCard(CardModel? card)
    {
        if (card == null || ReferenceEquals(_hoveredCard, card))
            _hoveredCard = null;
    }

    public static void SetHoveredRelic(RelicModel? relic) => _hoveredRelic = relic;
    public static void ClearHoveredRelic(RelicModel? relic)
    {
        if (relic == null || ReferenceEquals(_hoveredRelic, relic))
            _hoveredRelic = null;
    }

    public static void SetHoveredPotion(PotionModel? potion) => _hoveredPotion = potion;
    public static void ClearHoveredPotion(PotionModel? potion)
    {
        if (potion == null || ReferenceEquals(_hoveredPotion, potion))
            _hoveredPotion = null;
    }

    public static bool TryDeclareHovered()
    {
        if (!CoopQolConfig.Current.EnableTerminal)
            return false;

        if (_hoveredPower != null && GodotObject.IsInstanceValid(_hoveredPower) && _hoveredPower.IsVisibleInTree() && _hoveredPower.Model != null)
        {
            DeclarePower(_hoveredPower.Model);
            return true;
        }

        if (_hoveredCard != null && !_hoveredCard.HasBeenRemovedFromState)
        {
            DeclareCard(_hoveredCard);
            return true;
        }

        if (_hoveredRelic != null)
        {
            DeclareRelic(_hoveredRelic);
            return true;
        }

        if (_hoveredPotion != null && !_hoveredPotion.HasBeenRemovedFromState)
        {
            DeclarePotion(_hoveredPotion);
            return true;
        }

        try
        {
            var tracker = RunManager.Instance?.HoveredModelTracker;
            var localId = LocalContext.NetId;
            if (tracker != null && localId != null)
            {
                var model = tracker.GetHoveredModel(localId.Value);
                switch (model)
                {
                    case CardModel card:
                        DeclareCard(card);
                        return true;
                    case RelicModel relic:
                        DeclareRelic(relic);
                        return true;
                    case PotionModel potion:
                        DeclarePotion(potion);
                        return true;
                }
            }
        }
        catch { }

        return false;
    }

    public static string BuildCardShareText(CardModel card)
    {
        var title = card.Title ?? card.Id.Entry;
        if (card.IsUpgraded && !title.EndsWith("+", StringComparison.Ordinal))
            title += "+";

        string cost = string.Empty;
        if (card.EnergyCost != null)
        {
            cost = card.EnergyCost.CostsX
                ? CoopQolI18n.T(" (X费)", " (X-Cost)")
                : (CoopQolI18n.IsZh ? $" ({card.EnergyCost.GetAmountToSpend()}费)" : $" ({card.EnergyCost.GetAmountToSpend()}-Cost)");
        }

        string? rawDesc = null;
        try
        {
            var pileType = card.Pile?.Type ?? PileType.None;
            rawDesc = card.GetDescriptionForPile(pileType, null);
        }
        catch
        {
            rawDesc = null;
        }
        rawDesc ??= card.Description?.GetFormattedText();

        var desc = InGameChatNet.CleanDesc(rawDesc);
        string prefix = card.Owner != null && !LocalContext.IsMe(card.Owner)
            ? (CoopQolI18n.IsZh
                ? $"【队友卡牌】{PlayerNameHelper.GetPlayerName(card.Owner)} · {title}{cost}"
                : $"[Ally Card] {PlayerNameHelper.GetPlayerName(card.Owner)} · {title}{cost}")
            : (CoopQolI18n.IsZh
                ? $"【卡牌】{title}{cost}"
                : $"[Card] {title}{cost}");

        string sep = CoopQolI18n.IsZh ? "：" : ": ";
        return string.IsNullOrEmpty(desc) ? prefix : $"{prefix}{sep}{desc}";
    }

    public static string BuildRelicShareText(RelicModel relic)
    {
        var title = relic.Title?.GetFormattedText() ?? relic.Id.Entry;
        var desc = InGameChatNet.CleanDesc(relic.DynamicDescription?.GetFormattedText());
        string prefix = relic.Owner != null && !LocalContext.IsMe(relic.Owner)
            ? (CoopQolI18n.IsZh
                ? $"【队友遗物】{PlayerNameHelper.GetPlayerName(relic.Owner)} · {title}"
                : $"[Ally Relic] {PlayerNameHelper.GetPlayerName(relic.Owner)} · {title}")
            : (CoopQolI18n.IsZh
                ? $"我有遗物【{title}】"
                : $"I have Relic [{title}]");

        string sep = CoopQolI18n.IsZh ? "：" : ": ";
        return string.IsNullOrEmpty(desc) ? prefix : $"{prefix}{sep}{desc}";
    }

    public static string BuildPotionShareText(PotionModel potion)
    {
        var title = potion.Title?.GetFormattedText() ?? potion.Id.Entry;
        var desc = InGameChatNet.CleanDesc(potion.DynamicDescription?.GetFormattedText());
        string prefix = CoopQolI18n.IsZh ? $"我有药水【{title}】" : $"I have Potion [{title}]";
        string sep = CoopQolI18n.IsZh ? "：" : ": ";
        return string.IsNullOrEmpty(desc) ? prefix : $"{prefix}{sep}{desc}";
    }

    public static void DeclarePotion(PotionModel potion)
    {
        if (!CoopQolConfig.Current.EnableTerminal) return;
        var text = BuildPotionShareText(potion);
        var title = potion.Title?.GetFormattedText() ?? potion.Id.Entry;
        SendDebounced($"P:{potion.Id.Entry}:{title}", text);
    }

    public static void DeclareRelic(RelicModel relic)
    {
        if (!CoopQolConfig.Current.EnableTerminal) return;
        var text = BuildRelicShareText(relic);
        var title = relic.Title?.GetFormattedText() ?? relic.Id.Entry;
        SendDebounced($"R:{relic.Id.Entry}:{title}", text);
    }

    public static void DeclareCard(CardModel card)
    {
        if (!CoopQolConfig.Current.EnableTerminal) return;
        var text = BuildCardShareText(card);
        SendDebounced($"C:{card.Id.Entry}:{card.Title}:{card.IsUpgraded}", text);
    }

    public static void DeclarePower(PowerModel power)
    {
        if (!CoopQolConfig.Current.EnableTerminal) return;

        var title = power.Title?.GetFormattedText() ?? power.Id.Entry;
        string stack = power.StackType == PowerStackType.Counter
            ? (CoopQolI18n.IsZh ? $" ({power.DisplayAmount}层)" : $" ({power.DisplayAmount})")
            : string.Empty;

        string ownerName = ResolvePowerOwnerName(power);
        string? rawDesc = null;
        try
        {
            var tips = power.HoverTips;
            if (tips != null)
            {
                foreach (var tip in tips)
                {
                    if (tip is HoverTip ht && !string.IsNullOrWhiteSpace(ht.Description))
                    {
                        rawDesc = ht.Description;
                        break;
                    }
                }
            }
        }
        catch
        {
            rawDesc = null;
        }

        if (string.IsNullOrWhiteSpace(rawDesc))
        {
            try { rawDesc = power.DumbHoverTip.Description; } catch { }
        }

        var desc = InGameChatNet.CleanDesc(rawDesc);
        string prefix = CoopQolI18n.IsZh
            ? $"【状态】{ownerName} · {title}{stack}"
            : $"[Status] {ownerName} · {title}{stack}";
        string sep = CoopQolI18n.IsZh ? "：" : ": ";
        var text = string.IsNullOrEmpty(desc) ? prefix : $"{prefix}{sep}{desc}";

        SendDebounced($"W:{ownerName}:{power.Id.Entry}:{power.DisplayAmount}", text);
    }

    private static string ResolvePowerOwnerName(PowerModel power)
    {
        var owner = power.Owner;
        if (owner == null) return CoopQolI18n.T("场上", "Field");

        if (owner.IsPlayer && owner.Player != null)
        {
            return LocalContext.IsMe(owner.Player)
                ? CoopQolI18n.T("我", "Me")
                : PlayerNameHelper.GetPlayerName(owner.Player);
        }

        if (owner.IsPet)
        {
            if (owner.PetOwner != null && !LocalContext.IsMe(owner.PetOwner))
            {
                return CoopQolI18n.IsZh
                    ? $"{PlayerNameHelper.GetPlayerName(owner.PetOwner)}的{owner.Name}"
                    : $"{PlayerNameHelper.GetPlayerName(owner.PetOwner)}'s {owner.Name}";
            }
            return string.IsNullOrWhiteSpace(owner.Name) ? CoopQolI18n.T("随从", "Minion") : owner.Name;
        }

        if (!string.IsNullOrWhiteSpace(owner.Name))
            return owner.Name;

        return owner.Monster?.Title?.GetFormattedText() ?? CoopQolI18n.T("目标", "Target");
    }

    private static void SendDebounced(string key, string text)
    {
        ulong now = Time.GetTicksMsec();
        if (key == _lastSentKey && now - _lastSentMs < SendDebounceMs)
            return;

        _lastSentKey = key;
        _lastSentMs = now;
        UnifiedTerminalModule.SendChatMessage(text, true);
    }

    public static void NotifyCardPointerPress(CardModel? card, Control sourceControl, InputEvent inputEvent)
    {
        if (!CoopQolConfig.Current.EnableTerminal || card == null)
            return;

        if (!TryGetPrimaryPressPosition(inputEvent, sourceControl, out var pos))
            return;

        ulong now = Time.GetTicksMsec();

        if (inputEvent is InputEventMouseButton { DoubleClick: true }
            && _lastCardModel != null
            && ReferenceEquals(_lastCardModel, card)
            && now - _lastCardClickMs <= DoubleClickWindowMs)
        {
            TriggerCardDeclareAndCleanup(card, sourceControl);
            return;
        }

        _lastCardModel = card;
        _lastCardNode = sourceControl;
        _lastCardClickMs = now;
        _lastCardClickPos = pos;
        _inspectCardWasOpenBeforeClick1 = NGame.Instance?.InspectCardScreen?.Visible == true;
    }

    public static void NotifyRelicPointerPress(RelicModel? relic, Control sourceControl, InputEvent inputEvent)
    {
        if (!CoopQolConfig.Current.EnableTerminal || relic == null)
            return;

        if (!TryGetPrimaryPressPosition(inputEvent, sourceControl, out var pos))
            return;

        ulong now = Time.GetTicksMsec();

        if (inputEvent is InputEventMouseButton { DoubleClick: true }
            && _lastRelicModel != null
            && ReferenceEquals(_lastRelicModel, relic)
            && now - _lastRelicClickMs <= DoubleClickWindowMs)
        {
            TriggerRelicDeclareAndCleanup(relic, sourceControl);
            return;
        }

        _lastRelicModel = relic;
        _lastRelicNode = sourceControl;
        _lastRelicClickMs = now;
        _lastRelicClickPos = pos;
        _inspectRelicWasOpenBeforeClick1 = NGame.Instance?.InspectRelicScreen?.Visible == true;
    }

    private static void TriggerCardDeclareAndCleanup(CardModel card, Control? sourceControl)
    {
        bool inspectNowOpen = NGame.Instance?.InspectCardScreen?.Visible == true;
        bool closeInspect = !_inspectCardWasOpenBeforeClick1 && inspectNowOpen;

        _lastCardModel = null;
        _lastCardNode = null;
        _lastCardClickMs = 0;

        try
        {
            var hand = NPlayerHand.Instance;
            if (hand != null)
            {
                hand.TryCancelCardPlay(card);
                if (hand.CurrentMode == NPlayerHand.Mode.Play)
                    hand.CancelAllCardPlay();
            }
        }
        catch { }

        if (closeInspect)
        {
            try { NGame.Instance?.InspectCardScreen?.Close(); } catch { }
        }

        DeclareCard(card);
        if (sourceControl != null && GodotObject.IsInstanceValid(sourceControl))
            sourceControl.AcceptEvent();
        NGame.Instance?.GetViewport()?.SetInputAsHandled();
    }

    private static void TriggerRelicDeclareAndCleanup(RelicModel relic, Control? sourceControl)
    {
        bool inspectNowOpen = NGame.Instance?.InspectRelicScreen?.Visible == true;
        bool closeInspect = !_inspectRelicWasOpenBeforeClick1 && inspectNowOpen;

        _lastRelicModel = null;
        _lastRelicNode = null;
        _lastRelicClickMs = 0;

        if (closeInspect)
        {
            try { NGame.Instance?.InspectRelicScreen?.Close(); } catch { }
        }

        DeclareRelic(relic);
        if (sourceControl != null && GodotObject.IsInstanceValid(sourceControl))
            sourceControl.AcceptEvent();
        NGame.Instance?.GetViewport()?.SetInputAsHandled();
    }

    private static bool TryGetPrimaryPressPosition(InputEvent inputEvent, Node? contextNode, out Vector2 pos)
    {
        if (inputEvent is InputEventMouseButton mb
            && mb.Pressed
            && mb.ButtonIndex == MouseButton.Left
            && !mb.AltPressed)
        {
            // Always read Viewport mouse coordinates so _GuiInput (local Hitbox space)
            // and _Input (viewport space) share the exact same coordinate frame.
            var vp = contextNode?.GetViewport() ?? NGame.Instance?.GetViewport();
            pos = vp?.GetMousePosition() ?? mb.GlobalPosition;
            return true;
        }

        if (inputEvent is InputEventScreenTouch st && st.Pressed)
        {
            pos = st.Position;
            return true;
        }

        pos = Vector2.Zero;
        return false;
    }

    private static void OnPowerGuiInput(NPower powerNode, InputEvent inputEvent)
    {
        if (!CoopQolConfig.Current.EnableTerminal || powerNode.Model == null)
            return;

        if (!TryGetPrimaryPressPosition(inputEvent, powerNode, out var pos))
            return;

        ulong now = Time.GetTicksMsec();
        bool isNativeDouble = inputEvent is InputEventMouseButton { DoubleClick: true };
        bool isTapDouble = ReferenceEquals(_lastPowerNode, powerNode)
            && now - _lastPowerClickMs <= DoubleClickWindowMs
            && pos.DistanceTo(_lastPowerClickPos) <= DoubleClickMaxDistance;

        if (isNativeDouble || isTapDouble)
        {
            _lastPowerNode = null;
            _lastPowerClickMs = 0;
            DeclarePower(powerNode.Model);
            powerNode.AcceptEvent();
            NGame.Instance?.GetViewport()?.SetInputAsHandled();
            return;
        }

        _lastPowerNode = powerNode;
        _lastPowerClickMs = now;
        _lastPowerClickPos = pos;
    }

    private static ImageTexture GetWhiteTex()
    {
        if (_whiteTex != null) return _whiteTex;
        var img = Image.CreateEmpty(2, 2, false, Image.Format.Rgba8);
        img.Fill(Colors.White);
        _whiteTex = ImageTexture.CreateFromImage(img);
        return _whiteTex;
    }

    private sealed partial class QuickDeclareInputNode : Node
    {
        public override void _Input(InputEvent inputEvent)
        {
            if (!CoopQolConfig.Current.EnableTerminal)
                return;

            if (!TryGetPrimaryPressPosition(inputEvent, this, out var pos))
                return;

            ulong now = Time.GetTicksMsec();

            // Check card double-click/double-tap first
            if (_lastCardModel != null)
            {
                if (now - _lastCardClickMs <= DoubleClickWindowMs
                    && pos.DistanceTo(_lastCardClickPos) <= DoubleClickMaxDistance
                    && !_lastCardModel.HasBeenRemovedFromState
                    && !(_lastCardNode is NMerchantCard mc && mc.Entry?.IsStocked != true))
                {
                    bool inspectNowOpen = NGame.Instance?.InspectCardScreen?.Visible == true;
                    bool nodeVisible = _lastCardNode != null
                        && GodotObject.IsInstanceValid(_lastCardNode)
                        && _lastCardNode.IsVisibleInTree();

                    if (nodeVisible || (!_inspectCardWasOpenBeforeClick1 && inspectNowOpen))
                    {
                        TriggerCardDeclareAndCleanup(_lastCardModel, null);
                        GetViewport()?.SetInputAsHandled();
                        return;
                    }
                }

                _lastCardModel = null;
                _lastCardNode = null;
            }

            // Check relic double-click/double-tap
            if (_lastRelicModel != null)
            {
                if (now - _lastRelicClickMs <= DoubleClickWindowMs
                    && pos.DistanceTo(_lastRelicClickPos) <= DoubleClickMaxDistance
                    && !(_lastRelicNode is NMerchantRelic mr && mr.Entry?.IsStocked != true))
                {
                    bool inspectNowOpen = NGame.Instance?.InspectRelicScreen?.Visible == true;
                    bool nodeVisible = _lastRelicNode != null
                        && GodotObject.IsInstanceValid(_lastRelicNode)
                        && _lastRelicNode.IsVisibleInTree();

                    if (nodeVisible || (!_inspectRelicWasOpenBeforeClick1 && inspectNowOpen))
                    {
                        TriggerRelicDeclareAndCleanup(_lastRelicModel, null);
                        GetViewport()?.SetInputAsHandled();
                        return;
                    }
                }

                _lastRelicModel = null;
                _lastRelicNode = null;
            }
        }
    }

    [HarmonyPatch(typeof(NPotionPopup), nameof(NPotionPopup._Ready))]
    private static class NPotionPopupReadyPatch
    {
        private static readonly FieldInfo? HolderField = AccessTools.Field(typeof(NPotionPopup), "_holder");
        private static readonly FieldInfo? ContainerField = AccessTools.Field(typeof(NPotionPopup), "_popupContainer");
        private static readonly FieldInfo? UseBtnField = AccessTools.Field(typeof(NPotionPopup), "_useButton");
        private static readonly FieldInfo? DiscardBtnField = AccessTools.Field(typeof(NPotionPopup), "_discardButton");
        private static readonly FieldInfo? HoverTipBoundsField = AccessTools.Field(typeof(NPotionPopup), "_hoverTipBounds");

        private static void Postfix(NPotionPopup __instance)
        {
            try
            {
                if (!CoopQolConfig.Current.EnableTerminal)
                    return;

                if (ContainerField?.GetValue(__instance) is not Control popupContainer)
                    return;

                if (popupContainer.GetNodeOrNull<NPotionPopupButton>("DeclareButton") != null)
                    return;

                var useBtn = UseBtnField?.GetValue(__instance) as NPotionPopupButton;
                var discardBtn = DiscardBtnField?.GetValue(__instance) as NPotionPopupButton;
                if (useBtn == null || discardBtn == null)
                    return;

                // Expand NPotionPopup height so %Container (FullRect) wraps all 3 native options
                __instance.Size = new Vector2(259f, 324f);
                if (HoverTipBoundsField?.GetValue(__instance) is Control hoverBounds)
                    hoverBounds.OffsetBottom = 324f;

                PositionButtonTopLeft(useBtn, new Vector2(11.5f, 60f), new Vector2(236f, 80f));
                PositionButtonTopLeft(discardBtn, new Vector2(11.5f, 232f), new Vector2(236f, 80f));

                var declareBtn = new NPotionPopupButton
                {
                    Name = "DeclareButton",
                    FocusMode = Control.FocusModeEnum.All,
                    TooltipText = CoopQolI18n.T(
                        "一键发送大屏弹幕声明自己拥有这瓶药水",
                        "Broadcast this potion to teammates"),
                };
                PositionButtonTopLeft(declareBtn, new Vector2(11.5f, 146f), new Vector2(236f, 80f));

                var bg = new TextureRect
                {
                    Name = "Background",
                    Position = new Vector2(-2f, 0f),
                    Size = new Vector2(240f, 80f),
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    Texture = GetWhiteTex(),
                };
                declareBtn.AddChild(bg);

                var srcLabel = useBtn.GetNodeOrNull<MegaLabel>("Label");
                if (srcLabel != null && srcLabel.Duplicate() is MegaLabel dupLabel)
                {
                    dupLabel.Name = "Label";
                    dupLabel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                    dupLabel.OffsetLeft = 0f;
                    dupLabel.OffsetTop = 0f;
                    dupLabel.OffsetRight = 0f;
                    dupLabel.OffsetBottom = 0f;
                    dupLabel.HorizontalAlignment = HorizontalAlignment.Center;
                    dupLabel.VerticalAlignment = VerticalAlignment.Center;
                    dupLabel.AddThemeColorOverride("font_color", new Color(1f, 0.9647f, 0.8863f, 1f));
                    declareBtn.AddChild(dupLabel);
                }

                popupContainer.AddChild(declareBtn);

                var lbl = declareBtn.GetNodeOrNull<MegaLabel>("Label");
                lbl?.SetTextAutoSize(CoopQolI18n.T("声明", "Declare"));
                declareBtn.Enable();

                declareBtn.Connect(
                    NClickableControl.SignalName.Released,
                    Callable.From<NButton>(_ =>
                    {
                        if (__instance.IsMarkedForRemoval)
                            return;

                        var holder = HolderField?.GetValue(__instance) as NPotionHolder;
                        var potion = holder?.Potion?.Model;
                        if (potion != null)
                            DeclarePotion(potion);

                        __instance.Remove();
                    }));

                var usePath = useBtn.GetPath();
                var declarePath = declareBtn.GetPath();
                var discardPath = discardBtn.GetPath();

                useBtn.FocusNeighborBottom = declarePath;
                declareBtn.FocusNeighborLeft = declarePath;
                declareBtn.FocusNeighborRight = declarePath;
                declareBtn.FocusNeighborTop = usePath;
                declareBtn.FocusNeighborBottom = discardPath;
                discardBtn.FocusNeighborTop = declarePath;
            }
            catch (Exception ex)
            {
                CoopQolModEntry.Log.Warn($"NPotionPopupReadyPatch error: {ex.Message}");
            }
        }

        private static void PositionButtonTopLeft(Control btn, Vector2 pos, Vector2 size)
        {
            btn.AnchorLeft = 0f;
            btn.AnchorTop = 0f;
            btn.AnchorRight = 0f;
            btn.AnchorBottom = 0f;
            btn.OffsetLeft = pos.X;
            btn.OffsetTop = pos.Y;
            btn.OffsetRight = pos.X + size.X;
            btn.OffsetBottom = pos.Y + size.Y;
            btn.Position = pos;
            btn.Size = size;
        }
    }

    [HarmonyPatch(typeof(NPotionPopup), nameof(NPotionPopup.Remove))]
    private static class NPotionPopupRemovePatch
    {
        private static readonly FieldInfo? ContainerField = AccessTools.Field(typeof(NPotionPopup), "_popupContainer");

        private static void Postfix(NPotionPopup __instance)
        {
            try
            {
                if (ContainerField?.GetValue(__instance) is not Control popupContainer)
                    return;

                var btn = popupContainer.GetNodeOrNull<NPotionPopupButton>("DeclareButton");
                if (btn != null && GodotObject.IsInstanceValid(btn))
                    btn.Disable();
            }
            catch
            {
                // Ignore during teardown
            }
        }
    }

    [HarmonyPatch(typeof(NPower), nameof(NPower._Ready))]
    private static class NPowerReadyPatch
    {
        private static void Postfix(NPower __instance)
        {
            try
            {
                if (__instance.HasMeta("CoopQolPowerWired"))
                    return;

                __instance.SetMeta("CoopQolPowerWired", true);
                __instance.GuiInput += evt => OnPowerGuiInput(__instance, evt);
                __instance.MouseEntered += () => _hoveredPower = __instance;
                __instance.MouseExited += () =>
                {
                    if (ReferenceEquals(_hoveredPower, __instance))
                        _hoveredPower = null;
                };
            }
            catch (Exception ex)
            {
                CoopQolModEntry.Log.Warn($"NPowerReadyPatch error: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(NCardHolder), nameof(NCardHolder._Ready))]
    private static class NCardHolderReadyPatch
    {
        private static void Postfix(NCardHolder __instance)
        {
            try
            {
                if (__instance.HasMeta("CoopQolCardHoverWired"))
                    return;

                __instance.SetMeta("CoopQolCardHoverWired", true);
                var hitbox = __instance.Hitbox;
                if (hitbox != null)
                {
                    hitbox.MouseEntered += () => SetHoveredCard(__instance.CardModel ?? __instance.CardNode?.Model);
                    hitbox.MouseExited += () => ClearHoveredCard(__instance.CardModel ?? __instance.CardNode?.Model);
                    hitbox.FocusEntered += () => SetHoveredCard(__instance.CardModel ?? __instance.CardNode?.Model);
                    hitbox.FocusExited += () => ClearHoveredCard(__instance.CardModel ?? __instance.CardNode?.Model);
                }
            }
            catch (Exception ex)
            {
                CoopQolModEntry.Log.Warn($"NCardHolderReadyPatch error: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(NCardHolder), "OnMousePressed")]
    private static class NCardHolderMousePressedPatch
    {
        private static void Postfix(NCardHolder __instance, InputEvent inputEvent)
        {
            try
            {
                var card = __instance.CardModel ?? __instance.CardNode?.Model;
                if (card != null)
                    NotifyCardPointerPress(card, __instance, inputEvent);
            }
            catch (Exception ex)
            {
                CoopQolModEntry.Log.Warn($"NCardHolderMousePressedPatch error: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(NDeckHistoryEntry), nameof(NDeckHistoryEntry._Ready))]
    private static class NDeckHistoryEntryReadyPatch
    {
        private static void Postfix(NDeckHistoryEntry __instance)
        {
            try
            {
                if (__instance.HasMeta("CoopQolCardWired"))
                    return;

                __instance.SetMeta("CoopQolCardWired", true);
                __instance.GuiInput += evt =>
                {
                    if (__instance.Card != null)
                        NotifyCardPointerPress(__instance.Card, __instance, evt);
                };
                __instance.MouseEntered += () => SetHoveredCard(__instance.Card);
                __instance.MouseExited += () => ClearHoveredCard(__instance.Card);
            }
            catch (Exception ex)
            {
                CoopQolModEntry.Log.Warn($"NDeckHistoryEntryReadyPatch error: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(NMerchantCard), nameof(NMerchantCard._Ready))]
    private static class NMerchantCardReadyPatch
    {
        private static readonly FieldInfo? CardNodeField = AccessTools.Field(typeof(NMerchantCard), "_cardNode");

        private static void Postfix(NMerchantCard __instance)
        {
            try
            {
                var hitbox = __instance.Hitbox;
                if (hitbox == null || hitbox.HasMeta("CoopQolCardWired"))
                    return;

                hitbox.SetMeta("CoopQolCardWired", true);
                hitbox.GuiInput += evt =>
                {
                    if (__instance.Entry?.IsStocked != true)
                        return;

                    var cardNode = CardNodeField?.GetValue(__instance) as NCard;
                    var card = cardNode?.Model;
                    if (card != null)
                        NotifyCardPointerPress(card, __instance, evt);
                };
                hitbox.MouseEntered += () =>
                {
                    if (__instance.Entry?.IsStocked == true)
                        SetHoveredCard((CardNodeField?.GetValue(__instance) as NCard)?.Model);
                };
                hitbox.MouseExited += () => ClearHoveredCard((CardNodeField?.GetValue(__instance) as NCard)?.Model);
            }
            catch (Exception ex)
            {
                CoopQolModEntry.Log.Warn($"NMerchantCardReadyPatch error: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(NInspectCardScreen), nameof(NInspectCardScreen._Ready))]
    private static class NInspectCardScreenReadyPatch
    {
        private static readonly FieldInfo? HoverTipRectField = AccessTools.Field(typeof(NInspectCardScreen), "_hoverTipRect");
        private static readonly FieldInfo? CardField = AccessTools.Field(typeof(NInspectCardScreen), "_card");

        private static void Postfix(NInspectCardScreen __instance)
        {
            try
            {
                if (HoverTipRectField?.GetValue(__instance) is not Control hoverTipRect
                    || hoverTipRect.HasMeta("CoopQolCardWired"))
                    return;

                hoverTipRect.SetMeta("CoopQolCardWired", true);
                hoverTipRect.GuiInput += evt =>
                {
                    var cardNode = CardField?.GetValue(__instance) as NCard;
                    var card = cardNode?.Model;
                    if (card != null)
                        NotifyCardPointerPress(card, hoverTipRect, evt);
                };
                hoverTipRect.MouseEntered += () => SetHoveredCard((CardField?.GetValue(__instance) as NCard)?.Model);
                hoverTipRect.MouseExited += () => ClearHoveredCard((CardField?.GetValue(__instance) as NCard)?.Model);
            }
            catch (Exception ex)
            {
                CoopQolModEntry.Log.Warn($"NInspectCardScreenReadyPatch error: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(NRelicInventoryHolder), nameof(NRelicInventoryHolder._Ready))]
    private static class NRelicInventoryHolderReadyPatch
    {
        private static void Postfix(NRelicInventoryHolder __instance)
        {
            try
            {
                if (__instance.HasMeta("CoopQolRelicWired"))
                    return;

                __instance.SetMeta("CoopQolRelicWired", true);
                __instance.GuiInput += evt => NotifyRelicPointerPress(__instance.Relic?.Model, __instance, evt);
                __instance.MouseEntered += () => SetHoveredRelic(__instance.Relic?.Model);
                __instance.MouseExited += () => ClearHoveredRelic(__instance.Relic?.Model);
                __instance.FocusEntered += () => SetHoveredRelic(__instance.Relic?.Model);
                __instance.FocusExited += () => ClearHoveredRelic(__instance.Relic?.Model);
            }
            catch (Exception ex)
            {
                CoopQolModEntry.Log.Warn($"NRelicInventoryHolderReadyPatch error: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(NRelicBasicHolder), nameof(NRelicBasicHolder._Ready))]
    private static class NRelicBasicHolderReadyPatch
    {
        private static void Postfix(NRelicBasicHolder __instance)
        {
            try
            {
                if (__instance.HasMeta("CoopQolRelicWired"))
                    return;

                __instance.SetMeta("CoopQolRelicWired", true);
                __instance.GuiInput += evt => NotifyRelicPointerPress(__instance.Relic?.Model, __instance, evt);
                __instance.MouseEntered += () => SetHoveredRelic(__instance.Relic?.Model);
                __instance.MouseExited += () => ClearHoveredRelic(__instance.Relic?.Model);
                __instance.FocusEntered += () => SetHoveredRelic(__instance.Relic?.Model);
                __instance.FocusExited += () => ClearHoveredRelic(__instance.Relic?.Model);
            }
            catch (Exception ex)
            {
                CoopQolModEntry.Log.Warn($"NRelicBasicHolderReadyPatch error: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(NMerchantRelic), nameof(NMerchantRelic._Ready))]
    private static class NMerchantRelicReadyPatch
    {
        private static readonly FieldInfo? RelicField = AccessTools.Field(typeof(NMerchantRelic), "_relic");

        private static void Postfix(NMerchantRelic __instance)
        {
            try
            {
                var hitbox = __instance.Hitbox;
                if (hitbox == null || hitbox.HasMeta("CoopQolRelicWired"))
                    return;

                hitbox.SetMeta("CoopQolRelicWired", true);
                hitbox.GuiInput += evt =>
                {
                    if (__instance.Entry?.IsStocked != true)
                        return;

                    if (RelicField?.GetValue(__instance) is RelicModel relic)
                        NotifyRelicPointerPress(relic, __instance, evt);
                };
                hitbox.MouseEntered += () =>
                {
                    if (__instance.Entry?.IsStocked == true && RelicField?.GetValue(__instance) is RelicModel relic)
                        SetHoveredRelic(relic);
                };
                hitbox.MouseExited += () => ClearHoveredRelic(RelicField?.GetValue(__instance) as RelicModel);
            }
            catch (Exception ex)
            {
                CoopQolModEntry.Log.Warn($"NMerchantRelicReadyPatch error: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(NInspectRelicScreen), nameof(NInspectRelicScreen._Ready))]
    private static class NInspectRelicScreenReadyPatch
    {
        private static readonly FieldInfo? HoverTipRectField = AccessTools.Field(typeof(NInspectRelicScreen), "_hoverTipRect");
        private static readonly FieldInfo? RelicsField = AccessTools.Field(typeof(NInspectRelicScreen), "_relics");
        private static readonly FieldInfo? IndexField = AccessTools.Field(typeof(NInspectRelicScreen), "_index");

        private static RelicModel? GetCurrentRelic(NInspectRelicScreen screen)
        {
            if (RelicsField?.GetValue(screen) is not IReadOnlyList<RelicModel> list) return null;
            if (IndexField?.GetValue(screen) is not int idx || idx < 0 || idx >= list.Count) return null;
            return list[idx];
        }

        private static void Postfix(NInspectRelicScreen __instance)
        {
            try
            {
                if (HoverTipRectField?.GetValue(__instance) is not Control hoverTipRect
                    || hoverTipRect.HasMeta("CoopQolRelicWired"))
                    return;

                hoverTipRect.SetMeta("CoopQolRelicWired", true);
                hoverTipRect.GuiInput += evt => NotifyRelicPointerPress(GetCurrentRelic(__instance), hoverTipRect, evt);
                hoverTipRect.MouseEntered += () => SetHoveredRelic(GetCurrentRelic(__instance));
                hoverTipRect.MouseExited += () => ClearHoveredRelic(GetCurrentRelic(__instance));
            }
            catch (Exception ex)
            {
                CoopQolModEntry.Log.Warn($"NInspectRelicScreenReadyPatch error: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(NPotionHolder), nameof(NPotionHolder._Ready))]
    private static class NPotionHolderReadyPatch
    {
        private static void Postfix(NPotionHolder __instance)
        {
            try
            {
                if (__instance.HasMeta("CoopQolPotionWired"))
                    return;

                __instance.SetMeta("CoopQolPotionWired", true);
                __instance.MouseEntered += () => SetHoveredPotion(__instance.Potion?.Model);
                __instance.MouseExited += () => ClearHoveredPotion(__instance.Potion?.Model);
                __instance.FocusEntered += () => SetHoveredPotion(__instance.Potion?.Model);
                __instance.FocusExited += () => ClearHoveredPotion(__instance.Potion?.Model);
            }
            catch (Exception ex)
            {
                CoopQolModEntry.Log.Warn($"NPotionHolderReadyPatch error: {ex.Message}");
            }
        }
    }
}
