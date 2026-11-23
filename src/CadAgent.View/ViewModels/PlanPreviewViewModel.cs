using CadAgent.Core;
using CadAgent.Core.Llm;
using CadAgent.Data;
using CadAgent.Data.Handlers;
using CadAgent.Data.Semantic;
using CadAgent.Llm;

namespace CadAgent.View.ViewModels;

/// <summary>
/// ④纠偏计划视图预览（spec §4.1④/§5，计划3 LLM caller 解锁后承接）：
/// 意图→AgentOrchestrator（出境闸/预算/llm-call 审计/schema+kind 校验全在 Core）→
/// confirmPlan 回调 marshal 到预览面板→人工确认→执行（StepEngine 无旁路）→读回验证。
/// 密钥经 DPAPI/环境变量解析（spec §8），永不入预览文本/审计。
/// allowedKinds = HandlerRegistry.All()（insert-symbol 视图侧 deferred——CLI 有，
/// 跨文档符号导入的视图接线留后续切片；LLM 若提议该 kind 会被 parser 拒绝，诚实失败）。
/// 会话闸粗粒度持有整个 RunAsync（编排内含摘要/执行/读回多段 session 访问）——
/// 预览期间编辑/问题刷新在闸上阻塞（计划明示）。摘要也在闸内计算（读 session
/// 不得看到被编辑撕扯的 Doc，镜像 EditViewModel 闸口径）。
/// 编排经 Task.Run 在线程池执行（镜像 EditViewModel.ApplyAsync 模式）：
/// confirmPlan/confirmCloudEgress 是同步阻塞回调（等 UI 点击），绝不能占用
/// UI 线程——否则 PreviewReady 的 Dispatcher.Post 永远得不到处理（死锁）。
/// 事件在回调线程触发，MainWindow 经 Dispatcher.UIThread.Post marshal。
/// </summary>
public sealed class PlanPreviewViewModel
{
    private readonly DrawingSession _session;
    private readonly string _projectDir;
    private readonly IAuditStore _audit;
    private readonly IIdentityProvider _identity;
    private readonly RoleChecker _roles;
    private readonly SemaphoreSlim _sessionGate;
    private readonly ILlmClient? _clientOverride;
    private readonly ProviderProfile? _profileOverride;
    private TaskCompletionSource<bool>? _planTcs;
    private TaskCompletionSource<bool>? _egressTcs;
    private CancellationTokenSource? _cts;

    public PlanPreviewViewModel(DrawingSession session, string projectDir,
        IAuditStore audit, IIdentityProvider identity, RoleChecker roles,
        SemaphoreSlim sessionGate, ILlmClient? clientOverride = null, ProviderProfile? profileOverride = null)
    {
        _session = session;
        _projectDir = projectDir;
        _audit = audit;
        _identity = identity;
        _roles = roles;
        _sessionGate = sessionGate;
        _clientOverride = clientOverride;
        _profileOverride = profileOverride;
    }

    public string Intent { get; set; } = "";
    public IReadOnlyList<StepSpec> PreviewSteps { get; private set; } = Array.Empty<StepSpec>();
    public bool IsPreviewVisible { get; private set; }
    public bool IsBusy { get; private set; }
    public bool IsEgressConsentVisible { get; private set; }
    public string EgressPrompt { get; private set; } = "";
    public string Status { get; private set; } = "";
    public string? ErrorMessage { get; private set; }
    public string? LastTxId { get; private set; }

    /// <summary>预览就绪（步骤已填充）→ UI 显示面板。调用方上下文需 marshal（MainWindow Dispatcher）。</summary>
    public event Action? PreviewReady;
    /// <summary>首次云端出境需确认→ UI 显示同意提示。</summary>
    public event Action? EgressConsentRequired;
    /// <summary>编排结束（任何状态）→ UI 刷新。</summary>
    public event Action? RunCompleted;
    /// <summary>执行成功→ MainWindow 全视图刷新（渲染/图层/问题/审计）。</summary>
    public event Action? PlanExecuted;

    public async Task GenerateAsync()
    {
        if (IsBusy) return;
        ErrorMessage = null;
        Status = "";
        LastTxId = null;
        IsBusy = true;
        bool executed = false; // Imp#4：内层记录执行成功，释闸后才触发 PlanExecuted
        try
        {
            // ---- 前奏（Imp#2：整体纳入 try/catch）----
            // LlmConfigStore.Load / DpapiKeyProtector.ResolveKey / SemanticOverlay.Load
            // 损坏的 llm-profiles.json/semantic.json 会抛 JsonException——此前在 try/catch
            // 外，经 MainWindow async void 崩进程（违反 §9 降级不变量）。现降级为
            // ErrorMessage + Status="Failed" + RunCompleted（finally），不抛。
            string agentDir = Path.Combine(_projectDir, ".cadagent");
            ProviderProfile? profile = _profileOverride;
            ILlmClient? client = _clientOverride;
            if (profile is null)
            {
                var store = LlmConfigStore.Load(Path.Combine(agentDir, "llm-profiles.json"));
                string? name = store.ActiveProfileName;
                profile = name is null ? null : store.Profiles.FirstOrDefault(p =>
                    string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            }
            if (profile is null)
            {
                ErrorMessage = "无活动 LLM profile——请先在 CLI 执行 llm add / llm use 配置（spec §8）。";
                return; // RunCompleted 由 finally 统一触发
            }
            if (client is null)
            {
                var protector = new DpapiKeyProtector(Path.Combine(agentDir, "keys"));
                string? key = DpapiKeyProtector.ResolveKey(protector, profile.Name);
                if (string.IsNullOrEmpty(key))
                {
                    ErrorMessage = "无可用密钥（CLI llm add --key 或 CADAGENT_LLM_API_KEY 环境变量）。";
                    return; // RunCompleted 由 finally 统一触发
                }
                client = new OpenAiClient(profile, key);
            }

            string overlayPath = Path.Combine(agentDir, "semantic.json");
            var overlay = File.Exists(overlayPath) ? SemanticOverlay.Load(overlayPath) : null;
            ISemanticGate? gate = overlay is not null ? new SemanticGate(overlay) : null;
            var engine = new StepEngine(_audit, new SnapshotStore(Path.Combine(agentDir, "snapshots")),
                _identity, _roles, HandlerRegistry.All(), gate);
            var orch = new AgentOrchestrator(client, new UsageMeter(profile.Limits), new EgressPolicy(agentDir),
                engine, _audit, _identity, profile.Model, profile);
            var allowedKinds = HandlerRegistry.All().Select(h => h.Kind).ToHashSet(StringComparer.Ordinal);

            _cts = new CancellationTokenSource();
            await _sessionGate.WaitAsync();
            try
            {
                // 摘要在闸内计算（session 读与编辑互斥）；编排整体 Task.Run 下线程池——
                // confirmPlan 同步阻塞回调不占 UI 线程（见类注释死锁分析）。
                string summary = DrawingSummarizer.Summarize(_session, overlay);
                var result = await Task.Run(() => orch.RunAsync(Intent, summary, _session, "view-plan",
                    allowedKinds, HandlerKindDocs.Default,
                    confirmPlan: plan =>
                    {
                        PreviewSteps = plan.Steps;
                        IsPreviewVisible = true;
                        _planTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        PreviewReady?.Invoke();
                        return _planTcs.Task.GetAwaiter().GetResult();
                    },
                    confirmCloudEgress: () =>
                    {
                        EgressPrompt = $"首次向云端端点 {profile.Endpoint} 发送图纸数据，确认?";
                        IsEgressConsentVisible = true;
                        _egressTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        EgressConsentRequired?.Invoke();
                        return _egressTcs.Task.GetAwaiter().GetResult();
                    },
                    ct: _cts.Token));
                Status = result.Status.ToString();
                LastTxId = result.TxId;
                ErrorMessage = result.Error;
                if (result.Status == OrchestrationStatus.Executed)
                {
                    IsPreviewVisible = false;
                    executed = true; // 仅记录——不在此（持闸内）触发事件
                }
            }
            finally { _sessionGate.Release(); }
            // Imp#4：PlanExecuted 移到会话闸 Release 之后触发——MainWindow 经 Dispatcher.Post
            // 的全视图刷新（RefreshAfterEdit/Issues/Audit）不再与并发手动编辑的 Doc 枚举竞态
            // （此前持闸内触发，Post 排队到 UI 线程执行时闸可能已被另一编辑持有/释放，刷新绕闸）。
            // [合并前 ISTQB I3 更正] 上行"不再竞态"为过度声明：本修复仅重排序（释闸先于事件）。
            // PlanPreview 执行不置 _editBusy，释闸后 RefreshAfterEdit 仍在 UI 线程无闸枚举 Doc，
            // 与并发手动编辑的窄窗竞态仍在。真修复=刷新侧取闸，仍 deferred（窄窗竞态跟踪，
            // 见 view-closure 计划执行记录"合并前 ISTQB 审查 tracked deferred"小节）。
            if (executed) PlanExecuted?.Invoke();
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Status = "Failed";
        }
        finally
        {
            IsBusy = false;
            IsPreviewVisible = false;
            IsEgressConsentVisible = false;
            RunCompleted?.Invoke();
        }
    }

    /// <summary>预览面板"确认执行"按钮。</summary>
    public void ConfirmPlan(bool accept) => _planTcs?.TrySetResult(accept);

    /// <summary>出境同意提示"确认/拒绝"按钮。</summary>
    public void ConfirmEgress(bool accept)
    {
        IsEgressConsentVisible = false;
        _egressTcs?.TrySetResult(accept);
    }

    /// <summary>取消在途编排（预览拒绝+token 取消）。</summary>
    public void Cancel()
    {
        _planTcs?.TrySetResult(false);
        _egressTcs?.TrySetResult(false);
        _cts?.Cancel();
    }
}
