using BepInEx;
using BepInEx.Logging;

namespace LiveRagdollTest;

[BepInPlugin("com.liveragdolltest.core", "LiveRagdollTest", "0.4.0")]
public class Plugin : BaseUnityPlugin
{
    public static ManualLogSource Log;

    private void Awake()
    {
        Log = Logger;
        Settings.Init(Config);
        new Internal.FatalImpulsePatch().Enable();
        new Internal.LiveMotionCapturePatch().Enable();
        new Internal.ArmsUpdateSuppressPatch().Enable();
        new Internal.VisualPassSuppressPatch().Enable();
        Log.LogInfo("LiveRagdollTest plugin loaded!");
    }
}
