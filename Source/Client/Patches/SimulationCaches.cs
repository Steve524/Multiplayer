using Multiplayer.Client.Patches;
using RimWorld;

namespace Multiplayer.Client;

internal static class SimulationCaches
{
    public static void ClearAll()
    {
        InvalidateFrameKeyed();
        StatWorkerGetValuePatch.ClearAll();
    }

    public static void InvalidateFrameKeyed()
    {
        // A catching-up peer can run many simulation steps in one rendered frame.
        PlayerItemAccessibilityUtility.cachedAccessibleThingsForFrame = -1;
    }
}
