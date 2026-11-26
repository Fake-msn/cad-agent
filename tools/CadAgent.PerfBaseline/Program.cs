using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.View.Controls;
using CadAgent.View.Rendering;
using CadAgent.View.ViewModels;
using CSMath;

namespace CadAgent.PerfBaseline;

/// <summary>
/// 项3 T7 大图性能基线工具（net8 控制台，非测试套件；手工运行）。
/// 模式：<c>--synth 20k|80k|200k</c>（合成线 + 20% 文字，物化临时 DXF 后经 DrawingSession.Load）
/// 与 <c>--drawing &lt;path&gt;</c>（真实图，源首次载入走 %TEMP% 副本，标本目录零写入）。
/// 未给模式时依次跑 20k/80k/200k。指标：load（源首载/working 二载）、build、hash 冷/重复、
/// snapshot PNG、交互帧（同一 DrawingCanvas 首帧建缓存 → PanBy(32,0) 再渲染=blit 路径）、
/// Refined 首帧、hit×2000、托管内存。阈值只标注 PASS/TARGET，不 hard fail。
/// 控制台输出强制 ASCII（Windows GBK 终端安全）；markdown 以 UTF-8 写出。
/// </summary>
internal static class Program
{
    private const double InteractiveTargetMs = 50;
    private const double HitTargetMs = 2;
    private const double RefinedTargetMs = 150;
    private const double SnapshotTargetMs = 150;
    private const int FrameWidth = 1600;
    private const int FrameHeight = 1000;

    private static readonly List<string> Markdown = new();

    private static int Main(string[] args)
    {
        string? outPath = ArgValue(args, "--out");
        IReadOnlyList<string> drawings = ArgValues(args, "--drawing");
        string? synth = ArgValue(args, "--synth");

        Console.OutputEncoding = Encoding.UTF8;
        EnsureAvalonia();

        Markdown.Add("# CadAgent 大图性能基线（项3 T7 工具）");
        Markdown.Add("");
        Markdown.Add($"> 运行：{DateTime.Now:yyyy-MM-dd HH:mm:ss} · {RuntimeInformation.FrameworkDescription}" +
                     $" · {RuntimeInformation.OSDescription} · CPU={Environment.ProcessorCount} · headless Skia（软件渲染）");
        Markdown.Add($"> 阈值（不 hard fail）：交互帧 <{InteractiveTargetMs:0}ms、命中 <{HitTargetMs:0}ms/次、" +
                     $"Refined 帧/快照 <{RefinedTargetMs:0}ms；合成仅 200k 计入交互/命中目标（spec §7）。");
        Markdown.Add($"> 帧：{FrameWidth}×{FrameHeight} headless 窗口内同一 DrawingCanvas；Refined=真冷首帧（Refined 全量+缓存位图重建，不含 PNG 编码），" +
                     "交互=PanBy(32,0) 后 blit 复用缓存（128px 外扩内不重绘）。");
        Markdown.Add("> 快照=RenderPng（渲染 + PNG 编码；编码量级见同行 refined 的 +encode 列）。" +
                     "load src=源 DWG 首载（%TEMP% 同字节副本，OS 文件缓存已暖，非磁盘冷载）；load working=二开（working+侧车）。");
        Markdown.Add("");
        Markdown.Add("| case | load src | load working | build | hash cold | hash repeat | snapshot | refined frame(+encode) " +
                     "| interactive frame | hit x2000 | per hit | managed | status |");
        Markdown.Add("|---|---|---|---|---|---|---|---|---|---|---|---|---|");

        try
        {
            if (drawings.Count > 0)
            {
                // 多张真实图逐张独立基准（各自 %TEMP% 副本/侧车，互不影响）。
                foreach (string d in drawings) RunDrawing(Path.GetFullPath(d));
            }
            else if (synth is not null)
            {
                RunSynth(ParseSynth(synth));
            }
            else
            {
                RunSynth(20000);
                RunSynth(80000);
                RunSynth(200000);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAILED: " + Ascii(ex.GetType().Name + ": " + ex.Message));
            return 1;
        }

        if (outPath is not null)
        {
            string full = Path.GetFullPath(outPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, string.Join(Environment.NewLine, Markdown) + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Console.WriteLine("markdown: " + full);
        }
        return 0;
    }

    // ---- 模式 ----

    private static void RunDrawing(string source)
    {
        // 源首载在 %TEMP% 同一字节副本上测（标本目录只读：不新增/不删除任何文件）；
        // 二开（working 载 + 侧车信任）也在副本上做，指纹绑定副本路径。
        string tempRoot = Path.Combine(Path.GetTempPath(), "cadagent-perf", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string copy = Path.Combine(tempRoot, Path.GetFileName(source));
        try
        {
            File.Copy(source, copy, overwrite: true);
            BenchDrawing(source, copy, tempRoot);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void RunSynth(int entityCount)
    {
        string dir = Path.Combine(Path.GetTempPath(), "cadagent-perf", "synth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string dxf = Path.Combine(dir, $"synth-{entityCount}.dxf");
        try
        {
            double writeMs = 0;
            var metrics = new Metrics
            {
                Name = $"synth {entityCount / 1000}k",
                Kind = "synth",
            };
            WriteSynthDxf(dxf, entityCount, ms => writeMs = ms);
            metrics.WriteMs = writeMs;
            metrics.SourceBytes = new FileInfo(dxf).Length;

            DrawingSession session = null!;
            metrics.LoadWorkingMs = Ms(() => session = DrawingSession.Load(dxf));
            metrics.CensusState = "n/a (plain dxf)";
            BenchSession(metrics, session, requireTargets: entityCount >= 200000);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    /// <summary>合成 DXF：count 图元，每 5 个 1 个 TextEntity（20%），其余 Line；种子固定。</summary>
    private static void WriteSynthDxf(string path, int count, Action<double> reportWriteMs)
    {
        var doc = new CadDocument();
        doc.Header.Version = ACadVersion.AC1027;
        var rnd = new Random(42);
        for (int i = 0; i < count; i++)
        {
            double x = rnd.NextDouble() * 2000;
            double y = rnd.NextDouble() * 1500;
            if (i % 5 == 0)
            {
                doc.Entities.Add(new TextEntity
                {
                    InsertPoint = (XYZ)new XY(x, y),
                    Height = 2.5,
                    Value = "T" + (i % 1000),
                });
            }
            else
            {
                doc.Entities.Add(new Line
                {
                    StartPoint = (XYZ)new XY(x, y),
                    EndPoint = (XYZ)new XY(x + rnd.NextDouble() * 40, y + rnd.NextDouble() * 40),
                });
            }
        }
        var sw = Stopwatch.StartNew();
        using (var fs = File.Create(path)) DxfWriter.Write(fs, doc);
        sw.Stop();
        reportWriteMs(sw.Elapsed.TotalMilliseconds);
    }

    // ---- 真实图：载入/二开语义 ----

    private static void BenchDrawing(string specimenPath, string copy, string projectDir)
    {
        var metrics = new Metrics
        {
            Name = Path.GetFileName(specimenPath),
            Kind = "drawing",
            SourceBytes = new FileInfo(copy).Length,
        };

        long lenBefore = new FileInfo(copy).Length;
        DateTime mtimeBefore = File.GetLastWriteTimeUtc(copy);

        // 首载：无 working、无侧车 → DWG 解析 + census + working 物化（真实冷载）。
        UnparsedCensus persisted = UnparsedCensusStore.LoadTrusted(projectDir, copy, out bool verifiedEmpty);
        DrawingSession? first = null;
        UnparsedCensus firstCensus = UnparsedCensus.Empty;
        metrics.LoadSourceMs = Ms(() =>
        {
            first = DrawingSession.Load(copy, persisted, verifiedEmpty);
            firstCensus = first.Unparsed;
        });
        metrics.UnparsedCount = firstCensus.TotalCount;

        // 侧车回写（CLI/View 同口径）后二开：working 载 + 侧车 census（Empty 且已验证 → 不重读源）。
        UnparsedCensusStore.Save(projectDir, copy, firstCensus);
        first = null; // 释放首载文档（内存口径=应用单文档，非两倍）
        UnparsedCensus persisted2 = UnparsedCensusStore.LoadTrusted(projectDir, copy, out bool verifiedEmpty2);
        DrawingSession second = null!;
        metrics.LoadWorkingMs = Ms(() => second = DrawingSession.Load(copy, persisted2, verifiedEmpty2));
        metrics.CensusState = verifiedEmpty2
            ? "verified-empty (trusted, no source reread)"
            : persisted2.TotalCount > 0
                ? $"persisted non-empty ({persisted2.TotalCount})"
                : "none (R9 fallback)";

        metrics.SourceUntouched = new FileInfo(copy).Length == lenBefore
            && File.GetLastWriteTimeUtc(copy) == mtimeBefore;

        // 结构性证明「二开不重读源」：源移走后再载应成功（working + 侧车自足）。
        string held = copy + ".held";
        File.Move(copy, held);
        try
        {
            DrawingSession third = null!;
            metrics.LoadNoSourceMs = Ms(() => third = DrawingSession.Load(copy, persisted2, verifiedEmpty2));
            metrics.NoSourceReread = true;
        }
        catch (Exception ex)
        {
            metrics.NoSourceReread = false;
            Console.WriteLine("no-source reload failed: " + Ascii(ex.Message));
        }
        finally
        {
            File.Move(held, copy);
        }

        BenchSession(metrics, second, requireTargets: true);
    }

    // ---- 共用指标 ----

    private static void BenchSession(Metrics m, DrawingSession session, bool requireTargets)
    {
        m.TopEntities = session.Doc.Entities.Count;

        IReadOnlyList<RenderItem> items = null!;
        m.BuildMs = Ms(() => items = RenderModelBuilder.Build(session));
        m.ItemCount = items.Count;
        m.HatchPoints = items.Where(i => i.Kind == RenderKind.Hatch).Sum(i => i.Points.Count);
        m.TextItems = items.Count(i => i.Kind == RenderKind.Text);
        var bounds = RenderModelBuilder.Bounds(items);

        // hash：冷 → 重复（同修订应命中缓存，≈0）→ InvalidateHash 后再算（契约证据）。
        string hashCold = "";
        m.HashColdMs = Ms(() => hashCold = session.ComputeHash());
        string hashRepeat = "";
        m.HashRepeatMs = Ms(() => hashRepeat = session.ComputeHash());
        m.HashEqual = hashCold == hashRepeat;
        if (session is IHashRevision rev)
        {
            m.HashRevision = rev.Revision;
            rev.InvalidateHash();
            m.HashInvalidateMs = Ms(() => _ = session.ComputeHash());
        }

        var provider = new RenderSnapshotProvider(session);
        string sheetId = session.Sheets[0].SheetId; // 幅面注册（懒构建）不计入快照耗时
        byte[] png = null!;
        m.SnapshotMs = Ms(() => png = provider.RenderPng(sheetId, bounds, FrameWidth, FrameHeight));
        m.SnapshotKb = png.Length / 1024;
        // 信息项：重复快照（字体/几何一次性初始化后）；快照耗时方差较大，最暖一次即稳态近似。
        double warm1 = Ms(() => _ = provider.RenderPng(sheetId, bounds, FrameWidth, FrameHeight));
        double warm2 = Ms(() => _ = provider.RenderPng(sheetId, bounds, FrameWidth, FrameHeight));
        m.SnapshotWarmMs = Math.Min(warm1, warm2);

        BenchFrames(m, items, bounds);

        var vm = new CanvasViewModel(items);
        vm.Fit(bounds, FrameWidth, FrameHeight);
        var rnd = new Random(1);
        int hits = 0;
        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < 2000; i++)
        {
            double px = bounds.MinX + rnd.NextDouble() * (bounds.MaxX - bounds.MinX);
            double py = bounds.MinY + rnd.NextDouble() * (bounds.MaxY - bounds.MinY);
            if (vm.HitTest(px, py, 5 / vm.Scale) is not null) hits++;
        }
        sw.Stop();
        m.HitTotalMs = sw.Elapsed.TotalMilliseconds;
        m.HitPerCallMs = m.HitTotalMs / 2000.0;
        m.HitCount = hits;

        m.ManagedMb = GC.GetTotalMemory(forceFullCollection: true) / (1024 * 1024);

        Emit(m, requireTargets);
    }

    /// <summary>
    /// 帧计时（同一 DrawingCanvas 置于 headless 窗口内，保证位图缓存路径生效）。
    /// headless 适配：Show/布局泵会先消耗一帧（缓存已烘焙），故画布在宿主窗口已显示后才挂入，
    /// 手动 Measure/Arrange 而不泵消息——计时首帧即「真冷 Refined 全量绘制 + 缓存位图重建」
    /// （冷文字缓存 + RTB 分配，与生产首帧同路径）；随后 PanBy(32,0) 两次再渲染 = 平移 blit
    /// （真实交互路径，128px 外扩内不重绘）。帧计时不含 PNG 编码；refined 另列编码耗时
    /// （旧基线 frame 含编码）。
    /// </summary>
    private static void BenchFrames(Metrics m, IReadOnlyList<RenderItem> items,
        (double MinX, double MinY, double MaxX, double MaxY) bounds)
    {
        var vm = new CanvasViewModel(items);
        var canvas = new DrawingCanvas(vm);
        var host = new Window
        {
            Width = FrameWidth,
            Height = FrameHeight,
            SystemDecorations = SystemDecorations.None,
            Content = new Panel(),
        };
        host.Show();
        Dispatcher.UIThread.RunJobs();
        host.Content = canvas; // 挂树（TopLevel 非空 → 缓存路径）但不泵消息，首帧留给计时
        canvas.Measure(new Size(FrameWidth, FrameHeight));
        canvas.Arrange(new Rect(0, 0, FrameWidth, FrameHeight));
        vm.Fit(bounds, FrameWidth, FrameHeight);
        try
        {
            using var rtb = new RenderTargetBitmap(new PixelSize(FrameWidth, FrameHeight));
            m.RefinedFrameMs = Ms(() => rtb.Render(canvas));
            m.RefinedEncodeMs = Ms(() =>
            {
                using var stream = new MemoryStream();
                rtb.Save(stream);
            });

            vm.PanBy(32, 0);
            Dispatcher.UIThread.RunJobs();
            m.InteractiveFrameMs = Ms(() => rtb.Render(canvas));
            vm.PanBy(32, 0);
            Dispatcher.UIThread.RunJobs();
            m.InteractiveFrame2Ms = Ms(() => rtb.Render(canvas));

            // 信息项：稳态精绘重建（JIT/Skia 已暖）——对照冷首帧，判断冷帧成本是否含一次性预热。
            vm.ZoomAt(1.0001, FrameWidth / 2.0, FrameHeight / 2.0);
            Dispatcher.UIThread.RunJobs();
            m.RefinedWarmMs = Ms(() => rtb.Render(canvas));
        }
        finally
        {
            host.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    // ---- 输出 ----

    private static void Emit(Metrics m, bool requireTargets)
    {
        m.Targets = requireTargets;
        string status = Status(m);
        Markdown.Add($"| {m.Name} | {Num(m.LoadSourceMs)} | {Num(m.LoadWorkingMs)} | " +
                     $"{m.BuildMs:0}ms -> {m.ItemCount} items | {m.HashColdMs:0.#}ms | {m.HashRepeatMs:0.###}ms | " +
                     $"{m.SnapshotMs:0}ms ({m.SnapshotKb}KB, warm {m.SnapshotWarmMs:0}ms) | " +
                     $"{m.RefinedFrameMs:0.#}ms (+{m.RefinedEncodeMs:0.#}ms enc, warm {m.RefinedWarmMs:0.#}ms) | " +
                     $"{m.InteractiveFrameMs:0.##}ms / {m.InteractiveFrame2Ms:0.##}ms | {m.HitTotalMs:0}ms ({m.HitCount}) | " +
                     $"{m.HitPerCallMs:0.###}ms | {m.ManagedMb}MB | {status} |");

        Markdown.Add("");
        Markdown.Add($"- {m.Name}: entities={m.TopEntities} items={m.ItemCount} text={m.TextItems} " +
                     $"hatchPts={m.HatchPoints} census={m.CensusState} unparsed={m.UnparsedCount} " +
                     $"hashEqual={m.HashEqual}");
        if (m.Kind == "drawing")
        {
            Markdown.Add($"- {m.Name}: sourceUntouched={m.SourceUntouched} noSourceReread={m.NoSourceReread} " +
                         $"loadNoSource={Num(m.LoadNoSourceMs)} size={m.SourceBytes / (1024 * 1024)}MB");
        }
        else
        {
            Markdown.Add($"- {m.Name}: dxfWrite={m.WriteMs:0}ms size={m.SourceBytes / 1024}KB");
        }
        Markdown.Add($"- {m.Name}: hashRevision={m.HashRevision} hashInvalidate={Num(m.HashInvalidateMs)} " +
                     $"refinedWarm={m.RefinedWarmMs:0.#}ms snapshotWarm={m.SnapshotWarmMs:0}ms");
        Markdown.Add("");

        Console.WriteLine(Ascii($"[{m.Name}] loadSrc={Num(m.LoadSourceMs)} loadWorking={Num(m.LoadWorkingMs)} " +
                                $"build={m.BuildMs:0}ms items={m.ItemCount} hashCold={m.HashColdMs:0.#}ms " +
                                $"hashRepeat={m.HashRepeatMs:0.###}ms snapshot={m.SnapshotMs:0}ms " +
                                $"refined={m.RefinedFrameMs:0.#}ms(+{m.RefinedEncodeMs:0.#}) " +
                                $"interactive={m.InteractiveFrameMs:0.##}/{m.InteractiveFrame2Ms:0.##}ms " +
                                $"hit2000={m.HitTotalMs:0}ms per={m.HitPerCallMs:0.###}ms mem={m.ManagedMb}MB " +
                                $"census={m.CensusState} untouched={m.SourceUntouched} noReread={m.NoSourceReread} " +
                                $"status={status}"));
    }

    private static string Status(Metrics m)
    {
        var parts = new List<string>();
        bool frameOk = m.InteractiveFrameMs < InteractiveTargetMs || m.InteractiveFrame2Ms < InteractiveTargetMs;
        bool hitOk = m.HitPerCallMs < HitTargetMs;
        bool refinedOk = m.RefinedFrameMs < RefinedTargetMs;
        bool snapOk = m.SnapshotMs < SnapshotTargetMs;
        string info = $"INFO (frame {(frameOk ? "PASS" : "TARGET")}, hit {(hitOk ? "PASS" : "TARGET")}, " +
                      $"refined {(refinedOk ? "PASS" : "TARGET")}, snapshot {(snapOk ? "PASS" : "TARGET")})";
        if (!m.Targets)
        {
            parts.Add(info);
            return string.Join("; ", parts);
        }
        // 真实图：全部目标（含二开语义/哈希）。合成 200k：spec §7 只要求交互帧 + 命中。
        parts.Add(frameOk ? "frame PASS" : "frame TARGET");
        parts.Add(hitOk ? "hit PASS" : "hit TARGET");
        if (m.Kind == "drawing")
        {
            parts.Add(refinedOk ? "refined PASS" : "refined TARGET");
            parts.Add(snapOk ? "snapshot PASS" : "snapshot TARGET");
            parts.Add(m.HashRepeatMs < 1 ? "hash PASS" : "hash TARGET");
            parts.Add(m.SourceUntouched == true ? "srcUntouched PASS" : "srcUntouched TARGET");
            parts.Add(m.NoSourceReread == true ? "noReread PASS" : "noReread TARGET");
        }
        else
        {
            parts.Add(info);
        }
        return string.Join("; ", parts);
    }

    // ---- 基础设施 ----

    private static string Num(double ms) => ms < 0 ? "n/a" : $"{ms:0}ms";

    private static double Ms(Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    private static IReadOnlyList<string> ArgValues(string[] args, string name)
    {
        var values = new List<string>();
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) values.Add(args[i + 1]);
        return values;
    }

    private static int ParseSynth(string raw)
    {
        string v = raw.Trim().ToLowerInvariant();
        int factor = v.EndsWith('k') ? 1000 : 1;
        if (v.EndsWith('k')) v = v[..^1];
        if (!int.TryParse(v, out int n) || n <= 0) throw new ArgumentException($"无效 --synth: {raw}（例 20k/80k/200k）");
        return n * factor;
    }

    private static bool _platformReady;

    /// <summary>
    /// headless 平台一次初始化（UseHeadlessDrawing=false + Skia）：真实 Skia 帧，
    /// 与 View.Tests/HeadlessTestApp、RenderSnapshotProvider 生产 headless 入口同模式。
    /// </summary>
    private static void EnsureAvalonia()
    {
        if (_platformReady) return;
        AppBuilder.Configure<PerfApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .SetupWithoutStarting();
        _platformReady = true;
    }

    private sealed class PerfApp : Application
    {
        public override void Initialize() => Styles.Add(new FluentTheme());
    }

    /// <summary>控制台 ASCII 安全（Windows GBK 终端/重定向不因中文图名乱码/抛异常）。</summary>
    private static string Ascii(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text) sb.Append(c <= 0x7E ? c : '?');
        return sb.ToString();
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine("cleanup skipped: " + Ascii(ex.GetType().Name));
        }
    }

    private sealed class Metrics
    {
        public string Name = "";
        public string Kind = "";
        public bool Targets;
        public long SourceBytes;
        public double LoadSourceMs = -1;
        public double LoadWorkingMs = -1;
        public double LoadNoSourceMs = -1;
        public double WriteMs = -1;
        public bool? SourceUntouched;
        public bool? NoSourceReread;
        public string CensusState = "";
        public int UnparsedCount = -1;
        public int TopEntities;
        public double BuildMs;
        public int ItemCount;
        public int HatchPoints;
        public int TextItems;
        public double HashColdMs;
        public double HashRepeatMs;
        public double HashInvalidateMs = -1;
        public long HashRevision = -1;
        public bool HashEqual;
        public double SnapshotMs;
        public int SnapshotKb;
        public double SnapshotWarmMs;
        public double RefinedFrameMs;
        public double RefinedEncodeMs;
        public double RefinedWarmMs;
        public double InteractiveFrameMs;
        public double InteractiveFrame2Ms;
        public double HitTotalMs;
        public double HitPerCallMs;
        public int HitCount;
        public long ManagedMb;
    }
}
