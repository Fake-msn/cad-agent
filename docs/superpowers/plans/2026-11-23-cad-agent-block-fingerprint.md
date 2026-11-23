# 块级纠偏清单（块几何指纹匹配 skill 符号库）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为无属性 plain 块语料建立块级纠偏通道——块定义几何指纹匹配 skill 符号库，三级输出（精确自动 GeometryInference 0.9 / 近似建议待人工确认 / 无命中高频块歧义清单），人工确认走 map-block 落 HumanConfirmed 1.0。

**Architecture:** 纯 Data 层新增 BlockFingerprint（归一化矢量指纹+结构签名+贪心几何距离）与 BlockSymbolMatcher（符号索引+排除启发+三级输出+自动种子）；SemanticScanner.Scan 信号链在 BlockAttributeSeeder 后插入块指纹信号（加性参数，无包时行为逐字不变）；CLI semantic scan stdout 加性键 + 新 semantic map-block 确认命令（复核制，overlay-only，Yellow 豁免沿用 map 先例）。无渲染依赖、无云端调用、建议不持久化。

**Tech Stack:** .NET 8 / xUnit / ACadSharp 3.8.35（DxfReader/DxfWriter/BlockRecord/Insert/AttributeDefinition）/ 既有 SkillLoader/SemanticOverlay/SqliteAuditStore 链

**Spec:** `docs/superpowers/specs/2026-11-20-cad-agent-terminal-design.md` §13（块级纠偏清单，2026-11-23 批准）

## Global Constraints

- 分支 `feat/cad-agent-block-fingerprint` 从 main（282255b）新建；push/PR 需用户批准（finishing 菜单）。
- 落种子分级（spec §13.1）：精确→GeometryInference **0.9**；近似→只建议（confidence=**0.9−2d**，d≤**0.15**，数学保证<0.8 闸下）；人工确认→HumanConfirmed **1.0**。
- 扫描范围：全块定义含 `*U`；排除 `*Model`/`*Paper`（OrdinalIgnoreCase 前缀）、空定义、图框（**AttributeDefinition 存在且 bbox 最长边≥500**）、`$LinePat$`/`$TCHSYS$` 前缀（OrdinalIgnoreCase）。
- 指纹仅 LINE/CIRCLE/ARC（TEXT 不入哈希留辅助）；bbox 平移归零+**最长边归一**；量化 round(1e-3)；SHA256 大写 hex。
- 歧义阈值 instanceCount≥**3** 默认，`--min-block-instances <n>` 可配；代理无几何块 reason="代理图形不可读"+T3 指引。
- stdout 加性键（blockAutoSeeded/blockSuggestions/blockAmbiguous/symbolsSkipped 恒在）；旧字段逐字不变；审计 semantic-scan params 加性计数；新审计 kind semantic-map-block / semantic-map-block-rejected（Source="semantic"）。
- map-block 复核制（确认时重提取指纹，精确或 d≤0.15 否则拒绝留痕 exit 非零）；overlay-only before==after（drawing.ComputeHash()）；**不调 GateDetector**（Yellow 豁免沿用 SemanticMap 先例）。
- re-scan 清陈旧块级种子**仅 marker 范围**：Provenance==GeometryInference && Evidence.StartsWith("几何指纹精确匹配:")——不得误清墙/轴几何启发式种子；HumanConfirmed 跳过保留。
- TargetHandle=**w.HandlePath**（walked Insert，含嵌套后缀句柄）；SemanticAnnotation(TargetHandle, Class, Provenance, Confidence, Evidence, SheetId?)。
- 中文注释；EOF 换行；git 署名仅环境变量 Fake-msn（绝不 git config）。
- Windows GBK：dotnet 输出重定向 UTF-8 文件再读；dotnet test 单项目或全 sln（多项目参数→MSB1008）。
- TDD 每任务红→绿→提交；真实标本只读副本（源目录零触碰）；**本切片零云端调用**。
- 符号 DXF 读取镜像 ImportSymbolHandler：Path.Combine(pkg.SourceDir,"symbols",meta.File) + DxfReader；白名单 {Line,Circle,Arc,TextEntity} 外实体→该符号跳过+计数（不整体失败）。

## File Structure

| 文件 | 责任 | 任务 |
|---|---|---|
| Create `src/CadAgent.Data/Semantic/BlockFingerprint.cs` | 归一化提取/哈希/签名/距离 | 1 |
| Create `tests/CadAgent.Data.Tests/BlockFingerprintTests.cs` | 算法钉住 | 1 |
| Create `src/CadAgent.Data/Semantic/BlockSymbolMatcher.cs` | 符号索引+排除+三级输出+自动种子 | 2 |
| Create `tests/CadAgent.Data.Tests/BlockSymbolMatcherTests.cs` | 三级/排除/清理钉住 | 2 |
| Modify `src/CadAgent.Data/Semantic/SemanticScanner.cs` | Scan 加性参数+ScanResult.BlockMatch | 3 |
| Modify `src/CadAgent.Cli/CommandRouter.cs`（SemanticScan :609-671）+ `CommandRouter.Skills.cs`（LoadSkillPackages） | stdout 加性+审计加性+旗标 | 3 |
| Modify `tests/CadAgent.Data.Tests/SemanticScannerTests.cs` + `tests/CadAgent.Cli.Tests/SemanticE2ETests.cs` | 集成钉住 | 3 |
| Modify `src/CadAgent.Cli/CommandRouter.cs`（semantic 分发+SemanticMapBlock） | map-block 命令 | 4 |
| Create `tests/CadAgent.Cli.Tests/SemanticMapBlockTests.cs` | 确认/拒绝/豁免钉住 | 4 |
| Create `tests/CadAgent.Data.Tests/RealBlockFingerprintTests.cs` + 证据 + spec §12 注记 + 门禁 | 真实实证收尾 | 5 |

---

### Task 1: BlockFingerprint 算法

**Files:**
- Create: `src/CadAgent.Data/Semantic/BlockFingerprint.cs`
- Create: `tests/CadAgent.Data.Tests/BlockFingerprintTests.cs`

**Interfaces:**
- Consumes: ACadSharp.Entities（Line/Circle/Arc/TextEntity/Entity）
- Produces:
  - `public sealed record NormGeom(IReadOnlyList<(double X1,double Y1,double X2,double Y2)> Lines, IReadOnlyList<(double CX,double CY,double R)> Circles, IReadOnlyList<(double CX,double CY,double R,double A0,double A1)> Arcs, double Aspect)`（Aspect=min(w,h)/max(w,h)∈[0,1]，归一化前原始 bbox）
  - `public sealed record Fingerprint(string Hash, NormGeom Geom)`
  - `public static Fingerprint? Extract(IEnumerable<Entity> entities)`（无 LINE/CIRCLE/ARC→null；NaN/∞ 坐标实体剔除；最长边<1e-9→null）
  - `public static bool Compatible(Fingerprint a, Fingerprint b)`（三类计数逐等 && |Aspect差|≤0.2）
  - `public static double Distance(Fingerprint a, Fingerprint b)`（不 Compatible→1.0；否则贪心逐类匹配平均距离∈[0,1]）
  - `public const double NearThreshold = 0.15;` `public static double Confidence(double d) => 0.9 - 2 * d;`

- [ ] **Step 1: 写失败测试**

```csharp
using ACadSharp.Entities;
using CadAgent.Data.Semantic;

namespace CadAgent.Data.Tests;

public class BlockFingerprintTests
{
    private static List<Entity> Symbol() => new()
    {
        new Line { StartPoint = new ACadSharp.XY(0, 0), EndPoint = new ACadSharp.XY(10, 0) },
        new Circle { Center = new ACadSharp.XY(5, 3), Radius = 2 },
    };

    [Fact]
    public void Extract_CloneAndScaledAndTranslated_SameHash()
    {
        var a = BlockFingerprint.Extract(Symbol())!;
        var scaled = Symbol();
        ((Line)scaled[0]).StartPoint = new ACadSharp.XY(25, 25);
        ((Line)scaled[0]).EndPoint = new ACadSharp.XY(50, 25);
        ((Circle)scaled[1]).Center = new ACadSharp.XY(37.5, 32.5);
        ((Circle)scaled[1]).Radius = 5;
        var b = BlockFingerprint.Extract(scaled)!;
        Assert.Equal(a.Hash, b.Hash); // 平移+均匀缩放不变
    }

    [Fact]
    public void Extract_TextDifference_SameHash()
    {
        var withText = Symbol();
        withText.Add(new TextEntity { InsertPoint = new ACadSharp.XY(1, 1), Height = 2, Value = "注记" });
        Assert.Equal(BlockFingerprint.Extract(Symbol())!.Hash, BlockFingerprint.Extract(withText)!.Hash);
    }

    [Fact]
    public void Extract_NoWhitelistGeometry_Null()
    {
        Assert.Null(BlockFingerprint.Extract(new List<Entity> { new TextEntity { Height = 1 } }));
        Assert.Null(BlockFingerprint.Extract(new List<Entity>()));
    }

    [Fact]
    public void Extract_DegenerateSingleAxis_Normalizes()
    {
        var fp = BlockFingerprint.Extract(new List<Entity>
        {
            new Line { StartPoint = new ACadSharp.XY(3, 7), EndPoint = new ACadSharp.XY(3, 17) },
        })!;
        Assert.NotNull(fp); // 最长边归一，退化 bbox 安全
        Assert.Equal(0, fp.Geom.Aspect, 3);
    }

    [Fact]
    public void Extract_NonFiniteCoordinate_EntityDropped()
    {
        var ents = Symbol();
        ents.Add(new Line { StartPoint = new ACadSharp.XY(double.NaN, 0), EndPoint = new ACadSharp.XY(1, 1) });
        var fp = BlockFingerprint.Extract(ents)!;
        Assert.Equal(BlockFingerprint.Extract(Symbol())!.Hash, fp.Hash);
    }

    [Fact]
    public void Distance_PerturbedEndpoint_SmallPositive()
    {
        var a = BlockFingerprint.Extract(Symbol())!;
        var perturbed = Symbol();
        ((Line)perturbed[0]).EndPoint = new ACadSharp.XY(10.5, 0); // 归一化后偏移 0.05
        var b = BlockFingerprint.Extract(perturbed)!;
        Assert.NotEqual(a.Hash, b.Hash);
        double d = BlockFingerprint.Distance(a, b);
        Assert.InRange(d, 0.001, BlockFingerprint.NearThreshold);
    }

    [Fact]
    public void Distance_HistogramMismatch_One()
    {
        var a = BlockFingerprint.Extract(Symbol())!;
        var extra = Symbol();
        extra.Add(new Circle { Center = new ACadSharp.XY(1, 1), Radius = 1 });
        var b = BlockFingerprint.Extract(extra)!;
        Assert.False(BlockFingerprint.Compatible(a, b));
        Assert.Equal(1.0, BlockFingerprint.Distance(a, b));
    }

    [Fact]
    public void Distance_Identical_Zero_AndConfidenceFormula()
    {
        var a = BlockFingerprint.Extract(Symbol())!;
        Assert.Equal(0, BlockFingerprint.Distance(a, a), 9);
        Assert.Equal(0.9, BlockFingerprint.Confidence(0), 9);
        Assert.Equal(0.6, BlockFingerprint.Confidence(BlockFingerprint.NearThreshold), 9);
        Assert.True(BlockFingerprint.Confidence(BlockFingerprint.NearThreshold) < 0.8); // 闸下保证
    }

    [Fact]
    public void Distance_LineDirectionReversal_Invariant()
    {
        var a = BlockFingerprint.Extract(Symbol())!;
        var rev = Symbol();
        var l = (Line)rev[0];
        (l.StartPoint, l.EndPoint) = (l.EndPoint, l.StartPoint);
        Assert.Equal(0, BlockFingerprint.Distance(a, BlockFingerprint.Extract(rev)!), 9);
    }
}
```

（ACadSharp 点类型若为 `CSMath.XYZ`/`XY` 别名差异：读现状 ImportSymbolHandler/BlockWalker 用法定型，夹具与实现同口径。）

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/CadAgent.Data.Tests --filter BlockFingerprint > outputs/block-fingerprint/t1-red.log 2>&1`
Expected: 编译失败（BlockFingerprint 不存在）

- [ ] **Step 3: 实现 BlockFingerprint**

```csharp
using System.Security.Cryptography;
using System.Text;
using ACadSharp.Entities;

namespace CadAgent.Data.Semantic;

/// <summary>
/// 块几何指纹（spec §13.2）：块定义直接几何 LINE/CIRCLE/ARC → bbox 平移归零+最长边归一
/// → 量化 1e-3 → 规范排序 SHA256。TEXT 不入哈希（注记随实例变化）。
/// 插入 Scale/Rotation 作用于实例不进块定义——指纹天然插入变换不变。
/// </summary>
public static class BlockFingerprint
{
    public const double NearThreshold = 0.15;
    public static double Confidence(double d) => 0.9 - 2 * d;

    public sealed record NormGeom(
        IReadOnlyList<(double X1, double Y1, double X2, double Y2)> Lines,
        IReadOnlyList<(double CX, double CY, double R)> Circles,
        IReadOnlyList<(double CX, double CY, double R, double A0, double A1)> Arcs,
        double Aspect);
    public sealed record Fingerprint(string Hash, NormGeom Geom);

    public static Fingerprint? Extract(IEnumerable<Entity> entities)
    {
        var lines = new List<(double, double, double, double)>();
        var circles = new List<(double, double, double)>();
        var arcs = new List<(double, double, double, double, double)>();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Pt(double x, double y)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y)) return;
            if (x < minX) minX = x; if (y < minY) minY = y;
            if (x > maxX) maxX = x; if (y > maxY) maxY = y;
        }
        foreach (var e in entities)
        {
            switch (e)
            {
                case Line l:
                    if (!Finite(l.StartPoint.X, l.StartPoint.Y, l.EndPoint.X, l.EndPoint.Y)) continue;
                    lines.Add((l.StartPoint.X, l.StartPoint.Y, l.EndPoint.X, l.EndPoint.Y));
                    Pt(l.StartPoint.X, l.StartPoint.Y); Pt(l.EndPoint.X, l.EndPoint.Y); break;
                case Circle c when e is not Arc:
                    if (!Finite(c.Center.X, c.Center.Y, c.Radius)) continue;
                    circles.Add((c.Center.X, c.Center.Y, c.Radius));
                    Pt(c.Center.X - c.Radius, c.Center.Y - c.Radius); Pt(c.Center.X + c.Radius, c.Center.Y + c.Radius); break;
                case Arc a:
                    if (!Finite(a.Center.X, a.Center.Y, a.Radius, a.StartAngle, a.EndAngle)) continue;
                    arcs.Add((a.Center.X, a.Center.Y, a.Radius, a.StartAngle, a.EndAngle));
                    Pt(a.Center.X - a.Radius, a.Center.Y - a.Radius); Pt(a.Center.X + a.Radius, a.Center.Y + a.Radius); break;
            }
        }
        if (lines.Count + circles.Count + arcs.Count == 0) return null;
        double w = maxX - minX, h = maxY - minY, scale = Math.Max(w, h);
        if (scale < 1e-9) return null;
        double aspect = Math.Min(w, h) / scale;
        double Q(double v) => Math.Round(v, 3);
        var nl = lines.Select(t => (Q((t.Item1 - minX) / scale), Q((t.Item2 - minY) / scale), Q((t.Item3 - minX) / scale), Q((t.Item4 - minY) / scale)))
            .OrderBy(t => t.Item1).ThenBy(t => t.Item2).ThenBy(t => t.Item3).ThenBy(t => t.Item4).ToList();
        var nc = circles.Select(t => (Q((t.Item1 - minX) / scale), Q((t.Item2 - minY) / scale), Q(t.Item3 / scale)))
            .OrderBy(t => t.Item1).ThenBy(t => t.Item2).ThenBy(t => t.Item3).ToList();
        var na = arcs.Select(t => (Q((t.Item1 - minX) / scale), Q((t.Item2 - minY) / scale), Q(t.Item3 / scale), Q(t.Item4), Q(t.Item5)))
            .OrderBy(t => t.Item1).ThenBy(t => t.Item2).ThenBy(t => t.Item3).ThenBy(t => t.Item4).ThenBy(t => t.Item5).ToList();
        var sb = new StringBuilder();
        foreach (var t in nl) sb.Append(FormattableString.Invariant($"L{t.Item1},{t.Item2},{t.Item3},{t.Item4};"));
        foreach (var t in nc) sb.Append(FormattableString.Invariant($"C{t.Item1},{t.Item2},{t.Item3};"));
        foreach (var t in na) sb.Append(FormattableString.Invariant($"A{t.Item1},{t.Item2},{t.Item3},{t.Item4},{t.Item5};"));
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        return new Fingerprint(hash, new NormGeom(nl, nc, na, aspect));
    }

    private static bool Finite(params double[] vs) => vs.All(double.IsFinite);

    public static bool Compatible(Fingerprint a, Fingerprint b) =>
        a.Geom.Lines.Count == b.Geom.Lines.Count
        && a.Geom.Circles.Count == b.Geom.Circles.Count
        && a.Geom.Arcs.Count == b.Geom.Arcs.Count
        && Math.Abs(a.Geom.Aspect - b.Geom.Aspect) <= 0.2;

    /// <summary>贪心逐类最近邻匹配平均距离（不 Compatible→1.0）。线允许方向反转；弧角差权重 0.5。</summary>
    public static double Distance(Fingerprint a, Fingerprint b)
    {
        if (!Compatible(a, b)) return 1.0;
        double sum = 0; int n = 0;
        sum += MatchLines(a.Geom.Lines, b.Geom.Lines); n += a.Geom.Lines.Count;
        sum += MatchCircles(a.Geom.Circles, b.Geom.Circles); n += a.Geom.Circles.Count;
        sum += MatchArcs(a.Geom.Arcs, b.Geom.Arcs); n += a.Geom.Arcs.Count;
        return n == 0 ? 1.0 : Math.Min(1.0, sum / n);
    }

    private static double PtDist(double x1, double y1, double x2, double y2)
        => Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));

    private static double MatchLines(IReadOnlyList<(double X1, double Y1, double X2, double Y2)> a, IReadOnlyList<(double X1, double Y1, double X2, double Y2)> b)
    {
        var used = new bool[b.Count]; double sum = 0;
        foreach (var x in a)
        {
            double best = double.MaxValue; int bi = -1;
            for (int j = 0; j < b.Count; j++)
            {
                if (used[j]) continue;
                var y = b[j];
                double d = Math.Min(
                    (PtDist(x.X1, x.Y1, y.X1, y.Y1) + PtDist(x.X2, x.Y2, y.X2, y.Y2)) / 2,
                    (PtDist(x.X1, x.Y1, y.X2, y.Y2) + PtDist(x.X2, x.Y2, y.X1, y.Y1)) / 2);
                if (d < best) { best = d; bi = j; }
            }
            if (bi >= 0) { used[bi] = true; sum += best; }
        }
        return sum;
    }

    private static double MatchCircles(IReadOnlyList<(double CX, double CY, double R)> a, IReadOnlyList<(double CX, double CY, double R)> b)
    {
        var used = new bool[b.Count]; double sum = 0;
        foreach (var x in a)
        {
            double best = double.MaxValue; int bi = -1;
            for (int j = 0; j < b.Count; j++)
            {
                if (used[j]) continue;
                var y = b[j];
                double d = PtDist(x.CX, x.CY, y.CX, y.CY) + Math.Abs(x.R - y.R);
                if (d < best) { best = d; bi = j; }
            }
            if (bi >= 0) { used[bi] = true; sum += best; }
        }
        return sum;
    }

    private static double MatchArcs(IReadOnlyList<(double CX, double CY, double R, double A0, double A1)> a, IReadOnlyList<(double CX, double CY, double R, double A0, double A1)> b)
    {
        var used = new bool[b.Count]; double sum = 0;
        foreach (var x in a)
        {
            double best = double.MaxValue; int bi = -1;
            for (int j = 0; j < b.Count; j++)
            {
                if (used[j]) continue;
                var y = b[j];
                double d = PtDist(x.CX, x.CY, y.CX, y.CY) + Math.Abs(x.R - y.R)
                    + 0.5 * (AngDiff(x.A0, y.A0) + AngDiff(x.A1, y.A1)) / 2;
                if (d < best) { best = d; bi = j; }
            }
            if (bi >= 0) { used[bi] = true; sum += best; }
        }
        return sum;
    }

    private static double AngDiff(double a, double b)
    {
        double d = Math.Abs(a - b) % (2 * Math.PI);
        return Math.Min(d, 2 * Math.PI - d);
    }
}
```

（Arc 角度单位读 ACadSharp 现状：弧度/度按 API 事实——BlockWalker/RenderModel 用法定型；若度则 AngDiff 模 360 同构。Circle/Arc 继承关系：ACadSharp `Arc : Circle`，switch 顺序 Arc 先于 Circle 或 `when e is not Arc` 守卫——上面用守卫，实现时按编译事实调整。）

- [ ] **Step 4: 运行确认绿 + Data 全量**

Run: `dotnet test tests/CadAgent.Data.Tests > outputs/block-fingerprint/t1-green.log 2>&1`
Expected: PASS（既有测试不受影响——纯新增文件）

- [ ] **Step 5: 提交**

```bash
git add src/CadAgent.Data/Semantic/BlockFingerprint.cs tests/CadAgent.Data.Tests/BlockFingerprintTests.cs
GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com git commit -m "feat: block geometry fingerprint (normalize + hash + signature + greedy distance, spec §13.2)"
```

---

### Task 2: BlockSymbolMatcher 三级匹配

**Files:**
- Create: `src/CadAgent.Data/Semantic/BlockSymbolMatcher.cs`
- Create: `tests/CadAgent.Data.Tests/BlockSymbolMatcherTests.cs`

**Interfaces:**
- Consumes: T1 `BlockFingerprint.Extract/Compatible/Distance/NearThreshold/Confidence`；`SkillPackage`(Symbols/SourceDir)；`SymbolMeta(Name,File,Class,Description)`；`WalkedEntity`(Entity/HandlePath/SheetId)；`SemanticOverlay`(Annotations/UpsertAnnotation)；`SemanticAnnotation(TargetHandle,Class,Provenance,Confidence,Evidence,SheetId?)`
- Produces:
  - `public sealed record BlockCandidate(string SymbolName, double Distance, double Confidence);`
  - `public sealed record BlockSuggestion(string BlockName, int Instances, IReadOnlyList<BlockCandidate> Candidates);`
  - `public sealed record BlockAmbiguous(string BlockName, int Instances, string Reason, IReadOnlyList<string> SampleHandles);`
  - `public sealed record BlockAutoSeed(string BlockName, string SymbolName, IReadOnlyList<string> Handles);`
  - `public sealed record BlockMatchResult(IReadOnlyList<BlockAutoSeed> AutoSeeds, IReadOnlyList<BlockSuggestion> Suggestions, IReadOnlyList<BlockAmbiguous> Ambiguous, int SymbolsLoaded, int SymbolsSkipped, int BlocksScanned, int BlocksExcluded);`
  - `internal static BlockMatchResult MatchAndSeed(CadDocument doc, IReadOnlyList<WalkedEntity> walked, SemanticOverlay overlay, IReadOnlyList<SkillPackage> packages, int minInstances = 3)`（自动种子直接落 overlay；建议/歧义只披露不落）
  - `internal static bool IsExcluded(BlockRecord block)`（排除启发，供测试直调）
  - 证据字面值：自动种子 evidence=`$"几何指纹精确匹配: {symbolName}"`（**re-scan 清理 marker 前缀="几何指纹精确匹配:"**）；歧义 reason∈{"代理图形不可读","无匹配"}（前者附 T3 指引后缀="（代理图形不可读，建议天正 T3 转换后重扫）"——reason 字段存短值，指引另加 Guidance 字段？否：reason 存短值，stdout 披露侧拼指引——保持 record 四字段，Guidance 不入 record，CLI 侧对 reason=="代理图形不可读" 追加 guidance 键）

- [ ] **Step 1: 写失败测试**

```csharp
using ACadSharp;
using ACadSharp.Entities;
using CadAgent.Core;
using CadAgent.Core.Skills;
using CadAgent.Data.Semantic;

namespace CadAgent.Data.Tests;

public class BlockSymbolMatcherTests : IDisposable
{
    private readonly string _pkg = Path.Combine(Path.GetTempPath(), $"bfpg-{Guid.NewGuid():N}");

    public BlockSymbolMatcherTests()
    {
        Directory.CreateDirectory(Path.Combine(_pkg, "symbols"));
        File.WriteAllText(Path.Combine(_pkg, "skill.yaml"), "name: test-pkg\nversion: 1.0.0\n");
        File.WriteAllText(Path.Combine(_pkg, "symbols", "symbols.yaml"),
            "- { name: 测试符号, file: sym.dxf, class: Device, description: 夹具 }\n");
        var sym = new CadDocument();
        sym.Entities.Add(new Line { StartPoint = new ACadSharp.XY(0, 0), EndPoint = new ACadSharp.XY(10, 0) });
        sym.Entities.Add(new Circle { Center = new ACadSharp.XY(5, 3), Radius = 2 });
        using (var fs = File.Create(Path.Combine(_pkg, "symbols", "sym.dxf")))
            new ACadSharp.IO.DxfWriter(fs).Write(sym);
    }

    public void Dispose() { try { Directory.Delete(_pkg, true); } catch (IOException) { } }

    private static CadDocument DocWithBlock(string name, Action<BlockRecord> populate, int inserts = 4)
    {
        var doc = new CadDocument();
        var br = new BlockRecord(name);
        populate(br);
        doc.BlockRecords.Add(br);
        for (int i = 0; i < inserts; i++)
            doc.Entities.Add(new Insert(br) { InsertPoint = new ACadSharp.XY(i * 100, 0) });
        return doc;
    }

    private static void SymbolGeometry(BlockRecord br)
    {
        br.Entities.Add(new Line { StartPoint = new ACadSharp.XY(0, 0), EndPoint = new ACadSharp.XY(10, 0) });
        br.Entities.Add(new Circle { Center = new ACadSharp.XY(5, 3), Radius = 2 });
    }

    private BlockMatchResult Run(CadDocument doc, SemanticOverlay? overlay = null, int min = 3)
    {
        var packages = new[] { SkillLoader.LoadDirectory(_pkg) };
        var walked = BlockWalker.Walk(doc, Array.Empty<SheetRecord>()).ToList();
        return BlockSymbolMatcher.MatchAndSeed(doc, walked, overlay ?? new SemanticOverlay(), packages, min);
    }

    [Fact]
    public void ExactClone_AutoSeedsAllInstances_GeometryInference09()
    {
        var doc = DocWithBlock("符号块", SymbolGeometry);
        var overlay = new SemanticOverlay();
        var r = Run(doc, overlay);
        Assert.Single(r.AutoSeeds);
        Assert.Equal("测试符号", r.AutoSeeds[0].SymbolName);
        Assert.Equal(4, r.AutoSeeds[0].Handles.Count);
        Assert.Equal(4, overlay.Annotations.Count(a => a.Provenance == Provenance.GeometryInference));
        Assert.All(overlay.Annotations.Where(a => a.Provenance == Provenance.GeometryInference),
            a => { Assert.Equal(0.9, a.Confidence); Assert.StartsWith("几何指纹精确匹配:", a.Evidence); Assert.Equal(SemanticClass.Device, a.Class); });
        Assert.Empty(r.Suggestions);
    }

    [Fact]
    public void PerturbedBlock_SuggestionOnly_NoAutoSeed_ConfidenceFormula()
    {
        var doc = DocWithBlock("近似块", br =>
        {
            br.Entities.Add(new Line { StartPoint = new ACadSharp.XY(0, 0), EndPoint = new ACadSharp.XY(10.5, 0) });
            br.Entities.Add(new Circle { Center = new ACadSharp.XY(5, 3), Radius = 2 });
        });
        var overlay = new SemanticOverlay();
        var r = Run(doc, overlay);
        Assert.Empty(r.AutoSeeds);
        Assert.Empty(overlay.Annotations);
        var s = Assert.Single(r.Suggestions);
        var c = Assert.Single(s.Candidates);
        Assert.Equal("测试符号", c.SymbolName);
        Assert.InRange(c.Distance, 0.001, BlockFingerprint.NearThreshold);
        Assert.Equal(BlockFingerprint.Confidence(c.Distance), c.Confidence, 9);
        Assert.True(c.Confidence < 0.8);
    }

    [Fact]
    public void UnmatchedHighFreq_Ambiguous_NoMatch()
    {
        var doc = DocWithBlock("自定义块", br =>
        {
            br.Entities.Add(new Line { StartPoint = new ACadSharp.XY(0, 0), EndPoint = new ACadSharp.XY(1, 5) });
            br.Entities.Add(new Line { StartPoint = new ACadSharp.XY(1, 5), EndPoint = new ACadSharp.XY(3, 1) });
            br.Entities.Add(new Line { StartPoint = new ACadSharp.XY(3, 1), EndPoint = new ACadSharp.XY(0, 0) });
        });
        var r = Run(doc);
        var a = Assert.Single(r.Ambiguous);
        Assert.Equal("自定义块", a.BlockName);
        Assert.Equal("无匹配", a.Reason);
        Assert.Equal(4, a.Instances);
        Assert.NotEmpty(a.SampleHandles);
    }

    [Fact]
    public void ProxyLikeBlock_NoWhitelistGeometry_AmbiguousProxyReason()
    {
        var doc = DocWithBlock("$equip$0001", br => br.Entities.Add(new Hatch()));
        var r = Run(doc);
        var a = Assert.Single(r.Ambiguous);
        Assert.Equal("代理图形不可读", a.Reason);
    }

    [Fact]
    public void LowInstances_Unmatched_NotAmbiguous()
    {
        var doc = DocWithBlock("偶发块", br => br.Entities.Add(new Line { StartPoint = new ACadSharp.XY(0, 0), EndPoint = new ACadSharp.XY(9, 9) }), inserts: 2);
        var r = Run(doc);
        Assert.Empty(r.Ambiguous);
    }

    [Fact]
    public void Exclusions_Frame_SystemPrefix_ModelSpace()
    {
        var frame = new BlockRecord("院标准图框_A0");
        frame.Entities.Add(new AttributeDefinition { Tag = "图名" });
        frame.Entities.Add(new Line { StartPoint = new ACadSharp.XY(0, 0), EndPoint = new ACadSharp.XY(1189, 841) });
        Assert.True(BlockSymbolMatcher.IsExcluded(frame));
        var smallAttdef = new BlockRecord("小属性块");
        smallAttdef.Entities.Add(new AttributeDefinition { Tag = "A" });
        smallAttdef.Entities.Add(new Line { StartPoint = new ACadSharp.XY(0, 0), EndPoint = new ACadSharp.XY(10, 10) });
        Assert.False(BlockSymbolMatcher.IsExcluded(smallAttdef)); // 有 ATTDEF 但尺寸<500 不排除
        Assert.True(BlockSymbolMatcher.IsExcluded(new BlockRecord("$LinePat$00000004")));
        Assert.True(BlockSymbolMatcher.IsExcluded(new BlockRecord("$TCHSYS$DOOR3D")));
        Assert.True(BlockSymbolMatcher.IsExcluded(new BlockRecord("*Model_Space")));
        Assert.True(BlockSymbolMatcher.IsExcluded(new BlockRecord("空块"))); // 空定义排除
        var anon = new BlockRecord("*U160");
        SymbolGeometry(anon);
        Assert.False(BlockSymbolMatcher.IsExcluded(anon)); // 匿名块不排除
    }

    [Fact]
    public void Rescan_ClearsStaleBlockSeeds_KeepsHumanConfirmedAndWallHeuristics()
    {
        var doc = DocWithBlock("符号块", SymbolGeometry);
        var overlay = new SemanticOverlay();
        Run(doc, overlay);
        overlay.UpsertAnnotation(new SemanticAnnotation("999", SemanticClass.Wall, Provenance.GeometryInference, 0.7, "平行线对"));
        overlay.UpsertAnnotation(new SemanticAnnotation("888", SemanticClass.Device, Provenance.HumanConfirmed, 1.0, "人工确认"));
        // 块定义被改走样→re-scan 应清陈旧块种子
        var br = doc.BlockRecords.First(b => b.Name == "符号块");
        br.Entities.Add(new Line { StartPoint = new ACadSharp.XY(0, 0), EndPoint = new ACadSharp.XY(50, 50) });
        var r = Run(doc, overlay);
        Assert.Empty(r.AutoSeeds);
        Assert.DoesNotContain(overlay.Annotations, a => a.Evidence.StartsWith("几何指纹精确匹配:"));
        Assert.Contains(overlay.Annotations, a => a.TargetHandle == "999" && a.Provenance == Provenance.GeometryInference); // 墙启发式保留
        Assert.Contains(overlay.Annotations, a => a.TargetHandle == "888" && a.Provenance == Provenance.HumanConfirmed);
    }

    [Fact]
    public void HumanConfirmedInstance_SkippedByAutoSeed()
    {
        var doc = DocWithBlock("符号块", SymbolGeometry);
        var overlay = new SemanticOverlay();
        var walked = BlockWalker.Walk(doc, Array.Empty<SheetRecord>()).ToList();
        var first = walked.First(w => w.Entity is Insert);
        overlay.UpsertAnnotation(new SemanticAnnotation(first.HandlePath, SemanticClass.Lighting, Provenance.HumanConfirmed, 1.0, "人工"));
        var packages = new[] { SkillLoader.LoadDirectory(_pkg) };
        BlockSymbolMatcher.MatchAndSeed(doc, walked, overlay, packages);
        Assert.DoesNotContain(overlay.Annotations, a => a.TargetHandle == first.HandlePath && a.Provenance == Provenance.GeometryInference);
        Assert.Contains(overlay.Annotations, a => a.TargetHandle == first.HandlePath && a.Provenance == Provenance.HumanConfirmed && a.Class == SemanticClass.Lighting);
    }

    [Fact]
    public void BrokenSymbolDxf_SkippedWithCount_OthersStillMatch()
    {
        File.WriteAllText(Path.Combine(_pkg, "symbols", "bad.dxf"), "不是 DXF");
        File.WriteAllText(Path.Combine(_pkg, "symbols", "symbols.yaml"),
            "- { name: 测试符号, file: sym.dxf, class: Device, description: 夹具 }\n- { name: 坏符号, file: bad.dxf, class: Device, description: 坏 }\n");
        var doc = DocWithBlock("符号块", SymbolGeometry);
        var r = Run(doc);
        Assert.Equal(1, r.SymbolsLoaded);
        Assert.Equal(1, r.SymbolsSkipped);
        Assert.Single(r.AutoSeeds);
    }

    [Fact]
    public void EmptyPackages_EmptyResult_NoThrow()
    {
        var doc = DocWithBlock("符号块", SymbolGeometry);
        var walked = BlockWalker.Walk(doc, Array.Empty<SheetRecord>()).ToList();
        var r = BlockSymbolMatcher.MatchAndSeed(doc, walked, new SemanticOverlay(), Array.Empty<SkillPackage>());
        Assert.Empty(r.AutoSeeds); Assert.Empty(r.Suggestions); Assert.Empty(r.Ambiguous);
        Assert.Equal(0, r.SymbolsLoaded);
    }
}
```

（`BlockRecord(name)` 构造/`Insert(br)` 构造/`AttributeDefinition` 类型名/`DxfWriter(fs).Write(doc)` 实例 vs 静态——读现状 ImportSymbolHandler/ImportSymbolTests/DxfFixture 用法定型；`BlockWalker.Walk(doc, Array.Empty<SheetRecord>())` 空幅面口径读现状测试。）

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/CadAgent.Data.Tests --filter BlockSymbolMatcher > outputs/block-fingerprint/t2-red.log 2>&1`
Expected: 编译失败（BlockSymbolMatcher/records 不存在）

- [ ] **Step 3: 实现 BlockSymbolMatcher**

```csharp
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CadAgent.Core;
using CadAgent.Core.Skills;

namespace CadAgent.Data.Semantic;

// records namespace 级 public（ScanResult public 引用要求；类保持 internal）
public sealed record BlockCandidate(string SymbolName, double Distance, double Confidence);
public sealed record BlockSuggestion(string BlockName, int Instances, IReadOnlyList<BlockCandidate> Candidates);
public sealed record BlockAmbiguous(string BlockName, int Instances, string Reason, IReadOnlyList<string> SampleHandles);
public sealed record BlockAutoSeed(string BlockName, string SymbolName, IReadOnlyList<string> Handles);
public sealed record BlockMatchResult(
    IReadOnlyList<BlockAutoSeed> AutoSeeds, IReadOnlyList<BlockSuggestion> Suggestions,
    IReadOnlyList<BlockAmbiguous> Ambiguous, int SymbolsLoaded, int SymbolsSkipped,
    int BlocksScanned, int BlocksExcluded);

/// <summary>
/// 块级纠偏三级匹配（spec §13.3）：符号索引（skill 包 DXF）→ 排除启发 →
/// 精确自动种子 GeometryInference 0.9 / 近似建议（不自动）/ 无命中高频歧义。
/// 自动种子 marker evidence 前缀="几何指纹精确匹配:"——re-scan 仅清此 marker（保墙/轴启发式）。
/// </summary>
internal static class BlockSymbolMatcher
{
    internal const string EvidencePrefix = "几何指纹精确匹配:";

    internal static bool IsExcluded(BlockRecord block)
    {
        string n = block.Name;
        if (n.StartsWith("*Model", StringComparison.OrdinalIgnoreCase) || n.StartsWith("*Paper", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.StartsWith("$LinePat$", StringComparison.OrdinalIgnoreCase) || n.StartsWith("$TCHSYS$", StringComparison.OrdinalIgnoreCase)) return true;
        var ents = block.Entities.ToList();
        if (ents.Count == 0) return true;
        if (ents.Any(e => e is AttributeDefinition))
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var e in ents)
                foreach (var (x, y) in PointsOf(e))
                { if (x < minX) minX = x; if (y < minY) minY = y; if (x > maxX) maxX = x; if (y > maxY) maxY = y; }
            if (maxX > minX && Math.Max(maxX - minX, maxY - minY) >= 500) return true;
        }
        return false;
    }

    private static IEnumerable<(double X, double Y)> PointsOf(Entity e) => e switch
    {
        Line l => new[] { (l.StartPoint.X, l.StartPoint.Y), (l.EndPoint.X, l.EndPoint.Y) },
        Circle c => new[] { (c.Center.X - c.Radius, c.Center.Y - c.Radius), (c.Center.X + c.Radius, c.Center.Y + c.Radius) },
        LwPolyline lw => lw.Vertices.Select(v => (v.Location.X, v.Location.Y)),
        TextEntity t => new[] { (t.InsertPoint.X, t.InsertPoint.Y) },
        _ => Array.Empty<(double, double)>(),
    };

    internal static BlockMatchResult MatchAndSeed(
        CadDocument doc, IReadOnlyList<WalkedEntity> walked, SemanticOverlay overlay,
        IReadOnlyList<SkillPackage> packages, int minInstances = 3)
    {
        // 符号索引（读失败/超白名单→跳过计数，不整体失败）
        var symbols = new List<(string Name, SemanticClass Class, BlockFingerprint.Fingerprint Fp)>();
        int skipped = 0;
        foreach (var pkg in packages)
            foreach (var meta in pkg.Symbols)
            {
                try
                {
                    string path = Path.Combine(pkg.SourceDir, "symbols", meta.File);
                    using var fs = File.OpenRead(path);
                    var src = new DxfReader(fs).Read();
                    if (src.Entities.Any(e => e is not (Line or Circle or Arc or TextEntity))) { skipped++; continue; }
                    var fp = BlockFingerprint.Extract(src.Entities);
                    if (fp is null) { skipped++; continue; }
                    symbols.Add((meta.Name, meta.Class, fp));
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
                { skipped++; }
            }

        // re-scan 清陈旧块级种子（仅 marker；保墙/轴 GeometryInference 与 HumanConfirmed）
        overlay.Annotations.RemoveAll(a => a.Provenance == Provenance.GeometryInference
            && a.Evidence.StartsWith(EvidencePrefix, StringComparison.Ordinal));
        var humanHandles = overlay.Annotations.Where(a => a.Provenance == Provenance.HumanConfirmed)
            .Select(a => a.TargetHandle).ToHashSet();

        var insertsByBlock = walked.Where(w => w.Entity is Insert)
            .GroupBy(w => ((Insert)w.Entity).Block.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var autoSeeds = new List<BlockAutoSeed>();
        var suggestions = new List<BlockSuggestion>();
        var ambiguous = new List<BlockAmbiguous>();
        int scanned = 0, excluded = 0;

        foreach (var block in doc.BlockRecords)
        {
            if (IsExcluded(block)) { excluded++; continue; }
            scanned++;
            var instances = insertsByBlock.FirstOrDefault(g => g.Key.Equals(block.Name, StringComparison.OrdinalIgnoreCase))?.ToList()
                ?? new List<WalkedEntity>();
            var fp = BlockFingerprint.Extract(block.Entities);
            if (fp is null)
            {
                if (instances.Count >= minInstances)
                    ambiguous.Add(new BlockAmbiguous(block.Name, instances.Count, "代理图形不可读",
                        instances.Take(5).Select(w => w.HandlePath).ToList()));
                continue;
            }
            var exact = symbols.Where(s => s.Fp.Hash == fp.Hash).ToList();
            if (exact.Count > 0)
            {
                var sym = exact[0];
                var handles = new List<string>();
                foreach (var w in instances)
                {
                    if (humanHandles.Contains(w.HandlePath)) continue;
                    overlay.UpsertAnnotation(new SemanticAnnotation(w.HandlePath, sym.Class,
                        Provenance.GeometryInference, 0.9, $"{EvidencePrefix} {sym.Name}", w.SheetId));
                    handles.Add(w.HandlePath);
                }
                if (handles.Count > 0) autoSeeds.Add(new BlockAutoSeed(block.Name, sym.Name, handles));
                continue;
            }
            var candidates = symbols
                .Where(s => BlockFingerprint.Compatible(s.Fp, fp))
                .Select(s => (s.Name, D: BlockFingerprint.Distance(fp, s.Fp)))
                .Where(t => t.D <= BlockFingerprint.NearThreshold)
                .OrderBy(t => t.D).ThenBy(t => t.Name, StringComparer.Ordinal)
                .Select(t => new BlockCandidate(t.Name, t.D, BlockFingerprint.Confidence(t.D)))
                .ToList();
            if (candidates.Count > 0)
            {
                suggestions.Add(new BlockSuggestion(block.Name, instances.Count, candidates));
                continue;
            }
            if (instances.Count >= minInstances)
                ambiguous.Add(new BlockAmbiguous(block.Name, instances.Count, "无匹配",
                    instances.Take(5).Select(w => w.HandlePath).ToList()));
        }
        return new BlockMatchResult(autoSeeds, suggestions, ambiguous, symbols.Count, skipped, scanned, excluded);
    }
}
```

（符号读取 catch 面按 ACadSharp DxfReader 实际抛型调整——IOException/InvalidOperationException 之外若抛其他型，扩 catch 并钉住测试；白名单校验先于 Extract。）

- [ ] **Step 4: 运行确认绿 + Data 全量**

Run: `dotnet test tests/CadAgent.Data.Tests > outputs/block-fingerprint/t2-green.log 2>&1`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/CadAgent.Data/Semantic/BlockSymbolMatcher.cs tests/CadAgent.Data.Tests/BlockSymbolMatcherTests.cs
GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com git commit -m "feat: block symbol matcher three-tier output (auto-seed 0.9 / suggestions / ambiguous, spec §13.3)"
```

---

### Task 3: Scanner 集成 + CLI scan 加性披露

**Files:**
- Modify: `src/CadAgent.Data/Semantic/SemanticScanner.cs:13-19,35-58`
- Modify: `src/CadAgent.Cli/CommandRouter.cs:609-671`（SemanticScan）
- Modify: `src/CadAgent.Cli/CommandRouter.Skills.cs`（LoadSkillPackages 助手）
- Modify: `tests/CadAgent.Data.Tests/SemanticScannerTests.cs`
- Modify: `tests/CadAgent.Cli.Tests/SemanticE2ETests.cs`

**Interfaces:**
- Consumes: T2 `BlockSymbolMatcher.MatchAndSeed`+`BlockMatchResult`；`SkillLoader.LoadDirectory`；`GetOptionValues(args,"--skill")`
- Produces:
  - `ScanResult` 加性字段：`BlockMatchResult? BlockMatch = null`（末位可选参，旧构造不破）
  - `SemanticScanner.Scan` 加性可选参：`IReadOnlyList<SkillPackage>? symbolPackages = null, int minBlockInstances = 3`（末位追加；旧调用逐字兼容）
  - `CommandRouter.Skills.LoadSkillPackages(string[] args)` internal static → `IReadOnlyList<SkillPackage>`
  - stdout 加性键（恒在）：`blockAutoSeeded`(int=自动种子标注数)、`blockSuggestions`(array)、`blockAmbiguous`(array，reason=="代理图形不可读" 项追加 `guidance:"建议天正 T3 转换后重扫"`)、`symbolsSkipped`(int)
  - 审计 semantic-scan params 加性：`blockAutoSeeded, blockSuggestionCount, blockAmbiguousCount, symbolsSkipped`
  - `--min-block-instances <n>` 旗标（非法值→exit 1 stderr，先于副作用，镜像 --max-tiles 先例）

- [ ] **Step 1: 写失败测试**

SemanticScannerTests 追加：

```csharp
[Fact]
public void Scan_WithSymbolPackages_BlockMatchPopulated_AutoSeedInOverlay()
{
    // 夹具：临时 skill 包（镜像 BlockSymbolMatcherTests 构造）+ DxfFixture 造含克隆块文档
    // Scan(session, symbolPackages: packages) → BlockMatch 非 null + AutoSeeds 1 + overlay GeometryInference 0.9
}

[Fact]
public void Scan_WithoutPackages_BlockMatchNull_BehaviorUnchanged()
{
    // 既有夹具：Scan(session) → BlockMatch null；annotations/ambiguous 与旧口径逐字一致
}
```

（完整夹具代码镜像 BlockSymbolMatcherTests 的 _pkg 构造 + DxfFixture.CreateTempDxf(doc => { var br = new BlockRecord("符号块"); ... doc.BlockRecords.Add(br); doc.Entities.Add(new Insert(br) {...}) ×4; })——实现时展开为完整代码，勿留注释占位。）

SemanticE2ETests 追加：

```csharp
[Fact]
public void Scan_StdoutBlockKeys_AlwaysPresent_Additive()
{
    // 无 --skill：stdout JSON 含 blockAutoSeeded==0 / blockSuggestions==[] / blockAmbiguous==[] / symbolsSkipped==0
    // 旧键 version/annotations/ambiguous/sheets 逐字仍在
}

[Fact]
public void Scan_WithSkill_BlockSuggestionDisclosed_AndAuditParamsAdditive()
{
    // 临时 skill 包 + 近似块 DXF 夹具 → --skill <pkg> scan → blockSuggestions 非空 + candidates confidence<0.8
    // 审计 semantic-scan params 含 blockSuggestionCount>=1
}

[Fact]
public void Scan_MinBlockInstances_InvalidValue_Exit1BeforeSideEffects()
{
    // --min-block-instances abc → exit 1 + 无 audit.db 创建
}

[Fact]
public void Scan_Rescan_ClearsStaleBlockSeeds_KeepsHumanConfirmed()
{
    // scan（精确种子）→ 改块定义 SaveWorking → rescan → 陈旧块种子清 + HumanConfirmed 保留 + version 递增
}
```

（同样展开完整夹具代码；审计断言镜像 SemanticCommandTests 模式：SqliteAuditStore(project/.cadagent/audit.db) + ReadAll + ParamsJson JsonDocument。）

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/CadAgent.Data.Tests --filter Scan_WithSymbolPackages > outputs/block-fingerprint/t3-red-data.log 2>&1` + `dotnet test tests/CadAgent.Cli.Tests --filter Scan_StdoutBlockKeys > outputs/block-fingerprint/t3-red-cli.log 2>&1`
Expected: 编译失败（新参数/键不存在）

- [ ] **Step 3: 实现集成**

SemanticScanner.cs：

```csharp
// ScanResult 追加末位可选字段
public sealed record ScanResult(
    SemanticOverlay Overlay, IReadOnlyList<AmbiguousLayer> Ambiguous,
    IReadOnlyList<VisionAmbiguous>? VisionAmbiguous = null,
    BlockMatchResult? BlockMatch = null);

// Scan 签名追加末位可选参
public static ScanResult Scan(DrawingSession session, SemanticOverlay? existing = null,
    IReadOnlyDictionary<string, SemanticClass>? skillAliases = null, VisionReadResult? vision = null,
    IReadOnlyList<SkillPackage>? symbolPackages = null, int minBlockInstances = 3)
{
    // ... 既有 :41-47 不变（overlay/walked/Seeder）
    BlockMatchResult? blockMatch = null;
    if (symbolPackages is { Count: > 0 })
        blockMatch = BlockSymbolMatcher.MatchAndSeed(session.Doc, walked, overlay, symbolPackages, minBlockInstances);
    // ... 既有 LayerInference/几何/视觉/剪枝不变（剪枝 liveHandles 含 walked Insert 句柄，块种子存活）
    return new ScanResult(overlay, ambiguous, visionAmbiguous, blockMatch);
}
```

CommandRouter.Skills.cs 助手：

```csharp
internal static IReadOnlyList<SkillPackage> LoadSkillPackages(string[] args) =>
    GetOptionValues(args, "--skill").Select(SkillLoader.LoadDirectory).ToList();
```

SemanticScan（CommandRouter.cs:609-671）：
- --min-block-instances 解析先于副作用（镜像 --max-tiles：int.TryParse 失败→stderr+exit 1）；
- `var packages = LoadSkillPackages(args);`
- Scan 调用追加 `symbolPackages: packages, minBlockInstances: minBlockInstances`；
- stdout 匿名对象追加（旧字段逐字不动）：

```csharp
blockAutoSeeded = result.BlockMatch?.AutoSeeds.Sum(s => s.Handles.Count) ?? 0,
blockSuggestions = (result.BlockMatch?.Suggestions ?? Array.Empty<BlockSuggestion>())
    .Select(s => new { block = s.BlockName, instances = s.Instances,
        candidates = s.Candidates.Select(c => new { symbol = c.SymbolName, distance = c.Distance, confidence = c.Confidence }) }),
blockAmbiguous = (result.BlockMatch?.Ambiguous ?? Array.Empty<BlockAmbiguous>())
    .Select(a => new { block = a.BlockName, instances = a.Instances, reason = a.Reason,
        sampleHandles = a.SampleHandles,
        guidance = a.Reason == "代理图形不可读" ? "建议天正 T3 转换后重扫" : null }),
symbolsSkipped = result.BlockMatch?.SymbolsSkipped ?? 0,
```

- 审计 params 匿名对象追加 `blockAutoSeeded, blockSuggestionCount = result.BlockMatch?.Suggestions.Count ?? 0, blockAmbiguousCount = result.BlockMatch?.Ambiguous.Count ?? 0, symbolsSkipped`。

- [ ] **Step 4: 运行确认绿 + Data/Cli 全量**

Run: `dotnet test tests/CadAgent.Data.Tests > outputs/block-fingerprint/t3-green-data.log 2>&1` + `dotnet test tests/CadAgent.Cli.Tests > outputs/block-fingerprint/t3-green-cli.log 2>&1`
Expected: PASS（旧 shape 断言不破——加性键；--vision relay 路径不消费 BlockMatch，行为不变）

- [ ] **Step 5: 提交**

```bash
git add src/CadAgent.Data/Semantic/SemanticScanner.cs src/CadAgent.Cli/CommandRouter.cs src/CadAgent.Cli/CommandRouter.Skills.cs tests/CadAgent.Data.Tests/SemanticScannerTests.cs tests/CadAgent.Cli.Tests/SemanticE2ETests.cs
GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com git commit -m "feat: scanner block-fingerprint signal + scan stdout additive disclosure (spec §13.3)"
```

---

### Task 4: semantic map-block 确认命令

**Files:**
- Modify: `src/CadAgent.Cli/CommandRouter.cs`（semantic 分发 :570-586 + SemanticMapBlock 新方法，镜像 SemanticMap :693-740）
- Create: `tests/CadAgent.Cli.Tests/SemanticMapBlockTests.cs`

**Interfaces:**
- Consumes: T1 Extract/Distance/NearThreshold；T2 EvidencePrefix（人工证据不用 marker）；LoadSkillPackages；BlockWalker.Walk；SemanticOverlay；AuditEntry/SqliteAuditStore；drawing.ComputeHash()
- Produces:
  - CLI `semantic map-block --block <name> --symbol <symbolName> --drawing <path> [--class <cls>] [--skill <pkg>]...`
  - 成功 exit 0 stdout `{block, symbol, @class, instances, distance, exact}`；审计 **semantic-map-block**（Source="semantic"，Target=blockName，params `{block, symbol, @class, instances, distance, exact}`，BeforeHash==AfterHash==drawing.ComputeHash()）
  - 失败 exit 1 stderr；审计 **semantic-map-block-rejected**（params `{block, symbol, reason}`，reason∈{"块不存在","符号不存在","块无几何","符号无几何","指纹不符","class 非法"}）
  - 人工证据字面值：`$"人工确认块映射: {blockName} → {symbolName}"`；HumanConfirmed 1.0；落该块**全部 walked Insert 实例**（含嵌套）；每句柄先 RemoveAll 全 provenance 再 Upsert（镜像 ApplyMappingToAnnotations）
  - **不调 GateDetector**（Yellow 豁免）；class 默认 symbol meta.Class，--class 覆盖须 Enum.TryParse<SemanticClass> 成功

- [ ] **Step 1: 写失败测试**

```csharp
using System.Text.Json;
using CadAgent.Core;
using CadAgent.Data;

namespace CadAgent.Cli.Tests;

public class SemanticMapBlockTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"mapblock-{Guid.NewGuid():N}");
    private readonly string _pkg = Path.Combine(Path.GetTempPath(), $"mapblockpkg-{Guid.NewGuid():N}");
    private readonly string _drawing;

    public SemanticMapBlockTests()
    {
        Directory.CreateDirectory(_project);
        Directory.CreateDirectory(Path.Combine(_pkg, "symbols"));
        File.WriteAllText(Path.Combine(_pkg, "skill.yaml"), "name: test-pkg\nversion: 1.0.0\n");
        File.WriteAllText(Path.Combine(_pkg, "symbols", "symbols.yaml"),
            "- { name: 测试符号, file: sym.dxf, class: Device, description: 夹具 }\n");
        var sym = new ACadSharp.CadDocument();
        sym.Entities.Add(new ACadSharp.Entities.Line { StartPoint = new ACadSharp.XY(0, 0), EndPoint = new ACadSharp.XY(10, 0) });
        sym.Entities.Add(new ACadSharp.Entities.Circle { Center = new ACadSharp.XY(5, 3), Radius = 2 });
        using (var fs = File.Create(Path.Combine(_pkg, "symbols", "sym.dxf")))
            new ACadSharp.IO.DxfWriter(fs).Write(sym);
        var doc = new ACadSharp.CadDocument();
        var br = new ACadSharp.BlockRecord("符号块");
        br.Entities.Add(new ACadSharp.Entities.Line { StartPoint = new ACadSharp.XY(0, 0), EndPoint = new ACadSharp.XY(10, 0) });
        br.Entities.Add(new ACadSharp.Entities.Circle { Center = new ACadSharp.XY(5, 3), Radius = 2 });
        doc.BlockRecords.Add(br);
        for (int i = 0; i < 3; i++) doc.Entities.Add(new ACadSharp.Entities.Insert(br) { InsertPoint = new ACadSharp.XY(i * 100, 0) });
        _drawing = Path.Combine(_project, "d.dxf");
        using (var fs = File.Create(_drawing)) new ACadSharp.IO.DxfWriter(fs).Write(doc);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_project, true); } catch (IOException) { }
        try { Directory.Delete(_pkg, true); } catch (IOException) { }
    }

    private int Run(params string[] extra)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(new[] { "semantic", "map-block", "--drawing", _drawing,
            "--skill", _pkg }.Concat(extra).Append("--project").Append(_project).ToArray(), outW, errW);
        LastStdout = outW.ToString(); LastStderr = errW.ToString();
        return code;
    }
    private string LastStdout = "", LastStderr = "";

    [Fact]
    public void MapBlock_ExactMatch_HumanConfirmedAllInstances_AuditChain()
    {
        int code = Run("--block", "符号块", "--symbol", "测试符号");
        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(LastStdout);
        Assert.True(doc.RootElement.GetProperty("exact").GetBoolean());
        Assert.Equal(3, doc.RootElement.GetProperty("instances").GetInt32());
        var overlay = SemanticOverlay.Load(Path.Combine(_project, ".cadagent", "semantic.json"));
        Assert.Equal(3, overlay.Annotations.Count(a => a.Provenance == Provenance.HumanConfirmed));
        Assert.All(overlay.Annotations, a => { Assert.Equal(1.0, a.Confidence); Assert.Equal(SemanticClass.Device, a.Class); Assert.StartsWith("人工确认块映射:", a.Evidence); });
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        var rec = Assert.Single(audit.ReadAll(), r => r.Entry.Kind == "semantic-map-block");
        Assert.Equal(rec.Entry.BeforeHash, rec.Entry.AfterHash);
        Assert.True(audit.VerifyChain(out _));
        // 语义闸：HumanConfirmed 1.0 过 minConfidence 0.8
        Assert.All(overlay.Annotations, a => Assert.True(a.Confidence >= 0.8));
    }

    [Fact]
    public void MapBlock_NearMatch_WithinThreshold_Confirms()
    {
        // 改块定义微扰（d≤0.15）→ 确认成功 exact=false distance>0
        var doc = ACadSharp.IO.DxfReader... // 读回 _drawing 改 Line EndPoint (10.5,0) 重写
        int code = Run("--block", "符号块", "--symbol", "测试符号");
        Assert.Equal(0, code);
        using var j = JsonDocument.Parse(LastStdout);
        Assert.False(j.RootElement.GetProperty("exact").GetBoolean());
        Assert.InRange(j.RootElement.GetProperty("distance").GetDouble(), 0.001, 0.15);
    }

    [Fact]
    public void MapBlock_FingerprintMismatch_RejectedAudited_Exit1()
    {
        // 块定义改走样（加对角线→histogram 不兼容 d=1）→ exit 1 + semantic-map-block-rejected reason=指纹不符 + overlay 无 HumanConfirmed
    }

    [Fact]
    public void MapBlock_UnknownSymbol_Rejected_Exit1()
    {
        int code = Run("--block", "符号块", "--symbol", "不存在");
        Assert.Equal(1, code);
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.Contains(audit.ReadAll(), r => r.Entry.Kind == "semantic-map-block-rejected");
    }

    [Fact]
    public void MapBlock_UnknownBlock_Rejected_Exit1()
    {
        int code = Run("--block", "不存在块", "--symbol", "测试符号");
        Assert.Equal(1, code);
    }

    [Fact]
    public void MapBlock_InvalidClass_Rejected_Exit1()
    {
        int code = Run("--block", "符号块", "--symbol", "测试符号", "--class", "NotAClass");
        Assert.Equal(1, code);
    }

    [Fact]
    public void MapBlock_ClassOverride_Applied()
    {
        int code = Run("--block", "符号块", "--symbol", "测试符号", "--class", "Lighting");
        Assert.Equal(0, code);
        var overlay = SemanticOverlay.Load(Path.Combine(_project, ".cadagent", "semantic.json"));
        Assert.All(overlay.Annotations, a => Assert.Equal(SemanticClass.Lighting, a.Class));
    }

    [Fact]
    public void MapBlock_NoGateInvocation_OverlayOnly_YellowExemptParity()
    {
        // 成功跑后审计无 run-rejected/intake-rejected（map-block 不走闸）；drawing 文件字节前后一致
        byte[] before = File.ReadAllBytes(_drawing);
        Assert.Equal(0, Run("--block", "符号块", "--symbol", "测试符号"));
        Assert.Equal(before, File.ReadAllBytes(_drawing));
        using var audit = new SqliteAuditStore(Path.Combine(_project, ".cadagent", "audit.db"));
        Assert.DoesNotContain(audit.ReadAll(), r => r.Entry.Kind is "run-rejected" or "intake-rejected");
    }

    [Fact]
    public void MapBlock_RescanAfterConfirm_HumanConfirmedPreserved()
    {
        Assert.Equal(0, Run("--block", "符号块", "--symbol", "测试符号"));
        var outW = new StringWriter(); var errW = new StringWriter();
        CommandRouter.Run(new[] { "semantic", "scan", "--drawing", _drawing, "--skill", _pkg,
            "--project", _project }, outW, errW);
        var overlay = SemanticOverlay.Load(Path.Combine(_project, ".cadagent", "semantic.json"));
        Assert.Equal(3, overlay.Annotations.Count(a => a.Provenance == Provenance.HumanConfirmed));
    }
}
```

（NearMatch/Mismatch 夹具的读回改写展开完整代码：`using var fs = File.OpenRead(_drawing); var doc = new DxfReader(fs).Read();` 改实体后 `DxfWriter` 重写——镜像 DxfFixture 模式；勿留注释占位。）

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test tests/CadAgent.Cli.Tests --filter MapBlock > outputs/block-fingerprint/t4-red.log 2>&1`
Expected: FAIL（map-block 子命令不存在→exit 非 0/未知命令）

- [ ] **Step 3: 实现 SemanticMapBlock**

分发：semantic 族 case 追加 `"map-block" => SemanticMapBlock(args, outW, errW)`。方法镜像 SemanticMap 结构：

```csharp
private static int SemanticMapBlock(string[] args, TextWriter outW, TextWriter errW)
{
    string blockName = RequireOption(args, "--block", errW); // 失败 return 1 镜像现状
    string symbolName = RequireOption(args, "--symbol", errW);
    string drawingPath = RequireOption(args, "--drawing", errW);
    // --class 可选；--min-block-instances 不适用
    var op = IdentityResolver.Resolve(args); // 镜像 SemanticMap 身份解析
    string agentDir = ProjectDir(args); // 镜像现状 --project 解析
    using var audit = OpenAudit(agentDir); // 镜像 :626
    DrawingSession drawing;
    try { drawing = DrawingSession.Load(drawingPath); }
    catch (Exception ex) { /* intake-rejected 审计镜像 scan :635-637 */ return 1; }
    var packages = LoadSkillPackages(args);
    // 符号首包命中（OrdinalIgnoreCase，镜像 ImportSymbolHandler）
    SymbolMeta? meta = null; SkillPackage? owner = null;
    foreach (var p in packages) { meta = p.Symbols.FirstOrDefault(s => s.Name.Equals(symbolName, StringComparison.OrdinalIgnoreCase)); if (meta != null) { owner = p; break; } }
    if (meta is null) return Reject(audit, op, drawing, blockName, symbolName, "符号不存在", errW);
    var block = drawing.Doc.BlockRecords.FirstOrDefault(b => b.Name.Equals(blockName, StringComparison.OrdinalIgnoreCase));
    if (block is null) return Reject(..., "块不存在", ...);
    var fpBlock = BlockFingerprint.Extract(block.Entities);
    if (fpBlock is null) return Reject(..., "块无几何", ...);
    CadDocument symDoc;
    try { using var fs = File.OpenRead(Path.Combine(owner!.SourceDir, "symbols", meta.File)); symDoc = new DxfReader(fs).Read(); }
    catch { return Reject(..., "符号无几何", ...); }
    var fpSym = BlockFingerprint.Extract(symDoc.Entities);
    if (fpSym is null) return Reject(..., "符号无几何", ...);
    bool exact = fpBlock.Hash == fpSym.Hash;
    double d = exact ? 0 : BlockFingerprint.Distance(fpBlock, fpSym);
    if (!exact && d > BlockFingerprint.NearThreshold) return Reject(..., "指纹不符", ...);
    SemanticClass cls = meta.Class;
    string? classOpt = GetOptionValue(args, "--class");
    if (classOpt is not null && !Enum.TryParse(classOpt, out cls)) return Reject(..., "class 非法", ...);
    var walked = BlockWalker.Walk(drawing.Doc, drawing.Sheets).ToList();
    var instances = walked.Where(w => w.Entity is Insert ins && ins.Block.Name.Equals(blockName, StringComparison.OrdinalIgnoreCase)).ToList();
    string overlayPath = Path.Combine(agentDir, "semantic.json");
    var overlay = SemanticOverlay.Load(overlayPath);
    foreach (var w in instances)
    {
        overlay.Annotations.RemoveAll(a => a.TargetHandle == w.HandlePath);
        overlay.UpsertAnnotation(new SemanticAnnotation(w.HandlePath, cls, Provenance.HumanConfirmed, 1.0,
            $"人工确认块映射: {blockName} → {symbolName}", w.SheetId));
    }
    overlay.Save(overlayPath);
    string hash = drawing.ComputeHash();
    audit.Append(new AuditEntry(DateTimeOffset.UtcNow, op.Account, op.Role.ToString(), session, "semantic",
        "semantic-map-block", blockName,
        JsonSerializer.Serialize(new { block = blockName, symbol = symbolName, @class = cls.ToString(), instances = instances.Count, distance = d, exact }),
        hash, hash, "", ""));
    outW.WriteLine(JsonSerializer.Serialize(new { block = blockName, symbol = symbolName, @class = cls.ToString(), instances = instances.Count, distance = d, exact }, SemanticJsonOpts));
    return 0;
}
// Reject：审计 semantic-map-block-rejected（params {block, symbol, reason}，hash==hash）+ stderr + return 1
```

（RequireOption/GetOptionValue/IdentityResolver/ProjectDir/OpenAudit/session 变量名——**读现状 SemanticMap/SemanticScan 逐字镜像**，上面为结构骨架；session 字符串取法镜像 scan（"semantic" 会话标识或drawingPath——以现状为准）。BlockFingerprint/BlockSymbolMatcher 在 Data internal——Cli 引用 Data：BlockFingerprint 已 public；Extract public 可用。records public namespace 级（T2 裁定）。）

- [ ] **Step 4: 运行确认绿 + Cli 全量**

Run: `dotnet test tests/CadAgent.Cli.Tests > outputs/block-fingerprint/t4-green.log 2>&1`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/CadAgent.Cli/CommandRouter.cs tests/CadAgent.Cli.Tests/SemanticMapBlockTests.cs
GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com git commit -m "feat: semantic map-block confirmation command (re-verify + HumanConfirmed + audited refusal, spec §13.3)"
```

---

### Task 5: 真实标本实证 + spec 闭环 + 门禁

**Files:**
- Create: `tests/CadAgent.Data.Tests/RealBlockFingerprintTests.cs`（skip-gated）
- 证据: `outputs/block-fingerprint/`（combat stdout/对照/阈值校准）
- Modify: spec §12（追加闭环注记）

**Interfaces:**
- Consumes: T1-T4 全链；真实标本 `outputs/api-probe/specimen-elec.dwg`（电施强电副本，源 D: 零触碰）+ `examples/skills/electrical`（324 符号）
- Produces: 实证数字入 spec §12；门禁连绿两次

- [ ] **Step 1: skip-gated 真实测试**

```csharp
using CadAgent.Core.Skills;
using CadAgent.Data;
using CadAgent.Data.Semantic;

namespace CadAgent.Data.Tests;

public class RealBlockFingerprintTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CadAgent.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [SkippableFact]
    public void RealSpecimen_Elec_ThreeTierOutput_HonestDisclosure()
    {
        string root = RepoRoot();
        string specimen = Path.Combine(root, "outputs", "api-probe", "specimen-elec.dwg");
        string pkgDir = Path.Combine(root, "examples", "skills", "electrical");
        Skip.If(!File.Exists(specimen), "电施标本不在本机，跳过");
        Skip.If(!Directory.Exists(pkgDir), "electrical skill 包不在本机，跳过");
        // 副本（源零触碰）
        string tmp = Path.Combine(Path.GetTempPath(), $"realbf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            string copy = Path.Combine(tmp, "elec.dwg");
            File.Copy(specimen, copy);
            byte[] before = File.ReadAllBytes(copy);
            var session = DrawingSession.Load(copy);
            var packages = new[] { SkillLoader.LoadDirectory(pkgDir) };
            var result = SemanticScanner.Scan(session, symbolPackages: packages);
            var bm = Assert.NotNull(result.BlockMatch);
            Assert.Equal(324, bm.SymbolsLoaded + bm.SymbolsSkipped); // 库全量 accounted
            Assert.True(bm.BlocksScanned + bm.BlocksExcluded > 200); // 普查 246 非空块口径
            // 自动种子全 0.9 + marker；建议全闸下 + 公式；歧义 reason 合法 + 样本非空
            var overlay = result.Overlay;
            Assert.All(overlay.Annotations.Where(a => a.Evidence.StartsWith("几何指纹精确匹配:")),
                a => { Assert.Equal(0.9, a.Confidence); Assert.Equal(Provenance.GeometryInference, a.Provenance); });
            Assert.All(bm.Suggestions.SelectMany(s => s.Candidates), c =>
            {
                Assert.InRange(c.Distance, 0, BlockFingerprint.NearThreshold);
                Assert.Equal(BlockFingerprint.Confidence(c.Distance), c.Confidence, 9);
                Assert.True(c.Confidence < 0.8);
            });
            Assert.All(bm.Ambiguous, a =>
            {
                Assert.Contains(a.Reason, new[] { "代理图形不可读", "无匹配" });
                Assert.NotEmpty(a.SampleHandles);
                Assert.True(a.Instances >= 3);
            });
            Assert.Equal(before, File.ReadAllBytes(copy)); // 只读扫描
        }
        finally { try { Directory.Delete(tmp, true); } catch (IOException) { } }
    }
}
```

- [ ] **Step 2: 真实 CLI 对照实战**（bash，证据 outputs/block-fingerprint/）

电施副本临时项目 + `--skill examples/skills/electrical` scan → stdout 存档（blockAutoSeeded/blockSuggestions/blockAmbiguous/symbolsSkipped 精确数字）；审计 verify 绿；**诚实期望：GB 库精确命中可能 0**（语料用 TArch $equip$+自定义块）——记录实际三级分布；$equip$ 代理块若落歧义验证 reason；高频自定义块（`2`/`2021_0722_204741` 等）去向记录。阈值校准：若近似建议全空且目视有相似块，探针距离分布（outputs/api-probe 模式）→ 0.15 保持或裁定调整（调整须 spec 注记+测试同步，**不静默改阈**）。map-block 真实确认一例（若有命中/建议）→ HumanConfirmed + 验链；无命中则合成夹具已覆盖，诚实记录。

- [ ] **Step 3: spec §12 注记**（追加式：块级纠偏闭环——三级实证数字/阈值校准/诚实边界（$equip$ 代理不可匹配/GB 命中率）/剩余 deferred（View 面板/建议持久化/子块递归）；旧注记 0 删）+ 提交。

- [ ] **Step 4: 门禁连绿两次** `dotnet test CadAgent.sln`（ZWCAD 真机前后 tasklist；日志 outputs/block-fingerprint/gate{1,2}.log；精确数字；基线 1203+新增）。

- [ ] **Step 5: 提交（test:/docs:）；push/PR 保留用户批准（finishing 菜单）**。

---

## Self-Review 记录

- **Spec 覆盖**：§13.1 分级→T2 自动 0.9/建议闸下+T4 HumanConfirmed；§13.2 算法→T1（归一/量化/哈希/签名/距离/公式）；§13.3 组件数据流→T2/T3/T4（排除/索引/加性 stdout/审计/map-block 复核/Yellow 豁免）；§13.4 错误并发→T2 符号跳过计数+T4 拒绝面+单写入通道复用；§13.5 测试→各任务+T5 真实；§13.6 不做→无渲染/无持久化/无 View/无递归。
- **占位符扫描**：T3/T4 测试骨架注释处已明示"展开完整代码镜像 X 模式"——实现者按指定夹具模式展开（BlockSymbolMatcherTests._pkg/DxfFixture/ImportSymbolTests 现成）；算法/命令主体完整代码给出。
- **类型一致性**：BlockMatchResult/BlockSuggestion/BlockCandidate/BlockAmbiguous/BlockAutoSeed T2 定义（namespace 级 public）T3 ScanResult 引用 T4 不直接消费；EvidencePrefix T2 定义 T3 清理测试引用；NearThreshold/Confidence T1 定义 T2/T4/T5 消费；Scan 加性参末位可选旧调用兼容。
- **已知取舍**：贪心匹配 O(n²)（符号≤143 实体，块同量级，可接受）；Aspect 容差 0.2（量化 0.1 网格两倍裕度）；弧角差权重 0.5（角度量纲与长度折中，钉住测试判别）；符号首包命中同 insert-symbol 先例（重名跨包取首）。
---

## 执行记录（SDD 2026-11-23）

- T1 指纹算法：b7b4b65（Data 339；R1 定型 XYZ/弧度/when 守卫）。
- T2 三级匹配器：379c78b（Data 351；R2 宽 catch DxfException 实证；R5 夹具 12.0 修正 brief 数学矛盾；R6 空包早退保种子；弧测试闭合 T1 缺口）。
- T3 Scanner+CLI 集成：91f0982（Data 357/Cli 226；T2 四测试缺口补；加性纪律逐项核实）。
- T4 map-block：5ea99d7（Cli 235；复核制/六 reason/overlay-only/Yellow 豁免镜像逐字核实）。
- T5 真实实证：be95adf+ebb8a00（门禁 1246/0/4×2；**三级分布 102 自动种子/16 建议块 30 候选/68 歧义（无匹配 33+代理 35）**；map-block 真实确认 $equip$00002501→双联单控开关 122 实例 HumanConfirmed+re-scan 保留+验链绿；R7 闸下措辞更正/R8 $equip$00002832 占位几何 reason=无匹配更正；阈值 0.15 不动 R3）。
- 最终全分支审查：1 Important（map-block 用法校验顺序）→修复波 6ff1c71（+注释三处+§12 窄化披露 R9+测试四补；Cli 238/Data 358）；scoped 复审全 ADDRESSED。
- Rulings R1-R9 入 ledger（R1 API 定型/R2 宽 catch/R3 阈值不静默改/R4 首命中+Hatch 代代理/R5 夹具矛盾修正/R6 空包早退/R7 闸下措辞/R8 占位几何更正/R9 spec 三窄化披露 deferred）。
- 遗留 deferred（整支 triage 可留）：量化 ToEven 抖动/弧角绕回入哈希（Distance 兜底）；贪心非对称（消费固定序）；裸旗标静默默认/数字 class（SemanticMap 同病后续统一）；零实例 instances=0；歧义 histogram/bbox 诊断字段；NaN 剔除披露计数；半径比签名；map-block Load 失败 CLI 测试；存量警告。
- spec §12 闭环注记（ebb8a00）+窄化披露（6ff1c71）；§13 设计权威。

---

## 修复波 2（PR#21 合并前 ISTQB 审查，2026-11-23，追加式）

用户裁定"先修后合"。本波纯测试新增（M2 传播链已存在，无需产品微调），覆盖 2 Important + 6 择要 Minor；deferred（M7-M11）在案不做。

- **I1（安全关键拒绝面零测试）**：SemanticMapBlockTests 补 2 测——`MapBlock_BlockNoGeometry_RejectedAudited_Exit1`（块定义仅 TEXT+Hatch 矩形边界路径，零白名单几何 + 合法符号 → exit1 + reason="块无几何" + VerifyChain + 无成功记录 + overlay 不落）；`MapBlock_SymbolNoGeometry_RejectedAudited_Exit1`（垃圾文本 → DxfReader 抛 catch 分支 / 仅 TEXT 合法 DXF → Extract null 分支，双分支各留痕 reason="符号无几何" + VerifyChain）。Hatch 夹具镜像 MoveEntityTests 往返先例。
- **I2（哈希顺序无关无条件守卫）**：BlockFingerprintTests 补 `Extract_EntityOrderPermuted_SameHash`——Symbol() 反转 + 多实体（2 线 2 圆 1 弧）固定置换 → 哈希逐位相等，不依赖标本。
- **M1 点状退化**：`Extract_PointDegenerate_Null`（零长线 / 重合微半径圆 → scale<1e-9 → null）。实证修正：ACadSharp `Circle.Radius` 要求 >0（零半径抛 ArgumentOutOfRangeException），故"重合圆"用微半径 1e-12（bbox 2e-12）构造退化，非零半径。
- **M2 --min-block-instances 传播**：SemanticE2ETests 补 `Scan_MinBlockInstancesTwo_PropagatesToMatcher`（2 实例无匹配块默认 min=3 不歧义 / --min-block-instances 2 → 入歧义 instances=2 + 审计 blockAmbiguousCount=1 双通道）。
- **M3 同哈希取首确定性**：BlockSymbolMatcherTests 补 `ExactMultiSymbolSameHash_FirstWinsDeterministic`（yaml 序 + 包序双维度，class/evidence 随首符号）。
- **M4 R6 早退守卫**：`AllSymbolsBroken_PresetSeedsPreserved_R6EarlyExit`（预置 marker 种子 + 全符号损坏 → symbols.Count==0 早退，既有种子不清，扫描计数全 0）。
- **M5 边界择要**：`Compatible_AspectDeltaExactly02_Accepted_JustOverRejected`（|ΔAspect|=0.2 恰接受 / 0.2001 拒绝）；`Excluded_AttdefBboxExactly500_Excluded_JustUnderNot`（最长边 500 恰排除 / 499 不排除）；`Distance_ExactlyAtNearThreshold_ValuePinned` + `MapBlock_DistanceExactlyAtThreshold_Accepted`（双圆夹具内圆平移 3 → d=0.3/2=0.15 精确双精度，算法侧 + CLI 侧 <= 恰接受）；`Excluded_PaperPrefix_CaseInsensitive`（*Paper 前缀 OrdinalIgnoreCase，带几何隔离前缀规则）；`Ambiguous_SampleHandlesCappedAtFive_SixInstances`（6 实例 → 样本恰 5 且 walked 前 5 序）。
- **M6 贪心占用**：`Distance_GreedyMultiEntity_UsedOccupancyPinned`（两线两圆交叉最近邻，有占用 d=0.15 / 无占用回归判别 0.05）。
- **门禁**：Data.Tests 368（358+10）/ Cli.Tests 242（238+4）全绿；全 sln 一次 1262 通过 +4 跳过（基线 1248+14）；ZWCAD 真机前后 tasklist 无 CAD 进程残留。日志 outputs/block-fingerprint/fixwave2-*.log。
- **不做（deferred 在案）**：M7 DxfReader 释放 / M8 垂直 AttDef / M9 map-block 不复用 IsExcluded / M10 硬编 324 / M11 reason 语义宽——留后续。
