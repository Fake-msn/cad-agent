using CadAgent.Core;
using CadAgent.Data;
using CadAgent.View.Rendering;

namespace CadAgent.View.ViewModels;

/// <summary>
/// 主视图模型（计划4 T1）：纯 C#，无 Avalonia 依赖，可单测。
/// 构造时加载 DrawingSession 并构建渲染模型/幅面登记簿；失败→ErrorMessage
/// （Session/Items 不可用，UI 层先行判断）。
/// 项3 T5 + 终审：启动走 <see cref="LoadAsync"/>——Load + <see cref="RenderModelBuilder.Build"/> +
/// <see cref="DrawingSession.Sheets"/> 全在后台线程完成（大图 Load 6–12s + Build 0.2–1s 均不阻塞
/// UI）；MainWindow 直接消费 <see cref="Items"/>/<see cref="Sheets"/>，不在 UI 线程重建。
/// 同步构造保留（既有测试/CLI 零改动），同样产出 Items/Sheets。
/// </summary>
public sealed class MainViewModel
{
    private readonly DrawingSession? _session;
    private IReadOnlyList<RenderItem> _items = Array.Empty<RenderItem>();
    private IReadOnlyList<SheetRecord> _sheets = Array.Empty<SheetRecord>();

    public MainViewModel(string drawingPath, string projectDir, IReadOnlyList<string>? skillDirs = null)
    {
        Title = Path.GetFileName(drawingPath);
        ProjectDir = projectDir;
        SkillDirs = skillDirs ?? Array.Empty<string>();
        try
        {
            // census sidecar 接线（防假 Green T3，等价 CLI LoadWithCensus）：working DXF 丢失
            // TCH 对象后二次加载仍恢复 census（显示不完整警示依据）；未信任且源在→回写 sidecar
            // （含已验证空 census——二次打开同图免重读源 DWG，项3 §5.1 R9 语义扩展）。
            // 单文件多源互斥限制见 UnparsedCensusStore 注释；Save 尽力写（只读项目不抛）。
            var persisted = UnparsedCensusStore.LoadTrusted(projectDir, drawingPath, out bool verifiedEmpty);
            _session = DrawingSession.Load(drawingPath, persisted, verifiedEmpty);
            if (!verifiedEmpty && File.Exists(drawingPath))
                _ = UnparsedCensusStore.Save(projectDir, drawingPath, _session.Unparsed);
            // 项3 终审 Important 2：重活（Build + 幅面登记簿）与 Load 同处后台任务
            // （LoadAsync 的 Task.Run），结果由 MainWindow 消费（Items/Sheets）。
            _items = RenderModelBuilder.Build(_session);
            _sheets = _session.Sheets;
        }
        catch (Exception ex)
        {
            // R9：working 副本无源图且无 census → Load 抛 InvalidOperationException，
            // 经本通道优雅降级（MainWindow 先行短路展示 ErrorMessage，不进入渲染/编辑）。
            ErrorMessage = ex.Message;
        }
        // 重活执行线程（终审测试 seam：LoadAsync 路径应 != 调用线程；同步构造=当前线程）。
        BuildThreadId = Environment.CurrentManagedThreadId;
        BuildCompleted = ErrorMessage is null;
    }

    /// <summary>
    /// 异步加载（项3 T5 + 终审）：Load（DWG 物化/working 读取/census）+ RenderModelBuilder.Build
    /// + 幅面登记簿全部在后台线程完成，UI 线程不阻塞（大图 Load 6–12s + Build 0.2–1s）。
    /// 失败语义不变：构造内既有 catch 把异常落 ErrorMessage，本方法不抛（调用方仍按 vm.ErrorMessage 分支）。
    /// 同步构造保留（既有测试/CLI 零改动）。
    /// </summary>
    public static Task<MainViewModel> LoadAsync(string drawingPath, string projectDir,
        IReadOnlyList<string>? skillDirs = null) =>
        Task.Run(() => new MainViewModel(drawingPath, projectDir, skillDirs));

    /// <summary>窗口标题：图纸文件名。</summary>
    public string Title { get; }

    /// <summary>--skill 目录（可重复，出现序；T10）——Issues/Adjudication/Semantic 接线用。</summary>
    public IReadOnlyList<string> SkillDirs { get; }

    /// <summary>加载失败原因；null=加载成功。</summary>
    public string? ErrorMessage { get; }

    /// <summary>项目目录（--project，默认 cwd）——.cadagent 审计/裁定等状态的根。</summary>
    public string ProjectDir { get; }

    /// <summary>加载失败时访问抛异常——调用方应先检查 ErrorMessage。</summary>
    public DrawingSession Session => _session
        ?? throw new InvalidOperationException(ErrorMessage ?? "图纸未加载");

    /// <summary>
    /// 全模型渲染图元（<see cref="RenderModelBuilder.Build"/> 产物）：加载期（LoadAsync 后台线程）
    /// 构建一次，MainWindow 直接消费并按幅面裁剪/组合，不在 UI 线程重建。失败时为空。
    /// </summary>
    public IReadOnlyList<RenderItem> Items => _items;

    /// <summary>幅面登记簿（<see cref="DrawingSession.Sheets"/>，首幅面/布局）：与 Items 同批构建。
    /// 失败时为空（MainWindow 走 ErrorMessage 短路，不读）。</summary>
    public IReadOnlyList<SheetRecord> Sheets => _sheets;

    /// <summary>重活（Load+Build+Sheets）完成时所在线程 id：LoadAsync 路径可断言 != 调用线程。</summary>
    internal int BuildThreadId { get; }

    /// <summary>重活是否全部成功（Items/Sheets 已就绪）；镜像 ErrorMessage is null。</summary>
    internal bool BuildCompleted { get; }
}
