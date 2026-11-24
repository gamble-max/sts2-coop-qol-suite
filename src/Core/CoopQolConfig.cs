using Sts2Mods.Shared;

namespace CoopQolSuite.Core;

public sealed class CoopQolConfig
{
    private const string ModId = "CoopQolSuite";

    public bool EnableSpectate { get; set; } = true;
    public bool AutoSpectateOnEndTurn { get; set; } = false;

    public bool EnableStats { get; set; } = true;
    public bool StatsVisible { get; set; } = false;
    public int StatsViewMode { get; set; } = 0; // 0 = Combat, 1 = Run
    public int StatsDisplayState { get; set; } = 1; // 0 = Expanded, 1 = Compact, 2 = Collapsed
    public float StatsPosX { get; set; } = -1f;
    public float StatsPosY { get; set; } = -1f;

    public bool EnableQuickSl { get; set; } = true;
    public bool ConfirmBeforeQuickSl { get; set; } = true;
    public bool ShowTopBarButtons { get; set; } = true;
    public bool HudMinimized { get; set; } = false;
    public int ActiveHudTab { get; set; } = -1; // -1 = collapsed, 0 = Quick, 1 = Chat, 2 = Events, 3 = Stats
    public float QuickBarPosX { get; set; } = -1f;
    public float QuickBarPosY { get; set; } = -1f;

    public bool EnableTerminal { get; set; } = true;
    public bool TerminalVisible { get; set; } = false;
    public bool ShowBigScreenDanmaku { get; set; } = true;
    public float TerminalPosX { get; set; } = -1f;
    public float TerminalPosY { get; set; } = -1f;

    // 4-category chronological event stream toggles
    public bool FilterMajor { get; set; } = true;
    public bool FilterMinor { get; set; } = false;
    public bool FilterDamageStatus { get; set; } = true;
    public bool FilterNegative { get; set; } = true;

    // Key used to declare currently hovered card/relic/potion/status
    public string DeclareHoverKey { get; set; } = "G";

    public static CoopQolConfig Current { get; private set; } = new();

    public static void Load()
    {
        Current = ModConfigBridge.LoadJsonConfig(ModId, new CoopQolConfig());
    }

    public static void Save()
    {
        ModConfigBridge.SaveJsonConfigDeferred(ModId, Current);
    }
}
