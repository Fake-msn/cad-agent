# CAD 智能体终端 · 规范引擎与裁定 实现计划（计划 2b-i）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 确定性规范检查引擎：规则 schema（来源引用+效力分级+情态词）、7 条已核实的 GB/T 50001-2017 内置规则、问题清单 CLI、解读裁定工作流（效力override/豁免/解释，持久化+审计+复用）。

**Architecture:** Core 新增规则模型/裁定模型/引擎接口（厂商无关，YamlDotNet 已有）；Data 新增检查器实现（ACadSharp 实体属性+2a 语义叠加层）；Cli 新增 check/rule 命令族。skill 机制、电气/模型规则包、步骤模板在 2b-ii。

**Tech Stack:** .NET 8、ACadSharp 3.8.35、YamlDotNet 16.2.1、System.Text.Json、xUnit。

**Spec:** `docs/superpowers/specs/2026-11-20-cad-agent-terminal-design.md` §6.1/§6.2/§6.5、§4.1③、§5、§9。
**规则数据来源:** `standards-src/extraction-50001.md`（MinerU 转换+外部核验底稿，gitignored；规则 YAML 只含条款号+短摘录，不复制全文）。

## Global Constraints

- **效力映射（控制者裁定，写入规则数据）**：GB/T 推荐性国标——必须/严禁→mandatory；应/不应/不得→recommended（severity=high）；宜/不宜→recommended（severity=medium）；可→informational。**默认无拦截**；mandatory 拦截权来自院标 skill 升级或人工裁定（2b-ii/裁定）。
- **引用义务**：每条规则必带 source{standard, clause, excerpt, verification}；excerpt 逐字自 extraction 报告（含 LaTeX 标记归一化注记）；verification∈{source-text, external-corroborated, pending-original}；**pending-original 的数值不得建规则**。
- **标准身份注记**：GB/T 50001-2017 编号经外部全文核验（转换件缺封面），规则 source 注 verification=external-corroborated（身份）+ source-text（条文）。
- 检查只读不改图；问题清单必带 ruleId/条款引用/效力/严重度/目标句柄/消息/建议。
- 裁定（adjudication）持久化 `.cadagent/adjudications.json`、入审计（rule-adjudicated）、check 时应用（效力override/豁免/解释注记）；裁定集纳入可复现性契约。
- 一切 check/adjudicate 操作入审计（check-run/rule-adjudicated）；load 失败入审计（intake-rejected 模式，沿用 2a LoadOrReject）。
- Core 不引用 ACadSharp/厂商；Data 引用 ACadSharp 3.8.35（CSMath/XYZ；实体属性以包 XML 文档核实）。
- 计划 1/2a 契约不破坏；116 既有测试持续绿；StepEngine 本计划不改（回归检查在 CLI 层组合）。
- TDD；pristine；feat:/fix: 前缀；git 署名环境变量 Fake-msn <Fake-msn@users.noreply.github.com>（不改 config）。
- CLI UTF-8；Windows GBK 终端不回显中文；源文件 UTF-8。
- 检测启发式诚实边界：标高/轴号检测依赖语义叠加层+模式匹配，误报可能——issue 消息注明检测依据；无 overlay 时相关规则跳过并计数报告（不静默）。

## 文件结构

```
src/CadAgent.Core/
  Rules/RuleModel.cs        RuleSource/RuleForce/RuleSeverity/RuleScope/CheckSpec/Rule
  Rules/RuleLoader.cs       YAML 加载+schema 校验（InvalidRuleException）
  Rules/Adjudication.cs     Adjudication/AdjudicationSet（Load/Save JSON 原子写，沿用 SemanticOverlay 模式）
  Rules/Issue.cs            Issue/IssueList/CheckSummary
  Rules/IRuleChecker.cs     IRuleChecker 接口 + RuleEngine 组合（裁定应用+跳过计数）
src/CadAgent.Data/
  Checks/TextHeightChecker.cs    text-height-series（5.0.2）
  Checks/LineweightChecker.cs    lineweight-group（4.0.1）
  Checks/AxisLetterChecker.cs    axis-letter（8.0.4，overlay 驱动）
  Checks/ElevationChecker.cs     elevation-decimals + elevation-sign（11.8.4/5，overlay+模式）
  Checks/LayerNameChecker.cs     layer-charset + layer-structure（13.0.1/2）
  Rules/builtin/*.yaml           7 条内置规则数据
src/CadAgent.Cli/
  CommandRouter.cs          加性：check / rule adjudicate / rule list
tests/...
```

---

### Task 1: 规则模型与 YAML 加载（Core）

**Files:**
- Create: `src/CadAgent.Core/Rules/RuleModel.cs`、`src/CadAgent.Core/Rules/RuleLoader.cs`
- Test: `tests/CadAgent.Core.Tests/RuleLoaderTests.cs`

**Interfaces:**
- Produces:
  - `enum RuleForce { Mandatory, Recommended, Informational }`
  - `enum RuleSeverity { High, Medium, Low }`
  - `sealed record RuleSource(string Standard, string Clause, string Excerpt, string Verification)`
  - `sealed record RuleScope(string Discipline = "any", string DrawingType = "any")`
  - `sealed record CheckSpec(string Type, Dictionary<string, object> Params)`（YAML 参数经 YamlDotNet 反序列化为 object——数值为 double/string/list，检查器自行转换）
  - `sealed record Rule(string Id, RuleSource Source, string Modal, RuleForce Force, RuleSeverity Severity, RuleScope Scope, CheckSpec Check, string Suggestion)`
  - `static class RuleLoader`：`Rule LoadYaml(string yaml)`、`IReadOnlyList<Rule> LoadDirectory(string dir)`（*.yaml 逐文件；非法→InvalidRuleException 含文件名+原因）；校验：Id 非空、Source 四字段非空、Verification∈枚举集、Force/Severity 合法、Check.Type 非空
  - `sealed class InvalidRuleException : Exception`

- [ ] **Step 1: 写失败测试**

```csharp
namespace CadAgent.Core.Tests;

public class RuleLoaderTests
{
    private const string ValidYaml = """
        id: R-50001-5.0.2
        source:
          standard: "GB/T 50001-2017"
          clause: "5.0.2"
          excerpt: "文字的字高，应从表 5.0.2 中选用。"
          verification: source-text
        modal: "应／宜"
        force: recommended
        severity: high
        scope: { discipline: any, drawingType: any }
        check:
          type: text-height-series
          params:
            hanVector: [3.5, 5, 7, 10, 14, 20]
            other: [3, 4, 6, 8, 10, 14, 20]
        suggestion: "字高改选系列值"
        """;

    [Fact]
    public void LoadYaml_Valid_ParsesAllFields()
    {
        var rule = RuleLoader.LoadYaml(ValidYaml);
        Assert.Equal("R-50001-5.0.2", rule.Id);
        Assert.Equal("GB/T 50001-2017", rule.Source.Standard);
        Assert.Equal("5.0.2", rule.Source.Clause);
        Assert.Equal(RuleForce.Recommended, rule.Force);
        Assert.Equal(RuleSeverity.High, rule.Severity);
        Assert.Equal("text-height-series", rule.Check.Type);
        Assert.Equal(2, rule.Check.Params.Count);
    }

    [Fact]
    public void LoadYaml_MissingSource_Throws()
    {
        var ex = Assert.Throws<InvalidRuleException>(() => RuleLoader.LoadYaml("id: X\ncheck:\n  type: t\n"));
        Assert.Contains("source", ex.Message);
    }

    [Fact]
    public void LoadYaml_BadVerification_Throws()
    {
        string yaml = ValidYaml.Replace("verification: source-text", "verification: guessed");
        Assert.Throws<InvalidRuleException>(() => RuleLoader.LoadYaml(yaml));
    }

    [Fact]
    public void LoadYaml_PendingOriginal_RejectedForNumericRules()
    {
        // pending-original 允许加载但引擎标记不可执行——加载层仅校验枚举合法性
        string yaml = ValidYaml.Replace("verification: source-text", "verification: pending-original");
        var rule = RuleLoader.LoadYaml(yaml);
        Assert.Equal("pending-original", rule.Source.Verification);
    }
}
```

- [ ] **Step 2: RED** — `dotnet test --filter FullyQualifiedName~RuleLoaderTests` 编译失败。
- [ ] **Step 3: 实现** — record + YamlDotNet Deserializer（CamelCaseNamingConvention，枚举小写映射：用 Enum.Parse ignoreCase；Verification 合法集 {source-text, external-corroborated, pending-original}）；LoadDirectory 枚举 *.yaml。
- [ ] **Step 4: GREEN** — 4 条 PASS。
- [ ] **Step 5: Commit** — `feat: rule model with source citation, force/severity, YAML loader + validation`

---

### Task 2: 裁定模型与持久化（Core）

**Files:**
- Create: `src/CadAgent.Core/Rules/Adjudication.cs`
- Test: `tests/CadAgent.Core.Tests/AdjudicationTests.cs`

**Interfaces:**
- Produces:
  - `sealed record Adjudication(string RuleId, string Operator, DateTimeOffset Ts, string Reason, RuleForce? ForceOverride = null, bool Exempt = false, string Interpretation = "")`
  - `sealed class AdjudicationSet`：`List<Adjudication> Items`；`static Load(path)`（缺失→空）；`Save(path)`（**原子写 tmp+Move，沿用 SemanticOverlay 模式**）；`Upsert(Adjudication a)`（同 RuleId 替换——最新裁定生效）；`EffectiveForce(Rule rule)`→（Exempt→跳过信号；ForceOverride??rule.Force）；`IsExempt(ruleId)`
  - 审计 kind 常量：`rule-adjudicated`（Cli 用）

- [ ] **Step 1: 写失败测试**

```csharp
namespace CadAgent.Core.Tests;

public class AdjudicationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"adj-{Guid.NewGuid():N}.json");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); if (File.Exists(_path + ".tmp")) File.Delete(_path + ".tmp"); }

    private static Rule MakeRule(RuleForce f) =>
        new("R1", new RuleSource("GB/T 50001-2017", "5.0.2", "excerpt", "source-text"),
            "应", f, RuleSeverity.High, new RuleScope(), new CheckSpec("t", new()), "fix");

    [Fact]
    public void Load_Missing_Empty()
    {
        var s = AdjudicationSet.Load(_path);
        Assert.Empty(s.Items);
    }

    [Fact]
    public void Upsert_SameRule_Replaces_LatestWins()
    {
        var s = new AdjudicationSet();
        s.Upsert(new Adjudication("R1", "u1", DateTimeOffset.UnixEpoch, "r1", RuleForce.Mandatory));
        s.Upsert(new Adjudication("R1", "u2", DateTimeOffset.UnixEpoch, "r2", RuleForce.Informational));
        Assert.Single(s.Items);
        Assert.Equal(RuleForce.Informational, s.EffectiveForce(MakeRule(RuleForce.Recommended)));
    }

    [Fact]
    public void EffectiveForce_NoAdjudication_RuleDefault()
    {
        var s = new AdjudicationSet();
        Assert.Equal(RuleForce.Recommended, s.EffectiveForce(MakeRule(RuleForce.Recommended)));
    }

    [Fact]
    public void Exempt_Signal()
    {
        var s = new AdjudicationSet();
        s.Upsert(new Adjudication("R1", "u", DateTimeOffset.UnixEpoch, "本图类不适用", null, true));
        Assert.True(s.IsExempt("R1"));
    }

    [Fact]
    public void Save_Atomic_RoundTrips()
    {
        var s = new AdjudicationSet();
        s.Upsert(new Adjudication("R1", "u", DateTimeOffset.UnixEpoch, "理由", RuleForce.Mandatory, false, "解读"));
        s.Save(_path);
        var loaded = AdjudicationSet.Load(_path);
        Assert.Single(loaded.Items);
        Assert.Equal("解读", loaded.Items[0].Interpretation);
        Assert.False(File.Exists(_path + ".tmp"));
    }
}
```

- [ ] **Step 2: RED** — 编译失败。
- [ ] **Step 3: 实现** — JSON 序列化（JsonStringEnumConverter，可空 ForceOverride）；原子写 tmp+File.Move(overwrite:true)。
- [ ] **Step 4: GREEN** — 5 条 PASS。
- [ ] **Step 5: Commit** — `feat: adjudication model with force override, exemption, atomic persistence`

---

### Task 3: 检查器接口与引擎组合（Core）

**Files:**
- Create: `src/CadAgent.Core/Rules/Issue.cs`、`src/CadAgent.Core/Rules/IRuleChecker.cs`
- Test: `tests/CadAgent.Core.Tests/RuleEngineTests.cs`

**Interfaces:**
- Consumes: Rule/AdjudicationSet（T1/T2）；SemanticOverlay（2a）
- Produces:
  - `sealed record Issue(string RuleId, string Clause, string Standard, string Excerpt, RuleForce Force, RuleSeverity Severity, IReadOnlyList<string> Targets, string Message, string Suggestion, string DetectionBasis)`
  - `sealed record CheckSummary(IReadOnlyList<Issue> Issues, int RulesRun, int RulesSkipped, IReadOnlyList<string> SkippedReasons)`
  - `interface IRuleChecker { string CheckType { get; } IReadOnlyList<Issue> Check(Rule rule, IDrawingSession session, SemanticOverlay overlay); }`（session 为 Core 接口；Data 检查器 cast 到 DrawingSession）
  - `sealed class RuleEngine`：ctor `(IEnumerable<IRuleChecker> checkers)`；`CheckSummary Evaluate(IReadOnlyList<Rule> rules, IDrawingSession session, SemanticOverlay overlay, AdjudicationSet adjudications)`——逐规则：Exempt→跳过计数；verification=pending-original→跳过计数（原因"待原件核验"）；无匹配 checker→跳过计数；Force=EffectiveForce；Informational 规则也评估但 issue.Force=Informational（展示用）；checker 异常→该规则跳过计数+原因（不崩引擎）；DetectionBasis 由 checker 填
  - 跳过原因去重汇总

- [ ] **Step 1: 写失败测试**

```csharp
namespace CadAgent.Core.Tests;

public class RuleEngineTests
{
    private sealed class FakeSession : IDrawingSession
    {
        public string SourcePath => "fake.dxf";
        public string ComputeHash() => "h";
        public void SaveWorking() { }
        public void Reload() { }
    }

    private sealed class FakeChecker : IRuleChecker
    {
        public string CheckType => "fake";
        public IReadOnlyList<Issue> Check(Rule r, IDrawingSession s, SemanticOverlay o) =>
            new[] { new Issue(r.Id, r.Source.Clause, r.Source.Standard, r.Source.Excerpt,
                r.Force, r.Severity, new[] { "h1" }, "问题", "建议", "fake检测") };
    }

    private sealed class ThrowingChecker : IRuleChecker
    {
        public string CheckType => "boom";
        public IReadOnlyList<Issue> Check(Rule r, IDrawingSession s, SemanticOverlay o) =>
            throw new InvalidOperationException("检查器爆炸");
    }

    private static Rule MakeRule(string id, string type, RuleForce f = RuleForce.Recommended, string verification = "source-text") =>
        new(id, new RuleSource("GB/T 50001-2017", "1.0.1", "摘录", verification),
            "应", f, RuleSeverity.High, new RuleScope(), new CheckSpec(type, new()), "建议");

    [Fact]
    public void Evaluate_ProducesIssues_WithEffectiveForce()
    {
        var engine = new RuleEngine(new[] { new FakeChecker() });
        var adj = new AdjudicationSet();
        adj.Upsert(new Adjudication("R1", "u", DateTimeOffset.UnixEpoch, "院标升级", RuleForce.Mandatory));
        var summary = engine.Evaluate(new[] { MakeRule("R1", "fake") }, new FakeSession(), new SemanticOverlay(), adj);
        Assert.Single(summary.Issues);
        Assert.Equal(RuleForce.Mandatory, summary.Issues[0].Force); // 裁定升级生效
        Assert.Equal(1, summary.RulesRun);
    }

    [Fact]
    public void Evaluate_Exempt_Skips()
    {
        var engine = new RuleEngine(new[] { new FakeChecker() });
        var adj = new AdjudicationSet();
        adj.Upsert(new Adjudication("R1", "u", DateTimeOffset.UnixEpoch, "不适用", null, true));
        var summary = engine.Evaluate(new[] { MakeRule("R1", "fake") }, new FakeSession(), new SemanticOverlay(), adj);
        Assert.Empty(summary.Issues);
        Assert.Equal(1, summary.RulesSkipped);
    }

    [Fact]
    public void Evaluate_PendingOriginal_Skips()
    {
        var engine = new RuleEngine(new[] { new FakeChecker() });
        var summary = engine.Evaluate(new[] { MakeRule("R1", "fake", verification: "pending-original") },
            new FakeSession(), new SemanticOverlay(), new AdjudicationSet());
        Assert.Empty(summary.Issues);
        Assert.Contains(summary.SkippedReasons, r => r.Contains("待原件核验"));
    }

    [Fact]
    public void Evaluate_CheckerThrows_SkipsNotCrash()
    {
        var engine = new RuleEngine(new[] { new ThrowingChecker() });
        var summary = engine.Evaluate(new[] { MakeRule("R1", "boom") }, new FakeSession(), new SemanticOverlay(), new AdjudicationSet());
        Assert.Empty(summary.Issues);
        Assert.Equal(1, summary.RulesSkipped);
        Assert.Contains(summary.SkippedReasons, r => r.Contains("检查器爆炸"));
    }

    [Fact]
    public void Evaluate_NoChecker_Skips()
    {
        var engine = new RuleEngine(Array.Empty<IRuleChecker>());
        var summary = engine.Evaluate(new[] { MakeRule("R1", "unknown-type") }, new FakeSession(), new SemanticOverlay(), new AdjudicationSet());
        Assert.Equal(1, summary.RulesSkipped);
    }
}
```

- [ ] **Step 2: RED** — 编译失败。
- [ ] **Step 3: 实现** — 按契约；issue.Force 用 EffectiveForce 替换 rule.Force 后传给 checker？**裁定：checker 收到 rule 原样，引擎在收到 issue 后覆写 Force=EffectiveForce**（checker 不感知裁定）。
- [ ] **Step 4: GREEN** — 5 条 PASS。
- [ ] **Step 5: Commit** — `feat: rule engine composition with adjudication application, skip accounting, checker isolation`

---

### Task 4: 字高与线宽检查器（Data）

**Files:**
- Create: `src/CadAgent.Data/Checks/TextHeightChecker.cs`、`src/CadAgent.Data/Checks/LineweightChecker.cs`
- Test: `tests/CadAgent.Data.Tests/TextLineweightCheckerTests.cs`

**Interfaces:**
- Consumes: IRuleChecker/Rule/Issue（T3）、DrawingSession、SemanticOverlay
- Produces:
  - `TextHeightChecker : IRuleChecker`，CheckType="text-height-series"——params: hanVector/other（double 列表）；遍历 TextEntity（**MText : Entity 独立处理，两者都有 Height**）；高度∈两系列并集（容差 0.01mm）→通过；否则 issue（Targets=[handle]，Message 含实际字高与两套系列，DetectionBasis="实体字高直接比对；未检出图比例，出图换算见 14.0.4（计划3）"）；**字体归属细化留后续**（第一 slice 并集判定，消息注明）
  - `LineweightChecker : IRuleChecker`，CheckType="lineweight-group"——params: groups（四组 double 列表）；实体 Lineweight（**ACadSharp 属性名/单位以 XML 文档核实**：Lineweight 枚举或 mm 值；ByLayer/Default→跳过该实体不报）；值∈并集（容差 0.01）→通过；否则 issue（推荐级，消息含线宽组依据 4.0.1）

- [ ] **Step 1: 写失败测试**

```csharp
using CadAgent.Core;
using CSMath;
using ACadSharp.Entities;

namespace CadAgent.Data.Tests;

public class TextLineweightCheckerTests : IDisposable
{
    private readonly string _path;
    public TextLineweightCheckerTests() => _path = DxfFixture.CreateTempDxf(doc =>
    {
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "合规", Height = 5 });
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "违规", Height = 4.2 });
        doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(100, 0) }); // 默认线宽 ByLayer→跳过
    });
    public void Dispose() => File.Delete(_path);

    private static Rule HeightRule() => new("R-50001-5.0.2",
        new RuleSource("GB/T 50001-2017", "5.0.2", "文字的字高，应从表 5.0.2 中选用。", "source-text"),
        "应／宜", RuleForce.Recommended, RuleSeverity.High, new RuleScope(),
        new CheckSpec("text-height-series", new Dictionary<string, object>
        {
            ["hanVector"] = new List<object> { 3.5, 5, 7, 10, 14, 20 },
            ["other"] = new List<object> { 3, 4, 6, 8, 10, 14, 20 }
        }), "字高改选系列值");

    [Fact]
    public void TextHeight_OffSeries_Flagged_InSeries_Pass()
    {
        var session = DrawingSession.Load(_path);
        var issues = new TextHeightChecker().Check(HeightRule(), session, new SemanticOverlay());
        Assert.Single(issues);
        Assert.Contains("4.2", issues[0].Message);
        Assert.Contains("14.0.4", issues[0].DetectionBasis);
    }

    [Fact]
    public void Lineweight_ByLayer_Skipped_NoIssue()
    {
        var rule = new Rule("R-50001-4.0.1",
            new RuleSource("GB/T 50001-2017", "4.0.1", "图线的基本线宽 b……", "source-text"),
            "宜／应", RuleForce.Recommended, RuleSeverity.High, new RuleScope(),
            new CheckSpec("lineweight-group", new Dictionary<string, object>
            {
                ["groups"] = new List<object> { 1.4, 1.0, 0.7, 0.5, 0.35, 0.25, 0.18, 0.13 }
            }), "线宽改选线宽组值");
        var session = DrawingSession.Load(_path);
        var issues = new LineweightChecker().Check(rule, session, new SemanticOverlay());
        Assert.Empty(issues); // ByLayer 不报
    }
}
```
（ACadSharp Lineweight API 核实后若需构造显式线宽实体补一条违规测试；ByLayer 跳过行为必须钉死。）

- [ ] **Step 2: RED** — 编译失败。
- [ ] **Step 3: 实现** — 先查 XML 文档确认 Lineweight 属性与单位；params 数值转换辅助（object→double，List<object>→double[]）。
- [ ] **Step 4: GREEN** — PASS。
- [ ] **Step 5: Commit** — `feat: text-height-series and lineweight-group checkers (GB/T 50001 5.0.2/4.0.1)`

---

### Task 5: 轴号与标高检查器（Data，overlay 驱动）

**Files:**
- Create: `src/CadAgent.Data/Checks/AxisLetterChecker.cs`、`src/CadAgent.Data/Checks/ElevationChecker.cs`
- Test: `tests/CadAgent.Data.Tests/AxisElevationCheckerTests.cs`

**Interfaces:**
- Produces:
  - `AxisLetterChecker`，CheckType="axis-letter"——overlay 中 SemanticClass.Axis 且 Confidence≥0.6 的标注句柄→取 TextEntity 值；值须为：单大写字母（排除 I/O/Z）或双大写字母或字母+数字注脚（8.0.4 溢出条款）；小写/含 I/O/Z→issue；DetectionBasis="语义叠加层 Axis 标注（来源:{provenance}）+ 文本值"；**overlay 无 Axis 标注→返回空（引擎层不计数跳过——checker 返回空列表即可，跳过计数仅针对规则级）**
  - `ElevationChecker`，CheckType="elevation-format"——候选：overlay Annotation 类标注句柄的 TextEntity + 值匹配 `^±?\d+\.\d{2,3}$` 或 `^[+-]?\d+\.\d{2,3}$`；检查：小数位=3（params allowSitePlan2=true 时 2 位仅警告级消息注明总平面豁免需人工确认——第一 slice：2 位→issue severity 降为 Low 消息注"总平面图可 2 位（11.8.4 可条款），请确认图类"）；零点须 ±0.000；正数无 +；负数带 -；DetectionBasis 注明模式+overlay 来源

- [ ] **Step 1: 写失败测试**

```csharp
using CadAgent.Core;
using CSMath;
using ACadSharp.Entities;

namespace CadAgent.Data.Tests;

public class AxisElevationCheckerTests : IDisposable
{
    private readonly string _path;
    private readonly SemanticOverlay _overlay = new();
    public AxisElevationCheckerTests()
    {
        _path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "I", Height = 5 });   // 违规轴号
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "A", Height = 5 });  // 合规轴号
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(20, 0), Value = "+3.000", Height = 5 }); // 正数带+违规
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(30, 0), Value = "-0.600", Height = 5 }); // 合规
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(40, 0), Value = "3.00", Height = 5 });   // 2位小数
        });
        var session = DrawingSession.Load(_path);
        var es = session.Summarize().Entities;
        _overlay.UpsertAnnotation(new SemanticAnnotation(es[0].Handle, SemanticClass.Axis, Provenance.HumanConfirmed, 1.0, "t"));
        _overlay.UpsertAnnotation(new SemanticAnnotation(es[1].Handle, SemanticClass.Axis, Provenance.HumanConfirmed, 1.0, "t"));
        _overlay.UpsertAnnotation(new SemanticAnnotation(es[2].Handle, SemanticClass.Annotation, Provenance.HumanConfirmed, 1.0, "t"));
        _overlay.UpsertAnnotation(new SemanticAnnotation(es[3].Handle, SemanticClass.Annotation, Provenance.HumanConfirmed, 1.0, "t"));
        _overlay.UpsertAnnotation(new SemanticAnnotation(es[4].Handle, SemanticClass.Annotation, Provenance.HumanConfirmed, 1.0, "t"));
    }
    public void Dispose() => File.Delete(_path);

    private static Rule Make(string id, string type, string clause) =>
        new(id, new RuleSource("GB/T 50001-2017", clause, "摘录", "source-text"),
            "应", RuleForce.Recommended, RuleSeverity.High, new RuleScope(),
            new CheckSpec(type, new Dictionary<string, object>()), "建议");

    [Fact]
    public void AxisLetter_IOZ_Flagged_ValidPass()
    {
        var issues = new AxisLetterChecker().Check(Make("R-50001-8.0.4", "axis-letter", "8.0.4"),
            DrawingSession.Load(_path), _overlay);
        Assert.Single(issues);
        Assert.Contains("I", issues[0].Message);
        Assert.Contains("HumanConfirmed", issues[0].DetectionBasis);
    }

    [Fact]
    public void Elevation_PlusSign_Flagged()
    {
        var issues = new ElevationChecker().Check(Make("R-50001-11.8.5", "elevation-format", "11.8.5"),
            DrawingSession.Load(_path), _overlay);
        Assert.Contains(issues, i => i.Message.Contains("+3.000"));
    }

    [Fact]
    public void Elevation_TwoDecimals_LowSeverityNote()
    {
        var issues = new ElevationChecker().Check(Make("R-50001-11.8.4", "elevation-format", "11.8.4"),
            DrawingSession.Load(_path), _overlay);
        var two = issues.Single(i => i.Message.Contains("3.00"));
        Assert.Equal(RuleSeverity.Low, two.Severity);
        Assert.Contains("总平面", two.Message);
    }

    [Fact]
    public void AxisLetter_NoOverlay_Empty()
    {
        var issues = new AxisLetterChecker().Check(Make("R-50001-8.0.4", "axis-letter", "8.0.4"),
            DrawingSession.Load(_path), new SemanticOverlay());
        Assert.Empty(issues);
    }
}
```

- [ ] **Step 2: RED** — 编译失败。
- [ ] **Step 3: 实现** — 按契约；标高正则注意 ±0.000 零点特例；issue.Severity 覆写规则（2 位小数→Low）。
- [ ] **Step 4: GREEN** — 4 条 PASS。
- [ ] **Step 5: Commit** — `feat: axis-letter and elevation-format checkers (overlay-driven, GB/T 50001 8.0.4/11.8)`

---

### Task 6: 图层命名检查器（Data）

**Files:**
- Create: `src/CadAgent.Data/Checks/LayerNameChecker.cs`
- Test: `tests/CadAgent.Data.Tests/LayerNameCheckerTests.cs`

**Interfaces:**
- Produces: `LayerNameChecker`，CheckType="layer-name"——params: mode（"charset"|"structure"）；charset（13.0.1）：层名同时含汉字与 A-Za-z→issue（不得混用）；structure（13.0.2）：按 "-" 分段，段数∈[2,5]，每段非空且字符集∈[汉字A-Za-z0-9]（**专业代码白名单/附录B推荐名 deferred——视觉依赖表，消息注明**）；图层 "0" 跳过（默认层）；DetectionBasis="图层名直接检查"

- [ ] **Step 1: 写失败测试**

```csharp
using CadAgent.Core;
using ACadSharp.Tables;

namespace CadAgent.Data.Tests;

public class LayerNameCheckerTests : IDisposable
{
    private readonly string _path;
    public LayerNameCheckerTests() => _path = DxfFixture.CreateTempDxf(doc =>
    {
        doc.Layers.Add(new Layer("W-墙体"));      // 汉英混用违规
        doc.Layers.Add(new Layer("W-WALL"));      // 合规英文
        doc.Layers.Add(new Layer("建筑-墙体-新建")); // 合规汉字3段
        doc.Layers.Add(new Layer("单层"));         // 结构违规：1段
    });
    public void Dispose() => File.Delete(_path);

    private static Rule Make(string mode) =>
        new($"R-50001-13.0.{(mode == "charset" ? "1" : "2")}",
            new RuleSource("GB/T 50001-2017", mode == "charset" ? "13.0.1" : "13.0.2", "摘录", "source-text"),
            "不得／宜", RuleForce.Recommended, RuleSeverity.High, new RuleScope(),
            new CheckSpec("layer-name", new Dictionary<string, object> { ["mode"] = mode }), "建议");

    [Fact]
    public void Charset_MixedHanEnglish_Flagged()
    {
        var issues = new LayerNameChecker().Check(Make("charset"), DrawingSession.Load(_path), new SemanticOverlay());
        Assert.Single(issues);
        Assert.Contains("W-墙体", issues[0].Message);
    }

    [Fact]
    public void Structure_SegmentCount_Flagged()
    {
        var issues = new LayerNameChecker().Check(Make("structure"), DrawingSession.Load(_path), new SemanticOverlay());
        Assert.Contains(issues, i => i.Message.Contains("单层"));
        Assert.DoesNotContain(issues, i => i.Message.Contains("W-WALL"));
        Assert.DoesNotContain(issues, i => i.Message.Contains("建筑-墙体-新建"));
    }
}
```

- [ ] **Step 2: RED** — 编译失败。
- [ ] **Step 3: 实现** — 汉字判定 `[\u4e00-\u9fff]`；结构模式对汉字层与英文层分别校验段字符集。
- [ ] **Step 4: GREEN** — 2 条 PASS。
- [ ] **Step 5: Commit** — `feat: layer-name charset/structure checkers (GB/T 50001 13.0.1/2)`

---

### Task 7: 内置规则数据 + CLI check/rule 命令（Cli）

**Files:**
- Create: `src/CadAgent.Data/Rules/builtin/` 7 个 YAML（数据见下）
- Modify: `src/CadAgent.Cli/CommandRouter.cs`（加性）、`src/CadAgent.Data/CadAgent.Data.csproj`（YAML 嵌入资源或内容复制）
- Test: `tests/CadAgent.Cli.Tests/CheckCommandTests.cs`

**Interfaces:**
- Consumes: T1-T6 全部 + 2a LoadOrReject/semantic overlay/审计模式
- Produces:
  - 内置规则 7 条（**逐字摘录自 extraction-50001.md，控制者已核**）：
    1. R-50001-5.0.2 text-height-series（应／宜，recommended/high，hanVector+other 参数）
    2. R-50001-4.0.1 lineweight-group（宜／应，recommended/high，groups 并集参数）
    3. R-50001-8.0.4 axis-letter（应／不应／不得，recommended/high）
    4. R-50001-11.8.4 elevation-format（应／可，recommended/high，allowSitePlan2=true）
    5. R-50001-11.8.5 elevation-format（应，recommended/high，signMode=true）——同 checker 两规则实例，params 区分 decimals/sign
    6. R-50001-13.0.1 layer-name charset（不得，recommended/high）
    7. R-50001-13.0.2 layer-name structure（宜／应，recommended/medium）
  - YAML 以嵌入资源（EmbeddedResource）随 Data 程序集分发；`BuiltinRules.Load()` 读取全部
  - `check --drawing <file> [--project dir]`：LoadOrReject 图纸→overlay 存在则 Load（损坏→audited refusal）→RuleEngine.Evaluate(builtin, session, overlay, adjudications)→审计 check-run（paramsJson={rulesRun, rulesSkipped, issueCount, mandatoryViolations}）→stdout JSON {issues, summary}（camelCase，沿用 SemanticJsonOpts 模式）→**退出码：0=无 mandatory 违规；2=有 mandatory 违规**（recommended/informational 不影响退出码——决策权归人）
  - `rule adjudicate --rule <id> (--force mandatory|recommended|informational | --exempt) --reason <text> [--interpretation <text>] [--project dir]`：Upsert+Save+审计 rule-adjudicated（paramsJson={ruleId, forceOverride, exempt, reason}）→exit 0；--force 与 --exempt 互斥（同给→exit 1）；reason 必填
  - `rule list [--project dir]`：内置规则+当前裁定 JSON→exit 0
  - ElevationChecker params 扩展：decimals/sign 模式开关（T5 实现时预留：无 params→全检查；allowSitePlan2/signMode 按规则实例）

- [ ] **Step 1: 写失败测试**

```csharp
using CadAgent.Data.Tests;
using CSMath;
using ACadSharp.Entities;

namespace CadAgent.Cli.Tests;

public class CheckCommandTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"chk-{Guid.NewGuid():N}");
    private readonly string _drawing;
    public CheckCommandTests()
    {
        Directory.CreateDirectory(_project);
        _drawing = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "字高违规", Height = 4.2 });
            doc.Layers.Add(new ACadSharp.Tables.Layer("W-墙体"));
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
    public void Check_RecommendedIssues_ExitZero_Audited()
    {
        var (code, stdout, _) = Run("check", "--drawing", _drawing);
        Assert.Equal(0, code); // 全 recommended，无 mandatory
        Assert.Contains("R-50001-5.0.2", stdout);
        Assert.Contains("R-50001-13.0.1", stdout);
        Assert.Contains("GB/T 50001-2017", stdout); // 引用义务
        var (vc, _, _) = Run("audit", "verify");
        Assert.Equal(0, vc);
    }

    [Fact]
    public void Adjudicate_UpgradeMandatory_ThenCheckExitTwo()
    {
        var (ac, _, _) = Run("rule", "adjudicate", "--rule", "R-50001-5.0.2",
            "--force", "mandatory", "--reason", "院标升级：字高强制");
        Assert.Equal(0, ac);
        var (code, stdout, _) = Run("check", "--drawing", _drawing);
        Assert.Equal(2, code);
        Assert.Contains("mandatory", stdout);
        var (vc, _, _) = Run("audit", "verify");
        Assert.Equal(0, vc);
    }

    [Fact]
    public void Adjudicate_Exempt_RemovesIssue()
    {
        Run("rule", "adjudicate", "--rule", "R-50001-13.0.1", "--exempt", "--reason", "本项目图层约定另定");
        var (_, stdout, _) = Run("check", "--drawing", _drawing);
        Assert.DoesNotContain("R-50001-13.0.1", stdout);
    }

    [Fact]
    public void Adjudicate_ForceAndExempt_Conflict_ExitOne()
    {
        var (code, _, stderr) = Run("rule", "adjudicate", "--rule", "R1",
            "--force", "mandatory", "--exempt", "--reason", "x");
        Assert.Equal(1, code);
        Assert.NotEmpty(stderr);
    }

    [Fact]
    public void Adjudicate_MissingReason_ExitOne()
    {
        var (code, _, _) = Run("rule", "adjudicate", "--rule", "R1", "--exempt");
        Assert.Equal(1, code);
    }

    [Fact]
    public void RuleList_ShowsBuiltinsAndAdjudications()
    {
        Run("rule", "adjudicate", "--rule", "R-50001-5.0.2", "--force", "mandatory", "--reason", "院标");
        var (code, stdout, _) = Run("rule", "list");
        Assert.Equal(0, code);
        Assert.Contains("R-50001-5.0.2", stdout);
        Assert.Contains("院标", stdout);
    }
}
```

- [ ] **Step 2: RED** — 编译失败/命令未识别。
- [ ] **Step 3: 实现** — YAML 数据逐字摘录（控制者提供下方数据块）；EmbeddedResource + BuiltinRules.Load；CLI 三命令；ElevationChecker params 模式开关。

**内置规则 YAML 数据（逐字自 extraction-50001.md，LaTeX 标记归一化）**：
```yaml
# R-50001-5.0.2.yaml
id: R-50001-5.0.2
source:
  standard: "GB/T 50001-2017"
  clause: "5.0.2"
  excerpt: "文字的字高，应从表 5.0.2 中选用。字高大于 10mm 的文字宜采用 True type 字体，如需书写更大的字，其高度应按 √2 的倍数递增。"
  verification: source-text
modal: "应／宜"
force: recommended
severity: high
scope: { discipline: any, drawingType: any }
check:
  type: text-height-series
  params:
    hanVector: [3.5, 5, 7, 10, 14, 20]
    other: [3, 4, 6, 8, 10, 14, 20]
suggestion: "字高改选表 5.0.2 系列值（汉字矢量 3.5/5/7/10/14/20；TrueType 及非汉字矢量 3/4/6/8/10/14/20 mm）；出图比例换算见 14.0.4"
```
```yaml
# R-50001-4.0.1.yaml
id: R-50001-4.0.1
source:
  standard: "GB/T 50001-2017"
  clause: "4.0.1"
  excerpt: "图线的基本线宽 b，宜按照图纸比例及图纸性质从 1.4mm、1.0mm、0.7mm、0.5mm 线宽系列中选取。每个图样，应根据复杂程度与比例大小，先选定基本线宽 b，再选用表 4.0.1 中相应的线宽组。"
  verification: source-text
modal: "宜／应"
force: recommended
severity: high
scope: { discipline: any, drawingType: any }
check:
  type: lineweight-group
  params:
    groups: [1.4, 1.0, 0.7, 0.5, 0.35, 0.25, 0.18, 0.13]
suggestion: "线宽改选所选线宽组 {b, 0.7b, 0.5b, 0.25b} 的值"
```
```yaml
# R-50001-8.0.4.yaml
id: R-50001-8.0.4
source:
  standard: "GB/T 50001-2017"
  clause: "8.0.4"
  excerpt: "英文字母作为轴线号时，应全部采用大写字母，不应用同一个字母的大小写来区分轴线号。英文字母的 I、O、Z 不得用作轴线编号。当字母数量不够使用时，可增用双字母或单字母加数字注脚。"
  verification: source-text
modal: "应／不应／不得／可"
force: recommended
severity: high
scope: { discipline: any, drawingType: any }
check:
  type: axis-letter
  params: {}
suggestion: "轴号改用大写 A～Y（排除 I/O/Z）；溢出用双字母或字母+数字注脚"
```
```yaml
# R-50001-11.8.4.yaml
id: R-50001-11.8.4
source:
  standard: "GB/T 50001-2017"
  clause: "11.8.4"
  excerpt: "标高数字应以米为单位，注写到小数点以后第三位。在总平面图中，可注写到小数点以后第二位。"
  verification: source-text
modal: "应／可"
force: recommended
severity: high
scope: { discipline: any, drawingType: any }
check:
  type: elevation-format
  params: { mode: decimals, allowSitePlan2: true }
suggestion: "标高注写到小数点后 3 位（总平面图可 2 位，需确认图类）"
```
```yaml
# R-50001-11.8.5.yaml
id: R-50001-11.8.5
source:
  standard: "GB/T 50001-2017"
  clause: "11.8.5"
  excerpt: "零点标高应注写成±0.000，正数标高不注'+'，负数标高应注'-'，例如3.000、-0.600。"
  verification: source-text
modal: "应"
force: recommended
severity: high
scope: { discipline: any, drawingType: any }
check:
  type: elevation-format
  params: { mode: sign }
suggestion: "零点 ±0.000；正数去 +；负数带 -"
```
```yaml
# R-50001-13.0.1.yaml
id: R-50001-13.0.1
source:
  standard: "GB/T 50001-2017"
  clause: "13.0.1"
  excerpt: "图层名称宜使用汉字、英文字母、数字和连字符'-'的组合，但汉字与英文字母不得混用；在同一工程中，应使用统一的图层命名格式，图层名称应自始至终保持不变，且不应同时使用汉字和英文字母的命名格式。"
  verification: source-text
modal: "宜／不得／应／不应"
force: recommended
severity: high
scope: { discipline: any, drawingType: any }
check:
  type: layer-name
  params: { mode: charset }
suggestion: "图层名汉字与英文字母不得混用；项目内统一一套命名格式"
```
```yaml
# R-50001-13.0.2.yaml
id: R-50001-13.0.2
source:
  standard: "GB/T 50001-2017"
  clause: "13.0.2"
  excerpt: "图层命名应采用分级形式，每个图层名称宜由 2～5 个数据字段（代码）组成……每个相邻的数据字段应用连字符'-'分隔开。"
  verification: source-text
modal: "应／宜／不能"
force: recommended
severity: medium
scope: { discipline: any, drawingType: any }
check:
  type: layer-name
  params: { mode: structure }
suggestion: "图层名 2～5 级 '-' 分隔；专业代码白名单/附录B推荐名待视觉核验后启用"
```

- [ ] **Step 4: GREEN** — 6 条 PASS + 全套件绿。
- [ ] **Step 5: Commit** — `feat: 7 verified GB/T 50001 builtin rules + CLI check/rule adjudicate/list with audit`

---

### Task 8: 端到端 + 回归检查 + 承接记录

**Files:**
- Test: `tests/CadAgent.Cli.Tests/CheckE2ETests.cs`
- Modify: `src/CadAgent.Cli/CommandRouter.cs`（run 成功后可选回归检查）、spec §12

**Interfaces:**
- Produces:
  - run 命令加性：tx 成功后若 builtin 规则可执行→Evaluate→新 mandatory 违规→stderr 警告（**不自动回滚——决策权归人**）；审计 check-run（Source="run-regression"）
  - E2E：构造违规图（字高 4.2 + 混用层 + 轴号 I + 标高+3.000，overlay 人工确认轴号/标高语义）→check exit 0（recommended）→adjudicate 升级字高规则→check exit 2→豁免混用层→issue 消失→run 几何计划成功+回归警告路径→audit verify 绿→裁定 JSON 持久化复用（重开 check 仍生效）
  - spec §12 追加 2b-i 完成记录：效力映射裁定（GB/T 默认 recommended，升级权归院标/裁定）；7 条规则来源与核验状态；检测启发式边界（overlay 依赖、比例换算留计划3、字体归属细化留后续）；pending-original 数值未入库；电气/模型规则包+skill 机制=2b-ii；MinerU API 录入=后续能力

- [ ] **Step 1: 写 E2E 测试**（完整代码，按上述断言；fixture 用 DxfFixture + overlay 直接写入 .cadagent/semantic.json 或走 semantic scan+map 命令链——采用后者更真实：scan→map 轴号层/标高标注层→check）
- [ ] **Step 2: 运行**（红则修）
- [ ] **Step 3: 全套件** `dotnet test` 全绿
- [ ] **Step 4: spec §12 追加**
- [ ] **Step 5: Commit** — `test: standards-check end-to-end with adjudication loop; docs: plan-2b-i carry-overs`

---

## 自审记录

- 规则数据 7 条逐字自 extraction-50001.md（控制者已 grep 核对原文行）；verification 全 source-text（条文）+身份 external-corroborated 注记在 spec。
- 效力映射：必须/严禁→mandatory（本批无）；应/不得→recommended/high；宜→recommended/medium——默认零拦截，升级走裁定/skill，符合用户软规则关切。
- 契约一致性：Rule/Issue/CheckSpec/Adjudication 跨 T1-T8 形状一致；IRuleChecker.Check 签名一致；ElevationChecker params 模式（decimals/sign）T5 实现 T7 数据消费——T5 测试需覆盖两模式（Step 1 测试含 +3.000 sign 与 3.00 decimals，实现按 params 分流，无 params 全检查）。
- 116 既有测试保护；StepEngine 不改（回归在 CLI 组合）；Core 厂商无关。
- 诚实边界：比例换算（14.0.4）/字体归属/专业代码白名单/附录B/pending-original 数值全部显式 deferred，不假装已检。