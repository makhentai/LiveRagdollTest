using System.Reflection;
using EFT.Interactive;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace LiveRagdollTest.Internal;

/// <summary>Suppresses the game's own hit-reaction physics impulse for rigidbodies belonging to one
/// of our own live sessions (RagdollDrift.IsTracking) — a real Corpse's ragdoll is untouched. A
/// downed character stays HealthController.IsAlive for as long as it takes to finish it, so without
/// this every extra shot keeps shoving an already-jointed, already-moving ragdoll chain, compounding
/// into a launch far harder than the initial down. The initial reaction still comes through
/// (CorpseRagdoll's own launch velocity, set once at trigger time) — only additional impulses on an
/// already-tracked body are dropped.</summary>
internal sealed class FatalImpulsePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(CorpseRagdoll), nameof(CorpseRagdoll.ApplyImpulse),
            new[] { typeof(Rigidbody), typeof(Vector3), typeof(Vector3), typeof(float) });

    [PatchPrefix]
    private static bool PatchPrefix(Rigidbody rigidbody)
    {
        return rigidbody == null || !RagdollDrift.IsTracking(rigidbody);
    }
}
