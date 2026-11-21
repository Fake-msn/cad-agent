using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using CadAgent.Core;
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
/// </summary>
public sealed class MainWindow : Window
{
    /// <summary>纠偏下拉可选语义类（Unknown 不是确认目标，排除）。</summary>
    private static readonly string[] ClassOptions =
        Enum.GetNames<SemanticClass>().Where(n => n != nameof(SemanticClass.Unknown)).ToArray();

    private readonly string _baseStatus = "";
    private string _diffSummary = "";
    private TextBlock? _semanticHeader;

    public MainWindow(MainViewModel vm, IIdentityProvider? identity = null)
    {
        DataContext = vm;
        Title = vm.Title;
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

        IReadOnlyList<RenderItem> items = RenderModelBuilder.Build(vm.Session);
        Canvas = new CanvasViewModel(items);
        var layers = new LayersViewModel(items, Canvas);
        Layers = layers;

        _baseStatus = $"{vm.Title} — {items.Count} render items";
        var status = new TextBlock { Margin = new Thickness(8, 4), Text = _baseStatus };
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

        // T5：右栏问题清单（in-process check；skill 目录接线留 T8/T10，当前 builtin-only）。
        var issues = new IssuesViewModel(vm.Session, vm.ProjectDir, Array.Empty<string>(), Canvas);
        Issues = issues;
        issues.IssuesChanged += UpdateIssuesPanel;

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

        var issuesPanel = new DockPanel { Width = 320 };
        DockPanel.SetDock(issuesHeader, Dock.Top);
        issuesPanel.Children.Add(issuesHeader);
        DockPanel.SetDock(refresh, Dock.Top);
        issuesPanel.Children.Add(refresh);
        DockPanel.SetDock(issuesError, Dock.Top);
        issuesPanel.Children.Add(issuesError);
        issuesPanel.Children.Add(issueList); // LastChildFill

        // T6：底部审计时间线（只读回放）。audit.db 不存在 → 空存储占位，不创建库文件。
        string agentDir = Path.Combine(vm.ProjectDir, ".cadagent");
        string auditDbPath = Path.Combine(agentDir, "audit.db");
        IAuditStore auditStore = File.Exists(auditDbPath)
            ? new SqliteAuditStore(auditDbPath)
            : new EmptyAuditStore();
        var audit = new AuditViewModel(auditStore,
            new SnapshotStore(Path.Combine(agentDir, "snapshots")), vm.Session, Canvas);
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
        IIdentityProvider signoffIdentity = identity
            ?? new WindowsIdentityProvider(RoleConfig.Load(Path.Combine(agentDir, "roles.yaml")));
        var signoff = new SignoffViewModel(auditStore,
            new ReviewSignoffStore(Path.Combine(agentDir, "signoffs.json")),
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
        IAuditStore writeAudit = auditStore is SqliteAuditStore
            ? auditStore
            : new LazyWriteAuditStore(auditDbPath);

        // T8 纠偏 tab：Scan → 不明确图层列表（证据 + 类下拉 + Map + Isolate）。
        // skill 目录接线留 T10（当前 builtin-only，与 T5 问题清单同口径）。
        var semantic = new SemanticViewModel(vm.Session, vm.ProjectDir, Canvas, writeAudit, signoffIdentity);
        Semantic = semantic;
        semantic.SemanticChanged += UpdateSemanticPanel;

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
        var adjudication = new AdjudicationViewModel(vm.ProjectDir, Array.Empty<string>(), writeAudit, signoffIdentity);
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

        // T8：左栏三 tab（图层默认选中，T4 行为不变；未选中 tab 内容不进入视觉树）。
        var tabs = new TabControl { Width = 220 };
        tabs.Items.Add(new TabItem { Header = "图层", Content = panel });
        tabs.Items.Add(new TabItem { Header = "纠偏", Content = semanticPanel });
        tabs.Items.Add(new TabItem { Header = "裁定", Content = adjudicationPanel });
        LeftTabs = tabs;

        var dock = new DockPanel();
        DockPanel.SetDock(status, Dock.Bottom);
        dock.Children.Add(status);
        DockPanel.SetDock(auditPanel, Dock.Bottom);
        dock.Children.Add(auditPanel);
        DockPanel.SetDock(signoffPanel, Dock.Bottom);
        dock.Children.Add(signoffPanel);
        DockPanel.SetDock(tabs, Dock.Left);
        dock.Children.Add(tabs);
        DockPanel.SetDock(issuesPanel, Dock.Right);
        dock.Children.Add(issuesPanel);
        dock.Children.Add(new DrawingCanvas(Canvas)); // LastChildFill：画布占满其余区域
        Content = dock;

        Canvas.RenderInvalidated += OnCanvasInvalidated;
        UpdateIssuesPanel();
        UpdateSemanticPanel();
        UpdateAdjudicationPanel();
        if (adjudication.ErrorMessage is not null) SetAdjudicationError(adjudication.ErrorMessage);
    }

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

    /// <summary>左栏三 tab 容器（T8：图层/纠偏/裁定；加载成功时非 null）。</summary>
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

    /// <summary>行模板：复选框（可见性）+ 色样 + 名称（计数）。Click → Toggle。</summary>
    private static CheckBox BuildLayerRow(LayerEntry entry, LayersViewModel layers)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
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
        var check = new CheckBox
        {
            Content = row,
            IsChecked = entry.IsVisible,
            Margin = new Thickness(4, 2)
        };
        check.Click += (_, _) => layers.Toggle(entry.Name);
        return check;
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
    /// </summary>
    private Expander BuildIssueRow(IssueEntry entry)
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
                // 黄底黑字保证对比度；红/灰底白字。
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
        return expander;
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

    /// <summary>画布状态变化（选择/图层可见性/视图）→ 状态栏 + 复选框同步。</summary>
    private void OnCanvasInvalidated()
    {
        if (Status is not null)
        {
            RenderItem? selected = Canvas!.SelectedItem;
            string status = selected is null
                ? _baseStatus
                : $"{_baseStatus} | Selected {selected.Handle} ({selected.Kind}, layer {selected.Layer})";
            if (Audit?.SelectedSummary is string summary) status += $" | Tx {summary}";
            if (_diffSummary.Length > 0) status += $" | {_diffSummary}";
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
    /// </summary>
    private async Task ApplyDiffAsync(string txId)
    {
        if (DiffToggle is null || Canvas is null || Audit is null) return;
        DiffToggle.IsEnabled = false;
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
        finally { DiffToggle.IsEnabled = true; }
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
        try { await Semantic.ScanAsync(Array.Empty<string>()); }
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
