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
/// </summary>
public sealed class MainWindow : Window
{
    private readonly string _baseStatus = "";
    private string _diffSummary = "";

    public MainWindow(MainViewModel vm)
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

        var dock = new DockPanel();
        DockPanel.SetDock(status, Dock.Bottom);
        dock.Children.Add(status);
        DockPanel.SetDock(auditPanel, Dock.Bottom);
        dock.Children.Add(auditPanel);
        DockPanel.SetDock(panel, Dock.Left);
        dock.Children.Add(panel);
        DockPanel.SetDock(issuesPanel, Dock.Right);
        dock.Children.Add(issuesPanel);
        dock.Children.Add(new DrawingCanvas(Canvas)); // LastChildFill：画布占满其余区域
        Content = dock;

        Canvas.RenderInvalidated += OnCanvasInvalidated;
        UpdateIssuesPanel();
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

    /// <summary>T6：无 audit.db 时的只读空存储占位——视图绝不创建/写入审计库。</summary>
    private sealed class EmptyAuditStore : IAuditStore
    {
        public AuditRecord Append(AuditEntry e) =>
            throw new InvalidOperationException("视图为只读回放，不允许写入审计");
        public IReadOnlyList<AuditRecord> ReadAll() => Array.Empty<AuditRecord>();
        public bool VerifyChain(out string error) { error = ""; return true; }
    }
}
