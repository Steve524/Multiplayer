using Multiplayer.Client.Util;
using Verse;

namespace Multiplayer.Client;

internal static class MapLoadDiagnostic
{
    private const int TicksToLog = 20;

    public static void OnMapSetup(AsyncTimeComp time)
    {
        if (!MpDiagLog.Enabled || Multiplayer.Client == null) return;

        time.mapLoadDiagnosticTicksRemaining = TicksToLog;
        MpDiagLog.Write($"[MPMAPLOAD] setup map={time.map.uniqueID} startMapTicks={time.mapTicks} " +
                        $"gameStartAbsTick={time.GameStartAbsTick} randState={time.randState} " +
                        $"desiredSpeed={time.DesiredTimeSpeed} curMap={CurrentMapId()} timer={TickPatch.Timer}");
    }

    public static void OnTick(AsyncTimeComp time)
    {
        if (time.mapLoadDiagnosticTicksRemaining <= 0 || !MpDiagLog.Enabled) return;

        time.mapLoadDiagnosticTicksRemaining--;
        MpDiagLog.Write($"[MPMAPLOAD] tick map={time.map.uniqueID} mapTicks={time.mapTicks} " +
                        $"randState={time.randState} forceNormalUntil={time.slower.forceNormalSpeedUntil} " +
                        $"curMap={CurrentMapId()} timer={TickPatch.Timer}");
    }

    private static int CurrentMapId()
    {
        try { return Find.CurrentMap?.uniqueID ?? -1; }
        catch { return -2; }
    }
}
