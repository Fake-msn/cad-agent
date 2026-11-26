using System.Diagnostics;
using System.Globalization;
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
/// snapshot PNG、交互帧（同一 DrawingCanvas 首帧建缓存 → PanBy(32,0) 再渲染=blit 路径，3 次取中位）、
/// 交互档失效重建（暖缓存后强制 Interactive + 缩放失效 → 一次全量重绘，3 次取中位，spec §8 <50ms）、
/// Refined 冷首帧、hit×2000（3 次取中位）、托管内存。阈值只标注 PASS/TARGET，不 hard fail。
/// 控制台输出强制 ASCII（Windows GBK 终端安全）；markdown 以 UTF-8 写出（表格行连续，明细在表后）。
/// </summary>
internal static class Program
{
    private const double InteractiveTargetMs = 50;
    private const double HitTargetMs = 2;
    private const double RefinedTargetMs = 150;
    private const double SnapshotTargetMs = 150;
    private const int FrameWidth = 1600;
    private const int FrameHeight = 1000;
    private const int FrameSamples = 3;
    private const int HitIterations = 2000;

    private const string Usage =
        "usage: CadAgent.PerfBaseline [--out <file.md>] [--synth 20k|80k|200k] " +
        "[--drawing <path>]... (no mode = synth 20k/80k/200k)";

    private static readonly List<string> Preamble = new();
    private static readonly List<string> Rows = new();
    private static readonly List<string> Details = new();

    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;

            if (!TryParseArgs(args, out string? outPath, out IReadOnlyList<string> drawings,
                    out string? synth, out string? argError))
            {
                Console.Error.WriteLine("error: " + argError);
                Console.Error.WriteLine(Usage);
                return 2;
            }

            // --out 目标在校验阶段即确定（目录/非法路径提前失败，不白跑长时间基准）。
            string? outFull = null;
            if (outPath is not null)
            {
                outFull = Path.GetFullPath(outPath);
                if (Directory.Exists(outFull))
                    throw new IOException("--out points to a directory: " + outFull);
            }

            EnsureAvalonia();
            WritePreamble();

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

            string markdown = Compose();
            if (outFull is not null)
            {
                string? dir = Path.GetDirectoryName(outFull);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(outFull, markdown, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                Console.WriteLine("markdown: " + Ascii(outFull));
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAILED: " + Ascii(ex.GetType().Name + ": " + ex.Message));
            return 1;
        }
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
                    EndPoint = (XYZ)(new XY(x + rnd.NextDouble() * 40, y + rnd.NextDouble() * 40)),
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
        // 索引构建（CanvasViewModel ctor → RenderItemIndex）单列：与 RenderModelBuilder.Build
        // 分开计量（加载后首帧前的一次性成本；MainWindow 构造期在 UI 线程发生，列为信息项）。
        m.IndexBuildMs = Ms(() => _ = new CanvasViewModel(items));
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
        // 信息项：重复快照（字体/几何一次性初始化后）；快照耗时方差较大，取 2 次最小值。
        double warm1 = Ms(() => _ = provider.RenderPng(sheetId, bounds, FrameWidth, FrameHeight));
        double warm2 = Ms(() => _ = provider.RenderPng(sheetId, bounds, FrameWidth, FrameHeight));
        m.SnapshotWarmMs = Math.Min(warm1, warm2);

        BenchFrames(m, items, bounds);

        var vm = new CanvasViewModel(items);
        vm.Fit(bounds, FrameWidth, FrameHeight);
        var rnd = new Random(1);
        var hitTotals = new List<double>(FrameSamples);
        int hits = 0;
        for (int iter = 0; iter < FrameSamples; iter++)
        {
            int iterHits = 0;
            Stopwatch sw = Stopwatch.StartNew();
            for (int i = 0; i < HitIterations; i++)
            {
                double px = bounds.MinX + rnd.NextDouble() * (bounds.MaxX - bounds.MinX);
                double py = bounds.MinY + rnd.NextDouble() * (bounds.MaxY - bounds.MinY);
                if (vm.HitTest(px, py, 5 / vm.Scale) is not null) iterHits++;
            }
            sw.Stop();
            hitTotals.Add(sw.Elapsed.TotalMilliseconds);
            hits = iterHits; // 同一 rnd 流跨轮继续，命中数为参考（非断言）
        }
        m.HitSamples = hitTotals;
        m.HitMedianTotalMs = Median(hitTotals);
        m.HitMedianPerCallMs = m.HitMedianTotalMs / HitIterations;
        m.HitMinTotalMs = hitTotals.Min();
        m.HitMaxTotalMs = hitTotals.Max();
        m.HitCount = hits;

        m.ManagedMb = GC.GetTotalMemory(forceFullCollection: true) / (1024 * 1024);

        Emit(m, requireTargets);
    }

    /// <summary>
    /// 帧计时（同一 DrawingCanvas 置于 headless 窗口内，保证位图缓存路径生效）。
    /// headless 适配：Show/布局泵会先消耗一帧（缓存已烘焙），故画布在宿主窗口已显示后才挂入，
    /// 手动 Measure/Arrange 而不泵消息——计时首帧即「真冷 Refined 全量绘制 + 缓存位图重建」
    /// （冷文字缓存 + RTB 分配，与生产首帧同路径）；随后 PanBy(32,0) 三次再渲染 = 平移 blit
    /// （真实交互路径，128px 外扩内不重绘；3 次取中位，单次方差见明细）。
    /// 末段为「模拟重建」（spec §8 目标；同一 RebuildCache 例程的模拟触发）：暖缓存已存在时
    /// 强制 Interactive + 缩放令结构失效，下一次渲染走 RebuildCache（全量交互档栅格化 + blit）
    /// ——与上一行 Refined 暖重建同口径，仅质量档不同；生产触发点为交互期越界/编辑点击。
    /// BaseRenderCount 增量可证明确为重建而非 blit。
    /// 帧计时不含 PNG 编码；refined 另列编码耗时（旧基线 frame 含编码）。
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
            // 编码紧随其渲染的同一帧（与旧基线 frame 含编码同口径对比；不受后续平移影响）。
            m.RefinedEncodeMs = Ms(() =>
            {
                using var stream = new MemoryStream();
                rtb.Save(stream);
            });

            int baseBefore = canvas.BaseRenderCount;
            var panSamples = new List<double>(FrameSamples);
            for (int i = 0; i < FrameSamples; i++)
            {
                vm.PanBy(32, 0);
                Dispatcher.UIThread.RunJobs();
                panSamples.Add(Ms(() => rtb.Render(canvas)));
            }
            m.InteractiveFrameSamples = panSamples;
            m.InteractiveFrameMs = Median(panSamples);
            m.InteractiveFrameMinMs = panSamples.Min();
            m.InteractiveFrameMaxMs = panSamples.Max();
            // 平移未越出 128px 外扩 → 不应触发基元重建（样本有效性证据）。
            m.InteractiveBlitNoRebuild = canvas.BaseRenderCount == baseBefore;

            // 信息项：稳态精绘重建（JIT/Skia 已暖）——对照冷首帧，判断冷帧成本是否含一次性预热。
            vm.ZoomAt(1.0001, FrameWidth / 2.0, FrameHeight / 2.0);
            Dispatcher.UIThread.RunJobs();
            m.RefinedWarmMs = Ms(() => rtb.Render(canvas));

            // 模拟重建（非生产交互触发）：暖缓存已存在；强制 Interactive 并以同幅缩放令缓存结构失效，
            // 每次样本 = 一次完整渲染（RebuildCache[Interactive] + blit）。
            canvas.SetQualityForTests(RenderQuality.Interactive);
            var rebuildSamples = new List<double>(FrameSamples);
            int rebuilds = 0;
            int baseCountBefore = canvas.BaseRenderCount;
            for (int i = 0; i < FrameSamples; i++)
            {
                vm.ZoomAt(1.0001, FrameWidth / 2.0, FrameHeight / 2.0);
                int before = canvas.BaseRenderCount;
                rebuildSamples.Add(Ms(() => rtb.Render(canvas)));
                if (canvas.BaseRenderCount > before) rebuilds++;
            }
            m.InteractiveRebuildSamples = rebuildSamples;
            m.InteractiveRebuildMs = rebuildSamples[0];
            m.InteractiveRebuildMedianMs = Median(rebuildSamples);
            m.InteractiveRebuildMinMs = rebuildSamples.Min();
            m.InteractiveRebuildRebuilt = rebuilds == FrameSamples
                && canvas.BaseRenderCount - baseCountBefore >= FrameSamples;
            m.InteractiveRebuildQuality = canvas.Quality;
            canvas.SetQualityForTests(RenderQuality.Refined); // 复原（不影响后续用例）
        }
        finally
        {
            host.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    // ---- 输出 ----

    private static void WritePreamble()
    {
        Preamble.Add("# CadAgent 大图性能基线（项3 T7 工具）");
        Preamble.Add("");
        Preamble.Add($"> 运行：{DateTime.Now:yyyy-MM-dd HH:mm:ss} · {RuntimeInformation.FrameworkDescription}" +
                     $" · {RuntimeInformation.OSDescription} · CPU={Environment.ProcessorCount} · headless Skia（软件渲染）");
        Preamble.Add($"> 阈值（不 hard fail）：交互帧与模拟重建 <{InteractiveTargetMs:0}ms、命中 <{HitTargetMs:0}ms/次、" +
                     $"Refined 帧/快照 <{RefinedTargetMs:0}ms；合成仅 200k 计入交互/命中目标（spec §7）。");
        Preamble.Add($"> 帧：{FrameWidth}×{FrameHeight} headless 窗口内同一 DrawingCanvas；Refined=真冷首帧（Refined 全量+缓存位图重建，不含 PNG 编码），" +
                     $"平移交互=PanBy(32,0) 后 blit 复用缓存（{FrameSamples} 次取中位）。");
        Preamble.Add("> 模拟重建=暖缓存后切质量档 Interactive + 缩放失效 → 一次全量重建+blit（与生产同一 RebuildCache 例程，" +
                     $"非生产交互期真实触发；生产触发点为交互期越界/编辑点击。spec §8 目标 <50ms），{FrameSamples} 次取中位；" +
                     $"命中同为 {FrameSamples} 次×{HitIterations} 取中位。");
        Preamble.Add("> 快照=RenderPng（渲染 + PNG 编码；编码量级见同行 refined 的 +enc 列）；snapshot warm 为重复 2 次取最小值。" +
                     "load src=源 DWG 首载（%TEMP% 同字节副本，OS 文件缓存已暖，非磁盘冷载）；load working=二开（working+侧车）。");
        Preamble.Add("");
        Preamble.Add("| case | load src | load working | build | index build | hash cold | hash repeat | snapshot | refined frame (+enc) " +
                     "| pan frame med (min-max, n=" + FrameSamples + ") | simulated rebuild med (first/min) " +
                     "| hit x" + HitIterations + " med (count) | per hit | managed | status |");
        Preamble.Add("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
    }

    private static string Compose()
    {
        var sb = new StringBuilder();
        foreach (string line in Preamble) sb.AppendLine(line);
        // 指标表：全部行连续（明细在其后，避免表格被明细打断）。
        foreach (string row in Rows) sb.AppendLine(row);
        sb.AppendLine();
        foreach (string line in Details) sb.AppendLine(line);
        return sb.ToString();
    }

    private static void Emit(Metrics m, bool requireTargets)
    {
        m.Targets = requireTargets;
        string status = Status(m);
        Rows.Add($"| {m.Name} | {Num(m.LoadSourceMs)} | {Num(m.LoadWorkingMs)} | " +
                 $"{m.BuildMs:0}ms -> {m.ItemCount} items | {m.IndexBuildMs:0}ms | " +
                 $"{m.HashColdMs:0.#}ms | {m.HashRepeatMs:0.###}ms | " +
                 $"{m.SnapshotMs:0}ms ({m.SnapshotKb}KB, warm min2 {m.SnapshotWarmMs:0}ms) | " +
                 $"{m.RefinedFrameMs:0.#}ms (+{m.RefinedEncodeMs:0.#}ms enc, warm {m.RefinedWarmMs:0.#}ms) | " +
                 $"{m.InteractiveFrameMs:0.##}ms ({m.InteractiveFrameMinMs:0.##}-{m.InteractiveFrameMaxMs:0.##}) | " +
                 $"{m.InteractiveRebuildMedianMs:0.#}ms ({m.InteractiveRebuildMs:0.#}/{m.InteractiveRebuildMinMs:0.#}) | " +
                 $"{m.HitMedianTotalMs:0}ms ({m.HitCount}) | " +
                 $"{m.HitMedianPerCallMs:0.###}ms | {m.ManagedMb}MB | {status} |");

        // 明细：表后输出（每案一段，前置空行，列表不与表格相邻）。
        Details.Add("");
        Details.Add($"- {m.Name}: build={m.BuildMs:0}ms indexBuild={m.IndexBuildMs:0}ms " +
                    $"entities={m.TopEntities} items={m.ItemCount} text={m.TextItems} " +
                    $"hatchPts={m.HatchPoints} census={m.CensusState} unparsed={m.UnparsedCount} " +
                    $"hashEqual={m.HashEqual}");
        if (m.Kind == "drawing")
        {
            Details.Add($"- {m.Name}: sourceUntouched={m.SourceUntouched} noSourceReread={m.NoSourceReread} " +
                        $"loadNoSource={Num(m.LoadNoSourceMs)} size={m.SourceBytes / (1024 * 1024)}MB");
        }
        else
        {
            Details.Add($"- {m.Name}: dxfWrite={m.WriteMs:0}ms size={m.SourceBytes / 1024}KB");
        }
        Details.Add($"- {m.Name}: hashRevision={m.HashRevision} hashInvalidate={Num(m.HashInvalidateMs)} " +
                    $"refinedWarm={m.RefinedWarmMs:0.#}ms snapshotWarm(min2)={m.SnapshotWarmMs:0}ms");
        Details.Add($"- {m.Name}: panSamples=[{string.Join(", ", m.InteractiveFrameSamples.Select(v => v.ToString("0.##", CultureInfo.InvariantCulture)))}]ms " +
                    $"blitWithoutRebuild={m.InteractiveBlitNoRebuild}; " +
                    $"interactiveRebuildSamples=[{string.Join(", ", m.InteractiveRebuildSamples.Select(v => v.ToString("0.#", CultureInfo.InvariantCulture)))}]ms " +
                    $"rebuilt={m.InteractiveRebuildRebuilt} quality={m.InteractiveRebuildQuality}");
        Details.Add($"- {m.Name}: hitSamples=[{string.Join(", ", m.HitSamples.Select(v => v.ToString("0", CultureInfo.InvariantCulture)))}]ms " +
                    $"perHit median={m.HitMedianPerCallMs:0.###}ms");

        Console.WriteLine(Ascii($"[{m.Name}] loadSrc={Num(m.LoadSourceMs)} loadWorking={Num(m.LoadWorkingMs)} " +
                                $"build={m.BuildMs:0}ms index={m.IndexBuildMs:0}ms items={m.ItemCount} hashCold={m.HashColdMs:0.#}ms " +
                                $"hashRepeat={m.HashRepeatMs:0.###}ms snapshot={m.SnapshotMs:0}ms(warm {m.SnapshotWarmMs:0}) " +
                                $"refined={m.RefinedFrameMs:0.#}ms(+{m.RefinedEncodeMs:0.#}) " +
                                $"panMed={m.InteractiveFrameMs:0.##}ms rebuildMed={m.InteractiveRebuildMedianMs:0.#}ms " +
                                $"hitMed={m.HitMedianTotalMs:0}ms per={m.HitMedianPerCallMs:0.###}ms mem={m.ManagedMb}MB " +
                                $"census={m.CensusState} untouched={m.SourceUntouched} noReread={m.NoSourceReread} " +
                                $"status={status}"));
    }

    private static string Status(Metrics m)
    {
        var parts = new List<string>();
        bool frameOk = m.InteractiveFrameMs < InteractiveTargetMs;
        bool rebuildOk = m.InteractiveRebuildMedianMs < InteractiveTargetMs;
        bool hitOk = m.HitMedianPerCallMs < HitTargetMs;
        bool refinedOk = m.RefinedFrameMs < RefinedTargetMs;
        bool snapOk = m.SnapshotMs < SnapshotTargetMs;
        string info = $"INFO (frame {(frameOk ? "PASS" : "TARGET")}, rebuild {(rebuildOk ? "PASS" : "TARGET")}, " +
                      $"hit {(hitOk ? "PASS" : "TARGET")}, refined {(refinedOk ? "PASS" : "TARGET")}, " +
                      $"snapshot {(snapOk ? "PASS" : "TARGET")})";
        if (!m.Targets)
        {
            parts.Add(info);
            return string.Join("; ", parts);
        }
        // 真实图：全部目标（含二开语义/哈希）。合成：spec §7 只要求交互帧 + 命中（重建为 §8 交互语义，并列 INFO）。
        parts.Add(frameOk ? "frame PASS" : "frame TARGET");
        parts.Add(hitOk ? "hit PASS" : "hit TARGET");
        if (m.Kind == "drawing")
        {
            parts.Add(rebuildOk ? "rebuild PASS" : "rebuild TARGET");
            parts.Add(refinedOk ? "refined PASS" : "refined TARGET");
            parts.Add(snapOk ? "snapshot PASS" : "snapshot TARGET");
            parts.Add(m.HashRepeatMs < 1 ? "hash PASS" : "hash TARGET");
            parts.Add(m.SourceUntouched == true ? "srcUntouched PASS" : "srcUntouched TARGET");
            parts.Add(m.NoSourceReread == true ? "noReread PASS" : "noReread TARGET");
            parts.Add(m.InteractiveRebuildRebuilt ? "rebuildVerified PASS" : "rebuildVerified TARGET");
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

    private static double Median(IReadOnlyList<double> values)
    {
        double[] sorted = values.ToArray();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    /// <summary>
    /// 严格参数解析：未知参数、缺值（末位或后随另一 --flag）、重复 --out/--synth 均报错
    /// （ASCII 文案，调用方打印到 stderr 并以非 0 退出）。
    /// </summary>
    private static bool TryParseArgs(string[] args, out string? outPath, out IReadOnlyList<string> drawings,
        out string? synth, out string? error)
    {
        outPath = null;
        synth = null;
        error = null;
        var list = new List<string>();
        drawings = list;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is not ("--out" or "--synth" or "--drawing"))
            {
                error = Ascii($"unknown argument '{arg}'");
                return false;
            }
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"missing value for {arg}";
                return false;
            }
            string value = args[++i];
            switch (arg)
            {
                case "--out":
                    if (outPath is not null) { error = "duplicate --out"; return false; }
                    outPath = value;
                    break;
                case "--synth":
                    if (synth is not null) { error = "duplicate --synth"; return false; }
                    synth = value;
                    break;
                default:
                    list.Add(value);
                    break;
            }
        }
        return true;
    }

    private static int ParseSynth(string raw)
    {
        string v = raw.Trim().ToLowerInvariant();
        int factor = v.EndsWith('k') ? 1000 : 1;
        if (v.EndsWith('k')) v = v[..^1];
        if (!int.TryParse(v, out int n) || n <= 0) throw new ArgumentException($"invalid --synth: {raw} (e.g. 20k/80k/200k)");
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
        public double IndexBuildMs;
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
        public double InteractiveFrameMinMs;
        public double InteractiveFrameMaxMs;
        public bool InteractiveBlitNoRebuild;
        public IReadOnlyList<double> InteractiveFrameSamples = Array.Empty<double>();
        public double InteractiveRebuildMs;
        public double InteractiveRebuildMedianMs;
        public double InteractiveRebuildMinMs;
        public IReadOnlyList<double> InteractiveRebuildSamples = Array.Empty<double>();
        public bool InteractiveRebuildRebuilt;
        public RenderQuality InteractiveRebuildQuality;
        public double HitMedianTotalMs;
        public double HitMedianPerCallMs;
        public double HitMinTotalMs;
        public double HitMaxTotalMs;
        public IReadOnlyList<double> HitSamples = Array.Empty<double>();
        public int HitCount;
        public long ManagedMb;
    }
}
