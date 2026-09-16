namespace LiveRagdollTest.Api;

/// <summary>Per-call tuning for <see cref="LiveRagdollBridge.OnDowned"/>. Pass null to use the
/// mod's own F12 settings.</summary>
public readonly struct RagdollOptions
{
    public float MaxDurationSeconds { get; }
    public float VelocityThreshold { get; }

    /// <summary>True (default): once the body settles, physics stops and the pose is held until an
    /// explicit <see cref="LiveRagdollBridge.OnRecovered"/> call (or MaxDurationSeconds as a hard
    /// safety cap) — matches a "downed" state whose duration is controlled by an external caller,
    /// not a fixed timer. False: control is handed back automatically the instant the body settles.</summary>
    public bool KeepRagdolledUntilRestored { get; }

    public RagdollOptions(float maxDurationSeconds, float velocityThreshold, bool keepRagdolledUntilRestored = true)
    {
        MaxDurationSeconds = maxDurationSeconds;
        VelocityThreshold = velocityThreshold;
        KeepRagdolledUntilRestored = keepRagdolledUntilRestored;
    }

    public static RagdollOptions Default => new(
        Settings.DefaultMaxDurationSeconds.Value,
        Settings.DefaultVelocityThreshold.Value,
        keepRagdolledUntilRestored: true);
}
