using System.Globalization;
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

    public MainWindow(MainViewModel vm, IIdentityProvider? identity = null, SemaphoreSlim? sessionGate = null)
    {
        DataContext = vm;
        _skillDirs = vm.SkillDirs;
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

        IReadOnlyList<RenderItem> items = RenderModelBuilder.Build(vm.Session);
        Canvas = new CanvasViewModel(items);
        // T8：传 session → 图层面板 = renderItems ∪ 图层表（空图层计数 0+表色样）。
        var layers = new LayersViewModel(items, Canvas, vm.Session);
        Layers = layers;

        // T9 会话闸：编辑/撤销（EditViewModel）与后台 session 读（diff/check/scan）
        // 共享同一互斥闸——off-thread 读绝不看到被编辑撕扯的 Doc（T6 承接）。
        // T10：可注入（headless 测试持闸制造确定性 in-flight 编辑）。
        sessionGate ??= new SemaphoreSlim(1, 1);

        _baseStatus = $"{vm.Title} — {items.Count} render items";
        var status = new TextBlock { Margin = new Thickness(8, 4), Text = $"{_baseStatus} | Zoom {Canvas.Zoom:0.##}x" };
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

        var issuesPanel = new DockPanel { Width = 320 };
        DockPanel.SetDock(issuesHeader, Dock.Top);
        issuesPanel.Children.Add(issuesHeader);
        DockPanel.SetDock(refresh, Dock.Top);
        issuesPanel.Children.Add(refresh);
        DockPanel.SetDock(issuesError, Dock.Top);
        issuesPanel.Children.Add(issuesError);
        issuesPanel.Children.Add(issueList); // LastChildFill

        // T6：底部审计时间线（只读回放）。audit.db 不存在 → 空存储占位，不创建库文件。
        // 最终审查修复③：损坏 audit.db → 降级空时间线 + 提示（窗口仍打开，图纸仍渲染）。
        string agentDir = Path.Combine(vm.ProjectDir, ".cadagent");
        string auditDbPath = Path.Combine(agentDir, "audit.db");
        var stateNotes = new List<string>();
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
        dock.Children.Add(new DrawingCanvas(Canvas)); // LastChildFill：画布必须是最后一个子控件，占满其余区域
        Content = dock;

        Canvas.RenderInvalidated += OnCanvasInvalidated;
        UpdateIssuesPanel();
        UpdateSemanticPanel();
        UpdateAdjudicationPanel();
        if (adjudication.ErrorMessage is not null) SetAdjudicationError(adjudication.ErrorMessage);
    }

    /// <summary>
    /// 状态文件损坏降级提示（最终审查修复③；audit.db/roles.yaml/signoffs.json）。
    /// null=全部状态健康。图纸加载失败走 MainViewModel.ErrorMessage（先行短路）。
    /// </summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>降级提示文本块（有降级时可见，位于底部面板区上方）。</summary>
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

    /// <summary>手动编辑视图模型（T9，加载成功时非 null；所有编辑经 StepEngine）。</summary>
    public EditViewModel? Edit { get; }

    /// <summary>编辑工具栏（T9：七工具 + Undo + 文本/块名输入 + 目标层下拉 + 错误文本）。</summary>
    public StackPanel? EditToolbar { get; }

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
            // T10 状态栏收尾：图纸名/实体计数（_baseStatus）+ 缩放 + 选择 + 审计摘要。
            string status = $"{_baseStatus} | Zoom {Canvas.Zoom:0.##}x";
            if (selected is not null)
                status += $" | Selected {selected.Handle} ({selected.Kind}, layer {selected.Layer})";
            if (Audit?.SelectedSummary is string summary) status += $" | Tx {summary}";
            if (_diffSummary.Length > 0) status += $" | {_diffSummary}";
            // T7：编辑警示（Yellow 闸口 / run-regression 回归警告）入状态栏尾部。
            if (_editWarning.Length > 0) status += $" | {_editWarning}";
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
        Edit.ActiveTool = tool;
        Canvas.SetPendingPoint(null);
        _moveBase = null;
        SetEditError(null);
        SetEditWarning(""); // T7：切换工具清掉上一次编辑的警示（与错误通道同口径）
    }

    /// <summary>T9 Delete 两段式确认：工具已激活且有选中 → 再按即确认删除；否则激活工具。
    /// 视图闭环 T5：_diffBusy 并列硬闸——diff 加载期间一律忽略。
    /// 视图闭环 T9：第二击先经模态确认（ConfirmDialog.ShowDialog&lt;bool&gt;；headless
    /// 测试经 ConfirmDeleteOverride seam 注入）——取消保持选中不清状态；确认后才走
    /// 既有删除执行逻辑（EditViewModel→StepEngine，无审计旁路）。</summary>
    private async Task OnDeleteClickAsync()
    {
        if (Edit is null || Canvas is null || _editBusy || _diffBusy) return;
        if (Edit.ActiveTool == EditTool.Delete && Canvas.SelectedHandle is string handle)
        {
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
        string txId = Edit.RecentTxIds[^1];
        _ = RunEditAsync(() => Edit.UndoAsync(txId));
    }

    /// <summary>T9：ChangeLayer 激活且选中实体时，层下拉选择即应用改层。
    /// 视图闭环 T5：_diffBusy 并列硬闸——diff 加载期间一律忽略。</summary>
    private void OnEditLayerSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (Edit is null || Canvas is null || _editBusy || _diffBusy) return;
        if (Edit.ActiveTool == EditTool.ChangeLayer
            && Canvas.SelectedHandle is string handle
            && EditLayerSelector?.SelectedItem is string layer)
            _ = RunEditAsync(() => Edit.ChangeLayerAsync(handle, layer));
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
                double height = double.TryParse(TextHeightInput?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double h) && h > 0 ? h : DefaultTextHeight;
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
                double scale = double.TryParse(BlockScaleInput?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double s) && s > 0 ? s : 1;
                double rotation = double.TryParse(BlockRotationInput?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double deg) ? deg * Math.PI / 180.0 : 0;
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
                    _ = RunEditAsync(() => Edit.MoveAsync(moveHandle, wx - bx, wy - by));
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
    }

    /// <summary>
    /// T9：编辑/撤销完成（成功或失败）→ 重建渲染模型 → Canvas.Refresh +
    /// Layers.Refresh（T4 承接）+ 图层列表/层下拉/状态栏计数/错误/Undo 可用性。
    /// </summary>
    private void RefreshAfterEdit(MainViewModel vm)
    {
        if (Canvas is null || Layers is null || Edit is null) return;
        IReadOnlyList<RenderItem> items = RenderModelBuilder.Build(vm.Session);
        Canvas.Refresh(items);
        Layers.Refresh(items, vm.Session); // T8：图层表并集（编辑清空某层后空层仍显示）
        if (LayerList is not null) LayerList.ItemsSource = Layers.Layers;
        if (EditLayerSelector is not null)
            EditLayerSelector.ItemsSource = Layers.Layers.Select(l => l.Name).ToList();
        _baseStatus = $"{vm.Title} — {items.Count} render items";
        _moveBase = null;
        // 编辑使旧对比失效：Diff 切换回退（关分支幂等清叠加/摘要），避免残留勾选态。
        if (DiffToggle is { IsChecked: true }) DiffToggle.IsChecked = false;
        _diffSummary = "";
        Canvas.Invalidate(); // 状态栏计数刷新
        SetEditError(Edit.ErrorMessage);
        if (UndoButton is not null) UndoButton.IsEnabled = Edit.RecentTxIds.Count > 0;
        // T9 承接闭环：审计时间线 live-rebuild（编辑/撤销/回滚后无需重开窗口）
        Audit?.Rebuild();
    }

    private void SetEditError(string? message)
    {
        if (EditError is null) return;
        EditError.Text = message ?? "";
        EditError.IsVisible = message is not null;
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
