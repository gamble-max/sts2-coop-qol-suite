using System;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Platform;

namespace CoopQolSuite.Core;

public enum SpireButtonTone
{
    Normal = 0,
    GoldAccent = 1,
    SlAccent = 2,
    Danger = 3,
    ListItem = 4,
}

public static class NativeUiHelper
{
    private const string BtnLabelName = "SpireBtnLabel";

    // Authentic STS2 vanilla palette (extracted from rewards_screen.tscn / reward_button.tscn / ping_button.tscn)
    public static readonly Color SpireCream = new(1f, 0.9647f, 0.8863f, 1f);          // #FFF6E2
    public static readonly Color SpireGold = new(0.969f, 0.847f, 0.486f, 1f);          // #F7D87C
    public static readonly Color SpireTealOutline = new(0.075f, 0.196f, 0.220f, 1f);   // #133238
    public static readonly Color SpireBannerOutline = new(0.290f, 0.235f, 0.165f, 0.85f);
    public static readonly Color SpireSlateBg = new(0.078f, 0.137f, 0.169f, 0.96f);    // #14232B
    public static readonly Color SpireWellBg = new(0.039f, 0.086f, 0.110f, 0.90f);     // #0A161C
    public static readonly Color SpireWellBorder = new(0.165f, 0.302f, 0.357f, 0.95f); // #2A4D5B
    public static readonly Color SpireText = SpireCream;
    public static readonly Color SpireMutedText = new(0.56f, 0.65f, 0.69f, 1f);

    // Backwards-compatible aliases
    public static readonly Color SpireBrass = SpireWellBorder;
    public static readonly Color SpireGoldBorder = new(0.32f, 0.56f, 0.63f, 1f);
    public static readonly Color SpireBtnBg = new(0.11f, 0.21f, 0.25f, 0.96f);
    public static readonly Color SpireBtnHoverBg = new(0.15f, 0.28f, 0.33f, 0.98f);
    public static readonly Color SpireBtnActiveBg = new(0.12f, 0.36f, 0.40f, 0.98f);

    private static string _cachedLang = string.Empty;
    private static Font? _cachedRegularFont;
    private static Font? _cachedKreonBold;

    private static bool _texturesLoaded;
    private static Texture2D? _stonePanelTex;
    private static Texture2D? _jadeBtnTex;
    private static Texture2D? _slateBtnTex;
    private static Texture2D? _rewardBannerTex;

    private static StyleBox? _cachedPanelStyle;
    private static StyleBox? _cachedModalStyle;
    private static StyleBox? _cachedJadeNormal;
    private static StyleBox? _cachedJadeHover;
    private static StyleBox? _cachedJadePressed;
    private static StyleBox? _cachedSlateNormal;
    private static StyleBox? _cachedSlateHover;
    private static StyleBox? _cachedSlatePressed;
    private static StyleBox? _cachedSlateDisabled;
    private static StyleBox? _cachedDangerNormal;

    private static readonly StyleBoxFlat SharedWellStyle = CreateFlatBox(SpireWellBg, SpireWellBorder, 2, 6, 8, 6);
    private static readonly StyleBoxFlat SharedLogStyle = CreateFlatBox(SpireWellBg, SpireWellBorder, 2, 6, 8, 6);
    private static readonly StyleBoxFlat SharedInputNormal = CreateFlatBox(new Color(0.03f, 0.07f, 0.09f, 0.95f), SpireWellBorder, 2, 5, 10, 6);
    private static readonly StyleBoxFlat SharedInputFocus = CreateFlatBox(new Color(0.03f, 0.07f, 0.09f, 0.98f), new Color(0.35f, 0.68f, 0.75f, 1f), 2, 5, 10, 6);

    public static bool IsInActiveRun()
    {
        return NRun.Instance != null
            && GodotObject.IsInstanceValid(NRun.Instance)
            && NRun.Instance.IsInsideTree();
    }

    private static void EnsureTexturesLoaded()
    {
        if (_texturesLoaded) return;
        _texturesLoaded = true;

        _stonePanelTex = TryLoadTex("res://images/packed/common_ui/settings_tab_selected.png");
        _jadeBtnTex = TryLoadTex("res://images/ui/reward_screen/reward_item_button.png");
        _slateBtnTex = TryLoadTex("res://images/packed/common_ui/event_button.png");
        _rewardBannerTex = TryLoadTex("res://images/ui/reward_screen/reward_banner.png");
    }

    private static Texture2D? TryLoadTex(string path)
    {
        try
        {
            return ResourceLoader.Load<Texture2D>(path);
        }
        catch
        {
            return null;
        }
    }

    public static Texture2D? GetRewardBannerTexture()
    {
        EnsureTexturesLoaded();
        return _rewardBannerTex;
    }

    public static Font? GetKreonBaseFont()
    {
        if (_cachedKreonBold != null) return _cachedKreonBold;
        try { _cachedKreonBold = ResourceLoader.Load<Font>("res://themes/kreon_bold_shared.tres"); } catch { }
        return _cachedKreonBold;
    }

    public static void InvalidateFontCache()
    {
        _cachedLang = string.Empty;
        _cachedRegularFont = null;
    }

    public static Font? GetGameFont(bool bold = false)
    {
        string lang = "zhs";
        try
        {
            var current = LocManager.Instance?.Language;
            if (!string.IsNullOrWhiteSpace(current))
                lang = current;
        }
        catch
        {
            lang = "zhs";
        }

        if (!string.Equals(_cachedLang, lang, StringComparison.Ordinal))
        {
            _cachedLang = lang;
            _cachedRegularFont = null;
        }

        if (_cachedRegularFont != null)
            return _cachedRegularFont;

        Font? loaded = null;
        try
        {
            // Vanilla MegaLabel/MegaRichTextLabel uses FontType.Regular (NotoSansMonoCJKsc for zhs)
            loaded = FontManager.GetSubstituteFont(lang, FontType.Regular);
        }
        catch
        {
            loaded = null;
        }

        if (loaded == null)
        {
            try
            {
                string path = lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                    ? "res://themes/fonts/zhs/noto_sans_mono_cjksc_regular_shared.tres"
                    : "res://themes/kreon_bold_shared.tres";
                loaded = ResourceLoader.Load<Font>(path);
            }
            catch
            {
                loaded = null;
            }
        }

        loaded ??= GetKreonBaseFont();
        _cachedRegularFont = loaded;
        return loaded;
    }

    /// <summary>
    /// Creates a vanilla STS2 MegaLabel with font override pre-populated before tree entry.
    /// </summary>
    public static MegaLabel CreateMegaLabel(
        string text,
        int fontSize,
        Color? color = null,
        HorizontalAlignment hAlign = HorizontalAlignment.Center,
        int outlineSize = 6,
        Color? outlineColor = null)
    {
        var lbl = new MegaLabel
        {
            AutoSizeEnabled = false,
            MinFontSize = Math.Max(10, fontSize - 4),
            MaxFontSize = fontSize,
            Text = text,
            HorizontalAlignment = hAlign,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };

        var font = GetGameFont() ?? GetKreonBaseFont();
        if (font != null)
            lbl.AddThemeFontOverride(ThemeConstants.Label.Font, font);

        try
        {
            FontControlUtils.ApplyLocaleFontSubstitution(lbl, FontType.Regular, ThemeConstants.Label.Font);
        }
        catch
        {
            // Keep pre-assigned font fallback if LocManager isn't ready
        }

        lbl.AddThemeFontSizeOverride(ThemeConstants.Label.FontSize, fontSize);
        lbl.AddThemeColorOverride(ThemeConstants.Label.FontColor, color ?? SpireCream);
        if (outlineSize > 0)
        {
            lbl.AddThemeConstantOverride(ThemeConstants.Label.OutlineSize, outlineSize);
            lbl.AddThemeColorOverride(ThemeConstants.Label.FontOutlineColor, outlineColor ?? SpireTealOutline);
        }

        return lbl;
    }

    public static void ApplyFont(
        Control control,
        int fontSize,
        bool bold = false,
        int outlineSize = 5,
        Color? outlineColor = null)
    {
        var font = GetGameFont(bold) ?? GetKreonBaseFont();
        if (font != null)
            control.AddThemeFontOverride("font", font);

        try
        {
            FontControlUtils.ApplyLocaleFontSubstitution(control, FontType.Regular, ThemeConstants.Label.Font);
        }
        catch
        {
            // Ignore when LocManager unavailable
        }

        control.AddThemeFontSizeOverride("font_size", fontSize);

        if (outlineSize > 0)
        {
            control.AddThemeConstantOverride("outline_size", outlineSize);
            control.AddThemeColorOverride("font_outline_color", outlineColor ?? SpireTealOutline);
        }
    }

    public static void ApplyRichTextFont(RichTextLabel rtl, int fontSize, int outlineSize = 4)
    {
        var font = GetGameFont() ?? GetKreonBaseFont();
        if (font != null)
        {
            rtl.AddThemeFontOverride("normal_font", font);
            rtl.AddThemeFontOverride("bold_font", font);
        }

        try
        {
            FontControlUtils.ApplyLocaleFontSubstitution(rtl, FontType.Regular, ThemeConstants.RichTextLabel.NormalFont);
            FontControlUtils.ApplyLocaleFontSubstitution(rtl, FontType.Regular, ThemeConstants.RichTextLabel.BoldFont);
        }
        catch
        {
            // Keep fallback
        }

        rtl.AddThemeFontSizeOverride("normal_font_size", fontSize);
        rtl.AddThemeFontSizeOverride("bold_font_size", fontSize);
        rtl.AddThemeColorOverride("default_color", SpireCream);
        rtl.AddThemeConstantOverride("line_separation", 2);

        if (outlineSize > 0)
        {
            rtl.AddThemeConstantOverride("outline_size", outlineSize);
            rtl.AddThemeColorOverride("font_outline_color", SpireTealOutline);
        }
    }

    public static void ApplyFont(Control control, int fontSize, string fontProp)
    {
        if (control is RichTextLabel rtl && fontProp == "normal_font_size")
            ApplyRichTextFont(rtl, fontSize, outlineSize: 4);
        else
            ApplyFont(control, fontSize, bold: false, outlineSize: 5);
    }

    public static StyleBox CreateSpirePanelStyle(float bgAlpha = 0.95f, Color? borderColor = null, int cornerRadius = 7)
    {
        if (borderColor == null && _cachedPanelStyle != null)
            return _cachedPanelStyle;

        var panelBg = new Color(0.051f, 0.102f, 0.133f, bgAlpha); // #0D1A22
        var border = borderColor ?? new Color(0.243f, 0.451f, 0.522f, 1f); // #3E7385
        var box = CreateFlatBox(panelBg, border, 2, cornerRadius, 12, 10);
        if (borderColor == null)
            _cachedPanelStyle = box;
        return box;
    }

    public static StyleBox GetModalPanelStyle()
    {
        if (_cachedModalStyle != null) return _cachedModalStyle;
        _cachedModalStyle = CreateFlatBox(
            new Color(0.047f, 0.094f, 0.125f, 0.97f),
            new Color(0.267f, 0.494f, 0.573f, 1f),
            2, 8, 16, 12);
        return _cachedModalStyle;
    }

    public static StyleBoxFlat GetSubCardStyle() => SharedWellStyle;
    public static StyleBoxFlat GetHeaderBarStyle() => SharedWellStyle;
    public static StyleBoxFlat GetLogPanelStyle() => SharedLogStyle;

    private static StyleBoxTexture CreateTexBox(
        Texture2D tex,
        float mLeft,
        float mTop,
        float mRight,
        float mBottom,
        float padX,
        float padY,
        Color modulate)
    {
        return new StyleBoxTexture
        {
            Texture = tex,
            TextureMarginLeft = mLeft,
            TextureMarginTop = mTop,
            TextureMarginRight = mRight,
            TextureMarginBottom = mBottom,
            ContentMarginLeft = padX,
            ContentMarginRight = padX,
            ContentMarginTop = padY,
            ContentMarginBottom = padY,
            ModulateColor = modulate,
        };
    }

    private static StyleBoxFlat CreateFlatBox(Color bg, Color border, int borderW, int radius, int padX, int padY)
    {
        return new StyleBoxFlat
        {
            BgColor = bg,
            BorderColor = border,
            BorderWidthLeft = borderW,
            BorderWidthTop = borderW,
            BorderWidthRight = borderW,
            BorderWidthBottom = borderW,
            CornerRadiusTopLeft = radius,
            CornerRadiusTopRight = radius,
            CornerRadiusBottomLeft = radius,
            CornerRadiusBottomRight = radius,
            ContentMarginLeft = padX,
            ContentMarginRight = padX,
            ContentMarginTop = padY,
            ContentMarginBottom = padY,
            ShadowSize = 0,
        };
    }

    private static StyleBox? _cachedSlNormal;

    private static void EnsureButtonStyleBoxes()
    {
        if (_cachedSlateNormal != null) return;

        _cachedJadeNormal = CreateFlatBox(
            new Color(0.094f, 0.286f, 0.329f, 0.98f), // #184954
            new Color(0.875f, 0.741f, 0.392f, 1f),    // #DFBD64
            2, 5, 8, 4);
        _cachedJadeHover = CreateFlatBox(
            new Color(0.122f, 0.353f, 0.404f, 1f),    // #1F5A67
            new Color(1.000f, 0.878f, 0.510f, 1f),    // #FFE082
            2, 5, 8, 4);
        _cachedJadePressed = CreateFlatBox(
            new Color(0.071f, 0.220f, 0.255f, 1f),
            new Color(0.875f, 0.741f, 0.392f, 1f),
            2, 5, 8, 4);

        _cachedSlateNormal = CreateFlatBox(
            new Color(0.078f, 0.157f, 0.204f, 0.96f), // #142834
            new Color(0.224f, 0.412f, 0.478f, 1f),    // #39697A
            2, 5, 6, 4);
        _cachedSlateHover = CreateFlatBox(
            new Color(0.106f, 0.212f, 0.275f, 0.98f), // #1B3646
            new Color(0.416f, 0.816f, 0.871f, 1f),    // #6AD0DE
            2, 5, 6, 4);
        _cachedSlatePressed = CreateFlatBox(
            new Color(0.055f, 0.114f, 0.149f, 0.98f),
            new Color(0.416f, 0.816f, 0.871f, 1f),
            2, 5, 6, 4);
        _cachedSlateDisabled = CreateFlatBox(
            new Color(0.055f, 0.102f, 0.129f, 0.65f), // #0E1A21
            new Color(0.137f, 0.231f, 0.267f, 0.65f), // #233B44
            2, 5, 6, 4);

        _cachedSlNormal = CreateFlatBox(
            new Color(0.106f, 0.161f, 0.200f, 0.96f),
            new Color(0.788f, 0.518f, 0.243f, 1f),    // #C9843E
            2, 5, 6, 4);
        _cachedDangerNormal = CreateFlatBox(
            new Color(0.173f, 0.086f, 0.094f, 0.96f), // #2C1618
            new Color(0.722f, 0.290f, 0.259f, 1f),    // #B84A42
            2, 5, 6, 4);
    }

    public static void InitSpireTabButton(Button btn, int fontSize = 14)
    {
        InitSpireButton(btn, fontSize, tone: SpireButtonTone.Normal);
    }

    public static void SetSpireTabActive(Button btn, bool isActive)
    {
        SetSpireButtonActive(btn, isActive);
    }

    public static void InitSpireButton(
        Button btn,
        int fontSize = 13,
        bool isSlAccent = false,
        SpireButtonTone tone = SpireButtonTone.Normal,
        bool bold = true)
    {
        if (isSlAccent && tone == SpireButtonTone.Normal)
            tone = SpireButtonTone.SlAccent;

        EnsureButtonStyleBoxes();
        btn.FocusMode = Control.FocusModeEnum.None;

        string initialText = btn.Text ?? string.Empty;
        btn.Text = string.Empty;

        bool isJade = tone == SpireButtonTone.GoldAccent;
        bool isList = tone == SpireButtonTone.ListItem;

        var normalBox = tone switch
        {
            SpireButtonTone.GoldAccent => _cachedJadeNormal!,
            SpireButtonTone.SlAccent => _cachedSlNormal!,
            SpireButtonTone.Danger => _cachedDangerNormal!,
            _ => _cachedSlateNormal!,
        };

        btn.AddThemeStyleboxOverride("normal", normalBox);
        btn.AddThemeStyleboxOverride("hover", isJade ? _cachedJadeHover! : _cachedSlateHover!);
        btn.AddThemeStyleboxOverride("pressed", isJade ? _cachedJadePressed! : _cachedSlatePressed!);
        btn.AddThemeStyleboxOverride("focus", isJade ? _cachedJadeHover! : _cachedSlateHover!);
        btn.AddThemeStyleboxOverride("disabled", _cachedSlateDisabled!);

        // Keep native Button text transparent so only our centered MegaLabel renders
        btn.AddThemeColorOverride("font_color", Colors.Transparent);
        btn.AddThemeColorOverride("font_hover_color", Colors.Transparent);
        btn.AddThemeColorOverride("font_pressed_color", Colors.Transparent);
        btn.AddThemeColorOverride("font_disabled_color", Colors.Transparent);

        var existing = btn.GetNodeOrNull<MegaLabel>(BtnLabelName);
        if (existing == null)
        {
            var textColor = tone switch
            {
                SpireButtonTone.GoldAccent => SpireGold,
                SpireButtonTone.SlAccent => new Color(1f, 0.87f, 0.66f, 1f),
                SpireButtonTone.Danger => new Color(1f, 0.78f, 0.74f, 1f),
                _ => SpireCream,
            };

            var lbl = CreateMegaLabel(
                initialText,
                fontSize,
                textColor,
                isList ? HorizontalAlignment.Left : HorizontalAlignment.Center,
                outlineSize: isList ? 4 : 6);
            lbl.Name = BtnLabelName;
            lbl.ClipText = true;
            lbl.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            if (isList)
            {
                lbl.OffsetLeft = 14f;
                lbl.OffsetRight = -12f;
            }
            else
            {
                lbl.OffsetLeft = 4f;
                lbl.OffsetRight = -4f;
            }
            btn.AddChild(lbl);
        }
        else
        {
            existing.Text = initialText;
        }
    }

    public static void SetButtonText(Button btn, string text)
    {
        var lbl = btn.GetNodeOrNull<MegaLabel>(BtnLabelName);
        if (lbl != null && GodotObject.IsInstanceValid(lbl))
        {
            if (!string.Equals(lbl.Text, text, StringComparison.Ordinal))
                lbl.Text = text;
        }
        else
        {
            btn.Text = text;
        }
    }

    public static void SetSpireButtonActive(Button btn, bool isActive)
    {
        EnsureButtonStyleBoxes();
        btn.AddThemeStyleboxOverride("normal", isActive ? _cachedJadeNormal! : _cachedSlateNormal!);
        btn.AddThemeStyleboxOverride("hover", isActive ? _cachedJadeHover! : _cachedSlateHover!);
        btn.AddThemeStyleboxOverride("pressed", isActive ? _cachedJadePressed! : _cachedSlatePressed!);

        var lbl = btn.GetNodeOrNull<MegaLabel>(BtnLabelName);
        if (lbl != null && GodotObject.IsInstanceValid(lbl))
            lbl.AddThemeColorOverride(ThemeConstants.Label.FontColor, isActive ? SpireGold : SpireCream);
    }

    public static void StyleSpireButton(Button btn, int fontSize = 13, bool isActive = false, Color? accentColor = null)
    {
        InitSpireButton(btn, fontSize, isSlAccent: accentColor.HasValue);
        if (isActive)
            SetSpireButtonActive(btn, true);
    }

    public static LineEdit CreateSpireLineEdit(string placeholder, int fontSize = 13)
    {
        var edit = new LineEdit
        {
            PlaceholderText = placeholder,
        };
        StyleSpireLineEdit(edit, fontSize);
        return edit;
    }

    public static void StyleSpireLineEdit(LineEdit edit, int fontSize = 13)
    {
        ApplyFont(edit, fontSize, bold: false, outlineSize: 3);
        edit.AddThemeStyleboxOverride("normal", SharedInputNormal);
        edit.AddThemeStyleboxOverride("focus", SharedInputFocus);
        edit.AddThemeColorOverride("font_color", SpireCream);
        edit.AddThemeColorOverride("font_placeholder_color", SpireMutedText);
        edit.AddThemeColorOverride("caret_color", SpireGold);

        edit.Editable = true;
        edit.FocusMode = Control.FocusModeEnum.All;
        edit.CaretBlink = true;
        edit.CaretBlinkInterval = 0.5f;
        edit.VirtualKeyboardEnabled = true;
        edit.VirtualKeyboardShowOnFocus = true;
        edit.ContextMenuEnabled = true;
        edit.SelectingEnabled = true;

        if (!edit.HasMeta("CoopQolLineEditWired"))
        {
            edit.SetMeta("CoopQolLineEditWired", true);
            edit.GuiInput += evt =>
            {
                if (evt is InputEventScreenTouch { Pressed: true })
                {
                    ActivateLineEdit(edit, fromTouch: true);
                }
                else if (evt is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
                {
                    ActivateLineEdit(edit, fromTouch: false);
                }
            };
            edit.EditingToggled += toggledOn => ApplyImeState(edit, toggledOn);
            edit.FocusExited += () =>
            {
                if (GodotObject.IsInstanceValid(edit) && !edit.IsEditing())
                {
                    ApplyImeState(edit, false);
                    try { DisplayServer.VirtualKeyboardHide(); } catch { }
                }
            };
        }
    }

    public static void ActivateLineEdit(LineEdit edit, bool fromTouch = false)
    {
        if (!GodotObject.IsInstanceValid(edit) || !edit.IsVisibleInTree())
            return;

        // Godot 4.5 requires Edit() BEFORE GrabFocus() so NOTIFICATION_FOCUS_ENTER
        // sees editing == true and activates OS IME (ImmAssociateContext).
        if (!edit.IsEditing())
            edit.Edit();
        if (!edit.HasFocus())
            edit.GrabFocus();

        ApplyImeState(edit, true);
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(edit) && edit.IsVisibleInTree() && (edit.IsEditing() || edit.HasFocus()))
                ApplyImeState(edit, true);
        }).CallDeferred();

        try
        {
            if (Steamworks.SteamUtils.IsSteamRunningOnSteamDeck())
                PlatformUtil.OpenVirtualKeyboard();
        }
        catch { }

        if (fromTouch)
        {
            try
            {
                DisplayServer.VirtualKeyboardShow(
                    edit.Text ?? string.Empty,
                    edit.GetGlobalRect(),
                    DisplayServer.VirtualKeyboardType.Default,
                    edit.MaxLength,
                    edit.CaretColumn,
                    edit.CaretColumn);
            }
            catch { }
        }
    }

    public static void ApplyImeState(LineEdit edit, bool active)
    {
        if (!GodotObject.IsInstanceValid(edit))
            return;

        try
        {
            if (active)
            {
                var rect = edit.GetGlobalRect();
                var imePos = new Vector2I((int)rect.Position.X, (int)(rect.Position.Y + rect.Size.Y));
                DisplayServer.WindowSetImePosition(imePos, 0);
                DisplayServer.WindowSetImeActive(true, 0);
            }
            else
            {
                DisplayServer.WindowSetImeActive(false, 0);
            }
        }
        catch { }
    }

    /// <summary>
    /// Shows STS2's genuine NVerticalPopup inside NModalContainer (identical frame/buttons/backstop to vanilla).
    /// </summary>
    public static void ShowNativeConfirmPopup(
        string title,
        string body,
        string yesText,
        string noText,
        Action onConfirm,
        Action? onCancel = null)
    {
        try
        {
            var modalContainer = NModalContainer.Instance;
            if (modalContainer != null)
            {
                if (modalContainer.OpenModal is CoopConfirmModal)
                    modalContainer.Clear();

                if (modalContainer.OpenModal == null)
                {
                    var scenePath = SceneHelper.GetScenePath("ui/vertical_popup");
                    PackedScene? scene = null;
                    try { scene = PreloadManager.Cache.GetScene(scenePath); } catch { }
                    scene ??= ResourceLoader.Load<PackedScene>(scenePath);

                    if (scene != null)
                    {
                        var modal = new CoopConfirmModal { Name = "CoopQolConfirmModal" };
                        var popup = scene.Instantiate<NVerticalPopup>(PackedScene.GenEditState.Disabled);
                        modal.Setup(popup, title, body, yesText, noText, onConfirm, onCancel);
                        popup.Ready += modal.ConfigureNow;

                        modalContainer.Add(modal, showBackstop: true);
                        Callable.From(modal.ConfigureNow).CallDeferred();
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            CoopQolModEntry.Log.Warn($"ShowNativeConfirmPopup fallback triggered: {ex.Message}");
        }

        ShowSpireFallbackConfirmPopup(title, body, yesText, noText, onConfirm, onCancel);
    }

    private static void ShowSpireFallbackConfirmPopup(
        string title,
        string body,
        string yesText,
        string noText,
        Action onConfirm,
        Action? onCancel)
    {
        var root = NGame.Instance?.GetTree()?.Root;
        if (root == null) return;

        var backdrop = new ColorRect
        {
            Name = "CoopQolFallbackConfirmModal",
            Color = new Color(0f, 0f, 0f, 0.65f),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        backdrop.AddChild(center);

        var panel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(460, 200),
        };
        panel.AddThemeStyleboxOverride("panel", GetModalPanelStyle());
        center.AddChild(panel);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 12);
        panel.AddChild(vbox);

        var titleLbl = CreateMegaLabel(title, 20, SpireGold, HorizontalAlignment.Center, outlineSize: 6);
        vbox.AddChild(titleLbl);

        var bodyLbl = CreateMegaLabel(body, 15, SpireCream, HorizontalAlignment.Center, outlineSize: 4);
        bodyLbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        vbox.AddChild(bodyLbl);

        var btnRow = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center,
        };
        btnRow.AddThemeConstantOverride("separation", 20);
        vbox.AddChild(btnRow);

        var noBtn = new Button
        {
            Text = noText,
            CustomMinimumSize = new Vector2(120, 40),
        };
        InitSpireButton(noBtn, 15);
        noBtn.Pressed += () =>
        {
            backdrop.QueueFree();
            onCancel?.Invoke();
        };
        btnRow.AddChild(noBtn);

        var yesBtn = new Button
        {
            Text = yesText,
            CustomMinimumSize = new Vector2(120, 40),
        };
        InitSpireButton(yesBtn, 15, tone: SpireButtonTone.GoldAccent);
        yesBtn.Pressed += () =>
        {
            backdrop.QueueFree();
            onConfirm?.Invoke();
        };
        btnRow.AddChild(yesBtn);

        root.AddChild(backdrop);
    }

    private sealed partial class CoopConfirmModal : Control, IScreenContext
    {
        private NVerticalPopup? _popup;
        private string _title = string.Empty;
        private string _body = string.Empty;
        private string _yesText = string.Empty;
        private string _noText = string.Empty;
        private Action? _onConfirm;
        private Action? _onCancel;
        private bool _configured;

        public Control? DefaultFocusedControl => _popup?.NoButton ?? (Control?)_popup;

        public void Setup(
            NVerticalPopup popup,
            string title,
            string body,
            string yesText,
            string noText,
            Action onConfirm,
            Action? onCancel)
        {
            _popup = popup;
            _title = title;
            _body = body;
            _yesText = yesText;
            _noText = noText;
            _onConfirm = onConfirm;
            _onCancel = onCancel;
            _configured = false;

            SetAnchorsPreset(LayoutPreset.FullRect);
            MouseFilter = MouseFilterEnum.Ignore;
            AddChild(popup);
        }

        public void ConfigureNow()
        {
            if (_configured || _popup == null || !GodotObject.IsInstanceValid(_popup))
                return;

            try
            {
                if (!_popup.IsInsideTree())
                    return;

                _configured = true;
                _popup.SetText(_title, _body);
                _popup.InitYesButton(
                    new LocString("main_menu_ui", "GENERIC_POPUP.confirm"),
                    _ => _onConfirm?.Invoke());
                _popup.YesButton.SetText(_yesText);

                _popup.InitNoButton(
                    new LocString("main_menu_ui", "GENERIC_POPUP.cancel"),
                    _ => _onCancel?.Invoke());
                _popup.NoButton.SetText(_noText);
            }
            catch (Exception ex)
            {
                CoopQolModEntry.Log.Warn($"CoopConfirmModal.ConfigureNow error: {ex.Message}");
            }
        }
    }
}
