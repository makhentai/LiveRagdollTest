# LiveRagdollTest

A BepInEx mod for SPT 4.1.3 that gives a downed-but-alive character (bot or
player) real physical ragdoll motion while down, then returns control
smoothly when recovered — without teleporting, flying through walls, or the
limbs spinning/stretching wildly.

This mod does not decide what "downed" means — that's an external caller's
job (see `LiveRagdollTest.Api.LiveRagdollBridge`). It only handles the
physical ragdoll-and-recover behavior once told a character is down.

## Public API

```csharp
LiveRagdollTest.Api.LiveRagdollBridge.OnDowned(Player player, RagdollOptions? options = null); // returns bool: started?
LiveRagdollTest.Api.LiveRagdollBridge.OnRecovered(Player player);
LiveRagdollTest.Api.LiveRagdollBridge.IsActive(Player player);
```

`RagdollOptions` lets a caller override max duration / settle-velocity
threshold / whether the body stays ragdolled until explicitly recovered
(default) or hands back control automatically once it settles.

## Requirements / integration

Built against SPT 4.1.3's `Assembly-CSharp.dll`/`UnityEngine*`/BepInEx/
0Harmony/spt-reflection, referenced via `HintPath` in the `.csproj` — point
those at your own SPT install to build. No hard dependency on any other mod;
`_PMCCoopRevive/PMCoopRevive` integrates with this via a soft
GUID-based reference (see that project's `Helpers/LiveRagdollBridge.cs`).

## Stability notes (why this doesn't fling/spin like a naive attempt would)

- `RagdollSession` reuses the base game's own `CorpseRagdoll`/
  `RigidbodySpawner`/`CharacterJointSpawner` — the same components a real
  `Corpse` uses — rather than building custom physics.
- **`Player.EnabledAnimators` is zeroed for the duration of the session**,
  mirroring what `Player.OnDead()` itself does. This is the single most
  important line in the mod: a separate procedural/IK system
  (`Player.BodyUpdate`'s IK block — `RootMotion.FinalIK` `LimbIK` aiming the
  support hand at the weapon grip, among other things) keeps driving arm/
  weapon bones every frame *regardless of* whether the Animator components
  are disabled, and nothing else in this mod stops it.
- `RagdollSession` checks `HealthController.IsAlive` every physics tick —
  if the character dies for real mid-session (a finishing hit), the base
  game's real `Corpse` claims the same components; continuing to touch them
  crashes.
- `JointStability` hardens joints (unbreakable, projection enabled, softer
  limit springs, normalized mass scale) — largely redundant with what
  `CorpseRagdoll.Start()` already does automatically (confirmed by
  decompile), kept because it's harmless and documents the reasoning.
- `PoseReplayDrive` applies corrective torque/force toward the last known
  pose and toward closing any joint-anchor separation, entirely via
  `AddTorque`/`AddForce` — never a direct `Transform`/velocity write, so the
  existing velocity clamps still see and can cap it.

## Live motion capture (optional realism layer)

`LiveMotionSampler`/`LiveMotionCapturePatch` continuously sample every live
player's per-bone rotation (one Harmony postfix on `Player.BodyUpdate`,
same hook RagdollKinetics uses for its own continuous capture) so that when
`OnDowned` fires, the ragdoll can start from whatever motion the character
was already making — recoil, a mid-turn — instead of every bone starting
dead-still. Toggle: `Settings.CaptureLiveMotion` (on by default; turn off if
it measurably costs performance on a crowded raid — it runs every frame for
every live player, not just ones that go Downed).

The rotation-to-angular-velocity math (`AngularVelocityMath.Compute`) is
hand-rolled quaternion algebra on plain floats rather than a call into
`UnityEngine.Quaternion`'s own `Euler`/`Inverse`/`*`/`ToAngleAxis` — those
are native ECall stubs that only run inside a live Unity process (confirmed:
calling them from a standalone test throws `SecurityException`), which would
have made this logic untestable outside the game. It's covered by
`LiveRagdollTest.Tests/AngularVelocityMathTests.cs`.

Compatibility note: this only ever touches a character while
`HealthController.IsAlive` is true and hands off the instant that's no
longer the case, so it does not intersect with corpse-ragdoll mods
(RagdollKinetics, HollywoodFX) that only act on real `Corpse`/`RagdollClass`
instances after death.

Reference mods studied while building this (RagdollKinetics, TraumaCore,
VisceralCombat, HollywoodFX) are credited inline in the relevant source
comments.
