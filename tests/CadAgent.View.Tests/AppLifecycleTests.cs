using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using CadAgent.View;

namespace CadAgent.View.Tests;

/// <summary>
/// 项3 T5 App 生命周期（合并前审查）：占位窗关闭/应用退出 → 不替换主窗；
/// 成功 → 替换；异常 → 错误窗口工厂（消息逐字、红字）。纯决策与工厂可单测，
/// 不启动真实桌面生命周期（headless AppBuilder 不创建 IClassicDesktopStyleApplicationLifetime）。
/// </summary>
public class AppLifecycleTests
{
    [Fact]
    public void ShouldReplaceWindow_NormalLoad_True() =>
        Assert.True(App.ShouldReplaceWindow(placeholderClosed: false, shutdownRequested: false));

    [Fact]
    public void ShouldReplaceWindow_PlaceholderClosed_False() =>
        Assert.False(App.ShouldReplaceWindow(placeholderClosed: true, shutdownRequested: false));

    [Fact]
    public void ShouldReplaceWindow_ShutdownRequested_False() =>
        Assert.False(App.ShouldReplaceWindow(placeholderClosed: false, shutdownRequested: true));

    [AvaloniaFact]
    public void CreateErrorWindow_ShowsMessageInRedWrappedText()
    {
        Window error = App.CreateErrorWindow("图纸损坏：示例消息");
        try
        {
            Assert.Equal("CadAgent — 加载失败", error.Title);
            var text = Assert.IsType<TextBlock>(error.Content);
            Assert.Equal("图纸损坏：示例消息", text.Text);
            Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
            Assert.Same(Brushes.IndianRed, text.Foreground);
        }
        finally { error.Close(); }
    }
}
