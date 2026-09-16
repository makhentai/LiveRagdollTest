using System;
using UnityEngine;

namespace LiveRagdollTest.Internal;

/// <summary>The pure rotation-delta-to-angular-velocity math LiveMotionSampler uses. The actual
/// computation (<see cref="Compute"/>) takes and returns plain floats rather than
/// UnityEngine.Quaternion/Vector3 — several of those types' own methods (Euler, Inverse, the *
/// operator, ToAngleAxis) are native ECall stubs that only run inside a live Unity process
/// (confirmed: calling them from a standalone xunit run throws SecurityException), which makes
/// them impossible to unit-test outside the game. Hand-rolling the quaternion algebra with plain
/// arithmetic and System.Math keeps this core logic testable; <see cref="FromRotationDelta"/> is
/// the thin Unity-facing wrapper around it and isn't itself unit-tested, same as other
/// Unity-glue code in this mod.</summary>
internal static class AngularVelocityMath
{
    // A per-frame rotation jump bigger than this is treated as a pose glitch/teleport, not real
    // motion — same reasoning as RagdollKinetics' own 18-degree cutoff for the same kind of
    // sample.
    internal const float GlitchAngleDegrees = 18f;

    // Sampling-side sanity cap only — LiveMotionSampler's caller clamps again to whatever the
    // session's actual velocity ceiling is before ever writing this to a Rigidbody.
    internal const float MaxAngularVelocity = 60f;

    internal static Vector3 FromRotationDelta(Quaternion previous, Quaternion current, float inverseDelta)
    {
        (float x, float y, float z) = Compute(
            previous.x, previous.y, previous.z, previous.w,
            current.x, current.y, current.z, current.w,
            inverseDelta);
        return new Vector3(x, y, z);
    }

    /// <summary>Angular velocity (radians/second, as x/y/z components) implied by a unit
    /// quaternion changing from (previousX..previousW) to (currentX..currentW) over
    /// 1/<paramref name="inverseDelta"/> seconds. Returns zero for a degenerate (near-zero-angle)
    /// rotation or a same-frame jump larger than <see cref="GlitchAngleDegrees"/>.
    ///
    /// Deliberately not the same formula RagdollKinetics' RagdollPoseSampler uses for the
    /// equivalent capture (github.com/Hysocs/ragdollkinetics-spt) — that mod multiplies
    /// Quaternion.ToAngleAxis()'s angle (degrees) directly by 1/deltaTime, which is degrees/second;
    /// fine for their own use as a joint-drive target, but wrong for Unity's
    /// Rigidbody.angularVelocity, which is radians/second. This works in radians throughout.</summary>
    internal static (float x, float y, float z) Compute(
        float previousX, float previousY, float previousZ, float previousW,
        float currentX, float currentY, float currentZ, float currentW,
        float inverseDelta)
    {
        // Inverse of a unit quaternion is its conjugate.
        float invX = -previousX;
        float invY = -previousY;
        float invZ = -previousZ;
        float invW = previousW;

        // delta = current * inverse(previous) — standard Hamilton product.
        float w = currentW * invW - currentX * invX - currentY * invY - currentZ * invZ;
        float x = currentW * invX + currentX * invW + currentY * invZ - currentZ * invY;
        float y = currentW * invY - currentX * invZ + currentY * invW + currentZ * invX;
        float z = currentW * invZ + currentX * invY - currentY * invX + currentZ * invW;

        // Defensive re-normalization: inputs should already be unit quaternions, but composed
        // floating-point drift can nudge the product off by enough to matter once it feeds acos.
        double normSq = (double)w * w + (double)x * x + (double)y * y + (double)z * z;
        if (normSq < 1e-12)
        {
            return (0f, 0f, 0f);
        }

        double invNorm = 1.0 / Math.Sqrt(normSq);
        double nw = w * invNorm;
        double nx = x * invNorm;
        double ny = y * invNorm;
        double nz = z * invNorm;

        double clampedW = Math.Max(-1.0, Math.Min(1.0, nw));
        double angleRadians = 2.0 * Math.Acos(clampedW);
        if (angleRadians > Math.PI)
        {
            angleRadians -= 2.0 * Math.PI;
        }

        double angleDegrees = angleRadians * (180.0 / Math.PI);
        double sinHalfAngle = Math.Sqrt(Math.Max(0.0, 1.0 - clampedW * clampedW));
        if (sinHalfAngle < 1e-6 || Math.Abs(angleDegrees) > GlitchAngleDegrees)
        {
            return (0f, 0f, 0f);
        }

        double axisX = nx / sinHalfAngle;
        double axisY = ny / sinHalfAngle;
        double axisZ = nz / sinHalfAngle;

        double velocityX = axisX * angleRadians * inverseDelta;
        double velocityY = axisY * angleRadians * inverseDelta;
        double velocityZ = axisZ * angleRadians * inverseDelta;

        double magnitude = Math.Sqrt(velocityX * velocityX + velocityY * velocityY + velocityZ * velocityZ);
        if (magnitude > MaxAngularVelocity && magnitude > 1e-12)
        {
            double scale = MaxAngularVelocity / magnitude;
            velocityX *= scale;
            velocityY *= scale;
            velocityZ *= scale;
        }

        return ((float)velocityX, (float)velocityY, (float)velocityZ);
    }
}
