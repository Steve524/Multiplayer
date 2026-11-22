using Multiplayer.Common;

namespace Tests;

public class AutoSpeedReductionTest
{
    private MultiplayerServer server = null!;
    private ServerPlayer player = null!;

    [SetUp]
    public void SetUp()
    {
        server = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings());
        server.gameTimer = 123;
        var connection = new DummyConnection("player");
        player = server.playerManager.OnConnected(connection);
        connection.ChangeState(ConnectionStateEnum.ServerPlaying);
        player.status = PlayerStatus.Playing;
        player.ticksBehind = 61;
        player.ticksBehindReceivedAt = server.gameTimer;
        SetGlobalSpeed(5);
    }

    [TearDown]
    public void TearDown() => MultiplayerServer.instance = null;

    [Test]
    public void SustainedLagSchedulesOneStepPerCooldownDownToNormal()
    {
        server.AutoReduceSpeedIfBehind(1499);
        Assert.That(server.commands.SentCmds, Is.EqualTo(1));

        server.AutoReduceSpeedIfBehind(1);
        var commands = server.worldData.mapCmds[ScheduledCommand.Global];
        var reduction = ScheduledCommand.Deserialize(new ByteReader(commands.Last()));
        Assert.Multiple(() =>
        {
            Assert.That(reduction.type, Is.EqualTo(CommandType.GlobalTimeSpeed));
            Assert.That(reduction.playerId, Is.EqualTo(ScheduledCommand.NoPlayer));
            Assert.That(reduction.ticks, Is.EqualTo(server.gameTimer));
            Assert.That(reduction.data, Is.EqualTo(new byte[] { (byte)TimeVote.Ultrafast }));
        });

        server.AutoReduceSpeedIfBehind(4999);
        Assert.That(server.commands.SentCmds, Is.EqualTo(2));

        server.AutoReduceSpeedIfBehind(1);
        server.AutoReduceSpeedIfBehind(5000);
        server.AutoReduceSpeedIfBehind(5000);
        server.AutoReduceSpeedIfBehind(5000);
        Assert.That(server.commands.SentCmds, Is.EqualTo(5));
        Assert.That(server.commands.LastGlobalTimeSpeed, Is.EqualTo((byte)TimeVote.Normal));
    }

    [Test]
    public void RecoveringBelowTheThresholdClearsTheSustainedLagTimer()
    {
        server.AutoReduceSpeedIfBehind(1499);
        player.ticksBehind = 60;
        server.AutoReduceSpeedIfBehind(1);
        player.ticksBehind = 61;
        server.AutoReduceSpeedIfBehind(1499);

        Assert.That(server.commands.SentCmds, Is.EqualTo(1));
    }

    [Test]
    public void PauseAllPreventsTheServerFromSchedulingAnotherSpeed()
    {
        server.commands.PauseAll();
        server.AutoReduceSpeedIfBehind(5000);

        Assert.That(server.commands.SentCmds, Is.EqualTo(2));
        Assert.That(server.commands.LastGlobalTimeSpeed, Is.EqualTo((byte)TimeVote.Paused));
    }

    [Test]
    public void LowestWinsLeavesSpeedToThePlayers()
    {
        server.settings.timeControl = TimeControl.LowestWins;
        server.AutoReduceSpeedIfBehind(5000);

        Assert.That(server.commands.SentCmds, Is.EqualTo(1));
    }

    private void SetGlobalSpeed(byte speed) =>
        server.commands.Send(CommandType.GlobalTimeSpeed, ScheduledCommand.NoFaction, ScheduledCommand.Global, [speed], player);
}
