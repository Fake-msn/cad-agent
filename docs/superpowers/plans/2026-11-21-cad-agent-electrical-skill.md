# CAD 智能体终端 · 电气规则包与步骤模板 实现计划（计划 2b-ii-b）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** CommandRouter skill 族提取；电气/模型规则数据包（5 个新检查器，全部 TEXT 可核实条款）；步骤模板 schema + skill run；符号库元数据 + insert-block 处理器；遗留小项批。

**Architecture:** Cli partial class 提取（行为不变）；Data 新检查器（消费 2b-i IRuleChecker）；Core SkillPackage 加性 Steps/Symbols；Cli skill run 经 StepEngine（审计+语义闸口既有管线）。跨文档符号导入留计划 3。

**Tech Stack:** .NET 8、ACadSharp 3.8.35、YamlDotNet、System.Text.Json、xUnit。

**Spec:** `docs/superpowers/specs/2026-11-20-cad-agent-terminal-design.md` §6.3/§6.4、§4.1④⑤；承接 2b-ii-a 审查 triage。
**规则数据来源:** `standards-src/extraction-electrical.md`（D7/D8/D9/H3/F4 行）+ `extraction-model.md`（R1）——逐字摘录，符号本体/分数排版 VISUAL 项全部 defer。

## Global Constraints

- Core 不引用 ACadSharp/厂商；Data 引用 ACadSharp 3.8.35（CSMath/XYZ；MText : Entity；LineWeight 哨兵先跳过）。
- 规则数据逐字自 extraction 报告；情态冲突在案（3.4.3 应 vs 4.2.1 宜——modal 字段双录，force 按 GB/T 映射 recommended）；标准身份 GB/T 50786-2012 / JGJ/T 448-2018 均 external-corroborated 待原件确认（source.verification 注记）。
- 新检查器 DetectionBasis 必注检测启发式与边界；候选检测保守（宁漏勿滥——recommended 级误报也污染问题清单）。
- 步骤模板：声明式 {kind, params}，{{placeholder}} 替换来自 --args JSON；替换后过 StepPlanParser 同一 schema；skill run 走 StepEngine 既有审计/快照/闸口管线，**不另辟执行旁路**。
- insert-block 第一 slice：仅引用**图纸内已有块**（BlockRecord 存在→Insert 引用）；跨文档符号导入（symbols/*.dxf 克隆）留计划 3（COM 适配器可用时保真）；symbols.yaml 元数据先落地。
- 提取重构行为逐字节不变（245 测试守卫）；新文件 EOF 换行字节核验。
- 一切新操作入审计（skill-run 复用 tx 审计 + skill-run 请求记录）；load 失败入审计。
- TDD；pristine；feat:/fix:/refactor: 前缀；git 署名环境变量 Fake-msn <Fake-msn@users.noreply.github.com>（不改 config）。
- CLI UTF-8；Windows GBK 不回显中文；源文件 UTF-8。

## 文件结构

```
src/CadAgent.Cli/
  CommandRouter.cs           partial（主路由+check/scan/run/intake/audit/roles）
  CommandRouter.Skills.cs    partial（skill list/validate/promote + LoadRuleSet/GetOptionValues/skip helpers）
src/CadAgent.Core/
  Skills/SkillPackage.cs     加性：Steps/Symbols 属性 + 加载
  Skills/StepTemplate.cs     StepTemplate/TemplateStep + 替换器
  Skills/SymbolMeta.cs       SymbolMeta 记录
src/CadAgent.Data/
  Checks/TextHeightMinChecker.cs    text-height-min（50786 5.1.3）
  Checks/CableFormatChecker.cs      cable-format（4.1.5 序号8）
  Checks/SlotCodeChecker.cs         slot-code-whitelist（3.4.3/4.2.1 三表）
  Checks/LuminaireFormatChecker.cs  luminaire-format（4.1.5 序号5，保守）
  Checks/FileNamingChecker.cs       file-naming（448 5.4.3）
  Handlers/InsertBlockHandler.cs    insert-block（图纸内块引用）
  Rules/builtin/...          不动（治理测试钉死内容哈希）
examples/skills/electrical/  电气规则包（5规则+aliases）
examples/skills/model/       模型规则包（1规则）
examples/skills/sample-institute/  加 steps/ + symbols/ 示例
src/CadAgent.Cli/CommandRouter.Skills.cs  skill run 命令
tests/...
```

---

### Task 1: CommandRouter skill 族提取（refactor，行为不变）

**Files:**
- Modify: `src/CadAgent.Cli/CommandRouter.cs`（改 partial，移出 skill 族）
- Create: `src/CadAgent.Cli/CommandRouter.Skills.cs`（partial）

**Interfaces:**
- Produces: `static partial class CommandRouter` 两文件；移出：SkillList/SkillValidate/SemanticPromote/LoadRuleSet/GetOptionValues/WriteStructuredSkips/StructuredSkipAuditArray/ManifestVersionLine/AtomicWriteAllText/EnsureTrailingNewline（skill 族专用 helper）；主文件保留路由 switch 与 check/scan/run/intake/audit/roles/map/show/adjudicate/rule-list。共享 helper（OpenAudit/Identity/LoadOrReject/JsonOpts/SemanticJsonOpts）留主文件，partial 同类可见。
- **245 测试逐字节行为守卫**——纯移动，零逻辑改动。

- [ ] **Step 1: 确认基线** — `dotnet test` 245 绿（记录）。
- [ ] **Step 2: 提取** — class 声明改 `static partial class CommandRouter`（两文件）；移动方法/常量/正则；using 按需分配；路由 switch 不动。
- [ ] **Step 3: 全套件** — `dotnet test` 245 绿，零测试修改。
- [ ] **Step 4: Commit** — `refactor: extract skill command family into CommandRouter.Skills.cs partial (behavior identical)`

---

### Task 2: 遗留小项批（Core/Cli）

**Files:**
- Modify: `src/CadAgent.Core/Skills/SkillPackage.cs`（数字串 class 拒绝 + 包内重复 layerName 拒绝）、`src/CadAgent.Core/Rules/RuleLoader.cs`（inner exception）、`src/CadAgent.Core/Identity.cs`（CA1416）、`src/CadAgent.Cli/CommandRouter.Skills.cs`（dangling --skill 警告）
- Create: `tests/CadAgent.Core.Tests/BuiltinGovernanceTests.cs`
- Test: 既有测试文件加性

**Interfaces:**
- aliases class 全数字串（"3"）→InvalidSkillException（strict  parity）；包内 layerName OrdinalIgnoreCase 重复→InvalidSkillException 列名。
- RuleLoader.LoadYaml 的 YamlException wrap 传 inner（InvalidRuleException 加 (message, inner) ctor）。
- CA1416：`[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]`（Core csproj 生成 AssemblyInfo 或 AssemblyInfo.cs）——产品 Windows 目标（spec），消除警告而非压制。
- GetOptionValues：dangling 尾随 --skill（无值）→stderr 警告"忽略无值选项 --skill"（不 exit 1，宽容但可见）。
- **builtin 治理测试**：计算 BuiltinRules 全部 YAML 内容（ordinal 序拼接）SHA256，断言等于测试内钉死常量；内置规则内容变更→测试红→强制有意识升 revision（RulePackVersion 常量 "gb50001-2017.r1" 格式不变，既有测试守卫）。

- [ ] **Step 1: 写失败测试**（数字串/重复层名/inner/dangling 各一条 + 治理测试先计算当前哈希钉死）
- [ ] **Step 2: RED**
- [ ] **Step 3: 实现**
- [ ] **Step 4: GREEN + 全套件**（245+新）
- [ ] **Step 5: Commit** — `fix: loader strictness batch, inner exceptions, CA1416 platform attribute, builtin governance test`

---

### Task 3: text-height-min 检查器（Data，50786 5.1.3）

**Files:**
- Create: `src/CadAgent.Data/Checks/TextHeightMinChecker.cs`
- Test: `tests/CadAgent.Data.Tests/ElectricalCheckerTests.cs`（本任务起共享）

**Interfaces:**
- CheckType="text-height-min"；params: minLatin=2.5, minHan=3.5（mm）；TextEntity/MText 值含拉丁字母/数字（无汉字）→Height≥minLatin（不应，severity high）；含汉字→Height≥minHan（不宜，severity medium——issue.Severity 覆写）；混合文本按汉字判（较严）；DetectionBasis="实体字高直接比对；出图比例换算留计划3；主导专业用房 3.0mm 变体需图类判定（deferred）"。
- 规则 YAML（electrical 包，T6 入库）excerpt 逐字："图样中本专业的汉字标注字高不宜小于 3.5mm，主导专业工艺、功能用房的汉字标注字高不宜小于 3.0mm，字母或数字标注字高不应小于 2.5mm"。

- [ ] **Step 1: 写失败测试**

```csharp
using CadAgent.Core;
using CSMath;
using ACadSharp.Entities;

namespace CadAgent.Data.Tests;

public class ElectricalCheckerTests : IDisposable
{
    protected readonly string Path_;
    protected SemanticOverlay Overlay = new();
    public ElectricalCheckerTests() => Path_ = DxfFixture.CreateTempDxf(_ => { });
    public void Dispose() => File.Delete(Path_);

    protected static Rule R(string id, string type, Dictionary<string, object>? p = null) =>
        new(id, new RuleSource("GB/T 50786-2012", "5.1.3", "摘录", "source-text"),
            "不应／不宜", RuleForce.Recommended, RuleSeverity.High, new RuleScope(),
            new CheckSpec(type, p ?? new()), "建议");

    [Fact]
    public void TextHeightMin_LatinBelow25_Flagged_HanBelow35_Medium()
    {
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "WL1", Height = 2.0 });  // 拉丁违规
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "照明", Height = 3.0 }); // 汉字违规 medium
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(20, 0), Value = "WL2", Height = 2.5 });  // 合规
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(30, 0), Value = "说明", Height = 3.5 });  // 合规
        });
        try
        {
            var issues = new TextHeightMinChecker().Check(
                R("R-50786-5.1.3", "text-height-min", new() { ["minLatin"] = 2.5, ["minHan"] = 3.5 }),
                DrawingSession.Load(path), new SemanticOverlay());
            Assert.Equal(2, issues.Count);
            Assert.Contains(issues, i => i.Message.Contains("WL1") && i.Severity == RuleSeverity.High);
            Assert.Contains(issues, i => i.Message.Contains("照明") && i.Severity == RuleSeverity.Medium);
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: RED** — 编译失败。
- [ ] **Step 3: 实现** — 汉字判定 [\u4e00-\u9fff]；容差 epsilon 沿用 CheckParams；消息引号包裹值。
- [ ] **Step 4: GREEN**。
- [ ] **Step 5: Commit** — `feat: text-height-min checker (GB/T 50786 5.1.3)`

---

### Task 4: cable-format + slot-code 检查器（Data）

**Files:**
- Create: `src/CadAgent.Data/Checks/CableFormatChecker.cs`、`src/CadAgent.Data/Checks/SlotCodeChecker.cs`
- Test: `tests/CadAgent.Data.Tests/CableSlotCheckerTests.cs`

**Interfaces:**
- **CableFormatChecker** CheckType="cable-format"——候选：TextEntity/MText 值匹配 `^[A-Z]{2,4}-` 且含 `×`（缆线型号+规格特征，保守）；合规简写形（表4.1.5 序号8 的 N/PE 省略实例，如 BV-3×2.5-PC20-WC）：`^[A-Z]{2,4}-\d+(\.\d+)?×\d+(\.\d+)?(-[A-Z]{1,3}\d+)?(-[A-Z]{1,3})?$`；完整形 `a b-c(d×e+f×g)i-jh` 含空格/括号/加号——第一 slice 仅校验简写形候选，完整形候选（含空格或+）→不判（DetectionBasis 注"完整形校验留后续"）；不合规简写→issue（recommended，消息引格式 `a b-c(d×e+f×g)i-jh` 与简写实例）；DetectionBasis="文本模式候选（型号前缀+×），简写形正则；非缆线文本可能漏检"。
- **SlotCodeChecker** CheckType="slot-code-whitelist"——params: layingMethods/layingPositions/luminaireMounts（三白名单，数据逐字自 D9）；候选：缆线简写形文本的尾段代码（-PC20 的 PC、-WC 的 WC）与灯具标注尾码；尾码∈对应白名单→合规；不在→issue（消息列白名单依据表4.2.1-1/-2/-3）；**白名单数据**：
  - layingMethods: SC,MT,CP,PC,FPC,KPC,CT,CL,MR,PR,M,DB,TC,CE
  - layingPositions: AB,AC,CE,SCE,WS,RS,CC,BC,CLC,WC,FC
  - luminaireMounts: SW,CS,DS,W,C,R,CR,WR,S,CL,HM
  - 槽位判定：缆线形倒数第一段=敷设部位（∈positions）、倒数第二段去数字=敷设方式（∈methods）；CE/CL 一码多义（敷设方式 CE=电缆排管 vs 部位 CE=沿吊顶；CL=梯架 vs 柱上）——**按槽位消歧**（D9 注记），同码在不同槽位均合法。
- 规则 modal 双录："应（3.4.3）／宜（4.2.1）——情态冲突在案，按较严应执行"；force recommended（GB/T 映射）。

- [ ] **Step 1: 写失败测试**

```csharp
using CadAgent.Core;
using CSMath;
using ACadSharp.Entities;

namespace CadAgent.Data.Tests;

public class CableSlotCheckerTests : IDisposable
{
    private readonly string _path;
    public CableSlotCheckerTests() => _path = DxfFixture.CreateTempDxf(doc =>
    {
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "BV-3×2.5-PC20-WC", Height = 3.5 });   // 合规简写
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "BV-3×2.5-XX20-WC", Height = 3.5 });  // 敷设方式 XX 非法
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(20, 0), Value = "BV-3×2.5-PC20-YY", Height = 3.5 });  // 部位 YY 非法
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(30, 0), Value = "BV3×2.5", Height = 3.5 });            // 格式违规（缺-）
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(40, 0), Value = "普通文字", Height = 3.5 });            // 非候选
    });
    public void Dispose() => File.Delete(_path);

    private static Rule R(string type, Dictionary<string, object>? p = null) =>
        new("R-T", new RuleSource("GB/T 50786-2012", "4.1.5", "摘录", "source-text"),
            "宜", RuleForce.Recommended, RuleSeverity.High, new RuleScope(),
            new CheckSpec(type, p ?? new()), "建议");

    private static Dictionary<string, object> Whitelists() => new()
    {
        ["layingMethods"] = new List<object> { "SC","MT","CP","PC","FPC","KPC","CT","CL","MR","PR","M","DB","TC","CE" },
        ["layingPositions"] = new List<object> { "AB","AC","CE","SCE","WS","RS","CC","BC","CLC","WC","FC" },
        ["luminaireMounts"] = new List<object> { "SW","CS","DS","W","C","R","CR","WR","S","CL","HM" }
    };

    [Fact]
    public void CableFormat_ValidSimple_Pass_Invalid_Flagged()
    {
        var issues = new CableFormatChecker().Check(R("cable-format"), DrawingSession.Load(_path), new SemanticOverlay());
        Assert.Single(issues); // 仅 BV3×2.5（缺-）；XX/YY 是槽位问题归 SlotCode
        Assert.Contains("BV3×2.5", issues[0].Message);
    }

    [Fact]
    public void SlotCode_BadMethodAndPosition_Flagged_ValidPass()
    {
        var issues = new SlotCodeChecker().Check(R("slot-code-whitelist", Whitelists()), DrawingSession.Load(_path), new SemanticOverlay());
        Assert.Equal(2, issues.Count);
        Assert.Contains(issues, i => i.Message.Contains("XX"));
        Assert.Contains(issues, i => i.Message.Contains("YY"));
    }

    [Fact]
    public void SlotCode_CE_CL_Polysemy_BySlot()
    {
        string path = DxfFixture.CreateTempDxf(doc =>
        {
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "BV-3×2.5-CE20-CE", Height = 3.5 }); // 方式CE=排管 部位CE=吊顶 均合法
            doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "BV-3×2.5-CL20-CL", Height = 3.5 }); // 方式CL=梯架 部位CL? CL不在positions→部位违规
        });
        try
        {
            var issues = new SlotCodeChecker().Check(R("slot-code-whitelist", Whitelists()), DrawingSession.Load(path), new SemanticOverlay());
            Assert.Single(issues); // 仅第二条部位 CL
            Assert.Contains("CL", issues[0].Message);
        }
        finally { File.Delete(path); }
    }
}
```
（注意：CL 在 layingPositions 白名单？D9 部位 11 项无 CL——CLC 有。测试按白名单数据：第二条部位 CL 违规 ✓。CE 在两者都有 ✓。）

- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现** — 候选/简写正则/槽位拆分（按 '-' 分段，末段=部位，倒数第二段字母部分=方式）；白名单 params 转换 CheckParams。
- [ ] **Step 4: GREEN**。
- [ ] **Step 5: Commit** — `feat: cable-format + slot-code-whitelist checkers (GB/T 50786 4.1.5/3.4.3/4.2.1)`

---

### Task 5: luminaire-format + file-naming 检查器（Data）

**Files:**
- Create: `src/CadAgent.Data/Checks/LuminaireFormatChecker.cs`、`src/CadAgent.Data/Checks/FileNamingChecker.cs`
- Test: `tests/CadAgent.Data.Tests/LuminaireFileCheckerTests.cs`

**Interfaces:**
- **LuminaireFormatChecker** CheckType="luminaire-format"——候选（保守）：值匹配 `^\d+-[A-Za-z0-9]+\(\d+×\d+×[A-Za-z]+\)/`（数量-型号(光源数×容量×种类)/…）；合规全形 `a-b(c×d×L)/e f`：e=数值或 "—"（吸顶），f∈luminaireMounts 白名单（params）；e 非数值非"—"→issue；f 不在白名单→issue；候选不匹配全形（缺 /e f）→issue（消息引格式）；DetectionBasis="灯具标注模式候选；符号附近关联属视觉（deferred）；分数排版视觉项不检"。
- **FileNamingChecker** CheckType="file-naming"——检查 session.SourcePath 文件名（去扩展名）：`^[A-Za-z]+_[0-9]+_.+`（专业代码_图纸编号_图纸名称，448 5.4.3 宜）；描述字段可选 `_描述`；字段内词组 '-' 分隔（不强制校验内部）；不匹配→issue（Target=文件名，消息引命名格式）；专业代码白名单（GB/T 51301）未提供→结构校验 only（DetectionBasis 注）；**每图一次**（非逐实体）。

- [ ] **Step 1: 写失败测试**

```csharp
using CadAgent.Core;
using CSMath;
using ACadSharp.Entities;

namespace CadAgent.Data.Tests;

public class LuminaireFileCheckerTests : IDisposable
{
    private readonly string _path;
    public LuminaireFileCheckerTests() => _path = DxfFixture.CreateTempDxf(doc =>
    {
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "2-YG2(2×40×FL)/3.0 C", Height = 3.5 });  // 合规（吸顶C）
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "2-YG2(2×40×FL)/— C", Height = 3.5 }); // 合规（—吸顶）
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(20, 0), Value = "2-YG2(2×40×FL)/abc C", Height = 3.5 }); // e非法
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(30, 0), Value = "2-YG2(2×40×FL)/3.0 ZZ", Height = 3.5 }); // f非法
    });
    public void Dispose() => File.Delete(_path);

    private static Rule R(string type, Dictionary<string, object>? p = null) =>
        new("R-T", new RuleSource("GB/T 50786-2012", "4.1.5", "摘录", "source-text"),
            "宜", RuleForce.Recommended, RuleSeverity.Medium, new RuleScope(),
            new CheckSpec(type, p ?? new()), "建议");

    [Fact]
    public void Luminaire_BadHeightAndMount_Flagged()
    {
        var p = new Dictionary<string, object> { ["luminaireMounts"] = new List<object> { "SW","CS","DS","W","C","R","CR","WR","S","CL","HM" } };
        var issues = new LuminaireFormatChecker().Check(R("luminaire-format", p), DrawingSession.Load(_path), new SemanticOverlay());
        Assert.Equal(2, issues.Count);
        Assert.Contains(issues, i => i.Message.Contains("abc"));
        Assert.Contains(issues, i => i.Message.Contains("ZZ"));
    }

    [Fact]
    public void FileNaming_StructureCheck()
    {
        // 临时文件名不含 _ → 违规
        var issues = new FileNamingChecker().Check(
            new Rule("R-448-5.4.3", new RuleSource("JGJ/T 448-2018", "5.4.3", "摘录", "source-text"),
                "宜", RuleForce.Recommended, RuleSeverity.Medium, new RuleScope(),
                new CheckSpec("file-naming", new()), "建议"),
            DrawingSession.Load(_path), new SemanticOverlay());
        Assert.Single(issues);
        // 合规名
        string good = Path.Combine(Path.GetTempPath(), "A_001_平面图.dxf");
        File.Copy(_path, good);
        try
        {
            var ok = new FileNamingChecker().Check(
                new Rule("R-448-5.4.3", new RuleSource("JGJ/T 448-2018", "5.4.3", "摘录", "source-text"),
                    "宜", RuleForce.Recommended, RuleSeverity.Medium, new RuleScope(),
                    new CheckSpec("file-naming", new()), "建议"),
                DrawingSession.Load(good), new SemanticOverlay());
            Assert.Empty(ok);
        }
        finally { File.Delete(good); }
    }
}
```
（注意：.dwg 源 SourcePath=working sidecar 名含 .working.dxf——FileNamingChecker 用**原始源文件名**：DrawingSession 需暴露 OriginalPath？第一 slice 裁定：用 SourcePath 文件名去 ".working.dxf" 后缀还原（若含）；DetectionBasis 注记。）

- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现**。
- [ ] **Step 4: GREEN**。
- [ ] **Step 5: Commit** — `feat: luminaire-format + file-naming checkers (GB/T 50786 4.1.5 / JGJ/T 448 5.4.3)`

---

### Task 6: 电气/模型规则包数据（examples）

**Files:**
- Create: `examples/skills/electrical/`（skill.yaml + rules/5 YAML + aliases.yaml）、`examples/skills/model/`（skill.yaml + rules/1 YAML）

**Interfaces:**
- electrical skill.yaml: name 建筑电气制图（GB/T 50786-2012）, version 1.0.0, discipline electrical, description 注"标准号外部核验待原件确认；符号本体规则 VISUAL deferred"。
- 5 规则 YAML（excerpt 逐字自 extraction）：
  1. R-50786-5.1.3 text-height-min（minLatin 2.5/minHan 3.5；modal 不应／不宜；recommended/high；excerpt H3 逐字）
  2. R-50786-4.1.5-8 cable-format（modal 宜；recommended/high；excerpt D7 操作句"绘制图样时，宜采用表 4.1.5 的电气设备标注方式表示"+序号8 格式注记）
  3. R-50786-3.4.3 slot-code-whitelist（三白名单 params 逐字 D9；modal "应（3.4.3）／宜（4.2.1）冲突在案"；recommended/high；excerpt D8 逐字）
  4. R-50786-4.1.5-5 luminaire-format（luminaireMounts params；modal 宜；recommended/medium；excerpt D7 操作句+序号5 注记）
  5. R-50786-4.2.4-ref 参照代号？**不入库**——D12 子类白名单庞大且一码多义，第一 slice 排除（spec §12 注记）。electrical 包实为 4 规则。
- aliases.yaml：electrical 常见层名（E-LITE/E-POWER/E-SYST/ELIGHT 等——**无 extraction 依据的别名不编造**；第一 slice aliases 空文件+注释"待真实院标/天正电气图层核实后补充"）。
- model skill.yaml: name 信息模型制图（JGJ/T 448-2018）, version 1.0.0, discipline any；1 规则 R-448-5.4.3 file-naming（excerpt R1 逐字"工程图纸命名宜由专业代码、图纸编号、图纸名称、描述等字段依次组成，以下划线'_'隔开，字段内部的词组以连字符'-'隔开"；modal 宜；recommended/medium；专业代码白名单 deferred 注记）。
- **治理**：examples 包非 builtin——RulePackVersion 治理测试只钉 builtin；examples 包加载测试（skill validate 两包 exit 0）。

- [ ] **Step 1: 写 YAML 数据**（逐字摘录）+ 加载测试（SkillLoader.LoadDirectory 两包；规则数 4/1；Id 无冲突与 builtin）。
- [ ] **Step 2: 运行测试**（加载+check --skill 电气包对 T4 fixture：BV-3×2.5-PC20-WC 合规/XX 违规——Cli 测试）。
- [ ] **Step 3: GREEN**。
- [ ] **Step 4: Commit** — `feat: electrical + model rule packages (verified TEXT clauses, visual deferred)`

---

### Task 7: 步骤模板 schema 与加载（Core 加性）

**Files:**
- Create: `src/CadAgent.Core/Skills/StepTemplate.cs`
- Modify: `src/CadAgent.Core/Skills/SkillPackage.cs`（Steps 加载）
- Test: `tests/CadAgent.Core.Tests/StepTemplateTests.cs`

**Interfaces:**
- `sealed record TemplateStep(string Kind, Dictionary<string, object> Params)`（params 值可含 "{{name}}" 占位字符串）
- `sealed record StepTemplate(string Name, string Description, IReadOnlyList<string> ParamNames, IReadOnlyList<TemplateStep> Sequence)`
- steps/*.yaml 格式：`templates: [{name, description, params: [arg1, arg2], sequence: [{kind: add-line, params: {x1: "{{x1}}", ...}}]}]`
- `static class TemplateInstantiator`：`StepPlan Instantiate(StepTemplate t, IReadOnlyDictionary<string, object> args)`——ParamNames 全部必须在 args（缺→InvalidSkillException 列名）；占位替换（字符串值精确匹配 "{{name}}"→args 值；数值参数 args 须数值）；替换后构造 StepPlan（planId=$"{t.Name}-{Guid:N}"）；**未知占位符（不在 ParamNames）→InvalidSkillException**。
- SkillPackage.Steps 加性（steps/ 缺失→空；YAML 非法→InvalidSkillException inner）。

- [ ] **Step 1: 写失败测试**（替换正确/缺参/未知占位/数值类型/Steps 加载）
- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现**。
- [ ] **Step 4: GREEN**。
- [ ] **Step 5: Commit** — `feat: step template schema + instantiator with placeholder validation`

---

### Task 8: skill run 命令 + run --skill 接线（Cli）

**Files:**
- Modify: `src/CadAgent.Cli/CommandRouter.Skills.cs`（skill run）、`CommandRouter.cs`（run --skill 闸口接线）
- Test: `tests/CadAgent.Cli.Tests/SkillRunTests.cs`

**Interfaces:**
- `skill run <skillDir> --template <name> --drawing <file> --args <json> [--project dir]`：加载包→找模板（无→exit 1）→Instantiate(args)→**审计 skill-run 请求**（Source="skill", paramsJson={skill, template, args, planId}）→StepEngine.Run（既有审计/快照/闸口；overlay 存在→SemanticGate 注入，与 run 一致）→stdout TxResult JSON→exit 0/1。
- `run --skill <dir>`：加载包做规则集校验（Id 冲突→exit 1）+ 语义闸口 overlay 别名？**裁定：run --skill 第一 slice 仅校验包加载+闸口 overlay 不变（别名影响 scan 不影响 run 闸口存在性检查）；步骤模板执行走 skill run**。run --skill 记录 rulePackVersion 入 tx 审计（加性）。
- 失败路径：args JSON 非法→exit 1；模板缺参→InvalidSkillException→stderr+exit 1（LoadOrReject 审计）。

- [ ] **Step 1: 写失败测试**（sample skill 加 steps/lay-line.yaml 模板 {x1,y1,x2,y2}→skill run 成功画线+审计链绿；缺参 exit 1；未知模板 exit 1；run --skill 冲突 exit 1）
- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现**。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: skill run via StepEngine (audited, gated) + run --skill validation`

---

### Task 9: 符号库元数据 + insert-block 处理器（Data/Cli）

**Files:**
- Create: `src/CadAgent.Core/Skills/SymbolMeta.cs`、`src/CadAgent.Data/Handlers/InsertBlockHandler.cs`
- Modify: `src/CadAgent.Core/Skills/SkillPackage.cs`（Symbols 加载）、`src/CadAgent.Data/Handlers/HandlerRegistry.cs`（注册第六处理器）
- Test: `tests/CadAgent.Data.Tests/InsertBlockTests.cs`、`tests/CadAgent.Cli.Tests/SymbolMetaTests.cs`

**Interfaces:**
- `sealed record SymbolMeta(string Name, string File, SemanticClass Class, string Description)`；symbols/symbols.yaml：`{symbols: [{name, file, class, description}]}`；file 指向 symbols/*.dxf（第一 slice 元数据校验：name 非空/file 非空/class 合法；**文件存在性不强制**——跨文档导入计划 3）。
- **InsertBlockHandler** Kind="insert-block" params {block, x, y, scale?, rotation?}——图纸内 BlockRecord 存在→new Insert(blockRecord, insertPoint)（ACadSharp Insert API 以 XML 文档核实）；块不存在→StepResult(false, "块不存在: {block}（跨文档符号导入留计划3）")；**ISemanticAware 不实现**（几何级；语义需求随计划 3 布灯处理器声明）。
- HandlerRegistry.All() 六处理器；装配守卫既有。

- [ ] **Step 1: 写失败测试**（fixture 含 BlockRecord "LUM"→insert-block 成功 Insert 实体；缺块→失败结果；symbols.yaml 加载/非法 class 拒绝）
- [ ] **Step 2: RED**。
- [ ] **Step 3: 实现**（Insert API 核实）。
- [ ] **Step 4: GREEN + 全套件**。
- [ ] **Step 5: Commit** — `feat: symbols metadata + insert-block handler (in-drawing blocks; cross-doc import plan 3)`

---

### Task 10: 端到端与承接记录

**Files:**
- Create: `tests/CadAgent.Cli.Tests/ElectricalSkillE2ETests.cs`
- Modify: spec §12、examples/skills/sample-institute/symbols/（示例元数据）

**Interfaces:**
- E2E：电气包 check（fixture 缆线文本合规+违规+字高违规→issues 引用 GB/T 50786-2012 条款）→adjudicate 升级 slot-code 规则 mandatory→check exit 2→skill run 模板（sample skill lay-line args）画线成功+审计→insert-block（fixture 块）成功→audit verify 绿→rulePackVersion 含电气包@1.0.0。
- spec §12 追加 2b-ii-b 完成记录：CommandRouter 提取（行数）；电气 4 规则+模型 1 规则（TEXT 可核实，VISUAL deferred 清单：符号本体/分数排版/参照代号子类/光源种类联动）；步骤模板+skill run（审计管线无旁路）；insert-block 图纸内块（跨文档导入计划 3）；遗留小项批关闭清单；2b-ii 全部完成→计划 3 就绪。

- [ ] **Step 1: E2E 测试**（完整代码）
- [ ] **Step 2: 运行**
- [ ] **Step 3: 全套件**
- [ ] **Step 4: spec §12**
- [ ] **Step 5: Commit** — `test: electrical skill end-to-end; docs: plan-2b-ii-b completion, plan-3 readiness`

---

## 自审记录

- 规则数据逐字来源：H3（5.1.3）/D7（4.1.5 格式）/D8（3.4.3 应）/D9（4.2.1 三白名单 14+11+11）/R1（448 5.4.3）——extraction 行号在案；D12 参照代号排除（一码多义+庞大）；情态冲突双录。
- 新检查器 5 个（text-height-min/cable-format/slot-code/luminaire-format/file-naming）候选保守，DetectionBasis 诚实边界；CE/CL 槽位消歧有测试。
- FileNamingChecker 源文件名还原（.working.dxf 后缀）裁定在案。
- skill run 走 StepEngine 无旁路；insert-block 第六处理器装配守卫覆盖。
- 245 测试守卫（T1 纯移动）；治理测试钉 builtin 内容哈希（格式不变）。
- 跨文档符号导入/完整形缆线校验/专业代码白名单/视觉项全部显式 deferred。