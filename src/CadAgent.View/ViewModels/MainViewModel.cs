using CadAgent.Data;

namespace CadAgent.View.ViewModels;

/// <summary>
/// 主视图模型（计划4 T1）：纯 C#，无 Avalonia 依赖，可单测。
/// 构造时加载 DrawingSession；失败→ErrorMessage（Session 不可用，UI 层先行判断）。
/// 项3 T5：启动走 <see cref="LoadAsync"/>（后台 Load+Build+Sheets，UI 不阻塞）；同步构造保留。
/// </summary>
public sealed class MainViewModel
{
    private readonly DrawingSession? _session;

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
            // 单文件多源互斥限制见 UnparsedCensusStore 注释。
            var persisted = UnparsedCensusStore.LoadTrusted(projectDir, drawingPath, out bool verifiedEmpty);
            _session = DrawingSession.Load(drawingPath, persisted, verifiedEmpty);
            if (!verifiedEmpty && File.Exists(drawingPath))
                UnparsedCensusStore.Save(projectDir, drawingPath, _session.Unparsed);
        }
        catch (Exception ex)
        {
            // R9：working 副本无源图且无 census → Load 抛 InvalidOperationException，
            // 经本通道优雅降级（MainWindow 先行短路展示 ErrorMessage，不进入渲染/编辑）。
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>
    /// 异步加载（项3 T5）：Load（DWG 物化/working 读取/census）在后台线程完成，UI 线程不阻塞
    /// （大图 6–12s）；渲染模型/幅面登记簿仍由 MainWindow 在 UI 线程构建。
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
}
