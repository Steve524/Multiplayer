using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;
using Verse;

namespace Multiplayer.Client
{

    public class ClientSyncOpinion(int startTick)
    {
        public bool isLocalClientsOpinion;

        public int startTick = startTick;
        public List<uint> commandRandomStates = new();
        public List<uint> worldRandomStates = new();
        public List<MapRandomStateData> mapStates = new();

        // todo Unused for now
        public List<int> pawnCapacityHashes = new();
        public List<int> pawnStatHashes = new();
        public List<int> pawnNeedHashes = new();

        // Keep the full traces and per-call hashes for local diagnostics.
        public List<StackTraceLogItem> desyncStackTraces = new();
        private List<int> desyncStackTraceHashes = new();
        private List<int> traceStepHashes = new();
        private int lastTraceStepTimer;
        private const int TraceStepHashSeed = 0x6d707472;
        private const int MaxTracesInReport = 4000;
        public bool simulating;
        public RoundModeEnum roundMode;

        public string CheckForDesync(ClientSyncOpinion other)
        {
            if (roundMode != other.roundMode)
                return $"FP round mode doesn't match: {roundMode} != {other.roundMode}";

            if (mapStates.Count != other.mapStates.Count)
                return "Map instances don't match";
            for (int i = 0; i < mapStates.Count; i++)
                if (mapStates[i].mapId != other.mapStates[i].mapId)
                    return "Map instances don't match";

            // Each opinion has one entry per map and both lists have the same order.
            for (int i = 0; i < mapStates.Count; i++)
                if (!ListsEqual(mapStates[i].randomStates, other.mapStates[i].randomStates))
                    return $"Wrong random state on map {mapStates[i].mapId}";

            if (!ListsEqual(worldRandomStates, other.worldRandomStates))
                return "Wrong random state for the world";

            if (!ListsEqual(commandRandomStates, other.commandRandomStates))
                return "Random state from commands doesn't match";

            if (!simulating && !other.simulating && TraceStepCount > 0 && other.TraceStepCount > 0 &&
                !ListsEqual(traceStepHashes, other.traceStepHashes))
                return "Trace hashes don't match";

            return null;
        }

        private static bool ListsEqual<T>(List<T> lhs, List<T> rhs)
        {
            if (lhs.Count != rhs.Count) return false;

            var comparer = EqualityComparer<T>.Default;
            for (int i = 0; i < lhs.Count; i++)
                if (!comparer.Equals(lhs[i], rhs[i])) return false;

            return true;
        }

        public void AddTraceHash(int timer, int hash)
        {
            desyncStackTraceHashes.Add(hash);
            if (timer != lastTraceStepTimer || traceStepHashes.Count == 0)
            {
                lastTraceStepTimer = timer;
                traceStepHashes.Add(0);
                traceStepHashes.Add(Gen.HashCombineInt(TraceStepHashSeed, timer));
            }

            int last = traceStepHashes.Count - 1;
            traceStepHashes[last - 1]++;
            traceStepHashes[last] = Gen.HashCombineInt(traceStepHashes[last], hash);
        }

        public int TraceStepCount => traceStepHashes.Count / 2;
        public int TraceStepSize(int step) => traceStepHashes[step * 2];
        public int TraceStepHash(int step) => traceStepHashes[step * 2 + 1];

        public int TraceCount
        {
            get
            {
                if (desyncStackTraceHashes.Count > 0)
                    return desyncStackTraceHashes.Count;

                int total = 0;
                for (int i = 0; i < TraceStepCount; i++)
                    total += TraceStepSize(i);
                return total;
            }
        }

        public List<uint> GetRandomStatesForMap(int mapId)
        {
            for (int i = 0; i < mapStates.Count; i++)
                if (mapStates[i].mapId == mapId) return mapStates[i].randomStates;

            var result = new MapRandomStateData(mapId);
            mapStates.Add(result);
            return result.randomStates;
        }

        public byte[] Serialize()
        {
            var writer = new ByteWriter();

            writer.WriteInt32(startTick);
            writer.WritePrefixedUInts(commandRandomStates);
            writer.WritePrefixedUInts(worldRandomStates);

            writer.WriteInt32(mapStates.Count);
            foreach (var map in mapStates)
            {
                writer.WriteInt32(map.mapId);
                writer.WritePrefixedUInts(map.randomStates);
            }

            writer.WritePrefixedInts(desyncStackTraceHashes);
            writer.WriteBool(simulating);
            writer.WriteShort((short)roundMode);

            // Older reports end after the rounding mode and store per-call hashes in the original field.
            if (TraceStepCount > 0)
                writer.WritePrefixedInts(traceStepHashes);

            return writer.ToArray();
        }

        public static ClientSyncOpinion FromNet(SyncOpinion sync) => new(sync.startTick)
        {
            commandRandomStates = sync.commandRandomStates,
            worldRandomStates = sync.worldRandomStates,
            mapStates = sync.mapRandomStates.Select(state => new MapRandomStateData(state.mapId)
                { randomStates = state.randomStates }).ToList(),
            traceStepHashes = sync.traceStepHashes,
            simulating = sync.simulating,
            roundMode = sync.roundMode
        };

        public SyncOpinion ToNet() => new()
        {
            startTick = startTick,
            commandRandomStates = commandRandomStates,
            worldRandomStates = worldRandomStates,
            mapRandomStates = mapStates.Select(state => new MapRandomState
                { mapId = state.mapId, randomStates = state.randomStates }).ToList(),
            traceStepHashes = traceStepHashes,
            simulating = simulating,
            roundMode = roundMode
        };

        public static ClientSyncOpinion Deserialize(ByteReader data)
        {
            var startTick = data.ReadInt32();

            var cmds = new List<uint>(data.ReadPrefixedUInts());
            var world = new List<uint>(data.ReadPrefixedUInts());

            var maps = new List<MapRandomStateData>();
            int mapCount = data.ReadInt32();
            for (int i = 0; i < mapCount; i++)
            {
                int mapId = data.ReadInt32();
                var mapData = new List<uint>(data.ReadPrefixedUInts());
                maps.Add(new MapRandomStateData(mapId) { randomStates = mapData });
            }

            var traceHashes = new List<int>(data.ReadPrefixedInts());
            var simulating = data.ReadBool();
            var roundMode = data.Left == 0 ? RoundModeEnum.ToNearest : (RoundModeEnum)data.ReadShort();
            var stepHashes = data.Left == 0 ? new List<int>() : new List<int>(data.ReadPrefixedInts(SyncOpinion.MaxTraceStepHashEntries));

            return new ClientSyncOpinion(startTick)
            {
                commandRandomStates = cmds,
                worldRandomStates = world,
                mapStates = maps,
                desyncStackTraceHashes = traceHashes,
                traceStepHashes = stepHashes,
                simulating = simulating,
                roundMode = roundMode
            };
        }

        private int StepSizeStartingAt(int traceIndex)
        {
            int index = 0;
            for (int i = 0; i < TraceStepCount && index <= traceIndex; i++)
            {
                int size = TraceStepSize(i);
                if (index == traceIndex)
                    return size;
                index += size;
            }
            return 0;
        }

        public void TryMarkSimulating()
        {
            if (TickPatch.Simulating)
                simulating = true;
        }

        public string GetFormattedStackTracesForRange(int diffAt)
        {
            int radius = Multiplayer.settings.desyncTracesRadius;
            int anchor = Math.Min(Math.Max(0, diffAt), desyncStackTraces.Count);
            int stepSize = StepSizeStartingAt(diffAt);
            int start = Math.Max(0, anchor - radius);
            int end = Math.Min(anchor + stepSize + radius, start + MaxTracesInReport);
            var details = new StringBuilder().AppendLine($"Trace count: {desyncStackTraces.Count}");

            if (stepSize > 0)
                details.AppendLine($"First differing timer step: {stepSize} traces starting at {diffAt}.");
            else if (diffAt >= desyncStackTraces.Count)
                details.AppendLine($"No trace at index {diffAt}.");

            if (end < anchor + stepSize)
                details.AppendLine($"Trace output capped at {MaxTracesInReport} entries.");

            var traceId = start;
            return details.AppendLine("\nContext traces:")
                .Append(desyncStackTraces.Skip(start).Take(end - start).Join(a => traceId++ + " " + a, "\n\n"))
                .ToString();
        }

        public void Clear()
        {
            for (int i = 0; i < desyncStackTraces.Count; i++)
                desyncStackTraces[i].Dispose();
        }
    }
}
