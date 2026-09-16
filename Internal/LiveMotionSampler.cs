using System;
using System.Collections.Generic;
using EFT;
using EFT.AssetsManager;
using UnityEngine;

namespace LiveRagdollTest.Internal;

/// <summary>Continuously samples each live player's per-bone angular velocity from consecutive
/// frames, so that whenever <see cref="Api.LiveRagdollBridge.OnDowned"/> fires, the ragdoll can
/// start from whatever motion the character was already making (recoil, a mid-turn) instead of
/// dead-still — ported concept from RagdollKinetics' RagdollPoseSampler
/// (github.com/Hysocs/ragdollkinetics-spt), which does the same continuous per-frame capture for
/// its corpse-quality feature. The actual rotation-to-velocity math lives in
/// <see cref="AngularVelocityMath"/>, kept separate so it's unit-testable without a running game.
///
/// Hooked from a single Harmony postfix on Player.BodyUpdate (see LiveMotionCapturePatch) —
/// applies to every live player (bots and the local player alike), gated by
/// Settings.CaptureLiveMotion so it can be turned off if it ever turns out to cost too much on a
/// crowded raid.</summary>
internal static class LiveMotionSampler
{
    private sealed class PlayerState
    {
        internal CharacterJointSpawner[] Spawners;
        internal readonly Dictionary<string, Quaternion> PreviousLocalRotation = new(20);
        internal readonly Dictionary<string, Vector3> AngularVelocity = new(20);
    }

    private static readonly Dictionary<Player, PlayerState> States = new();

    // Bounds the cost of pruning dead entries — sweeps roughly every 500 calls instead of every
    // call, since Unity's overloaded null check on a destroyed GameObject is not free at
    // dictionary-iteration scale done every frame for every player.
    private static int _callsSinceSweep;
    private const int SweepInterval = 500;

    private static bool _loggedError;

    /// <summary>Call once per Player.BodyUpdate tick. Never throws — a live-motion capture is a
    /// nice-to-have, not something that should ever be able to break the character update loop it
    /// piggybacks on.</summary>
    internal static void Capture(Player player, float deltaTime)
    {
        try
        {
            if (player == null || player.PlayerBody == null || deltaTime <= 0.0001f)
            {
                return;
            }

            if (player.HealthController == null || !player.HealthController.IsAlive)
            {
                States.Remove(player);
                return;
            }

            if (!States.TryGetValue(player, out PlayerState state))
            {
                state = new PlayerState
                {
                    Spawners = player.PlayerBody.GetComponentsInChildren<CharacterJointSpawner>(),
                };
                States[player] = state;
            }

            float inverseDelta = 1f / deltaTime;
            CharacterJointSpawner[] spawners = state.Spawners;
            for (int i = 0; i < spawners.Length; i++)
            {
                CharacterJointSpawner spawner = spawners[i];
                if (spawner == null)
                {
                    continue;
                }

                string key = spawner.name;
                Quaternion current = spawner.transform.localRotation;
                if (state.PreviousLocalRotation.TryGetValue(key, out Quaternion previous))
                {
                    Vector3 measured = AngularVelocityMath.FromRotationDelta(previous, current, inverseDelta);
                    if (state.AngularVelocity.TryGetValue(key, out Vector3 prior))
                    {
                        measured = Vector3.Lerp(prior, measured, 0.35f);
                    }

                    state.AngularVelocity[key] = measured;
                }

                state.PreviousLocalRotation[key] = current;
            }

            _callsSinceSweep++;
            if (_callsSinceSweep >= SweepInterval)
            {
                _callsSinceSweep = 0;
                SweepDestroyedPlayers();
            }
        }
        catch (Exception ex)
        {
            // One-shot: this runs every frame for every live player, so an ongoing failure must
            // not turn into an ongoing flood of log writes.
            if (!_loggedError)
            {
                _loggedError = true;
                Plugin.Log.LogError($"Error capturing live motion for {player?.ProfileId} (further errors suppressed): {ex}");
            }
        }
    }

    /// <summary>Takes and clears this player's captured per-bone angular velocities, keyed by bone
    /// name (matching RigidbodySpawner/CharacterJointSpawner.name elsewhere in this mod). Removed
    /// rather than left in place: once a session owns the character, its bones are physics-driven,
    /// not animated — continuing to "capture" that would be circular, not live motion.</summary>
    internal static bool TryTake(Player player, out Dictionary<string, Vector3> angularVelocities)
    {
        if (player != null && States.TryGetValue(player, out PlayerState state))
        {
            angularVelocities = state.AngularVelocity;
            States.Remove(player);
            return true;
        }

        angularVelocities = null;
        return false;
    }

    private static void SweepDestroyedPlayers()
    {
        List<Player> deadKeys = null;
        foreach (Player player in States.Keys)
        {
            if (player == null)
            {
                (deadKeys ??= new List<Player>()).Add(player);
            }
        }

        if (deadKeys == null)
        {
            return;
        }

        for (int i = 0; i < deadKeys.Count; i++)
        {
            States.Remove(deadKeys[i]);
        }
    }
}
