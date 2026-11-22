using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Tests;
using CadAgent.View.ViewModels;
using CSMath;

namespace CadAgent.View.Tests;

/// <summary>
/// fake LLM 客户端：首次调用返回固定计划 JSON（不触网）；后续调用是编排器
/// 读回验证（AgentOrchestrator.VerifyLoopAsync）→ 返回 {"verdict":"ok"}，
/// 否则固定计划会被当作纠正计划再次要求确认，Executed 不可达。
/// ChatResponse 三参构造（Content/Usage/Model）与 ListModelsAsync 按
/// Core/Llm/ILlmClient.cs 实际接口补全。
/// </summary>
internal sealed class FakeLlmClient : ILlmClient
{
    private readonly string _content;
    private int _calls;

    public FakeLlmClient(string content) => _content = content;

    public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
    {
        string body = Interlocked.Increment(ref _calls) == 1 ? _content : """{"verdict":"ok"}""";
        return Task.FromResult(new ChatResponse(body, new TokenUsage(10, 20), "fake-model"));
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(new[] { "fake-model" });
}

/// <summary>
/// 视图闭环 T10（spec §4.1④）：PlanPreviewViewModel——意图→AgentOrchestrator
/// （出境闸/预算/llm-call 审计/schema+kind 校验全在 Core，零改动）→confirmPlan
/// 回调即预览钩子（预览不执行）→人工确认→StepEngine 执行（无审计旁路）。
/// 夹具镜像 EditViewModelTests：临时项目 + audit.db + session + identity。
/// profileOverride 本地端点（localhost 一等公民，出境闸 Allowed）；
/// LocalOnly + 云端端点 → BlockedEgress（orchestrator 既有路径）。
/// </summary>
public class PlanPreviewViewModelTests
{
    private const string PlanJson = """{"steps":[{"kind":"add-line","params":{"x1":0,"y1":0,"x2":10,"y2":0}}]}""";

    /// <summary>本地 profile（DataPolicy.LocalOnly + localhost → 出境闸 Allowed）。</summary>
    private static ProviderProfile LocalProfile => new(
        "local", "http://localhost:11434/v1", "fake-model",
        VisionCapable: false, DataPolicy.LocalOnly, new ProfileLimits());

    private sealed class Fixture : IDisposable
    {
        public string ProjectDir { get; }
        public string DxfPath { get; }
        public DrawingSession Session { get; }
        public SqliteAuditStore Audit { get; }
        public string AgentDir => Path.Combine(ProjectDir, ".cadagent");

        public Fixture()
        {
            ProjectDir = Path.Combine(Path.GetTempPath(), "cadagent-plan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(AgentDir);
            DxfPath = DxfFixture.CreateTempDxf(doc =>
            {
                var wall = new Layer("WALL");
                doc.Layers.Add(wall);
                doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0), Layer = wall });
            });
            Session = DrawingSession.Load(DxfPath);
            Audit = new SqliteAuditStore(Path.Combine(AgentDir, "audit.db"));
        }

        public PlanPreviewViewModel MakeVm(ILlmClient? client = null, ProviderProfile? profile = null) =>
            new(Session, ProjectDir, Audit,
                new FixedIdentity(new OperatorIdentity("tester", Role.Draw)),
                new RoleChecker(), new SemaphoreSlim(1, 1), client, profile);

        public void Dispose()
        {
            Audit.Dispose();
            // Microsoft.Data.Sqlite 连接池在 Dispose 后仍持有文件句柄，Windows 下须先清池才能删库
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(DxfPath);
            if (Directory.Exists(ProjectDir)) Directory.Delete(ProjectDir, true);
        }
    }

    private sealed class FixedIdentity : IIdentityProvider
    {
        public FixedIdentity(OperatorIdentity id) => Current = id;
        public OperatorIdentity Current { get; }
    }

    /// <summary>订阅 PreviewReady（线程池上下文触发）→ TCS 供测试等待。</summary>
    private static TaskCompletionSource ReadySignal(PlanPreviewViewModel vm, out Action handler)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler = () => tcs.TrySetResult();
        vm.PreviewReady += handler;
        return tcs;
    }

    [Fact]
    public async Task Generate_ValidPlan_PreviewPopulated_NotExecuted()
    {
        using var f = new Fixture();
        PlanPreviewViewModel vm = f.MakeVm(new FakeLlmClient(PlanJson), LocalProfile);
        TaskCompletionSource ready = ReadySignal(vm, out Action handler);
        try
        {
            Task run = vm.GenerateAsync();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));

            // 预览就绪：1 步 add-line，面板可见，编排仍在途（等人工确认）。
            Assert.Single(vm.PreviewSteps);
            Assert.Equal("add-line", vm.PreviewSteps[0].Kind);
            Assert.True(vm.IsPreviewVisible);
            Assert.True(vm.IsBusy);
            List<AuditRecord> records = f.Audit.ReadAll().ToList();
            Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-start"); // 预览不执行
            Assert.Contains(records, r => r.Entry.Kind == "llm-call"); // orchestrator 留痕

            vm.ConfirmPlan(false); // 收尾：拒绝使编排结束（本测试只关心预览态）
            await run.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { vm.PreviewReady -= handler; }
    }

    [Fact]
    public async Task ConfirmPlan_Executes_TxCommitted()
    {
        using var f = new Fixture();
        PlanPreviewViewModel vm = f.MakeVm(new FakeLlmClient(PlanJson), LocalProfile);
        int countBefore = f.Session.Doc.Entities.Count;
        int executed = 0;
        vm.PlanExecuted += () => executed++;
        TaskCompletionSource ready = ReadySignal(vm, out Action handler);
        try
        {
            Task run = vm.GenerateAsync(); // 启动不 await 完成——confirmPlan 阻塞等确认
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            vm.ConfirmPlan(true);
            await run.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal("Executed", vm.Status);
            Assert.False(string.IsNullOrEmpty(vm.LastTxId));
            Assert.Null(vm.ErrorMessage);
            Assert.False(vm.IsBusy);
            Assert.False(vm.IsPreviewVisible);
            Assert.Equal(countBefore + 1, f.Session.Doc.Entities.Count);
            List<AuditRecord> records = f.Audit.ReadAll().ToList();
            Assert.Contains(records, r => r.Entry.Kind == "tx-commit");
            Assert.Equal(1, executed); // PlanExecuted 事件触发（MainWindow 全视图刷新联动）
            Assert.True(f.Audit.VerifyChain(out string error), error);
        }
        finally { vm.PreviewReady -= handler; }
    }

    [Fact]
    public async Task RejectPlan_RejectedByHuman_NoTx()
    {
        using var f = new Fixture();
        PlanPreviewViewModel vm = f.MakeVm(new FakeLlmClient(PlanJson), LocalProfile);
        int countBefore = f.Session.Doc.Entities.Count;
        TaskCompletionSource ready = ReadySignal(vm, out Action handler);
        try
        {
            Task run = vm.GenerateAsync();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            vm.ConfirmPlan(false);
            await run.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal("RejectedByHuman", vm.Status);
            Assert.Null(vm.LastTxId);
            Assert.False(vm.IsPreviewVisible);
            Assert.Equal(countBefore, f.Session.Doc.Entities.Count);
            List<AuditRecord> records = f.Audit.ReadAll().ToList();
            Assert.Contains(records, r => r.Entry.Kind == "plan-rejected");
            Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-start");
            Assert.True(f.Audit.VerifyChain(out string error), error);
        }
        finally { vm.PreviewReady -= handler; }
    }

    [Fact]
    public async Task NoProfile_ErrorMessageGuidance_NoCrash()
    {
        using var f = new Fixture();
        // 不传 profileOverride 且项目无 llm-profiles.json → 引导 CLI llm add/use，不抛。
        PlanPreviewViewModel vm = f.MakeVm();

        await vm.GenerateAsync();

        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("llm", vm.ErrorMessage);
        Assert.False(vm.IsBusy);
        Assert.Empty(f.Audit.ReadAll()); // 未触达编排，无审计
    }

    [Fact]
    public async Task LocalOnlyProfile_CloudEndpoint_BlockedEgress()
    {
        using var f = new Fixture();
        // LocalOnly + 云端端点 → 出境闸在任何 LLM 调用之前阻止（orchestrator 既有路径）。
        var cloud = new ProviderProfile("cloud", "https://api.example.com/v1", "fake-model",
            VisionCapable: false, DataPolicy.LocalOnly, new ProfileLimits());
        PlanPreviewViewModel vm = f.MakeVm(new FakeLlmClient(PlanJson), cloud);

        await vm.GenerateAsync();

        Assert.Equal("BlockedEgress", vm.Status);
        Assert.Null(vm.LastTxId);
        List<AuditRecord> records = f.Audit.ReadAll().ToList();
        Assert.Contains(records, r => r.Entry.Kind == "egress-blocked");
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-start");
        Assert.DoesNotContain(records, r => r.Entry.Kind == "llm-call"); // 未触网
        Assert.True(f.Audit.VerifyChain(out string error), error);
    }

    [Fact]
    public async Task InvalidPlanJson_Failed_ErrorMessage()
    {
        using var f = new Fixture();
        PlanPreviewViewModel vm = f.MakeVm(new FakeLlmClient("抱歉，我无法生成计划。"), LocalProfile);
        int countBefore = f.Session.Doc.Entities.Count;

        await vm.GenerateAsync();

        Assert.Equal("Failed", vm.Status);
        Assert.False(string.IsNullOrEmpty(vm.ErrorMessage));
        Assert.False(vm.IsPreviewVisible);
        Assert.Equal(countBefore, f.Session.Doc.Entities.Count);
        List<AuditRecord> records = f.Audit.ReadAll().ToList();
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-start");
        Assert.DoesNotContain(records, r => r.Entry.Kind == "tx-commit");
        Assert.True(f.Audit.VerifyChain(out string error), error);
    }

    // ---- 最终审查 Imp#2：前奏（profile/key/semantic 加载）损坏配置不崩 UI ----

    [Fact]
    public async Task CorruptLlmProfiles_GracefulFailure_NoThrow()
    {
        using var f = new Fixture();
        // 垃圾字节 llm-profiles.json：LlmConfigStore.Load 抛 JsonException——
        // 前奏须在 try/catch 内 → ErrorMessage 诚实失败，绝不沿 async void 崩进程。
        File.WriteAllBytes(Path.Combine(f.AgentDir, "llm-profiles.json"), new byte[] { 0xFF, 0xFE, 0x00, 0x7B, 0x22 });
        PlanPreviewViewModel vm = f.MakeVm(); // 无 override → 走真实 Load 路径

        await vm.GenerateAsync(); // 不抛即为首要断言

        Assert.False(string.IsNullOrEmpty(vm.ErrorMessage));
        Assert.Equal("Failed", vm.Status);
        Assert.False(vm.IsBusy);
        Assert.Empty(f.Audit.ReadAll()); // 未触达编排，无审计
    }

    [Fact]
    public async Task CorruptSemanticOverlay_GracefulFailure_NoThrow()
    {
        using var f = new Fixture();
        // 垃圾字节 semantic.json：SemanticOverlay.Load 抛 JsonException（前奏段）。
        File.WriteAllBytes(Path.Combine(f.AgentDir, "semantic.json"), new byte[] { 0xFF, 0xFE, 0x00, 0x7B, 0x22 });
        PlanPreviewViewModel vm = f.MakeVm(new FakeLlmClient(PlanJson), LocalProfile);
        int countBefore = f.Session.Doc.Entities.Count;

        await vm.GenerateAsync();

        Assert.False(string.IsNullOrEmpty(vm.ErrorMessage));
        Assert.Equal("Failed", vm.Status);
        Assert.False(vm.IsBusy);
        Assert.Equal(countBefore, f.Session.Doc.Entities.Count);
        Assert.DoesNotContain(f.Audit.ReadAll(), r => r.Entry.Kind == "tx-start");
    }

    // ---- 最终审查 Imp#4：PlanExecuted 释闸后触发（刷新不再绕闸竞态） ----

    [Fact]
    public async Task PlanExecuted_FiresAfterSessionGateReleased()
    {
        using var f = new Fixture();
        var gate = new SemaphoreSlim(1, 1);
        var vm = new PlanPreviewViewModel(f.Session, f.ProjectDir, f.Audit,
            new FixedIdentity(new OperatorIdentity("tester", Role.Draw)),
            new RoleChecker(), gate, new FakeLlmClient(PlanJson), LocalProfile);
        // 事件触发瞬间闸必须已可用（WaitAsync(0) 确定性钉住释闸先于事件）；
        // 旧缺陷：持闸内触发 → WaitAsync(0) 失败。取得后立即归还，不干扰编排收尾。
        bool gateFreeAtEvent = false;
        void OnExecuted()
        {
            gateFreeAtEvent = gate.WaitAsync(0).GetAwaiter().GetResult();
            if (gateFreeAtEvent) gate.Release();
        }
        vm.PlanExecuted += OnExecuted;
        TaskCompletionSource ready = ReadySignal(vm, out Action handler);
        try
        {
            Task run = vm.GenerateAsync();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            vm.ConfirmPlan(true);
            await run.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal("Executed", vm.Status);
            Assert.True(gateFreeAtEvent, "PlanExecuted 触发时会话闸应已释放");
            Assert.Equal(1, gate.CurrentCount); // 闸完整归还（无泄漏）
        }
        finally
        {
            vm.PlanExecuted -= OnExecuted;
            vm.PreviewReady -= handler;
        }
    }
}
