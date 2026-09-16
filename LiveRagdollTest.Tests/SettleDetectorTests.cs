using System;
using LiveRagdollTest.Internal;
using Xunit;

public class SettleDetectorTests
{
    [Fact]
    public void Tick_ReturnsFalse_WhileAnySpeedAboveThreshold()
    {
        var detector = new SettleDetector();
        Assert.False(detector.Tick(new[] { 0.01f, 2.0f, 0.01f }, threshold: 0.15f));
    }

    [Fact]
    public void Tick_ReturnsFalse_UntilThreeConsecutiveCallsAreAllBelowThreshold()
    {
        var detector = new SettleDetector();
        Assert.False(detector.Tick(new[] { 0.01f }, threshold: 0.15f));
        Assert.False(detector.Tick(new[] { 0.01f }, threshold: 0.15f));
        Assert.True(detector.Tick(new[] { 0.01f }, threshold: 0.15f));
    }

    [Fact]
    public void Tick_ResetsStreak_WhenASpeedSpikesBackUp()
    {
        var detector = new SettleDetector();
        Assert.False(detector.Tick(new[] { 0.01f }, threshold: 0.15f));
        Assert.False(detector.Tick(new[] { 0.01f }, threshold: 0.15f));
        Assert.False(detector.Tick(new[] { 5.0f }, threshold: 0.15f));
        Assert.False(detector.Tick(new[] { 0.01f }, threshold: 0.15f));
        Assert.False(detector.Tick(new[] { 0.01f }, threshold: 0.15f));
        Assert.True(detector.Tick(new[] { 0.01f }, threshold: 0.15f));
    }

    [Fact]
    public void Tick_EmptySpeedArray_CountsAsSettledImmediately()
    {
        var detector = new SettleDetector();
        detector.Tick(Array.Empty<float>(), threshold: 0.15f);
        detector.Tick(Array.Empty<float>(), threshold: 0.15f);
        Assert.True(detector.Tick(Array.Empty<float>(), threshold: 0.15f));
    }

    [Fact]
    public void Reset_ClearsStreak()
    {
        var detector = new SettleDetector();
        detector.Tick(new[] { 0.01f }, threshold: 0.15f);
        detector.Tick(new[] { 0.01f }, threshold: 0.15f);
        detector.Reset();
        Assert.False(detector.Tick(new[] { 0.01f }, threshold: 0.15f));
    }
}
