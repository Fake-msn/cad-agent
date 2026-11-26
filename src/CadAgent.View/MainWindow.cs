using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CadAgent.Core;
using CadAgent.Data.Rules;
using CadAgent.Report;
using CadAgent.View.Controls;
using CadAgent.View.Rendering;
using CadAgent.View.Services;
using CadAgent.View.ViewModels;

namespace CadAgent.View;

/// <summary>
/// code-driven 主窗口（无 XAML）：T4 起为左栏图层面板（复选框可见性 + 色样 +
/// 计数；双击隔离；Show all 恢复）+ 中央 DrawingCanvas + 底部状态栏
/// （选中实体显示真实 DXF 句柄/类型/图层）。加载失败时展示 ErrorMessage。
/// 面板/状态栏通过 CanvasViewModel.RenderInvalidated 与画布同步刷新。
/// T5：右栏问题清单面板——Refresh 触发 in-process 规范检查（IssuesViewModel），
/// 行内效力徽章（Mandatory 红/Recommended 黄/Informational 灰）+ 规则 Id/条款/
/// 消息，展开显示原文摘录/建议/检测依据；点击行 → 画布高亮 Targets 句柄并居中；
/// 头部显示 mandatory 计数与跳过计数（跳过原因见头部 tooltip）。
/// T6：底部审计时间线——事务列表（短号/操作者/时间/commit ✓/failed ✗），
/// 选择事务 → 画布高亮 step 目标句柄 + 状态栏摘要（只读回放不改图）；
/// Diff 切换 → 快照前后对比叠加（before 灰/after 正常/added 绿/removed 红删除线），
/// 快照缺失等失败 → 行内错误 + 切换回退。无 audit.db → 空时间线（不创建库）。
/// T7：审计面板上方签认面板——事务选择 + 意见框 + Approve/Reject（Role>=Review
/// 才可点；自审拒绝行内报错）+ 每事务签认状态；签认写 signoffs.json + 审计
/// review-signed（身份默认 WindowsIdentityProvider + .cadagent/roles.yaml，
/// 可注入 IIdentityProvider 供测试）。
/// T8：左栏改为三 tab（图层/纠偏/裁定）。纠偏 tab：Scan → SemanticViewModel
/// 扫描（overlay.Save + 审计 semantic-scan），不明确图层列表带证据（计数/名称
/// 提示/样例句柄）+ 语义类下拉 + Map（HumanConfirmed 1.0 + 审计 semantic-map）
/// + Isolate（画布隐藏其他层）。裁定 tab：规则列表（builtin）+ 强制力覆盖下拉/
/// 豁免勾选 + 理由/解读框 + Apply（校验同 CLI，拒绝行内报错）；裁定成功 →
/// 问题清单自动刷新（AdjudicationsChanged → Issues.RefreshAsync 联动）。
/// 写审计：audit.db 存在 → 复用 T6 存储；缺失 → 首次写入惰性建库
/// （打开窗口仍不建库，沿用 T6 只读回放约定）。skill 目录接线留 T10。
/// T9：顶部编辑工具栏（Select/Line/Text/Move/Delete/ChangeLayer/InsertBlock/Undo
/// + 文本/块名输入 + 目标层下拉）。**所有编辑经 EditViewModel→StepEngine——
/// 无直接 Doc 修改、无审计旁路**（view-edit 请求审计先于 tx）。Line 两点点击
/// （第一击画布预览锚点）；Text/InsertBlock 工具栏输入+点击；Move 选中+目标
/// 点击（dx/dy 相对图元首点）；Delete 选中后再次点击工具栏按钮确认；
/// ChangeLayer 选中+层下拉。编辑/撤销完成 → RenderModelBuilder 重建 →
/// Canvas.Refresh + Layers.Refresh（T4 承接）+ 状态栏计数。会话闸
/// （SemaphoreSlim）共享给编辑与后台 session 读（diff/check/scan）；
/// UI 层编辑期间禁用 Diff 切换、diff 加载期间禁用工具栏（互斥）。
/// T10：编辑重入硬闸（_editBusy 标志——在途编辑期间画布点击/删除确认/撤销/
/// 改层一律忽略，不依赖调度时序）；--skill 经 MainViewModel.SkillDirs 接线
/// Issues/Adjudication/Semantic；状态栏补缩放倍率；工具栏全按钮 tooltip；
/// 窗口标题 CadAgent — 图纸名；徽章深红/深灰 WCAG 修正（见 IssueEntry.ForceBrush）。
/// 最终审查修复波：①编辑边界 Red 准入闸（EditViewModel 镜像 CLI RunPlan）；
/// ②驳回签认→快照回滚（RollbackTx→EditViewModel.UndoAsync，仅已提交 tx 可签认）；
/// ③状态文件损坏优雅降级（audit.db/roles.yaml/signoffs.json → ErrorMessage/
/// StateNote 提示，窗口照常打开、图纸照常渲染）；SetActiveTool 补 _editBusy 硬闸。
/// 视图闭环 T5：审计时间线 live-rebuild（RefreshAfterEdit → Audit.Rebuild →
/// TimelineChanged 刷新列表，编辑/撤销后无需重开窗口）；_diffBusy 硬闸
/// （diff 加载期间画布点击/工具切换/撤销/删除/改层一律忽略，镜像 _editBusy）。
/// 视图闭环 T9：编辑 UX 收尾——字高/缩放/旋转°输入框（非法输入 fallback
/// 3.5/1/0；旋转 UI 度→handler 弧度）；Delete 第二击经 ConfirmDialog 模态确认
/// （取消保持选中不清状态；headless 测试经 ConfirmDeleteOverride seam 注入，
/// 真模态走 Task 11 真机协议）。删除仍经 EditViewModel→StepEngine，无审计旁路。
/// 视图闭环 T10：④纠偏计划预览面板（左栏"计划"tab，spec §4.1④）——意图→
/// AgentOrchestrator（Core 零改动，confirmPlan/confirmCloudEgress 回调即预览钩子）
/// →人工确认→StepEngine 执行（无审计旁路）；首次云端出境同意提示；执行成功→
/// 全视图刷新 + 问题清单重评 + 审计 live-rebuild。编排期间会话闸粗粒度持有
/// （编辑/问题刷新在闸上阻塞——计划明示）。
/// 幅面感知 T5 修复波：①布局幅面只读硬闸（_layoutSheetActive 结构性标志——编辑
/// 工具激活/画布点击/删除/撤销/改层一律拒绝，纸面坐标绝不进模型空间；状态栏只读
/// 注记 + EditError 引导；不做纸→模型逆映射，deferred）；②OnSheetSelected 补
/// _editBusy 硬闸（在途编辑期间幅面不切换，selector 索引恢复，防 torn read）。
/// §4.1④ 勾选→意图补全（Imp#5 deferred 闭合）：问题行 CheckBox（TwoWay IsChecked）
/// → 填充意图勾选项优先（"{RuleId} [{Force}]: {Suggestion}"）+ 零勾选 mandatory
/// 兜底（旧格式保持）+ recommended 采纳审计 rule-adopted（Source=rule，经既有
/// writeAudit/signoffIdentity 同源实例，无旁路）；忽略=未勾选正常决策不留痕。
/// 天读证据（只读）：左栏第五 tab——DeepReadViewModel 从 .cadagent/semantic.json
/// 重建五区（原生参数/炸开证据/符号候选/块定义/证据状态）+ 中心线叠加开关
/// （Canvas.ShowCenterlines，有中心线才可勾选）；a/b/e 区行点击 → Canvas.SelectByHandle
/// 定位；未命中（TDb 实体仅存在于深读证据）→ 清画布选中/高亮 + 状态栏提示
/// （下一次成功定位/选中接管清除）。块定义区来源哈希不一致 → 行内明示"图纸可能
/// 已编辑或来自他图"（tooltip 同口径），哈希缺失 → "—"（不猜）。
/// 刷新=初始 / 语义扫描·映射完成（SemanticChanged）/ 编辑后 RefreshAfterEdit；
/// 面板只读（不写审计、不落盘），overlay 损坏 → 行内错误 + 五区清空。
/// </summary>
public sealed class MainWindow : Window
{
    /// <summary>纠偏下拉可选语义类（Unknown 不是确认目标，排除）。</summary>
    private static readonly string[] ClassOptions =
        Enum.GetNames<SemanticClass>().Where(n => n != nameof(SemanticClass.Unknown)).ToArray();

    /// <summary>T9 文本工具默认字高（GB 50001 字高系列常用值；视图闭环 T9 起
    /// 字高输入框可覆盖，非法/≤0 输入回落此值）。</summary>
    private const double DefaultTextHeight = 3.5;

    /// <summary>T9 工具点击命中容差（设备像素，与 DrawingCanvas.PickTolerancePixels 同口径）。</summary>
    private const double PickTolerancePixels = 5;

    private string _baseStatus = "";
    private string _diffSummary = "";
    private TextBlock? _semanticHeader;

    /// <summary>天读证据五区小节名（与 <see cref="_deepReadSectionHeaders"/> 同序）。</summary>
    private static readonly string[] DeepReadSectionNames =
        { "原生参数", "炸开证据", "符号候选", "块定义", "证据状态" };

    /// <summary>天读证据五区小节标题（含计数；随 Reload 刷新；与 DeepReadSectionNames 同序）。</summary>
    private readonly TextBlock[] _deepReadSectionHeaders = new TextBlock[5];

    /// <summary>T10：--skill 目录（MainViewModel 承载）——Issues/Adjudication/Semantic 共用。</summary>
    private readonly IReadOnlyList<string> _skillDirs;

    /// <summary>
    /// T10 编辑重入闸（结构性保证，不依赖调度时序）：RunEditAsync 置位、完成复位；
    /// 画布工具点击/删除确认/撤销/改层在位期间一律忽略——工具栏 IsEnabled 只是
    /// 视觉提示，RaiseEvent/画布点击可绕过禁用态，标志位才是硬闸。
    /// </summary>
    private bool _editBusy;

    /// <summary>视图闭环 T7：最近一次编辑的非空 Warning（Yellow 闸口警示 / run-regression
    /// 回归警告）——拼入状态栏尾部；新编辑/撤销/切换工具时清除，避免陈旧警示误导。</summary>
    private string _editWarning = "";

    /// <summary>
    /// Diff 加载重入闸（视图闭环 T5，镜像 _editBusy 结构性保证）：ApplyDiffAsync
    /// 置位、完成复位；Diff 加载期间画布点击/工具切换/撤销/删除/改层一律忽略——
    /// IsEnabled 只是视觉提示，RaiseEvent/画布点击可绕过禁用态，标志位才是硬闸。
    /// </summary>
    private bool _diffBusy;

    /// <summary>T9 Move 工具：选中图元的首点（dx/dy 基准）；目标点击后清除。</summary>
    private (double X, double Y)? _moveBase;

    /// <summary>最终审查顺手修（T10 deferred）：预览确认/拒绝按钮的轮次闸。
    /// PreviewReady 处理时置位（启用按钮）；确认/拒绝点击后立即复位（禁用）——
    /// 防纠正循环二次 confirmPlan 时，用户在下一轮 PreviewReady 的 Post 处理前
    /// 点击确认（确认到尚未显示的旧计划）。仅 PreviewReady 重新启用，RunCompleted 不复位。</summary>
    private bool _planConfirmPending;

    // ===== 幅面感知 T5：视图幅面选择 =====
    /// <summary>图纸文档（布局视口查找用）。</summary>
    private ACadSharp.CadDocument _doc = null!;
    /// <summary>图纸会话（视图闭环 T8：Layers.Refresh 图层表并集——空图层显示；PR#20 合并接线）。</summary>
    private CadAgent.Data.DrawingSession _session = null!;
    /// <summary>全模型渲染图元（模型幅面按 FrameBounds 锚点裁剪自此）。</summary>
    private IReadOnlyList<RenderItem> _fullModelItems = Array.Empty<RenderItem>();
    /// <summary>幅面登记簿（模型幅面在前，布局幅面在后）。</summary>
    private IReadOnlyList<SheetRecord> _sheets = Array.Empty<SheetRecord>();
    /// <summary>当前选中幅面 Id/名（编辑重建后按 Id 保持选择；状态栏幅面名）。</summary>
    private string? _currentSheetId;
    private string _currentSheetName = "";
    /// <summary>画布控件引用（切换幅面后按新内容 bounds 重新 Fit）。</summary>
    private DrawingCanvas? _canvasControl;
    /// <summary>程序性刷新幅面下拉时抑制 SelectionChanged（避免编辑重建二次刷新/重 Fit）。</summary>
    private bool _suppressSheetSelection;
    /// <summary>
    /// 修复①：布局幅面只读标志（结构性硬闸，非仅 IsEnabled——RaiseEvent/画布点击
    /// 可绕过禁用态）。布局幅面激活期间编辑工具一律硬禁：纸面坐标绝不提交进模型
    /// 空间 Doc.Entities（静默损坏）；不做纸→模型逆映射（纸面编辑非本期语义，deferred）。
    /// Select 只读选择路径不受影响。
    /// </summary>
    private bool _layoutSheetActive;
    /// <summary>修复①：布局幅面只读引导文本（EditError 通道；状态栏另有只读注记）。</summary>
    private const string LayoutReadOnlyMessage = "布局幅面为只读视图，编辑请切回模型幅面";
    /// <summary>窗口标题图纸名（状态栏 _baseStatus 重建用）。</summary>
    private string _title = "";

    /// <summary>§4.1④ 采纳审计存储（构造期 writeAudit 同源实例：Issues/Edit/Adjudication
    /// 共用的 T8 写审计——audit.db 存在复用/缺失惰性建库）；加载失败短路分支为 null。</summary>
    private readonly IAuditStore? _writeAudit;

    /// <summary>§4.1④ 采纳审计身份（构造期 signoffIdentity 同源：签认/编辑/裁定共用）。</summary>
    private readonly IIdentityProvider? _auditIdentity;

    /// <summary>审图报告导出：与 Issues/编辑/签认共用写审计实例（无第二存储）。</summary>
    private readonly IAuditStore? _reportAuditStore;

    /// <summary>审图报告导出：签认记录来源（导出前 Load 刷新磁盘态）。</summary>
    private ReviewSignoffStore? _signoffStore;

    /// <summary>审图报告导出重入闸（结构性，不依赖按钮 IsEnabled——RaiseEvent 可绕过）。</summary>
    private bool _reportBusy;

    /// <summary>项目目录（报告默认输出 &lt;项目&gt;/reports；报告元数据）。</summary>
    private readonly string _projectDir;

    /// <summary>会话闸（报告取图纸哈希与编辑/后台读互斥）。</summary>
    private readonly SemaphoreSlim? _sessionGate;

    /// <summary>§4.1④ 零勾选兜底状态栏提示（镜像 _editWarning 机制：OnCanvasInvalidated
    /// 拼接待显文本；checked 路径/新填充动作清除，避免陈旧提示误导）。</summary>
    private string _fillIntentNote = "";

    /// <summary>行点击定位失败提示文案：句柄只存在于深读证据（TDb 实体不在渲染图元）。</summary>
    internal const string LocateMissNote = "该句柄不在当前渲染图元中（TDb 实体仅存在于深读证据）";

    /// <summary>天读证据行点击定位失败提示（镜像 _editWarning 机制：OnCanvasInvalidated
    /// 拼接到状态栏尾部；下一次成功定位/选中态接管时清除，避免陈旧提示误导）。</summary>
    private string _deepReadLocateNote = "";

    public MainWindow(MainViewModel vm, IIdentityProvider? identity = null, SemaphoreSlim? sessionGate = null,
        PlanPreviewViewModel? planPreview = null)
    {
        DataContext = vm;
        _skillDirs = vm.SkillDirs;
        _projectDir = vm.ProjectDir;
        Title = $"CadAgent — {vm.Title}"; // T10：窗口标题含应用名 + 图纸名
        Width = 1200;
        Height = 800;

        if (vm.ErrorMessage is not null)
        {
            Content = new TextBlock
            {
                Margin = new Thickness(16),
                TextWrapping = TextWrapping.Wrap,
                Text = vm.ErrorMessage
            };
            return;
        }

        // T5：全模型渲染 + 幅面登记簿；默认首幅面（R4 sanctioned：模型幅面按框内锚点裁剪，
        // 布局幅面经 BuildViewport 纸面映射）。单幅面/无布局图纸 bounds=ModelBounds → 全保留（legacy 无感）。
        _title = vm.Title;
        _doc = vm.Session.Doc;
        _session = vm.Session;
        _fullModelItems = RenderModelBuilder.Build(vm.Session);
        _sheets = vm.Session.Sheets;
        SheetRecord firstSheet = _sheets[0];
        _currentSheetId = firstSheet.SheetId;
        _currentSheetName = firstSheet.Name;
        _layoutSheetActive = firstSheet.LayoutName is not null; // 修复①：只读标志与幅面同步
        IReadOnlyList<RenderItem> items = ItemsForSheet(firstSheet);
        Canvas = new CanvasViewModel(items);
        // T8：传 session → 图层面板 = renderItems ∪ 图层表（空图层计数 0+表色样）。
        var layers = new LayersViewModel(items, Canvas, vm.Session);
        Layers = layers;

        // T9 会话闸：编辑/撤销（EditViewModel）与后台 session 读（diff/check/scan）
        // 共享同一互斥闸——off-thread 读绝不看到被编辑撕扯的 Doc（T6 承接）。
        // T10：可注入（headless 测试持闸制造确定性 in-flight 编辑）。
        sessionGate ??= new SemaphoreSlim(1, 1);
        _sessionGate = sessionGate;

        _baseStatus = $"{vm.Title} — {items.Count} render items";
        var status = new TextBlock { Margin = new Thickness(8, 4), Text = $"{_baseStatus} | Zoom {Canvas.Zoom:0.##}x | 幅面 {_currentSheetName}" };
        Status = status;

        var showAll = new Button { Content = "Show all", Margin = new Thickness(4, 2) };
        showAll.Click += (_, _) => layers.ShowAll();

        var layerList = new ListBox
        {
            ItemsSource = layers.Layers,
            ItemTemplate = new FuncDataTemplate<LayerEntry>((entry, _) =>
                entry is null ? new Control() : BuildLayerRow(entry, layers))
        };
        layerList.DoubleTapped += OnLayerDoubleTapped;
        LayerList = layerList;

        var header = new TextBlock
        {
            Text = "Layers",
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(6, 4)
        };
        var panel = new DockPanel { Width = 220 };
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        DockPanel.SetDock(showAll, Dock.Top);
        panel.Children.Add(showAll);
        panel.Children.Add(layerList); // LastChildFill：列表占满面板其余区域

        // T5：右栏问题清单 UI（VM 构造移至 writeAudit/signoffIdentity 声明之后——
        // 视图闭环 T6：Issues 刷新留 check-run 审计需要两者）。
        var issuesHeader = new TextBlock
        {
            Text = "Issues",
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(6, 4),
            TextWrapping = TextWrapping.Wrap
        };
        IssuesHeader = issuesHeader;

        var refresh = new Button { Content = "Refresh", Margin = new Thickness(4, 2) };
        refresh.Click += OnRefreshIssuesClick;
        RefreshIssuesButton = refresh;

        var issuesError = new TextBlock
        {
            Margin = new Thickness(6, 2),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.IndianRed,
            IsVisible = false
        };
        IssuesError = issuesError;

        var issueList = new ListBox
        {
            ItemTemplate = new FuncDataTemplate<IssueEntry>((entry, _) =>
                entry is null ? new Control() : BuildIssueRow(entry))
        };
        issueList.SelectionChanged += OnIssueSelected;
        IssueList = issueList;

        // 审图报告导出（全量）：与 CLI report export 共用 ReviewReportExporter；
        // 导出前重评问题清单（留 check-run 审计），成功后追加 report-exported 审计。
        var exportReport = new Button { Content = "导出报告", Margin = new Thickness(4, 2) };
        exportReport.Click += OnExportReportClick;
        ExportReportButton = exportReport;
        var reportStatus = new TextBlock
        {
            Margin = new Thickness(6, 2),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.MediumSeaGreen,
            IsVisible = false
        };
        ReportStatus = reportStatus;

        var issuesPanel = new DockPanel { Width = 320 };
        DockPanel.SetDock(issuesHeader, Dock.Top);
        issuesPanel.Children.Add(issuesHeader);
        DockPanel.SetDock(refresh, Dock.Top);
        issuesPanel.Children.Add(refresh);
        DockPanel.SetDock(exportReport, Dock.Top);
        issuesPanel.Children.Add(exportReport);
        DockPanel.SetDock(reportStatus, Dock.Top);
        issuesPanel.Children.Add(reportStatus);
        DockPanel.SetDock(issuesError, Dock.Top);
        issuesPanel.Children.Add(issuesError);
        issuesPanel.Children.Add(issueList); // LastChildFill

        // T6：底部审计时间线（只读回放）。audit.db 不存在 → 空存储占位，不创建库文件。
        // 最终审查修复③：损坏 audit.db → 降级空时间线 + 提示（窗口仍打开，图纸仍渲染）。
        string agentDir = Path.Combine(vm.ProjectDir, ".cadagent");
        string auditDbPath = Path.Combine(agentDir, "audit.db");
        var stateNotes = new List<string>();
        // 防假 Green 切片 T3：census 警示——未解析天正对象不进入渲染模型（无句柄/几何），
        // 显示不完整；修改操作已由 EditViewModel 准入闸全图级拦截（只读审查）。
        if (vm.Session.Unparsed.TotalCount > 0)
            stateNotes.Add($"含 {vm.Session.Unparsed.TotalCount} 个未解析天正对象，显示不完整（只读审查）");
        IAuditStore auditStore;
        if (!File.Exists(auditDbPath))
        {
            // Fix Round 1：创建前 ReadAll 空库语义等价 EmptyAuditStore；与 writeAudit
            // 同实例 → 首写后 live-rebuild 可见（首 tx 无需重开窗口）。打开窗口不建库
            // 约定不变——Ensure 仅首 Append 触发。
            auditStore = new LazyWriteAuditStore(auditDbPath);
        }
        else
        {
            try
            {
                auditStore = new SqliteAuditStore(auditDbPath);
            }
            catch (Exception ex)
            {
                auditStore = new EmptyAuditStore();
                stateNotes.Add($"审计库损坏，时间线不可用（{ex.Message}）");
            }
        }
        var audit = new AuditViewModel(auditStore,
            new SnapshotStore(Path.Combine(agentDir, "snapshots")), vm.Session, Canvas, sessionGate);
        Audit = audit;

        var auditHeader = new TextBlock
        {
            Text = "Audit",
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(6, 4),
            VerticalAlignment = VerticalAlignment.Center
        };
        var diffToggle = new ToggleButton { Content = "Diff", Margin = new Thickness(4, 2) };
        // IsCheckedChanged 在值翻转后触发（Click 时序不保证 Toggle 先行）。
        diffToggle.IsCheckedChanged += OnDiffCheckedChanged;
        DiffToggle = diffToggle;

        var auditError = new TextBlock
        {
            Margin = new Thickness(6, 2),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.IndianRed,
            IsVisible = false
        };
        AuditError = auditError;

        var auditList = new ListBox
        {
            ItemsSource = audit.Transactions,
            ItemTemplate = new FuncDataTemplate<TxGroup>((group, _) =>
                group is null ? new Control() : BuildTxRow(group))
        };
        auditList.SelectionChanged += OnTxSelected;
        AuditList = auditList;
        // 视图闭环 T5：Rebuild 完成（成功或失败）→ live 刷新事务列表（无需重开窗口）。
        audit.TimelineChanged += () =>
        {
            if (AuditList is not null && Audit is not null)
                AuditList.ItemsSource = Audit.Transactions;
        };

        var auditTopRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        auditTopRow.Children.Add(auditHeader);
        auditTopRow.Children.Add(diffToggle);
        if (audit.ErrorMessage is not null)
        {
            auditError.Text = audit.ErrorMessage;
            auditError.IsVisible = true;
        }

        var auditPanel = new DockPanel { Height = 160 };
        DockPanel.SetDock(auditTopRow, Dock.Top);
        auditPanel.Children.Add(auditTopRow);
        DockPanel.SetDock(auditError, Dock.Top);
        auditPanel.Children.Add(auditError);
        auditPanel.Children.Add(auditList); // LastChildFill

        // T7：审查签认面板（审计面板上方）。身份默认 Windows 账号 + roles.yaml；
        // 测试可注入 FixedIdentity。Draw 角色 → 按钮禁用；自审 → 行内拒绝信息。
        // 最终审查修复③：roles.yaml 损坏 → 默认 Draw 角色 + 提示；signoffs.json 损坏 →
        // 空签认存储 + 提示（后续 Save 原子写覆盖修复）。窗口构造绝不因状态文件损坏崩溃。
        IIdentityProvider signoffIdentity;
        if (identity is not null)
        {
            signoffIdentity = identity;
        }
        else
        {
            RoleConfig roleConfig;
            try
            {
                roleConfig = RoleConfig.Load(Path.Combine(agentDir, "roles.yaml"));
            }
            catch (Exception ex)
            {
                roleConfig = RoleConfig.Parse("roles: {}"); // 全员默认 Draw
                stateNotes.Add($"roles.yaml 损坏，已降级为默认 Draw 角色（{ex.Message}）");
            }
            signoffIdentity = new WindowsIdentityProvider(roleConfig);
        }
        string signoffPath = Path.Combine(agentDir, "signoffs.json");
        ReviewSignoffStore signoffStore;
        try
        {
            signoffStore = new ReviewSignoffStore(signoffPath);
        }
        catch (Exception ex)
        {
            signoffStore = new ReviewSignoffStore(signoffPath, loadOnConstruct: false);
            stateNotes.Add($"signoffs.json 损坏，签认历史不可用（{ex.Message}）");
        }
        _signoffStore = signoffStore;
        var signoff = new SignoffViewModel(auditStore, signoffStore,
            signoffIdentity, new RoleChecker(), audit.Transactions);
        Signoff = signoff;
        signoff.SignoffsChanged += UpdateSignoffStatus;

        var signoffHeader = new TextBlock
        {
            Text = "Signoff",
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(6, 4),
            VerticalAlignment = VerticalAlignment.Center
        };
        var txSelector = new ComboBox
        {
            ItemsSource = audit.Transactions,
            Width = 300,
            Margin = new Thickness(4, 2),
            ItemTemplate = new FuncDataTemplate<TxGroup>((group, _) =>
                group is null ? new Control() : new TextBlock
                {
                    Text = $"{(group.TxId.Length > 8 ? group.TxId[..8] : group.TxId)} · {group.Operator} · {group.Status}"
                })
        };
        txSelector.SelectionChanged += OnSignoffTxSelected;
        SignoffTxSelector = txSelector;

        var comment = new TextBox { Watermark = "Comment", Width = 240, Margin = new Thickness(4, 2) };
        SignoffComment = comment;

        var approve = new Button { Content = "Approve", Margin = new Thickness(4, 2), IsEnabled = signoff.CanSign };
        approve.Click += (_, _) => _ = SignCoreAsync(approved: true);
        ApproveButton = approve;
        var reject = new Button { Content = "Reject", Margin = new Thickness(4, 2), IsEnabled = signoff.CanSign };
        reject.Click += (_, _) => _ = SignCoreAsync(approved: false);
        RejectButton = reject;

        var signoffStatus = new TextBlock { Margin = new Thickness(6, 2) };
        SignoffStatus = signoffStatus;
        var signoffError = new TextBlock
        {
            Margin = new Thickness(6, 2),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.IndianRed,
            IsVisible = false
        };
        SignoffError = signoffError;

        var signoffTopRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        signoffTopRow.Children.Add(signoffHeader);
        signoffTopRow.Children.Add(txSelector);
        signoffTopRow.Children.Add(comment);
        signoffTopRow.Children.Add(approve);
        signoffTopRow.Children.Add(reject);

        var signoffPanel = new DockPanel { Height = 96 };
        DockPanel.SetDock(signoffTopRow, Dock.Top);
        signoffPanel.Children.Add(signoffTopRow);
        DockPanel.SetDock(signoffError, Dock.Top);
        signoffPanel.Children.Add(signoffError);
        signoffPanel.Children.Add(signoffStatus); // LastChildFill

        // T8：写审计存储——audit.db 存在 → 复用 T6 存储；缺失 → 惰性建库占位
        // （首次 Append 才创建 .cadagent/audit.db；打开窗口不建库，沿用 T6 约定）。
        // Fix Round 1：无库分支 auditStore 已是 LazyWriteAuditStore → 复用同实例
        // （读/写同源，首 tx 后 Audit.Rebuild 即可见）；损坏降级分支仍另建惰性占位。
        IAuditStore writeAudit = auditStore is SqliteAuditStore or LazyWriteAuditStore
            ? auditStore
            : new LazyWriteAuditStore(auditDbPath);
        // §4.1④：采纳审计（rule-adopted）经同一写审计实例 + 同一身份（无第二存储/旁路）。
        _writeAudit = writeAudit;
        _auditIdentity = signoffIdentity;
        _reportAuditStore = writeAudit; // 报告导出审计同源（审计失败→撤销报告）

        // T5：右栏问题清单 VM（in-process check；T10：--skill 目录经 MainViewModel 接线）。
        // 视图闭环 T6：刷新留 check-run 审计（parity CLI check）——写审计复用 T8 存储，
        // 身份与签认同源；构造位于 writeAudit/signoffIdentity 声明之后。
        var issues = new IssuesViewModel(vm.Session, vm.ProjectDir, _skillDirs, Canvas, sessionGate,
            audit: writeAudit, identity: signoffIdentity);
        Issues = issues;
        issues.IssuesChanged += UpdateIssuesPanel;

        // T9：手动编辑视图模型——所有编辑经 StepEngine（无旁路）；写审计复用 T8
        // 存储；身份/角色与签认同源；语义闸口由 VM 按 overlay 存在性注入（镜像 CLI）。
        var edit = new EditViewModel(vm.Session, writeAudit,
            new SnapshotStore(Path.Combine(agentDir, "snapshots")),
            signoffIdentity, new RoleChecker(), vm.ProjectDir, sessionGate: sessionGate);
        Edit = edit;
        edit.EditCompleted += () => RefreshAfterEdit(vm);
        // 最终审查修复②：驳回签认 → 快照回滚（spec §4.1⑥/§5.4）。回调走
        // EditViewModel.UndoAsync（StepEngine.Rollback，无旁路）；UndoAsync 自身
        // 不抛出（失败落 ErrorMessage），这里转成异常供签认 VM 记 ErrorMessage
        // （签认记录已先行落盘，不回撤）。回滚完成触发 EditCompleted → 视图重建。
        signoff.RollbackTx = async txId =>
        {
            await edit.UndoAsync(txId);
            if (edit.ErrorMessage is not null)
                throw new InvalidOperationException(edit.ErrorMessage);
        };

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(4, 2) };
        EditToolbar = toolbar;
        // T5：幅面选择下拉（入工具栏顶栏，不动主 DockPanel 子序——LastChildFill 教训）。
        // items=幅面名+SheetId；默认首幅面（初始 items 已按首幅面构建，SelectedIndex 先于事件接线）。
        var sheetSelector = new ComboBox
        {
            ItemsSource = _sheets,
            Width = 220,
            Margin = new Thickness(2, 0),
            [ToolTip.TipProperty] = "选择幅面：模型幅面按图框裁剪，布局幅面按视口纸面映射",
            ItemTemplate = new FuncDataTemplate<SheetRecord>((s, _) => s is null ? new Control() : new TextBlock
            {
                Text = $"{s.Name} · {s.SheetId}"
            })
        };
        sheetSelector.SelectedIndex = 0;
        sheetSelector.SelectionChanged += OnSheetSelected;
        SheetSelector = sheetSelector;
        toolbar.Children.Add(sheetSelector);
        Button Tool(string label, EditTool tool, string tip)
        {
            var b = new Button { Content = label, Margin = new Thickness(2, 0), [ToolTip.TipProperty] = tip };
            b.Click += (_, _) => SetActiveTool(tool);
            toolbar.Children.Add(b);
            return b;
        }
        Tool("Select", EditTool.Select, "选择：点击选中实体（默认工具）");
        Tool("Line", EditTool.Line, "画线：第一击设起点，第二击提交（经审计管线）");
        Tool("Text", EditTool.Text, "文字：先在工具栏输入内容，再点击放置（字高取输入框，默认 3.5）");
        Tool("Move", EditTool.Move, "移动：点击实体选中，再点击目标点（相对首点位移）");
        // Delete 两段式：首按激活工具 → 画布点击选中 → 再按本按钮确认删除。
        var delete = new Button
        {
            Content = "Delete",
            Margin = new Thickness(2, 0),
            [ToolTip.TipProperty] = "选中实体后再次点击确认删除"
        };
        delete.Click += (_, _) => _ = OnDeleteClickAsync();
        toolbar.Children.Add(delete);
        Tool("ChangeLayer", EditTool.ChangeLayer, "改层：点击实体选中，再从层下拉选择目标层");
        Tool("InsertBlock", EditTool.InsertBlock, "插块：先在工具栏输入块名，再点击放置");
        var undo = new Button
        {
            Content = "Undo",
            Margin = new Thickness(2, 0),
            IsEnabled = false,
            [ToolTip.TipProperty] = "撤销最近一次编辑事务（StepEngine.Rollback，审计留痕）"
        };
        undo.Click += OnUndoClick;
        toolbar.Children.Add(undo);
        UndoButton = undo;

        var textInput = new TextBox { Watermark = "Text content", Width = 140, Margin = new Thickness(2, 0) };
        EditTextInput = textInput;
        toolbar.Children.Add(textInput);
        var blockInput = new TextBox { Watermark = "Block name", Width = 120, Margin = new Thickness(2, 0) };
        EditBlockInput = blockInput;
        toolbar.Children.Add(blockInput);
        // 视图闭环 T9：字高/缩放/旋转°输入（镜像 textInput/blockInput 装配模式）。
        var heightInput = new TextBox { Watermark = "字高", Width = 60, Margin = new Thickness(2, 0), Text = "3.5" };
        TextHeightInput = heightInput;
        toolbar.Children.Add(heightInput);
        var scaleInput = new TextBox { Watermark = "缩放", Width = 60, Margin = new Thickness(2, 0), Text = "1" };
        BlockScaleInput = scaleInput;
        toolbar.Children.Add(scaleInput);
        var rotationInput = new TextBox { Watermark = "旋转°", Width = 60, Margin = new Thickness(2, 0), Text = "0" };
        BlockRotationInput = rotationInput;
        toolbar.Children.Add(rotationInput);
        var layerSelector = new ComboBox
        {
            ItemsSource = layers.Layers.Select(l => l.Name).ToList(),
            PlaceholderText = "layer",
            Width = 120,
            Margin = new Thickness(2, 0)
        };
        layerSelector.SelectionChanged += OnEditLayerSelected;
        EditLayerSelector = layerSelector;
        toolbar.Children.Add(layerSelector);
        var editError = new TextBlock
        {
            Margin = new Thickness(6, 2),
            Foreground = Brushes.IndianRed,
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center
        };
        EditError = editError;
        toolbar.Children.Add(editError);

        Canvas.ToolClickHandler = OnCanvasToolClick;

        // T8 纠偏 tab：Scan → 不明确图层列表（证据 + 类下拉 + Map + Isolate）。
        // T10：--skill 别名经 ScanAsync(_skillDirs) 接线（别名命中层不再不明确）。
        var semantic = new SemanticViewModel(vm.Session, vm.ProjectDir, Canvas, writeAudit, signoffIdentity,
            sessionGate: sessionGate);
        Semantic = semantic;
        semantic.SemanticChanged += UpdateSemanticPanel;
        // 天读证据：扫描/映射落盘后重读 overlay（DeepRead 在下方构造——事件首触发前已就绪）。
        semantic.SemanticChanged += ReloadDeepRead;

        var semanticHeader = new TextBlock
        {
            Text = "语义纠偏",
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(6, 4),
            TextWrapping = TextWrapping.Wrap
        };
        _semanticHeader = semanticHeader;
        var scan = new Button { Content = "Scan", Margin = new Thickness(4, 2) };
        scan.Click += OnScanClick;
        SemanticScanButton = scan;

        var semanticError = new TextBlock
        {
            Margin = new Thickness(6, 2),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.IndianRed,
            IsVisible = false
        };
        SemanticError = semanticError;

        var ambiguousList = new ListBox
        {
            ItemTemplate = new FuncDataTemplate<AmbiguousLayer>((a, _) =>
                a is null ? new Control() : BuildAmbiguousRow(a))
        };
        AmbiguousList = ambiguousList;

        var semanticPanel = new DockPanel();
        DockPanel.SetDock(semanticHeader, Dock.Top);
        semanticPanel.Children.Add(semanticHeader);
        DockPanel.SetDock(scan, Dock.Top);
        semanticPanel.Children.Add(scan);
        DockPanel.SetDock(semanticError, Dock.Top);
        semanticPanel.Children.Add(semanticError);
        semanticPanel.Children.Add(ambiguousList); // LastChildFill

        // T8 裁定 tab：规则列表 + 强制力覆盖/豁免 + 理由/解读 + Apply；
        // 裁定成功 → 问题清单自动刷新（事件联动，无需手动 Refresh）。
        var adjudication = new AdjudicationViewModel(vm.ProjectDir, _skillDirs, writeAudit, signoffIdentity);
        Adjudication = adjudication;
        adjudication.AdjudicationsChanged += () =>
        {
            UpdateAdjudicationPanel();
            _ = issues.RefreshAsync(); // 联动：裁定生效 → 问题清单重评
        };

        var adjudicationHeader = new TextBlock
        {
            Text = "规则裁定",
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(6, 4)
        };
        var ruleList = new ListBox
        {
            ItemsSource = adjudication.Rules,
            Height = 160,
            ItemTemplate = new FuncDataTemplate<Rule>((r, _) =>
                r is null ? new Control() : new TextBlock
                {
                    Text = $"{r.Id} · {r.Force} · {r.Source.Clause}",
                    Margin = new Thickness(4, 2),
                    TextWrapping = TextWrapping.Wrap
                })
        };
        AdjudicationRuleList = ruleList;

        var forceSelector = new ComboBox
        {
            ItemsSource = new[] { "(default)", "Mandatory", "Recommended", "Informational" },
            SelectedIndex = 0,
            Width = 150,
            Margin = new Thickness(4, 2)
        };
        ForceOverrideSelector = forceSelector;
        var exempt = new CheckBox { Content = "Exempt", Margin = new Thickness(4, 2) };
        ExemptCheck = exempt;
        var forceRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        forceRow.Children.Add(forceSelector);
        forceRow.Children.Add(exempt);

        var reason = new TextBox { Watermark = "Reason (必填)", Margin = new Thickness(4, 2) };
        AdjudicationReason = reason;
        var interpretation = new TextBox { Watermark = "Interpretation", Margin = new Thickness(4, 2) };
        AdjudicationInterpretation = interpretation;
        var apply = new Button { Content = "Apply", Margin = new Thickness(4, 2) };
        apply.Click += OnAdjudicateClick;
        AdjudicationApplyButton = apply;

        var adjudicationError = new TextBlock
        {
            Margin = new Thickness(6, 2),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.IndianRed,
            IsVisible = false
        };
        AdjudicationError = adjudicationError;

        var adjudicationList = new ListBox
        {
            ItemsSource = adjudication.Adjudications,
            ItemTemplate = new FuncDataTemplate<Adjudication>((a, _) =>
                a is null ? new Control() : new TextBlock
                {
                    Text = $"{a.RuleId} → {(a.Exempt ? "exempt" : a.ForceOverride?.ToString() ?? "default")} · {a.Reason}",
                    Margin = new Thickness(4, 2),
                    TextWrapping = TextWrapping.Wrap
                })
        };
        AdjudicationList = adjudicationList;

        var adjudicationControls = new StackPanel { Spacing = 2 };
        adjudicationControls.Children.Add(forceRow);
        adjudicationControls.Children.Add(reason);
        adjudicationControls.Children.Add(interpretation);
        adjudicationControls.Children.Add(apply);

        var adjudicationPanel = new DockPanel();
        DockPanel.SetDock(adjudicationHeader, Dock.Top);
        adjudicationPanel.Children.Add(adjudicationHeader);
        DockPanel.SetDock(ruleList, Dock.Top);
        adjudicationPanel.Children.Add(ruleList);
        DockPanel.SetDock(adjudicationControls, Dock.Top);
        adjudicationPanel.Children.Add(adjudicationControls);
        DockPanel.SetDock(adjudicationError, Dock.Top);
        adjudicationPanel.Children.Add(adjudicationError);
        adjudicationPanel.Children.Add(adjudicationList); // LastChildFill：当前裁定列表

        // 视图闭环 T10：④纠偏计划预览面板（spec §4.1④）。生产装配复用
        // writeAudit/signoffIdentity/sessionGate（与编辑/签认同源，无审计旁路）；
        // headless 测试经 planPreview 可选参注入 fake client VM。
        PlanPreview = planPreview ?? new PlanPreviewViewModel(vm.Session, vm.ProjectDir,
            writeAudit, signoffIdentity, new RoleChecker(), sessionGate);

        var planHeader = new TextBlock
        {
            Text = "纠偏计划",
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(6, 4)
        };
        var intentInput = new TextBox
        {
            Watermark = "纠偏意图（可先从问题清单填充）",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 64,
            Margin = new Thickness(4, 2)
        };
        PlanIntentInput = intentInput;
        var fillIntent = new Button { Content = "从问题填充", Margin = new Thickness(4, 2) };
        fillIntent.Click += OnFillIntentFromIssues;
        FillIntentButton = fillIntent;
        var generate = new Button { Content = "生成计划", Margin = new Thickness(4, 2) };
        generate.Click += OnGeneratePlanClick;
        GeneratePlanButton = generate;
        var planButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        planButtons.Children.Add(fillIntent);
        planButtons.Children.Add(generate);

        var planError = new TextBlock
        {
            Margin = new Thickness(6, 2),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.IndianRed,
            IsVisible = false
        };
        PlanError = planError;

        // 出境同意提示（首次云端出境；ConfirmEgress(true/false) 应答编排回调）。
        var egressPrompt = new TextBlock { Margin = new Thickness(6, 2), TextWrapping = TextWrapping.Wrap };
        EgressPromptText = egressPrompt;
        var egressConfirm = new Button { Content = "确认发送", Margin = new Thickness(4, 2) };
        egressConfirm.Click += (_, _) => { PlanPreview?.ConfirmEgress(true); UpdatePlanPanel(); };
        ConfirmEgressButton = egressConfirm;
        var egressReject = new Button { Content = "拒绝", Margin = new Thickness(4, 2) };
        egressReject.Click += (_, _) => { PlanPreview?.ConfirmEgress(false); UpdatePlanPanel(); };
        RejectEgressButton = egressReject;
        var egressButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        egressButtons.Children.Add(egressConfirm);
        egressButtons.Children.Add(egressReject);
        var egressSection = new StackPanel { Spacing = 2, IsVisible = false };
        egressSection.Children.Add(egressPrompt);
        egressSection.Children.Add(egressButtons);
        EgressConsentSection = egressSection;

        // 预览步骤列表（kind + params JSON 文本）+ 确认执行/拒绝（ConfirmPlan 应答）。
        var previewList = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<StepSpec>((s, _) =>
                s is null ? new Control() : new TextBlock
                {
                    Text = $"{s.Kind} {s.Params.GetRawText()}",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11,
                    Margin = new Thickness(4, 2)
                })
        };
        PlanPreviewList = previewList;
        var confirmPlan = new Button { Content = "确认执行", Margin = new Thickness(4, 2) };
        confirmPlan.Click += (_, _) => { _planConfirmPending = false; PlanPreview?.ConfirmPlan(true); UpdatePlanPanel(); };
        ConfirmPlanButton = confirmPlan;
        var rejectPlan = new Button { Content = "拒绝", Margin = new Thickness(4, 2) };
        rejectPlan.Click += (_, _) => { _planConfirmPending = false; PlanPreview?.ConfirmPlan(false); UpdatePlanPanel(); };
        RejectPlanButton = rejectPlan;
        var previewButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        previewButtons.Children.Add(confirmPlan);
        previewButtons.Children.Add(rejectPlan);
        var previewSection = new DockPanel { IsVisible = false };
        DockPanel.SetDock(previewButtons, Dock.Bottom);
        previewSection.Children.Add(previewButtons);
        previewSection.Children.Add(new ScrollViewer { Content = previewList }); // LastChildFill
        PlanPreviewSection = previewSection;

        var planStatus = new TextBlock { Margin = new Thickness(6, 2), TextWrapping = TextWrapping.Wrap };
        PlanStatusText = planStatus;

        var planPanel = new DockPanel();
        DockPanel.SetDock(planHeader, Dock.Top);
        planPanel.Children.Add(planHeader);
        DockPanel.SetDock(intentInput, Dock.Top);
        planPanel.Children.Add(intentInput);
        DockPanel.SetDock(planButtons, Dock.Top);
        planPanel.Children.Add(planButtons);
        DockPanel.SetDock(planError, Dock.Top);
        planPanel.Children.Add(planError);
        DockPanel.SetDock(egressSection, Dock.Top);
        planPanel.Children.Add(egressSection);
        DockPanel.SetDock(planStatus, Dock.Top);
        planPanel.Children.Add(planStatus);
        planPanel.Children.Add(previewSection); // LastChildFill：预览步骤占满其余区域

        // 事件 marshal：VM 事件在编排线程触发（Task.Run）→ Dispatcher.Post 回 UI 线程。
        // PreviewReady 处理时置位轮次闸（启用确认/拒绝按钮）——新一轮计划已显示方可应答。
        PlanPreview.PreviewReady += () => Dispatcher.UIThread.Post(() => { _planConfirmPending = true; UpdatePlanPanel(); });
        PlanPreview.RunCompleted += () => Dispatcher.UIThread.Post(UpdatePlanPanel);
        PlanPreview.EgressConsentRequired += () => Dispatcher.UIThread.Post(UpdatePlanPanel);
        PlanPreview.PlanExecuted += () => Dispatcher.UIThread.Post(() =>
        {
            RefreshAfterEdit(vm);
            _ = Issues?.RefreshAsync();
            Audit?.Rebuild();
        });

        // 天读证据 tab（只读）：从 .cadagent/semantic.json 重建五区（原生参数/炸开证据/
        // 符号候选/块定义/证据状态）+ 中心线叠加开关。不写审计、不落盘；刷新时机=
        // 初始加载 / 语义扫描·映射完成（SemanticChanged 追加订阅）/ 编辑后 RefreshAfterEdit。
        var deepRead = new DeepReadViewModel(
            Path.Combine(vm.ProjectDir, ".cadagent"), () => SafeDrawingHash(vm));
        DeepRead = deepRead;

        var deepReadHeader = new TextBlock
        {
            Text = "天读证据",
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(6, 4),
            TextWrapping = TextWrapping.Wrap
        };
        DeepReadHeader = deepReadHeader;

        var showCenterlines = new CheckBox
        {
            Content = "显示中心线",
            IsChecked = false,
            Margin = new Thickness(4, 2)
        };
        showCenterlines.IsCheckedChanged += (_, _) =>
        {
            if (Canvas is not null) Canvas.ShowCenterlines = showCenterlines.IsChecked == true;
        };
        DeepReadShowCenterlines = showCenterlines;

        var deepReadError = new TextBlock
        {
            Margin = new Thickness(6, 2),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.IndianRed,
            IsVisible = false
        };
        DeepReadError = deepReadError;

        ListBox nativeList = DeepReadSection<NativeParamRow>(deepRead.NativeRows, r =>
            $"{r.Handle} · {r.ObjectName} · {r.Class} · {r.Layer} · {(r.ReadOk ? "可读" : "读参失败")}"
            + $"{Environment.NewLine}{r.ParamsSummary}"
            + (r.FailReason is null ? "" : $" · 原因：{r.FailReason}"));
        ListBox explodeList = DeepReadSection<ExplodeRow>(deepRead.ExplodeRows, r =>
            $"{r.Handle} · {r.RxClass} · {(r.FullyExplodable ? "全炸" : "未全炸")}"
            + $" · 基元 {r.PrimitiveCount} · 残 TDb {r.NestedTdbCount}{(r.Degenerate ? " · 退化" : "")}"
            + (r.FailReasonsSummary is null ? "" : $"{Environment.NewLine}{r.FailReasonsSummary}"));
        ListBox symbolList = DeepReadSection<SymbolCandidateRow>(deepRead.SymbolRows, r =>
            $"{r.Handle} · {r.BlockName} · {r.Kind} · {r.ClassHint}");
        ListBox blockDefList = DeepReadSection<BlockDefRow>(deepRead.BlockDefRows,
            FormatBlockDefRow,
            r => r.IsCurrentDrawing == false
                ? "来源哈希不一致（图纸可能已编辑或来自他图）：块定义计数不得与当前图纸混读"
                : null);
        ListBox evidenceList = DeepReadSection<EvidenceStateRow>(deepRead.EvidenceRows, r =>
            $"{r.Handle} · 参数 {r.ParamsState} · 炸开 {r.ExplodeState}{Environment.NewLine}{r.Advisory}");
        DeepReadNativeList = nativeList;
        DeepReadExplodeList = explodeList;
        DeepReadSymbolList = symbolList;
        DeepReadBlockDefList = blockDefList;
        DeepReadEvidenceList = evidenceList;

        // 行点击 → 画布按句柄定位（a/b/e 区有句柄；块定义区无句柄不接）。
        nativeList.SelectionChanged += (_, _) =>
        {
            if (nativeList.SelectedItem is NativeParamRow row) LocateDeepReadHandle(row.Handle);
        };
        explodeList.SelectionChanged += (_, _) =>
        {
            if (explodeList.SelectedItem is ExplodeRow row) LocateDeepReadHandle(row.Handle);
        };
        evidenceList.SelectionChanged += (_, _) =>
        {
            if (evidenceList.SelectedItem is EvidenceStateRow row) LocateDeepReadHandle(row.Handle);
        };

        _deepReadSectionHeaders[0] = DeepReadSectionHeader("原生参数");
        _deepReadSectionHeaders[1] = DeepReadSectionHeader("炸开证据");
        _deepReadSectionHeaders[2] = DeepReadSectionHeader("符号候选");
        _deepReadSectionHeaders[3] = DeepReadSectionHeader("块定义");
        _deepReadSectionHeaders[4] = DeepReadSectionHeader("证据状态");

        var deepReadSections = new StackPanel { Spacing = 2 };
        ListBox[] deepReadLists = { nativeList, explodeList, symbolList, blockDefList, evidenceList };
        for (int i = 0; i < deepReadLists.Length; i++)
        {
            deepReadSections.Children.Add(_deepReadSectionHeaders[i]);
            deepReadSections.Children.Add(deepReadLists[i]);
        }

        var deepReadPanel = new DockPanel();
        DockPanel.SetDock(deepReadHeader, Dock.Top);
        deepReadPanel.Children.Add(deepReadHeader);
        DockPanel.SetDock(showCenterlines, Dock.Top);
        deepReadPanel.Children.Add(showCenterlines);
        DockPanel.SetDock(deepReadError, Dock.Top);
        deepReadPanel.Children.Add(deepReadError);
        deepReadPanel.Children.Add(new ScrollViewer { Content = deepReadSections }); // LastChildFill

        // T8：左栏三 tab（图层默认选中，T4 行为不变；未选中 tab 内容不进入视觉树）。
        // 视图闭环 T10：计划面板加入既有 TabControl（第四 tab）——不动主 DockPanel
        // 子序（LastChildFill 教训：画布必须保持最后一个子控件）。
        // 天读证据：只读 overlay 深读面板追加为第五 tab。
        var tabs = new TabControl { Width = 220 };
        tabs.Items.Add(new TabItem { Header = "图层", Content = panel });
        tabs.Items.Add(new TabItem { Header = "纠偏", Content = semanticPanel });
        tabs.Items.Add(new TabItem { Header = "裁定", Content = adjudicationPanel });
        tabs.Items.Add(new TabItem { Header = "计划", Content = planPanel });
        tabs.Items.Add(new TabItem { Header = "天读证据", Content = deepReadPanel });
        LeftTabs = tabs;

        var dock = new DockPanel();
        DockPanel.SetDock(status, Dock.Bottom);
        dock.Children.Add(status);
        DockPanel.SetDock(auditPanel, Dock.Bottom);
        dock.Children.Add(auditPanel);
        DockPanel.SetDock(signoffPanel, Dock.Bottom);
        dock.Children.Add(signoffPanel);
        DockPanel.SetDock(toolbar, Dock.Top);
        dock.Children.Add(toolbar);
        DockPanel.SetDock(tabs, Dock.Left);
        dock.Children.Add(tabs);
        DockPanel.SetDock(issuesPanel, Dock.Right);
        dock.Children.Add(issuesPanel);

        // 最终审查修复③：状态文件损坏降级提示汇总（窗口照常构造、图纸照常渲染）。
        // 必须先于 DrawingCanvas 加入：DockPanel LastChildFill 只填充最后一个子控件，
        // 若注记后加会抢走填充位，画布（无 MeasureOverride，期望 0×0）被排成 0 宽。
        if (stateNotes.Count > 0)
        {
            ErrorMessage = string.Join("；", stateNotes);
            var note = new TextBlock
            {
                Margin = new Thickness(8, 2),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.IndianRed,
                Text = ErrorMessage
            };
            StateNote = note;
            DockPanel.SetDock(note, Dock.Bottom);
            dock.Children.Add(note);
        }
        _canvasControl = new DrawingCanvas(Canvas);
        dock.Children.Add(_canvasControl); // LastChildFill：画布必须是最后一个子控件，占满其余区域
        Content = dock;

        Canvas.RenderInvalidated += OnCanvasInvalidated;
        UpdateIssuesPanel();
        UpdateSemanticPanel();
        UpdateAdjudicationPanel();
        UpdatePlanPanel();
        UpdateDeepReadPanel(); // 天读证据：初始加载已在 VM 构造完成，此处发布行/计数/开关态
        if (adjudication.ErrorMessage is not null) SetAdjudicationError(adjudication.ErrorMessage);
    }

    /// <summary>
    /// 状态文件损坏降级提示（最终审查修复③；audit.db/roles.yaml/signoffs.json）
    /// + census 显示不完整警示（T3：含 N 个未解析天正对象，只读审查）。
    /// null=全部状态健康且无未解析对象。图纸加载失败走 MainViewModel.ErrorMessage（先行短路）。
    /// </summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>降级/警示文本块（有降级或 census 警示时可见，位于底部面板区上方）。</summary>
    public TextBlock? StateNote { get; private set; }

    /// <summary>画布视图模型（加载成功时非 null）——图层面板/选择联动入口。</summary>
    public CanvasViewModel? Canvas { get; }

    /// <summary>图层面板视图模型（加载成功时非 null）。</summary>
    public LayersViewModel? Layers { get; }

    /// <summary>图层列表控件（headless 测试/后续面板任务入口）。</summary>
    public ListBox? LayerList { get; }

    /// <summary>底部状态栏文本控件。</summary>
    public TextBlock? Status { get; }

    /// <summary>问题清单视图模型（T5，加载成功时非 null）。</summary>
    public IssuesViewModel? Issues { get; }

    /// <summary>问题列表控件（T5，headless 测试入口）。</summary>
    public ListBox? IssueList { get; }

    /// <summary>问题清单刷新按钮（T5）。</summary>
    public Button? RefreshIssuesButton { get; }

    /// <summary>审图报告导出按钮（View 入口；与 CLI report export 共用导出器）。</summary>
    public Button? ExportReportButton { get; }

    /// <summary>报告导出状态/错误文本（成功绿/失败红；null=隐藏）。</summary>
    public TextBlock? ReportStatus { get; }

    /// <summary>问题清单头部（含 mandatory/skipped 计数；跳过原因 tooltip）。</summary>
    public TextBlock? IssuesHeader { get; }

    /// <summary>问题清单错误文本（刷新失败时可见）。</summary>
    public TextBlock? IssuesError { get; }

    /// <summary>审计时间线视图模型（T6，加载成功时非 null）。</summary>
    public AuditViewModel? Audit { get; }

    /// <summary>事务列表控件（T6，headless 测试入口）。</summary>
    public ListBox? AuditList { get; }

    /// <summary>前后对比切换（T6）。</summary>
    public ToggleButton? DiffToggle { get; }

    /// <summary>审计/对比错误文本（快照缺失等失败时可见）。</summary>
    public TextBlock? AuditError { get; }

    /// <summary>审查签认视图模型（T7，加载成功时非 null）。</summary>
    public SignoffViewModel? Signoff { get; }

    /// <summary>签认事务选择器（T7，headless 测试入口）。</summary>
    public ComboBox? SignoffTxSelector { get; }

    /// <summary>签认意见输入框（T7）。</summary>
    public TextBox? SignoffComment { get; }

    /// <summary>通过按钮（T7；Role&lt;Review 时禁用）。</summary>
    public Button? ApproveButton { get; }

    /// <summary>驳回按钮（T7；Role&lt;Review 时禁用）。</summary>
    public Button? RejectButton { get; }

    /// <summary>选中事务的签认状态文本（T7：unsigned / approved ✓ / rejected ✗）。</summary>
    public TextBlock? SignoffStatus { get; }

    /// <summary>签认错误文本（角色拒绝/自审拒绝/未选事务等，可见时非空）。</summary>
    public TextBlock? SignoffError { get; }

    /// <summary>左栏 tab 容器（T8：图层/纠偏/裁定；T10 计划；天读证据=第五 tab；加载成功时非 null）。</summary>
    public TabControl? LeftTabs { get; }

    /// <summary>语义纠偏视图模型（T8，加载成功时非 null）。</summary>
    public SemanticViewModel? Semantic { get; }

    /// <summary>规则裁定视图模型（T8，加载成功时非 null）。</summary>
    public AdjudicationViewModel? Adjudication { get; }

    /// <summary>纠偏扫描按钮（T8，headless 测试入口）。</summary>
    public Button? SemanticScanButton { get; }

    /// <summary>不明确图层列表（T8，headless 测试入口）。</summary>
    public ListBox? AmbiguousList { get; }

    /// <summary>纠偏错误文本（扫描/映射失败时可见）。</summary>
    public TextBlock? SemanticError { get; }

    /// <summary>裁定规则列表（T8，builtin+skill；headless 测试入口）。</summary>
    public ListBox? AdjudicationRuleList { get; }

    /// <summary>强制力覆盖下拉（T8：(default)/Mandatory/Recommended/Informational）。</summary>
    public ComboBox? ForceOverrideSelector { get; }

    /// <summary>豁免勾选（T8，与强制力覆盖互斥）。</summary>
    public CheckBox? ExemptCheck { get; }

    /// <summary>裁定理由输入框（T8，必填）。</summary>
    public TextBox? AdjudicationReason { get; }

    /// <summary>裁定解读输入框（T8，可选）。</summary>
    public TextBox? AdjudicationInterpretation { get; }

    /// <summary>裁定应用按钮（T8，headless 测试入口）。</summary>
    public Button? AdjudicationApplyButton { get; }

    /// <summary>裁定错误文本（校验拒绝/存储故障时可见）。</summary>
    public TextBlock? AdjudicationError { get; }

    /// <summary>当前裁定列表（T8，同 RuleId 最新裁定生效）。</summary>
    public ListBox? AdjudicationList { get; }

    /// <summary>天读证据视图模型（只读 overlay 五区；加载成功时非 null）。</summary>
    public DeepReadViewModel? DeepRead { get; }

    /// <summary>天读证据头部（五区计数 + 非放行依据注记）。</summary>
    public TextBlock? DeepReadHeader { get; }

    /// <summary>原生参数区列表（headless 测试入口）。</summary>
    public ListBox? DeepReadNativeList { get; }

    /// <summary>炸开证据区列表（headless 测试入口）。</summary>
    public ListBox? DeepReadExplodeList { get; }

    /// <summary>符号候选区列表（headless 测试入口）。</summary>
    public ListBox? DeepReadSymbolList { get; }

    /// <summary>块定义区列表（headless 测试入口）。</summary>
    public ListBox? DeepReadBlockDefList { get; }

    /// <summary>证据状态区列表（headless 测试入口）。</summary>
    public ListBox? DeepReadEvidenceList { get; }

    /// <summary>中心线叠加开关（有中心线才可勾选；headless 测试入口）。</summary>
    public CheckBox? DeepReadShowCenterlines { get; }

    /// <summary>天读证据错误文本（overlay 损坏/IO 失败时可见）。</summary>
    public TextBlock? DeepReadError { get; }

    /// <summary>手动编辑视图模型（T9，加载成功时非 null；所有编辑经 StepEngine）。</summary>
    public EditViewModel? Edit { get; }

    /// <summary>编辑工具栏（T9：七工具 + Undo + 文本/块名输入 + 目标层下拉 + 错误文本）。</summary>
    public StackPanel? EditToolbar { get; }

    /// <summary>幅面选择下拉（T5，internal 测试可达；加载成功时非 null）。items=幅面名+SheetId，默认首幅面。</summary>
    internal ComboBox? SheetSelector { get; }

    /// <summary>撤销按钮（T9：撤销最近事务；无事务时禁用）。</summary>
    public Button? UndoButton { get; }

    /// <summary>文本工具内容输入框（T9）。</summary>
    public TextBox? EditTextInput { get; }

    /// <summary>块插入工具块名输入框（T9）。</summary>
    public TextBox? EditBlockInput { get; }

    /// <summary>字高输入框（视图闭环 T9：Text 工具字高；非法/≤0 回落 3.5。headless 测试可达）。</summary>
    internal TextBox? TextHeightInput { get; }

    /// <summary>缩放输入框（视图闭环 T9：InsertBlock 缩放；非法/≤0 回落 1。headless 测试可达）。</summary>
    internal TextBox? BlockScaleInput { get; }

    /// <summary>旋转输入框（视图闭环 T9：InsertBlock 旋转，UI 度→handler 弧度；非法回落 0。headless 测试可达）。</summary>
    internal TextBox? BlockRotationInput { get; }

    /// <summary>
    /// Delete 确认 seam（视图闭环 T9，headless 测试注入）：null → 真模态
    /// ConfirmDialog.ShowDialog&lt;bool&gt;；非 null → 测试代理确认结果
    /// （headless 无 owner 消息泵不测 ShowDialog，真模态走 Task 11 真机协议）。
    /// </summary>
    internal Func<string, Task<bool>>? ConfirmDeleteOverride;

    /// <summary>改层工具目标层下拉（T9：ChangeLayer 激活且选中实体时选择即应用）。</summary>
    public ComboBox? EditLayerSelector { get; }

    /// <summary>编辑错误/引导文本（T9，可见时非空）。</summary>
    public TextBlock? EditError { get; }

    /// <summary>纠偏计划预览视图模型（视图闭环 T10，加载成功时非 null；headless 测试可达）。</summary>
    internal PlanPreviewViewModel? PlanPreview { get; }

    /// <summary>纠偏意图输入框（视图闭环 T10；headless 测试可达）。</summary>
    internal TextBox? PlanIntentInput { get; }

    /// <summary>从问题填充按钮（视图闭环 T10；§4.1④：勾选项优先（force 标签）+
    /// 零勾选 mandatory 兜底 + recommended 采纳审计）。</summary>
    internal Button? FillIntentButton { get; }

    /// <summary>生成计划按钮（视图闭环 T10；IsBusy 期间禁用 + 硬闸守卫）。</summary>
    internal Button? GeneratePlanButton { get; }

    /// <summary>预览步骤列表（视图闭环 T10：kind + params JSON 文本）。</summary>
    internal ItemsControl? PlanPreviewList { get; }

    /// <summary>预览区段（视图闭环 T10：IsPreviewVisible 联动可见性；headless 测试可达）。</summary>
    internal DockPanel? PlanPreviewSection { get; }

    /// <summary>确认执行按钮（视图闭环 T10：ConfirmPlan(true)）。</summary>
    internal Button? ConfirmPlanButton { get; }

    /// <summary>拒绝计划按钮（视图闭环 T10：ConfirmPlan(false)）。</summary>
    internal Button? RejectPlanButton { get; }

    /// <summary>出境同意区段（视图闭环 T10：首次云端出境时可见）。</summary>
    internal StackPanel? EgressConsentSection { get; }

    /// <summary>出境同意提示文本（视图闭环 T10：EgressPrompt）。</summary>
    internal TextBlock? EgressPromptText { get; }

    /// <summary>确认发送按钮（视图闭环 T10：ConfirmEgress(true)）。</summary>
    internal Button? ConfirmEgressButton { get; }

    /// <summary>拒绝出境按钮（视图闭环 T10：ConfirmEgress(false)）。</summary>
    internal Button? RejectEgressButton { get; }

    /// <summary>计划状态文本（视图闭环 T10：Status + LastTxId 短号）。</summary>
    internal TextBlock? PlanStatusText { get; }

    /// <summary>计划错误/引导文本（视图闭环 T10：ErrorMessage/填充引导，可见时非空）。</summary>
    internal TextBlock? PlanError { get; }

    /// <summary>
    /// 行模板：复选框（仅勾选框本体，可见性）+ 色样 + 名称（计数）。Click → Toggle。
    /// T10（真机验证发现）：色样/名称置于 CheckBox 之外——旧结构整行是 CheckBox
    /// 内容，双击名字被 Toggle 消费（两次翻转抵消）且 DoubleTapped 不冒泡，
    /// 隔离失效；现在名字/色样/空白区的指针事件不被处理，双击冒泡到 ListBox 隔离。
    /// 行根 DataContext=entry：CheckBox 经继承 DataContext 供状态栏同步循环识别。
    /// </summary>
    private static StackPanel BuildLayerRow(LayerEntry entry, LayersViewModel layers)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, DataContext = entry };
        var check = new CheckBox
        {
            IsChecked = entry.IsVisible,
            Margin = new Thickness(4, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        check.Click += (_, _) => layers.Toggle(entry.Name);
        row.Children.Add(check);
        row.Children.Add(new Border
        {
            Width = 12,
            Height = 12,
            Background = new SolidColorBrush(Color.Parse(entry.ColorBrush)),
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(new TextBlock
        {
            Text = $"{entry.Name} ({entry.EntityCount})",
            VerticalAlignment = VerticalAlignment.Center
        });
        return row;
    }

    /// <summary>双击行 → 隔离该图层（ListBox 先选中行，DoubleTapped 取 SelectedItem）。</summary>
    private void OnLayerDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (LayerList?.SelectedItem is LayerEntry entry) Layers!.Isolate(entry.Name);
    }

    /// <summary>
    /// T5 问题行模板：Expander 头部 = 效力徽章（红/黄/灰）+ 规则 Id（条款）+ 消息；
    /// 展开 = 标准引用 + 原文摘录 + 整改建议 + 检测依据 + 目标句柄。
    /// 行级 PointerPressed（handledEventsToo）→ 定位：Expander 头部 ToggleButton
    /// 会标记指针事件已处理，ListBoxItem 选中不一定触发，故在行根上兜底。
    /// §4.1④：行首加勾选 CheckBox（content 空，徽章/RuleId 仍在 Expander）——
    /// IsChecked TwoWay 绑定 IssueEntry.IsChecked（INPC），勾选→填充意图优先取勾选项；
    /// 复选框在 Expander 之外，点击不触发行级定位（镜像 BuildLayerRow 结构教训）。
    /// </summary>
    private Control BuildIssueRow(IssueEntry entry)
    {
        var badge = new Border
        {
            Background = new SolidColorBrush(Color.Parse(entry.ForceBrush)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = entry.Force.ToString(),
                FontSize = 11,
                // 黄底黑字保证对比度；深红/深灰底白字（T10 WCAG AA ≥4.5:1）。
                Foreground = entry.Force == RuleForce.Recommended ? Brushes.Black : Brushes.White
            }
        };
        var topRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        topRow.Children.Add(badge);
        topRow.Children.Add(new TextBlock
        {
            Text = $"{entry.RuleId} ({entry.Clause})",
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });
        var rowHeader = new StackPanel { Spacing = 2 };
        rowHeader.Children.Add(topRow);
        rowHeader.Children.Add(new TextBlock
        {
            Text = entry.Message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        });

        var details = new StackPanel { Spacing = 4, Margin = new Thickness(4) };
        details.Children.Add(new TextBlock
        {
            Text = $"{entry.Standard} {entry.Clause}",
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        details.Children.Add(new TextBlock
        {
            Text = entry.Excerpt,
            TextWrapping = TextWrapping.Wrap,
            FontStyle = FontStyle.Italic
        });
        details.Children.Add(new TextBlock
        {
            Text = "Suggestion: " + entry.Suggestion,
            TextWrapping = TextWrapping.Wrap
        });
        details.Children.Add(new TextBlock
        {
            Text = "Detection basis: " + entry.DetectionBasis,
            TextWrapping = TextWrapping.Wrap
        });
        details.Children.Add(new TextBlock
        {
            Text = "Targets: " + string.Join(", ", entry.Targets),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11
        });

        var expander = new Expander
        {
            Header = rowHeader,
            Content = details,
            Margin = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        expander.AddHandler(PointerPressedEvent,
            new EventHandler<PointerPressedEventArgs>((_, _) => Issues!.LocateIssue(entry)),
            RoutingStrategies.Bubble, handledEventsToo: true);

        // §4.1④ 勾选框：DataContext=entry → TwoWay 绑定 IsChecked（勾选态跨刷新由
        // IssuesViewModel key 集恢复，UI 只反映条目状态）。
        var check = new CheckBox
        {
            DataContext = entry,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(2, 6, 0, 0),
            [!ToggleButton.IsCheckedProperty] = new Binding(nameof(IssueEntry.IsChecked), BindingMode.TwoWay)
        };
        var row = new DockPanel();
        DockPanel.SetDock(check, Dock.Left);
        row.Children.Add(check);
        row.Children.Add(expander); // LastChildFill：Expander 占满行其余区域
        return row;
    }

    /// <summary>T5：选中问题行（键盘导航等）→ 画布高亮 Targets 句柄并居中。</summary>
    private void OnIssueSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (IssueList?.SelectedItem is IssueEntry entry) Issues!.LocateIssue(entry);
    }

    /// <summary>T5：Refresh → in-process 检查（评估期间禁用按钮防重入）。</summary>
    private async void OnRefreshIssuesClick(object? sender, RoutedEventArgs e)
    {
        if (Issues is null || RefreshIssuesButton is null) return;
        RefreshIssuesButton.IsEnabled = false;
        try { await Issues.RefreshAsync(); }
        finally { RefreshIssuesButton.IsEnabled = true; }
    }

    /// <summary>T5：刷新完成 → 重建列表 + 计数头部 + 错误/跳过原因展示。</summary>
    private void UpdateIssuesPanel()
    {
        if (Issues is null || IssueList is null || IssuesHeader is null) return;
        IssueList.ItemsSource = Issues.Issues;
        IssuesHeader.Text =
            $"Issues — {Issues.Issues.Count} issues, {Issues.MandatoryCount} mandatory, {Issues.RulesSkipped} skipped";
        ToolTip.SetTip(IssuesHeader, Issues.SkippedReasons.Count > 0
            ? string.Join("\n", Issues.SkippedReasons.Select(s => $"[{s.Category}] {s.RuleId}: {s.Message}"))
            : null);
        if (IssuesError is not null)
        {
            IssuesError.Text = Issues.ErrorMessage ?? "";
            IssuesError.IsVisible = Issues.ErrorMessage is not null;
        }
    }

    /// <summary>审图报告导出按钮（与 CLI report export 共用导出器；异步防重入）。</summary>
    private async void OnExportReportClick(object? sender, RoutedEventArgs e) => await ExportReportAsync();

    /// <summary>
    /// 导出审图报告（全量：问题/跳过/裁定/签认/审计验链/当前幅面缩略图）。
    /// 先 RefreshAsync 重评（与 CLI check 同语义并留 check-run 审计）；评估失败不导出。
    /// 缩略图渲染失败→报告标注"未生成"（不阻断）。导出成功后追加 report-exported 审计，
    /// 审计写入失败→删除本次产物（无审计不留产物）。默认落盘 &lt;项目&gt;/reports/。
    /// </summary>
    internal async Task ExportReportAsync()
    {
        if (_reportBusy || Issues is null || _reportAuditStore is null || _auditIdentity is null) return;
        _reportBusy = true;
        if (ExportReportButton is not null) ExportReportButton.IsEnabled = false;
        SetReportStatus(null, error: false);
        try
        {
            await Issues.RefreshAsync();
            if (Issues.ErrorMessage is not null)
            {
                SetReportStatus($"审查评估失败，未导出: {Issues.ErrorMessage}", error: true);
                return;
            }
            OperatorIdentity op = _auditIdentity.Current;
            IReadOnlyList<AuditRecord> auditAll = _reportAuditStore.ReadAll();
            bool chainOk = _reportAuditStore.VerifyChain(out string chainError);
            var auditSummary = new ReportAuditSummary(auditAll.Count, chainOk,
                chainOk ? null : chainError,
                auditAll.Count > 0 ? auditAll[^1].Seq : 0,
                auditAll.Count > 0 ? auditAll[^1].Hash : null);

            var verifications = new Dictionary<string, string>(StringComparer.Ordinal);
            string? rulePackVersion = null;
            try
            {
                SkillSet ruleSet = SkillSet.Compose(BuiltinRules.Load(),
                    _skillDirs.Select(SkillLoader.LoadDirectory).ToList());
                rulePackVersion = ruleSet.RulePackVersion;
                foreach (Rule rule in ruleSet.AllRules)
                    verifications[rule.Id] = rule.Source.Verification;
            }
            catch { /* 规则集重载失败：报告缺核对方式/版本字段，不阻断主数据 */ }

            IReadOnlyList<ReportSignoff> signoffs;
            try
            {
                _signoffStore?.Load();
                signoffs = _signoffStore is null
                    ? Array.Empty<ReportSignoff>()
                    : _signoffStore.Items.Select(s => new ReportSignoff(
                        s.TxId, s.Operator, s.Role.ToString(), s.Approved, s.Comment, s.Ts)).ToList();
            }
            catch { signoffs = Array.Empty<ReportSignoff>(); }

            IReadOnlyList<Adjudication> adjudications;
            string? adjudicationHash = null;
            try
            {
                AdjudicationSet set = AdjudicationSet.Load(
                    Path.Combine(_projectDir, ".cadagent", "adjudications.json"));
                adjudications = set.Items;
                adjudicationHash = set.Fingerprint();
            }
            catch { adjudications = Array.Empty<Adjudication>(); }

            byte[]? thumbnail = TryRenderThumbnail();
            DateTimeOffset issuedAt = DateTimeOffset.Now;
            string outPath = Path.Combine(_projectDir, "reports",
                $"{Path.GetFileNameWithoutExtension(_title)}-{issuedAt:yyyyMMdd-HHmmss}-review.pdf");

            string hash = "";
            if (_sessionGate is not null) _sessionGate.Wait();
            try { hash = _session.ComputeHash(); }
            finally { _sessionGate?.Release(); }

            var input = new ReviewReportInput
            {
                DrawingPath = _session.SourcePath,
                ProjectDir = _projectDir,
                Identity = new ReportIdentity(op.Account, op.Role.ToString()),
                IssuedAt = issuedAt,
                DrawingHash = hash,
                RulePackVersion = rulePackVersion,
                AdjudicationHash = adjudicationHash,
                OverlayPresent = Issues.OverlayPresent,
                Issues = Issues.Issues.Select(ToIssue).ToList(),
                RulesRun = Issues.RulesRun,
                RulesSkipped = Issues.RulesSkipped,
                Skipped = Issues.SkippedReasons,
                Adjudications = adjudications,
                Signoffs = signoffs,
                Audit = auditSummary,
                ThumbnailPng = thumbnail,
                RuleVerifications = verifications
            };

            ReviewReportResult result = await Task.Run(
                () => ReviewReportExporter.Export(input, outPath));

            try
            {
                _reportAuditStore.Append(new AuditEntry(DateTimeOffset.UtcNow,
                    op.Account, op.Role.ToString(), "view", "view-report", "report-exported",
                    _session.SourcePath,
                    JsonSerializer.Serialize(new
                    {
                        outPath = result.Path,
                        issueCount = input.Issues.Count,
                        mandatoryViolations = Issues.MandatoryCount,
                        auditChainOk = result.AuditChainOk,
                        thumbnail = thumbnail is not null
                    }),
                    hash, hash, "", ""));
            }
            catch (Exception ex)
            {
                // 审计先行（spec §9 无审计旁路）：审计失败→撤销本次报告（自家生成文件）
                try { File.Delete(result.Path); } catch { /* 撤销清理失败不掩盖审计错误 */ }
                SetReportStatus($"审计写入失败，已撤销报告: {ex.Message}", error: true);
                return;
            }

            SetReportStatus($"已导出: {result.Path}", error: false);
        }
        catch (Exception ex)
        {
            SetReportStatus($"导出失败: {ex.Message}", error: true);
        }
        finally
        {
            _reportBusy = false;
            if (ExportReportButton is not null) ExportReportButton.IsEnabled = true;
        }
    }

    private static Issue ToIssue(IssueEntry entry) => new(
        entry.RuleId, entry.Clause, entry.Standard, entry.Excerpt, entry.Force, entry.Severity,
        entry.Targets, entry.Message, entry.Suggestion, entry.DetectionBasis);

    /// <summary>当前幅面缩略图（离屏 Skia；失败→null，报告如实标注未生成）。</summary>
    private byte[]? TryRenderThumbnail()
    {
        try
        {
            SheetRecord sheet = _sheets.FirstOrDefault(s => s.SheetId == _currentSheetId) ?? _sheets[0];
            var provider = new RenderSnapshotProvider(_session);
            return provider.RenderPng(sheet.SheetId, sheet.FrameBounds, 640, 480);
        }
        catch
        {
            return null;
        }
    }

    private void SetReportStatus(string? message, bool error)
    {
        if (ReportStatus is null) return;
        ReportStatus.Text = message ?? "";
        ReportStatus.IsVisible = message is not null;
        ReportStatus.Foreground = error ? Brushes.IndianRed : Brushes.MediumSeaGreen;
    }

    /// <summary>画布状态变化（选择/图层可见性/视图）→ 状态栏 + 复选框同步。</summary>
    private void OnCanvasInvalidated()
    {
        if (Status is not null)
        {
            RenderItem? selected = Canvas!.SelectedItem;
            // 天读证据定位失败提示：任一选中态接管（成功定位/画布点击选择）即清除。
            if (selected is not null) _deepReadLocateNote = "";
            // T10 状态栏收尾：图纸名/实体计数（_baseStatus）+ 缩放 + 选择 + 审计摘要。
            // T5：幅面名（当前选中幅面）。
            string status = $"{_baseStatus} | Zoom {Canvas.Zoom:0.##}x | 幅面 {_currentSheetName}";
            // 修复①：布局幅面只读注记（UI 提示；编辑尝试另经 EditError 通道引导）。
            if (_layoutSheetActive) status += "（只读）";
            if (selected is not null)
                status += $" | Selected {selected.Handle} ({selected.Kind}, layer {selected.Layer})";
            if (Audit?.SelectedSummary is string summary) status += $" | Tx {summary}";
            if (_diffSummary.Length > 0) status += $" | {_diffSummary}";
            // T7：编辑警示（Yellow 闸口 / run-regression 回归警告）入状态栏尾部。
            if (_editWarning.Length > 0) status += $" | {_editWarning}";
            // §4.1④：零勾选兜底填充提示（checked 路径/新填充动作清除）。
            if (_fillIntentNote.Length > 0) status += $" | {_fillIntentNote}";
            // 天读证据：行点击定位失败提示（下一次成功定位/选中接管时清除）。
            if (_deepReadLocateNote.Length > 0) status += $" | {_deepReadLocateNote}";
            Status.Text = status;
        }
        if (LayerList is not null)
        {
            foreach (CheckBox check in LayerList.GetVisualDescendants().OfType<CheckBox>())
                if (check.DataContext is LayerEntry entry)
                    check.IsChecked = entry.IsVisible;
        }
    }

    /// <summary>T6 事务行：短号 · 操作者 · 本地时间 · 状态（commit ✓ 绿 / failed ✗ 红 / open 灰）· 步数。</summary>
    private static TextBlock BuildTxRow(TxGroup group)
    {
        string shortId = group.TxId.Length > 8 ? group.TxId[..8] : group.TxId;
        return new TextBlock
        {
            Text = $"{shortId} · {group.Operator} · {group.StartTs.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {group.Status} · {group.Steps.Count} steps",
            Margin = new Thickness(4, 2),
            Foreground = group.HasFailed ? Brushes.IndianRed
                : group.HasCommit ? Brushes.MediumSeaGreen
                : Brushes.Gray
        };
    }

    /// <summary>T6：选择事务 → 只读回放高亮 step 目标句柄；Diff 开启时随选择重载对比。</summary>
    private void OnTxSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (AuditList?.SelectedItem is not TxGroup group) return;
        Audit!.SelectTx(group.TxId);
        if (DiffToggle?.IsChecked == true) _ = ApplyDiffAsync(group.TxId);
    }

    /// <summary>
    /// T6：Diff 切换——开 → 加载选中事务的前后对比；关 → 清除叠加层。
    /// 程序性回退（IsChecked=false）会再次进入关分支（幂等），错误信息须在回退后设置。
    /// </summary>
    private void OnDiffCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (DiffToggle is null || Canvas is null || Audit is null) return;
        if (DiffToggle.IsChecked != true)
        {
            Canvas.SetDiffOverlay(null);
            _diffSummary = "";
            SetAuditError(null);
            Canvas.Invalidate();
            return;
        }
        if (Audit.SelectedTxId is not string txId)
        {
            DiffToggle.IsChecked = false;
            SetAuditError("Select a transaction first");
            return;
        }
        _ = ApplyDiffAsync(txId);
    }

    /// <summary>
    /// T6：加载对比并施加叠加层（评估期间禁用切换防重入）。失败 → 行内错误 +
    /// 切换回退；等待期间选择了别的事务 → 丢弃过期结果。
    /// 视图闭环 T5：_diffBusy 硬闸——加载在途时重入一律忽略（IsEnabled 只是视觉提示）。
    /// </summary>
    private async Task ApplyDiffAsync(string txId)
    {
        if (_diffBusy) return; // 硬闸：diff 加载在途时重入一律忽略（RaiseEvent 可绕过禁用态）
        if (DiffToggle is null || Canvas is null || Audit is null) return;
        _diffBusy = true;
        DiffToggle.IsEnabled = false;
        if (EditToolbar is not null) EditToolbar.IsEnabled = false; // T9：diff 加载期间禁止编辑（互斥）
        try
        {
            DiffResult result = await Audit.LoadDiffAsync(txId);
            if (Audit.SelectedTxId != txId) return; // 过期结果（等待期间切换了事务）
            if (result.ErrorMessage is not null)
            {
                DiffToggle.IsChecked = false; // 关分支会清错误 → 错误信息在回退后设置
                Canvas.SetDiffOverlay(null);
                _diffSummary = "";
                SetAuditError(result.ErrorMessage);
            }
            else
            {
                SetAuditError(null);
                Canvas.SetDiffOverlay(new DiffOverlay(result.BeforeItems, result.AddedHandles, result.RemovedHandles));
                _diffSummary = $"Diff +{result.AddedHandles.Count} -{result.RemovedHandles.Count} ~{result.ChangedHandles.Count}";
            }
            Canvas.Invalidate(); // 状态栏摘要刷新
        }
        finally
        {
            _diffBusy = false;
            DiffToggle.IsEnabled = true;
            if (EditToolbar is not null) EditToolbar.IsEnabled = true;
        }
    }

    private void SetAuditError(string? message)
    {
        if (AuditError is null) return;
        AuditError.Text = message ?? "";
        AuditError.IsVisible = message is not null;
    }

    /// <summary>T7：切换签认事务 → 刷新该事务签认状态。</summary>
    private void OnSignoffTxSelected(object? sender, SelectionChangedEventArgs e) => UpdateSignoffStatus();

    /// <summary>T7：选中事务的签认状态（未签认 / 最新签认人 + 结论 + 本地时间）。</summary>
    private void UpdateSignoffStatus()
    {
        if (SignoffStatus is null || Signoff is null) return;
        if (SignoffTxSelector?.SelectedItem is not TxGroup group)
        {
            SignoffStatus.Text = "";
            return;
        }
        string shortId = group.TxId.Length > 8 ? group.TxId[..8] : group.TxId;
        ReviewSignoff? s = Signoff.SignoffFor(group.TxId);
        SignoffStatus.Text = s is null
            ? $"{shortId}: unsigned"
            : $"{shortId}: {(s.Approved ? "approved ✓" : "rejected ✗")} by {s.Operator} ({s.Ts.ToLocalTime():yyyy-MM-dd HH:mm})";
    }

    /// <summary>
    /// T7：Approve/Reject 共用入口。未选事务 → 引导错误；角色拒绝/自审拒绝/
    /// 存储故障 → 捕获异常行内展示（签认与审计均未落盘）。
    /// </summary>
    private async Task SignCoreAsync(bool approved)
    {
        if (Signoff is null) return;
        if (SignoffTxSelector?.SelectedItem is not TxGroup group)
        {
            SetSignoffError("Select a transaction first");
            return;
        }
        SetSignoffError(null);
        try
        {
            await Signoff.SignAsync(group.TxId, approved, SignoffComment?.Text ?? "");
            UpdateSignoffStatus();
            // 驳回回滚失败（签认记录已落盘）→ 行内提示，不掩盖签认结果。
            SetSignoffError(Signoff.ErrorMessage);
        }
        catch (Exception ex)
        {
            SetSignoffError(ex.Message);
        }
    }

    private void SetSignoffError(string? message)
    {
        if (SignoffError is null) return;
        SignoffError.Text = message ?? "";
        SignoffError.IsVisible = message is not null;
    }

    /// <summary>
    /// T8 纠偏行模板：图层名（计数）+ 名称提示 + 样例句柄 + 语义类下拉 +
    /// Map（人工确认映射）+ Isolate（画布隔离显示）。
    /// </summary>
    private StackPanel BuildAmbiguousRow(AmbiguousLayer a)
    {
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(4) };
        panel.Children.Add(new TextBlock
        {
            Text = $"{a.LayerName} ({a.EntityCount})",
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap
        });
        if (a.NameHint.Length > 0)
            panel.Children.Add(new TextBlock
            {
                Text = a.NameHint,
                FontSize = 11,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap
            });
        panel.Children.Add(new TextBlock
        {
            Text = "样例: " + string.Join(", ", a.SampleHandles),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap
        });

        var combo = new ComboBox
        {
            ItemsSource = ClassOptions,
            PlaceholderText = "class",
            Width = 110,
            Margin = new Thickness(0, 2)
        };
        var map = new Button { Content = "Map", Margin = new Thickness(4, 2) };
        map.Click += (_, _) => _ = MapCoreAsync(a.LayerName, combo, map);
        var isolate = new Button { Content = "Isolate", Margin = new Thickness(4, 2) };
        isolate.Click += (_, _) => Semantic!.IsolateLayer(a.LayerName);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        row.Children.Add(combo);
        row.Children.Add(map);
        row.Children.Add(isolate);
        panel.Children.Add(row);
        return panel;
    }

    /// <summary>T8：Scan → 语义扫描（评估期间禁用按钮防重入）。</summary>
    private async void OnScanClick(object? sender, RoutedEventArgs e)
    {
        if (Semantic is null || SemanticScanButton is null) return;
        SemanticScanButton.IsEnabled = false;
        try { await Semantic.ScanAsync(_skillDirs); }
        finally { SemanticScanButton.IsEnabled = true; }
    }

    /// <summary>T8：Map → 人工确认映射。未选语义类 → 引导错误；失败行内展示。</summary>
    private async Task MapCoreAsync(string layer, ComboBox combo, Button map)
    {
        if (Semantic is null) return;
        if (combo.SelectedItem is not string className || !Enum.TryParse<SemanticClass>(className, out SemanticClass cls))
        {
            SetSemanticError("请先选择语义类");
            return;
        }
        SetSemanticError(null);
        map.IsEnabled = false;
        try { await Semantic.MapAsync(layer, cls); }
        finally { map.IsEnabled = true; }
    }

    /// <summary>T8：扫描/映射完成 → 重建不明确列表 + 计数头部 + 错误展示。</summary>
    private void UpdateSemanticPanel()
    {
        if (Semantic is null || AmbiguousList is null) return;
        AmbiguousList.ItemsSource = Semantic.Ambiguous;
        if (_semanticHeader is not null)
            _semanticHeader.Text =
                $"语义纠偏 — {Semantic.Ambiguous.Count} ambiguous, {Semantic.AnnotationCount} annotations (v{Semantic.Version})";
        SetSemanticError(Semantic.ErrorMessage);
    }

    private void SetSemanticError(string? message)
    {
        if (SemanticError is null) return;
        SemanticError.Text = message ?? "";
        SemanticError.IsVisible = message is not null;
    }

    /// <summary>天读证据：重读 overlay + 发布面板（语义扫描/映射完成、编辑后调用）。</summary>
    private void ReloadDeepRead()
    {
        DeepRead?.Reload();
        UpdateDeepReadPanel();
    }

    /// <summary>天读证据：五区列表源/计数标题/中心线开关态/错误展示刷新。</summary>
    private void UpdateDeepReadPanel()
    {
        if (DeepRead is null) return;
        Canvas?.SetCenterlines(DeepRead.Centerlines);
        if (DeepReadShowCenterlines is not null)
        {
            DeepReadShowCenterlines.IsEnabled = DeepRead.HasCenterlines; // 无中心线不可开
            if (!DeepRead.HasCenterlines && DeepReadShowCenterlines.IsChecked == true)
                DeepReadShowCenterlines.IsChecked = false; // 数据消失 → 叠加关闭（开关事件同步画布）
        }
        if (DeepReadHeader is not null)
            DeepReadHeader.Text =
                $"天读证据 — {DeepRead.CountsSummary}（只读预览，非放行依据）"
                + (DeepRead.SkewedCenterlines > 0 ? $"；畸形中心线 {DeepRead.SkewedCenterlines}" : "");
        int[] counts =
        {
            DeepRead.NativeRows.Count, DeepRead.ExplodeRows.Count, DeepRead.SymbolRows.Count,
            DeepRead.BlockDefRows.Count, DeepRead.EvidenceRows.Count
        };
        for (int i = 0; i < _deepReadSectionHeaders.Length; i++)
            _deepReadSectionHeaders[i].Text = $"{DeepReadSectionNames[i]} ({counts[i]})";
        SetDeepReadError(DeepRead.ErrorMessage);
    }

    private void SetDeepReadError(string? message)
    {
        if (DeepReadError is null) return;
        DeepReadError.Text = message ?? "";
        DeepReadError.IsVisible = message is not null;
    }

    /// <summary>
    /// 天读证据行点击定位：命中 → 清定位失败提示；未命中（句柄只存在于深读证据——
    /// TDb 实体不在当前渲染图元）→ 清画布选中/定位高亮（Select(null)，无独立
    /// deselect API）+ 状态栏提示。提示在下一次成功定位或画布选中接管时清除
    /// （OnCanvasInvalidated），避免陈旧提示误导。
    /// </summary>
    private void LocateDeepReadHandle(string handle)
    {
        if (Canvas is null) return;
        if (Canvas.SelectByHandle(handle))
        {
            SetDeepReadLocateNote("");
            return;
        }
        Canvas.Select(null);
        SetDeepReadLocateNote(LocateMissNote);
    }

    /// <summary>天读证据定位失败提示入状态栏（空串=清除）。</summary>
    private void SetDeepReadLocateNote(string note)
    {
        _deepReadLocateNote = note;
        Canvas?.Invalidate();
    }

    /// <summary>块定义行文本：来源哈希比对——true="本图"；false=来源哈希不一致
    /// （图纸可能已编辑或来自他图，计数不得混读）；null=不可比（"—"，不猜）。</summary>
    internal static string FormatBlockDefRow(BlockDefRow r) =>
        $"{r.BlockName} · {r.RxClass} · {r.Count}"
        + $" · {r.IsCurrentDrawing switch
        {
            true => "本图",
            false => "来源哈希不一致（图纸可能已编辑或来自他图）",
            null => "—"
        }}"
        + (r.DrawingHash is null ? "" : $" · {r.DrawingHash}");

    /// <summary>天读证据：当前图纸哈希（块定义来源比对）；不可得 → null（不猜来源）。</summary>
    private static string? SafeDrawingHash(MainViewModel vm)
    {
        try { return vm.Session.ComputeHash(); }
        catch (Exception) { return null; } // 只读面板：哈希不可得不阻断面板
    }

    /// <summary>天读证据区列表：ItemsSource=区集合，行模板=单 TextBlock（换行自适应）；
    /// tooltip 可选（块定义来源哈希不一致提示）。</summary>
    private static ListBox DeepReadSection<T>(System.Collections.IEnumerable rows, Func<T, string> text,
        Func<T, string?>? tooltip = null)
        where T : class => new()
    {
        ItemsSource = rows,
        Height = 110,
        ItemTemplate = new FuncDataTemplate<T>((row, _) =>
        {
            if (row is null) return new Control();
            var block = new TextBlock
            {
                Text = text(row),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 2)
            };
            if (tooltip is not null) ToolTip.SetTip(block, tooltip(row));
            return block;
        })
    };

    /// <summary>天读证据小节标题（计数由 UpdateDeepReadPanel 刷新）。</summary>
    private static TextBlock DeepReadSectionHeader(string text) => new()
    {
        Text = text,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(6, 4, 6, 0)
    };

    /// <summary>
    /// T8：Apply → 人工裁定。未选规则 → 引导错误；校验拒绝（互斥/reason 必填/
    /// 孤儿）/存储故障 → 捕获异常行内展示（裁定与审计均未落盘）。
    /// 成功 → AdjudicationsChanged 联动刷新当前裁定列表 + 问题清单。
    /// </summary>
    private async void OnAdjudicateClick(object? sender, RoutedEventArgs e)
    {
        if (Adjudication is null) return;
        if (AdjudicationRuleList?.SelectedItem is not Rule rule)
        {
            SetAdjudicationError("Select a rule first");
            return;
        }
        RuleForce? force = null;
        if (ForceOverrideSelector is { SelectedIndex: > 0 } fs)
            force = Enum.Parse<RuleForce>((string)fs.SelectedItem!);
        bool exempt = ExemptCheck?.IsChecked == true;
        SetAdjudicationError(null);
        try
        {
            await Adjudication.AdjudicateAsync(rule.Id, force, exempt,
                AdjudicationReason?.Text ?? "", AdjudicationInterpretation?.Text ?? "");
        }
        catch (Exception ex)
        {
            SetAdjudicationError(ex.Message);
        }
    }

    /// <summary>T8：裁定成功 → 重建规则列表 + 当前裁定列表。</summary>
    private void UpdateAdjudicationPanel()
    {
        if (Adjudication is null) return;
        if (AdjudicationRuleList is not null) AdjudicationRuleList.ItemsSource = Adjudication.Rules;
        if (AdjudicationList is not null) AdjudicationList.ItemsSource = Adjudication.Adjudications;
    }

    private void SetAdjudicationError(string? message)
    {
        if (AdjudicationError is null) return;
        AdjudicationError.Text = message ?? "";
        AdjudicationError.IsVisible = message is not null;
    }

    /// <summary>T9：激活工具（清预览锚点/Move 基准/旧错误）。Select 恢复默认选择路径。
    /// 最终审查打包小项：_editBusy 硬闸——在途编辑期间工具切换一律忽略
    /// （RaiseEvent 可绕过工具栏 IsEnabled=false，标志位才是结构性保证）。
    /// 视图闭环 T5：_diffBusy 并列硬闸——diff 加载期间同样忽略。</summary>
    private void SetActiveTool(EditTool tool)
    {
        if (Edit is null || Canvas is null || _editBusy || _diffBusy) return;
        // 修复①：布局幅面只读硬闸——编辑工具激活一律拒绝（Select 为只读选择路径，放行）。
        if (_layoutSheetActive && tool != EditTool.Select)
        {
            SetEditError(LayoutReadOnlyMessage);
            return;
        }
        Edit.ActiveTool = tool;
        Canvas.SetPendingPoint(null);
        _moveBase = null;
        SetEditError(null);
        SetEditWarning(""); // T7：切换工具清掉上一次编辑的警示（与错误通道同口径）
    }

    /// <summary>
    /// Imp#3：Insert/Dimension 子图元渲染句柄为 "{parent}:{i}"（嵌套 "{top}:{i}:{j}"，
    /// RenderModel 展开口径）。编辑语义 = 编辑父实体/块整体——剥后缀取顶层句柄传处理器
    /// （EditViewModel.WithHandle 的 ulong.TryParse 只认十进制顶层句柄，后缀会致"非法句柄"
    /// 拒绝，块/标注几何在视图内 Move/Delete/ChangeLayer 不可达）。独立子图元编辑 deferred
    /// （spec §12 注记）。三编辑入口（Move/Delete/ChangeLayer）统一经此。
    /// </summary>
    private static string ParentHandle(string handle) => handle.Split(':')[0];

    /// <summary>T9 Delete 两段式确认：工具已激活且有选中 → 再按即确认删除；否则激活工具。
    /// 视图闭环 T5：_diffBusy 并列硬闸——diff 加载期间一律忽略。
    /// 视图闭环 T9：第二击先经模态确认（ConfirmDialog.ShowDialog&lt;bool&gt;；headless
    /// 测试经 ConfirmDeleteOverride seam 注入）——取消保持选中不清状态；确认后才走
    /// 既有删除执行逻辑（EditViewModel→StepEngine，无审计旁路）。</summary>
    private async Task OnDeleteClickAsync()
    {
        if (Edit is null || Canvas is null || _editBusy || _diffBusy) return;
        // 修复①：布局幅面只读硬闸（确认删除/激活 Delete 均拒绝）。
        if (_layoutSheetActive)
        {
            SetEditError(LayoutReadOnlyMessage);
            return;
        }
        if (Edit.ActiveTool == EditTool.Delete && Canvas.SelectedHandle is string selected)
        {
            string handle = ParentHandle(selected); // Imp#3：子图元句柄→父实体整体删除
            string msg = $"确认删除选中实体（句柄 {handle}）？删除经审计管线，可 Undo 撤回。";
            bool confirmed = ConfirmDeleteOverride is not null
                ? await ConfirmDeleteOverride(msg)
                : await new ConfirmDialog(msg).ShowDialog<bool>(this);
            if (!confirmed) return; // 取消：保持选中，不清状态
            // 确认等待期间编辑/diff 抢跑 → 硬闸兜底（模态阻塞 owner 时理论不可达，seam 路径防御）。
            if (_editBusy || _diffBusy) return;
            await RunEditAsync(() => Edit.DeleteAsync(handle));
        }
        else
            SetActiveTool(EditTool.Delete);
    }

    /// <summary>T9：撤销最近事务（UndoAsync → StepEngine.Rollback，无旁路）。
    /// 视图闭环 T5：_diffBusy 并列硬闸——diff 加载期间一律忽略。</summary>
    private void OnUndoClick(object? sender, RoutedEventArgs e)
    {
        if (Edit is null || _editBusy || _diffBusy || Edit.RecentTxIds.Count == 0) return;
        // 修复①：布局幅面只读硬闸——撤销同样改模型空间，布局视图期间拒绝。
        if (_layoutSheetActive)
        {
            SetEditError(LayoutReadOnlyMessage);
            return;
        }
        string txId = Edit.RecentTxIds[^1];
        _ = RunEditAsync(() => Edit.UndoAsync(txId));
    }

    /// <summary>T9：ChangeLayer 激活且选中实体时，层下拉选择即应用改层。
    /// 视图闭环 T5：_diffBusy 并列硬闸——diff 加载期间一律忽略。</summary>
    private void OnEditLayerSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (Edit is null || Canvas is null || _editBusy || _diffBusy) return;
        // 修复①：布局幅面只读硬闸——改层写模型空间，布局视图期间拒绝。
        if (_layoutSheetActive)
        {
            SetEditError(LayoutReadOnlyMessage);
            return;
        }
        if (Edit.ActiveTool == EditTool.ChangeLayer
            && Canvas.SelectedHandle is string selected
            && EditLayerSelector?.SelectedItem is string layer)
            _ = RunEditAsync(() => Edit.ChangeLayerAsync(ParentHandle(selected), layer)); // Imp#3：父实体整体改层
    }

    /// <summary>
    /// T9：画布左击工具路由（世界坐标）。返回 true=已消费（跳过默认选择）。
    /// Line 第一击设预览锚点、第二击提交；Text/InsertBlock 需工具栏输入（空→引导）；
    /// Move 点击实体选中并记首点基准、点击目标点提交 dx/dy；Delete/ChangeLayer
    /// 点击仅选中（确认走工具栏二次按压/层下拉）。Select 走默认选择路径。
    /// </summary>
    private bool OnCanvasToolClick(double wx, double wy)
    {
        if (Edit is null || Canvas is null) return false;
        // T10 重入闸：编辑在途时忽略画布点击（消费事件，不落预览锚点/不改选择）。
        // 视图闭环 T5：_diffBusy 并列硬闸——diff 加载期间同样消费忽略。
        if (_editBusy || _diffBusy) return true;
        // 修复①：布局幅面只读硬闸——编辑工具点击消费但不提交（纸面坐标绝不进模型
        // 空间；覆盖模型幅面激活工具后切布局的残留态）；Select 走默认只读选择路径。
        if (_layoutSheetActive && Edit.ActiveTool != EditTool.Select)
        {
            SetEditError(LayoutReadOnlyMessage);
            return true;
        }
        switch (Edit.ActiveTool)
        {
            case EditTool.Line:
                if (Canvas.PendingPoint is (double x1, double y1))
                {
                    Canvas.SetPendingPoint(null);
                    _ = RunEditAsync(() => Edit.DrawLineAsync(x1, y1, wx, wy));
                }
                else
                {
                    Canvas.SetPendingPoint((wx, wy));
                }
                return true;

            case EditTool.Text:
                string text = EditTextInput?.Text ?? "";
                if (text.Length == 0)
                {
                    SetEditError("请先输入文本内容（工具栏 Text content 框）");
                    return true;
                }
                // 视图闭环 T9：字高取输入框（InvariantCulture；非法/≤0 → 默认 3.5，不崩）。
                // 最终审查顺手修：double.IsFinite 守卫——NaN/Infinity → fallback（∞ 曾通过 >0 检查）。
                double height = double.TryParse(TextHeightInput?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double h) && double.IsFinite(h) && h > 0 ? h : DefaultTextHeight;
                _ = RunEditAsync(() => Edit.AddTextAsync(wx, wy, text, height));
                return true;

            case EditTool.InsertBlock:
                string block = EditBlockInput?.Text ?? "";
                if (block.Length == 0)
                {
                    SetEditError("请先输入块名（工具栏 Block name 框）");
                    return true;
                }
                // 视图闭环 T9：缩放非法/≤0 → 1；旋转 UI 度 → handler 弧度（非法 → 0）。
                // 最终审查顺手修：double.IsFinite 守卫——NaN/Infinity → fallback（∞ 曾通过 >0 检查；
                // NaN/∞ 入 JsonSerializer 会抛异常致编辑静默失败）。
                double scale = double.TryParse(BlockScaleInput?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double s) && double.IsFinite(s) && s > 0 ? s : 1;
                double rotation = double.TryParse(BlockRotationInput?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double deg) && double.IsFinite(deg) ? deg * Math.PI / 180.0 : 0;
                _ = RunEditAsync(() => Edit.InsertBlockAsync(block, wx, wy, scale, rotation));
                return true;

            case EditTool.Move:
                int? hit = Canvas.HitTest(wx, wy, PickTolerancePixels / Canvas.Scale);
                if (hit is int i)
                {
                    Canvas.Select(i);
                    _moveBase = Canvas.Items[i].Points.Count > 0
                        ? Canvas.Items[i].Points[0]
                        : ((double X, double Y)?)null;
                }
                else if (Canvas.SelectedHandle is string moveHandle && _moveBase is (double bx, double by))
                {
                    _moveBase = null;
                    // Imp#3：子图元句柄→父 Insert/Dimension 整体移动（块/标注几何随父平移）。
                    _ = RunEditAsync(() => Edit.MoveAsync(ParentHandle(moveHandle), wx - bx, wy - by));
                }
                return true;

            case EditTool.Delete:
            case EditTool.ChangeLayer:
                Canvas.Select(Canvas.HitTest(wx, wy, PickTolerancePixels / Canvas.Scale));
                return true;

            default:
                return false; // Select：默认 HitTest→Select 路径
        }
    }

    /// <summary>T9：编辑/撤销评估期间禁用工具栏 + Diff 切换（防重入 + diff/编辑互斥）。
    /// 视图闭环 T7：撤销无 EditResult——清掉上一次编辑的回归/闸口警示（图纸已变，旧警告失效）。</summary>
    private async Task RunEditAsync(Func<Task> apply)
    {
        SetEditBusy(true);
        try { SetEditWarning(""); await apply(); }
        finally { SetEditBusy(false); }
    }

    /// <summary>
    /// 视图闭环 T7：带结果的编辑入口——任何非空 EditResult.Warning（Yellow 闸口警示 /
    /// 编辑后 run-regression 回归警告）均入状态栏（此前 RunEditAsync 丢弃结果，Warning
    /// 完全不可见）。新编辑开始先清旧警示；与 EditError 错误通道分离，互不覆盖。
    /// 重载决议：Task&lt;EditResult&gt; 比 Task 更特化，编辑调用点自动命中本重载。
    /// </summary>
    private async Task RunEditAsync(Func<Task<EditResult>> apply)
    {
        SetEditBusy(true);
        try
        {
            SetEditWarning("");
            EditResult result = await apply();
            SetEditWarning(result.Warning);
        }
        finally { SetEditBusy(false); }
    }

    /// <summary>T7：编辑警示入状态栏（OnCanvasInvalidated 拼接待显警告；空串=清除）。</summary>
    private void SetEditWarning(string warning)
    {
        _editWarning = warning;
        Canvas?.Invalidate(); // 状态栏重建（EditCompleted 先于结果返回，须再刷一次）
    }

    private void SetEditBusy(bool busy)
    {
        _editBusy = busy; // T10：结构性硬闸（画布点击/工具栏事件均检查）
        if (EditToolbar is not null) EditToolbar.IsEnabled = !busy;
        if (DiffToggle is not null) DiffToggle.IsEnabled = !busy;
        // I2（fixwave2）：编辑在途时填充按钮同步禁用（镜像 EditToolbar/DiffToggle；
        // 恢复时仍须让位于 diff 在途/计划编排 busy——与 UpdatePlanPanel 口径一致）。
        if (FillIntentButton is not null) FillIntentButton.IsEnabled = !busy && !_diffBusy && !(PlanPreview?.IsBusy ?? false);
    }

    /// <summary>T5：幅面下拉选择 → 刷新画布为该幅面图元（用户交互触发，重新 Fit 到新内容）。
    /// 修复②：_editBusy 硬闸——在途编辑期间程序性 SelectionChanged 也不得切换幅面
    /// （ApplySheet→BuildViewport 枚举 doc.Entities 无会话闸 = torn read，T6/T10 同款
    /// 防护）；忽略并恢复 selector 索引到当前幅面。</summary>
    private void OnSheetSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSheetSelection) return;
        if (_editBusy)
        {
            RestoreSheetSelectorIndex();
            return;
        }
        if (SheetSelector?.SelectedItem is SheetRecord sheet) ApplySheet(sheet, refit: true);
    }

    /// <summary>修复②：恢复幅面下拉索引到当前幅面（抑制程序性 SelectionChanged 递归）。</summary>
    private void RestoreSheetSelectorIndex()
    {
        if (SheetSelector is null) return;
        int index = 0;
        for (int i = 0; i < _sheets.Count; i++)
            if (_sheets[i].SheetId == _currentSheetId) { index = i; break; }
        _suppressSheetSelection = true;
        try { SheetSelector.SelectedIndex = index; }
        finally { _suppressSheetSelection = false; }
    }

    /// <summary>
    /// T5：应用幅面——模型幅面（LayoutName=null）按 FrameBounds 锚点裁剪全模型图元；
    /// 布局幅面经 viewportHandle 查找视口 → BuildViewport 纸面映射。刷新 Canvas/Layers/
    /// 层下拉/状态栏计数；refit=true（用户切换）按新内容 bounds 重新 Fit，false（编辑重建）保持视图。
    /// </summary>
    private void ApplySheet(SheetRecord sheet, bool refit)
    {
        if (Canvas is null || Layers is null) return;
        IReadOnlyList<RenderItem> items = ItemsForSheet(sheet);
        Canvas.Refresh(items);
        Layers.Refresh(items, _session); // T8：图层表并集（编辑清空某层后空层仍显示）
        if (LayerList is not null) LayerList.ItemsSource = Layers.Layers;
        if (EditLayerSelector is not null)
            EditLayerSelector.ItemsSource = Layers.Layers.Select(l => l.Name).ToList();
        _currentSheetId = sheet.SheetId;
        _currentSheetName = sheet.Name;
        // 修复①：只读标志与幅面同步；切换幅面清除跨空间残留预览锚点/Move 基准
        // （模型/纸面坐标不得混用——纸面编辑非本期语义，deferred）。
        _layoutSheetActive = sheet.LayoutName is not null;
        Canvas.SetPendingPoint(null);
        _moveBase = null;
        _baseStatus = $"{_title} — {items.Count} render items";
        if (refit && _canvasControl is { Bounds.Width: > 0, Bounds.Height: > 0 })
            Canvas.Fit(RenderModelBuilder.Bounds(items), _canvasControl.Bounds.Width, _canvasControl.Bounds.Height);
        Canvas.Invalidate();
    }

    /// <summary>T5：幅面图元——模型幅面框内锚点裁剪；布局幅面 BuildViewport（视口缺失→空，诚实）。</summary>
    private IReadOnlyList<RenderItem> ItemsForSheet(SheetRecord sheet)
    {
        if (sheet.LayoutName is null)
            return RenderModelBuilder.CropToFrame(_fullModelItems, sheet.FrameBounds);
        ACadSharp.Entities.Viewport? vp = FindViewport(_doc, sheet.TitleAttributes.GetValueOrDefault("viewportHandle"));
        return vp is null ? Array.Empty<RenderItem>() : RenderModelBuilder.BuildViewport(_doc, vp);
    }

    /// <summary>T5：按句柄在纸空间布局关联块中查找视口实体。</summary>
    private static ACadSharp.Entities.Viewport? FindViewport(ACadSharp.CadDocument doc, string? handle)
    {
        if (handle is null || doc.Layouts is null) return null;
        foreach (var layout in doc.Layouts)
            foreach (var vp in layout.AssociatedBlock?.Entities.OfType<ACadSharp.Entities.Viewport>() ?? Enumerable.Empty<ACadSharp.Entities.Viewport>())
                if (vp.Handle.ToString() == handle) return vp;
        return null;
    }

    /// <summary>
    /// T9：编辑/撤销完成（成功或失败）→ 重建渲染模型 → Canvas.Refresh +
    /// Layers.Refresh（T4 承接）+ 图层列表/层下拉/状态栏计数/错误/Undo 可用性。
    /// T5：重建全模型图元 + 幅面登记簿，按当前幅面 Id 保持选择（幅面消失→回退首幅面），
    /// 经 ApplySheet 刷新该幅面图元（保持视图，不重 Fit——legacy 编辑行为）。
    /// </summary>
    private void RefreshAfterEdit(MainViewModel vm)
    {
        if (Canvas is null || Layers is null || Edit is null) return;
        // R11：undo/rollback（StepEngine.Rollback→RestoreState）整体替换 Session.Doc——
        // 同步 _doc 引用，否则布局幅面 ItemsForSheet（FindViewport/BuildViewport）用 detached
        // 旧文档渲染 undo 前状态。模型幅面经 _fullModelItems 重建已跟随，布局幅面依赖 _doc。
        _doc = vm.Session.Doc;
        _fullModelItems = RenderModelBuilder.Build(vm.Session);
        _sheets = vm.Session.Sheets;
        SheetRecord sheet = _sheets.FirstOrDefault(s => s.SheetId == _currentSheetId) ?? _sheets[0];
        // 程序性刷新下拉（抑制 SelectionChanged，避免与 ApplySheet 二次刷新/重 Fit）
        _suppressSheetSelection = true;
        if (SheetSelector is not null)
        {
            SheetSelector.ItemsSource = _sheets;
            SheetSelector.SelectedItem = sheet;
        }
        _suppressSheetSelection = false;
        ApplySheet(sheet, refit: false);
        _moveBase = null;
        // 编辑使旧对比失效：Diff 切换回退（关分支幂等清叠加/摘要），避免残留勾选态。
        if (DiffToggle is { IsChecked: true }) DiffToggle.IsChecked = false;
        _diffSummary = "";
        SetEditError(Edit.ErrorMessage);
        if (UndoButton is not null) UndoButton.IsEnabled = Edit.RecentTxIds.Count > 0;
        // T9 承接闭环：审计时间线 live-rebuild（编辑/撤销/回滚后无需重开窗口）
        Audit?.Rebuild();
        // 天读证据：编辑改图后块定义来源哈希/证据表须重读（只读面板，不写审计）。
        ReloadDeepRead();
    }

    private void SetEditError(string? message)
    {
        if (EditError is null) return;
        EditError.Text = message ?? "";
        EditError.IsVisible = message is not null;
    }

    /// <summary>§4.1④ 从问题填充意图（勾选优先）：
    /// 有勾选 → intent=勾选项（mandatory+recommended 混合），行格式
    /// "{RuleId} [{Force}]: {Suggestion}"（Force 枚举名原文）；勾选项含 Recommended
    /// → 追加 rule-adopted 采纳审计（Source="rule"，镜像 rule-adjudicated 先例；
    /// 仅 Recommended——Mandatory 是义务非采纳；每次填充各留一条，append-only 不去重）。
    /// 零勾选 → 保持既有 mandatory 自动填充兜底（旧格式 "{RuleId}: {Suggestion}"
    /// 逐字不变）+ 状态栏提示；无 mandatory 项 → 行内引导（先 Refresh 或手动输入）。
    /// 忽略=未勾选正常决策（spec §6.2），不留痕。
    /// I2（合并前 ISTQB fixwave2）：_editBusy/_diffBusy/IsBusy 结构闸（镜像
    /// OnGeneratePlanClick）——执行在途时 RaiseEvent 可绕过按钮禁用态，而
    /// TryAppendRuleAdoptedAudit 的 ComputeHash 是无会话闸读点，busy 一律拒绝
    /// （不填充不审计），防 torn read。</summary>
    private void OnFillIntentFromIssues(object? sender, RoutedEventArgs e)
    {
        if (PlanPreview is null || PlanIntentInput is null) return;
        if (_editBusy || _diffBusy || PlanPreview.IsBusy) return; // 结构硬闸：在途一律忽略
        SetFillIntentNote(""); // 新填充动作先清旧兜底提示（镜像 SetEditWarning 口径）
        List<IssueEntry> all = Issues?.Issues.ToList() ?? new List<IssueEntry>();
        List<IssueEntry> checkedIssues = all.Where(i => i.IsChecked).ToList();
        if (checkedIssues.Count > 0)
        {
            // 采纳审计先行（镜像 AdjudicationViewModel Append 先于 Save）：写失败 →
            // 行内错误、不填充（不崩 UI，无审计旁路）。
            List<IssueEntry> adopted = checkedIssues.Where(i => i.Force == RuleForce.Recommended).ToList();
            if (adopted.Count > 0 && !TryAppendRuleAdoptedAudit(adopted)) return;
            SetPlanError(null);
            PlanIntentInput.Text = string.Join("\n",
                checkedIssues.Select(i => $"{i.RuleId} [{i.Force}]: {i.Suggestion}"));
            return;
        }
        // 零勾选兜底：既有 mandatory 自动填充行为逐字保持。
        List<IssueEntry> mandatory = all.Where(i => i.Force == RuleForce.Mandatory).ToList();
        if (mandatory.Count == 0)
        {
            SetPlanError("问题清单无 mandatory 项——先 Refresh 问题清单或手动输入纠偏意图");
            return;
        }
        SetPlanError(null);
        PlanIntentInput.Text = string.Join("\n", mandatory.Select(i => $"{i.RuleId}: {i.Suggestion}"));
        SetFillIntentNote("未勾选问题，已按 mandatory 自动填充");
    }

    /// <summary>§4.1④ recommended 采纳审计：Kind="rule-adopted"、Source="rule"
    /// （镜像 rule-adjudicated 先例）、Target=图纸路径、Session="view" 常量口径
    /// （镜像 IssuesVM check-run）；params={adopted:[{ruleId,clause,standard}],count}；
    /// 填充不改图纸 → BeforeHash==AfterHash==drawing.ComputeHash()。
    /// I2（fixwave2）：ComputeHash 为无会话闸读点，由 OnFillIntentFromIssues 首行
    /// busy 结构闸（_editBusy/_diffBusy/IsBusy）保护——在途编辑/编排期间不可达。
    /// 返回 false=写入失败（错误已入行内通道，调用方中止填充——不崩 UI）。</summary>
    private bool TryAppendRuleAdoptedAudit(List<IssueEntry> adopted)
    {
        if (_writeAudit is null || _auditIdentity is null)
        {
            // 防御方向=失败口径：加载失败短路当前不可达，但未来重构若使其可达，
            // 必须中止填充并入行内错误——绝不静默跳过审计继续填充（审计旁路）。
            SetPlanError("采纳审计不可用：写审计或身份未加载");
            return false;
        }
        try
        {
            OperatorIdentity op = _auditIdentity.Current;
            string hash = _session.ComputeHash();
            _writeAudit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(),
                "view", "rule", "rule-adopted", _session.SourcePath,
                JsonSerializer.Serialize(new
                {
                    adopted = adopted.Select(i => new { ruleId = i.RuleId, clause = i.Clause, standard = i.Standard }).ToArray(),
                    count = adopted.Count
                }),
                hash, hash, "", ""));
            return true;
        }
        catch (Exception ex)
        {
            // 镜像现状降级（AdjudicationViewModel 审计先行 + UI 行内错误）：不崩 UI。
            SetPlanError($"采纳审计写入失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>§4.1④ 兜底提示入状态栏（OnCanvasInvalidated 拼接待显文本；空串=清除）。</summary>
    private void SetFillIntentNote(string note)
    {
        _fillIntentNote = note;
        Canvas?.Invalidate(); // 状态栏重建（镜像 SetEditWarning）
    }

    /// <summary>视图闭环 T10：生成计划（async void 包装 GenerateAsync）。
    /// _editBusy/_diffBusy 并列硬闸 + IsBusy 重入闸（IsEnabled 只是视觉提示，
    /// RaiseEvent 可绕过禁用态，标志位才是结构性保证——镜像 T10 编辑重入闸）。
    /// 编排经 VM 内 Task.Run 下线程池，confirmPlan 阻塞回调不占 UI 线程。</summary>
    private async void OnGeneratePlanClick(object? sender, RoutedEventArgs e)
    {
        if (PlanPreview is null || _editBusy || _diffBusy || PlanPreview.IsBusy) return;
        PlanPreview.Intent = PlanIntentInput?.Text ?? "";
        if (GeneratePlanButton is not null) GeneratePlanButton.IsEnabled = false;
        try
        {
            await PlanPreview.GenerateAsync();
            UpdatePlanPanel();
        }
        catch (Exception ex)
        {
            // Imp#2 防御兜底（镜像既有 async void 惯例 OnAdjudicateClick）：GenerateAsync
            // 已内部全捕获前奏/编排异常，此处兜底装配/刷新意外——异常入错误通道，绝不崩进程。
            SetPlanError(ex.Message);
        }
        finally { if (GeneratePlanButton is not null) GeneratePlanButton.IsEnabled = true; }
    }

    /// <summary>视图闭环 T10：计划面板刷新——预览/出境同意可见性 + 步骤列表 +
    /// 状态文本（Status/LastTxId）+ 错误通道（ErrorMessage）。事件经 Dispatcher.Post
    /// marshal 到 UI 线程后调用；按钮 Click 应答后也同步刷新。</summary>
    private void UpdatePlanPanel()
    {
        if (PlanPreview is null) return;
        if (PlanPreviewList is not null) PlanPreviewList.ItemsSource = PlanPreview.PreviewSteps;
        if (PlanPreviewSection is not null) PlanPreviewSection.IsVisible = PlanPreview.IsPreviewVisible;
        if (EgressConsentSection is not null) EgressConsentSection.IsVisible = PlanPreview.IsEgressConsentVisible;
        if (EgressPromptText is not null) EgressPromptText.Text = PlanPreview.EgressPrompt;
        if (GeneratePlanButton is not null) GeneratePlanButton.IsEnabled = !PlanPreview.IsBusy;
        // I2（fixwave2）：填充按钮 busy 同步禁用（镜像 GeneratePlanButton；结构闸在
        // OnFillIntentFromIssues 首行，IsEnabled 只是视觉提示）。
        if (FillIntentButton is not null) FillIntentButton.IsEnabled = !(_editBusy || _diffBusy || PlanPreview.IsBusy);
        // 最终审查顺手修（T10）：确认/拒绝按钮仅在 PreviewReady 处理后启用（_planConfirmPending），
        // 点击应答后立即禁用至下一轮 PreviewReady——防纠正循环确认到尚未显示的计划。
        if (ConfirmPlanButton is not null) ConfirmPlanButton.IsEnabled = _planConfirmPending;
        if (RejectPlanButton is not null) RejectPlanButton.IsEnabled = _planConfirmPending;
        if (PlanStatusText is not null)
        {
            string text = PlanPreview.Status;
            if (PlanPreview.LastTxId is string tx)
                text += (text.Length > 0 ? " | " : "") + $"tx {(tx.Length > 8 ? tx[..8] : tx)}";
            PlanStatusText.Text = text;
        }
        SetPlanError(PlanPreview.ErrorMessage);
    }

    private void SetPlanError(string? message)
    {
        if (PlanError is null) return;
        PlanError.Text = message ?? "";
        PlanError.IsVisible = message is not null;
    }

    /// <summary>T6：无 audit.db 时的只读空存储占位——视图绝不创建/写入审计库。</summary>
    private sealed class EmptyAuditStore : IAuditStore
    {
        public AuditRecord Append(AuditEntry e) =>
            throw new InvalidOperationException("视图为只读回放，不允许写入审计");
        public IReadOnlyList<AuditRecord> ReadAll() => Array.Empty<AuditRecord>();
        public bool VerifyChain(out string error) { error = ""; return true; }
    }

    /// <summary>
    /// T8：无 audit.db 时的写审计占位——打开窗口不建库（沿用 T6 约定），
    /// 首次 Append（扫描/映射/裁定）才惰性创建 SqliteAuditStore 落痕。
    /// 创建前的 ReadAll/VerifyChain 表现为空库（只读回放语义不变）。
    /// </summary>
    private sealed class LazyWriteAuditStore : IAuditStore
    {
        private readonly string _dbPath;
        private readonly object _createLock = new();
        private SqliteAuditStore? _inner;

        public LazyWriteAuditStore(string dbPath) => _dbPath = dbPath;

        private SqliteAuditStore Ensure()
        {
            // 双检锁（审查修复）：并发首写（Scan 的后台 Append 与裁定的同步
            // Append 共享本占位）必须创建同一个 SqliteAuditStore——两个实例
            // 各持独立 _writeLock，事务交错读同一链头会写出重复 seq/断链。
            SqliteAuditStore? inner = _inner;
            if (inner is not null) return inner;
            lock (_createLock)
            {
                if (_inner is null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_dbPath))!);
                    _inner = new SqliteAuditStore(_dbPath);
                }
                return _inner;
            }
        }

        public AuditRecord Append(AuditEntry e) => Ensure().Append(e);
        public IReadOnlyList<AuditRecord> ReadAll() => _inner?.ReadAll() ?? Array.Empty<AuditRecord>();
        public bool VerifyChain(out string error)
        {
            error = "";
            return _inner is null || _inner.VerifyChain(out error);
        }
    }
}
