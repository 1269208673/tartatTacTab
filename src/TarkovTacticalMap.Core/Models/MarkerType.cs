namespace TarkovTacticalMap.Core.Models;

/// <summary>
/// 标记类型。数值写入网络协议，新增类型必须追加在末尾，不得复用旧编号。
/// </summary>
[Serializable]
public enum MarkerType
{
    /// <summary>语义图标（配合 IconId）或默认圆点。</summary>
    Point = 0,

    /// <summary>文本，(X, Y) 为文本左上角。</summary>
    Text = 1,

    /// <summary>箭头，(X, Y) 为箭尾，Rotation 为方向，Size 为长度。</summary>
    Arrow = 2,

    /// <summary>直线段，(X, Y) 为起点，(EndX, EndY) 为终点。</summary>
    Line = 3,

    /// <summary>圆，(X, Y) 为圆心，Size 为半径。</summary>
    Circle = 4,

    /// <summary>方框，(X, Y) 与 (EndX, EndY) 为对角。</summary>
    Rectangle = 5,
}
