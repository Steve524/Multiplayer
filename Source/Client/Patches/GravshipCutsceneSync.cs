using System;
using Multiplayer.Client.Factions;
using Multiplayer.Client.Persistent;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using RimWorld;
using Verse;

namespace Multiplayer.Client.Patches;

internal static class GravshipCutsceneSync
{
    private static WorldComponent_GravshipController activeController;
    private static Faction flightFaction;
    private static int startTimer;
    private static bool localReady;
    private static bool applying;
    private static Action deferredJoinPoint;

    // Vanilla changes the map during local capture and animation callbacks. Keep the shared
    // timeline at the cutscene boundary until these callbacks have run on this peer.
    public static bool HoldingSimulation => activeController != null && !localReady;

    public static void Start(WorldComponent_GravshipController controller)
    {
        // Older replays never recorded a completion command.
        if (Multiplayer.IsReplay && Multiplayer.session.replayProtocol < MpVersion.GravshipCutsceneProtocol)
            return;

        activeController = controller;
        flightFaction = Faction.OfPlayer;
        startTimer = TickPatch.Timer;
        localReady = false;

        if (!Multiplayer.IsReplay)
            GravshipTravelUtils.StartFreeze();
    }

    public static bool AllowLocalCompletion(WorldComponent_GravshipController controller)
    {
        if (applying || activeController != controller) return true;
        if (localReady) return false;

        localReady = true;
        if (!Multiplayer.IsReplay)
        {
            Multiplayer.Client.SendCommand(CommandType.GravshipCutsceneEnded, ScheduledCommand.Global,
                controller.map.uniqueID, startTimer, controller.isTakeoff);

            // The server must be able to advance far enough to deliver the completion command.
            GravshipTravelUtils.StopFreeze();
        }

        return false;
    }

    public static void Finish(int mapId, int startedAt, bool takeoff)
    {
        var controller = activeController;
        if (!localReady || controller?.map?.uniqueID != mapId || startTimer != startedAt || controller.isTakeoff != takeoff)
            return;

        var joinPoint = deferredJoinPoint;
        FactionExtensions.PushFaction(null, flightFaction, force: true);
        applying = true;
        try
        {
            if (takeoff)
                controller.TakeoffEnded();
            else
                controller.LandingEnded();
        }
        finally
        {
            FactionExtensions.PopFaction();
            Reset();
        }

        joinPoint?.Invoke();
    }

    public static void RunOrDeferJoinPoint(Action createJoinPoint)
    {
        // Cutscene state is transient and cannot be resumed from a join-point save.
        if (activeController != null)
            deferredJoinPoint += createJoinPoint;
        else
            createJoinPoint();
    }

    public static void Reset()
    {
        activeController = null;
        flightFaction = null;
        startTimer = 0;
        localReady = false;
        applying = false;
        deferredJoinPoint = null;
    }
}
