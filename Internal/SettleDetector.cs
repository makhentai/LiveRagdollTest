namespace LiveRagdollTest.Internal;

/// <summary>Debounced "has everything stopped moving" check — requires
/// <see cref="ConsecutiveTicksRequired"/> consecutive calls where every speed is under the
/// threshold, so one lucky quiet frame mid-tumble doesn't end the ragdoll early.</summary>
internal sealed class SettleDetector
{
    private const int ConsecutiveTicksRequired = 3;

    private int _consecutiveQuietTicks;

    public bool Tick(float[] speeds, float threshold)
    {
        bool allBelowThreshold = true;
        foreach (float speed in speeds)
        {
            if (speed >= threshold)
            {
                allBelowThreshold = false;
                break;
            }
        }

        _consecutiveQuietTicks = allBelowThreshold ? _consecutiveQuietTicks + 1 : 0;
        return _consecutiveQuietTicks >= ConsecutiveTicksRequired;
    }

    public void Reset()
    {
        _consecutiveQuietTicks = 0;
    }
}
