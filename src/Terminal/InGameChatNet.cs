using System;
using System.Text;
using CoopQolSuite.Core;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using Steamworks;

namespace CoopQolSuite.Terminal;

public static class InGameChatNet
{
    private const string PrefixBig = "[CQCHAT1]|";
    private const string PrefixQuiet = "[CQCHAT0]|";
    private const string PrefixAction = "[CQACT]|";

    private static Callback<LobbyChatMsg_t>? _lobbyChatCallback;
    private static bool _initialized;

    public static event Action<string, string, bool, bool>? MessageReceived;
    public static event Action<ulong, string, string, bool>? ActionPacketReceived;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            _lobbyChatCallback = Callback<LobbyChatMsg_t>.Create(OnLobbyChatMsg);
        }
        catch (Exception ex)
        {
            CoopQolModEntry.Log.Warn($"Steam LobbyChatMsg_t callback init skipped: {ex.Message}");
        }
    }

    public static void SendMessage(string text, bool bigScreen = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var trimmed = text.Trim();
        var netService = RunManager.Instance?.NetService;

        if (netService != null
            && netService.Platform == PlatformType.Steam
            && netService.Type != NetGameType.Singleplayer
            && netService.Type != NetGameType.None
            && TryGetCurrentLobbyId(netService, out var lobbyId))
        {
            try
            {
                var payload = (bigScreen ? PrefixBig : PrefixQuiet) + trimmed;
                var bytes = Encoding.UTF8.GetBytes(payload);
                if (SteamMatchmaking.SendLobbyChatMsg(lobbyId, bytes, bytes.Length))
                    return;
            }
            catch (Exception ex)
            {
                CoopQolModEntry.Log.Warn($"Steam SendLobbyChatMsg failed, falling back to local echo: {ex.Message}");
            }
        }

        // Singleplayer or non-Steam fallback: local echo
        var localName = GetLocalPlayerDisplayName();
        MessageReceived?.Invoke(localName, trimmed, bigScreen, true);
    }

    public static bool SendActionPacket(string actionId, string args = "")
    {
        if (string.IsNullOrWhiteSpace(actionId)) return false;

        var netService = RunManager.Instance?.NetService;
        if (netService != null
            && netService.Platform == PlatformType.Steam
            && netService.Type != NetGameType.Singleplayer
            && netService.Type != NetGameType.None
            && TryGetCurrentLobbyId(netService, out var lobbyId))
        {
            try
            {
                var safeArgs = (args ?? string.Empty).Replace("|", " ");
                var payload = $"{PrefixAction}{actionId.Trim()}|{safeArgs}";
                var bytes = Encoding.UTF8.GetBytes(payload);
                return SteamMatchmaking.SendLobbyChatMsg(lobbyId, bytes, bytes.Length);
            }
            catch (Exception ex)
            {
                CoopQolModEntry.Log.Warn($"Steam SendActionPacket failed: {ex.Message}");
            }
        }

        return false;
    }

    public static bool TryBuildHoveredItemShareText(out string shareText)
    {
        shareText = string.Empty;
        try
        {
            var tracker = RunManager.Instance?.HoveredModelTracker;
            var localId = LocalContext.NetId;
            if (tracker == null || localId == null) return false;

            var model = tracker.GetHoveredModel(localId.Value);
            if (model == null) return false;

            switch (model)
            {
                case CardModel card:
                {
                    shareText = QuickDeclareModule.BuildCardShareText(card);
                    return true;
                }
                case RelicModel relic:
                {
                    shareText = QuickDeclareModule.BuildRelicShareText(relic);
                    return true;
                }
                case PotionModel potion:
                {
                    shareText = QuickDeclareModule.BuildPotionShareText(potion);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            CoopQolModEntry.Log.Warn($"TryBuildHoveredItemShareText failed: {ex.Message}");
        }

        return false;
    }

    private static void OnLobbyChatMsg(LobbyChatMsg_t callback)
    {
        try
        {
            var netService = RunManager.Instance?.NetService;
            if (netService == null || netService.Platform != PlatformType.Steam)
                return;

            if (TryGetCurrentLobbyId(netService, out var currentLobby)
                && currentLobby.m_SteamID != callback.m_ulSteamIDLobby)
            {
                return;
            }

            var buffer = new byte[4096];
            var lobbyId = new CSteamID(callback.m_ulSteamIDLobby);

            int len = SteamMatchmaking.GetLobbyChatEntry(
                lobbyId,
                (int)callback.m_iChatID,
                out var senderSteamId,
                buffer,
                buffer.Length,
                out _);

            if (len <= 0) return;

            var raw = Encoding.UTF8.GetString(buffer, 0, len).TrimEnd('\0');
            if (string.IsNullOrWhiteSpace(raw)) return;

            bool isSelf = false;
            try
            {
                isSelf = senderSteamId == SteamUser.GetSteamID();
            }
            catch
            {
                // Ignore if SteamUser unavailable
            }

            if (raw.StartsWith(PrefixAction, StringComparison.Ordinal))
            {
                var rest = raw.Substring(PrefixAction.Length);
                int sep = rest.IndexOf('|');
                string actionId = sep >= 0 ? rest.Substring(0, sep) : rest;
                string args = sep >= 0 ? rest.Substring(sep + 1) : string.Empty;
                if (!string.IsNullOrWhiteSpace(actionId))
                    ActionPacketReceived?.Invoke(senderSteamId.m_SteamID, actionId.Trim(), args, isSelf);
                return;
            }

            bool bigScreen = true;
            string body = raw;
            if (raw.StartsWith(PrefixBig, StringComparison.Ordinal))
            {
                bigScreen = true;
                body = raw.Substring(PrefixBig.Length);
            }
            else if (raw.StartsWith(PrefixQuiet, StringComparison.Ordinal))
            {
                bigScreen = false;
                body = raw.Substring(PrefixQuiet.Length);
            }

            string senderName = CoopQolI18n.T("队友", "Ally");
            try
            {
                var friendName = SteamFriends.GetFriendPersonaName(senderSteamId);
                if (!string.IsNullOrWhiteSpace(friendName))
                    senderName = friendName;
            }
            catch
            {
                // Keep fallback name
            }

            MessageReceived?.Invoke(senderName, body, bigScreen, isSelf);
        }
        catch (Exception ex)
        {
            CoopQolModEntry.Log.Warn($"OnLobbyChatMsg error: {ex.Message}");
        }
    }

    private static bool TryGetCurrentLobbyId(INetGameService netService, out CSteamID lobbyId)
    {
        lobbyId = CSteamID.Nil;
        var raw = netService.GetRawLobbyIdentifier();
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (!ulong.TryParse(raw, out var parsed) || parsed == 0) return false;
        lobbyId = new CSteamID(parsed);
        return true;
    }

    private static string GetLocalPlayerDisplayName()
    {
        var runState = PlayerNameHelper.GetRunState();
        if (runState != null && LocalContext.NetId != null)
        {
            var player = runState.GetPlayer(LocalContext.NetId.Value);
            if (player != null)
                return PlayerNameHelper.GetPlayerName(player);
        }

        try
        {
            var persona = SteamFriends.GetPersonaName();
            if (!string.IsNullOrWhiteSpace(persona))
                return persona;
        }
        catch
        {
            // Ignore
        }

        return CoopQolI18n.T("我", "Me");
    }

    public static string CleanDesc(string? raw, int maxLen = 72)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.Replace("\r", " ").Replace("\n", " ");
        // Strip simple BBCode tags like [gold]...[/gold] for concise chat preview
        var sb = new StringBuilder(s.Length);
        bool inTag = false;
        foreach (var ch in s)
        {
            if (ch == '[') { inTag = true; continue; }
            if (ch == ']') { inTag = false; continue; }
            if (!inTag) sb.Append(ch);
        }
        var cleaned = sb.ToString().Trim();
        return cleaned.Length > maxLen ? cleaned.Substring(0, Math.Max(1, maxLen - 3)) + "..." : cleaned;
    }
}
