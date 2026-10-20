using TarkovTacticalMap.Core.Models;

namespace TarkovTacticalMap.Core.State;

/// <summary>一次标记变更的处置结果。</summary>
public enum MutationOutcome
{
    /// <summary>已生效（应广播）。</summary>
    Applied = 0,

    /// <summary>幂等忽略（如重复 Id 的新增、删除不存在的 Id）。</summary>
    Ignored = 1,

    /// <summary>权限不足（客户端操作非自己创建的标记）。</summary>
    PermissionDenied = 2,

    /// <summary>请求内容不合法。</summary>
    Invalid = 3,

    /// <summary>要更新/删除的标记不存在。</summary>
    NotFound = 4,

    /// <summary>客户端模式：请求已发往房主，等待房主权威广播后本地状态才会变化。</summary>
    Sent = 5,
}

/// <summary>变更结果：是否生效 + 权威对象（广播用，已规范化）。</summary>
public readonly record struct MutationResult(MutationOutcome Outcome, Marker? Marker)
{
    public bool Applied => Outcome == MutationOutcome.Applied;

    public static MutationResult Ok(Marker m) => new(MutationOutcome.Applied, m);
    public static MutationResult Ignored() => new(MutationOutcome.Ignored, null);
    public static MutationResult Denied() => new(MutationOutcome.PermissionDenied, null);
    public static MutationResult Invalid() => new(MutationOutcome.Invalid, null);
    public static MutationResult Missing() => new(MutationOutcome.NotFound, null);
    public static MutationResult SentToHost() => new(MutationOutcome.Sent, null);
}

/// <summary>
/// 权威标记状态表（房主持有权威副本，客户端用它保存快照）。
/// 线程安全；房主串行调用天然无并发冲突，客户端仅做快照替换。
/// 所有进入状态表的标记都会被规范化，取出的对象都是副本，避免外部意外修改。
/// </summary>
public sealed class MarkerStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Marker> _byId = new(StringComparer.Ordinal);
    private readonly List<Marker> _ordered = new();

    /// <summary>当前标记数量。</summary>
    public int Count
    {
        get { lock (_gate) return _byId.Count; }
    }

    /// <summary>按 CreatedAt 排序的快照（副本）。</summary>
    public IReadOnlyList<Marker> Snapshot()
    {
        lock (_gate)
        {
            var list = new List<Marker>(_ordered.Count);
            foreach (var m in _ordered) list.Add(m.Clone());
            return list;
        }
    }

    /// <summary>按 CreatedAt 排序的只读视图（内部对象，仅供只读用途）。</summary>
    public IReadOnlyList<Marker> OrderedView
    {
        get { lock (_gate) return _ordered.ToArray(); }
    }

    public Marker? Get(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_gate) return _byId.TryGetValue(id, out var m) ? m.Clone() : null;
    }

    public bool Contains(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        lock (_gate) return _byId.ContainsKey(id);
    }

    /// <summary>新增（幂等：Id 已存在则忽略）。</summary>
    public MutationResult Add(Marker? marker)
    {
        var (status, normalized) = MarkerValidator.Validate(marker);
        if (status != ValidateStatus.Ok || normalized is null) return MutationResult.Invalid();

        lock (_gate)
        {
            if (_byId.ContainsKey(normalized.Id)) return MutationResult.Ignored();
            _byId[normalized.Id] = normalized;
            InsertSorted(_ordered, normalized);
            return MutationResult.Ok(normalized.Clone());
        }
    }

    /// <summary>
    /// 全量更新。校验权限：客户端只能修改自己 OwnerId 的标记，房主可操作全部。
    /// 幂等：标记不存在时返回 NotFound（房主回 ERROR 并忽略）。
    /// </summary>
    public MutationResult Update(string id, Marker? marker, string actorId, bool actorIsHost)
    {
        if (string.IsNullOrEmpty(id)) return MutationResult.Invalid();
        var (status, normalized) = MarkerValidator.Validate(marker);
        if (status != ValidateStatus.Ok || normalized is null) return MutationResult.Invalid();

        // Id 以请求里的 Id 为准（防止客户端篡改）
        normalized.Id = id;

        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var existing)) return MutationResult.Missing();
            if (!CanModify(existing, actorId, actorIsHost)) return MutationResult.Denied();

            // 创建者与创建时间不可被客户端改写
            normalized.OwnerId = existing.OwnerId;
            normalized.CreatedAt = existing.CreatedAt;

            _byId[id] = normalized;
            ReplaceSorted(_ordered, normalized);
            return MutationResult.Ok(normalized.Clone());
        }
    }

    /// <summary>删除（幂等：不存在则忽略）。</summary>
    public MutationResult Delete(string id, string actorId, bool actorIsHost)
    {
        if (string.IsNullOrEmpty(id)) return MutationResult.Invalid();

        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var existing)) return MutationResult.Ignored();
            if (!CanModify(existing, actorId, actorIsHost)) return MutationResult.Denied();

            _byId.Remove(id);
            _ordered.RemoveAll(m => string.Equals(m.Id, id, StringComparison.Ordinal));
            return new MutationResult(MutationOutcome.Applied, existing.Clone());
        }
    }

    /// <summary>用全量快照替换当前状态（客户端收到 WELCOME / 重连后调用）。</summary>
    public void ReplaceAll(IEnumerable<Marker>? markers)
    {
        lock (_gate)
        {
            _byId.Clear();
            _ordered.Clear();
            if (markers is null) return;
            foreach (var m in markers)
            {
                var (status, normalized) = MarkerValidator.Validate(m);
                if (status != ValidateStatus.Ok || normalized is null) continue;
                if (_byId.ContainsKey(normalized.Id)) continue;
                _byId[normalized.Id] = normalized;
                InsertSorted(_ordered, normalized);
            }
        }
    }

    /// <summary>清空全部标记。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _byId.Clear();
            _ordered.Clear();
        }
    }

    /// <summary>权限判定：房主可操作全部，客户端只能操作自己创建的标记。</summary>
    public static bool CanModify(Marker marker, string? actorId, bool actorIsHost)
    {
        if (actorIsHost) return true;
        if (marker is null) return false;
        return !string.IsNullOrEmpty(actorId) &&
               string.Equals(marker.OwnerId, actorId, StringComparison.Ordinal);
    }

    private static void InsertSorted(List<Marker> list, Marker marker)
    {
        var index = list.Count;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].CreatedAt > marker.CreatedAt)
            {
                index = i;
                break;
            }
        }
        list.Insert(index, marker);
    }

    private static void ReplaceSorted(List<Marker> list, Marker marker)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].Id, marker.Id, StringComparison.Ordinal))
            {
                list[i] = marker;
                return;
            }
        }
        InsertSorted(list, marker);
    }
}
