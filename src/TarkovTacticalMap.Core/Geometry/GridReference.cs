namespace TarkovTacticalMap.Core.Geometry;

/// <summary>
/// 网格引用（如 "C3"）。列从左到右 A,B,C…（最多 A~J），行从上到下 0,1,2…（最多 0~9）。
/// 网格只做视觉参考与辅助定位，不参与标记存储。
/// </summary>
public static class GridReference
{
    /// <summary>计算鼠标归一化坐标所在的网格单元（已 clamp 到合法范围）。</summary>
    public static (int Col, int Row) GetCell(float nx, float ny, int cols, int rows)
    {
        if (cols <= 0) cols = 1;
        if (rows <= 0) rows = 1;
        var col = (int)MathF.Floor(nx * cols);
        var row = (int)MathF.Floor(ny * rows);
        return (Math.Clamp(col, 0, cols - 1), Math.Clamp(row, 0, rows - 1));
    }

    /// <summary>计算网格标签，如 "C3"。坐标不在图内（0~1 之外）时返回 null。</summary>
    public static string? GetLabel(float nx, float ny, int cols, int rows)
    {
        if (nx < 0f || nx > 1f || ny < 0f || ny > 1f) return null;
        var (col, row) = GetCell(nx, ny, cols, rows);
        return ColumnLabel(col) + RowLabel(row);
    }

    /// <summary>列序号 → 列标签（0 → A，9 → J，25 → Z，26 → AA）。</summary>
    public static string ColumnLabel(int col)
    {
        if (col < 0) return "?";
        var sb = new System.Text.StringBuilder();
        var n = col;
        do
        {
            sb.Insert(0, (char)('A' + n % 26));
            n = n / 26 - 1;
        }
        while (n >= 0);
        return sb.ToString();
    }

    /// <summary>行序号 → 行标签（从 0 开始：0,1,2…）。</summary>
    public static string RowLabel(int row) => row.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
