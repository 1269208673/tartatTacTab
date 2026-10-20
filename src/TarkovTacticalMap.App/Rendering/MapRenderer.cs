using SkiaSharp;
using TarkovTacticalMap.Core.Geometry;
using TarkovTacticalMap.Core.Models;
using TarkovTacticalMap.Core.Utils;

namespace TarkovTacticalMap.App.Rendering;

/// <summary>
/// SkiaSharp 绘制逻辑。不依赖 WPF，可直接对 SKCanvas / SKBitmap 作画（便于单测与截图自检）。
///
/// 绘制顺序：底图 → 网格 → 标记（按 CreatedAt） → 选中高亮与控制点 → 临时绘制层。
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
            DrawGrid(canvas, map, vp, scene.HoverGridLabel);
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
    public static MapRect GetScreenBounds(Marker m, ViewportState vp)
    {
        var displayWidth = vp.DisplayWidth;
        var sizePx = MathF.Abs(m.Size) * displayWidth;
        var anchor = vp.NormalizedToScreen(m.X, m.Y);

        switch (m.Type)
        {
            case MarkerType.Point:
            {
                var half = MathF.Max(sizePx / 2f, 5f);
                return new MapRect(anchor.X - half, anchor.Y - half, half * 2, half * 2);
            }
            case MarkerType.Text:
            {
                var fontPx = MathF.Max(sizePx, 8f);
                var text = m.Text ?? string.Empty;
                var w = MathF.Max(fontPx * 0.62f * Math.Max(text.Length, 1), fontPx);
                var h = fontPx * 1.35f;
                return new MapRect(anchor.X, anchor.Y, w, h);
            }
            case MarkerType.Arrow:
            {
                var tip = MarkerGeometry.GetArrowTipScreen(m, vp);
                var thickness = MathF.Max(2f, sizePx * 0.16f);
                var left = MathF.Min(anchor.X, tip.X) - thickness;
                var top = MathF.Min(anchor.Y, tip.Y) - thickness;
                var right = MathF.Max(anchor.X, tip.X) + thickness;
                var bottom = MathF.Max(anchor.Y, tip.Y) + thickness;
                return new MapRect(left, top, right - left, bottom - top);
            }
            case MarkerType.Line:
            {
                var end = vp.NormalizedToScreen(m.EndX, m.EndY);
                var pad = MathF.Max(sizePx, 3f);
                var left = MathF.Min(anchor.X, end.X) - pad;
                var top = MathF.Min(anchor.Y, end.Y) - pad;
                var right = MathF.Max(anchor.X, end.X) + pad;
                var bottom = MathF.Max(anchor.Y, end.Y) + pad;
                return new MapRect(left, top, right - left, bottom - top);
            }
            case MarkerType.Circle:
            {
                var radiusPx = MathF.Max(sizePx, 2f);
                return new MapRect(anchor.X - radiusPx, anchor.Y - radiusPx, radiusPx * 2, radiusPx * 2);
            }
            case MarkerType.Rectangle:
            {
                var end = vp.NormalizedToScreen(m.EndX, m.EndY);
                var left = MathF.Min(anchor.X, end.X);
                var top = MathF.Min(anchor.Y, end.Y);
                var right = MathF.Max(anchor.X, end.X);
                var bottom = MathF.Max(anchor.Y, end.Y);
                return new MapRect(left, top, MathF.Max(right - left, 1f), MathF.Max(bottom - top, 1f));
            }
            default:
            {
                var half = MathF.Max(sizePx / 2f, 5f);
                return new MapRect(anchor.X - half, anchor.Y - half, half * 2, half * 2);
            }
        }
    }

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
                var dir = MarkerGeometry.Direction(m.Rotation);
                var tip = new MapPoint(anchor.X + dir.X * lengthPx, anchor.Y + dir.Y * lengthPx);                var thickness = MathF.Max(2f, lengthPx * 0.16f);
                var headLength = MathF.Min(lengthPx * 0.5f, MathF.Max(9f, thickness * 3.2f));
                var headWidth = thickness * 2.4f;
                var shaftEnd = new MapPoint(tip.X - dir.X * headLength * 0.85f, tip.Y - dir.Y * headLength * 0.85f);
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
                    StrokeWidth = MathF.Max(sizePx, 1f),
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
                    StrokeWidth = MathF.Max(1f, MathF.Min(2f, radiusPx * 0.1f)),
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
                    StrokeWidth = MathF.Max(sizePx, 1f),
                    Color = color,
                };
                var left = MathF.Min(anchor.X, end.X);
                var top = MathF.Min(anchor.Y, end.Y);
                var right = MathF.Max(anchor.X, end.X);
                var bottom = MathF.Max(anchor.Y, end.Y);
                canvas.DrawRect(new SKRect(left, top, right, bottom), paint);
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
        var bounds = GetScreenBounds(m, vp);
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

    private void DrawGrid(SKCanvas canvas, MapInfo map, ViewportState vp, string? hoverLabel)
    {
        var cols = Math.Clamp(map.GridCols, 1, MapInfo.MaxGrid);
        var rows = Math.Clamp(map.GridRows, 1, MapInfo.MaxGrid);
        var r = vp.ImageRect;
        if (r.IsEmpty) return;

        var cellW = r.Width / cols;
        var cellH = r.Height / rows;

        // 鼠标所在网格的高亮
        if (!string.IsNullOrEmpty(hoverLabel))
        {
            using var hoverPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(0x22FFFFFF) };
            for (var col = 0; col < cols; col++)
            {
                for (var row = 0; row < rows; row++)
                {
                    var label = GridReference.ColumnLabel(col) + (row + 1);
                    if (!string.Equals(label, hoverLabel, StringComparison.Ordinal)) continue;
                    var rect = new SKRect(
                        r.Left + col * cellW, r.Top + row * cellH,
                        r.Left + (col + 1) * cellW, r.Top + (row + 1) * cellH);
                    canvas.DrawRect(rect, hoverPaint);
                }
            }
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
            var x = r.Left + col * cellW;
            canvas.DrawLine(x, r.Top, x, r.Bottom, linePaint);
        }
        for (var row = 0; row <= rows; row++)
        {
            var y = r.Top + row * cellH;
            canvas.DrawLine(r.Left, y, r.Right, y, linePaint);
        }

        // 边缘标签 A/B/C… 与 1/2/3…
        var fontPx = MathF.Min(MathF.Max(MathF.Min(cellW, cellH) * 0.42f, 9f), 20f);
        if (fontPx < 7f) return;

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

        for (var col = 0; col < cols; col++)
        {
            var label = GridReference.ColumnLabel(col);
            var cx = r.Left + (col + 0.5f) * cellW;
            var cy = r.Top + fontPx * 1.25f - fontPx * 0.15f;
            canvas.DrawText(label, cx + 1f, cy + 1f, shadowPaint);
            canvas.DrawText(label, cx, cy, textPaint);
        }

        textPaint.TextAlign = SKTextAlign.Left;
        shadowPaint.TextAlign = SKTextAlign.Left;
        for (var row = 0; row < rows; row++)
        {
            var label = (row + 1).ToString();
            var x = r.Left + 3f;
            var y = r.Top + (row + 0.5f) * cellH + fontPx * 0.35f;
            canvas.DrawText(label, x + 1f, y + 1f, shadowPaint);
            canvas.DrawText(label, x, y, textPaint);
        }
    }
}
