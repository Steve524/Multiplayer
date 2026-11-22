using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using JetBrains.Annotations;
using Verse;

namespace Multiplayer.Client.Desyncs;

public class JittedMethod
{
    [CanBeNull] public MethodBase method;
    [CanBeNull] public MethodBase from;
    [CanBeNull] public int[] mapTicks;
    public int worldTicks;
    public int timer;
    public bool inInterface;

    internal JittedMethod Snapshot()
    {
        var snapshot = (JittedMethod)MemberwiseClone();
        snapshot.mapTicks = (int[])mapTicks?.Clone();
        return snapshot;
    }

    public string TimeString()
    {
        return $"m:{mapTicks?.Join(delimiter: ",") ?? "[]"} w:{worldTicks} t:{timer}";
    }

    public override string ToString()
    {
        return $"{method?.DeclaringType}.{method?.Name} from {from?.DeclaringType}.{from?.Name} {TimeString()} i:{inInterface}";
    }
}

public static class JittedMethods
{
    private static readonly Queue<JittedMethod> methodQueue = new();
    private static IntPtr profiler;
    [ThreadStatic] private static bool adding;

    public static void Init()
    {
        profiler = Native.mono_profiler_create(IntPtr.Zero);
        Native.mono_profiler_set_jit_done_callback(profiler, (_, method, _) =>
        {
            if (adding) return;

            try
            {
                adding = true;

                if (UnityData.IsInMainThread && Multiplayer.settings != null && Native.mono_method_get_token(method) != 0)
                {
                    var limit = Math.Max(0, Multiplayer.settings.jittedMethodsInDesync);
                    lock (methodQueue)
                    {
                        while (methodQueue.Count > limit)
                            methodQueue.Dequeue();
                        if (limit == 0) return;

                        var methodBase = Native.GetMethodBaseFromRuntimePointer(method);
                        var caller = new StackFrame(1, false).GetMethod();
                        var entry = methodQueue.Count == limit ? methodQueue.Dequeue() : new JittedMethod();
                        var game = Multiplayer.game;
                        var maps = game?.asyncTimeComps;

                        if (maps == null)
                            entry.mapTicks = null;
                        else
                        {
                            if (entry.mapTicks?.Length != maps.Count)
                                entry.mapTicks = maps.Count == 0 ? Array.Empty<int>() : new int[maps.Count];
                            for (int i = 0; i < maps.Count; i++)
                                entry.mapTicks[i] = maps[i].mapTicks;
                        }

                        entry.method = methodBase;
                        entry.from = caller;
                        entry.worldTicks = game?.asyncWorldTimeComp?.worldTicks ?? -1;
                        entry.timer = TickPatch.Timer;
                        entry.inInterface = Multiplayer.InInterface;
                        methodQueue.Enqueue(entry);
                    }
                }
            }
            finally
            {
                adding = false;
            }
        });
    }

    public static void OnApplicationQuit()
    {
        Native.mono_profiler_set_jit_done_callback(profiler, null);
    }

    public static string GetJittedMethodsString()
    {
        var jittedMethods = CaptureSnapshot().Select((j, i) => (j, i)).ToArray();

        var builder = new StringBuilder();
        builder.Append("In simulation:"); // This will get a newline from the != comparison below
        var timeString = "";

        foreach (var j in jittedMethods.Where(j => !j.j.inInterface))
        {
            if (timeString != j.j.TimeString())
                builder.AppendLine();
            builder.AppendLine($"{j.i} {j.j}");
            timeString = j.j.TimeString();
        }

        builder.AppendLine();
        builder.AppendLine();

        timeString = "";

        builder.Append("In interface:");
        foreach (var j in jittedMethods.Where(j => j.j.inInterface))
        {
            if (timeString != j.j.TimeString())
                builder.AppendLine();
            builder.AppendLine($"{j.i} {j.j}");
            timeString = j.j.TimeString();
        }

        return builder.ToString();
    }

    private static JittedMethod[] CaptureSnapshot()
    {
        // A JIT callback can run while building a report, and the queue reuses its entries.
        var wasAdding = adding;
        adding = true;
        try
        {
            lock (methodQueue)
                return methodQueue.Select(entry => entry.Snapshot()).ToArray();
        }
        finally
        {
            adding = wasAdding;
        }
    }
}
