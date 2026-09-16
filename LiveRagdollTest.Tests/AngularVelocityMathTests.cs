using System;
using LiveRagdollTest.Internal;
using Xunit;

public class AngularVelocityMathTests
{
    // Quaternion for a rotation of `degrees` around the Y axis: (0, sin(half), 0, cos(half)).
    // Computed by hand (not via UnityEngine.Quaternion.Euler) since several of that type's own
    // methods are native ECall stubs that only run inside a live Unity process and throw
    // SecurityException in a standalone test run — see AngularVelocityMath's own doc comment.
    private static (float x, float y, float z, float w) YRotation(double degrees)
    {
        double half = degrees * Math.PI / 180.0 / 2.0;
        return (0f, (float)Math.Sin(half), 0f, (float)Math.Cos(half));
    }

    private static readonly (float x, float y, float z, float w) Identity = (0f, 0f, 0f, 1f);

    [Fact]
    public void Compute_NoRotation_ReturnsZero()
    {
        var (x, y, z) = AngularVelocityMath.Compute(
            Identity.x, Identity.y, Identity.z, Identity.w,
            Identity.x, Identity.y, Identity.z, Identity.w,
            inverseDelta: 60f);

        Assert.Equal(0f, x);
        Assert.Equal(0f, y);
        Assert.Equal(0f, z);
    }

    [Fact]
    public void Compute_SmallKnownRotation_ReturnsExpectedRadiansPerSecond()
    {
        // 6 degrees around Y in one 1/60s frame is 360 deg/s = 2*pi rad/s (~6.283).
        var current = YRotation(6.0);

        var (x, y, z) = AngularVelocityMath.Compute(
            Identity.x, Identity.y, Identity.z, Identity.w,
            current.x, current.y, current.z, current.w,
            inverseDelta: 60f);

        Assert.True(y > 6.0f && y < 6.5f, $"expected ~2*pi (~6.28) rad/s on Y, got {y}");
        Assert.Equal(0f, x, 3);
        Assert.Equal(0f, z, 3);
    }

    [Fact]
    public void Compute_RotationDirectionIsPreserved()
    {
        var forward = YRotation(6.0);
        var backward = YRotation(-6.0);

        var (_, forwardY, _) = AngularVelocityMath.Compute(
            Identity.x, Identity.y, Identity.z, Identity.w,
            forward.x, forward.y, forward.z, forward.w,
            inverseDelta: 60f);
        var (_, backwardY, _) = AngularVelocityMath.Compute(
            Identity.x, Identity.y, Identity.z, Identity.w,
            backward.x, backward.y, backward.z, backward.w,
            inverseDelta: 60f);

        Assert.True(forwardY > 0f);
        Assert.True(backwardY < 0f);
    }

    [Fact]
    public void Compute_GlitchSizedJump_ReturnsZero()
    {
        // A same-frame jump past GlitchAngleDegrees (18) is treated as a teleport/reset, not motion.
        var current = YRotation(90.0);

        var (x, y, z) = AngularVelocityMath.Compute(
            Identity.x, Identity.y, Identity.z, Identity.w,
            current.x, current.y, current.z, current.w,
            inverseDelta: 60f);

        Assert.Equal(0f, x);
        Assert.Equal(0f, y);
        Assert.Equal(0f, z);
    }

    [Fact]
    public void Compute_JustUnderGlitchThreshold_IsNotZero()
    {
        var current = YRotation(17.0);

        var (_, y, _) = AngularVelocityMath.Compute(
            Identity.x, Identity.y, Identity.z, Identity.w,
            current.x, current.y, current.z, current.w,
            inverseDelta: 60f);

        Assert.NotEqual(0f, y);
    }

    [Fact]
    public void Compute_ResultIsClampedToMaxAngularVelocity()
    {
        // A large-ish rotation under a huge inverseDelta (tiny frame time) implies an enormous
        // velocity — must still come back clamped, never an unbounded spike written to physics.
        var current = YRotation(17.0);

        var (x, y, z) = AngularVelocityMath.Compute(
            Identity.x, Identity.y, Identity.z, Identity.w,
            current.x, current.y, current.z, current.w,
            inverseDelta: 100000f);

        double magnitude = Math.Sqrt((double)x * x + (double)y * y + (double)z * z);
        Assert.True(magnitude <= AngularVelocityMath.MaxAngularVelocity + 0.001);
    }

    [Fact]
    public void Compute_IdenticalQuaternionsDifferentSign_ReturnsZero()
    {
        // Quaternion q and -q represent the same rotation; must not be read as a 360-degree spin.
        var negatedIdentity = (-Identity.x, -Identity.y, -Identity.z, -Identity.w);

        var (x, y, z) = AngularVelocityMath.Compute(
            Identity.x, Identity.y, Identity.z, Identity.w,
            negatedIdentity.Item1, negatedIdentity.Item2, negatedIdentity.Item3, negatedIdentity.Item4,
            inverseDelta: 60f);

        Assert.Equal(0f, x);
        Assert.Equal(0f, y);
        Assert.Equal(0f, z);
    }

    [Fact]
    public void Compute_ZeroInverseDelta_DoesNotThrowOrProduceNaN()
    {
        var current = YRotation(6.0);

        var (x, y, z) = AngularVelocityMath.Compute(
            Identity.x, Identity.y, Identity.z, Identity.w,
            current.x, current.y, current.z, current.w,
            inverseDelta: 0f);

        Assert.False(float.IsNaN(x));
        Assert.False(float.IsNaN(y));
        Assert.False(float.IsNaN(z));
    }
}
