using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.View.ViewModels;

namespace CadAgent.View.Tests;

/// <summary>
/// 天读证据面板（只读）：DeepReadViewModel 从 .cadagent/semantic.json 读
/// NativeEntities/ExplodeEvidence/BlockDefTdb，重建五区行（原生参数/炸开证据/
/// 符号候选/块定义/证据状态），句柄序 Ordinal 排序；中心线 JSON（kind=line）
/// 解析失败行计入 SkewedCenterlines；损坏/IO 错误 → ErrorMessage + 全清空
/// （镜像 SemanticViewModel 守卫风格，只读面板不写审计）。
/// </summary>
public sealed class DeepReadViewModelTests
{
    private const string ValidCenterline =
        "{\"kind\":\"line\",\"p1\":[0.0,5.0,0.0],\"p2\":[10.0,5.0,0.0],\"width\":200.0}";

    private sealed class Fixture : IDisposable
    {
        public string AgentDir { get; }
        public string OverlayPath => Path.Combine(AgentDir, "semantic.json");

        public Fixture()
        {
            AgentDir = Path.Combine(Path.GetTempPath(), "cadagent-deepread-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(AgentDir);
        }

        public void Save(SemanticOverlay overlay) => overlay.Save(OverlayPath);

        public void Dispose()
        {
            if (Directory.Exists(AgentDir)) Directory.Delete(AgentDir, true);
        }
    }

    /// <summary>乱序入库的抽样 overlay：句柄 "20" 先于 "10" 写入，行序须按句柄 Ordinal。</summary>
    private static SemanticOverlay Overlay() => new()
    {
        NativeEntities = new List<NativeEntityParam>
        {
            new("20", "TDbWall", SemanticClass.Wall,
                new Dictionary<string, string> { ["Width"] = "200", ["ObjectName"] = "TDbWall" },
                new double[] { 0, 0, 0, 10, 10, 0 }, DateTimeOffset.UnixEpoch, ReadOk: true, Layer: "W-WALL"),
            new("10", "TDbWall", SemanticClass.Unknown,
                new Dictionary<string, string> { ["Error"] = "忙窗耗尽" },
                null, DateTimeOffset.UnixEpoch, ReadOk: false),
        },
        ExplodeEvidence = new List<ExplodeEvidence>
        {
            new("20", "TDbWall", "TDbWall", "W-WALL", null, true, 3, 0,
                new List<ExplodePrimitive>
                {
                    new("block", Array.Empty<double>(), null, "$DorLib2D$00000116"),
                    new("block", Array.Empty<double>(), null, "$TWTSYS$00000007"),
                    new("line", new double[] { 0, 5, 0, 10, 5, 0 }, null, null),
                },
                new Dictionary<string, string>(), ValidCenterline, DateTimeOffset.UnixEpoch,
                FailReason: "extents:degenerate",
                FailReasons: new[] { "extents:degenerate", "explode:eInvalidInput" }),
            new("30", "TDbDoor", "TDbDoor", "W-DOOR", null, false, 2, 0,
                new List<ExplodePrimitive>(),
                new Dictionary<string, string>(), "{\"kind\":\"line\",\"p1\":[0,0]}", DateTimeOffset.UnixEpoch),
        },
        BlockDefTdb = new List<BlockDefTdbEntry>
        {
            new("BLK-B", "TDbDoor", 2, null),
            new("BLK-A", "TDbWall", 3, "A"),
        },
    };

    [Fact]
    public void Load_BuildsFiveSections_OrderedByHandleOrdinal()
    {
        using var f = new Fixture();
        f.Save(Overlay());
        var vm = new DeepReadViewModel(f.AgentDir, () => "B");

        Assert.Null(vm.ErrorMessage);
        Assert.Equal(2, vm.NativeRows.Count);
        Assert.Equal(new[] { "10", "20" }, vm.NativeRows.Select(r => r.Handle).ToArray());
        NativeParamRow failed = vm.NativeRows[0];
        Assert.Equal("TDbWall", failed.ObjectName);
        Assert.Equal("Unknown", failed.Class);
        Assert.Equal("—", failed.Layer);
        Assert.False(failed.ReadOk);
        Assert.Equal("Error=忙窗耗尽", failed.ParamsSummary);
        Assert.Equal("忙窗耗尽", failed.FailReason); // Params["Error"] 优先（overlay 无独立原因字段）
        NativeParamRow ok = vm.NativeRows[1];
        Assert.Equal("Wall", ok.Class);
        Assert.Equal("W-WALL", ok.Layer);
        Assert.True(ok.ReadOk);
        Assert.Equal("ObjectName=TDbWall; Width=200", ok.ParamsSummary);
        Assert.Null(ok.FailReason);

        Assert.Equal(new[] { "20", "30" }, vm.ExplodeRows.Select(r => r.Handle).ToArray());
        ExplodeRow degenerate = vm.ExplodeRows[0];
        Assert.Equal("TDbWall", degenerate.RxClass);
        Assert.True(degenerate.FullyExplodable);
        Assert.Equal(3, degenerate.PrimitiveCount);
        Assert.Equal(0, degenerate.NestedTdbCount);
        Assert.True(degenerate.Degenerate);
        Assert.Equal("extents:degenerate; explode:eInvalidInput", degenerate.FailReasonsSummary);
        Assert.False(vm.ExplodeRows[1].Degenerate);
        Assert.Null(vm.ExplodeRows[1].FailReasonsSummary);

        Assert.Equal(2, vm.SymbolRows.Count);
        SymbolCandidateRow door = vm.SymbolRows[0];
        Assert.Equal("20", door.Handle);
        Assert.Equal("$DorLib2D$00000116", door.BlockName);
        Assert.Equal("door-window", door.Kind);
        Assert.Equal("门窗库块（候选）", door.ClassHint);
        Assert.Equal("equipment-sym", vm.SymbolRows[1].Kind);

        Assert.Equal(new[] { "BLK-A", "BLK-B" }, vm.BlockDefRows.Select(r => r.BlockName).ToArray());
        BlockDefRow a = vm.BlockDefRows[0];
        Assert.Equal("TDbWall", a.RxClass);
        Assert.Equal(3, a.Count);
        Assert.Equal("A", a.DrawingHash);
        Assert.False(a.IsCurrentDrawing); // 当前哈希 B ≠ 行哈希 A
        Assert.Null(vm.BlockDefRows[1].IsCurrentDrawing); // 行哈希缺失 → 不猜

        Assert.Equal(new[] { "10", "20", "30" }, vm.EvidenceRows.Select(r => r.Handle).ToArray());
        EvidenceStateRow failRow = vm.EvidenceRows[0];
        Assert.Equal("不可验(ReadOk=false)", failRow.ParamsState);
        Assert.Equal("不可验(缺失)", failRow.ExplodeState);
        Assert.False(string.IsNullOrEmpty(failRow.Advisory));
        Assert.Equal("可验", vm.EvidenceRows[1].ParamsState);
        Assert.Equal("不可验(bbox缺失)", vm.EvidenceRows[1].ExplodeState); // 全炸但 bbox null
        Assert.Equal("不可验(缺行)", vm.EvidenceRows[2].ParamsState);
        Assert.Equal("不可验(未全炸)", vm.EvidenceRows[2].ExplodeState);

        Assert.Contains("原生参数 2", vm.CountsSummary);
        Assert.Contains("炸开证据 2", vm.CountsSummary);
        Assert.Contains("符号候选 2", vm.CountsSummary);
        Assert.Contains("块定义 2", vm.CountsSummary);
        Assert.Contains("证据状态 3", vm.CountsSummary);
    }

    [Fact]
    public void BlockDefRows_NoCurrentHash_IsCurrentDrawingNull()
    {
        using var f = new Fixture();
        f.Save(Overlay());
        var vm = new DeepReadViewModel(f.AgentDir); // 无哈希提供方 → 一律 null（不可比不猜）
        Assert.All(vm.BlockDefRows, r => Assert.Null(r.IsCurrentDrawing));
    }

    [Fact]
    public void Centerlines_ParsesValidLine_SkipsMalformed()
    {
        using var f = new Fixture();
        f.Save(Overlay());
        var vm = new DeepReadViewModel(f.AgentDir, () => "B");

        Assert.True(vm.HasCenterlines);
        CenterlineSeg seg = Assert.Single(vm.Centerlines);
        Assert.Equal("20", seg.Handle);
        Assert.Equal(0, seg.X1, 6);
        Assert.Equal(5, seg.Y1, 6);
        Assert.Equal(10, seg.X2, 6);
        Assert.Equal(5, seg.Y2, 6);
        Assert.Equal(1, vm.SkewedCenterlines); // 句柄 30 的 JSON 缺 p2/width → 跳过并计数
    }

    [Fact]
    public void CorruptJson_ErrorMessageAndListsCleared()
    {
        using var f = new Fixture();
        File.WriteAllText(f.OverlayPath, "{ not-json");
        var vm = new DeepReadViewModel(f.AgentDir, () => "B");

        Assert.False(string.IsNullOrEmpty(vm.ErrorMessage));
        Assert.Empty(vm.NativeRows);
        Assert.Empty(vm.ExplodeRows);
        Assert.Empty(vm.SymbolRows);
        Assert.Empty(vm.BlockDefRows);
        Assert.Empty(vm.EvidenceRows);
        Assert.Empty(vm.Centerlines);
        Assert.False(vm.HasCenterlines);
        Assert.Equal(0, vm.SkewedCenterlines);
    }

    [Fact]
    public void MissingFile_EmptyNoError()
    {
        using var f = new Fixture();
        var vm = new DeepReadViewModel(f.AgentDir, () => "B");

        Assert.Null(vm.ErrorMessage);
        Assert.Empty(vm.NativeRows);
        Assert.Empty(vm.ExplodeRows);
        Assert.Empty(vm.SymbolRows);
        Assert.Empty(vm.BlockDefRows);
        Assert.Empty(vm.EvidenceRows);
        Assert.False(vm.HasCenterlines);
    }

    [Fact]
    public void OldOverlayWithoutEvidenceTables_EmptyCompatNoError()
    {
        using var f = new Fixture();
        File.WriteAllText(f.OverlayPath, "{\"Annotations\":[],\"Mappings\":[],\"Version\":1}");
        var vm = new DeepReadViewModel(f.AgentDir, () => "B");

        Assert.Null(vm.ErrorMessage);
        Assert.Empty(vm.NativeRows);
        Assert.Empty(vm.ExplodeRows);
        Assert.Empty(vm.SymbolRows);
        Assert.Empty(vm.BlockDefRows);
        Assert.Empty(vm.EvidenceRows);
        Assert.False(vm.HasCenterlines);
    }
}
