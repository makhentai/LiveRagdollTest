using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace LiveRagdollTest.Internal;

/// <summary>Feeds LiveMotionSampler from the same per-frame hook RagdollKinetics uses for its own
/// continuous motion capture (Player.BodyUpdate). Runs for every live player, every frame — kept
/// deliberately trivial (one call, no branching) since LiveMotionSampler.Capture already contains
/// all its own gating/error handling; this patch is just the wire from the game's own update loop
/// to that method.</summary>
internal sealed class LiveMotionCapturePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(Player), nameof(Player.BodyUpdate));

    [PatchPostfix]
    private static void PatchPostfix(Player __instance, float deltaTime)
    {
        if (!Settings.CaptureLiveMotion.Value)
        {
            return;
        }

        LiveMotionSampler.Capture(__instance, deltaTime);
    }
}
