using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LiveRagdollTest.Internal;

/// <summary>Safety net: snaps every bone back to a recent anchor pose if physics ever sends one
/// further away than it plausibly should go in that window — a body clipped through a wall or
/// fallen through the floor otherwise never resettles on its own. Default leash is short (2m, see
/// Settings.MaxDriftMeters).
///
/// The anchor is NOT a single snapshot from session start — a caller can keep a session alive far
/// longer than "a few seconds" (PMCoopRevive's keepRagdolledUntilRestored routinely holds one for
/// minutes), during which a body legitimately sliding down a slope, tumbling down stairs, or
/// getting shoved by an explosion can easily end up more than 2m from wherever it started —
/// nothing wrong, just normal physics given enough time. A fixed start-of-session anchor would
/// read that as a "glitch" and snap the whole body back to its ORIGINAL pose/position, which can
/// itself land the body inside nearby geometry (a container, a wall) it had since moved clear of —
/// field-reported as bodies getting "sucked into" a container, or slowly creeping since the low
/// in-session depenetration cap can only push it back out gradually. Rebase() re-baselines the
/// anchor to the current pose, called periodically from RagdollSession whenever a tick did NOT
/// need to recover — so the check only ever catches an implausible jump within one rebase window,
/// never cumulative-but-legitimate travel over the life of a long session.
///
/// Also doubles as the lookup <see cref="FatalImpulsePatch"/> uses to scope its impulse scaling to
/// only our own live sessions, leaving a real Corpse's ragdoll untouched.</summary>
internal sealed class RagdollDrift
{
    private static readonly HashSet<Rigidbody> TrackedBodies = new();

    private readonly Rigidbody[] _bodies;
    private Vector3[] _offsets;
    private Vector3 _anchor;

    private RagdollDrift(Rigidbody[] bodies, Vector3[] offsets, Vector3 anchor)
    {
        _bodies = bodies;
        _offsets = offsets;
        _anchor = anchor;
    }

    public static RagdollDrift Track(Rigidbody[] bodies)
    {
        Rigidbody anchorBody = bodies.FirstOrDefault(b => b != null && b.name.ToLowerInvariant().Contains("pelvis"))
            ?? bodies.FirstOrDefault(b => b != null);
        if (anchorBody == null)
        {
            return null;
        }

        Vector3 anchor = anchorBody.position;
        var offsets = new Vector3[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = bodies[i] != null ? bodies[i].position - anchor : Vector3.zero;
        }

        var drift = new RagdollDrift(bodies, offsets, anchor);
        foreach (Rigidbody body in bodies)
        {
            if (body != null)
            {
                TrackedBodies.Add(body);
            }
        }

        return drift;
    }

    public static bool IsTracking(Rigidbody rigidbody) => TrackedBodies.Contains(rigidbody);

    /// <summary>Re-baselines the anchor and every bone's offset to the CURRENT pose. Call this once
    /// per rebase window as long as CheckAndRecover did not just fire — advancing the baseline is
    /// exactly what tells legitimate multi-meter travel over a long session apart from a genuine
    /// one-window teleport/glitch.</summary>
    public void Rebase()
    {
        Rigidbody anchorBody = _bodies.FirstOrDefault(b => b != null && b.name.ToLowerInvariant().Contains("pelvis"))
            ?? _bodies.FirstOrDefault(b => b != null);
        if (anchorBody == null)
        {
            return;
        }

        _anchor = anchorBody.position;
        for (int i = 0; i < _bodies.Length; i++)
        {
            _offsets[i] = _bodies[i] != null ? _bodies[i].position - _anchor : Vector3.zero;
        }
    }

    public void Stop()
    {
        foreach (Rigidbody body in _bodies)
        {
            if (body != null)
            {
                TrackedBodies.Remove(body);
            }
        }
    }

    /// <returns>True if a recovery snap happened this call (caller may want to reset its own settle
    /// detector, since a just-recovered body is momentarily at rest for the wrong reason).</returns>
    public bool CheckAndRecover(float maxDistanceMeters)
    {
        float maxSq = maxDistanceMeters * maxDistanceMeters;
        bool glitched = false;
        foreach (Rigidbody body in _bodies)
        {
            if (body == null)
            {
                continue;
            }

            Vector3 position = body.position;
            if (!IsFinite(position) || (position - _anchor).sqrMagnitude > maxSq)
            {
                glitched = true;
                break;
            }
        }

        if (!glitched)
        {
            return false;
        }

        Plugin.Log.LogInfo($"[RagdollLifecycle] RagdollDrift recovery triggered (anchor={_anchor}, maxDistance={maxDistanceMeters})");

        for (int i = 0; i < _bodies.Length; i++)
        {
            Rigidbody body = _bodies[i];
            if (body == null)
            {
                continue;
            }

            body.position = _anchor + _offsets[i];
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.WakeUp();
        }

        return true;
    }

    private static bool IsFinite(Vector3 value) =>
        !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
        !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
        !float.IsNaN(value.z) && !float.IsInfinity(value.z);
}
