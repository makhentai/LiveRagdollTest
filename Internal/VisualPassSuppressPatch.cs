using System.Reflection;
using EFT;
using HarmonyLib;
using LiveRagdollTest.Api;
using SPT.Reflection.Patching;

namespace LiveRagdollTest.Internal;

/// <summary>Player.LateUpdate (decompiled Assembly-CSharp) is a normal Unity message — runs every
/// frame regardless of anything this mod disables, since the Player component itself is never
/// disabled. It calls VisualPass() as long as HealthController.IsAlive, which is true for the whole
/// Downed-but-alive window by design. VisualPass() unconditionally calls FBBIKUpdate(num) (full-body
/// IK, not gated by EnabledAnimators at all — only the deeper IkProcess/IkApply/_limbs[] block a few
/// lines later is), HandsController.ManualLateUpdate(deltaTime) (a separate call site from the
/// ManualUpdate() ArmsUpdateSuppressPatch already blocks), and ProceduralWeaponAnimation.
/// LateTransformations(deltaTime) (ignores ProceduralWeaponAnimation.enabled entirely, same pattern
/// as HandsController.ManualUpdate). Any one of these can keep the weapon oriented toward whatever
/// target the character was last aiming at — field-observed as the weapon continuing to track/aim at
/// the player from a downed body. Scoped strictly to LiveRagdollBridge.IsActive, exactly like
/// ArmsUpdateSuppressPatch — a real corpse (HealthController.IsAlive false) never reaches VisualPass
/// via this path at all, so this patch has no effect on real deaths.</summary>
internal sealed class VisualPassSuppressPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(Player), nameof(Player.VisualPass), System.Array.Empty<System.Type>());

    [PatchPrefix]
    private static bool PatchPrefix(Player __instance) =>
        __instance == null || !LiveRagdollBridge.IsActive(__instance);
}
