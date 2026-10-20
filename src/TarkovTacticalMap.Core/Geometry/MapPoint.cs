namespace TarkovTacticalMap.Core.Geometry;

/// <summary>二维点（float），用于屏幕坐标或归一化坐标。</summary>
public readonly struct MapPoint : IEquatable<MapPoint>
{
    public MapPoint(float x, float y)
    {
        X = x;
        Y = y;
    }

    public float X { get; }
    public float Y { get; }

    public static MapPoint operator +(MapPoint a, MapPoint b) => new(a.X + b.X, a.Y + b.Y);
    public static MapPoint operator -(MapPoint a, MapPoint b) => new(a.X - b.X, a.Y - b.Y);
    public static MapPoint operator *(MapPoint a, float s) => new(a.X * s, a.Y * s);

    public float Length => MathF.Sqrt(X * X + Y * Y);

    public float DistanceTo(MapPoint other) => (this - other).Length;

    public bool Equals(MapPoint other) => X.Equals(other.X) && Y.Equals(other.Y);

    public override bool Equals(object? obj) => obj is MapPoint p && Equals(p);

    public override int GetHashCode() => HashCode.Combine(X, Y);

    public override string ToString() => $"({X:F2},{Y:F2})";
}

/// <summary>矩形（float），Left/Top/Width/Height 语义与 Skia / WPF 一致。</summary>
public readonly struct MapRect : IEquatable<MapRect>
{
    public MapRect(float left, float top, float width, float height)
    {
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    public float Left { get; }
    public float Top { get; }
    public float Width { get; }
    public float Height { get; }

    public float Right => Left + Width;
    public float Bottom => Top + Height;
    public float CenterX => Left + Width / 2f;
    public float CenterY => Top + Height / 2f;

    public bool IsEmpty => Width <= 0f || Height <= 0f;

    public bool Contains(MapPoint p) => p.X >= Left && p.X <= Right && p.Y >= Top && p.Y <= Bottom;

    public bool Equals(MapRect other)
        => Left.Equals(other.Left) && Top.Equals(other.Top)
           && Width.Equals(other.Width) && Height.Equals(other.Height);

    public override bool Equals(object? obj) => obj is MapRect r && Equals(r);

    public override int GetHashCode() => HashCode.Combine(Left, Top, Width, Height);

    public override string ToString() => $"[{Left:F1},{Top:F1} {Width:F1}x{Height:F1}]";
}
