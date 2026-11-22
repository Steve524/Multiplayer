using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Client.AsyncTime;

[HarmonyPatch(typeof(PostTickVisuals), nameof(PostTickVisuals.ProcessPostTickVisuals))]
internal static class PostTickVisualsPerFrame
{
    private const int MaxTicksPerFrame = 250;

    internal static void TickSimulationVisuals(AsyncTimeComp time)
    {
        // The simulation reads pawn rotation and draw position. Keep the normal pawn and drawer hooks on every tick.
        var tickManager = Find.TickManager;
        int previousTicks = tickManager.ticksThisFrame;
        tickManager.ticksThisFrame = 1;
        try
        {
            time.map.postTickVisuals.ProcessPostTickVisuals();
        }
        finally
        {
            tickManager.ticksThisFrame = previousTicks;
        }

        if (TickPatch.Simulating)
            time.pendingVisualTicks = 0;
        else if (time.pendingVisualTicks < MaxTicksPerFrame)
            time.pendingVisualTicks++;
    }

    static bool Prefix(PostTickVisuals __instance)
    {
        if (Multiplayer.Client == null || !MapUpdateMarker.updating || Multiplayer.Ticking || Multiplayer.ExecutingCmds)
            return true;

        var map = __instance.map;
        var time = map.AsyncTime();
        if (time == null) return false;

        int ticks = time.pendingVisualTicks;
        time.pendingVisualTicks = 0;
        if (ticks == 0 || TickPatch.Simulating || !WorldRendererUtility.DrawingMap || Find.CurrentMap != map)
            return false;

        var view = Find.CameraDriver.CurrentViewRect.ExpandedBy(3);
        var pawns = map.mapPawns.AllPawnsSpawned;
        Rand.PushState();
        try
        {
            for (int i = 0; i < pawns.Count; i++)
            {
                var pawn = pawns[i];
                if (pawn.Suspended || !pawn.Spawned || !view.Contains(pawn.Position)) continue;

                var drawer = pawn.Drawer;
                drawer.footprintMaker.ProcessPostTickVisuals(ticks);
                drawer.breathMoteMaker.ProcessPostTickVisuals(ticks);
                drawer.waterRippleMaker.ProcessPostTickVisuals(ticks);
            }
        }
        finally
        {
            Rand.PopState();
        }

        return false;
    }
}

[HarmonyPatch]
internal static class DeferPawnCosmeticVisuals
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.DeclaredMethod(typeof(PawnFootprintMaker), nameof(PawnFootprintMaker.ProcessPostTickVisuals));
        yield return AccessTools.DeclaredMethod(typeof(PawnBreathMoteMaker), nameof(PawnBreathMoteMaker.ProcessPostTickVisuals));
        yield return AccessTools.DeclaredMethod(typeof(PawnWaterRippleMaker), nameof(PawnWaterRippleMaker.ProcessPostTickVisuals));
    }

    static bool Prefix() => AsyncTimeComp.tickingMap == null || Multiplayer.Client == null;
}
