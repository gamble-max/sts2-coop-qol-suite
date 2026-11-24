using System;
using System.Collections.Generic;
using System.Threading;
using CoopQolSuite.Actions;
using CoopQolSuite.Core;
using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace CoopQolSuite.Terminal;

public static class UnifiedTerminalModule
{
    public readonly record struct ChatMessageEntry(string Sender, string Text, bool BigScreen, bool IsSelf);

    private const int LaneCount = 5;

    private static bool _initialized;
    private static Control? _bigScreenLayer;

    private static readonly List<DanmakuItem> ActiveDanmaku = new();
    private static readonly Queue<ChatMessageEntry> PendingChat = new();
    private static volatile int _pendingChatCount;
    private static int _danmakuLane;

    public static bool HasPendingChat => _pendingChatCount > 0;

    private sealed class DanmakuItem
    {
        public RichTextLabel Label = null!;
        public float Speed;
        public int Lane;
    }

    public static void Initialize(Node rootContainer)
    {
        if (_initialized) return;
        _initialized = true;

        InGameChatNet.Initialize();
        CharacterActionRegistry.Initialize();
        EventStreamTracker.Initialize();
        QuickDeclareModule.Initialize(rootContainer);

        _bigScreenLayer = new Control
        {
            Name = "CoopQolBigScreenLayer",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _bigScreenLayer.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        InGameChatNet.MessageReceived += OnChatMessageReceived;
        _bigScreenLayer.TreeExiting += () => InGameChatNet.MessageReceived -= OnChatMessageReceived;

        rootContainer.AddChild(_bigScreenLayer);
    }

    public static void OnRunEntered()
    {
        EventStreamTracker.OnRunEntered();
    }

    public static void OnRunExited()
    {
        for (int i = ActiveDanmaku.Count - 1; i >= 0; i--)
        {
            var lbl = ActiveDanmaku[i].Label;
            if (GodotObject.IsInstanceValid(lbl))
                lbl.QueueFree();
        }
        ActiveDanmaku.Clear();
    }

    public static void SendChatMessage(string text, bool bigScreen = true)
    {
        InGameChatNet.SendMessage(text, bigScreen);
    }

    public static void HandleAltClick()
    {
        if (!_initialized || !CoopQolConfig.Current.EnableTerminal)
            return;

        if (InGameChatNet.TryBuildHoveredItemShareText(out var shareText))
        {
            SendChatMessage(shareText, true);
            NGame.Instance?.GetViewport()?.SetInputAsHandled();
        }
    }

    public static bool TryDequeueChat(out ChatMessageEntry entry)
    {
        lock (PendingChat)
        {
            if (PendingChat.Count == 0)
            {
                _pendingChatCount = 0;
                entry = default;
                return false;
            }
            entry = PendingChat.Dequeue();
            Interlocked.Decrement(ref _pendingChatCount);
            return true;
        }
    }

    public static void TriggerBigScreen(string safeSender, string safeText, string color)
    {
        if (CoopQolConfig.Current.ShowBigScreenDanmaku)
            SpawnDanmaku(safeSender, safeText, color);
    }

    private static void OnChatMessageReceived(string sender, string text, bool bigScreen, bool isSelf)
    {
        lock (PendingChat)
        {
            PendingChat.Enqueue(new ChatMessageEntry(sender, text, bigScreen, isSelf));
            Interlocked.Increment(ref _pendingChatCount);
        }
    }

    public static void Tick(double delta)
    {
        if (!_initialized || ActiveDanmaku.Count == 0)
            return;

        for (int i = ActiveDanmaku.Count - 1; i >= 0; i--)
        {
            var item = ActiveDanmaku[i];
            if (!GodotObject.IsInstanceValid(item.Label))
            {
                ActiveDanmaku.RemoveAt(i);
                continue;
            }

            var pos = item.Label.Position;
            pos.X -= item.Speed * (float)delta;
            item.Label.Position = pos;
            if (pos.X + item.Label.Size.X < -60f)
            {
                item.Label.QueueFree();
                ActiveDanmaku.RemoveAt(i);
            }
        }
    }

    private static int PickBestLane(float vpWidth)
    {
        int bestLane = _danmakuLane % LaneCount;
        float bestRightMost = float.MaxValue;

        for (int l = 0; l < LaneCount; l++)
        {
            float rightMost = -9999f;
            for (int i = 0; i < ActiveDanmaku.Count; i++)
            {
                var item = ActiveDanmaku[i];
                if (item.Lane != l || !GodotObject.IsInstanceValid(item.Label)) continue;
                float rightEdge = item.Label.Position.X + Math.Max(item.Label.Size.X, 220f);
                if (rightEdge > rightMost) rightMost = rightEdge;
            }

            if (rightMost < vpWidth - 60f)
            {
                _danmakuLane = (l + 1) % LaneCount;
                return l;
            }

            if (rightMost < bestRightMost)
            {
                bestRightMost = rightMost;
                bestLane = l;
            }
        }

        _danmakuLane = (bestLane + 1) % LaneCount;
        return bestLane;
    }

    private static void SpawnDanmaku(string safeSender, string safeText, string color)
    {
        if (_bigScreenLayer == null) return;
        var vpWidth = _bigScreenLayer.GetViewportRect().Size.X;
        if (vpWidth <= 0) vpWidth = 1920f;

        int lane = PickBestLane(vpWidth);
        float y = 108f + lane * 38f;

        var sep = CoopQolI18n.IsZh ? "：" : ": ";
        var label = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.Off,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Position = new Vector2(vpWidth + 24f, y),
            Text = $"[b][color={color}]{safeSender}[/color]{sep}[color=#FFF6E2]{safeText}[/color][/b]",
        };
        NativeUiHelper.ApplyRichTextFont(label, 26, outlineSize: 5);
        _bigScreenLayer.AddChild(label);

        ActiveDanmaku.Add(new DanmakuItem
        {
            Label = label,
            Speed = 250f,
            Lane = lane,
        });
    }
}
