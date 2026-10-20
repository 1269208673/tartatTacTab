using SkiaSharp;
using TarkovTacticalMap.Core.Geometry;
using TarkovTacticalMap.Core.Models;
using TarkovTacticalMap.Core.Utils;

namespace TarkovTacticalMap.App.Rendering;

/// <summary>
/// SkiaSharp 绘制逻辑。不依赖 WPF，可直接对 SKCanvas / SKBitmap 作画（便于单测与截图自检）。
///
/// 绘制顺序：底图 → 网格（含嵌套细网格） → 标记（按 CreatedAt） → 选中高亮与控制点 → 临时绘制层。
/// </summary>
public sealed class MapRenderer
{
    private static SKTypeface? _cachedTypeface;

    private static SKTypeface Typeface => _cachedTypeface ??= ResolveTypeface();

    /// <summary>网格最多画多少条线（防止极端缩放下刷屏）。</summary>
    private const int MaxGridLinesPerLevel = 4000;

    /// <summary>二级及以后每帧最多画多少个标签。</summary>
    private const int MaxSubLabels = 300;

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

    // ── 网格（嵌套）──────────────────────────────────────────────────────

    private void DrawGrid(SKCanvas canvas, MapInfo map, ViewportState vp, MapPoint? hoverPoint)
    {
        var specs = NestedGrid.BuildSpecs(map);
        var depth = NestedGrid.GetVisibleDepth(specs, vp);
        var r = vp.ImageRect;
        if (r.IsEmpty) return;

        // 鼠标所在的最深层级格子高亮
        if (hoverPoint is { } hp && hp.X is >= 0f and <= 1f && hp.Y is >= 0f and <= 1f)
        {
            var cell = NestedGrid.GetCellRectAt(specs, depth, hp.X, hp.Y);
            using var hoverPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(0x22FFFFFF) };
            canvas.DrawRect(ToScreen(vp, cell), hoverPaint);
        }

        for (var level = 1; level <= depth; level++)
        {
            byte alpha = level switch { 1 => 0x66, 2 => 0x4C, 3 => 0x38, _ => 0x2A };
            DrawLevelLines(canvas, vp, specs, level, alpha);
        }

        DrawGridLabels(canvas, vp, map, specs, depth);
    }

    private static SKRect ToScreen(ViewportState vp, MapRect rect)
    {
        var a = vp.NormalizedToScreen(rect.Left, rect.Top);
        var b = vp.NormalizedToScreen(rect.Right, rect.Bottom);
        return new SKRect(a.X, a.Y, b.X, b.Y);
    }

    private static void DrawLevelLines(SKCanvas canvas, ViewportState vp, IReadOnlyList<GridLevelSpec> specs, int level, byte alpha)
    {
        var (cellW, cellH) = NestedGrid.GetCellSize(specs, level);
        if (cellW <= 0f || cellH <= 0f) return;

        var imageRect = vp.ImageRect;
        var totalCols = (int)MathF.Round(1f / cellW);
        var totalRows = (int)MathF.Round(1f / cellH);
        if (totalCols <= 0 || totalRows <= 0) return;

        // 只画可视区域内的线
        var topLeft = vp.ScreenToNormalized(imageRect.Left, imageRect.Top);
        var bottomRight = vp.ScreenToNormalized(imageRect.Right, imageRect.Bottom);
        var visibleLeft = Math.Clamp(MathF.Min(topLeft.X, bottomRight.X), 0f, 1f);
        var visibleRight = Math.Clamp(MathF.Max(topLeft.X, bottomRight.X), 0f, 1f);
        var visibleTop = Math.Clamp(MathF.Min(topLeft.Y, bottomRight.Y), 0f, 1f);
        var visibleBottom = Math.Clamp(MathF.Max(topLeft.Y, bottomRight.Y), 0f, 1f);

        var skipCols = level > 1 ? Math.Max(1, specs[level - 2].Cols) : 0;
        var skipRows = level > 1 ? Math.Max(1, specs[level - 2].Rows) : 0;

        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = level == 1 ? 1f : 0.8f,
            Color = new SKColor(alpha, 0xFF, 0xFF, 0xFF),
        };

        var startCol = Math.Max(0, (int)MathF.Floor(visibleLeft * totalCols));
        var endCol = Math.Min(totalCols, (int)MathF.Ceiling(visibleRight * totalCols));
        var drawn = 0;
        for (var col = startCol; col <= endCol && drawn < MaxGridLinesPerLevel; col++)
        {
            if (skipCols > 0 && col % skipCols == 0) continue; // 与上一级重合，跳过
            var x = imageRect.Left + col * cellW * imageRect.Width;
            canvas.DrawLine(x, imageRect.Top, x, imageRect.Bottom, paint);
            drawn++;
        }

        var startRow = Math.Max(0, (int)MathF.Floor(visibleTop * totalRows));
        var endRow = Math.Min(totalRows, (int)MathF.Ceiling(visibleBottom * totalRows));
        for (var row = startRow; row <= endRow && drawn < MaxGridLinesPerLevel; row++)
        {
            if (skipRows > 0 && row % skipRows == 0) continue;
            var y = imageRect.Top + row * cellH * imageRect.Height;
            canvas.DrawLine(imageRect.Left, y, imageRect.Right, y, paint);
            drawn++;
        }
    }

    private void DrawGridLabels(SKCanvas canvas, ViewportState vp, MapInfo map, IReadOnlyList<GridLevelSpec> specs, int depth)
    {
        var imageRect = vp.ImageRect;

        // 一级网格：边缘列字母 / 行号（格太小时按间隔抽样，避免糊成一片）
        var baseSpec = specs[0];
        var baseCellW = imageRect.Width / baseSpec.Cols;
        var baseCellH = imageRect.Height / baseSpec.Rows;
        if (MathF.Min(baseCellW, baseCellH) >= 10f)
        {
            var fontPx = Math.Clamp(MathF.Min(baseCellW, baseCellH) * 0.42f, 8f, 18f);
            var colStep = Math.Max(1, (int)MathF.Ceiling(24f / MathF.Max(baseCellW, 1f)));
            var rowStep = Math.Max(1, (int)MathF.Ceiling(18f / MathF.Max(baseCellH, 1f)));

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

            for (var col = 0; col < baseSpec.Cols; col += colStep)
            {
                var label = GridReference.ColumnLabel(col);
                var cx = imageRect.Left + (col + 0.5f) * baseCellW;
                var cy = imageRect.Top + fontPx * 1.15f;
                canvas.DrawText(label, cx + 1f, cy + 1f, shadowPaint);
                canvas.DrawText(label, cx, cy, textPaint);
            }

            textPaint.TextAlign = SKTextAlign.Left;
            shadowPaint.TextAlign = SKTextAlign.Left;
            for (var row = 0; row < baseSpec.Rows; row += rowStep)
            {
                var label = GridReference.RowLabel(row); // 行标签从 0 开始：0,1,2…
                var x = imageRect.Left + 3f;
                var y = imageRect.Top + (row + 0.5f) * baseCellH + fontPx * 0.35f;
                canvas.DrawText(label, x + 1f, y + 1f, shadowPaint);
                canvas.DrawText(label, x, y, textPaint);
            }
        }

        // 二级及以后：只在「最深层级」且格子足够大时给每个可见格打标签
        if (depth < 2) return;

        var (subW, subH) = NestedGrid.GetCellSize(specs, depth);
        var subCellW = subW * imageRect.Width;
        var subCellH = subH * imageRect.Height;
        if (MathF.Min(subCellW, subCellH) < 42f) return;

        var subFont = Math.Clamp(MathF.Min(subCellW, subCellH) * 0.3f, 8f, 12f);
        var totalCols = Math.Max(1, (int)MathF.Round(1f / subW));
        var totalRows = Math.Max(1, (int)MathF.Round(1f / subH));

        var topLeft = vp.ScreenToNormalized(imageRect.Left, imageRect.Top);
        var bottomRight = vp.ScreenToNormalized(imageRect.Right, imageRect.Bottom);
        var startCol = Math.Max(0, (int)MathF.Floor(Math.Clamp(MathF.Min(topLeft.X, bottomRight.X), 0f, 1f) * totalCols));
        var endCol = Math.Min(totalCols - 1, (int)MathF.Ceiling(Math.Clamp(MathF.Max(topLeft.X, bottomRight.X), 0f, 1f) * totalCols));
        var startRow = Math.Max(0, (int)MathF.Floor(Math.Clamp(MathF.Min(topLeft.Y, bottomRight.Y), 0f, 1f) * totalRows));
        var endRow = Math.Min(totalRows - 1, (int)MathF.Ceiling(Math.Clamp(MathF.Max(topLeft.Y, bottomRight.Y), 0f, 1f) * totalRows));

        using var subPaint = new SKPaint
        {
            IsAntialias = true,
            Typeface = Typeface,
            TextSize = subFont,
            TextAlign = SKTextAlign.Left,
            Color = new SKColor(0xCCFFFFFF),
        };

        var labels = 0;
        for (var col = startCol; col <= endCol && labels < MaxSubLabels; col++)
        {
            for (var row = startRow; row <= endRow && labels < MaxSubLabels; row++)
            {
                var spec = specs[depth - 1];
                var subCol = col % spec.Cols;
                var subRow = row % spec.Rows;
                var label = NestedGrid.GetCellLabel(subCol, subRow);
                var x = imageRect.Left + col * subCellW + 2f;
                var y = imageRect.Top + row * subCellH + subFont + 1f;
                canvas.DrawText(label, x, y, subPaint);
                labels++;
            }
        }
    }
}
