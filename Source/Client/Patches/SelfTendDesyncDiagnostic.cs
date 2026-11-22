using HarmonyLib;
using Multiplayer.Client.Desyncs;
using Multiplayer.Client.Util;
using RimWorld;
using Verse;
using Verse.AI;

namespace Multiplayer.Client;

internal static class SelfTendDesyncDiagnostic
{
    private static bool samplingEligibility;

    private static bool ShouldLog(Pawn pawn) =>
        MpDiagLog.Enabled && UnityData.IsInMainThread && !samplingEligibility && Multiplayer.Client != null &&
        (Multiplayer.Ticking || Multiplayer.ExecutingCmds) &&
        pawn is { Spawned: true, RaceProps.Humanlike: true, Faction.IsPlayer: true };

    private static string DescribeContext(Pawn pawn)
    {
        var map = pawn.Map;
        var currentMap = Find.CurrentMap;
        return $"timer={TickPatch.Timer} mapTicks={map?.AsyncTime()?.mapTicks ?? -1} " +
               $"pawnMap={map?.uniqueID ?? -1} curMap={currentMap?.uniqueID ?? -1} onCurrentMap={map != null && map == currentMap}";
    }

    [HarmonyPatch(typeof(HealthAIUtility), nameof(HealthAIUtility.ShouldBeTendedNowByPlayer))]
    private static class Eligibility
    {
        static void Postfix(Pawn pawn, bool __result)
        {
            try
            {
                if (!ShouldLog(pawn)) return;

                var (needsTend, tendableCount) = ReadEligibility(pawn);
                MpDiagLog.Write($"[MPSELFTEND] ShouldBeTendedNowByPlayer pawn={pawn.thingIDNumber} " +
                                $"result={__result} needsTendNow={needsTend} tendableNow={tendableCount} {DescribeContext(pawn)}");
            }
            catch { }
        }

        private static (bool needsTend, int tendableCount) ReadEligibility(Pawn pawn)
        {
            // Modded hediff checks can use Rand or call back into the same probe.
            Rand.PushState();
            DeferredStackTracing.ignoreTraces++;
            samplingEligibility = true;
            try
            {
                bool needsTend = pawn.health.HasHediffsNeedingTend();
                int tendableCount = 0;
                foreach (var hediff in pawn.health.hediffSet.hediffs)
                    if (hediff.TendableNow())
                        tendableCount++;
                return (needsTend, tendableCount);
            }
            finally
            {
                samplingEligibility = false;
                DeferredStackTracing.ignoreTraces--;
                Rand.PopState();
            }
        }
    }

    [HarmonyPatch(typeof(Medicine), nameof(Medicine.GetMedicineCountToFullyHeal))]
    private static class MedicineCount
    {
        static void Postfix(Pawn pawn, int __result)
        {
            try
            {
                if (!ShouldLog(pawn)) return;

                MpDiagLog.Write($"[MPSELFTEND] GetMedicineCountToFullyHeal pawn={pawn.thingIDNumber} " +
                                $"count={__result} {DescribeContext(pawn)}");
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(WorkGiver_Tend), nameof(WorkGiver_Tend.JobOnThing))]
    private static class TendJob
    {
        static void Postfix(Pawn pawn, Thing t, Job __result)
        {
            try
            {
                if (__result == null || !ShouldLog(pawn)) return;

                var medicine = __result.targetB.Thing;
                int stack = medicine?.stackCount ?? -1;
                int count = __result.count;
                bool willSplit = medicine != null && count >= 0 && count < stack;

                MpDiagLog.Write($"[MPSELFTEND] JobOnThing worker={pawn.thingIDNumber} patient={t?.thingIDNumber} " +
                                $"selfTend={t == pawn} medId={medicine?.thingIDNumber ?? -1} medStack={stack} " +
                                $"count={count} willSplit={willSplit} {DescribeContext(pawn)}");
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(Pawn_CarryTracker), nameof(Pawn_CarryTracker.TryStartCarry),
        new[] { typeof(Thing), typeof(int), typeof(bool) })]
    private static class MedicineCarry
    {
        static void Prefix(Pawn_CarryTracker __instance, Thing item, int count)
        {
            try
            {
                var pawn = __instance.pawn;
                if (!ShouldLog(pawn) || item?.def?.IsMedicine != true) return;

                MpDiagLog.Write($"[MPSELFTEND] TryStartCarry carrier={pawn.thingIDNumber} medId={item.thingIDNumber} " +
                                $"medStack={item.stackCount} count={count} willSplit={count < item.stackCount} {DescribeContext(pawn)}");
            }
            catch { }
        }
    }
}
