using FluentAssertions;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace Tests;

public class PacketWriteTest
{
    [Test]
    public void ScalarWritesMatchLegacyBytes()
    {
        const short signedShort = -257;
        const ushort unsignedShort = 0xFEDC;
        const int signedInt = -0x123456;
        const uint unsignedInt = 0xFEDCBA98;
        const long signedLong = -0x123456789ABC;
        const ulong unsignedLong = 0xFEDCBA9876543210;
        var single = BitConverter.Int32BitsToSingle(unchecked((int)0xFFC12345));
        var @double = BitConverter.Int64BitsToDouble(long.MinValue);

        var writer = new ByteWriter();
        writer.WriteULong(unsignedLong);
        writer.WriteShort(signedShort);
        writer.WriteLong(signedLong);
        writer.WriteInt32(signedInt);
        writer.WriteDouble(@double);
        writer.WriteUShort(unsignedShort);
        writer.WriteUInt32(unsignedInt);
        writer.WriteFloat(single);

        byte[] expected =
        [
            .. BitConverter.GetBytes(unsignedLong), .. BitConverter.GetBytes(signedShort),
            .. BitConverter.GetBytes(signedLong), .. BitConverter.GetBytes(signedInt),
            .. BitConverter.GetBytes(@double), .. BitConverter.GetBytes(unsignedShort),
            .. BitConverter.GetBytes(unsignedInt), .. BitConverter.GetBytes(single)
        ];
        writer.ToArray().Should().Equal(expected);
    }

    [Test]
    public void ScalarWritesReuseScratchSpace()
    {
        const int count = 1000;
        var writer = new ByteWriter((count + 1) * sizeof(int));
        writer.WriteInt32(0);
        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < count; i++)
            writer.WriteInt32(i);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        allocated.Should().BeLessThan(1024);
        writer.Position.Should().Be((count + 1) * sizeof(int));
    }

    [Test]
    public void FragmentedSendPreservesHeadersAndPayload()
    {
        var connection = new RecordingConnection();
        var payload = Enumerable.Range(0, 2040).Select(i => (byte)i).ToArray();
        const Packets packet = Packets.Client_Command;
        byte header = (byte)((byte)packet & 0x3F);

        connection.SendFragmented(packet, payload);

        byte[][] expected =
        [
            [(byte)(header | 0x40), 0, .. BitConverter.GetBytes((ushort)3),
                .. BitConverter.GetBytes((uint)payload.Length), .. payload[..1016]],
            [(byte)(header | 0x40), 0, .. payload[1016..2038]],
            [(byte)(header | 0x80), 0, .. payload[2038..]]
        ];
        connection.Frames.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
    }

    [Test]
    public void DisconnectedSendSkipsPacketBinding()
    {
        var connection = new RecordingConnection();
        connection.ChangeState(ConnectionStateEnum.Disconnected);
        connection.Send(new BindingProbe());
        connection.Frames.Should().BeEmpty();
    }

    private sealed class RecordingConnection() : DummyConnection("test")
    {
        public List<byte[]> Frames { get; } = [];

        protected override void SendRaw(byte[] raw, bool reliable) => Frames.Add(raw);
    }

    private struct BindingProbe : IPacket
    {
        public void Bind(PacketBuffer buf) => throw new InvalidOperationException("Packet binding ran after disconnect.");
    }
}
