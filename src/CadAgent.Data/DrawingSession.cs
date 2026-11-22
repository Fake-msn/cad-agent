using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CadAgent.Core;

namespace CadAgent.Data;

public sealed class DrawingSession : IDrawingSession
{
    public CadDocument Doc { get; private set; }
    public string SourcePath { get; }

    private IReadOnlyList<SheetRecord>? _sheets;
    private string? _sheetsFingerprint;
    /// <summary>
    /// 幅面登记簿（懒加载 + 轻量指纹校验，T4）：每次访问校验实体计数+图框句柄/插入点/属性指纹，
    /// handler 就地编辑（加图框/移动图框等）后指纹变化→重建，不再陈旧；指纹不变→复用缓存。
    /// Reload/RestoreState 整体替换文档仍显式失效。
    /// </summary>
    public IReadOnlyList<SheetRecord> Sheets
    {
        get
        {
            string fp = Data.Sheets.SheetRegistryBuilder.Fingerprint(Doc);
            if (_sheets == null || _sheetsFingerprint != fp)
            {
                _sheets = Data.Sheets.SheetRegistryBuilder.Build(Doc);
                _sheetsFingerprint = fp;
            }
            return _sheets;
        }
    }

    /// <summary>
    /// 读取器通知 census（防假 Green）：ACadSharp 把天正 TCH_* 读为 UnknownEntity 但不暴露进对象图，
    /// 图内 Unknowns 普查为 0 也不代表图纸可全解析——本 census 记录通知侧的未解析对象类×计数。
    /// 无 TCH 图纸恒为 Empty（行为保持）；Reload/RestoreState 不重置（内存 DXF 无 TCH，census 保持原值）。
    /// </summary>
    public UnparsedCensus Unparsed { get; }

    // R1：internal（原 private）+ 第三可选参——经 InternalsVisibleTo 供 Data.Tests 合成 session；public 面不变。
    internal DrawingSession(string path, CadDocument doc, UnparsedCensus? unparsed = null)
    {
        SourcePath = path;
        Doc = doc;
        Unparsed = unparsed ?? UnparsedCensus.Empty;
    }

    /// <summary>
    /// Maps a source path to THE WORKING FILE the engine snapshots/restores.
    /// A .dwg source gets a sidecar ".working.dxf"; the original .dwg is never written.
    /// Any other extension is used unchanged.
    /// </summary>
    public static string WorkingPathFor(string sourcePath)
    {
        return Path.GetExtension(sourcePath).ToLowerInvariant() == ".dwg"
            ? sourcePath + ".working.dxf"
            : sourcePath;
    }

    /// <summary>
    /// Load-decision for .dwg sources: an existing working copy IS the durable
    /// editable state (committed edits live there) and must be loaded as-is;
    /// only when absent may it be materialized from the read-only .dwg original.
    /// </summary>
    internal static bool ShouldLoadWorkingCopy(string workingPath) => File.Exists(workingPath);

    /// <summary>
    /// 加载图纸。.dwg 首载经 DWG 通知通道重建 census；working 分支内存 DXF 无 TCH 通知，
    /// census 优先用 <paramref name="persisted"/>（sidecar UnparsedCensusStore.Load）。
    /// R9（陈旧 working 假 Green 旁路闭合）：persisted Empty/缺 + 源 .dwg 存在 → 从源 DWG
    /// 通知一次性重建 census（只取 census，doc 丢弃，working 不重写）；源缺失 → 拒绝
    /// （InvalidOperationException，无法核验完整性，不得假 Green 放行）。
    /// T3：persisted 对 .dxf 源同样生效（sidecar 注入/恢复 census；null→Empty，无 census 行为逐字不变）。
    /// </summary>
    public static DrawingSession Load(string path, UnparsedCensus? persisted = null)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".dwg")
        {
            string workingPath = WorkingPathFor(path);
            if (ShouldLoadWorkingCopy(workingPath))
            {
                // The working copy is durable editable state: committed edits live there.
                // Load it as-is; re-deriving from the pristine .dwg would wipe them.
                // working DXF 已丢失 TCH 对象→census 优先靠 sidecar 持久化值（persisted）恢复。
                CadDocument workingDoc = LoadDxf(workingPath);
                if (persisted is { TotalCount: > 0 })
                    return new DrawingSession(workingPath, workingDoc, persisted);
                // R9：persisted Empty/缺（切片前物化的陈旧 working + 新项目无 sidecar）——
                // 此前直接 Empty 放行 = 假 Green 旁路（intake exit 0 + run tx-commit）。
                if (!File.Exists(path))
                    throw new InvalidOperationException(
                        "working 副本无源图且无 census，无法核验完整性：请提供源图或删除 working 副本重新导入");
                // 源存在 → 从源 DWG 通知一次性重建 census（只取 census，doc 丢弃——
                // working 仍是持久编辑态，绝不重物化）；调用方 LoadWithCensus 既有逻辑回写 sidecar。
                var (_, rebuilt) = LoadDwg(path);
                return new DrawingSession(workingPath, workingDoc, rebuilt);
            }
            // First load only: materialize the working DXF from the read-only original.
            var (dwgDoc, census) = LoadDwg(path);
            var session = new DrawingSession(workingPath, dwgDoc, census);
            session.SaveWorking();
            return session;
        }
        CadDocument doc = ext switch
        {
            ".dxf" => LoadDxf(path),
            _ => throw new NotSupportedException($"不支持的图纸格式: {ext}")
        };
        return new DrawingSession(WorkingPathFor(path), doc, persisted);
    }

    private static CadDocument LoadDxf(string path)
    {
        using var reader = new DxfReader(path);
        return reader.Read();
    }

    /// <summary>
    /// DWG 读取 + 通知 census 捕获：ACadSharp 3.8.35 将天正 TCH_* 读为 UnknownEntity 但
    /// 不暴露进文档对象图（实证：OnNotification 数千条/全块表普查 0）——通知是唯一证据通道。
    /// </summary>
    private static (CadDocument Doc, UnparsedCensus Census) LoadDwg(string path)
    {
        using var reader = new DwgReader(path);
        var messages = new List<string>();
        reader.OnNotification += (_, e) => messages.Add(e.Message);
        var doc = reader.Read();
        return (doc, UnparsedCensus.FromNotifications(messages));
    }

    /// <summary>
    /// SHA-256 over a canonical content projection of the in-memory document.
    /// Excludes volatile header fields ($HANDSEED, Julian-date doubles) so the hash
    /// is stable across no-change SaveWorking/Reload cycles and reflects real
    /// content changes only. Doubles use round-trip "R" format, invariant culture.
    /// </summary>
    public string ComputeHash()
    {
        var sb = new StringBuilder();
        foreach (var layer in Doc.Layers.OrderBy(l => l.Name, StringComparer.Ordinal))
        {
            sb.Append(FormattableString.Invariant($"L|{layer.Name}|{layer.Color}")).Append('\n');
        }
        foreach (var e in Doc.Entities.OrderBy(e => e.Handle))
        {
            sb.Append(FormattableString.Invariant($"E|{e.Handle}|{e.GetType().Name}|{e.Layer?.Name ?? "0"}|{e.Color}"));
            switch (e)
            {
                case Line line:
                    sb.Append(FormattableString.Invariant(
                        $"|{line.StartPoint.X:R}|{line.StartPoint.Y:R}|{line.StartPoint.Z:R}|{line.EndPoint.X:R}|{line.EndPoint.Y:R}|{line.EndPoint.Z:R}"));
                    break;
                case TextEntity text: // single-line TEXT only; MText : Entity (NOT TextEntity) — MText projection deferred
                    sb.Append(FormattableString.Invariant(
                        $"|{text.InsertPoint.X:R}|{text.InsertPoint.Y:R}|{text.InsertPoint.Z:R}|{text.Value}|{text.Height:R}"));
                    break;
                case UnknownEntity unknown:
                    sb.Append(FormattableString.Invariant($"|{unknown.ObjectName}|{unknown.DxfClass?.CppClassName}"));
                    break;
                case ProxyEntity proxy:
                    sb.Append(FormattableString.Invariant($"|{proxy.ObjectName}|{proxy.DxfClass?.CppClassName}"));
                    break;
                case Insert insert:
                    sb.Append(FormattableString.Invariant(
                        $"|I|{insert.Block?.Name}|{insert.InsertPoint.X:R}|{insert.InsertPoint.Y:R}|{insert.InsertPoint.Z:R}|{insert.XScale:R}|{insert.YScale:R}|{insert.ZScale:R}|{insert.Rotation:R}"));
                    break;
                // Other entity types: base fields only (documented limitation; extend as handlers grow).
            }
            sb.Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public void SaveWorking()
    {
        // 原子写（合并前审查 Critical#1）：先写 tmp 再 Move——写中途崩溃不损持久工作副本
        // （spec §12 load-if-exists 持久状态；同 SemanticOverlay/LlmConfigStore/Anchor 模式）。
        string tmp = SourcePath + ".tmp";
        using (var fs = File.Create(tmp)) DxfWriter.Write(fs, Doc);
        File.Move(tmp, SourcePath, overwrite: true);
    }

    public void Reload()
    {
        Doc = LoadDxf(SourcePath);
        _sheets = null; // 文档整体替换→幅面登记簿缓存失效，下次访问重建
        _sheetsFingerprint = null;
        // Unparsed census 保持原值：working DXF 无 TCH 对象/通知，重读不产生新证据（首载或 sidecar 值仍有效）。
    }

    /// <summary>步级 undo（3b-ii）：内存 DXF 序列化捕获模型状态，不触磁盘文件。</summary>
    public byte[] CaptureState()
    {
        using var ms = new MemoryStream();
        DxfWriter.Write(ms, Doc);
        return ms.ToArray();
    }

    /// <summary>从捕获状态恢复模型（内存 DXF 读回）。</summary>
    public void RestoreState(byte[] state)
    {
        using var ms = new MemoryStream(state);
        using var reader = new DxfReader(ms);
        Doc = reader.Read();
        _sheets = null; // undo 恢复整体替换文档→幅面登记簿缓存失效
        _sheetsFingerprint = null;
        // Unparsed census 保持原值：内存 DXF 无 TCH（undo 不改变源图未解析对象事实）。
    }

    public DrawingSummary Summarize()
    {
        var layers = Doc.Layers.Select(l => l.Name).ToList();
        var entities = new List<EntityBrief>();
        var unknowns = new List<UnknownBrief>();
        foreach (var e in Doc.Entities)
        {
            string handle = e.Handle.ToString();
            string layer = e.Layer?.Name ?? "0";
            if (e is UnknownEntity u)
                unknowns.Add(new UnknownBrief(handle, u.ObjectName ?? "",
                    u.DxfClass?.CppClassName ?? "", u.DxfClass?.ApplicationName ?? ""));
            else if (e is ProxyEntity p)
                unknowns.Add(new UnknownBrief(handle, p.ObjectName ?? "",
                    p.DxfClass?.CppClassName ?? "", p.DxfClass?.ApplicationName ?? ""));
            else
                entities.Add(new EntityBrief(handle, e.GetType().Name, layer));
        }
        // census briefs 与图内 Unknowns 分离（不混入）：通知侧未解析对象无句柄/图层可言，
        // Handle=""、ObjectName=CppClassName（通知只给 DXF 名）、ApplicationName=""。
        // 逐对象展开（类×Count → Count 条 brief）：Unparsed.Count == census.TotalCount，
        // 与实体计数同口径（T1 测试钉住：TCH_WALL×2 → 2 条；闸口按对象数比较）。
        var unparsed = Unparsed.Classes
            .SelectMany(c => Enumerable.Repeat(new UnknownBrief("", c.CppClassName, c.CppClassName, ""), c.Count))
            .ToList();
        // 空 census → null（Fix Round 1：无 TCH 图纸 summary/intake stdout 逐字不变，
        // 不得多出 "Unparsed": []；配合 DrawingSummary.Unparsed 的 WhenWritingNull 省略字段）。
        return new DrawingSummary(SourcePath, Doc.Header.Version.ToString(), layers, entities, unknowns,
            unparsed.Count == 0 ? null : unparsed);
    }
}
