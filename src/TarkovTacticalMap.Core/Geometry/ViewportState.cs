namespace TarkovTacticalMap.Core.Geometry;

/// <summary>
/// 画布视图状态：缩放 / 平移 / 画布尺寸 / 图片像素尺寸。
/// 负责「归一化坐标 ↔ 屏幕坐标」的换算，不依赖任何 UI 类型，可单独单测。
///
/// 换算规则：
///   FitScale      = min(CanvasWidth / ImageWidth, CanvasHeight / ImageHeight)
///   ImageRect     = 居中((ImageWidth * FitScale * Zoom, ImageHeight * FitScale * Zoom)) + Pan
///   ScreenX       = ImageRect.Left + X * ImageRect.Width
///   ScreenY       = ImageRect.Top  + Y * ImageRect.Height
/// </summary>
public sealed class ViewportState
{
    public const float MinZoom = 0.2f;

    /// <summary>最大缩放：放得足够大才能看到更深层的嵌套网格。</summary>
    public const float MaxZoom = 40f;

    /// <summary>画布（可视区）宽度，像素。</summary>
    public float CanvasWidth { get; set; }

    /// <summary>画布（可视区）高度，像素。</summary>
    public float CanvasHeight { get; set; }

    /// <summary>原图像素宽。</summary>
    public int ImageWidth { get; set; }

    /// <summary>原图像素高。</summary>
    public int ImageHeight { get; set; }

    /// <summary>相对「适应窗口」的缩放倍数，0.2 ~ 10。</summary>
    public float Zoom { get; private set; } = 1f;

    /// <summary>在居中基础上的额外平移（屏幕像素）。</summary>
    public float PanX { get; private set; }

    public float PanY { get; private set; }

    /// <summary>是否已具备可渲染的尺寸信息。</summary>
    public bool IsValid => ImageWidth > 0 && ImageHeight > 0 && CanvasWidth > 0 && CanvasHeight > 0;

    /// <summary>「适应窗口」比例（Zoom = 1 时的显示比例）。</summary>
    public float FitScale
    {
        get
        {
            if (!IsValid) return 1f;
            return MathF.Min(CanvasWidth / ImageWidth, CanvasHeight / ImageHeight);
        }
    }

    /// <summary>图片在画布上的显示矩形。</summary>
    public MapRect ImageRect
    {
        get
        {
            var dw = ImageWidth * FitScale * Zoom;
            var dh = ImageHeight * FitScale * Zoom;
            return new MapRect((CanvasWidth - dw) / 2f + PanX, (CanvasHeight - dh) / 2f + PanY, dw, dh);
        }
    }

    /// <summary>图片当前显示宽度（像素），Size 归一化值乘它得到像素尺寸。</summary>
    public float DisplayWidth => ImageRect.Width;

    /// <summary>图片当前显示高度（像素）。</summary>
    public float DisplayHeight => ImageRect.Height;

    public void ResetView()
    {
        Zoom = 1f;
        PanX = 0f;
        PanY = 0f;
    }

    public void SetCanvas(float width, float height)
    {
        CanvasWidth = width;
        CanvasHeight = height;
    }

    public void SetImage(int width, int height)
    {
        ImageWidth = Math.Max(0, width);
        ImageHeight = Math.Max(0, height);
    }

    /// <summary>屏幕坐标 → 归一化坐标（可能超出 0~1，由调用方决定是否 clamp）。</summary>
    public MapPoint ScreenToNormalized(float screenX, float screenY)
    {
        var r = ImageRect;
        if (r.IsEmpty) return new MapPoint(0f, 0f);
        return new MapPoint((screenX - r.Left) / r.Width, (screenY - r.Top) / r.Height);
    }

    /// <summary>归一化坐标 → 屏幕坐标。</summary>
    public MapPoint NormalizedToScreen(float nx, float ny)
    {
        var r = ImageRect;
        return new MapPoint(r.Left + nx * r.Width, r.Top + ny * r.Height);
    }

    /// <summary>以屏幕点为中心缩放（滚轮缩放），保证该点下方的地图内容不动。</summary>
    public void ZoomAt(float screenX, float screenY, float newZoom)
    {
        newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        if (MathF.Abs(newZoom - Zoom) < 1e-6f) return;

        var anchor = ScreenToNormalized(screenX, screenY);
        var before = ImageRect;
        if (before.IsEmpty)
        {
            Zoom = newZoom;
            return;
        }

        Zoom = newZoom;
        var dw = ImageWidth * FitScale * Zoom;
        var dh = ImageHeight * FitScale * Zoom;

        // 让 anchor 依旧落在 (screenX, screenY) 上
        PanX = screenX - anchor.X * dw - (CanvasWidth - dw) / 2f;
        PanY = screenY - anchor.Y * dh - (CanvasHeight - dh) / 2f;
    }

    /// <summary>按倍数缩放（键盘 +/- 等），以画布中心为锚点。</summary>
    public void ZoomBy(float factor) => ZoomAt(CanvasWidth / 2f, CanvasHeight / 2f, Zoom * factor);

    /// <summary>平移（中键拖拽 / 空格+左键）。</summary>
    public void PanBy(float dx, float dy)
    {
        PanX += dx;
        PanY += dy;
    }

    /// <summary>把平移限制到合理范围，避免图片被拖出视野。</summary>
    public void ClampPan()
    {
        var r = ImageRect;
        if (r.IsEmpty) return;
        var marginX = CanvasWidth * 0.5f;
        var marginY = CanvasHeight * 0.5f;
        var minPanX = -r.Width / 2f - CanvasWidth / 2f - marginX;
        var maxPanX = r.Width / 2f + CanvasWidth / 2f + marginX;
        var minPanY = -r.Height / 2f - CanvasHeight / 2f - marginY;
        var maxPanY = r.Height / 2f + CanvasHeight / 2f + marginY;
        PanX = Math.Clamp(PanX, minPanX, maxPanX);
        PanY = Math.Clamp(PanY, minPanY, maxPanY);
    }

    public ViewportState Clone()
    {
        var c = new ViewportState
        {
            CanvasWidth = CanvasWidth,
            CanvasHeight = CanvasHeight,
            ImageWidth = ImageWidth,
            ImageHeight = ImageHeight,
            Zoom = Zoom,
            PanX = PanX,
            PanY = PanY,
        };
        return c;
    }
}
