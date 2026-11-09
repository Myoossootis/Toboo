using Toboo.Core;
using Xunit;

namespace Toboo.Tests;

public sealed class SensorTelemetryTests
{
    private static TimeSpan Ms(double value) => TimeSpan.FromMilliseconds(value);
    private static SensorTelemetry Receiver()
    {
        var telemetry = new SensorTelemetry();
        Assert.True(telemetry.Accept(SensorPacket.Hello, Ms(0)));
        return telemetry;
    }

    [Theory]
    [InlineData(SensorChannel.Acceleration, true, -1000, 2, 1000)]
    [InlineData(SensorChannel.Distance, true, 200, 0, 0)]
    [InlineData(SensorChannel.Distance, false, 0, 0, 0)]
    public void PacketRoundTripUsesInvariantIntegerUnits(SensorChannel channel, bool valid, int x, int y, int z)
    {
        var expected = new SensorPacket(uint.MaxValue, uint.MaxValue, channel, valid, x, y, z);
        Assert.True(SensorPacket.TryParse(expected.Encode(), out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CorruptionUnsupportedVersionNonfiniteAndRangeErrorsAreRejected()
    {
        var encoded = new SensorPacket(0, 0, SensorChannel.Distance, true, 200).Encode();
        Assert.False(SensorPacket.TryParse(encoded.Replace("200", "201"), out _));
        Assert.False(SensorPacket.TryParse(encoded.Replace("T1", "T2"), out _));
        Assert.False(SensorPacket.TryParse(new string('a', 1000), out _));
        Assert.False(SensorPacket.TryParse(new SensorPacket(0, 0, SensorChannel.Distance, true, 0).Encode(), out _));
        Assert.False(SensorPacket.TryParse(new SensorPacket(0, 0, SensorChannel.Distance, false, 500).Encode(), out _));
        Assert.False(SensorPacket.TryParse(new SensorPacket(0, 0, SensorChannel.Acceleration, true, 2001).Encode(), out _));
    }

    [Fact]
    public void HandshakeIsRequiredAndNewSessionClearsOldObservations()
    {
        var telemetry = new SensorTelemetry();
        var line = new SensorPacket(0, 0, SensorChannel.Distance, true, 500).Encode();
        Assert.False(telemetry.Accept(line, Ms(0)));
        Assert.True(telemetry.Accept(SensorPacket.Hello, Ms(1)));
        Assert.True(telemetry.Accept(line, Ms(2)));
        Assert.NotNull(telemetry.Distance);
        telemetry.Accept(SensorPacket.Hello, Ms(3));
        Assert.Null(telemetry.Distance);
        telemetry.Disconnect();
        Assert.False(telemetry.Accept(line, Ms(4)));
    }

    [Fact]
    public void DuplicateOutOfOrderAndClockReversalNeverRefreshASample()
    {
        var telemetry = Receiver();
        var line = new SensorPacket(2, 50, SensorChannel.Acceleration, true, 0, 0, 1000).Encode();
        Assert.True(telemetry.Accept(line, Ms(50)));
        Assert.False(telemetry.Accept(line, Ms(100)));
        Assert.False(telemetry.Accept(new SensorPacket(1, 60, SensorChannel.Acceleration, true, 0, 0, 1000).Encode(), Ms(100)));
        Assert.False(telemetry.Accept(new SensorPacket(3, 60, SensorChannel.Acceleration, true, 0, 0, 1000).Encode(), Ms(40)));
        Assert.Equal(Ms(50), telemetry.Acceleration!.SampledAt);
        Assert.False(telemetry.Acceleration.Fresh(Ms(251), Ms(200)));
    }

    [Fact]
    public void DeviceTimeAndSequenceWrapAreAcceptedButRebootWithoutHandshakeIsNot()
    {
        var telemetry = Receiver();
        Assert.True(telemetry.Accept(new SensorPacket(uint.MaxValue, uint.MaxValue - 20, SensorChannel.Distance, true, 500).Encode(), Ms(0)));
        Assert.True(telemetry.Accept(new SensorPacket(0, 29, SensorChannel.Distance, true, 500).Encode(), Ms(50)));
        Assert.False(telemetry.Accept(new SensorPacket(1, 0, SensorChannel.Distance, true, 500).Encode(), Ms(60)));
        Assert.Equal(0, telemetry.MissingPackets);
    }

    [Fact]
    public void BufferedPacketsKeepDeviceTimeAndCannotBecomeFreshOnArrival()
    {
        var telemetry = Receiver();
        telemetry.Accept(new SensorPacket(0, 0, SensorChannel.Distance, true, 500).Encode(), Ms(0));
        Assert.False(telemetry.Accept(new SensorPacket(4, 100, SensorChannel.Distance, true, 500).Encode(), Ms(1000)));
        Assert.Equal(3, telemetry.MissingPackets);
        Assert.Equal(Ms(0), telemetry.Distance!.SampledAt);
        Assert.True(telemetry.Accept(new SensorPacket(5, 1000, SensorChannel.Distance, true, 600).Encode(), Ms(1000)));
        Assert.Equal(Ms(1000), telemetry.Distance.SampledAt);
    }

    [Fact]
    public void InvalidEchoReplacesPreviousDistanceImmediately()
    {
        var telemetry = Receiver();
        telemetry.Accept(new SensorPacket(0, 0, SensorChannel.Distance, true, 500).Encode(), Ms(0));
        telemetry.Accept(new SensorPacket(1, 100, SensorChannel.Distance, false, 0).Encode(), Ms(100));
        Assert.False(telemetry.Distance!.Fresh(Ms(100), Ms(500)));
    }

    [Fact]
    public void ProtectionRequiresFreshStableDataAndExplicitResumeAndFaceExpires()
    {
        var telemetry = Receiver(); var fusion = new CompanionFusionPreview(); uint seq = 0;
        FusionPreview Tick(int ms, int distance = 500, int x = 0, int z = 1000, bool resume = false, bool face = true)
        {
            telemetry.Accept(new SensorPacket(seq++, (uint)ms, SensorChannel.Acceleration, true, x, 0, z).Encode(), Ms(ms));
            telemetry.Accept(new SensorPacket(seq++, (uint)ms, SensorChannel.Distance, true, distance).Encode(), Ms(ms));
            fusion.ObserveFace(face, Ms(ms));
            return fusion.Step(telemetry, Ms(ms), resume);
        }
        for (var ms = 0; ms <= 2100; ms += 50) Assert.False(Tick(ms).MotionCandidate);
        Assert.True(Tick(2150, resume: true).MotionCandidate);
        Assert.Equal(CompanionState.PostureProtection, Tick(2200, distance: 50, x: 1000, z: 0).State);
        Assert.Equal(CompanionState.DistanceProtection, Tick(2250, distance: 50).State);
        for (var ms = 2300; ms <= 4400; ms += 50) Assert.False(Tick(ms).MotionCandidate);
        Assert.True(Tick(4450, resume: true).MotionCandidate);
        for (var ms = 4500; ms <= 5000; ms += 50) Tick(ms, face: false);
        Assert.Equal(CompanionState.Resting, Tick(5050, face: false).State);
        Assert.Equal(CompanionState.WaitingForSensors, fusion.Step(telemetry, Ms(5300)).State);
        Assert.False(Tick(5350, resume: true).MotionCandidate);
    }

    [Fact]
    public void ANewSessionRequiresNewStableHistoryAndCannotReuseAnOldFace()
    {
        var telemetry = Receiver(); var fusion = new CompanionFusionPreview(); uint sequence = 0;
        FusionPreview Tick(int ms, bool resume = false, bool face = true)
        {
            telemetry.Accept(new SensorPacket(sequence++, (uint)ms, SensorChannel.Acceleration, true, 0, 0, 1000).Encode(), Ms(ms));
            telemetry.Accept(new SensorPacket(sequence++, (uint)ms, SensorChannel.Distance, true, 500).Encode(), Ms(ms));
            fusion.ObserveFace(face, Ms(ms));
            return fusion.Step(telemetry, Ms(ms), resume);
        }
        for (var ms = 0; ms <= 2100; ms += 50) Tick(ms);
        Assert.True(Tick(2150, true).MotionCandidate);
        telemetry.Accept(SensorPacket.Hello, Ms(2200));
        sequence = 0;
        Assert.False(Tick(2200, true, false).MotionCandidate);
        for (var ms = 2250; ms < 4250; ms += 50) Tick(ms, false, false);
        Assert.Equal(CompanionState.Resting, Tick(4250, true, false).State);
        Assert.True(Tick(4300).MotionCandidate);
    }

    [Fact]
    public void UpdateGapCannotCountAsTwoSecondsOfStability()
    {
        var telemetry = Receiver(); var fusion = new CompanionFusionPreview();
        telemetry.Accept(new SensorPacket(0, 0, SensorChannel.Acceleration, true, 0, 0, 1000).Encode(), Ms(0));
        telemetry.Accept(new SensorPacket(1, 0, SensorChannel.Distance, true, 500).Encode(), Ms(0));
        fusion.Step(telemetry, Ms(0));
        telemetry.Accept(new SensorPacket(2, 3000, SensorChannel.Acceleration, true, 0, 0, 1000).Encode(), Ms(3000));
        telemetry.Accept(new SensorPacket(3, 3000, SensorChannel.Distance, true, 500).Encode(), Ms(3000));
        fusion.ObserveFace(true, Ms(3000));
        Assert.False(fusion.Step(telemetry, Ms(3000), true).MotionCandidate);
        Assert.Throws<ArgumentException>(() => fusion.Step(telemetry, Ms(2000)));
    }
}
