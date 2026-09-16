using System.Reflection;
using EFT;
using HarmonyLib;
using LiveRagdollTest.Api;
using SPT.Reflection.Patching;

namespace LiveRagdollTest.Internal;

/// <summary>Player.ComplexUpdate (decompiled Assembly-CSharp) calls ArmsUpdate every tick as long as
/// HealthController.IsAlive — true for the whole Downed-but-alive window this mod exists for, by
/// design. ArmsUpdate calls _handsController.ManualUpdate(deltaTime) directly (not through Unity's
/// enabled-gated message pump), which runs the weapon's own aim/handling update — completely
/// unaffected by every disable this mod already does (BodyAnimatorCommon, ArmsAnimatorCommon,
/// EnabledAnimators, CharacterController, BotOwner) since none of those gate this call. Field-
/// observed as a downed character's weapon still tracking/aiming at the player, muzzle following
/// within the arm's joint range, throughout physics ragdoll. Scoped strictly to LiveRagdollBridge.
/// IsActive — false the instant a session ends (recovery or real death), so a real corpse's
/// ArmsUpdate (irrelevant anyway, HealthController.IsAlive is false for a corpse) is never
/// touched by this patch at all.</summary>
internal sealed class ArmsUpdateSuppressPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(Player), nameof(Player.ArmsUpdate), new[] { typeof(float) });

    [PatchPrefix]
    private static bool PatchPrefix(Player __instance) =>
        __instance == null || !LiveRagdollBridge.IsActive(__instance);
}
