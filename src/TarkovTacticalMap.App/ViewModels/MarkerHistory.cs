using TarkovTacticalMap.Core.Models;

namespace TarkovTacticalMap.App.ViewModels;

/// <summary>一次可撤销的标记编辑类型。</summary>
public enum MarkerEditKind
{
    Add = 0,
    Update = 1,
    Delete = 2,
}

/// <summary>一条编辑历史：正向操作前的对象（Before）与操作后的权威对象（After）。</summary>
public sealed class MarkerEdit
{
    public MarkerEditKind Kind { get; init; }

    /// <summary>操作前的标记（Add 时为 null）。</summary>
    public Marker? Before { get; init; }

    /// <summary>操作后的标记（Delete 时为 null）。</summary>
    public Marker? After { get; init; }

    public string Label { get; init; } = string.Empty;

    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// 撤销 / 重做历史。只记录「本机用户发起的编辑」，远端玩家广播来的变更不进入历史；
/// 同一标记在短时间内连续修改（拖动滑杆、拖拽大小手柄）会合并成一条，避免历史被塞满。
/// </summary>
public sealed class MarkerHistory
{
    private const int MaxEntries = 200;
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(1200);

    private readonly List<MarkerEdit> _undo = new();
    private readonly List<MarkerEdit> _redo = new();

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public string? UndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;

    public string? RedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;

    public int UndoCount => _undo.Count;

    public int RedoCount => _redo.Count;

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    public void RecordAdd(Marker marker)
        => Push(new MarkerEdit { Kind = MarkerEditKind.Add, After = marker.Clone(), Label = "新增" + Describe(marker) });

    public void RecordDelete(Marker marker)
        => Push(new MarkerEdit { Kind = MarkerEditKind.Delete, Before = marker.Clone(), Label = "删除" + Describe(marker) });

    public void RecordUpdate(Marker before, Marker after)
    {
        if (_undo.Count > 0)
        {
            var last = _undo[^1];
            if (last.Kind == MarkerEditKind.Update &&
                last.After is not null &&
                string.Equals(last.After.Id, after.Id, StringComparison.Ordinal) &&
                DateTime.UtcNow - last.TimestampUtc < CoalesceWindow)
            {
                _undo[^1] = new MarkerEdit
                {
                    Kind = MarkerEditKind.Update,
                    Before = last.Before,
                    After = after.Clone(),
                    Label = last.Label,
                    TimestampUtc = DateTime.UtcNow,
                };
                _redo.Clear();
                return;
            }
        }

        Push(new MarkerEdit
        {
            Kind = MarkerEditKind.Update,
            Before = before.Clone(),
            After = after.Clone(),
            Label = "修改" + Describe(after),
        });
    }

    public MarkerEdit? PopUndo()
    {
        if (_undo.Count == 0) return null;
        var edit = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        return edit;
    }

    public MarkerEdit? PopRedo()
    {
        if (_redo.Count == 0) return null;
        var edit = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        return edit;
    }

    public void PushRedo(MarkerEdit edit) => _redo.Add(edit);

    public void PushUndo(MarkerEdit edit) => _undo.Add(edit);

    private void Push(MarkerEdit edit)
    {
        _redo.Clear();
        _undo.Add(edit);
        if (_undo.Count > MaxEntries) _undo.RemoveAt(0);
    }

    private static string Describe(Marker marker) => marker.Type switch
    {
        MarkerType.Point => "图标",
        MarkerType.Text => "文本",
        MarkerType.Arrow => "箭头",
        MarkerType.Line => "直线",
        MarkerType.Circle => "圆圈",
        MarkerType.Rectangle => "方框",
        MarkerType.Stroke => "笔画",
        _ => "标记",
    };
}
