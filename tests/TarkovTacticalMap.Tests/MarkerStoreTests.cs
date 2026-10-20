using TarkovTacticalMap.Core.Geometry;
using TarkovTacticalMap.Core.Models;
using TarkovTacticalMap.Core.State;
using Xunit;

namespace TarkovTacticalMap.Tests;

/// <summary>标记状态表、校验与权限测试（对应文档 5.3）。</summary>
public class MarkerStoreTests
{
    private static Marker Marker(MarkerType type = MarkerType.Point, string id = "m1", string owner = "p1", long createdAt = 1000)
        => new()
        {
            Id = id,
            Type = type,
            X = 0.5f,
            Y = 0.5f,
            Size = 0.02f,
            Color = "#FFE066",
            OwnerId = owner,
            CreatedAt = createdAt,
        };

    [Fact]
    public void Add_IsIdempotentForSameId()
    {
        var store = new MarkerStore();
        Assert.Equal(MutationOutcome.Applied, store.Add(Marker()).Outcome);
        Assert.Equal(MutationOutcome.Ignored, store.Add(Marker()).Outcome);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Add_RejectsEmptyId()
    {
        var store = new MarkerStore();
        var marker = Marker();
        marker.Id = "   ";
        Assert.Equal(MutationOutcome.Invalid, store.Add(marker).Outcome);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Delete_IsIdempotent()
    {
        var store = new MarkerStore();
        store.Add(Marker());
        Assert.Equal(MutationOutcome.Applied, store.Delete("m1", "p1", false).Outcome);
        Assert.Equal(MutationOutcome.Ignored, store.Delete("m1", "p1", false).Outcome);
    }

    [Fact]
    public void Client_CanOnlyModifyOwnMarkers()
    {
        var store = new MarkerStore();
        store.Add(Marker(owner: "p1"));

        var updated = Marker(owner: "p1");
        updated.X = 0.9f;

        Assert.Equal(MutationOutcome.PermissionDenied, store.Update("m1", updated, "p2", false).Outcome);
        Assert.Equal(MutationOutcome.Applied, store.Update("m1", updated, "p1", false).Outcome);

        Assert.Equal(MutationOutcome.PermissionDenied, store.Delete("m1", "p2", false).Outcome);
        Assert.Equal(MutationOutcome.Applied, store.Delete("m1", "p1", false).Outcome);
    }

    [Fact]
    public void Host_CanModifyAnyMarker()
    {
        var store = new MarkerStore();
        store.Add(Marker(owner: "p1"));

        var updated = Marker(owner: "p1");
        updated.Y = 0.1f;
        Assert.Equal(MutationOutcome.Applied, store.Update("m1", updated, "host", true).Outcome);
        Assert.Equal(MutationOutcome.Applied, store.Delete("m1", "host", true).Outcome);
    }

    [Fact]
    public void Update_KeepsOwnerAndCreatedAt()
    {
        var store = new MarkerStore();
        store.Add(Marker(owner: "p1", createdAt: 111));

        var malicious = Marker(owner: "p2", createdAt: 999);
        malicious.X = 0.25f;
        var result = store.Update("m1", malicious, "p1", false);

        Assert.True(result.Applied);
        Assert.Equal("p1", result.Marker!.OwnerId);
        Assert.Equal(111, result.Marker.CreatedAt);
        Assert.Equal(0.25f, store.Get("m1")!.X, 4);
    }

    [Fact]
    public void Update_UsesRequestIdNotBodyId()
    {
        var store = new MarkerStore();
        store.Add(Marker(id: "m1", owner: "p1"));

        var body = Marker(id: "other", owner: "p1");
        body.X = 0.7f;
        var result = store.Update("m1", body, "p1", false);

        Assert.True(result.Applied);
        Assert.Equal("m1", result.Marker!.Id);
        Assert.Equal(MutationOutcome.NotFound, store.Update("nope", body, "p1", false).Outcome);
    }

    [Fact]
    public void Snapshot_IsSortedByCreatedAt()
    {
        var store = new MarkerStore();
        store.Add(Marker(id: "c", createdAt: 300));
        store.Add(Marker(id: "a", createdAt: 100));
        store.Add(Marker(id: "b", createdAt: 200));

        var ids = store.Snapshot().Select(m => m.Id).ToArray();
        Assert.Equal(new[] { "a", "b", "c" }, ids);
    }

    [Fact]
    public void Snapshot_ReturnsCopies()
    {
        var store = new MarkerStore();
        store.Add(Marker());
        var snapshot = store.Snapshot();
        snapshot[0].X = 0.99f;
        Assert.Equal(0.5f, store.Get("m1")!.X, 4);
    }

    [Fact]
    public void ReplaceAll_RebuildsSnapshotAndSkipsDuplicates()
    {
        var store = new MarkerStore();
        store.Add(Marker(id: "old"));

        store.ReplaceAll(new[]
        {
            Marker(id: "n1", createdAt: 5),
            Marker(id: "n2", createdAt: 6),
            Marker(id: "n1", createdAt: 7), // 重复 Id 忽略
            null!,
        });

        var ids = store.Snapshot().Select(m => m.Id).ToArray();
        Assert.Equal(new[] { "n1", "n2" }, ids);
        Assert.Null(store.Get("old"));
    }

    [Fact]
    public void Validator_ClampsAndNormalizes()
    {
        var marker = new Marker
        {
            Id = "m",
            Type = MarkerType.Circle,
            X = -3f,
            Y = 9f,
            EndX = 2f,
            EndY = -1f,
            Size = 99f,
            Rotation = -90f,
            Color = "ff0000",
            Text = new string('x', 900),
            IconId = "  ",
        };

        var normalized = MarkerValidator.Normalize(marker);
        Assert.Equal(0f, normalized.X);
        Assert.Equal(1f, normalized.Y);
        Assert.Equal(1f, normalized.EndX);
        Assert.Equal(0f, normalized.EndY);
        Assert.Equal(MarkerValidator.MaxSize, normalized.Size);
        Assert.Equal(270f, normalized.Rotation, 3);
        Assert.Equal("#FFFF0000", normalized.Color);
        Assert.Equal(MarkerValidator.MaxTextLength, normalized.Text!.Length);
        Assert.Null(normalized.IconId);
        // 原始对象未被修改
        Assert.Equal(-3f, marker.X);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(360f, 0f)]
    [InlineData(-45f, 315f)]
    [InlineData(450f, 90f)]
    [InlineData(float.NaN, 0f)]
    public void Validator_NormalizesRotation(float input, float expected)
    {
        Assert.Equal(expected, MarkerValidator.NormalizeRotation(input), 3);
    }

    [Fact]
    public void Validator_UnknownMarkerTypeIsKeptForCompatibility()
    {
        var marker = new Marker { Id = "m", Type = (MarkerType)99, Size = 0.01f };
        var (status, normalized) = MarkerValidator.Validate(marker);
        Assert.Equal(ValidateStatus.Ok, status);
        Assert.Equal((MarkerType)99, normalized!.Type);
    }

    [Fact]
    public void Validator_RejectsNegativeUnknownType()
    {
        var marker = new Marker { Id = "m", Type = (MarkerType)(-1) };
        var (status, _) = MarkerValidator.Validate(marker);
        Assert.Equal(ValidateStatus.InvalidType, status);
    }

    [Fact]
    public void Store_IsThreadSafeUnderConcurrentWrites()
    {
        var store = new MarkerStore();
        Parallel.For(0, 400, i =>
        {
            store.Add(Marker(id: "m" + i, createdAt: i));
        });
        Assert.Equal(400, store.Count);
        Assert.Equal(400, store.Snapshot().Count);
    }
}
