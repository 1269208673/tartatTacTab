using SkiaSharp;
using TarkovTacticalMap.Core.Geometry;
using TarkovTacticalMap.Core.Models;
using TarkovTacticalMap.Core.Utils;

namespace TarkovTacticalMap.App.Rendering;

/// <summary>
/// SkiaSharp 绘制逻辑。不依赖 WPF，可直接对 SKCanvas / SKBitmap 作画（便于单测与截图自检）。
///
/// 绘制顺序：底图 → 网格（一级） → 标记（按 CreatedAt） → 选中高亮与控制点 → 临时绘制层。
/// </summary>
public sealed class MapRenderer
{
    private static SKTypeface? _cachedTypeface;

    private static SKTypeface Typeface => _cachedTypeface ??= ResolveTypeface();

    private static SKTypeface ResolveTypeface()
    {
        foreach (var name in new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Segoe UI" })
        {
            var tf = SKTypeface.FromFamilyName(name);
            if (tf is not null) return tf;
        }
        return SKTypeface.Default;
    }

    /// <summary>绘制整帧。</summary>
    public void Draw(SKCanvas canvas, RenderScene scene)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(scene);

        canvas.Clear(scene.BackdropColor);
        var vp = scene.Viewport;

        if (scene.Background is not null && vp.IsValid)
        {
            var r = vp.ImageRect;
            using var imagePaint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
            canvas.DrawImage(scene.Background, new SKRect(r.Left, r.Top, r.Right, r.Bottom), imagePaint);

            using var borderPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f,
                Color = new SKColor(0x55FFFFFF),
            };
            canvas.DrawRect(new SKRect(r.Left, r.Top, r.Right, r.Bottom), borderPaint);
        }

        if (scene.Map is { } map && vp.IsValid && map.GridVisible)
        {
            DrawGrid(canvas, map, vp, scene.HoverPoint);
        }

        if (!vp.IsValid) return;

        // 标记（临时草稿覆盖同 Id 的正式标记，新草稿追加在最上层）
        var drawList = BuildDrawList(scene);

        foreach (var marker in drawList)
        {
            DrawMarker(canvas, marker, vp, scene.Icons);
        }

        // 选中高亮 + 控制点
        var selected = ResolveSelected(scene);
        if (selected is not null)
        {
            DrawSelection(canvas, selected, vp);
        }
    }

    private static List<Marker> BuildDrawList(RenderScene scene)
    {
        var list = new List<Marker>(scene.Markers.Count + 1);
        var draft = scene.Draft;
        var draftApplied = false;

        foreach (var marker in scene.Markers)
        {
            if (draft is not null && string.Equals(marker.Id, draft.Id, StringComparison.Ordinal))
            {
                list.Add(draft);
                draftApplied = true;
            }
            else
            {
                list.Add(marker);
            }
        }

        if (draft is not null && !draftApplied) list.Add(draft);
        return list;
    }

    private static Marker? ResolveSelected(RenderScene scene)
    {
        if (scene.Draft is not null &&
            string.Equals(scene.Draft.Id, scene.SelectedMarkerId, StringComparison.Ordinal))
        {
            return scene.Draft;
        }

        if (string.IsNullOrEmpty(scene.SelectedMarkerId)) return null;
        foreach (var marker in scene.Markers)
        {
            if (string.Equals(marker.Id, scene.SelectedMarkerId, StringComparison.Ordinal)) return marker;
        }
        return null;
    }

    /// <summary>标记在屏幕上的包围盒（用于选中框，也可用于命中调试）。</summary>
    public static MapRect GetScreenBounds(Marker m, ViewportState vp) => MarkerGeometry.GetScreenBounds(m, vp);

    private void DrawMarker(SKCanvas canvas, Marker m, ViewportState vp, IconLibrary icons)
    {
        var displayWidth = vp.DisplayWidth;
        if (displayWidth <= 0f) return;

        var color = new SKColor(ColorHex.ToArgb(m.Color));
        var anchor = vp.NormalizedToScreen(m.X, m.Y);
        var sizePx = MathF.Abs(m.Size) * displayWidth;

        switch (m.Type)
        {
            case MarkerType.Point:
            {
                var side = MathF.Max(sizePx, 6f);
                var dest = new SKRect(anchor.X - side / 2f, anchor.Y - side / 2f, anchor.X + side / 2f, anchor.Y + side / 2f);
                if (string.IsNullOrEmpty(m.IconId) || !icons.TryDraw(canvas, m.IconId, dest, color, m.Rotation))
                {
                    // IconId 为 null 或未知：回退为默认圆点
                    using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };
                    canvas.DrawCircle(anchor.X, anchor.Y, MathF.Max(sizePx, 3f), paint);
                }
                break;
            }

            case MarkerType.Text:
            {
                var fontPx = MathF.Max(sizePx, 6f);
                var text = string.IsNullOrEmpty(m.Text) ? "文本" : m.Text;
                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    Color = color,
                    Typeface = Typeface,
                    TextSize = fontPx,
                    TextAlign = SKTextAlign.Left,
                    SubpixelText = true,
                };

                var state = canvas.Save();
                try
                {
                    canvas.Translate(anchor.X, anchor.Y);
                    if (MathF.Abs(m.Rotation) > 0.01f) canvas.RotateDegrees(m.Rotation);
                    canvas.DrawText(text, 0f, fontPx * 0.85f, paint);
                }
                finally
                {
                    canvas.RestoreToCount(state);
                }
                break;
            }

            case MarkerType.Arrow:
            {
                var lengthPx = sizePx;
                if (lengthPx < 2f) break;

                // 关键：杆粗与箭头头部只由 Thickness 决定，拉长不会再整体放大
                var thickness = MathF.Max(MarkerGeometry.GetStrokeWidthPixels(m, vp), 1f);
                var dir = MarkerGeometry.Direction(m.Rotation);
                var tip = new MapPoint(anchor.X + dir.X * lengthPx, anchor.Y + dir.Y * lengthPx);

                var headLength = MathF.Min(MathF.Max(thickness * 4.5f, 9f), MathF.Max(lengthPx * 0.45f, 9f));
                var headWidth = MathF.Max(headLength * 0.85f, thickness * 2.5f);
                var shaftEnd = new MapPoint(tip.X - dir.X * headLength * 0.9f, tip.Y - dir.Y * headLength * 0.9f);
                var perp = new MapPoint(-dir.Y, dir.X);

                using (var shaft = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = thickness,
                    StrokeCap = SKStrokeCap.Round,
                    Color = color,
                })
                {
                    canvas.DrawLine(anchor.X, anchor.Y, shaftEnd.X, shaftEnd.Y, shaft);
                }

                using var head = new SKPath();
                head.MoveTo(tip.X, tip.Y);
                head.LineTo(tip.X - dir.X * headLength + perp.X * headWidth / 2f, tip.Y - dir.Y * headLength + perp.Y * headWidth / 2f);
                head.LineTo(tip.X - dir.X * headLength - perp.X * headWidth / 2f, tip.Y - dir.Y * headLength - perp.Y * headWidth / 2f);
                head.Close();

                using var headPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };
                canvas.DrawPath(head, headPaint);
                break;
            }

            case MarkerType.Line:
            {
                var end = vp.NormalizedToScreen(m.EndX, m.EndY);
                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = MarkerGeometry.GetStrokeWidthPixels(m, vp),
                    StrokeCap = SKStrokeCap.Round,
                    Color = color,
                };
                canvas.DrawLine(anchor.X, anchor.Y, end.X, end.Y, paint);
                break;
            }

            case MarkerType.Circle:
            {
                var radiusPx = MathF.Max(sizePx, 2f);
                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = MarkerGeometry.GetStrokeWidthPixels(m, vp),
                    Color = color,
                };
                canvas.DrawCircle(anchor.X, anchor.Y, radiusPx, paint);
                break;
            }

            case MarkerType.Rectangle:
            {
                var end = vp.NormalizedToScreen(m.EndX, m.EndY);
                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = MarkerGeometry.GetStrokeWidthPixels(m, vp),
                    Color = color,
                };
                var left = MathF.Min(anchor.X, end.X);
                var top = MathF.Min(anchor.Y, end.Y);
                var right = MathF.Max(anchor.X, end.X);
                var bottom = MathF.Max(anchor.Y, end.Y);
                canvas.DrawRect(new SKRect(left, top, right, bottom), paint);
                break;
            }

            case MarkerType.Stroke:
            {
                var points = MarkerGeometry.GetStrokeScreenPoints(m, vp);
                if (points.Length < 2) break;

                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = MathF.Max(MarkerGeometry.GetStrokeWidthPixels(m, vp), 1f),
                    StrokeCap = SKStrokeCap.Round,
                    StrokeJoin = SKStrokeJoin.Round,
                    Color = color,
                };
                using var path = new SKPath();
                path.MoveTo(points[0].X, points[0].Y);
                for (var i = 1; i < points.Length; i++) path.LineTo(points[i].X, points[i].Y);
                canvas.DrawPath(path, paint);
                break;
            }

            default:
            {
                // 未知 MarkerType：安全回退为默认圆点（不崩溃）
                using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };
                canvas.DrawCircle(anchor.X, anchor.Y, MathF.Max(sizePx, 3f), paint);
                break;
            }
        }
    }

    private void DrawSelection(SKCanvas canvas, Marker m, ViewportState vp)
    {
        var bounds = MarkerGeometry.GetScreenBounds(m, vp);
        var accent = new SKColor(0xFF4FC3F7);

        using (var dash = SKPathEffect.CreateDash(new[] { 5f, 4f }, 0f))
        using (var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.4f,
            Color = accent,
            PathEffect = dash,
        })
        {
            canvas.DrawRect(new SKRect(bounds.Left - 3f, bounds.Top - 3f, bounds.Right + 3f, bounds.Bottom + 3f), paint);
        }

        foreach (var handle in MarkerGeometry.GetHandles(m, vp))
        {
            const float side = MarkerGeometry.HandleRadius;
            var rect = new SKRect(handle.ScreenX - side, handle.ScreenY - side, handle.ScreenX + side, handle.ScreenY + side);
            using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = SKColors.White };
            using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = accent };
            canvas.DrawRect(rect, fill);
            canvas.DrawRect(rect, stroke);
        }
    }

    // ── 网格（只有一级：列 A~J、行 0~9）────────────────────────────────

    private void DrawGrid(SKCanvas canvas, MapInfo map, ViewportState vp, MapPoint? hoverPoint)
    {
        var imageRect = vp.ImageRect;
        if (imageRect.IsEmpty) return;

        var (cols, rows) = GridLayout.GetSize(map);
        var cellWidth = imageRect.Width / cols;
        var cellHeight = imageRect.Height / rows;

        // 鼠标所在格子高亮
        if (hoverPoint is { } hp && hp.X is >= 0f and <= 1f && hp.Y is >= 0f and <= 1f)
        {
            var cell = GridLayout.GetCellRectAt(cols, rows, hp.X, hp.Y);
            using var hoverPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(0x22FFFFFF) };
            canvas.DrawRect(ToScreen(vp, cell), hoverPaint);
        }

        using var linePaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            Color = new SKColor(0x66FFFFFF),
        };

        for (var col = 0; col <= cols; col++)
        {
            var x = imageRect.Left + col * cellWidth;
            canvas.DrawLine(x, imageRect.Top, x, imageRect.Bottom, linePaint);
        }

        for (var row = 0; row <= rows; row++)
        {
            var y = imageRect.Top + row * cellHeight;
            canvas.DrawLine(imageRect.Left, y, imageRect.Right, y, linePaint);
        }

        DrawGridLabels(canvas, imageRect, cols, rows, cellWidth, cellHeight);
    }

    private static SKRect ToScreen(ViewportState vp, MapRect rect)
    {
        var a = vp.NormalizedToScreen(rect.Left, rect.Top);
        var b = vp.NormalizedToScreen(rect.Right, rect.Bottom);
        return new SKRect(a.X, a.Y, b.X, b.Y);
    }

    /// <summary>边缘标签：列 A~J（顶部）、行 0~9（左侧）；格子太小时按间隔抽样，避免糊成一片。</summary>
    private static void DrawGridLabels(SKCanvas canvas, MapRect imageRect, int cols, int rows, float cellWidth, float cellHeight)
    {
        if (MathF.Min(cellWidth, cellHeight) < 10f) return;

        var fontPx = Math.Clamp(MathF.Min(cellWidth, cellHeight) * 0.42f, 8f, 20f);
        var colStep = Math.Max(1, (int)MathF.Ceiling(24f / MathF.Max(cellWidth, 1f)));
        var rowStep = Math.Max(1, (int)MathF.Ceiling(18f / MathF.Max(cellHeight, 1f)));

        using var textPaint = new SKPaint
        {
            IsAntialias = true,
            Typeface = Typeface,
            TextSize = fontPx,
            TextAlign = SKTextAlign.Center,
            Color = new SKColor(0xE6FFFFFF),
        };
        using var shadowPaint = new SKPaint
        {
            IsAntialias = true,
            Typeface = Typeface,
            TextSize = fontPx,
            TextAlign = SKTextAlign.Center,
            Color = new SKColor(0x99000000),
        };

        for (var col = 0; col < cols; col += colStep)
        {
            var label = GridReference.ColumnLabel(col);
            var cx = imageRect.Left + (col + 0.5f) * cellWidth;
            var cy = imageRect.Top + fontPx * 1.15f;
            canvas.DrawText(label, cx + 1f, cy + 1f, shadowPaint);
            canvas.DrawText(label, cx, cy, textPaint);
        }

        textPaint.TextAlign = SKTextAlign.Left;
        shadowPaint.TextAlign = SKTextAlign.Left;
        for (var row = 0; row < rows; row += rowStep)
        {
            var label = GridReference.RowLabel(row); // 行标签从 0 开始：0,1,2…
            var x = imageRect.Left + 3f;
            var y = imageRect.Top + (row + 0.5f) * cellHeight + fontPx * 0.35f;
            canvas.DrawText(label, x + 1f, y + 1f, shadowPaint);
            canvas.DrawText(label, x, y, textPaint);
        }
    }
}
