using System;
using System.Collections.Generic;
using EFT;
using LiveRagdollTest.Internal;

namespace LiveRagdollTest.Api;

public static class LiveRagdollBridge
{
    private static readonly Dictionary<string, RagdollSession> ActiveSessions = new();

    public static event Action<Player> OnSettled;

    public static bool OnDowned(Player player, RagdollOptions? options = null)
    {
        try
        {
            if (!Settings.Enabled.Value || player == null)
            {
                return false;
            }

            string profileId = player.ProfileId;
            if (TryGetLiveSession(profileId, out _))
            {
                return false;
            }

            RagdollOptions resolved = options ?? RagdollOptions.Default;
            var session = RagdollSession.TryStart(player, resolved, natural => OnSessionEnded(profileId, player, natural));
            if (session == null)
            {
                return false;
            }

            ActiveSessions[profileId] = session;
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"Error in OnDowned for {player?.ProfileId}: {ex.Message}");
            return false;
        }
    }

    public static void OnRecovered(Player player)
    {
        try
        {
            if (player == null || !TryGetLiveSession(player.ProfileId, out var session))
            {
                return;
            }

            session.BeginTeardown(natural: false);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"Error in OnRecovered for {player?.ProfileId}: {ex.Message}");
        }
    }

    public static bool IsActive(Player player)
    {
        try
        {
            return player != null && TryGetLiveSession(player.ProfileId, out _);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"Error checking ragdoll active state for {player?.ProfileId}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Looks up an active session, first evicting it if its player reference has gone
    /// (GameObject destroyed/deactivated while the ragdoll coroutine was still running). Unity kills
    /// that coroutine silently, so BeginTeardown/OnSessionEnded never run and the entry would
    /// otherwise never leave ActiveSessions.</summary>
    private static bool TryGetLiveSession(string profileId, out RagdollSession session)
    {
        if (ActiveSessions.TryGetValue(profileId, out session))
        {
            if (!session.PlayerGone)
            {
                return true;
            }

            ActiveSessions.Remove(profileId);
        }

        session = null;
        return false;
    }

    private static void OnSessionEnded(string profileId, Player player, bool natural)
    {
        ActiveSessions.Remove(profileId);
        if (natural)
        {
            OnSettled?.Invoke(player);
        }
    }
}
