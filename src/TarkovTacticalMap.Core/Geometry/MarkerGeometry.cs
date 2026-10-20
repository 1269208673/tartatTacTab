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
/// 标记的屏幕几何计算：控制点位置、包围盒、线宽、命中检测。
/// 全部在屏幕坐标系完成（控制点半径固定 7~8 像素，不随缩放变化）。
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

    /// <summary>
    /// 描边 / 线宽（屏幕像素）。Thickness 优先；为 0 时按旧规则从 Size 推导，保证旧数据渲染一致。
    /// </summary>
    public static float GetStrokeWidthPixels(Models.Marker m, ViewportState vp)
    {
        var displayWidth = vp.DisplayWidth;
        if (m.Thickness > 0f) return MathF.Max(0.8f, m.Thickness * displayWidth);

        var sizePx = MathF.Abs(m.Size) * displayWidth;
        return m.Type switch
        {
            Models.MarkerType.Circle => MathF.Max(1f, MathF.Min(2f, sizePx * 0.1f)),
            Models.MarkerType.Arrow => MathF.Max(2f, sizePx * 0.16f),
            _ => MathF.Max(1f, sizePx),
        };
    }

    /// <summary>自由笔画的屏幕坐标折线。</summary>
    public static MapPoint[] GetStrokeScreenPoints(Models.Marker m, ViewportState vp)
    {
        var points = m.Points;
        if (points is null || points.Length == 0) return Array.Empty<MapPoint>();

        var result = new MapPoint[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            result[i] = vp.NormalizedToScreen(points[i].X, points[i].Y);
        }
        return result;
    }

    /// <summary>标记在屏幕上的包围盒（选中框 / 大小控制点定位用）。</summary>
    public static MapRect GetScreenBounds(Models.Marker m, ViewportState vp)
    {
        var displayWidth = vp.DisplayWidth;
        var sizePx = MathF.Abs(m.Size) * displayWidth;
        var anchor = vp.NormalizedToScreen(m.X, m.Y);

        switch (m.Type)
        {
            case Models.MarkerType.Point:
            {
                var half = MathF.Max(sizePx / 2f, 5f);
                return new MapRect(anchor.X - half, anchor.Y - half, half * 2, half * 2);
            }
            case Models.MarkerType.Text:
            {
                var fontPx = MathF.Max(sizePx, 8f);
                var text = m.Text ?? string.Empty;
                var w = MathF.Max(fontPx * 0.62f * Math.Max(text.Length, 1), fontPx);
                var h = fontPx * 1.35f;
                return new MapRect(anchor.X, anchor.Y, w, h);
            }
            case Models.MarkerType.Arrow:
            {
                var tip = GetArrowTipScreen(m, vp);
                var thickness = GetStrokeWidthPixels(m, vp);
                var left = MathF.Min(anchor.X, tip.X) - thickness;
                var top = MathF.Min(anchor.Y, tip.Y) - thickness;
                var right = MathF.Max(anchor.X, tip.X) + thickness;
                var bottom = MathF.Max(anchor.Y, tip.Y) + thickness;
                return new MapRect(left, top, right - left, bottom - top);
            }
            case Models.MarkerType.Line:
            {
                var end = vp.NormalizedToScreen(m.EndX, m.EndY);
                var pad = MathF.Max(GetStrokeWidthPixels(m, vp), 3f);
                var left = MathF.Min(anchor.X, end.X) - pad;
                var top = MathF.Min(anchor.Y, end.Y) - pad;
                var right = MathF.Max(anchor.X, end.X) + pad;
                var bottom = MathF.Max(anchor.Y, end.Y) + pad;
                return new MapRect(left, top, right - left, bottom - top);
            }
            case Models.MarkerType.Circle:
            {
                var radiusPx = MathF.Max(sizePx, 2f) + GetStrokeWidthPixels(m, vp) / 2f;
                return new MapRect(anchor.X - radiusPx, anchor.Y - radiusPx, radiusPx * 2, radiusPx * 2);
            }
            case Models.MarkerType.Rectangle:
            {
                var end = vp.NormalizedToScreen(m.EndX, m.EndY);
                var pad = GetStrokeWidthPixels(m, vp) / 2f;
                var left = MathF.Min(anchor.X, end.X) - pad;
                var top = MathF.Min(anchor.Y, end.Y) - pad;
                var right = MathF.Max(anchor.X, end.X) + pad;
                var bottom = MathF.Max(anchor.Y, end.Y) + pad;
                return new MapRect(left, top, MathF.Max(right - left, 1f), MathF.Max(bottom - top, 1f));
            }
            case Models.MarkerType.Stroke:
            {
                var points = GetStrokeScreenPoints(m, vp);
                if (points.Length == 0)
                {
                    var half = MathF.Max(sizePx / 2f, 5f);
                    return new MapRect(anchor.X - half, anchor.Y - half, half * 2, half * 2);
                }

                var pad = MathF.Max(GetStrokeWidthPixels(m, vp) / 2f, 2f);
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                foreach (var p in points)
                {
                    minX = MathF.Min(minX, p.X);
                    minY = MathF.Min(minY, p.Y);
                    maxX = MathF.Max(maxX, p.X);
                    maxY = MathF.Max(maxY, p.Y);
                }
                return new MapRect(minX - pad, minY - pad, MathF.Max(maxX - minX, 1f) + pad * 2, MathF.Max(maxY - minY, 1f) + pad * 2);
            }
            default:
            {
                var half = MathF.Max(sizePx / 2f, 5f);
                return new MapRect(anchor.X - half, anchor.Y - half, half * 2, half * 2);
            }
        }
    }

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
        var strokePx = GetStrokeWidthPixels(m, vp);

        switch (m.Type)
        {
            case Models.MarkerType.Point:
            {
                var radius = MathF.Max(sizePx / 2f, tolerance);
                return center.DistanceTo(screenPoint) <= radius;
            }
            case Models.MarkerType.Text:
            {
                var bounds = GetScreenBounds(m, vp);
                return bounds.Contains(screenPoint) || center.DistanceTo(screenPoint) <= tolerance;
            }
            case Models.MarkerType.Arrow:
            {
                var tip = GetArrowTipScreen(m, vp);
                return DistanceToSegment(screenPoint, center, tip) <= MathF.Max(tolerance, strokePx / 2f + 4f);
            }
            case Models.MarkerType.Line:
            {
                var end = vp.NormalizedToScreen(m.EndX, m.EndY);
                return DistanceToSegment(screenPoint, center, end) <= MathF.Max(tolerance, strokePx / 2f + 4f);
            }
            case Models.MarkerType.Circle:
            {
                var radiusPx = MathF.Abs(m.Size) * displayWidth;
                var d = center.DistanceTo(screenPoint);
                var tol = MathF.Max(tolerance, strokePx / 2f + 4f);
                return MathF.Abs(d - radiusPx) <= tol || d <= tol;
            }
            case Models.MarkerType.Rectangle:
            {
                var p1 = center;
                var p2 = vp.NormalizedToScreen(m.EndX, m.EndY);
                var left = MathF.Min(p1.X, p2.X);
                var right = MathF.Max(p1.X, p2.X);
                var top = MathF.Min(p1.Y, p2.Y);
                var bottom = MathF.Max(p1.Y, p2.Y);
                var tol = MathF.Max(tolerance, strokePx / 2f + 4f);
                var inside = screenPoint.X >= left - tol && screenPoint.X <= right + tol &&
                             screenPoint.Y >= top - tol && screenPoint.Y <= bottom + tol;
                if (!inside) return false;
                var nearEdgeX = MathF.Min(MathF.Abs(screenPoint.X - left), MathF.Abs(screenPoint.X - right)) <= tol;
                var nearEdgeY = MathF.Min(MathF.Abs(screenPoint.Y - top), MathF.Abs(screenPoint.Y - bottom)) <= tol;
                return nearEdgeX || nearEdgeY || (right - left) < tol * 2f || (bottom - top) < tol * 2f;
            }
            case Models.MarkerType.Stroke:
            {
                var points = GetStrokeScreenPoints(m, vp);
                if (points.Length == 0) return false;
                if (points.Length == 1) return points[0].DistanceTo(screenPoint) <= MathF.Max(tolerance, strokePx / 2f + 4f);

                var tol = MathF.Max(tolerance, strokePx / 2f + 4f);
                for (var i = 1; i < points.Length; i++)
                {
                    if (DistanceToSegment(screenPoint, points[i - 1], points[i]) <= tol) return true;
                }
                return false;
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
