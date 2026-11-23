using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Gilzoide.ManagedJobs;
using HarmonyLib;
using Unity.Collections;
using Unity.Jobs;
using Verse;

namespace Multiplayer.Client.Patches;

internal static class ConcurrentPathfinding
{
    [ThreadStatic] private static PathFinder schedulingFinder;
    private static readonly Dictionary<PathFinder, List<IDisposable>> snapshots = new();
    private static bool? safetyHooksInstalled;

    internal static bool Available => PathFinderRunDoorsJobOnMainThread.Patched && (safetyHooksInstalled ??=
        HasHook(typeof(PathFinderRunDoorsJobOnMainThread), AccessTools.DeclaredMethod(typeof(PathFinder), nameof(PathFinder.ScheduleBatchedPathJobs)), HarmonyPatchType.Transpiler) &&
        HasHook(typeof(PathFinderSnapshotGridInputs), AccessTools.DeclaredMethod(typeof(PathFinderMapData), nameof(PathFinderMapData.ParameterizeGridJob))) &&
        HasHook(typeof(PathFinderReleaseSnapshots), AccessTools.DeclaredMethod(typeof(PathFinder), nameof(PathFinder.ForceCompleteScheduledJobs))) &&
        HasHook(typeof(PathFinderReleaseSnapshots), AccessTools.DeclaredMethod(typeof(PathFinder), nameof(PathFinder.Dispose))) &&
        HasHook(typeof(PathFinderCompleteBeforeFactionRemoved), AccessTools.DeclaredMethod(typeof(PathFinderMapData), nameof(PathFinderMapData.Notify_FactionRemoved)), HarmonyPatchType.Prefix));

    internal static bool Active =>
        Multiplayer.Client != null && Multiplayer.game?.gameComp is { concurrentPathfinding: true } &&
        UnityData.IsInMainThread && Available;

    internal static void MapPreTick(Map map, PathFinder finder)
    {
        var previous = schedulingFinder;
        schedulingFinder = finder;
        try
        {
            map.MapPreTick();
        }
        finally
        {
            schedulingFinder = previous;
        }
    }

    internal static void Complete(PathFinder finder)
    {
        if (finder is { disposed: false })
            finder.ForceCompleteScheduledJobs();
    }

    internal static void SnapshotGridInputs(PathFinderMapData data, ref PathGridJob job)
    {
        var finder = schedulingFinder;
        if (finder == null || !ReferenceEquals(finder.MapData, data)) return;

        job.avoidGrid = Copy(job.avoidGrid, finder);
        job.custom = Copy(job.custom, finder);
        job.lordGrid = Copy(job.lordGrid, finder);
    }

    internal static JobHandle RunDoorsJob(ref ManagedJob job, JobHandle dependsOn, PathFinder finder)
    {
        if (!ReferenceEquals(schedulingFinder, finder))
            return job.Schedule(dependsOn);

        // Only this request's buffers are written here. The path job still waits for the grid and its batch.
        Rand.PushState();
        try
        {
            job.Run();
        }
        finally
        {
            Rand.PopState();
        }
        return dependsOn;
    }

    internal static void ReleaseSnapshots(PathFinder finder)
    {
        if (!snapshots.TryGetValue(finder, out var owned)) return;

        foreach (var snapshot in owned)
            snapshot.Dispose();
        snapshots.Remove(finder);
    }

    private static NativeArray<T>.ReadOnly Copy<T>(NativeArray<T>.ReadOnly source, PathFinder finder) where T : struct
    {
        if (source.Length == 0) return source;

        var copy = new NativeArray<T>(source.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        try
        {
            NativeArray<T>.Copy(source, copy);
            var readOnly = copy.AsReadOnly();
            Track(finder, copy);
            return readOnly;
        }
        catch
        {
            copy.Dispose();
            throw;
        }
    }

    private static NativeBitArray.ReadOnly Copy(NativeBitArray.ReadOnly source, PathFinder finder)
    {
        if (source.Length == 0) return source;

        var copy = new NativeBitArray(source.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        try
        {
#if RW17
            for (int bit = 0; bit < source.Length; bit++)
                copy.Set(bit, source.IsSet(bit));
#else
            for (int offset = 0; offset < source.Length; offset += 64)
            {
                int bits = Math.Min(64, source.Length - offset);
                copy.SetBits(offset, source.GetBits(offset, bits), bits);
            }
#endif

            var readOnly = copy.AsReadOnly();
            Track(finder, copy);
            return readOnly;
        }
        catch
        {
            copy.Dispose();
            throw;
        }
    }

    private static void Track(PathFinder finder, IDisposable snapshot)
    {
        if (!snapshots.TryGetValue(finder, out var owned))
            snapshots[finder] = owned = new List<IDisposable>();
        owned.Add(snapshot);
    }

    private static bool HasHook(Type patchType, MethodBase target, HarmonyPatchType kind = HarmonyPatchType.Postfix)
    {
        if (target == null) return false;
        var info = Harmony.GetPatchInfo(target);
        var hooks = kind switch
        {
            HarmonyPatchType.Transpiler => info?.Transpilers,
            HarmonyPatchType.Prefix => info?.Prefixes,
            _ => info?.Postfixes,
        };
        return hooks?.Any(p => p.owner == Multiplayer.harmony.Id && p.PatchMethod.DeclaringType == patchType) == true;
    }
}

[HarmonyPatch(typeof(PathFinder), nameof(PathFinder.ScheduleBatchedPathJobs))]
internal static class PathFinderRunDoorsJobOnMainThread
{
    internal static bool Patched { get; private set; }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var schedule = AccessTools.DeclaredMethod(typeof(ManagedJob), nameof(ManagedJob.Schedule), [typeof(JobHandle)]);
        var calls = code.FindAll(i => i.Calls(schedule));
        Patched = calls.Count == 1;
        if (!Patched)
        {
            Log.Warning($"MP: concurrent pathfinding disabled. Expected one managed path job schedule call, found {calls.Count}.");
            return code;
        }

        var call = calls[0];
        int index = code.IndexOf(call);
        // The existing stack is (ref ManagedJob, JobHandle). Add the owning finder to scope the replacement.
        var loadFinder = new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(call).MoveBlocksFrom(call);
        code.Insert(index, loadFinder);
        call.opcode = OpCodes.Call;
        call.operand = AccessTools.DeclaredMethod(typeof(ConcurrentPathfinding), nameof(ConcurrentPathfinding.RunDoorsJob));
        return code;
    }
}

[HarmonyPatch(typeof(PathFinderMapData), nameof(PathFinderMapData.ParameterizeGridJob))]
internal static class PathFinderSnapshotGridInputs
{
    static void Postfix(PathFinderMapData __instance, ref PathGridJob job) =>
        ConcurrentPathfinding.SnapshotGridInputs(__instance, ref job);
}

[HarmonyPatch]
internal static class PathFinderReleaseSnapshots
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.DeclaredMethod(typeof(PathFinder), nameof(PathFinder.ForceCompleteScheduledJobs));
        yield return AccessTools.DeclaredMethod(typeof(PathFinder), nameof(PathFinder.Dispose));
    }

    static void Postfix(PathFinder __instance, bool __runOriginal)
    {
        // Both vanilla methods complete every reader before returning.
        if (__runOriginal)
            ConcurrentPathfinding.ReleaseSnapshots(__instance);
    }
}

[HarmonyPatch(typeof(PathFinderMapData), nameof(PathFinderMapData.Notify_FactionRemoved))]
internal static class PathFinderCompleteBeforeFactionRemoved
{
    static void Prefix(PathFinderMapData __instance)
    {
        // The faction's path cost grid is disposed immediately by this notification.
        if (ConcurrentPathfinding.Active)
            ConcurrentPathfinding.Complete(__instance.map?.pathFinder);
    }
}
