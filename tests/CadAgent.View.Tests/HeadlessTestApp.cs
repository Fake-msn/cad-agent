using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

// Avalonia.Headless.XUnit 11.3.2 setup: [AvaloniaFact] tests run on the Avalonia
// dispatcher; the assembly attribute points at the BuildAvaloniaApp entry below.
// UseHeadlessDrawing=false -> real Skia rendering (via Avalonia.Desktop reference),
// so CaptureRenderedFrame returns actual pixels and Render exceptions surface.
[assembly: AvaloniaTestApplication(typeof(CadAgent.View.Tests.HeadlessTestApp))]

namespace CadAgent.View.Tests;

/// <summary>Headless test application (plan 4 task 3): Fluent theme + Skia frames.</summary>
public sealed class HeadlessTestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessTestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia(); // UseHeadlessDrawing=false 不自动注册渲染后端，需显式 Skia
}
