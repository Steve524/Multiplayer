using System.Collections.Generic;

namespace Multiplayer.Common.Networking.Packet;

[PacketDefinition(Packets.Client_SyncInfo, allowFragmented: true)]
public record struct ClientSyncInfoPacket : IPacket
{
    // 8MiB. For some big saves, the sync opinion can reach ~2.5MiB, so this includes some leeway.
    public const int MaxLength = 1 << 23;
    public byte[] rawSyncOpinion;

    public SyncOpinion SyncOpinion
    {
        get => SyncOpinionBinder.Deserialize(rawSyncOpinion);
        set => rawSyncOpinion = SyncOpinionBinder.Serialize(value);
    }

    public void Bind(PacketBuffer buf)
    {
        buf.BindRemaining(ref rawSyncOpinion, maxLength: MaxLength);
    }

    private static readonly Binder<SyncOpinion> SyncOpinionBinder = BinderOf.Identity<SyncOpinion>();
}

[PacketDefinition(Packets.Server_SyncInfo, allowFragmented: true)]
public record struct ServerSyncInfoPacket : IPacket
{
    public byte[] rawSyncOpinion;
    public SyncOpinion SyncOpinion
    {
        get => SyncOpinionBinder.Deserialize(rawSyncOpinion);
        set => rawSyncOpinion = SyncOpinionBinder.Serialize(value);
    }

    public void Bind(PacketBuffer buf)
    {
        buf.BindRemaining(ref rawSyncOpinion, maxLength: ClientSyncInfoPacket.MaxLength);
    }

    private static readonly Binder<SyncOpinion> SyncOpinionBinder = BinderOf.Identity<SyncOpinion>();
}

public record struct SyncOpinion : IPacketBufferable
{
    public const int MaxTraceStepHashEntries = 1 << 16;

    public int startTick;
    public List<uint> commandRandomStates;
    public List<uint> worldRandomStates;
    public List<MapRandomState> mapRandomStates;
    // One pair per timer step with traces: [traceCount, rollingHash, ...].
    public List<int> traceStepHashes;
    public bool simulating;
    public RoundModeEnum roundMode;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref startTick);

        buf.Bind(ref commandRandomStates, BinderOf.UInt());
        buf.Bind(ref worldRandomStates, BinderOf.UInt());
        buf.Bind(ref mapRandomStates, BinderOf.Identity<MapRandomState>());
        buf.Bind(ref traceStepHashes, BinderOf.Int(), maxLength: MaxTraceStepHashEntries);

        if (!buf.isWriting)
        {
            if (traceStepHashes.Count % 2 != 0)
                throw new ReaderException("Incomplete trace step summary");

            long traceCount = 0;
            for (int i = 0; i < traceStepHashes.Count; i += 2)
            {
                int stepSize = traceStepHashes[i];
                traceCount += stepSize;
                if (stepSize <= 0 || traceCount > int.MaxValue)
                    throw new ReaderException("Invalid trace count in step summary");
            }
        }

        buf.Bind(ref simulating);
        buf.BindEnum(ref roundMode);
    }
}

public record struct MapRandomState : IPacketBufferable
{
    public int mapId;
    public List<uint> randomStates;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref mapId);
        buf.Bind(ref randomStates, BinderOf.UInt());
    }
}
