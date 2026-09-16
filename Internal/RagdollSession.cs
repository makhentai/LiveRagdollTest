using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.AssetsManager;
using EFT.Interactive;
using LiveRagdollTest.Api;
using UnityEngine;

namespace LiveRagdollTest.Internal;

/// <summary>Owns one player's physics ragdoll from spawn to teardown. Reuses the base game's own
/// dormant RigidbodySpawner/CharacterJointSpawner/CorpseRagdoll components — the same ones a real
/// Corpse uses — rather than building new physics.</summary>
internal sealed class RagdollSession
{
    private readonly Player _player;
    private readonly RagdollOptions _options;
    private readonly Action<bool> _onEnded;
    private readonly SettleDetector _settleDetector = new();
    private readonly JointStability _jointStability = new();
    private readonly PoseReplayDrive _poseReplayDrive = new();

    private const float MinSettleSeconds = 0.5f;

    // How often RagdollDrift's anchor advances to the current pose when nothing looked like a
    // glitch — long enough that a real teleport/wall-clip within one window still trips
    // MaxDriftMeters, short enough that legitimate settling (sliding down a slope, tumbling down
    // stairs) over a long keepRagdolledUntilRestored session never accumulates past it.
    private const float DriftRebaseIntervalSeconds = 1f;

    // PlayerRigidbodySleepHierarchy.TryPutToSleep() (base game, called every tick below) is the
    // real mechanism behind arms getting permanently stuck at TryStart's identity reset: that
    // reset gives the bone ~zero velocity in a pose PoseReplayDrive then actively HOLDS near-still
    // for its own decay window (spring+damper cancel out any gravity-induced drift while the hold
    // is active) — exactly the "quiet" signature TryPutToSleep looks for, so the bone can fall
    // asleep WHILE still mid-hold. Once asleep, PoseReplayDrive.Tick itself skips sleeping bones
    // (see its own loop), so the hold-release taper never finishes on it either, and nothing else
    // in this session ever wakes it back up — gravity never gets a real, unopposed chance to rotate
    // it away from the artificial reset pose, so it just stays there: a T-pose that never resolves,
    // most visible when the rest of the body (which had real fall momentum) settles quickly against
    // nearby geometry and the frozen arm reads as "standing there" rather than an obvious glitch.
    // Guarding sleep until past both decay windows (general DecaySeconds and the shorter
    // ArmDecaySeconds in PoseReplayDrive) guarantees every bone gets a real, fully-unheld physics
    // window before Unity's own sleep system can freeze anything.
    private const float SleepGuardSeconds = 0.45f;

    private RigidbodySpawner[] _rigidbodySpawners;
    private CharacterJointSpawner[] _jointSpawners;
    private ArmorPlateCollider[] _armorPlates;
    private RagdollDrift _drift;
    private List<PlayerRigidbodySleepHierarchy> _sleepHierarchy;
    private HashSet<Collider> _ownColliders;
    private int[] _originalLayers;
    private bool _animatorsDisabled;
    private bool _controllerDisabled;
    private bool _weaponAnimDisabled;
    private bool _fastAnimatorStopped;
    private float _rootMinusPelvisY;
    private float _lastDriftRebase;
    private int _consecutiveNotAliveTicks;
    private bool _tornDown;
    private bool _frozen;
    private Coroutine _coroutine;
    private bool _botOwnerDisabled;
    private bool _botOwnerWasEnabled;
    private bool _enabledAnimatorsChanged;
    private Player.EAnimatorMask _originalEnabledAnimators;
    private float[] _originalMaxDepenetration;

    // Precomputed once per bone in TryStart instead of re-running IsHandBone's ToLowerInvariant/
    // Contains scan on every rigidbody every physics tick in Run() — same bone names, same result,
    // every time a session is alive.
    private bool[] _isHandBone;
    private Transform _weaponRootAnim;
    private Transform _weaponAnchorBone;
    private Vector3 _weaponLocalOffset;
    private Quaternion _weaponLocalRotation;

    private RagdollSession(Player player, RagdollOptions options, Action<bool> onEnded)
    {
        _player = player;
        _options = options;
        _onEnded = onEnded;
    }

    /// <summary>True once the underlying Player GameObject has been destroyed/deactivated — e.g.
    /// raid end or despawn while the ragdoll was still running, which silently kills this session's
    /// coroutine before BeginTeardown/_onEnded ever run.</summary>
    public bool PlayerGone => _player == null;

    public static RagdollSession TryStart(Player player, RagdollOptions options, Action<bool> onEnded)
    {
        if (player?.PlayerBody == null)
        {
            return null;
        }

        var rigidbodySpawners = player.PlayerBody.GetComponentsInChildren<RigidbodySpawner>();
        var jointSpawners = player.PlayerBody.GetComponentsInChildren<CharacterJointSpawner>();
        if (rigidbodySpawners.Length == 0 && jointSpawners.Length == 0)
        {
            return null;
        }

        var session = new RagdollSession(player, options, onEnded)
        {
            _rigidbodySpawners = rigidbodySpawners,
            _jointSpawners = jointSpawners,
            _armorPlates = player.PlayerBody.PlayerBones.ArmorPlateColliders,
        };

        try
        {
            Transform startPelvis = player.PlayerBody.PlayerBones.Pelvis?.Original;
            if (startPelvis != null)
            {
                session._rootMinusPelvisY = player.MovementContext.TransformPosition.y - startPelvis.position.y;
            }

            // Captured here, before anything below touches the animator: the visible weapon model
            // isn't parented to any of the RigidbodySpawner/CharacterJointSpawner bones physics
            // drives — it's a separate animation-only pivot (PlayerBones.Weapon_Root_Anim) repositioned
            // every frame by Player.VisualPass -> PlayerBones.ShiftWeaponRoot, gated by EnabledAnimators
            // exactly like the IK block, so it already went stale the moment EnabledAnimators was first
            // zeroed — just masked by the arm/weapon-spin chaos that fix was aimed at. With that noise
            // gone, an ungoverned Weapon_Root_Anim just stays frozen at its last computed pose while the
            // physically-ragdolled body falls out from under it — field-observed as the weapon floating
            // in place on its sling while the character collapses. RightPalm is a plain rig Transform
            // (not physics-driven itself) that ShiftWeaponRoot's own math (see PlayerBones.Kinematics)
            // uses as the weapon's hand anchor, so it's a reliable reference point regardless of exact
            // bone-name conventions in a given skeleton. Walking up from it to the nearest bone this
            // session actually ragdolls (rather than assuming a specific name like "RightHand") ties the
            // capture to whatever the real rig hierarchy is, not a guess. Run() mirrors Weapon_Root_Anim
            // onto that bone's live physics pose every tick below — no Player API, no animator, so it
            // can't fight the ragdoll and can't get stuck mid-operation the way LiveRagdollBridge.Helpers.
            // WeaponHolster's SetEmptyHands/TrySetLastEquippedWeapon would for a character whose animator
            // is deliberately disabled and whose session can end at an arbitrary tick.
            Transform weaponRootAnim = player.PlayerBody.PlayerBones.Weapon_Root_Anim;
            Transform rightPalm = player.PlayerBody.PlayerBones.RightPalm;
            if (weaponRootAnim != null && rightPalm != null)
            {
                Transform anchor = FindNearestRagdolledAncestor(rightPalm, rigidbodySpawners);
                if (anchor != null)
                {
                    session._weaponRootAnim = weaponRootAnim;
                    session._weaponAnchorBone = anchor;
                    session._weaponLocalOffset = Quaternion.Inverse(anchor.rotation) * (weaponRootAnim.position - anchor.position);
                    session._weaponLocalRotation = Quaternion.Inverse(anchor.rotation) * weaponRootAnim.rotation;
                }
            }

            player.BodyAnimatorCommon.enabled = false;

            if (AppEnvironment.Config.UseBodyFastAnimator)
            {
                player.PlayerBody.PlayerBones.PlayableAnimator.Stop();
                session._fastAnimatorStopped = true;
            }

            player.ArmsAnimatorCommon.enabled = false;
            session._animatorsDisabled = true;
            player._characterController.isEnabled = false;
            session._controllerDisabled = true;

            if (player.ProceduralWeaponAnimation != null)
            {
                player.ProceduralWeaponAnimation.enabled = false;
                session._weaponAnimDisabled = true;
            }

            // Decompiled Player.BodyUpdate (Assembly-CSharp): a large block gated only by
            // `EnabledAnimators & EAnimatorMask.IK` — separate from every Animator component
            // disabled above — runs every frame regardless: RotateHead, IkProcess/IkApply/
            // AdjustElbows, and _limbs[0]/_limbs[1].solver (the two-hand weapon-grip IK, RootMotion
            // FinalIK LimbIK) that continuously aims the support hand at the weapon's grip point.
            // This is almost certainly the real source of "arms/weapon keep moving on their own"
            // reported after every other animator/IK-adjacent fix — none of them touch this field.
            // EnabledAnimators is a plain public bitmask field; zeroing it turns off Thirdperson/
            // Arms/Procedural/FBBIK/IK together for as long as this session holds the character.
            session._originalEnabledAnimators = player.EnabledAnimators;
            player.EnabledAnimators = 0;
            session._enabledAnimatorsChanged = true;

            // AI brain: field logs showed angularVelocity reading exactly 0.00 on a bone every
            // tick for a whole session while its rotation kept visibly changing between samples —
            // the signature of something writing Transform.rotation directly rather than physics
            // moving it (a velocity-based clamp/drive can't see or stop that). Every animator/IK
            // component above is already disabled; BotOwner's own aim/look logic is a separate
            // system on top of those and was never touched. Only relevant for bots — the local
            // player has no BotOwner.
            if (player.IsAI && player.AIData?.BotOwner != null)
            {
                session._botOwnerWasEnabled = player.AIData.BotOwner.enabled;
                player.AIData.BotOwner.enabled = false;
                session._botOwnerDisabled = true;
            }

            int deadbodyLayer = LayerMask.NameToLayer("Deadbody");
            if (deadbodyLayer >= 0)
            {
                session._originalLayers = new int[rigidbodySpawners.Length];
                for (int i = 0; i < rigidbodySpawners.Length; i++)
                {
                    GameObject go = rigidbodySpawners[i]?.gameObject;
                    if (go == null)
                    {
                        continue;
                    }

                    session._originalLayers[i] = go.layer;
                    go.layer = deadbodyLayer;
                }
            }

            var bodies = player.gameObject.GetComponentsInChildren<Collider>()
                .Where(c => c != null)
                .ToArray();
            for (int i = 0; i < bodies.Length; i++)
            {
                for (int j = i + 1; j < bodies.Length; j++)
                {
                    Physics.IgnoreCollision(bodies[i], bodies[j], true);
                }
            }

            session._ownColliders = new HashSet<Collider>(bodies);

            List<PlayerRigidbodySleepHierarchy> sleepHierarchy = PlayerPoolObject.CreatePlayerRigidbodySleepHierarchy(rigidbodySpawners);

            _ = new CorpseRagdoll(
                rigidbodySpawners, jointSpawners, sleepHierarchy,
                player.Velocity, EFTHardSettings.Instance.CorpseMaxDepenetrationVelocity,
                CollisionDetectionMode.Discrete, player,
                (stopped, elapsed) => true, player.PlayerBody, () => true, () => { },
                keepRigidbody: false, putToSleep: false);

            session._sleepHierarchy = sleepHierarchy;
            session._drift = RagdollDrift.Track(rigidbodySpawners.Select(s => s?.Rigidbody).ToArray());
            session._jointStability.Capture(jointSpawners);

            // Captured after the arm-bone identity reset near the top of this method (so arms
            // target that reset pose, not the original combat pose) and after CorpseRagdoll has
            // attached the joints (so bone.rotation reads live ragdoll transforms, not stale
            // pre-physics ones).
            session._poseReplayDrive.Capture(jointSpawners);

            session._originalMaxDepenetration = new float[rigidbodySpawners.Length];
            session._isHandBone = new bool[rigidbodySpawners.Length];
            for (int i = 0; i < rigidbodySpawners.Length; i++)
            {
                Rigidbody rb = rigidbodySpawners[i]?.Rigidbody;
                if (rb == null)
                {
                    continue;
                }

                // Capped for the duration of this session only — restored in BeginTeardown before
                // handing these same Rigidbody components back, whether to the animator (recovery)
                // or to the base game's own Corpse (a finishing hit mid-session, see BeginTeardown).
                // Left uncapped, a corpse that ever passed through a live-ragdoll session inherits
                // this session's 1 m/s depenetration ceiling permanently — far too weak to push a
                // body back out of geometry it's overlapping, which reads as the body getting
                // "sucked into" a wall or floor instead of settling on top of it.
                session._originalMaxDepenetration[i] = rb.maxDepenetrationVelocity;
                session._isHandBone[i] = IsHandBone(rb.name);

                rb.solverIterations = Mathf.Max(rb.solverIterations, 16);
                rb.solverVelocityIterations = Mathf.Max(rb.solverVelocityIterations, 8);
                rb.maxDepenetrationVelocity = Mathf.Min(rb.maxDepenetrationVelocity, 1f);
                rb.maxAngularVelocity = Settings.MaxAngularVelocity.Value;

                // Unity's default Rigidbody.angularDrag (0.05) barely slows spin between our own
                // per-tick correction in Run() — RagdollKinetics (a separate, working SPT ragdoll
                // mod) instead tunes drag per bone category, heavier for the torso/pelvis than the
                // limbs, and reaches values this much higher. Ported directly; these are its
                // numbers, not independently re-derived.
                rb.angularDrag = GetAngularDragForBone(rb.name);
            }

            // Carries whatever motion LiveMotionSampler captured (recoil, mid-turn) into the fall
            // as a one-time initial velocity, instead of every bone starting dead-still. Applied
            // last, after every other per-rigidbody setup above (including maxAngularVelocity),
            // and clamped again to the same ceiling Run() enforces every tick afterward — hand/
            // wrist bones included, since a bad sample landing on the weapon-holding hand is
            // exactly the "weapon spins on its own" symptom, amplified by the muzzle's lever arm
            // the same way an uncapped per-tick hand velocity would be.
            if (LiveMotionSampler.TryTake(player, out Dictionary<string, Vector3> capturedVelocities))
            {
                foreach (var spawner in rigidbodySpawners)
                {
                    Rigidbody rb = spawner?.Rigidbody;
                    if (rb == null || !capturedVelocities.TryGetValue(spawner.name, out Vector3 angularVelocity))
                    {
                        continue;
                    }

                    float ceiling = IsHandBone(spawner.name)
                        ? Settings.MaxAngularVelocity.Value * 0.25f
                        : Settings.MaxAngularVelocity.Value;
                    rb.angularVelocity = Vector3.ClampMagnitude(angularVelocity, ceiling);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"Error spawning ragdoll physics for {player.ProfileId}: {ex}");
            // JointStability.Capture may have already mutated joint properties (breakForce,
            // projection, limit springs) before the failure — undo that here too, or they leak
            // onto a real Corpse this character inherits later (the exact leak JointStability's
            // own doc comment warns about; BeginTeardown does this in its own path, but a failed
            // TryStart never reaches BeginTeardown).
            session._jointStability.Restore();
            session.RestoreControl();
            return null;
        }

        session._coroutine = player.StartCoroutine(session.Run());
        Plugin.Log.LogInfo(
            $"[RagdollLifecycle] TryStart OK: profile={player.ProfileId} botOwnerFound={session._botOwnerDisabled} " +
            $"rigidbodies={rigidbodySpawners.Length} joints={jointSpawners.Length} " +
            $"isAliveAtStart={player.HealthController?.IsAlive}");
        return session;
    }

    /// <summary>Walks up from <paramref name="from"/> (e.g. PlayerBones.RightPalm) through its parent
    /// chain looking for the closest ancestor whose Transform belongs to one of this session's own
    /// ragdolled bones — ties the weapon-mirror anchor to whatever the actual rig hierarchy is,
    /// rather than guessing a bone name that may not match every skeleton's naming convention.</summary>
    private static Transform FindNearestRagdolledAncestor(Transform from, RigidbodySpawner[] rigidbodySpawners)
    {
        for (Transform t = from; t != null; t = t.parent)
        {
            for (int i = 0; i < rigidbodySpawners.Length; i++)
            {
                if (rigidbodySpawners[i] != null && rigidbodySpawners[i].transform == t)
                {
                    return t;
                }
            }
        }

        return null;
    }

    private static bool IsArmBone(string boneName) =>
        ContainsAny(boneName.ToLowerInvariant(), "forearm", "upperarm", "hand", "shoulder", "clavicle");

    private static bool IsHandBone(string boneName) =>
        ContainsAny(boneName.ToLowerInvariant(), "hand", "wrist");

    /// <summary>Ported from RagdollKinetics' GetJointProfile (github.com/Hysocs/ragdollkinetics-spt)
    /// — only the angularDrag column, the rest of its per-bone profile (twist/swing joint-limit
    /// scale) isn't used here since this session doesn't rebuild joints from scratch.</summary>
    private static float GetAngularDragForBone(string boneName)
    {
        string name = (boneName ?? string.Empty).ToLowerInvariant();
        if (ContainsAny(name, "calf", "shin", "lowerleg")) return 2.8f;
        if (ContainsAny(name, "forearm", "lowerarm")) return 2.3f;
        if (ContainsAny(name, "thigh", "upleg")) return 2.2f;
        if (ContainsAny(name, "upperarm", "shoulder")) return 1.8f;
        if (ContainsAny(name, "hand", "wrist")) return 1.7f;
        if (ContainsAny(name, "foot", "ankle")) return 2.0f;
        if (ContainsAny(name, "head", "neck")) return 1.6f;
        if (ContainsAny(name, "spine", "chest", "rib", "pelvis")) return 3.0f;
        return 2.0f;
    }

    private static bool ContainsAny(string value, params string[] fragments)
    {
        for (int i = 0; i < fragments.Length; i++)
        {
            if (value.Contains(fragments[i]))
            {
                return true;
            }
        }

        return false;
    }

    private IEnumerator Run()
    {
        float elapsed = 0f;
        var speeds = new float[_rigidbodySpawners.Length];

        RigidbodySpawner armSpawner = Settings.DebugLogging.Value
            ? _rigidbodySpawners.FirstOrDefault(s => s?.Rigidbody != null && s.Rigidbody.name.ToLowerInvariant().Contains("forearm"))
            : null;
        // Head/neck previously had zero telemetry — added once arm/head spin (not flying) became
        // the reported symptom, so a repeat test can actually show whether PoseReplayDrive helped.
        RigidbodySpawner headSpawner = Settings.DebugLogging.Value
            ? _rigidbodySpawners.FirstOrDefault(s => s?.Rigidbody != null && s.Rigidbody.name.ToLowerInvariant().Contains("head"))
            : null;
        int debugTick = 0;

        while (!_tornDown)
        {
            // WaitForFixedUpdate, not null: this loop's job is damping/clamping Rigidbody
            // velocities, which only change during physics steps. Yielding on the render frame
            // (yield return null) let multiple FixedUpdate physics steps run between two
            // corrections whenever render framerate trailed the physics rate — a joint-limit
            // impulse could drive angular velocity to 100+ rad/s within those uncorrected steps,
            // confirmed via Settings.DebugLogging (armAngVel logged as high as ~195 rad/s several
            // seconds into a session, far past both the manual clamp and rb.maxAngularVelocity).
            // One correction per physics step closes that window.
            yield return new WaitForFixedUpdate();

            bool shouldTeardown = false;
            bool settled = false;
            bool shouldFreeze = false;

            try
            {
                // A finishing hit on an already-Downed character can let a REAL death through
                // (see PMCoopRevive's HealthKillPatch's AllowRealDeath branch) while this session
                // is still mid-physics. The base game then owns these exact same
                // RigidbodySpawner/CharacterJointSpawner components for its own Corpse — but not
                // necessarily right away: across 4 observed field crashes, every one followed a
                // finishing hit on an already-Downed profile, but the actual crash landed anywhere
                // from 0.75s to 25s later (whatever the base game's own corpse/pool cleanup timing
                // is — not tied to any fixed delay this session controls). Every one crashed with a
                // NullReferenceException inside the game's own
                // PlayerRigidbodySleepHierarchy.TryPutToSleep(), leaving the body wherever physics
                // happened to be at that instant (flown away, or fighting a now-partially-torn-down
                // joint chain against a wall). Regardless of exactly when the base game reclaims
                // these components, we have no business still touching them once the character is
                // actually dead — checking aliveness every tick (instead of only discovering the
                // ownership loss via a crash however many seconds later) closes that whole window.
                if (_player == null)
                {
                    BeginTeardown(natural: false);
                    yield break;
                }

                // Debounced, unlike the null check above: PMCoopRevive's own Kill() interception
                // suppresses the base game's real death entirely for a fresh Downed hit, but there's
                // a narrow window between EnterDowned() and Stabilize() actually restoring health
                // where a completely unrelated health/effect tick elsewhere could read HealthController
                // as momentarily not-alive before Stabilize() catches up. A single false reading here
                // took the real-death branch below, which never calls RestoreControl() — leaving
                // BodyAnimatorCommon/EnabledAnimators disabled forever with physics never having
                // moved the character at all (T-pose, standing, weapon still tracking/firing since
                // whatever's left enabled keeps running). Two consecutive not-alive ticks (~0.033s
                // apart) still closes the real crash window described below just as fast for an
                // actual death, while giving a one-tick stabilize race a chance to resolve first.
                bool aliveNow = _player.HealthController != null && _player.HealthController.IsAlive;
                if (!aliveNow)
                {
                    _consecutiveNotAliveTicks++;
                    if (_consecutiveNotAliveTicks < 2)
                    {
                        continue;
                    }
                }
                else
                {
                    _consecutiveNotAliveTicks = 0;
                }

                if (!aliveNow)
                {
                    BeginTeardown(natural: false);
                    yield break;
                }

                // Time.deltaTime here would still read the last rendered frame's delta (Unity
                // resumes coroutines during the Update phase even after WaitForFixedUpdate) —
                // fixedDeltaTime matches the physics-step cadence this loop now actually runs on.
                elapsed += Time.fixedDeltaTime;

                if (!Settings.BareDiagnosticMode.Value)
                {
                    _poseReplayDrive.Tick(elapsed);
                }

                if (_sleepHierarchy != null && elapsed >= SleepGuardSeconds)
                {
                    foreach (PlayerRigidbodySleepHierarchy item in _sleepHierarchy)
                    {
                        item.TryPutToSleep();
                    }
                }

                for (int i = 0; i < _rigidbodySpawners.Length; i++)
                {
                    Rigidbody rb = _rigidbodySpawners[i]?.Rigidbody;
                    if (rb == null)
                    {
                        speeds[i] = 0f;
                        continue;
                    }

                    if (!Settings.BareDiagnosticMode.Value)
                    {
                        // Energetic damping only while actually moving — multiplying a sleeping
                        // body's already-near-zero velocity is pointless work, not a correctness
                        // issue, but there's no reason to pay for it every tick for every bone.
                        if (!rb.IsSleeping())
                        {
                            float damping = Settings.VelocityDampingPerTick.Value;
                            rb.angularVelocity *= damping;
                            rb.velocity *= damping;
                        }

                        // The hard ceiling, unlike the damping above, runs unconditionally — field
                        // logs caught a bone reading 42 rad/s against a configured 20 rad/s ceiling
                        // while this clamp was gated the same way the damping was, meaning
                        // whatever pushed it past the ceiling did so after this ran but before the
                        // debug log read it. Clamping regardless of sleep state closes that gap:
                        // a body that's asleep is at ~zero velocity anyway, so this is a no-op for
                        // it, and one that isn't gets capped no matter what woke it up this tick.
                        //
                        // Hand/wrist bones get a tighter ceiling than everything else: the held
                        // weapon is rigidly attached there with its muzzle ~0.5-0.7m out past the
                        // wrist as a lever arm, so even rotation within the general ceiling reads,
                        // at the muzzle tip, several times faster — reported as "the weapon spins
                        // around the wrist" separately from any single bone reading unusually hot
                        // in the debug log (arm/head telemetry alone can't see this; it's a
                        // consequence of what's attached, not of the hand bone's own speed).
                        float angularCeiling = _isHandBone[i]
                            ? Settings.MaxAngularVelocity.Value * 0.25f
                            : Settings.MaxAngularVelocity.Value;
                        if (rb.angularVelocity.magnitude > angularCeiling)
                        {
                            rb.angularVelocity = Vector3.ClampMagnitude(rb.angularVelocity, angularCeiling);
                        }

                        if (rb.velocity.magnitude > Settings.MaxLinearVelocity.Value)
                        {
                            rb.velocity = Vector3.ClampMagnitude(rb.velocity, Settings.MaxLinearVelocity.Value);
                        }
                    }

                    speeds[i] = Mathf.Max(rb.velocity.magnitude, rb.angularVelocity.magnitude);
                }

                // Manual mirror, not a Player API call: see the capture comment in TryStart for why.
                // Runs after the physics/clamp loop above so it reflects this tick's settled pose.
                if (_weaponRootAnim != null && _weaponAnchorBone != null)
                {
                    _weaponRootAnim.SetPositionAndRotation(
                        _weaponAnchorBone.position + _weaponAnchorBone.rotation * _weaponLocalOffset,
                        _weaponAnchorBone.rotation * _weaponLocalRotation);
                }

                debugTick++;
                if (debugTick % 15 == 0)
                {
                    if (armSpawner?.Rigidbody != null)
                    {
                        LogDebugState("arm", armSpawner.Rigidbody, elapsed);
                    }

                    if (headSpawner?.Rigidbody != null)
                    {
                        LogDebugState("head", headSpawner.Rigidbody, elapsed);
                    }
                }

                bool quiet = _settleDetector.Tick(speeds, _options.VelocityThreshold);
                settled = quiet && elapsed >= MinSettleSeconds;
                bool timedOut = elapsed >= _options.MaxDurationSeconds;

                if (!Settings.BareDiagnosticMode.Value && _drift != null && elapsed >= MinSettleSeconds)
                {
                    if (_drift.CheckAndRecover(Settings.MaxDriftMeters.Value))
                    {
                        _settleDetector.Reset();
                        settled = false;
                        _lastDriftRebase = elapsed;
                    }
                    else if (elapsed - _lastDriftRebase >= DriftRebaseIntervalSeconds)
                    {
                        _drift.Rebase();
                        _lastDriftRebase = elapsed;
                    }
                }

                if (settled && _options.KeepRagdolledUntilRestored)
                {
                    shouldFreeze = true;
                }
                else
                {
                    shouldTeardown = timedOut || settled;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Error in ragdoll physics loop for {_player?.ProfileId}: {ex}");
                BeginTeardown(natural: false);
                yield break;
            }

            if (shouldFreeze)
            {
                Freeze();
                yield break;
            }

            if (shouldTeardown)
            {
                BeginTeardown(natural: settled);
                yield break;
            }
        }
    }

    private void LogDebugState(string label, Rigidbody bone, float elapsed)
    {
        Collider[] overlaps = Physics.OverlapSphere(bone.position, 0.15f);
        string contacts = string.Join(", ", overlaps
            .Where(c => c != null && (_ownColliders == null || !_ownColliders.Contains(c)))
            .Select(c => c.name)
            .Distinct());

        Plugin.Log.LogInfo(string.Format(
            "[RagdollDebug] {0} {1} t={2:0.00} angVel={3:0.00} rot={4} bodyAnim={5} armsAnim={6} weaponAnim={7} charController={8} botOwnerEnabled={9} moverPause={10} externalContacts=[{11}]",
            _player?.ProfileId, label, elapsed, bone.angularVelocity.magnitude, bone.rotation.eulerAngles,
            _player.BodyAnimatorCommon != null && _player.BodyAnimatorCommon.enabled,
            _player.ArmsAnimatorCommon != null && _player.ArmsAnimatorCommon.enabled,
            _player.ProceduralWeaponAnimation != null && _player.ProceduralWeaponAnimation.enabled,
            _player._characterController != null && _player._characterController.isEnabled,
            _player.AIData?.BotOwner != null && _player.AIData.BotOwner.enabled,
            _player.AIData?.BotOwner?.Mover?.Pause,
            contacts));
    }

    /// <summary>Stops the body where it physically is without handing control back — used once a
    /// KeepRagdolledUntilRestored session settles, so it holds its pose until an explicit
    /// OnRecovered instead of standing back up early or slowly creeping under residual physics
    /// noise.</summary>
    private void Freeze()
    {
        if (_frozen)
        {
            return;
        }

        _frozen = true;

        foreach (var spawner in _rigidbodySpawners)
        {
            Rigidbody rb = spawner?.Rigidbody;
            if (rb == null)
            {
                continue;
            }

            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic = true;
        }
    }

    public void BeginTeardown(bool natural)
    {
        if (_tornDown)
        {
            return;
        }

        _tornDown = true;
        _drift?.Stop();

        try
        {
            if (_player != null && _player.gameObject != null && _coroutine != null)
            {
                _player.StopCoroutine(_coroutine);
            }

            // Unconditional, before the stillOurs branch below: a finishing hit mid-session hands
            // these same Rigidbody components straight to the base game's own Corpse without ever
            // running RestoreControl (see the stillOurs check), so restoring this here is the only
            // place both the recovery path and the real-death path both pass through.
            if (_originalMaxDepenetration != null)
            {
                for (int i = 0; i < _rigidbodySpawners.Length; i++)
                {
                    Rigidbody rb = _rigidbodySpawners[i]?.Rigidbody;
                    if (rb != null)
                    {
                        rb.maxDepenetrationVelocity = _originalMaxDepenetration[i];
                    }
                }
            }

            bool stillOurs = _player != null && _player.HealthController != null && _player.HealthController.IsAlive;
            Plugin.Log.LogInfo(
                $"[RagdollLifecycle] BeginTeardown: profile={_player?.ProfileId} natural={natural} stillOurs={stillOurs} " +
                $"botOwnerWasDisabled={_botOwnerDisabled} animatorsWereDisabled={_animatorsDisabled}");

            // REVERTED (twice now — see project memory before trying either of these again):
            // 1) RestoreAnimatorsOnly() in the else-branch — re-enabling BodyAnimatorCommon/
            //    EnabledAnimators for what turned out to be a real corpse made every corpse stand
            //    up in a T-pose at death (Animator's LateUpdate re-stamps bind pose over the
            //    ragdoll's physics every frame).
            // 2) Unconditionally Remove()-ing joints/rigidbodies in BOTH branches (attempting to
            //    hand physics back before Player.OnDead()'s own CreateCorpse() via a prefix patch)
            //    — made things worse again; the exact mechanism isn't nailed down, so don't touch
            //    a real corpse's ragdoll/animator state AT ALL from this session. Physics/animator
            //    handback below runs ONLY when stillOurs — a real death is the base game's exclusive
            //    territory from here on, full stop.
            if (stillOurs)
            {
                _jointStability.Restore();

                foreach (var j in _jointSpawners)
                {
                    j?.Remove();
                }

                foreach (var r in _rigidbodySpawners)
                {
                    r?.Remove();
                }

                if (_armorPlates != null)
                {
                    foreach (var plate in _armorPlates)
                    {
                        if (plate != null)
                        {
                            plate.gameObject.SetActive(true);
                        }
                    }
                }

                // Deliberately last: the root Transform never moves during the physics phase (only
                // the individual bones do), so it's still sitting exactly where the character was
                // when it went Downed. RestoreControl() re-enables the CharacterController and (for
                // bots) BotOwner — both of which own position/navmesh state of their own that was
                // frozen at that same pre-ragdoll spot while disabled. Re-enabling either BEFORE this
                // resync risked one of them re-asserting its stale cached position over top of our
                // teleport the instant it came back on, which read as the character "teleporting
                // back to where they were originally shot" on recovery instead of standing up where
                // the body actually settled. Calling this after RestoreControl() makes our teleport
                // the last write, unconditionally.
                RestoreControl();
                ResyncRootToRagdollPose();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"Error tearing down ragdoll for {_player?.ProfileId}: {ex}");
            RestoreControl();
        }
        finally
        {
            _onEnded?.Invoke(natural);
        }
    }

    /// <summary>The invisible root transform (what CharacterController/Animator resume from) never
    /// moves during the physics phase — only the individually-simulated bones do. Snaps the root to
    /// where the pelvis bone's rigidbody actually settled, on the horizontal plane (keeping the
    /// root's own Y so re-enabling the CharacterController doesn't drop/launch it), before flipping
    /// animators and the controller back on.
    ///
    /// Uses Player.Teleport(...) rather than writing MovementContext.TransformPosition directly —
    /// Teleport does that same write internally but also resets height interpolation and "flying"
    /// state, which a raw write skips. onServerToo: true so a Fika-authoritative host also
    /// broadcasts the corrected position.
    ///
    /// Y comes from the pelvis's own settled height plus _rootMinusPelvisY (the root-to-pelvis
    /// offset captured in TryStart, before the Animator disabled) rather than the pre-ragdoll root Y
    /// directly, since that Y is frozen wherever the character was when the ragdoll STARTED and the
    /// raw pelvis Y alone would sink the character partway into the floor.</summary>
    private void ResyncRootToRagdollPose()
    {
        Transform pelvis = _player.PlayerBody.PlayerBones.Pelvis?.Original;
        if (pelvis == null)
        {
            return;
        }

        Vector3 target = pelvis.position;
        _player.Teleport(new Vector3(target.x, target.y + _rootMinusPelvisY, target.z), onServerToo: true);
    }

    private void RestoreControl()
    {
        try
        {
            if (_player == null)
            {
                return;
            }

            if (_animatorsDisabled)
            {
                if (_player.BodyAnimatorCommon != null)
                {
                    _player.BodyAnimatorCommon.enabled = true;
                }

                if (_player.ArmsAnimatorCommon != null)
                {
                    _player.ArmsAnimatorCommon.enabled = true;
                }
            }

            if (_controllerDisabled && _player._characterController != null)
            {
                _player._characterController.isEnabled = true;
            }

            if (_weaponAnimDisabled && _player.ProceduralWeaponAnimation != null)
            {
                _player.ProceduralWeaponAnimation.enabled = true;
            }

            if (_fastAnimatorStopped && _player.PlayerBody?.PlayerBones?.PlayableAnimator != null)
            {
                _player.PlayerBody.PlayerBones.PlayableAnimator.Play();
            }

            if (_botOwnerDisabled && _player.AIData?.BotOwner != null)
            {
                _player.AIData.BotOwner.enabled = _botOwnerWasEnabled;
            }

            if (_enabledAnimatorsChanged)
            {
                _player.EnabledAnimators = _originalEnabledAnimators;
            }

            if (_originalLayers != null)
            {
                for (int i = 0; i < _rigidbodySpawners.Length; i++)
                {
                    GameObject go = _rigidbodySpawners[i]?.gameObject;
                    if (go != null)
                    {
                        go.layer = _originalLayers[i];
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"Error restoring animator/controller after ragdoll: {ex}");
        }
    }
}
