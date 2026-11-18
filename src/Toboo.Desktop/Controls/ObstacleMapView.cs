using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Toboo.Core;

namespace Toboo.Desktop.Controls;

public sealed class ObstacleMapView : Control
{
    private static readonly IBrush BackgroundBrush = Brush.Parse("#F4F8F7");
    private static readonly IBrush EchoBrush = Brush.Parse("#CE7947");
    private static readonly Pen GridPen = new(Brush.Parse("#DFE9E6"), 1);
    private static readonly Pen Axis = new(Brush.Parse("#B6C8C4"), 1);
    private static readonly Pen Trail = new(Brush.Parse("#179387"), 2.5);
    private static readonly Pen Stale = new(Brushes.Gray, 2);
    private ObstacleMap? map;
    private bool positionFresh;

    internal void Show(ObstacleMap? value, bool fresh)
    {
        map = value; positionFresh = fresh; InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(BackgroundBrush, bounds);
        if (bounds.Width < 32 || bounds.Height < 32) return;
        using var clip = context.PushClip(bounds);
        var minX = -2.0; var maxX = 2.0; var minZ = -0.5; var maxZ = 3.5;
        void Extend(MapPoint p)
        {
            minX = Math.Min(minX, p.X - 0.3); maxX = Math.Max(maxX, p.X + 0.3);
            minZ = Math.Min(minZ, p.Z - 0.3); maxZ = Math.Max(maxZ, p.Z + 0.3);
        }
        if (map is not null)
        {
            foreach (var visit in map.Path) Extend(visit.Position);
            foreach (var hit in map.Hits) Extend(new(hit.X * ObstacleMap.CellSize, hit.Z * ObstacleMap.CellSize));
        }
        var scale = Math.Min((bounds.Width - 32) / (maxX - minX), (bounds.Height - 32) / (maxZ - minZ));
        Point At(MapPoint p) => new(bounds.Width / 2 + (p.X - (minX + maxX) / 2) * scale,
            bounds.Height / 2 + (p.Z - (minZ + maxZ) / 2) * scale);
        for (var x = Math.Floor(minX); x <= maxX; x += 0.5)
            context.DrawLine(x == 0 ? Axis : GridPen, At(new(x, minZ)), At(new(x, maxZ)));
        for (var z = Math.Floor(minZ); z <= maxZ; z += 0.5)
            context.DrawLine(z == 0 ? Axis : GridPen, At(new(minX, z)), At(new(maxX, z)));
        var tag = At(new(0, 0));
        context.FillRectangle(Brushes.DarkSlateGray, new Rect(tag.X - 5, tag.Y - 4, 10, 8));
        context.DrawLine(Axis, new(18, bounds.Height - 15), new(18 + scale, bounds.Height - 15));
        if (map is null) return;
        var cell = ObstacleMap.CellSize;
        foreach (var hit in map.Hits)
            context.FillRectangle(EchoBrush, new Rect(At(new(hit.X * cell, hit.Z * cell)), new Size(cell * scale + 0.3, cell * scale + 0.3)));
        for (var i = 1; i < map.Path.Count; i++)
        {
            var visit = map.Path[i];
            if (!visit.StartsSegment) context.DrawLine(Trail, At(map.Path[i - 1].Position), At(visit.Position));
        }
        if (map.Path.Count == 0) return;
        var pen = positionFresh ? Trail : Stale;
        var position = map.Pose?.Position ?? map.Path[^1].Position;
        var current = At(position);
        context.DrawEllipse(Brushes.White, pen, current, 5, 5);
        if (positionFresh && map.Pose is { } pose)
        {
            var tip = At(position + pose.Forward * 0.25);
            context.DrawLine(pen, current, tip);
            context.DrawLine(pen, tip, At(position + pose.Forward * 0.17 + pose.Right * 0.05));
            context.DrawLine(pen, tip, At(position + pose.Forward * 0.17 - pose.Right * 0.05));
        }
    }
}
