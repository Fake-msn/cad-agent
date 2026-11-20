# CAD 智能体终端 · skill 机制 实现计划（计划 2b-ii-a）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 自定义 skill 包机制：skill.yaml 清单+规则+别名加载校验、跨包规则 Id 守卫、别名接入 2a 语义扫描、检查器硬化（mode 校验/标高候选窗/参数守卫）、结构化跳过报告、裁定指纹+规则包版本入审计、CLI skill 命令族与 promote 沉淀流程。

**Architecture:** Core 新增 Skill 模型/加载器/SkillSet（厂商无关，YamlDotNet）；Data 扫描器加性别名 overload + 检查器硬化；Cli skill 命令族 + check/scan --skill 接线。电气/模型规则数据、步骤模板、符号库、skill run 在 2b-ii-b。

**Tech Stack:** .NET 8、ACadSharp 3.8.35、YamlDotNet 16.2.1、System.Text.Json、xUnit。

**Spec:** `docs/superpowers/specs/2026-11-20-cad-agent-terminal-design.md` §6.3/§6.4/§6.5、§4.4（映射提升为院标别名）、§5；承接 2b-i 审查 triage（规则Id守卫/mode校验/结构化跳过/裁定指纹/加载器硬化/标高窗扩展/孤儿裁定/FP epsilon/裁定Load去重）。

## Global Constraints

- Core 不引用 ACadSharp/厂商；Data 引用 ACadSharp 3.8.35（CSMath/XYZ）。
- skill 包结构（spec §6.3）：`skill.yaml`（name/version/discipline/drawingType/description）+ `rules/*.yaml`（2b-i Rule schema 原样）+ `aliases.yaml`（图层/块名→SemanticClass）；steps/symbols 目录 2b-ii-b 定义，本计划加载器**容忍其存在但不解析**。
- 规则 Id 全局唯一（builtin+所有 skill）：冲突→拒绝加载整包+明确错误（列冲突 Id）；孤儿裁定（ruleId 不在加载集）→adjudicate 拒绝 exit 1。
- 别名优先级：人工映射 > skill 别名 > 内置约定库；别名大小写不敏感。
- 结构化跳过：SkipReason(Category, RuleId, Message)；Category∈{Exempt, PendingOriginal, NoChecker, CheckerError, NotEvaluable}；overlay 依赖检查器（axis-letter/elevation-format）在 overlay.Annotations 空时→NotEvaluable 跳过计数（取代 2b-i interim stderr 提示的计数语义；stderr 提示保留兼容）。
- 裁定指纹：check-run/run-regression 审计 paramsJson 加 adjudicationHash（裁定集规范化 JSON SHA256，空集=固定值）+ rulePackVersion（builtin 常量 "gb50001-2017.r1" + skill name@version 列表）。
- 检查器硬化：未知 mode→抛 ArgumentException（引擎隔离记账 CheckerError）；标高候选窗扩展覆盖 0/4+ 位小数违规；CheckParams null 元素守卫 + 容差 epsilon 1e-9。
- promote：项目图层映射写入 skill aliases.yaml + manifest version patch +1（内容变更必须升版）；审计 semantic-promoted。
- 一切新操作入审计；load 失败入审计（LoadOrReject 模式）；168 既有测试持续绿；计划1/2a/2b-i 命令行为不变（加性）。
- TDD；pristine；feat:/fix: 前缀；git 署名环境变量 Fake-msn <Fake-msn@users.noreply.github.com>（不改 config）。
- CLI UTF-8；Windows GBK 不回显中文；源文件 UTF-8；新文件带 EOF 换行。

## 文件结构

```
src/CadAgent.Core/
  Skills/SkillManifest.cs     SkillManifest + InvalidSkillException
  Skills/SkillPackage.cs      SkillPackage（Manifest/Rules/Aliases）+ SkillLoader.LoadDirectory
  Skills/SkillSet.cs          多包+内置合并、Id 冲突守卫、Aliases 合并、RulePackVersion
  Rules/Adjudication.cs       加性：Fingerprint()（规范化JSON SHA256）+ Load 去重
  Rules/IRuleChecker.cs       加性：IOverlayDependent 接口 + SkipReason record + CheckSummary.StructuredSkips（SkippedReasons 改投影）+ 引擎 NotEvaluable 分支
src/CadAgent.Data/
  Semantic/SemanticScanner.cs 加性：Scan overload(skillAliases)
  Semantic/ConventionLibrary.cs 加性：LayerInference.Annotate overload(skillAliases)
  Checks/ElevationChecker.cs  候选窗扩展 + mode 校验
  Checks/LayerNameChecker.cs  mode 校验
  Checks/CheckParams.cs       null 守卫 + epsilon
  Checks/AxisLetterChecker.cs IOverlayDependent 标记
src/CadAgent.Cli/
  CommandRouter.cs            加性：skill list/validate、check --skill、scan --skill、promote、孤儿裁定校验、指纹接线
tests/...
```

---

### Task 1: skill 清单与包加载器（Core）

**Files:**
- Create: `src/CadAgent.Core/Skills/SkillManifest.cs`、`src/CadAgent.Core/Skills/SkillPackage.cs`
- Test: `tests/CadAgent.Core.Tests/SkillLoaderTests.cs`

**Interfaces:**
- Produces:
  - `sealed record SkillManifest(string Name, string Version, string Discipline, string DrawingType, string Description)`
  - `sealed class InvalidSkillException : Exception`（含 inner exception ctor——修复 2b-i T1 内层异常丢失）
  - `sealed record SkillAlias(string LayerName, SemanticClass Class)`
  - `sealed class SkillPackage`：`SkillManifest Manifest`、`IReadOnlyList<Rule> Rules`、`IReadOnlyList<SkillAlias> Aliases`、`string SourceDir`
  - `static class SkillLoader`：`SkillPackage LoadDirectory(string dir)`——读 skill.yaml（缺失→InvalidSkillException）；rules/*.yaml 经 RuleLoader.LoadYaml（包内 Id 重复→InvalidSkillException）；aliases.yaml 可选（缺失→空）；steps/symbols 目录容忍不解析；manifest 校验：Name/Version 非空，Version 形如 digits.digits.digits（semver 简化）；YAML 未知键拒绝（ strict，沿用 RoleConfig 先例）；异常带文件名+inner

- [ ] **Step 1: 写失败测试**

```csharp
namespace CadAgent.Core.Tests;

public class SkillLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"skill-{Guid.NewGuid():N}");
    public SkillLoaderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private void WriteSkill(string version = "1.0.0", string rules = "", string aliases = "")
    {
        File.WriteAllText(Path.Combine(_dir, "skill.yaml"), $"""
            name: 示例院标
            version: "{version}"
            discipline: architecture
            drawingType: any
            description: 测试 skill
            """);
        if (rules != "")
        {
            Directory.CreateDirectory(Path.Combine(_dir, "rules"));
            File.WriteAllText(Path.Combine(_dir, "rules", "r1.yaml"), rules);
        }
        if (aliases != "")
            File.WriteAllText(Path.Combine(_dir, "aliases.yaml"), aliases);
    }

    private const string RuleYaml = """
        id: SK-TEST-001
        source: { standard: "院标", clause: "1.0", excerpt: "测试条款", verification: source-text }
        modal: "应"
        force: mandatory
        severity: high
        scope: { discipline: architecture, drawingType: any }
        check: { type: layer-name, params: { mode: charset } }
        suggestion: "测试"
        """;

    [Fact]
    public void Load_ValidPackage_ManifestRulesAliases()
    {
        WriteSkill(rules: RuleYaml, aliases: "aliases:\n  - layerName: W-新墙\n    class: Wall\n");
        var pkg = SkillLoader.LoadDirectory(_dir);
        Assert.Equal("示例院标", pkg.Manifest.Name);
        Assert.Equal("1.0.0", pkg.Manifest.Version);
        Assert.Single(pkg.Rules);
        Assert.Single(pkg.Aliases);
        Assert.Equal(SemanticClass.Wall, pkg.Aliases[0].Class);
    }

    [Fact]
    public void Load_MissingManifest_Throws()
    {
        var ex = Assert.Throws<InvalidSkillException>(() => SkillLoader.LoadDirectory(_dir));
        Assert.Contains("skill.yaml", ex.Message);
    }

    [Fact]
    public void Load_BadVersion_Throws()
    {
        WriteSkill(version: "abc");
        Assert.Throws<InvalidSkillException>(() => SkillLoader.LoadDirectory(_dir));
    }

    [Fact]
    public void Load_DuplicateRuleId_Throws()
    {
        WriteSkill(rules: RuleYaml);
        File.WriteAllText(Path.Combine(_dir, "rules", "r2.yaml"), RuleYaml);
        var ex = Assert.Throws<InvalidSkillException>(() => SkillLoader.LoadDirectory(_dir));
        Assert.Contains("SK-TEST-001", ex.Message);
    }

    [Fact]
    public void Load_NoAliasesFile_Empty()
    {
        WriteSkill();
        var pkg = SkillLoader.LoadDirectory(_dir);
        Assert.Empty(pkg.Aliases);
        Assert.Empty(pkg.Rules);
    }

    [Fact]
    public void Load_StepsSymbolsDirs_Tolerated()
    {
        WriteSkill();
        Directory.CreateDirectory(Path.Combine(_dir, "steps"));
        Directory.CreateDirectory(Path.Combine(_dir, "symbols"));
        var pkg = SkillLoader.LoadDirectory(_dir);
        Assert.Equal("示例院标", pkg.Manifest.Name);
    }

    [Fact]
    public void Load_UnknownManifestKey_Throws()
    {
        WriteSkill();
        File.AppendAllText(Path.Combine(_dir, "skill.yaml"), "\nbogusKey: x\n");
        Assert.Throws<InvalidSkillException>(() => SkillLoader.LoadDirectory(_dir));
    }
}
```

- [ ] **Step 2: RED** — 编译失败。
- [ ] **Step 3: 实现** — YamlDotNet strict DTO→record；aliases.yaml 格式 `{aliases: [{layerName, class}]}`（class 枚举 ignoreCase）；Version 正则 `^\d+\.\d+\.\d+$`；InvalidSkillException(message, inner) ctor。
- [ ] **Step 4: GREEN** — 7 条 PASS。
- [ ] **Step 5: Commit** — `feat: skill package manifest/loader with validation, aliases, strict schema`

---

### Task 2: SkillSet 合并与 Id 冲突守卫（Core）

**Files:**
- Create: `src/CadAgent.Core/Skills/SkillSet.cs`
- Test: `tests/CadAgent.Core.Tests/SkillSetTests.cs`

**Interfaces:**
- Consumes: SkillPackage/SkillLoader（T1）、Rule（2b-i）、BuiltinRules 在 Data——**Core 不引用 Data**：SkillSet 接收 builtin rules 作为参数
- Produces:
  - `sealed class SkillSet`：`static SkillSet Compose(IReadOnlyList<Rule> builtinRules, IReadOnlyList<SkillPackage> packages)`——Id 冲突（builtin×skill 或 skill×skill）→InvalidSkillException 列冲突 Id+来源；`IReadOnlyList<Rule> AllRules`（builtin 序+包序确定性）；`IReadOnlyDictionary<string, SemanticClass> MergedAliases`（OrdinalIgnoreCase；后包覆盖前包同层名——确定性包序）；`string RulePackVersion`（"gb50001-2017.r1" 常量 + "|name@version" 拼接）；`bool ContainsRule(string id)`

- [ ] **Step 1: 写失败测试**

```csharp
namespace CadAgent.Core.Tests;

public class SkillSetTests
{
    private static Rule R(string id) => new(id,
        new RuleSource("院标", "1.0", "摘录", "source-text"), "应",
        RuleForce.Recommended, RuleSeverity.High, new RuleScope(),
        new CheckSpec("layer-name", new Dictionary<string, object> { ["mode"] = "charset" }), "建议");

    private static SkillPackage Pkg(string name, string version, params Rule[] rules) =>
        new() { Manifest = new SkillManifest(name, version, "any", "any", ""), Rules = rules,
                Aliases = Array.Empty<SkillAlias>(), SourceDir = "." };

    [Fact]
    public void Compose_NoCollision_MergesRulesAndVersion()
    {
        var set = SkillSet.Compose(new[] { R("B1") }, new[] { Pkg("院标A", "1.0.0", R("S1")) });
        Assert.Equal(2, set.AllRules.Count);
        Assert.Contains("gb50001-2017.r1", set.RulePackVersion);
        Assert.Contains("院标A@1.0.0", set.RulePackVersion);
        Assert.True(set.ContainsRule("S1"));
        Assert.False(set.ContainsRule("NOPE"));
    }

    [Fact]
    public void Compose_BuiltinSkillCollision_Throws()
    {
        var ex = Assert.Throws<InvalidSkillException>(() =>
            SkillSet.Compose(new[] { R("DUP") }, new[] { Pkg("院标A", "1.0.0", R("DUP")) }));
        Assert.Contains("DUP", ex.Message);
    }

    [Fact]
    public void Compose_SkillSkillCollision_Throws()
    {
        Assert.Throws<InvalidSkillException>(() =>
            SkillSet.Compose(Array.Empty<Rule>(), new[] { Pkg("A", "1.0.0", R("X")), Pkg("B", "1.0.0", R("X")) }));
    }

    [Fact]
    public void Compose_AliasesMerge_CaseInsensitive_LastWins()
    {
        var p1 = Pkg("A", "1.0.0");
        p1.Aliases = new[] { new SkillAlias("W-墙", SemanticClass.Wall) };
        var p2 = Pkg("B", "1.0.0");
        p2.Aliases = new[] { new SkillAlias("w-墙", SemanticClass.Column) };
        var set = SkillSet.Compose(Array.Empty<Rule>(), new[] { p1, p2 });
        Assert.Equal(SemanticClass.Column, set.MergedAliases["W-墙"]);
    }
}
```
（SkillPackage 属性可初始化——T1 实现为 sealed class 带 init/set 属性以支持此构造；若 T1 用 ctor 则测试适配，契约不变。）

- [ ] **Step 2: RED** — 编译失败。
- [ ] **Step 3: 实现** — 按契约；冲突错误消息列 Id+双方来源名。
- [ ] **Step 4: GREEN** — 4 条 PASS。
- [ ] **Step 5: Commit** — `feat: SkillSet composition with global rule-Id collision guard and merged aliases`

---

### Task 3: 别名接入语义扫描（Data 加性）

**Files:**
- Modify: `src/CadAgent.Data/Semantic/ConventionLibrary.cs`（LayerInference.Annotate overload）、`src/CadAgent.Data/Semantic/SemanticScanner.cs`（Scan overload）
- Test: `tests/CadAgent.Data.Tests/SkillAliasScanTests.cs`

**Interfaces:**
- Consumes: SkillAlias/SemanticClass（Core）；2a LayerInference/SemanticScanner
- Produces:
  - `LayerInference.Annotate(DrawingSession session, SemanticOverlay overlay, IReadOnlyDictionary<string, SemanticClass>? skillAliases = null)`——原签名保留（默认 null 调新 overload）；优先级：已有人工标注跳过 > overlay.Mappings > **skillAliases（LayerInference 0.8，证据"skill别名: {layer}"）** > 内置约定库
  - `SemanticScanner.Scan(DrawingSession session, SemanticOverlay? existing = null, IReadOnlyDictionary<string, SemanticClass>? skillAliases = null)`——原签名保留；不明确清单判定同步：skillAliases 命中的层**不再不明确**
  - 既有无别名调用行为逐字节不变

- [ ] **Step 1: 写失败测试**

```csharp
using CadAgent.Core;
using CSMath;
using ACadSharp.Entities;
using ACadSharp.Tables;

namespace CadAgent.Data.Tests;

public class SkillAliasScanTests : IDisposable
{
    private readonly string _path;
    public SkillAliasScanTests() => _path = DxfFixture.CreateTempDxf(doc =>
    {
        doc.Layers.Add(new Layer("W-自定义"));
        doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(100, 0),
            Layer = doc.Layers.First(l => l.Name == "W-自定义") });
    });
    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Scan_WithSkillAlias_AnnotatesAndRemovesAmbiguous()
    {
        var aliases = new Dictionary<string, SemanticClass>(StringComparer.OrdinalIgnoreCase)
        { ["W-自定义"] = SemanticClass.Wall };
        var result = SemanticScanner.Scan(DrawingSession.Load(_path), null, aliases);
        Assert.Empty(result.Ambiguous);
        var a = Assert.Single(result.Overlay.Annotations);
        Assert.Equal(SemanticClass.Wall, a.Class);
        Assert.Contains("skill别名", a.Evidence);
    }

    [Fact]
    public void Scan_WithoutAlias_StillAmbiguous()
    {
        var result = SemanticScanner.Scan(DrawingSession.Load(_path));
        Assert.Contains("W-自定义", result.Ambiguous.Select(x => x.LayerName));
    }

    [Fact]
    public void Scan_HumanMappingBeatsSkillAlias()
    {
        var existing = new SemanticOverlay();
        existing.ConfirmMapping(new LayerMapping("W-自定义", SemanticClass.Column, "u", DateTimeOffset.UnixEpoch));
        var aliases = new Dictionary<string, SemanticClass> { ["W-自定义"] = SemanticClass.Wall };
        var result = SemanticScanner.Scan(DrawingSession.Load(_path), existing, aliases);
        var a = Assert.Single(result.Overlay.Annotations);
        Assert.Equal(SemanticClass.Column, a.Class);
        Assert.Equal(Provenance.HumanConfirmed, a.Provenance);
    }
}
```

- [ ] **Step 2: RED** — 编译失败（overload 不存在）。
- [ ] **Step 3: 实现** — 加性 overload；原方法委托新方法（null）；不明确判定加 skillAliases 命中检查。
- [ ] **Step 4: GREEN** — 3 条 PASS + 2a 既有扫描测试不变。
- [ ] **Step 5: Commit** — `feat: skill aliases integrate into semantic scan (human > skill > builtin precedence)`

---

### Task 4: 检查器硬化（Data）

**Files:**
- Modify: `src/CadAgent.Data/Checks/ElevationChecker.cs`、`LayerNameChecker.cs`、`CheckParams.cs`、`AxisLetterChecker.cs`
- Test: `tests/CadAgent.Data.Tests/CheckerHardeningTests.cs`

**Interfaces:**
- Produces:
  - ElevationChecker/LayerNameChecker：未知 mode（非空且非合法值）→`ArgumentException($"未知 mode: {mode}")`（引擎隔离记 CheckerError）；空 mode 行为不变
  - ElevationChecker 候选窗扩展：`^(±|[+-])?\d+(\.\d+)?$`（覆盖 0 位/4+ 位小数）；0 位→decimals issue（High，消息"应注写到小数点后第三位"）；4+ 位→decimals issue（High）；2 位→Low+总平面注记（不变）；3 位合规；sign 检查仅对有小数点的值（"3" 无小数→sign 模式不查符号？裁定：sign 模式对整数值也查 +/± 前缀——"＋3" 违规；实现按此）
  - CheckParams：null 元素→跳过（不产生幻影 0 值）；容差比较 `<= ToleranceMm + 1e-9`
  - AxisLetterChecker/ElevationChecker 实现 `IOverlayDependent`（T5 定义于 Core——**本任务先不加标记，T5 统一加**；本任务只做 mode/窗/参数硬化）

- [ ] **Step 1: 写失败测试**

```csharp
using CadAgent.Core;
using CSMath;
using ACadSharp.Entities;

namespace CadAgent.Data.Tests;

public class CheckerHardeningTests : IDisposable
{
    private readonly string _path;
    private readonly SemanticOverlay _overlay = new();
    public CheckerHardeningTests()
    {
        _path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "3", Height = 5 });      // 0位小数
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "3.0000", Height = 5 }); // 4位
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(20, 0), Value = "3.000", Height = 5 });  // 合规
        });
        var session = DrawingSession.Load(_path);
        foreach (var e in session.Summarize().Entities)
            _overlay.UpsertAnnotation(new SemanticAnnotation(e.Handle, SemanticClass.Annotation, Provenance.HumanConfirmed, 1.0, "t"));
    }
    public void Dispose() => File.Delete(_path);

    private static Rule ElevRule(string mode) => new("R-E",
        new RuleSource("GB/T 50001-2017", "11.8.4", "摘录", "source-text"), "应",
        RuleForce.Recommended, RuleSeverity.High, new RuleScope(),
        new CheckSpec("elevation-format", new Dictionary<string, object> { ["mode"] = mode }), "建议");

    [Fact]
    public void Elevation_ZeroAndFourDecimals_Flagged()
    {
        var issues = new ElevationChecker().Check(ElevRule("decimals"), DrawingSession.Load(_path), _overlay);
        Assert.Contains(issues, i => i.Message.Contains("\"3\""));
        Assert.Contains(issues, i => i.Message.Contains("3.0000"));
        Assert.DoesNotContain(issues, i => i.Message.Contains("3.000\""));
    }

    [Fact]
    public void Elevation_UnknownMode_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new ElevationChecker().Check(ElevRule("bogus"), DrawingSession.Load(_path), _overlay));
    }

    [Fact]
    public void LayerName_UnknownMode_Throws()
    {
        var rule = new Rule("R-L", new RuleSource("GB/T 50001-2017", "13.0.1", "摘录", "source-text"),
            "不得", RuleForce.Recommended, RuleSeverity.High, new RuleScope(),
            new CheckSpec("layer-name", new Dictionary<string, object> { ["mode"] = "bogus" }), "建议");
        Assert.Throws<ArgumentException>(() =>
            new LayerNameChecker().Check(rule, DrawingSession.Load(_path), _overlay));
    }

    [Fact]
    public void CheckParams_NullElement_Skipped_NoPhantomZero()
    {
        var values = CheckParams.ToDoubleArray(new List<object> { 3.5, null!, 5 });
        Assert.Equal(new[] { 3.5, 5 }, values);
    }
}
```
（CheckParams.ToDoubleArray 若为 internal 则测试同程序集可见性已具备——Data.Tests 经 InternalsVisibleTo 或 public；按 T4 既有可见性适配，意图不变。消息引号格式沿用 T5 裁定。）

- [ ] **Step 2: RED** — 编译失败/行为失败。
- [ ] **Step 3: 实现** — 按契约；sign 模式整数查前缀；epsilon 入 CheckParams 容差辅助。
- [ ] **Step 4: GREEN** — 4 条 PASS + 既有检查器测试不变（标高既有测试值均 2-3 位，窗扩展不影响）。
- [ ] **Step 5: Commit** — `fix: checker hardening — mode validation, elevation window 0/4+ decimals, param null guard, tolerance epsilon`

---

### Task 5: 结构化跳过报告（Core + Cli）

**Files:**
- Modify: `src/CadAgent.Core/Rules/IRuleChecker.cs`、`src/CadAgent.Data/Checks/AxisLetterChecker.cs`、`ElevationChecker.cs`、`src/CadAgent.Cli/CommandRouter.cs`
- Test: `tests/CadAgent.Core.Tests/StructuredSkipTests.cs`、`tests/CadAgent.Cli.Tests/StructuredSkipCliTests.cs`

**Interfaces:**
- Produces:
  - `sealed record SkipReason(string Category, string RuleId, string Message)`；Category 常量类 `SkipCategories { Exempt, PendingOriginal, NoChecker, CheckerError, NotEvaluable }`
  - `interface IOverlayDependent { }`（标记接口；AxisLetterChecker/ElevationChecker 实现）
  - `CheckSummary` 加性：ctor 第 5 参 `IReadOnlyList<SkipReason>? StructuredSkips = null`；`SkippedReasons` 改为投影属性 `=> (StructuredSkips ?? ...).Select(s => $"{s.RuleId}: {s.Message}")`——**既有字符串断言兼容**（引擎旧 reason 格式 "规则 {id} 已豁免" 等须保持投影后子串兼容：Message 含旧文案）
  - 引擎：overlay.Annotations.Count==0 && checker is IOverlayDependent → Skip(NotEvaluable, "无语义标注，未评估")；其余跳过路径迁移到结构化（Exempt/PendingOriginal/NoChecker/CheckerError），Message 保留旧文案子串
  - CLI check/regression：stderr 跳过行改用结构化（"跳过: [{Category}] {RuleId}: {Message}"）；**2b-i interim 的 overlayPresent stderr 提示保留**（兼容既有测试）；审计 paramsJson skippedReasons 改结构化数组

- [ ] **Step 1: 写失败测试**

Core：
```csharp
namespace CadAgent.Core.Tests;

public class StructuredSkipTests
{
    private sealed class FakeSession : IDrawingSession
    {
        public string SourcePath => "f.dxf";
        public string ComputeHash() => "h";
        public void SaveWorking() { }
        public void Reload() { }
    }
    private sealed class OverlayChecker : IRuleChecker, IOverlayDependent
    {
        public string CheckType => "overlay-dep";
        public IReadOnlyList<Issue> Check(Rule r, IDrawingSession s, SemanticOverlay o) =>
            new[] { new Issue(r.Id, "1", "S", "e", r.Force, r.Severity, new[] { "h" }, "m", "s", "b") };
    }
    private static Rule R(string id) => new(id, new RuleSource("S", "1", "e", "source-text"),
        "应", RuleForce.Recommended, RuleSeverity.High, new RuleScope(),
        new CheckSpec("overlay-dep", new()), "s");

    [Fact]
    public void EmptyOverlay_OverlayDependent_NotEvaluableSkip()
    {
        var engine = new RuleEngine(new[] { new OverlayChecker() });
        var summary = engine.Evaluate(new[] { R("R1") }, new FakeSession(), new SemanticOverlay(), new AdjudicationSet());
        Assert.Equal(0, summary.RulesRun);
        Assert.Equal(1, summary.RulesSkipped);
        var skip = Assert.Single(summary.StructuredSkips!);
        Assert.Equal(SkipCategories.NotEvaluable, skip.Category);
        Assert.Equal("R1", skip.RuleId);
        Assert.Contains("R1", summary.SkippedReasons[0]); // 投影兼容
    }

    [Fact]
    public void NonEmptyOverlay_OverlayDependent_Runs()
    {
        var overlay = new SemanticOverlay();
        overlay.UpsertAnnotation(new SemanticAnnotation("h", SemanticClass.Wall, Provenance.HumanConfirmed, 1.0, "t"));
        var engine = new RuleEngine(new[] { new OverlayChecker() });
        var summary = engine.Evaluate(new[] { R("R1") }, new FakeSession(), overlay, new AdjudicationSet());
        Assert.Equal(1, summary.RulesRun);
        Assert.Single(summary.Issues);
    }

    [Fact]
    public void ExemptSkip_StructuredCategory()
    {
        var adj = new AdjudicationSet();
        adj.Upsert(new Adjudication("R1", "u", DateTimeOffset.UnixEpoch, "豁免", null, true));
        var engine = new RuleEngine(new[] { new OverlayChecker() });
        var summary = engine.Evaluate(new[] { R("R1") }, new FakeSession(), new SemanticOverlay(), adj);
        Assert.Equal(SkipCategories.Exempt, summary.StructuredSkips![0].Category);
        Assert.Contains("已豁免", summary.SkippedReasons[0]); // 旧文案子串兼容
    }
}
```
Cli：check 无 overlay → 审计 paramsJson skippedReasons 含 category="NotEvaluable" 条目（axis-letter/elevation-format 两规则）；stderr 含 "[NotEvaluable]"；既有 Check_NoOverlay 测试的提示行仍在。

- [ ] **Step 2: RED** — 编译失败。
- [ ] **Step 3: 实现** — 按契约；CheckSummary record 加性第 5 参；引擎所有 Skip 调用结构化；CLI stderr/审计接线。
- [ ] **Step 4: GREEN** — 新测试 PASS + 168 既有全绿（投影兼容是关键——旧断言子串逐一核对）。
- [ ] **Step 5: Commit** — `feat: structured skip report with NotEvaluable overlay-dependent accounting`

---

### Task 6: 裁定指纹与孤儿校验（Core + Cli）

**Files:**
- Modify: `src/CadAgent.Core/Rules/Adjudication.cs`、`src/CadAgent.Cli/CommandRouter.cs`
- Test: `tests/CadAgent.Core.Tests/AdjudicationFingerprintTests.cs`、`tests/CadAgent.Cli.Tests/OrphanAdjudicationTests.cs`

**Interfaces:**
- Produces:
  - `AdjudicationSet.Fingerprint()`→string：Items 按 RuleId Ordinal 排序→规范化 JSON（固定字段序、枚举 PascalCase、Ts "O"）→SHA256 大写 hex；空集→SHA256("[]")
  - `AdjudicationSet.Load` 去重：同 RuleId 多条→保留最后一条（与 Upsert latest-wins 一致；修复 EffectiveForce/IsExempt 分歧）
  - CLI rule adjudicate：ruleId 不在加载集（builtin+--skill 包）→stderr+exit 1（**不留孤儿裁定**）；adjudicate 命令加 --skill 可选选项（与 check 一致加载规则集做校验）
  - check/run-regression 审计 paramsJson 加性：adjudicationHash + rulePackVersion（SkillSet.RulePackVersion；无 skill 时 builtin 常量）

- [ ] **Step 1: 写失败测试**

Core：
```csharp
namespace CadAgent.Core.Tests;

public class AdjudicationFingerprintTests
{
    [Fact]
    public void Fingerprint_EmptySet_Stable()
    {
        Assert.Equal(new AdjudicationSet().Fingerprint(), new AdjudicationSet().Fingerprint());
        Assert.Equal(64, new AdjudicationSet().Fingerprint().Length);
    }

    [Fact]
    public void Fingerprint_OrderIndependent()
    {
        var a = new AdjudicationSet();
        a.Upsert(new Adjudication("R2", "u", DateTimeOffset.UnixEpoch, "r"));
        a.Upsert(new Adjudication("R1", "u", DateTimeOffset.UnixEpoch, "r"));
        var b = new AdjudicationSet();
        b.Upsert(new Adjudication("R1", "u", DateTimeOffset.UnixEpoch, "r"));
        b.Upsert(new Adjudication("R2", "u", DateTimeOffset.UnixEpoch, "r"));
        Assert.Equal(a.Fingerprint(), b.Fingerprint());
    }

    [Fact]
    public void Fingerprint_ContentChange_Differs()
    {
        var a = new AdjudicationSet();
        a.Upsert(new Adjudication("R1", "u", DateTimeOffset.UnixEpoch, "r1"));
        var b = new AdjudicationSet();
        b.Upsert(new Adjudication("R1", "u", DateTimeOffset.UnixEpoch, "r2"));
        Assert.NotEqual(a.Fingerprint(), b.Fingerprint());
    }

    [Fact]
    public void Load_DuplicateRuleId_LastWins()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dup-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {"Items":[
              {"RuleId":"R1","Operator":"u","Ts":"2026-01-01T00:00:00+00:00","Reason":"old","ForceOverride":null,"Exempt":false,"Interpretation":""},
              {"RuleId":"R1","Operator":"u","Ts":"2026-01-02T00:00:00+00:00","Reason":"new","ForceOverride":"Mandatory","Exempt":false,"Interpretation":""}
            ]}
            """);
        try
        {
            var set = AdjudicationSet.Load(path);
            Assert.Single(set.Items);
            Assert.Equal("new", set.Items[0].Reason);
        }
        finally { File.Delete(path); }
    }
}
```
Cli：adjudicate 未知 ruleId → exit 1 + stderr + **审计无 rule-adjudicated 记录**；adjudicate 已知 builtin Id → exit 0；check 审计含 adjudicationHash（64 hex）+ rulePackVersion 含 "gb50001-2017.r1"。

- [ ] **Step 2: RED** — 编译失败/行为失败。
- [ ] **Step 3: 实现** — 指纹规范化（System.Text.Json 排序字典或手工拼接固定序）；Load 去重；CLI 校验+审计字段。
- [ ] **Step 4: GREEN** — 新测试 PASS + 既有全绿。
- [ ] **Step 5: Commit** — `feat: adjudication fingerprint, load dedup, orphan-rule rejection, audit pack version`

---

### Task 7: CLI skill 命令族与 promote（Cli）

**Files:**
- Modify: `src/CadAgent.Cli/CommandRouter.cs`
- Test: `tests/CadAgent.Cli.Tests/SkillCommandTests.cs`

**Interfaces:**
- Consumes: T1-T6 全部
- Produces:
  - `skill list [--project dir]`：扫描 project/.cadagent/skills/*/（每子目录一个包）→JSON [{name, version, ruleCount, aliasCount}]→exit 0；目录缺失→空列表 exit 0
  - `skill validate <dir>`：SkillLoader.LoadDirectory→stdout 摘要→exit 0；InvalidSkillException→stderr+exit 1
  - `check --drawing <file> [--skill <dir>]... [--project dir]`：--skill 可重复；加载包→SkillSet.Compose(builtin, packages)→冲突→stderr+exit 1（审计 check-rejected? 裁定：冲突属用法错误，stderr+exit 1 不审计——与 adjudicate 互斥校验先例一致）；规则集=SkillSet.AllRules；别名暂不影响 check（check 不扫描）
  - `semantic scan --drawing <file> [--skill <dir>]... [--project dir]`：包别名→MergedAliases→Scan overload；其余行为不变
  - `semantic promote --layer <name> --skill <dir> [--project dir]`：项目 overlay.Mappings 含该层→写入/更新 skill aliases.yaml（upsert）+manifest version patch+1→审计 semantic-promoted（paramsJson={layer, class, skill, newVersion}）→exit 0；无映射→stderr+exit 1；skill 目录非法→LoadOrReject 审计
  - 项目 skill 目录约定：`.cadagent/skills/<name>/`；skill list 读它；--skill 显式路径优先
  - 未知 skill 子命令→stderr+exit 1

- [ ] **Step 1: 写失败测试**

```csharp
namespace CadAgent.Cli.Tests;

public class SkillCommandTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"skcli-{Guid.NewGuid():N}");
    private readonly string _skillDir;
    private readonly string _drawing;
    public SkillCommandTests()
    {
        Directory.CreateDirectory(_project);
        _skillDir = Path.Combine(_project, "myskill");
        Directory.CreateDirectory(Path.Combine(_skillDir, "rules"));
        File.WriteAllText(Path.Combine(_skillDir, "skill.yaml"), """
            name: 测试院标
            version: "1.0.0"
            discipline: architecture
            drawingType: any
            description: 测试
            """);
        File.WriteAllText(Path.Combine(_skillDir, "rules", "r.yaml"), """
            id: SK-TEST-001
            source: { standard: "院标", clause: "1.0", excerpt: "测试条款", verification: source-text }
            modal: "应"
            force: mandatory
            severity: high
            scope: { discipline: any, drawingType: any }
            check: { type: layer-name, params: { mode: charset } }
            suggestion: "测试"
            """);
        File.WriteAllText(Path.Combine(_skillDir, "aliases.yaml"), "aliases:\n  - layerName: W-自定义\n    class: Wall\n");
        _drawing = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Layers.Add(new ACadSharp.Tables.Layer("W-自定义"));
            doc.Entities.Add(new ACadSharp.Entities.Line
            {
                StartPoint = (CSMath.XYZ)new CSMath.XY(0, 0), EndPoint = (CSMath.XYZ)new CSMath.XY(100, 0),
                Layer = doc.Layers.First(l => l.Name == "W-自定义")
            });
        });
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_project, true);
        File.Delete(_drawing);
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    [Fact]
    public void SkillValidate_Valid_ExitZero()
    {
        var (code, stdout, _) = Run("skill", "validate", _skillDir);
        Assert.Equal(0, code);
        Assert.Contains("测试院标", stdout);
    }

    [Fact]
    public void SkillValidate_BadVersion_ExitOne()
    {
        File.WriteAllText(Path.Combine(_skillDir, "skill.yaml"),
            File.ReadAllText(Path.Combine(_skillDir, "skill.yaml")).Replace("1.0.0", "abc"));
        var (code, _, stderr) = Run("skill", "validate", _skillDir);
        Assert.Equal(1, code);
        Assert.NotEmpty(stderr);
    }

    [Fact]
    public void Check_WithSkill_MandatoryRule_ExitTwo()
    {
        // SK-TEST-001 mandatory layer-charset：fixture 层 W-自定义 纯汉字无混用→不违规；
        // 加混用层触发
        var drawing2 = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Layers.Add(new ACadSharp.Tables.Layer("W-墙"));
            doc.Entities.Add(new ACadSharp.Entities.Line
            {
                StartPoint = (CSMath.XYZ)new CSMath.XY(0, 0), EndPoint = (CSMath.XYZ)new CSMath.XY(1, 1),
                Layer = doc.Layers.First(l => l.Name == "W-墙")
            });
        });
        try
        {
            var (code, stdout, _) = Run("check", "--drawing", drawing2, "--skill", _skillDir);
            Assert.Equal(2, code); // skill mandatory 规则生效
            Assert.Contains("SK-TEST-001", stdout);
        }
        finally { File.Delete(drawing2); }
    }

    [Fact]
    public void Check_SkillIdCollision_ExitOne()
    {
        // 复制 builtin Id 到 skill
        File.WriteAllText(Path.Combine(_skillDir, "rules", "dup.yaml"),
            File.ReadAllText(Path.Combine(_skillDir, "rules", "r.yaml")).Replace("SK-TEST-001", "R-50001-5.0.2"));
        var (code, _, stderr) = Run("check", "--drawing", _drawing, "--skill", _skillDir);
        Assert.Equal(1, code);
        Assert.Contains("R-50001-5.0.2", stderr);
    }

    [Fact]
    public void Scan_WithSkillAlias_RemovesAmbiguous()
    {
        var (code, stdout, _) = Run("semantic", "scan", "--drawing", _drawing, "--skill", _skillDir);
        Assert.Equal(0, code);
        Assert.DoesNotContain("W-自定义", stdout); // 别名命中→不再不明确
    }

    [Fact]
    public void Promote_WritesAlias_BumpsVersion_Audited()
    {
        Run("semantic", "scan", "--drawing", _drawing);
        Run("semantic", "map", "--layer", "W-自定义", "--class", "Column", "--drawing", _drawing);
        var (code, _, _) = Run("semantic", "promote", "--layer", "W-自定义", "--skill", _skillDir);
        Assert.Equal(0, code);
        string aliases = File.ReadAllText(Path.Combine(_skillDir, "aliases.yaml"));
        Assert.Contains("Column", aliases);
        string manifest = File.ReadAllText(Path.Combine(_skillDir, "skill.yaml"));
        Assert.Contains("1.0.1", manifest); // patch bump
        var (vc, _, _) = Run("audit", "verify");
        Assert.Equal(0, vc);
    }

    [Fact]
    public void Promote_NoMapping_ExitOne()
    {
        var (code, _, stderr) = Run("semantic", "promote", "--layer", "不存在", "--skill", _skillDir);
        Assert.Equal(1, code);
        Assert.NotEmpty(stderr);
    }

    [Fact]
    public void SkillList_ProjectSkills_ShowsPackage()
    {
        string skillsRoot = Path.Combine(_project, ".cadagent", "skills", "测试院标");
        Directory.CreateDirectory(skillsRoot);
        foreach (string f in Directory.GetFiles(_skillDir))
            File.Copy(f, Path.Combine(skillsRoot, Path.GetFileName(f)));
        Directory.CreateDirectory(Path.Combine(skillsRoot, "rules"));
        File.Copy(Path.Combine(_skillDir, "rules", "r.yaml"), Path.Combine(skillsRoot, "rules", "r.yaml"));
        var (code, stdout, _) = Run("skill", "list");
        Assert.Equal(0, code);
        Assert.Contains("测试院标", stdout);
    }
}
```

- [ ] **Step 2: RED** — 命令未识别。
- [ ] **Step 3: 实现** — 按契约；promote 的 aliases.yaml upsert 用 YamlDotNet 序列化（保持格式）；version patch bump 正则替换末位。
- [ ] **Step 4: GREEN** — 8 条 PASS + 既有全绿。
- [ ] **Step 5: Commit** — `feat: CLI skill list/validate, check/scan --skill, promote with version bump + audit`

---

### Task 8: 端到端与承接记录

**Files:**
- Create: `tests/CadAgent.Cli.Tests/SkillE2ETests.cs`、示例 skill `examples/skills/sample-institute/`（skill.yaml+rules/1条+aliases.yaml——**入库示例，非真实院标**）
- Modify: spec §12

**Interfaces:**
- E2E：示例 skill（含 1 条 mandatory 图层规则 + 别名）→skill validate→scan --skill（别名生效）→check --skill exit 2→adjudicate 豁免该 skill 规则→check exit 0→promote 项目映射到示例 skill 副本（version bump）→audit verify 绿（含 semantic-promoted + adjudicationHash + rulePackVersion 含 skill@version）→指纹复用：同裁定集重跑 check 审计 adjudicationHash 一致
- spec §12 追加 2b-ii-a 完成记录：skill 机制落地（包结构/加载校验/Id守卫/别名优先级/结构化跳过/指纹/promote升版）；steps/symbols/skill run+电气模型规则数据=2b-ii-b；LLM 起草 skill=计划3；示例 skill 非真实院标（真实院标待用户提供）

- [ ] **Step 1: 写示例 skill + E2E 测试**（完整代码）
- [ ] **Step 2: 运行**（红则修）
- [ ] **Step 3: 全套件** `dotnet test` 全绿
- [ ] **Step 4: spec §12 追加**
- [ ] **Step 5: Commit** — `test: skill mechanism end-to-end + sample institute skill; docs: plan-2b-ii-a carry-overs`

---

## 自审记录

- 契约一致性：SkillManifest/SkillPackage/SkillAlias/SkillSet 跨 T1-T7 形状一致；CheckSummary 第 5 参加性+投影兼容旧断言（T5 关键风险——旧 reason 文案子串逐一保留）；IOverlayDependent T5 定义并标记（T4 不做）。
- 2b-i 承接全收：Id 守卫（T2/T7）、mode 校验（T4）、结构化跳过（T5）、指纹+包版本（T6）、孤儿裁定（T6）、Load 去重（T6）、inner exception（T1）、null 守卫+epsilon（T4）、标高窗扩展（T4）、LoadDirectory 测试（T1 覆盖）。
- 168 既有测试保护；投影兼容是 T5 最大风险，测试断言旧文案子串。
- 诚实边界：steps/symbols 容忍不解析（2b-ii-b）；示例 skill 非真实院标；check 冲突不审计（用法错误先例）；promote 升 patch 版。