using System.Collections.Generic;
using UnityEngine;

namespace LiveRagdollTest.Internal;

/// <summary>Actively resists the gap between each bone's current rotation and the pose it was
/// captured in, for a short decaying window after physics takes over — the concept (not the code)
/// is ported from RagdollKinetics' ApplyAnimationDrive/BoneReplayStrength
/// (github.com/Hysocs/ragdollkinetics-spt). That mod converts CharacterJoint to ConfigurableJoint
/// to get a native slerpDrive; this session doesn't do that swap — RigidbodySpawner/
/// CharacterJointSpawner.Remove() (base-game code, needed on recovery to hand bones back to the
/// live Animator) may have assumptions about the joint type it originally spawned, and getting
/// that wrong risks a worse regression than the one being fixed here. Applying corrective torque
/// directly to the Rigidbody instead gets the same "hold near the last pose, then let go" result
/// without touching the joint component or its spawner-managed lifecycle at all.
///
/// This closes the gap the joint-limit fixes (projection/springs/drag in JointStability) didn't:
/// a bone captured well outside its joint limits (arms mid-weapon-raise, head/neck mid-aim) had
/// nothing pulling it back toward anything resembling its last pose — projection stops it flying
/// away, but nothing stopped it spinning in place near the limit boundary. Arm bones' captured
/// pose is whatever RagdollSession.TryStart already reset them to (identity local rotation) since
/// Capture() runs after that reset; every other bone (head/neck included, previously with zero
/// mitigation) targets its own actual last-animated local rotation.
///
/// Also pulls a joint's two anchor points back together with a corrective force when they
/// separate — field video caught a limb visibly stretched several meters across a room (a joint
/// anchor pulling apart, not high angular velocity — the angular-velocity clamp elsewhere in this
/// session can't see or stop this at all). The old LiveRagdoll mod had the same idea
/// (RepairExcessiveSeparation, ported from TraumaCore) but wrote position and zeroed velocity
/// directly, which is exactly as invisible to velocity-based safeguards as the problem it was
/// fixing — confirmed independently by the session maintaining that mod. Using AddForce instead
/// keeps the correction inside physics: it shows up in Rigidbody.velocity like everything else,
/// so the existing velocity clamp still applies to it rather than fighting an invisible write.</summary>
internal sealed class PoseReplayDrive
{
    // Originally 2.5s. Field video showed a bot that was captured mid-kneel-and-shoot held in
    // that exact combat pose — one knee down, arms out — for well over a second after the rest of
    // the ragdoll had already collapsed, since this torque actively resists gravity pulling any
    // bone away from wherever it was captured. The joint-limit oscillation this class exists to
    // fix (see class doc) settles in a small fraction of a second once physics takes over; nothing
    // about it needs multiple seconds of active holding, and holding a real (not synthetic) last
    // pose this long just reads as "frozen mid-action" instead of collapsing.
    private const float DecaySeconds = 0.4f;

    // Arm bones' captured target isn't a real last-animated pose — RagdollSession.TryStart resets
    // them to Quaternion.identity first specifically to stop the weapon-grip IK's carryover torque
    // (see the comment there), and Capture() runs after that reset. Originally 0.2s to bridge the
    // one-tick IK handoff the identity reset exists for — but VisualPassSuppressPatch now skips
    // Player.VisualPass entirely for the whole session, and that's the ONLY place IkProcess/
    // IkApply/_limbs[] ever run, so that IK block structurally cannot execute during this session
    // at all anymore; the bridge it was sized for is moot. Left holding this long past that point
    // actively fights whatever real motion LiveMotionSampler captured (a running/turning/shooting
    // bot has real per-bone angular velocity applied right after this same reset — see TryStart) —
    // every bot converges back toward the same fixed identity pose for whatever's left of this
    // window regardless of how it actually died, reading as "every ragdoll collapses the same way"
    // even though the physics underneath isn't actually identical. Cut to a token safety margin
    // instead of removed outright, since this exact area has a real history of surprises (see
    // project memory) and a bare instant of bridging is cheap insurance for near-zero cost.
    private const float ArmDecaySeconds = 0.05f;
    private const float SpringConstant = 40f;
    private const float DampingConstant = 8f;
    private const float MaxTorque = 60f;

    // A joint's own solver already tries to keep separation near zero; these only step in once
    // it visibly fails to. 0.03 is generous slack for normal constraint solving noise — small
    // enough to catch a stretch long before it's the multi-meter kind seen on video.
    private const float SeparationThreshold = 0.03f;
    private const float SeparationSpring = 200f;
    private const float MaxSeparationForce = 40f;

    private sealed class BoneState
    {
        internal Rigidbody Body;
        internal Rigidbody Parent;
        internal CharacterJoint Joint;
        internal Quaternion CapturedLocalRotation;
        internal bool IsArm;
    }

    private readonly List<BoneState> _bones = new();

    internal void Capture(IEnumerable<CharacterJointSpawner> spawners)
    {
        if (spawners == null)
        {
            return;
        }

        foreach (CharacterJointSpawner spawner in spawners)
        {
            CharacterJoint joint = spawner?.GetComponent<CharacterJoint>();
            Rigidbody body = joint != null ? joint.GetComponent<Rigidbody>() : null;
            Rigidbody parent = joint != null ? joint.connectedBody : null;
            if (body == null || parent == null)
            {
                continue;
            }

            _bones.Add(new BoneState
            {
                Body = body,
                Parent = parent,
                Joint = joint,
                CapturedLocalRotation = Quaternion.Inverse(parent.rotation) * body.rotation,
                IsArm = IsArmBone(body.name),
            });
        }
    }

    // Mirrors RagdollSession.IsArmBone — kept as its own copy rather than a shared reference since
    // the two call sites belong to different concerns (initial pose reset vs. ongoing pose hold)
    // and this one only ever needs to answer "does this bone's captured target come from the
    // identity reset, not a real pose".
    private static bool IsArmBone(string boneName) =>
        boneName != null && (
            boneName.IndexOf("forearm", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
            boneName.IndexOf("upperarm", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
            boneName.IndexOf("hand", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
            boneName.IndexOf("shoulder", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
            boneName.IndexOf("clavicle", System.StringComparison.OrdinalIgnoreCase) >= 0);

    /// <summary>Call once per physics tick. The pose-replay torque decays to nothing past
    /// DecaySeconds for most bones (physics runs fully unassisted for rotation from then on) — arm
    /// bones use the much shorter ArmDecaySeconds instead, since their captured target is the
    /// artificial identity reset, not a real pose worth holding. Anchor-separation correction below
    /// has no such expiry — a joint can stretch at any point in a session, not only early on, so it
    /// stays live for as long as this drive is ticked.</summary>
    internal void Tick(float elapsed)
    {
        float rotationStrength = RotationStrength(elapsed, DecaySeconds);
        float armRotationStrength = RotationStrength(elapsed, ArmDecaySeconds);

        foreach (BoneState bone in _bones)
        {
            if (bone.Body == null || bone.Parent == null || bone.Body.isKinematic || bone.Body.IsSleeping())
            {
                continue;
            }

            float strength = bone.IsArm ? armRotationStrength : rotationStrength;
            if (strength > 0f)
            {
                Quaternion currentLocal = Quaternion.Inverse(bone.Parent.rotation) * bone.Body.rotation;
                Quaternion error = bone.CapturedLocalRotation * Quaternion.Inverse(currentLocal);
                error.ToAngleAxis(out float angle, out Vector3 axis);
                if (angle > 180f)
                {
                    angle -= 360f;
                }

                if (IsFinite(axis) && Mathf.Abs(angle) >= 0.01f)
                {
                    Vector3 torque = axis.normalized * (angle * Mathf.Deg2Rad) * SpringConstant * strength
                        - bone.Body.angularVelocity * DampingConstant * strength;
                    bone.Body.AddTorque(Vector3.ClampMagnitude(torque, MaxTorque * strength), ForceMode.Acceleration);
                }
            }

            if (bone.Joint == null)
            {
                continue;
            }

            Vector3 anchorWorld = bone.Body.position + bone.Body.rotation * bone.Joint.anchor;
            Vector3 connectedAnchorWorld = bone.Parent.position + bone.Parent.rotation * bone.Joint.connectedAnchor;
            Vector3 separation = connectedAnchorWorld - anchorWorld;
            float distance = separation.magnitude;
            if (distance <= SeparationThreshold || !IsFinite(separation))
            {
                continue;
            }

            Vector3 pull = separation.normalized * Mathf.Min(distance * SeparationSpring, MaxSeparationForce);
            bone.Body.AddForce(pull, ForceMode.Acceleration);
        }
    }

    // Ease out rather than a linear fade, so the handoff to free physics isn't an abrupt cut.
    private static float RotationStrength(float elapsed, float decaySeconds)
    {
        float t = 1f - Mathf.Clamp01(elapsed / decaySeconds);
        return t * t * (3f - 2f * t);
    }

    private static bool IsFinite(Vector3 value) =>
        !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
        !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
        !float.IsNaN(value.z) && !float.IsInfinity(value.z);
}
