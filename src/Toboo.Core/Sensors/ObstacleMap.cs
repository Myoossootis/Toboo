namespace Toboo.Core;

public readonly record struct MapPoint(double X, double Z)
{
    public double Length => Math.Sqrt(X * X + Z * Z);
    public static MapPoint operator +(MapPoint a, MapPoint b) => new(a.X + b.X, a.Z + b.Z);
    public static MapPoint operator -(MapPoint a, MapPoint b) => new(a.X - b.X, a.Z - b.Z);
    public static MapPoint operator *(MapPoint a, double s) => new(a.X * s, a.Z * s);
}

public readonly record struct MapPose(MapPoint Position, MapPoint Right, MapPoint Down, MapPoint Forward);
public readonly record struct RangeMount(double RightMeters, double DownMeters, double ForwardMeters);
public readonly record struct MapCell(int X, int Z);
public readonly record struct MapVisit(MapPoint Position, bool StartsSegment);
public enum ObstacleAdvice { Unknown, Stop, NoNearEcho }

public sealed class ObstacleMap(RangeMount mount, int nearMm)
{
    public const double CellSize = 0.10;
    public static readonly TimeSpan PairWindow = TimeSpan.FromMilliseconds(140);
    public static readonly TimeSpan RangeLifetime = TimeSpan.FromMilliseconds(350);
    private readonly List<MapVisit> path = [];
    private readonly HashSet<MapCell> hits = [];
    private readonly Queue<(TimeSpan At, MapPose? Pose)> frames = new();
    private readonly Queue<(TimeSpan At, int Mm)> pending = new();
    private TimeSpan? lastPose;
    private TimeSpan? lastRange;
    private bool broken = true, near;
    private int clearReadings;
    public IReadOnlyList<MapVisit> Path => path;
    public IReadOnlyCollection<MapCell> Hits => hits;
    public MapPose? Pose { get; private set; }
    public int? DistanceMm { get; private set; }
    public int MappedEchoes { get; private set; }

    public void ObservePose(TimeSpan at, MapPose? pose)
    {
        if (lastPose is { } previous)
        {
            if (at <= previous) return;
            if (at - previous > PairWindow) broken = true;
        }
        lastPose = at;
        Pose = pose;
        frames.Enqueue((at, pose));
        while (frames.Count > 1 && (frames.Count > 32 || at - frames.Peek().At > RangeLifetime)) frames.Dequeue();
        if (pose is { } p && (broken || path.Count == 0 || (p.Position - path[^1].Position).Length >= 0.035))
        {
            path.Add(new(p.Position, broken));
            if (path.Count > 2500) path.RemoveAt(0);
            broken = false;
        }
        if (pose is null) broken = true;
        PairPending();
    }

    public void ObserveRange(TimeSpan at, int? mm)
    {
        if (lastRange is { } last && at <= last) return;
        lastRange = at;
        DistanceMm = mm is >= 20 and <= 4000 ? mm : null;
        if (DistanceMm is not { } distance) { pending.Clear(); clearReadings = 0; return; }
        if (distance <= nearMm) { near = true; clearReadings = 0; }
        else if (near && distance >= nearMm + 80)
        {
            if (++clearReadings >= 2) near = false;
        }
        else clearReadings = 0;
        pending.Enqueue((at, distance));
        while (pending.Count > 8) pending.Dequeue();
        PairPending();
    }

    public bool HasFreshPose(TimeSpan now) => Pose.HasValue && lastPose is { } last && now >= last && now - last <= PairWindow;
    public int? FreshDistance(TimeSpan now) => lastRange is { } last && now >= last && now - last <= RangeLifetime ? DistanceMm : null;
    public ObstacleAdvice Advice(TimeSpan now) => FreshDistance(now) is null ? ObstacleAdvice.Unknown : near
        ? ObstacleAdvice.Stop : HasFreshPose(now) ? ObstacleAdvice.NoNearEcho : ObstacleAdvice.Unknown;

    private void PairPending()
    {
        while (pending.TryPeek(out var range) && lastPose is { } latest && latest >= range.At)
        {
            pending.Dequeue();
            (TimeSpan At, MapPose? Pose)? before = null;
            foreach (var frame in frames)
            {
                if (frame.At <= range.At) before = frame;
                if (frame.At < range.At) continue;
                if (before is { Pose: { } earlier } previous && frame.Pose is { } later &&
                    frame.At - previous.At <= PairWindow && (later.Position - earlier.Position).Length <= 0.18 &&
                    (later.Forward - earlier.Forward).Length <= 0.25)
                {
                    var pose = range.At - previous.At <= frame.At - range.At ? earlier : later;
                    Project(pose, range.Mm / 1000.0);
                }
                break;
            }
        }
    }

    private void Project(MapPose pose, double meters)
    {
        // When the probe points up/down more than about 20 degrees, a 2-D floor map is unreliable.
        if (pose.Forward.Length < 0.94) return;
        var origin = pose.Position + pose.Right * mount.RightMeters + pose.Down * mount.DownMeters + pose.Forward * mount.ForwardMeters;
        for (var degrees = -10; degrees <= 10; degrees += 2)
        {
            var radians = degrees * Math.PI / 180;
            var candidate = origin + (pose.Forward * Math.Cos(radians) + pose.Right * Math.Sin(radians)) * meters;
            if (hits.Count < 6000) hits.Add(new((int)Math.Floor(candidate.X / CellSize), (int)Math.Floor(candidate.Z / CellSize)));
        }
        MappedEchoes++;
    }
}
