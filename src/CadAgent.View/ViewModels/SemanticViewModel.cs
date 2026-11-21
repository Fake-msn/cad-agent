using System.Text.Json;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Rules;
using CadAgent.Data.Semantic;

namespace CadAgent.View.ViewModels;

/// <summary>
/// 语义纠偏视图模型（计划4 T8）：纯 C#，无 Avalonia 依赖，可单测。
/// 与 CLI semantic scan/map 同一语义路径（控制器裁定：ViewModel 直接调
/// SemanticScanner+overlay+audit，审计 kind/params 逐字镜像 CLI）：
/// ScanAsync → SkillSet.Compose(builtin+skill) → SemanticScanner.Scan(session,
/// 既有 overlay, MergedAliases) → overlay.Save(.cadagent/semantic.json) →
/// 审计 semantic-scan（Source="semantic"，params{annotationCount,ambiguousCount,
/// version,rulePackVersion}，Target=图纸路径，前后哈希一致——扫描不改图纸）。
/// MapAsync → ConfirmMapping(操作者) + ApplyMappingToAnnotations(该层实体句柄，
/// 大小写不敏感) → Save → 审计 semantic-map（params{layer,class,operator}，
/// Target=图层名，前后哈希一致——映射只改叠加层）。
/// 加载守卫镜像 CLI LoadOrReject：overlay Load 失败 → intake-rejected 留痕
/// （Source="semantic"，params{Error,File}，哈希不可得→空串）后抛出。
/// 失败一律降级为 ErrorMessage（不崩 UI）；SemanticChanged 在调用方上下文触发。
/// IsolateLayer → 画布 HiddenLayers 隐藏其他全部图层（未知图层/未接画布→无操作）。
/// </summary>
public sealed class SemanticViewModel
{
    private readonly DrawingSession _session;
    private readonly string _projectDir;
    private readonly CanvasViewModel? _canvas;
    private readonly IAuditStore _audit;
    private readonly IIdentityProvider _identity;
    private readonly string _sessionId;

    public SemanticViewModel(DrawingSession session, string projectDir,
        CanvasViewModel? canvasVM, IAuditStore auditStore, IIdentityProvider identity,
        string? sessionId = null)
    {
        _session = session;
        _projectDir = projectDir;
        _canvas = canvasVM;
        _audit = auditStore;
        _identity = identity;
        _sessionId = sessionId ?? Guid.NewGuid().ToString("N");
    }

    /// <summary>最近一次扫描的不明确图层列表（证据：计数/样例句柄/名称提示；失败时清空）。</summary>
    public IReadOnlyList<AmbiguousLayer> Ambiguous { get; private set; } = Array.Empty<AmbiguousLayer>();

    /// <summary>最近一次扫描/映射后的标注总数。</summary>
    public int AnnotationCount { get; private set; }

    /// <summary>最近一次扫描/映射后的叠加层版本。</summary>
    public int Version { get; private set; }

    /// <summary>最近一次操作失败原因（包非法/overlay 损坏/未先扫描等）；null=成功。</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>扫描/映射完成（成功或失败）→ UI 重建列表。在调用方上下文触发。</summary>
    public event Action? SemanticChanged;

    private string OverlayPath => Path.Combine(_projectDir, ".cadagent", "semantic.json");

    /// <summary>
    /// 扫描：后台执行加载+扫描+落盘+审计（不阻塞 UI 线程），结果回到调用方上下文发布。
    /// 任何异常（包非法/Id 冲突/overlay 损坏/审计故障）→ ErrorMessage，列表清空。
    /// </summary>
    public async Task ScanAsync(IReadOnlyList<string> skillDirs)
    {
        try
        {
            ScanResult result = await Task.Run(() => ScanCore(skillDirs));
            Ambiguous = result.Ambiguous;
            AnnotationCount = result.Overlay.Annotations.Count;
            Version = result.Overlay.Version;
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            Ambiguous = Array.Empty<AmbiguousLayer>();
            AnnotationCount = 0;
            Version = 0;
            ErrorMessage = ex.Message;
        }
        SemanticChanged?.Invoke();
    }

    /// <summary>
    /// 人工确认图层映射：后台执行确认+升级标注+落盘+审计。无 overlay → 提示先扫描
    /// （同 CLI：不留审计、不落盘）；成功后该层移出不明确列表。
    /// </summary>
    public async Task MapAsync(string layer, SemanticClass cls)
    {
        try
        {
            SemanticOverlay overlay = await Task.Run(() => MapCore(layer, cls));
            Ambiguous = Ambiguous
                .Where(a => !string.Equals(a.LayerName, layer, StringComparison.OrdinalIgnoreCase))
                .ToList();
            AnnotationCount = overlay.Annotations.Count;
            Version = overlay.Version;
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        SemanticChanged?.Invoke();
    }

    /// <summary>
    /// 隔离显示：仅保留该图层可见，画布其余图层全部隐藏（大小写不敏感，
    /// 沿用 LayersViewModel.Isolate 语义）。未知图层/未接画布 → 无操作。
    /// </summary>
    public void IsolateLayer(string layer)
    {
        if (_canvas is null) return;
        List<string> names = _canvas.Items.Select(i => i.Layer)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!names.Contains(layer, StringComparer.OrdinalIgnoreCase)) return;
        _canvas.HiddenLayers.Clear();
        foreach (string name in names)
            if (!string.Equals(name, layer, StringComparison.OrdinalIgnoreCase))
                _canvas.HiddenLayers.Add(name);
        _canvas.Invalidate();
    }

    /// <summary>镜像 CLI SemanticScan：规则集校验先于副作用；Save 先于审计（CLI 序）。</summary>
    private ScanResult ScanCore(IReadOnlyList<string> skillDirs)
    {
        // 与 CLI LoadRuleSet 同一路径：包非法/Id 冲突 → 异常先于任何副作用（不审计）。
        SkillSet ruleSet = ComposeRuleSet(skillDirs);
        OperatorIdentity op = _identity.Current;
        SemanticOverlay existing = LoadOrReject(() => SemanticOverlay.Load(OverlayPath), OverlayPath);
        ScanResult result = SemanticScanner.Scan(_session, existing, ruleSet.MergedAliases);
        result.Overlay.Save(OverlayPath);
        // 扫描不改图纸：前后哈希一致（沿用 CLI intake 的诚实留痕）。
        string hash = _session.ComputeHash();
        _audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
            _sessionId, "semantic", "semantic-scan", _session.SourcePath,
            JsonSerializer.Serialize(new
            {
                annotationCount = result.Overlay.Annotations.Count,
                ambiguousCount = result.Ambiguous.Count,
                version = result.Overlay.Version,
                // 规则包溯源（与 CLI check/run-regression 同口径；无 skill→builtin 常量）。
                rulePackVersion = ruleSet.RulePackVersion
            }),
            hash, hash, "", ""));
        return result;
    }

    /// <summary>镜像 CLI SemanticMap：句柄取自在载 session 该层实体（大小写不敏感）。</summary>
    private SemanticOverlay MapCore(string layer, SemanticClass cls)
    {
        if (!File.Exists(OverlayPath))
            throw new InvalidOperationException("未找到语义叠加层，请先执行 semantic scan");
        OperatorIdentity op = _identity.Current;
        SemanticOverlay overlay = LoadOrReject(() => SemanticOverlay.Load(OverlayPath), OverlayPath);
        List<string> handlesOnLayer = _session.Doc.Entities
            .Where(e => string.Equals(e.Layer?.Name ?? "0", layer, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Handle.ToString())
            .ToList();
        overlay.ConfirmMapping(new LayerMapping(layer, cls, op.Account, DateTimeOffset.UtcNow));
        overlay.ApplyMappingToAnnotations(layer, cls, handlesOnLayer);
        overlay.Save(OverlayPath);
        // map 只改叠加层不改图纸：前后哈希一致。
        string hash = _session.ComputeHash();
        _audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
            _sessionId, "semantic", "semantic-map", layer,
            JsonSerializer.Serialize(new { layer, @class = cls.ToString(), @operator = op.Account }),
            hash, hash, "", ""));
        return overlay;
    }

    /// <summary>与 CLI LoadRuleSet / IssuesViewModel.Evaluate 同一合成路径。</summary>
    private static SkillSet ComposeRuleSet(IReadOnlyList<string> skillDirs) =>
        SkillSet.Compose(BuiltinRules.Load(), skillDirs.Select(SkillLoader.LoadDirectory).ToList());

    /// <summary>
    /// 加载守卫——逐字镜像 CLI LoadOrReject（spec §9 错误处理全部入审计）：
    /// Load 失败 → intake-rejected 留痕（Source="semantic"，target=失败文件，
    /// params{Error,File}，哈希不可得→空串）后重新抛出（调用方降级 ErrorMessage）。
    /// </summary>
    private T LoadOrReject<T>(Func<T> load, string file) where T : class
    {
        try
        {
            return load();
        }
        catch (Exception ex)
        {
            OperatorIdentity op = _identity.Current;
            _audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
                _sessionId, "semantic", "intake-rejected", file,
                JsonSerializer.Serialize(new { Error = ex.Message, File = file }), "", "", "", ""));
            throw;
        }
    }
}
