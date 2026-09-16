using System.Collections.Generic;
using UnityEngine;

namespace LiveRagdollTest.Internal;

/// <summary>Only Capture()/Restore() are called from RagdollSession — passive, one-time, cheap.
/// Sets breakForce/breakTorque to infinity while a session is live so a CharacterJoint can't be
/// destroyed outright by Unity mid-impact, and MUST be reverted regardless: those are live
/// properties on the same CharacterJoint component a subsequent real Corpse can inherit if the
/// character actually dies while still Downed. Restore() undoes only what Capture() captured.
///
/// Also enables joint projection and disables preprocessing — per Unity's own ragdoll-stability
/// guidance (docs.unity3d.com/Manual/RagdollStability.html): without projection, a joint whose
/// current pose is far outside its authored twist/swing limits (exactly what happens when physics
/// takes over a character captured mid-animation — weapon raised, arms bent well past a relaxed
/// pose) gets corrected by the solver injecting velocity/force every step instead of snapping the
/// position back directly, which reads as the body flinging through the air or climbing up a wall
/// as the solver repeatedly "fixes" a constraint violation it can only satisfy with more force.
/// Projection makes that correction positional instead. RagdollKinetics (a separate, working
/// SPT ragdoll mod also reviewed for this mod) independently reaches the same settings
/// (projectionDistance 0.01, projectionAngle 2) for its own converted ConfigurableJoints; applied
/// here directly to the native CharacterJoint the base game's CharacterJointSpawner produces,
/// since this session never converts joint type the way that mod does.
///
/// Also softens the twist/swing limit springs (RagdollKinetics' own JointLimitStiffness setting
/// converts to spring=100/damper=20 — same values used here) so a joint approaching its limit
/// gets progressively resisted instead of hitting a hard stop, which is the other half of the
/// same "captured mid-animation, already near/past limit" instability class projection alone
/// doesn't fully cover.
///
/// Also normalizes massScale/connectedMassScale (joint.massScale = connectedBody.mass /
/// thisBody.mass, connectedMassScale = 1) — Unity's ragdoll-stability guidance flags a large mass
/// ratio between two jointed bodies (e.g. a torso vs. a hand) as a jitter source; this is the same
/// correction VisceralCombat (a separate, established SPT ragdoll mod, reviewed for this mod)
/// applies via the identical formula when it patches the base game's own ragdoll joint creation —
/// independent confirmation this and the projection settings above are the right knobs.</summary>
internal sealed class JointStability
{
    private sealed class JointState
    {
        internal CharacterJoint Joint;
        internal float BreakForce;
        internal float BreakTorque;
        internal bool EnableProjection;
        internal float ProjectionDistance;
        internal float ProjectionAngle;
        internal bool EnablePreprocessing;
        internal SoftJointLimitSpring TwistLimitSpring;
        internal SoftJointLimitSpring SwingLimitSpring;
        internal float MassScale;
        internal float ConnectedMassScale;
    }

    private readonly List<JointState> _jointStates = new();

    internal void Capture(IEnumerable<CharacterJointSpawner> spawners)
    {
        if (spawners == null)
        {
            return;
        }

        foreach (CharacterJointSpawner spawner in spawners)
        {
            CharacterJoint joint = spawner?.GetComponent<CharacterJoint>();
            if (joint == null)
            {
                continue;
            }

            _jointStates.Add(new JointState
            {
                Joint = joint,
                BreakForce = joint.breakForce,
                BreakTorque = joint.breakTorque,
                EnableProjection = joint.enableProjection,
                ProjectionDistance = joint.projectionDistance,
                ProjectionAngle = joint.projectionAngle,
                EnablePreprocessing = joint.enablePreprocessing,
                TwistLimitSpring = joint.twistLimitSpring,
                SwingLimitSpring = joint.swingLimitSpring,
                MassScale = joint.massScale,
                ConnectedMassScale = joint.connectedMassScale,
            });

            joint.breakForce = float.PositiveInfinity;
            joint.breakTorque = float.PositiveInfinity;
            joint.enableProjection = true;
            joint.projectionDistance = 0.01f;
            joint.projectionAngle = 2f;
            joint.enablePreprocessing = false;

            SoftJointLimitSpring twist = joint.twistLimitSpring;
            twist.spring = 100f;
            twist.damper = 20f;
            joint.twistLimitSpring = twist;

            SoftJointLimitSpring swing = joint.swingLimitSpring;
            swing.spring = 100f;
            swing.damper = 20f;
            joint.swingLimitSpring = swing;

            Rigidbody body = joint.GetComponent<Rigidbody>();
            if (body != null && joint.connectedBody != null && body.mass > 0f)
            {
                joint.massScale = joint.connectedBody.mass / body.mass;
                joint.connectedMassScale = 1f;
            }
        }
    }

    internal void Restore()
    {
        for (int i = 0; i < _jointStates.Count; i++)
        {
            JointState state = _jointStates[i];
            if (state.Joint == null)
            {
                continue;
            }

            state.Joint.breakForce = state.BreakForce;
            state.Joint.breakTorque = state.BreakTorque;
            state.Joint.enableProjection = state.EnableProjection;
            state.Joint.projectionDistance = state.ProjectionDistance;
            state.Joint.projectionAngle = state.ProjectionAngle;
            state.Joint.enablePreprocessing = state.EnablePreprocessing;
            state.Joint.twistLimitSpring = state.TwistLimitSpring;
            state.Joint.swingLimitSpring = state.SwingLimitSpring;
            state.Joint.massScale = state.MassScale;
            state.Joint.connectedMassScale = state.ConnectedMassScale;
        }

        _jointStates.Clear();
    }
}
