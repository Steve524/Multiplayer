using HarmonyLib;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Client.Patches
{
    [HarmonyPatch(typeof(GenTicks), nameof(GenTicks.GetCameraUpdateRate))]
    public static class VTRSyncPatch
    {
        static bool Prefix(Thing thing, ref int __result)
        {
            if (Multiplayer.Client == null)
                return true;

            __result = VTRSync.GetSynchronizedUpdateRate(thing);
            return false;
        }
    }

    [HarmonyPatch(typeof(Projectile), nameof(Projectile.UpdateRateTicks), MethodType.Getter)]
    public static class VtrSyncProjectilePatch
    {
        static bool Prefix(ref int __result, Projectile __instance)
        {
            if (Multiplayer.Client == null)
                return true;

            // Projectile movement runs in TickInterval and needs the shorter interval on a viewed map.
            __result = __instance.Spawned && __instance.Map.AsyncTime() is { CurrentPlayerCount: > 0 }
                ? VTRSync.MinimumVtr
                : VTRSync.MaximumVtr;
            return false;
        }
    }

    [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.UpdateRateTicks), MethodType.Getter)]
    public static class VtrSyncWorldObjectPatch
    {
        static bool Prefix(ref int __result, WorldObject __instance)
        {
            if (Multiplayer.Client == null)
                return true;

            __result = Multiplayer.AsyncWorldTime.VTR;
            return false;
        }
    }

    static class VTRSync
    {
        public const int WorldMapId = -2;
        public const int InvalidMapId = -1;
        private static int lastReportedMapId = InvalidMapId;

        public const int MaximumVtr = 15;
        public const int MinimumVtr = 1;
        public const int ViewedMapVtr = 3;

        public static int ViewedThingVtr => Multiplayer.IsReplay && Multiplayer.session.replayProtocol < MpVersion.ViewedMapTickRateProtocol
            ? MinimumVtr
            : ViewedMapVtr;

        public static int GetSynchronizedUpdateRate(Thing thing) => thing?.MapHeld?.AsyncTime()?.VTR ?? MaximumVtr;

        public static void UpdateViewedMap()
        {
            if (!Multiplayer.InInterface || Multiplayer.IsReplay || Multiplayer.arbiterInstance || TickPatch.Simulating ||
                Multiplayer.session.desynced || Multiplayer.session.dataSnapshot == null ||
                Multiplayer.Client.State != ConnectionStateEnum.ClientPlaying)
                return;

            // Report the actual view once per frame, after temporary simulation map switches have unwound.
            int current = WorldRendererUtility.CurrentWorldRenderMode == WorldRenderMode.Planet
                ? WorldMapId
                : Find.CurrentMap?.uniqueID ?? InvalidMapId;
            if (current == lastReportedMapId) return;

            Multiplayer.Client.SendCommand(CommandType.PlayerCount, ScheduledCommand.Global, ByteWriter.GetBytes(lastReportedMapId, current));
            lastReportedMapId = current;
        }

        public static void ClearPlayerCounts()
        {
            Multiplayer.AsyncWorldTime.CurrentPlayerCount = 0;
            foreach (var map in Find.Maps)
                map.AsyncTime().CurrentPlayerCount = 0;
        }

        public static void Reset() => lastReportedMapId = InvalidMapId;
    }
}
