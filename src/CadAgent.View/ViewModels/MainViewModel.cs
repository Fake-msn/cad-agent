using CadAgent.Data;

namespace CadAgent.View.ViewModels;

/// <summary>
/// 主视图模型（计划4 T1）：纯 C#，无 Avalonia 依赖，可单测。
/// 构造时加载 DrawingSession；失败→ErrorMessage（Session 不可用，UI 层先行判断）。
/// </summary>
public sealed class MainViewModel
{
    private readonly DrawingSession? _session;

    public MainViewModel(string drawingPath, string projectDir)
    {
        Title = Path.GetFileName(drawingPath);
        ProjectDir = projectDir;
        try
        {
            _session = DrawingSession.Load(drawingPath);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>窗口标题：图纸文件名。</summary>
    public string Title { get; }

    /// <summary>加载失败原因；null=加载成功。</summary>
    public string? ErrorMessage { get; }

    /// <summary>项目目录（--project，默认 cwd）——.cadagent 审计/裁定等状态的根。</summary>
    public string ProjectDir { get; }

    /// <summary>加载失败时访问抛异常——调用方应先检查 ErrorMessage。</summary>
    public DrawingSession Session => _session
        ?? throw new InvalidOperationException(ErrorMessage ?? "图纸未加载");
}
