using System;
using System.Collections.Generic;
using CoopQolSuite.Core;
using CoopQolSuite.Terminal;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CoopQolSuite.Actions;

/// <summary>
/// Context passed to registered funny character actions when triggered locally or over Steam lobby sync.
/// </summary>
public sealed class CharacterActionContext
{
    public required Player Player { get; init; }
    public Creature? Creature { get; init; }
    public NCreature? CreatureNode { get; init; }
    public Node2D? BodyNode { get; init; }
    public bool IsLocalPlayer { get; init; }
    public string Args { get; init; } = string.Empty;
}

public sealed class CharacterActionDef
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Icon { get; init; } = string.Empty;
    public string Tooltip { get; init; } = string.Empty;
    public float CooldownSeconds { get; init; } = 0.25f;
    public required Action<CharacterActionContext> Execute { get; init; }
}

/// <summary>
/// Extensible character action interface reserved for future ActionPack mods.
/// When no external action pack registers actions, CoopQolSuite keeps all action UI hidden.
/// </summary>
public static class CharacterActionRegistry
{
    private static readonly List<CharacterActionDef> Registered = new();
    private static readonly Dictionary<string, CharacterActionDef> ById = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ulong> LastTriggerMs = new(StringComparer.OrdinalIgnoreCase);
    private static bool _initialized;

    public static event Action? ActionsChanged;

    public static IReadOnlyList<CharacterActionDef> Actions => Registered;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        InGameChatNet.ActionPacketReceived += OnNetworkActionReceived;
    }

    public static void Register(
        string id,
        string title,
        Action<CharacterActionContext> execute,
        string icon = "",
        string tooltip = "",
        float cooldownSeconds = 0.25f)
    {
        if (string.IsNullOrWhiteSpace(id) || execute == null)
            return;

        var cleanId = id.Trim();
        var def = new CharacterActionDef
        {
            Id = cleanId,
            Title = string.IsNullOrWhiteSpace(title) ? cleanId : title.Trim(),
            Icon = string.IsNullOrWhiteSpace(icon) ? string.Empty : icon.Trim(),
            Tooltip = tooltip ?? string.Empty,
            CooldownSeconds = Math.Max(0.1f, cooldownSeconds),
            Execute = execute,
        };

        lock (Registered)
        {
            if (ById.TryGetValue(cleanId, out var existing))
                Registered.Remove(existing);

            ById[cleanId] = def;
            Registered.Add(def);
        }

        Callable.From(() => ActionsChanged?.Invoke()).CallDeferred();
    }

    /// <summary>
    /// Reflection-friendly registration overload for external ActionPack mods without compile-time type coupling.
    /// Callback receives (Player, NCreature?).
    /// </summary>
    public static void RegisterSimple(string id, string title, Action<object, object?> callback, string icon = "")
    {
        if (callback == null) return;
        Register(id, title, ctx => callback(ctx.Player, ctx.CreatureNode), icon);
    }

    public static bool Unregister(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        bool removed;
        lock (Registered)
        {
            if (ById.TryGetValue(id.Trim(), out var existing))
            {
                ById.Remove(id.Trim());
                removed = Registered.Remove(existing);
            }
            else
            {
                removed = false;
            }
        }

        if (removed)
            Callable.From(() => ActionsChanged?.Invoke()).CallDeferred();
        return removed;
    }

    public static void Trigger(string id, string args = "")
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        CharacterActionDef? def;
        lock (Registered)
        {
            ById.TryGetValue(id.Trim(), out def);
        }
        if (def == null) return;

        ulong now = Time.GetTicksMsec();
        if (LastTriggerMs.TryGetValue(def.Id, out var lastMs))
        {
            if (now - lastMs < (ulong)(def.CooldownSeconds * 1000f))
                return;
        }
        LastTriggerMs[def.Id] = now;

        if (!InGameChatNet.SendActionPacket(def.Id, args))
        {
            var localPlayer = ResolveLocalPlayer();
            if (localPlayer != null)
                DispatchToMainThread(localPlayer, def, args, isLocal: true);
        }
    }

    private static void OnNetworkActionReceived(ulong senderNetId, string actionId, string args, bool isSelf)
    {
        CharacterActionDef? def;
        lock (Registered)
        {
            ById.TryGetValue(actionId, out def);
        }
        if (def == null) return;

        var player = ResolvePlayerByNetId(senderNetId) ?? (isSelf ? ResolveLocalPlayer() : null);
        if (player == null) return;

        DispatchToMainThread(player, def, args, isSelf);
    }

    private static void DispatchToMainThread(Player player, CharacterActionDef def, string args, bool isLocal)
    {
        Callable.From(() =>
        {
            try
            {
                var creature = player.Creature;
                var creatureNode = creature != null ? NCombatRoom.Instance?.GetCreatureNode(creature) : null;
                Node2D? body = null;
                if (creatureNode != null && GodotObject.IsInstanceValid(creatureNode))
                    body = creatureNode.Body ?? creatureNode.Visuals;

                var ctx = new CharacterActionContext
                {
                    Player = player,
                    Creature = creature,
                    CreatureNode = creatureNode,
                    BodyNode = body,
                    IsLocalPlayer = isLocal,
                    Args = args ?? string.Empty,
                };
                def.Execute(ctx);
            }
            catch (Exception ex)
            {
                CoopQolModEntry.Log.Warn($"CharacterAction '{def.Id}' execution failed: {ex.Message}");
            }
        }).CallDeferred();
    }

    private static Player? ResolveLocalPlayer()
    {
        var state = PlayerNameHelper.GetRunState();
        if (state == null) return null;
        return LocalContext.GetMe(state) ?? (state.Players.Count > 0 ? state.Players[0] : null);
    }

    private static Player? ResolvePlayerByNetId(ulong netId)
    {
        var state = PlayerNameHelper.GetRunState();
        if (state == null) return null;
        if (netId != 0)
        {
            var p = state.GetPlayer(netId);
            if (p != null) return p;
        }
        return state.Players.Count > 0 ? state.Players[0] : null;
    }

    // --- Reusable Tween Animation Helpers for future ActionPack mods ---

    public static void AnimateFunnyJump(Node2D? body, float height = 52f, double duration = 0.42)
    {
        if (body == null || !GodotObject.IsInstanceValid(body)) return;
        var origin = body.Position;
        var tween = body.CreateTween();
        tween.TweenProperty(body, "position:y", origin.Y - height, duration * 0.45)
             .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
        tween.TweenProperty(body, "position:y", origin.Y, duration * 0.55)
             .SetTrans(Tween.TransitionType.Bounce).SetEase(Tween.EaseType.Out);
    }

    public static void AnimateFunnySpin(Node2D? body, float revolutions = 1f, double duration = 0.45)
    {
        if (body == null || !GodotObject.IsInstanceValid(body)) return;
        float startRot = body.Rotation;
        var tween = body.CreateTween();
        tween.TweenProperty(body, "rotation", startRot + Mathf.Tau * revolutions, duration)
             .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        tween.TweenProperty(body, "rotation", startRot, 0.01);
    }

    public static void AnimateSquashBounce(Node2D? body, double duration = 0.40)
    {
        if (body == null || !GodotObject.IsInstanceValid(body)) return;
        var baseScale = body.Scale;
        var tween = body.CreateTween();
        tween.TweenProperty(body, "scale", new Vector2(baseScale.X * 1.24f, baseScale.Y * 0.76f), duration * 0.30);
        tween.TweenProperty(body, "scale", new Vector2(baseScale.X * 0.86f, baseScale.Y * 1.18f), duration * 0.35);
        tween.TweenProperty(body, "scale", baseScale, duration * 0.35)
             .SetTrans(Tween.TransitionType.Elastic).SetEase(Tween.EaseType.Out);
    }

    public static void AnimateHeadbob(Node2D? body, int count = 3)
    {
        if (body == null || !GodotObject.IsInstanceValid(body)) return;
        float baseRot = body.Rotation;
        var tween = body.CreateTween();
        for (int i = 0; i < count; i++)
        {
            tween.TweenProperty(body, "rotation", baseRot + 0.14f, 0.09);
            tween.TweenProperty(body, "rotation", baseRot - 0.14f, 0.09);
        }
        tween.TweenProperty(body, "rotation", baseRot, 0.08);
    }
}
