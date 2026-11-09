using System.Globalization;

namespace Toboo.Core;

public enum SensorChannel { Acceleration, Distance }

/// <summary>Acceleration in mg; distance in mm. Invalid samples contain zero payloads.</summary>
public sealed record SensorPacket(uint Sequence, uint DeviceMilliseconds, SensorChannel Channel,
    bool Valid, int X, int Y = 0, int Z = 0)
{
    public const string Hello = "TOBOO_SENSOR,1,UNO";

    public string Encode()
    {
        var body = FormattableString.Invariant($"T1,{Sequence},{DeviceMilliseconds},{(Channel == SensorChannel.Acceleration ? "A" : "D")},{(Valid ? 1 : 0)},{X},{Y},{Z}");
        return $"{body}*{Checksum(body):X2}";
    }

    public static bool TryParse(string line, out SensorPacket? packet)
    {
        packet = null;
        if (line.Length is < 18 or > 96 || line.Any(c => c > 127)) return false;
        var split = line.LastIndexOf('*');
        if (split != line.Length - 3 || !byte.TryParse(line[(split + 1)..], NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var checksum) || Checksum(line[..split]) != checksum) return false;
        var p = line[..split].Split(',');
        if (p.Length != 8 || p[0] != "T1" || p[3] is not ("A" or "D") || p[4] is not ("0" or "1") ||
            !uint.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) ||
            !uint.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds)) return false;
        var values = new int[3];
        for (var i = 0; i < 3; i++)
            if (!int.TryParse(p[i + 5], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out values[i])) return false;
        var valid = p[4] == "1";
        if (!valid && values.Any(v => v != 0)) return false;
        if (p[3] == "D" && (values[1] != 0 || values[2] != 0 || valid && values[0] is < 20 or > 4000)) return false;
        if (p[3] == "A" && values.Any(v => v is < -2000 or > 2000)) return false;
        packet = new(sequence, milliseconds, p[3] == "A" ? SensorChannel.Acceleration : SensorChannel.Distance,
            valid, values[0], values[1], values[2]);
        return true;
    }

    private static byte Checksum(string body)
    {
        byte value = 0;
        foreach (var c in body) value ^= (byte)c;
        return value;
    }
}

public sealed record SensorReading(SensorPacket Packet, TimeSpan SampledAt)
{
    public bool Fresh(TimeSpan now, TimeSpan maximumAge) => Packet.Valid && now >= SampledAt && now - SampledAt <= maximumAge;
}

/// <summary>Pure telemetry receiver. Reconnect/reboot requires a hello; device ticks are never wall time.</summary>
public sealed class SensorTelemetry
{
    private uint? sequence;
    private uint deviceTicks;
    private TimeSpan mappedTime;
    private TimeSpan lastArrival;
    public bool Connected { get; private set; }
    public SensorReading? Acceleration { get; private set; }
    public SensorReading? Distance { get; private set; }
    public int Rejected { get; private set; }
    public long MissingPackets { get; private set; }
    public int Sessions { get; private set; }
    public string Notice { get; private set; } = "等待 UNO 遥测";

    public bool Accept(string line, TimeSpan receivedAt)
    {
        if (receivedAt < TimeSpan.Zero || Connected && receivedAt < lastArrival) return Reject("接收时钟倒退");
        if (line == SensorPacket.Hello)
        {
            Connected = true; sequence = null; Acceleration = Distance = null;
            lastArrival = receivedAt; Sessions++; Notice = "新遥测会话，等待有效样本";
            return true;
        }
        if (!Connected || !SensorPacket.TryParse(line, out var packet)) return Reject("协议、校验或握手无效");
        var value = packet!;
        if (sequence is { } previous)
        {
            var delta = unchecked(value.Sequence - previous);
            var elapsed = unchecked(value.DeviceMilliseconds - deviceTicks);
            if (delta == 0 || delta >= 0x80000000 || elapsed >= 0x80000000)
                return Reject("重复、乱序或设备重启；重启后需重新握手");
            MissingPackets += delta - 1;
            mappedTime += TimeSpan.FromMilliseconds(elapsed);
            // Keep the tightest observed device/host clock offset. Never stamp buffered data as new.
            if (mappedTime > receivedAt) mappedTime = receivedAt;
        }
        else mappedTime = receivedAt;
        sequence = value.Sequence; deviceTicks = value.DeviceMilliseconds; lastArrival = receivedAt;
        if (receivedAt - mappedTime > TimeSpan.FromMilliseconds(250)) return Reject("缓冲数据延迟超过 250ms");
        var reading = new SensorReading(value, mappedTime);
        if (value.Channel == SensorChannel.Acceleration) Acceleration = reading;
        else Distance = reading;
        Notice = value.Valid ? "遥测接收中" : "模块未启用、读取失败或无有效回波";
        return true;
    }

    public void Disconnect()
    {
        Connected = false; sequence = null; Acceleration = Distance = null; Notice = "采集已断开";
    }

    private bool Reject(string reason) { Rejected++; Notice = reason; return false; }
}

public enum CompanionState { WaitingForSensors, Resting, Companion, PostureProtection, DistanceProtection, AwaitingResume }
public sealed record FusionPreview(CompanionState State, bool MotionCandidate, string Reason,
    double? TiltDegrees, int? DistanceMillimeters, bool AccelerationFresh, bool DistanceFresh);

/// <summary>Experimental preview only: no actuator output. Z-up mounting and thresholds need calibration.</summary>
public sealed class CompanionFusionPreview
{
    private bool latched = true;
    private TimeSpan? stableSince;
    private TimeSpan? lastNow;
    private TimeSpan? faceAt;
    private int session;
    public void ObserveFace(bool confirmed, TimeSpan now) { if (confirmed) faceAt = now; }

    public FusionPreview Step(SensorTelemetry telemetry, TimeSpan now, bool requestResume = false)
    {
        if (now < TimeSpan.Zero || lastNow is { } previous && now < previous) throw new ArgumentException("Fusion requires a monotonic clock.");
        if (session != telemetry.Sessions)
        {
            session = telemetry.Sessions; latched = true; stableSince = null; faceAt = null;
        }
        if (lastNow is { } last && now - last > TimeSpan.FromMilliseconds(200))
        {
            latched = true; stableSince = null; faceAt = null;
        }
        lastNow = now;
        var a = telemetry.Acceleration;
        var d = telemetry.Distance;
        var af = telemetry.Connected && a?.Fresh(now, TimeSpan.FromMilliseconds(200)) == true;
        var df = telemetry.Connected && d?.Fresh(now, TimeSpan.FromMilliseconds(500)) == true;
        double? tilt = null;
        double magnitude = 0;
        if (af)
        {
            var p = a!.Packet;
            magnitude = Math.Sqrt((double)p.X * p.X + (double)p.Y * p.Y + (double)p.Z * p.Z);
            if (magnitude > 0) tilt = Math.Acos(Math.Clamp(p.Z / magnitude, -1, 1)) * 180 / Math.PI;
        }
        int? distance = df ? d!.Packet.X : null;
        FusionPreview Result(CompanionState state, string reason, bool permit = false) =>
            new(state, permit, reason, tilt, distance, af, df);
        // Any fault latches recovery. Fresh, stable samples alone never resume an old action.
        if (!af || !df)
        {
            latched = true; stableSince = null; faceAt = null;
            return Result(CompanionState.WaitingForSensors, "缺失、无效或过期数据；候选运动暂停");
        }
        if (tilt is null or >= 25 || magnitude is < 750 or > 1250)
        {
            latched = true; stableSince = null; faceAt = null;
            return Result(CompanionState.PostureProtection, "倾斜或运动加速度异常（待标定）");
        }
        if (distance < 150)
        {
            latched = true; stableSince = null; faceAt = null;
            return Result(CompanionState.DistanceProtection, "近物小于 150mm（待标定）");
        }
        var stable = tilt <= 15 && magnitude is >= 900 and <= 1100 && distance >= 200;
        if (!stable) stableSince = null;
        else stableSince ??= now;
        if (latched && requestResume && stableSince is { } since && now - since >= TimeSpan.FromSeconds(2)) latched = false;
        if (latched) return Result(CompanionState.AwaitingResume, "放稳并退开持续 2 秒后，需显式确认恢复");
        var face = faceAt is { } seen && now >= seen && now - seen <= TimeSpan.FromMilliseconds(500);
        return face ? Result(CompanionState.Companion, "新鲜人脸与传感器数据；仅预览运动候选", true)
            : Result(CompanionState.Resting, "无新鲜人脸确认；休息");
    }
}
