using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using Steamworks;

namespace CoopQolSuite.Core;

public static class PlayerNameHelper
{
    private static readonly Dictionary<ulong, string> NameCache = new();
    private static bool _steamProbed;
    private static string? _cachedLocalPersona;

    public static void ResetCache()
    {
        NameCache.Clear();
    }

    public static RunState? GetRunState()
    {
        return RunManager.Instance?.DebugOnlyGetState();
    }

    public static Player? ResolveOwnerPlayer(Creature? creature)
    {
        if (creature == null) return null;
        if (creature.IsPlayer) return creature.Player;
        if (creature.IsPet) return creature.PetOwner;
        return null;
    }

    public static string GetPlayerName(Player? player)
    {
        if (player == null) return CoopQolI18n.T("未知玩家", "Unknown Player");
        if (NameCache.TryGetValue(player.NetId, out var cached))
            return cached;

        string? resolved = null;
        try
        {
            var netService = RunManager.Instance?.NetService;
            if (netService != null && netService.Type != NetGameType.Singleplayer)
            {
                var raw = PlatformUtil.GetPlayerNameRaw(netService.Platform, player.NetId);
                if (!string.IsNullOrWhiteSpace(raw))
                    resolved = raw;
            }
            else
            {
                if (!_steamProbed)
                {
                    _steamProbed = true;
                    try
                    {
                        var persona = SteamFriends.GetPersonaName();
                        _cachedLocalPersona = string.IsNullOrWhiteSpace(persona) ? null : persona;
                    }
                    catch
                    {
                        _cachedLocalPersona = null;
                    }
                }
                resolved = _cachedLocalPersona;
            }
        }
        catch
        {
            resolved = null;
        }

        if (string.IsNullOrWhiteSpace(resolved))
        {
            var charTitle = player.Character?.Title?.GetFormattedText();
            resolved = !string.IsNullOrWhiteSpace(charTitle)
                ? charTitle
                : (CoopQolI18n.IsZh ? $"玩家#{player.NetId}" : $"Player #{player.NetId}");
        }

        NameCache[player.NetId] = resolved;
        return resolved;
    }

    public static string GetCreatureName(Creature? creature)
    {
        if (creature == null) return CoopQolI18n.T("未知目标", "Unknown");
        if (creature.IsPlayer) return GetPlayerName(creature.Player);
        if (creature.IsPet)
        {
            return CoopQolI18n.IsZh
                ? $"{GetPlayerName(creature.PetOwner)}的{creature.Name}"
                : $"{GetPlayerName(creature.PetOwner)}'s {creature.Name}";
        }
        return string.IsNullOrWhiteSpace(creature.Name) ? CoopQolI18n.T("敌人", "Enemy") : creature.Name;
    }

    public static string EscapeBbCode(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.IndexOf('[') >= 0 ? text.Replace("[", "[lb]") : text;
    }
}
