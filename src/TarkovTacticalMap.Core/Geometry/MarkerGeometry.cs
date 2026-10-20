namespace TarkovTacticalMap.Core.Geometry;

/// <summary>标记控制点类别。</summary>
public enum HandleKind
{
    None = 0,

    /// <summary>箭头头部（改长度与方向）。</summary>
    Tip = 1,

    /// <summary>圆半径控制点（改 Size）。</summary>
    Radius = 2,

    /// <summary>方框右下角（改 EndX/EndY）。</summary>
    Corner = 3,

    /// <summary>线终点（改 EndX/EndY）。</summary>
    EndPoint = 4,
}

/// <summary>一个控制点：类别 + 屏幕坐标。</summary>
public readonly struct HandleInfo
{
    public HandleInfo(HandleKind kind, float screenX, float screenY)
    {
        Kind = kind;
        ScreenX = screenX;
        ScreenY = screenY;
    }

    public HandleKind Kind { get; }
    public float ScreenX { get; }
    public float ScreenY { get; }

    public MapPoint ScreenPoint => new(ScreenX, ScreenY);
}

/// <summary>
/// 标记的屏幕几何计算：控制点位置、命中检测。
/// 全部在屏幕坐标系完成（控制点半径固定 6~8 像素，不随缩放变化）。
/// </summary>
public static class MarkerGeometry
{
    /// <summary>控制点半径（屏幕像素）。</summary>
    public const float HandleRadius = 7f;

    /// <summary>控制点命中容差（屏幕像素）。</summary>
    public const float HandleHitTolerance = 10f;

    /// <summary>箭头的方向向量（Rotation 为顺时针角度，屏幕 Y 轴向下）。</summary>
    public static MapPoint Direction(float rotationDegrees)
    {
        var rad = rotationDegrees * MathF.PI / 180f;
        return new MapPoint(MathF.Cos(rad), MathF.Sin(rad));
    }

    /// <summary>
    /// 箭头头部在屏幕上的位置：从 (X,Y) 沿 Rotation 方向延伸 Size × 图片显示宽度。
    /// 方向按截图坐标（屏幕）定义，因此旋转角与窗口宽高比无关。
    /// </summary>
    public static MapPoint GetArrowTipScreen(Models.Marker m, ViewportState vp)
    {
        var start = vp.NormalizedToScreen(m.X, m.Y);
        var lengthPx = MathF.Abs(m.Size) * vp.DisplayWidth;
        var dir = Direction(m.Rotation);
        return new MapPoint(start.X + dir.X * lengthPx, start.Y + dir.Y * lengthPx);
    }

    /// <summary>由「箭尾屏幕坐标 → 目标屏幕坐标」反算 Rotation(度) 与 Size(相对图宽)。</summary>
    public static (float Rotation, float Size) ComputeArrowGeometry(MapPoint startScreen, MapPoint targetScreen, float displayWidth)
    {
        var dx = targetScreen.X - startScreen.X;
        var dy = targetScreen.Y - startScreen.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 1e-4f) return (0f, 0f);

        var degrees = MathF.Atan2(dy, dx) * 180f / MathF.PI;
        if (degrees < 0f) degrees += 360f;

        var size = displayWidth > 1e-4f ? length / displayWidth : 0f;
        return (degrees, size);
    }

    /// <summary>圆半径（归一化，相对图宽）。</summary>
    public static float GetCircleRadiusNormalized(Models.Marker m) => MathF.Abs(m.Size);

    /// <summary>获取某标记的控制点（屏幕坐标）。不支持的标记返回空列表。</summary>
    public static IReadOnlyList<HandleInfo> GetHandles(Models.Marker m, ViewportState vp)
    {
        if (m is null || !vp.IsValid) return Array.Empty<HandleInfo>();
        switch (m.Type)
        {
            case Models.MarkerType.Arrow:
            {
                var tip = GetArrowTipScreen(m, vp);
                return new[] { new HandleInfo(HandleKind.Tip, tip.X, tip.Y) };
            }
            case Models.MarkerType.Line:
            {
                var p = vp.NormalizedToScreen(m.EndX, m.EndY);
                return new[] { new HandleInfo(HandleKind.EndPoint, p.X, p.Y) };
            }
            case Models.MarkerType.Circle:
            {
                var r = GetCircleRadiusNormalized(m);
                var p = vp.NormalizedToScreen(m.X + r, m.Y);
                return new[] { new HandleInfo(HandleKind.Radius, p.X, p.Y) };
            }
            case Models.MarkerType.Rectangle:
            {
                var p = vp.NormalizedToScreen(m.EndX, m.EndY);
                return new[] { new HandleInfo(HandleKind.Corner, p.X, p.Y) };
            }
            default:
                return Array.Empty<HandleInfo>();
        }
    }

    /// <summary>命中控制点检测（屏幕坐标，容差 HandleHitTolerance 像素）。</summary>
    public static HandleInfo? HitTestHandle(Models.Marker m, MapPoint screenPoint, ViewportState vp)
    {
        foreach (var h in GetHandles(m, vp))
        {
            if (MathF.Abs(h.ScreenX - screenPoint.X) <= HandleHitTolerance &&
                MathF.Abs(h.ScreenY - screenPoint.Y) <= HandleHitTolerance)
            {
                return h;
            }
        }
        return null;
    }

    /// <summary>命中标记检测（屏幕坐标）。</summary>
    public static bool HitTest(Models.Marker m, MapPoint screenPoint, ViewportState vp, float tolerance = 8f)
    {
        if (m is null || !vp.IsValid) return false;
        var displayWidth = vp.DisplayWidth;
        var center = vp.NormalizedToScreen(m.X, m.Y);
        var sizePx = MathF.Abs(m.Size) * displayWidth;

        switch (m.Type)
        {
            case Models.MarkerType.Point:
            {
                var radius = MathF.Max(sizePx / 2f, tolerance);
                return center.DistanceTo(screenPoint) <= radius;
            }
            case Models.MarkerType.Text:
            {
                // 粗略包围盒（渲染时按字号与字数估算），命中判定只需足够接近
                var fontPx = MathF.Max(MathF.Abs(m.Size) * displayWidth, 8f);
                var text = m.Text ?? string.Empty;
                var w = MathF.Max(fontPx * 0.6f * text.Length, fontPx);
                var h = fontPx * 1.4f;
                var rect = new MapRect(center.X, center.Y, w, h);
                return rect.Contains(new MapPoint(screenPoint.X, screenPoint.Y)) ||
                       center.DistanceTo(screenPoint) <= tolerance;
            }
            case Models.MarkerType.Arrow:
            {
                var tip = GetArrowTipScreen(m, vp);
                return DistanceToSegment(screenPoint, center, tip) <= MathF.Max(tolerance, 6f);
            }
            case Models.MarkerType.Line:
            {
                var end = vp.NormalizedToScreen(m.EndX, m.EndY);
                var tol = MathF.Max(tolerance, sizePx / 2f + 4f);
                return DistanceToSegment(screenPoint, center, end) <= tol;
            }
            case Models.MarkerType.Circle:
            {
                var radiusPx = MathF.Abs(m.Size) * displayWidth;
                var d = center.DistanceTo(screenPoint);
                return MathF.Abs(d - radiusPx) <= MathF.Max(tolerance, 6f) ||
                       d <= MathF.Max(tolerance, 6f);
            }
            case Models.MarkerType.Rectangle:
            {
                var p1 = center;
                var p2 = vp.NormalizedToScreen(m.EndX, m.EndY);
                var left = MathF.Min(p1.X, p2.X);
                var right = MathF.Max(p1.X, p2.X);
                var top = MathF.Min(p1.Y, p2.Y);
                var bottom = MathF.Max(p1.Y, p2.Y);
                var inside = screenPoint.X >= left - tolerance && screenPoint.X <= right + tolerance &&
                             screenPoint.Y >= top - tolerance && screenPoint.Y <= bottom + tolerance;
                if (!inside) return false;
                // 靠近边框或足够小（内部也算选中）
                var nearEdgeX = MathF.Min(MathF.Abs(screenPoint.X - left), MathF.Abs(screenPoint.X - right)) <= tolerance;
                var nearEdgeY = MathF.Min(MathF.Abs(screenPoint.Y - top), MathF.Abs(screenPoint.Y - bottom)) <= tolerance;
                return nearEdgeX || nearEdgeY || (right - left) < tolerance * 2f || (bottom - top) < tolerance * 2f;
            }
            default:
                return center.DistanceTo(screenPoint) <= MathF.Max(tolerance, sizePx / 2f);
        }
    }

    /// <summary>点到线段距离。</summary>
    public static float DistanceToSegment(MapPoint p, MapPoint a, MapPoint b)
    {
        var abx = b.X - a.X;
        var aby = b.Y - a.Y;
        var lenSq = abx * abx + aby * aby;
        if (lenSq < 1e-6f) return p.DistanceTo(a);
        var t = ((p.X - a.X) * abx + (p.Y - a.Y) * aby) / lenSq;
        t = Math.Clamp(t, 0f, 1f);
        var proj = new MapPoint(a.X + abx * t, a.Y + aby * t);
        return p.DistanceTo(proj);
    }
}
