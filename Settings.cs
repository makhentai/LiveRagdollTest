using BepInEx.Configuration;

namespace LiveRagdollTest;

internal static class Settings
{
    public static ConfigEntry<bool> Enabled;
    public static ConfigEntry<float> DefaultMaxDurationSeconds;
    public static ConfigEntry<float> DefaultVelocityThreshold;
    public static ConfigEntry<float> MaxDriftMeters;
    public static ConfigEntry<float> MaxAngularVelocity;
    public static ConfigEntry<float> MaxLinearVelocity;
    public static ConfigEntry<float> VelocityDampingPerTick;
    public static ConfigEntry<bool> DebugLogging;
    public static ConfigEntry<bool> BareDiagnosticMode;
    public static ConfigEntry<bool> CaptureLiveMotion;

    public static void Init(ConfigFile config)
    {
        const string general = "1. General";

        Enabled = config.Bind(general, "Enabled", true,
            "Master switch. Off disables OnDowned() entirely — OnRecovered() still works so a caller is never left with a stuck ragdoll if this gets turned off mid-raid.");
        DefaultMaxDurationSeconds = config.Bind(general, "Default Max Duration (sec)", 3f,
            new ConfigDescription(
                "Hard cap on how long the physics phase can run before being forced back to animated control even if it never settles. A caller can override this per-call via RagdollOptions.",
                new AcceptableValueRange<float>(0.5f, 10f)));
        DefaultVelocityThreshold = config.Bind(general, "Default Velocity Threshold", 0.15f,
            new ConfigDescription(
                "The ragdoll counts as settled once every bone's rigidbody velocity stays below this (m/s) for a few consecutive ticks. A caller can override this per-call via RagdollOptions.",
                new AcceptableValueRange<float>(0.01f, 2f)));
        MaxDriftMeters = config.Bind(general, "Max Drift Before Recovery (m)", 2f,
            new ConfigDescription(
                "If any ragdoll bone ends up farther than this from where the ragdoll started, every bone is snapped back and stopped — a safety net for a body that clipped through a wall/floor, or a joint whose anchor is stretching apart (visually: a limb pulled far off the body, sometimes toward a wall), and would otherwise never resettle. The old default (6) let a stretching joint travel a very visible distance before this caught it — with Max Linear Velocity now capped at 3 m/s, a bone can't legitimately cover more than ~1.5m in this check's own 0.5s grace window anyway, so 2 is already generous, not tight.",
                new AcceptableValueRange<float>(0.5f, 10f)));
        MaxAngularVelocity = config.Bind(general, "Max Angular Velocity (rad/s)", 10f,
            new ConfigDescription(
                "Every bone's spin is clamped to this every physics tick — a crash-prevention backstop, not a 'looks calm' target. Was 4 (~230 deg/s) for a while specifically to force a 'looks calm' result, but combined with aggressive VelocityDampingPerTick that flattened every ragdoll's collapse into the same generic gentle slump regardless of how the character actually died (field-confirmed 2026-09-16 — see project memory project-liveragdolltest-ik-fix). Raised back toward the original crash-backstop intent; let VelocityDampingPerTick do the settling-speed shaping instead of a low hard ceiling.",
                new AcceptableValueRange<float>(1f, 20f)));
        MaxLinearVelocity = config.Bind(general, "Max Linear Velocity (m/s)", 6f,
            new ConfigDescription(
                "Every bone's center-of-mass speed is clamped to this every physics tick — same reasoning and history as Max Angular Velocity above, but for straight-line motion.",
                new AcceptableValueRange<float>(1f, 15f)));
        VelocityDampingPerTick = config.Bind(general, "Velocity Damping Per Tick", 0.975f,
            new ConfigDescription(
                "Every bone's velocity is multiplied by this every physics tick (while awake) — a lower value settles faster but erases how hard a bone was actually hit within under a second regardless of magnitude (0.85 at 60 ticks/s decays to ~0.003% of the original velocity in one second flat), which field-tested as every ragdoll settling the same generic 'gentle slump' way no matter how it actually died (2026-09-16, see project memory project-liveragdolltest-ik-fix). Raise toward 1 to let a hard hit visibly stay energetic longer; the hard velocity ceilings above still cap how fast is 'too fast' regardless of this value. Nudged 0.96->0.975 on 2026-09-16 for more visible/varied hit reactions and less uniform collapse (0.975^60 ~= 22% of initial velocity survives after 1s vs 0.96's ~9%) — deliberately NOT touching MaxAngularVelocity/MaxLinearVelocity, those are the values with the actual crash/spin history (see project memory project-liveragdolltest-ik-fix); this knob only changes how long already-clamped motion visibly persists, not how fast it can ever get.",
                new AcceptableValueRange<float>(0.5f, 0.999f)));
        DebugLogging = config.Bind(general, "Debug Logging", false,
            "Logs one arm bone's angular velocity/rotation and key component states to the BepInEx log every ~0.25s while a session is active. Leave off otherwise.");
        BareDiagnosticMode = config.Bind(general, "Bare Diagnostic Mode", false,
            "Diagnostic only: disables every active per-tick correction (velocity damping/clamp, drift snap-back) to isolate whether those corrections themselves contribute to instability. Leave off outside of testing that specific question.");
        CaptureLiveMotion = config.Bind(general, "Capture Live Motion", true,
            "Continuously samples every live player's per-bone rotation (every Player.BodyUpdate tick) so a ragdoll can start from whatever motion the character was already making (recoil, mid-turn) instead of dead-still. Runs for every live player every frame regardless of whether they ever go Downed — turn off if this measurably costs performance on a crowded raid.");
    }
}
