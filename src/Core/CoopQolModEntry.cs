using System;
using System.Reflection;
using CoopQolSuite.GodMode;
using CoopQolSuite.QuickSl;
using CoopQolSuite.Spectate;
using CoopQolSuite.Stats;
using CoopQolSuite.Terminal;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using Sts2Mods.Shared;

namespace CoopQolSuite.Core;

[ModInitializer(nameof(Init))]
public static class CoopQolModEntry
{
    public static readonly ModLog Log = new("CoopQolSuite");
    private static Control? _rootControl;
    private static double _visCheckAccum;
    private static bool _lastRootVisible;

    public static void Init()
    {
        CoopQolConfig.Load();
        var harmony = new Harmony("sts2mods.coopqolsuite");

        int ok = 0;
        int failed = 0;
        foreach (var type in AccessTools.GetTypesFromAssembly(Assembly.GetExecutingAssembly()))
        {
            if (type.GetCustomAttribute<HarmonyPatch>() == null)
                continue;

            try
            {
                harmony.CreateClassProcessor(type).Patch();
                ok++;
            }
            catch (Exception ex)
            {
                failed++;
                Log.Error($"Harmony patch class {type.FullName} failed: {ex.Message}");
            }
        }

        Log.Info($"CoopQolSuite v1.2.0 loaded (patches ok={ok}, failed={failed}, ModConfig={ModConfigBridge.IsModConfigLoaded()}).");

        Callable.From(() =>
        {
            if (NGame.Instance != null && GodotObject.IsInstanceValid(NGame.Instance))
                EnsureUiInitialized(NGame.Instance);
        }).CallDeferred();
    }

    public static void EnsureUiInitialized(NGame game)
    {
        if (_rootControl != null && GodotObject.IsInstanceValid(_rootControl))
            return;

        try
        {
            _rootControl = new Control
            {
                Name = "CoopQolRoot",
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Visible = false,
            };
            _rootControl.SetAnchorsPreset(Control.LayoutPreset.FullRect);

            void AttachRoot()
            {
                if (!GodotObject.IsInstanceValid(game) || !GodotObject.IsInstanceValid(_rootControl))
                    return;

                if (_rootControl.GetParent() == null)
                    game.AddChild(_rootControl);

                if (game.RootSceneContainer != null && GodotObject.IsInstanceValid(game.RootSceneContainer))
                    game.MoveChild(_rootControl, game.RootSceneContainer.GetIndex() + 1);
            }

            if (game.IsInsideTree())
                AttachRoot();
            else
                Callable.From(AttachRoot).CallDeferred();

            if (CoopQolConfig.Current.EnableSpectate)
                SpectateModule.Initialize(_rootControl);
            if (CoopQolConfig.Current.EnableStats)
                CombatStatsModule.Initialize();
            if (CoopQolConfig.Current.EnableQuickSl)
                QuickSlModule.Initialize(_rootControl);
            if (CoopQolConfig.Current.EnableTerminal)
                UnifiedTerminalModule.Initialize(_rootControl);

            TopBarQuickBar.Initialize(_rootControl);
            GodModeModule.Initialize(_rootControl);

            try
            {
                if (MegaCrit.Sts2.Core.Nodes.Debug.NDevConsole.InstanceOrNull == null)
                {
                    var consoleLayer = MegaCrit.Sts2.Core.Nodes.Debug.NDevConsole.Create();
                    if (consoleLayer != null)
                        game.AddChild(consoleLayer);
                }
            }
            catch (Exception cex)
            {
                Log.Warn($"Optional NDevConsole pre-init skipped: {cex.Message}");
            }

            if (NativeUiHelper.IsInActiveRun())
                NotifyRunEntered();

            Log.Info("CoopQolSuite v1.2.0 UI modules attached to NGame.");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to initialize CoopQolSuite UI: {ex}");
        }
    }

    private static void DispatchProcess(double delta)
    {
        try
        {
            _visCheckAccum += delta;
            if (_visCheckAccum >= 0.15)
            {
                _visCheckAccum = 0;
                if (_rootControl != null && GodotObject.IsInstanceValid(_rootControl))
                {
                    bool showRoot = NativeUiHelper.IsInActiveRun()
                        && NCapstoneContainer.Instance?.InUse != true;
                    if (showRoot != _lastRootVisible)
                    {
                        _lastRootVisible = showRoot;
                        _rootControl.Visible = showRoot;
                    }
                }
            }

            TopBarQuickBar.Tick(delta);
            QuickSlModule.Tick(delta);
            UnifiedTerminalModule.Tick(delta);
            SpectateModule.Tick(delta);
        }
        catch (Exception ex)
        {
            Log.Warn($"DispatchProcess error: {ex.Message}");
        }
    }

    private static void NotifyRunEntered()
    {
        PlayerNameHelper.ResetCache();
        if (_rootControl != null && GodotObject.IsInstanceValid(_rootControl))
        {
            _lastRootVisible = true;
            _rootControl.Visible = true;
        }

        if (CoopQolConfig.Current.EnableSpectate)
            SpectateModule.OnRunEntered();
        if (CoopQolConfig.Current.EnableStats)
            CombatStatsModule.OnRunEntered();
        if (CoopQolConfig.Current.EnableQuickSl)
            QuickSlModule.OnRunEntered();
        if (CoopQolConfig.Current.EnableTerminal)
            UnifiedTerminalModule.OnRunEntered();

        GodModeModule.OnRunEntered();
        TopBarQuickBar.Refresh();
    }

    private static void NotifyRunExited()
    {
        if (_rootControl != null && GodotObject.IsInstanceValid(_rootControl))
        {
            _lastRootVisible = false;
            _rootControl.Visible = false;
        }

        SpectateModule.OnRunExited();
        UnifiedTerminalModule.OnRunExited();
        GodModeModule.OnRunExited();
        TopBarQuickBar.Refresh();
    }

    public static void OnGameRelocalized()
    {
        try
        {
            NativeUiHelper.InvalidateFontCache();
            PlayerNameHelper.ResetCache();
            CombatStatsModule.MarkDirty();

            if (_rootControl != null && GodotObject.IsInstanceValid(_rootControl))
            {
                if (CoopQolConfig.Current.EnableSpectate)
                    SpectateModule.Relocalize(_rootControl);
                if (CoopQolConfig.Current.EnableQuickSl)
                    QuickSlModule.Relocalize();
                TopBarQuickBar.Relocalize(_rootControl);
                GodModeModule.Relocalize(_rootControl);
            }
            else if (CoopQolConfig.Current.EnableQuickSl)
            {
                QuickSlModule.Relocalize();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"OnGameRelocalized error: {ex.Message}");
        }
    }

    [HarmonyPatch(typeof(NGame), nameof(NGame._Ready))]
    private static class NGameReadyPatch
    {
        private static void Postfix(NGame __instance)
        {
            EnsureUiInitialized(__instance);
        }
    }

    [HarmonyPatch(typeof(NGame), nameof(NGame.Relocalize))]
    private static class NGameRelocalizePatch
    {
        private static void Postfix()
        {
            OnGameRelocalized();
        }
    }

    [HarmonyPatch(typeof(NGame), nameof(NGame._Input))]
    private static class NGameInputPatch
    {
        private static void Postfix(InputEvent inputEvent)
        {
            if (inputEvent is InputEventKey keyEvt)
            {
                if (keyEvt.Echo) return;
                if (!keyEvt.Pressed)
                {
                    if (keyEvt.Keycode == Key.F5)
                        QuickSlModule.HandleKeyReleaseF5();
                    return;
                }

                var focusOwner = NGame.Instance?.GetViewport()?.GuiGetFocusOwner();
                if (focusOwner is LineEdit focusedEdit)
                {
                    if (keyEvt.Keycode == Key.Escape)
                    {
                        focusedEdit.Unedit();
                        focusedEdit.ReleaseFocus();
                        try
                        {
                            MegaCrit.Sts2.Core.Platform.PlatformUtil.CloseVirtualKeyboard();
                            DisplayServer.VirtualKeyboardHide();
                        }
                        catch { }
                        NGame.Instance?.GetViewport()?.SetInputAsHandled();
                    }
                    // Never mark typing/IME/Enter keys as handled here so LineEdit._GuiInput receives them!
                    return;
                }
                if (focusOwner is TextEdit)
                    return;

                try
                {
                    if (!GodModeModule.IsOpen
                        && !keyEvt.CtrlPressed && !keyEvt.AltPressed && !keyEvt.MetaPressed
                        && keyEvt.Keycode == QuickDeclareModule.GetConfiguredHoverKey()
                        && QuickDeclareModule.TryDeclareHovered())
                    {
                        NGame.Instance?.GetViewport()?.SetInputAsHandled();
                        return;
                    }
                    if (GodModeModule.HandleKeyPress(keyEvt))
                        return;
                    QuickSlModule.HandleKeyPress(keyEvt);
                    TopBarQuickBar.HandleKeyPress(keyEvt);
                    SpectateModule.HandleKeyPress(keyEvt);
                }
                catch (Exception ex)
                {
                    Log.Warn($"DispatchInput error: {ex.Message}");
                }
                return;
            }

            if (inputEvent is InputEventMouseButton { Pressed: true, AltPressed: true, ButtonIndex: MouseButton.Left })
            {
                try
                {
                    UnifiedTerminalModule.HandleAltClick();
                }
                catch (Exception ex)
                {
                    Log.Warn($"DispatchAltClick error: {ex.Message}");
                }
            }
        }
    }

    [HarmonyPatch(typeof(NRun), nameof(NRun._Ready))]
    private static class NRunReadyPatch
    {
        private static void Postfix()
        {
            try
            {
                if (NGame.Instance != null)
                    EnsureUiInitialized(NGame.Instance);
                NotifyRunEntered();
            }
            catch (Exception ex)
            {
                Log.Error($"NRunReadyPatch error: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(NRun), nameof(NRun._Process))]
    private static class NRunProcessPatch
    {
        private static void Postfix(double delta)
        {
            DispatchProcess(delta);
        }
    }

    [HarmonyPatch(typeof(NRun), nameof(NRun._Notification))]
    private static class NRunNotificationPatch
    {
        private static void Postfix(int what)
        {
            if (what == (int)Node.NotificationExitTree)
            {
                try
                {
                    NotifyRunExited();
                }
                catch (Exception ex)
                {
                    Log.Error($"NRunNotificationPatch error: {ex}");
                }
            }
        }
    }
}
