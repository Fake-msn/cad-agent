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

    private DrawingSession(string path, CadDocument doc)
    {
        SourcePath = path;
        Doc = doc;
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

    public static DrawingSession Load(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".dwg")
        {
            string workingPath = WorkingPathFor(path);
            if (ShouldLoadWorkingCopy(workingPath))
            {
                // The working copy is durable editable state: committed edits live there.
                // Load it as-is; re-deriving from the pristine .dwg would wipe them.
                return new DrawingSession(workingPath, LoadDxf(workingPath));
            }
            // First load only: materialize the working DXF from the read-only original.
            var session = new DrawingSession(workingPath, LoadDwg(path));
            session.SaveWorking();
            return session;
        }
        CadDocument doc = ext switch
        {
            ".dxf" => LoadDxf(path),
            _ => throw new NotSupportedException($"不支持的图纸格式: {ext}")
        };
        return new DrawingSession(WorkingPathFor(path), doc);
    }

    private static CadDocument LoadDxf(string path)
    {
        using var reader = new DxfReader(path);
        return reader.Read();
    }

    private static CadDocument LoadDwg(string path)
    {
        using var reader = new DwgReader(path);
        return reader.Read();
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
        return new DrawingSummary(SourcePath, Doc.Header.Version.ToString(), layers, entities, unknowns);
    }
}
