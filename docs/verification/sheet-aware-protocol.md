# 幅面感知读图切片——真实图纸复核协议（sheet-aware T1-T7）

本协议供持有真实图纸标本的验证者按步复核幅面感知读图切片（Sheet 登记簿 / 层名归一+块遍历 /
属性种子 / 幅面闸口 / 布局视口+幅面 UI / 视觉交叉管道 / agent 联动）。
合成夹具为 CI 主闸口（仓库不携带用户图纸，Ruling R5）；真实标本测试为 skip-gated
（`SkippableFact` + `Skip.If(!File.Exists(...))`），本机有标本则实跑，缺标本显式 Skipped 不假装。

## 0. 前置条件

- **真实标本**（本机路径，仓库不携带）：
  `E:\随机任务 ·Qoder·\outputs\tarch-native\电施（强电）-2#厂房电力+普通照明平面图.dwg`
  （天正电气 DWG，AC1027；bound xref 底图 `2号厂房0917_t9$0$*` 层族；Layouts=2：Model + 空布局1）。
  探针证据：`outputs/api-probe/`（COM 原生探针）、`outputs/tarch-native/intake.json`（准入 Green）。
- **ZWCAD 真机核查**（涉及真机测试前后各一次）：
  `tasklist | findstr /i "ZWCAD"` 应无进程；无并行 dotnet test 会话（COM STA 串行约束）。
- **日志口径**：Windows GBK 控制台——dotnet 输出重定向 UTF-8 文件再读
  （`cmd /c "chcp 65001 >nul & dotnet test ... > outputs\sheet-aware\<name>.log 2>&1"`）；
  单项目或全 sln 一次一目标（MSB1008）。
- 真实 DWG 只读复核优先 `DwgReader` 直读（不经 `DrawingSession.Load`，无工作副本副作用）；
  CLI 复核用 `.dwg` 源时 Load 会物化 `.working.dxf` 工作副本（提交态编辑落于此，原件永不写）。

## 1. 自动化复核（skip-gated 真实测试，本机实跑）

```
dotnet test tests/CadAgent.Data.Tests --filter FullyQualifiedName~RealDrawing
```

| 测试 | 钉住事实 | 精确期望 |
|---|---|---|
| `SheetRegistryTests.RealDrawing_NineSheets_NamesAndAttributes` | 幅面登记簿 | **9 幅面**；名含 一层电力平面图 / 屋顶层电力平面图 / 四层普通照明平面图；全部 Date=2026.09、TitleAttributes[版本号]=01 |
| `BlockAttributeSeederTests.RealDrawing_LuminaireCount_MatchesProbe` | 属性 NativeObject 种子 | **Lighting == 1364**（A=深照型灯，探针精确钉住）；Equipment ≥ 137（实测 147 = 屏台箱柜 137 + 交流电动机 10）；全部 Confidence=0.95 |
| `SheetRegistryTests.RealDrawing_LayoutsRegistered_EmptyLayoutHonest` | 空布局诚实 | 模型幅面 9 + **布局幅面 0**（空布局1 无真实视口不登记；纸边界视口 RepresentsPaper 不计） |

缺标本时三者显式 Skipped（原因"真实图纸不在本机"）——不得将 Skipped 报告为通过。

## 2. 手动复核步骤（CLI，真实标本）

以下 `<dwg>` 指第 0 节标本路径；`--project` 指向独立临时目录避免污染既有叠加层。

### 2.1 幅面名与 sheets 摘要（T1/T7）

```
cadagent semantic scan --drawing <dwg> --project <tmp>
```

- stdout JSON `sheets` 数组（T7 加性契约）：**9 项**，每项 `{sheetId,name,number}`；
  sheetId=图框 Insert 句柄（十进制字符串）；name 与 CAD 内目视图框名逐一核对
  （一层电力平面图 / 二层电力平面图 / … / 屋顶层电力平面图 / 四层普通照明平面图 等 9 幅）。
- `version/annotations/ambiguous` 既有字段保持。

### 2.2 属性计数（T3）

- stdout `annotations` 中 `provenance=NativeObject` 且 `class=Lighting` 计数 **== 1364**；
  `class=Equipment` ≥ 137（当前口径 147）。
- 抽样 3 条 Lighting 标注：`targetHandle` 能在 CAD 内定位到灯具块 Insert，`sheetId` 归属其所在图框。
- 人工改判闭环：`semantic map` 改判某层后 re-scan，HumanConfirmed 不被种子覆盖（R7：
  属性改未知→re-scan→陈旧 NativeObject 清除→闸口拒绝）。

### 2.3 底图墙对经块遍历（T2）

- 底图墙线在 bound xref 嵌套层 `2号厂房0917_t9$0$WALL`（归一→WALL）。
- stdout `annotations` 中 `class=Wall`、`provenance=GeometryInference` 的标注：
  `targetHandle` 应含嵌套句柄路径后缀（`{parent}:{i}` 形态，块遍历世界坐标口径）；
  抽样核对墙对两条线在底图内平行且间距合理（墙厚量级）。
- 框外远端残元（若有）`sheetId=null`，不混入任何幅面。
- 诚实注记：墙对检测为平行线对启发式（GeometryInference 0.6 < 闸口 0.8，须人工确认才放行操作）。

### 2.4 跨幅拒绝（T4/T7）

- 合成夹具 E2E 已钉住（`tests/CadAgent.Cli.Tests/SheetAwareAgentE2ETests.cs`）：
  双幅面图 Lighting 确认仅 S1，fake LLM 计划 `label-luminaires` params `sheetId=S2` →
  **tx failed + semantic-gate-refused 审计含 sheetId** + 重规划一次仍拒→Escalated + 零落图；
  `sheetId=S1` → Executed + tx-commit + agent-verify 绿 + 验链；无 sheetId legacy → 全局放行。
- 真实图纸复核：`semantic scan` 后取 sheets 摘要两个不同 sheetId；构造 agent 意图使计划
  携带与 Lighting 确认不同幅的 sheetId（真实 LLM 或 harness 注入），核对：
  `audit verify` 链绿；`semantic-gate-refused` 记录 paramsJson 含目标 sheetId 与 required 类名；
  图纸哈希前后一致（拒绝零变更）。
- **无审计旁路**：任何拒绝路径必须留痕；legacy 两参路径拒绝消息保持原样（作用域注记仅幅面路径追加）。

### 2.5 空布局诚实注记（T5）

- 真实标本 Layouts=2（Model + 空布局1）→ 布局幅面 **0**：`sheets` 摘要无 `L` 前缀项。
- 视图幅面选择器仅列 9 模型幅面；布局幅面只读硬闸（纸面编辑 deferred）由合成视口夹具钉住
  （`ViewportRenderTests` / T5 Fix1 编辑工具硬禁 + 在途编辑切幅守卫）。
- **证据缺口明示**：空布局无内容标本——视口真实内容渲染 E2E 未覆盖（合成 Viewport 夹具
  经 DXF roundtrip 钉住登记/裁剪/纸面映射数学；真实含视口图纸待用户提供后补复核）。

### 2.6 视觉交叉管道 harness（T6，真实模型 E2E 留 harness）

生产 CLI 尚未接 VL reader（`IRenderSnapshot` 实现未接生产管线）——以下为 harness 注入示例
（视图侧经 RenderModelBuilder 裁剪 + Skia 离屏编码实现 seam；测试侧 fake 不触网）：

```csharp
// 1) 幅面瓦片（字高中位数反算可读区域，1 overview + ≤6 tiles）
var walked = BlockWalker.Walk(session.Doc, session.Sheets).ToList();
var tiles = SheetTiler.Tiles(session.Sheets[0], walked);

// 2) IRenderSnapshot 注入示例（Data 不引用 View，无环）
sealed class SkiaSnapshot : IRenderSnapshot
{
    public byte[] RenderPng(string sheetId,
        (double MinX, double MinY, double MaxX, double MaxY) rect, int w, int h)
    {
        // 视图侧：RenderModelBuilder 按 rect 裁剪 → Skia 离屏 SKSurface(w,h) → Encode PNG
        throw new NotImplementedException("harness 注入点");
    }
}

// 3) VL 读图（Core ILlmClient.ImageUrl 加性通道）→ VisionReadResult
byte[] png = snapshot.RenderPng(tiles[0].SheetId, tiles[0].Rect, 2800, 1750);
var resp = await llm.ChatAsync(new ChatRequest(
    new[] { new ChatMessage("user", "读出图中设备文字标注，输出 JSON claims") },
    MaxTokens: 2000, ImageUrl: "data:image/png;base64," + Convert.ToBase64String(png)));
var vision = ParseVisionReadResult(resp.Content); // claims: [{text,region,confidence}]

// 4) 矢量交叉验证 + 种子（第五级确定性来源；幻觉拦截）
var result = SemanticScanner.Scan(session, existingOverlay, skillAliases, vision);
// result.VisionAmbiguous = 无矢量佐证/矛盾声称清单（人工纠偏）；
// 已种子 claim 的跨幅"无佐证"假歧义被抑制（R9）；短值（<3 字符）包含匹配不种子。
```

- 复核要点：声称仅以**本幅面**矢量 GT 佐证才种子（LlmInference 0.9，跨幅跑偏拒绝）；
  无佐证声称入 `VisionAmbiguous` 不静默丢弃；单字符轴号 GT 不得以包含匹配误种子（R9 短值守卫）。
- **诚实边界**：真实 VL 模型端到端（真实瓦片渲染→真实模型读图→交叉验证）留 harness，
  未接生产 CLI；合成 fake seam 测试为当前自动化闸口。

## 3. 剩余边界（明示，不假装）

1. **代理 🔴 路径无真实标本**：两份标本（原生 DWG 与其工作 DXF）准入均 Green、代理实体 0
   （`outputs/tarch-native/intake.json` unsupported=[]）——🔴 代理拒绝路径仅合成 ProxyEntity
   夹具覆盖（DxfWriter/DxfReader 往返实证），真实天正代理实体标本待补。
   **⚠️ 本节 Green 判定已被防假 Green 切片推翻（见第 5 节）**：census 通知通道实证四份
   原生标本均 Yellow（图内 Unknowns=0 是 ACadSharp 对象图盲区，非全解析证据）。
2. **空布局视口无内容标本**：视口真实 E2E 缺口（见 2.5）。
3. **视觉管道真实模型 E2E 留 harness**：IRenderSnapshot 实现 + VL reader 未接生产 CLI（见 2.6）。
4. **纸面编辑 deferred**：布局幅面只读硬闸（编辑工具硬禁；不做纸面→模型逆映射）。
5. **TwistAngle 与非矩形视口裁剪 deferred**：视口数学仅平移+缩放（ViewTarget/ViewHeight）。
6. **图框块内部几何编辑在指纹外**：Sheets 缓存指纹覆盖顶层实体与图框插入变换，块定义内容
   变化需 Reload 失效（就地改块内几何不触发重建）。
7. **signoff.RollbackTx 不经布局只读闸**：快照回滚作用于模型 tx，与视图当前幅面无关——
   与"布局只读"预期存在轻微张力（回滚是审计事实源操作，非纸面编辑；注记在案）。
8. **属性表硬编码**：AttributeSemantics 映射表（A=深照型灯 等）为方法内硬编码，
   skill 覆写留后续切片（可注入重构）。
9. **瓦片零重叠骑缝**：SheetTiler 瓦片无重叠边距，骑缝文字可能截半（~10% 重叠留后续）。

## 4. 证据归档要求

- 每次复核：日志入 `outputs/sheet-aware/`（UTF-8；重定向文件再读，禁直接 GBK 控制台截取）；
  精确数字记录（幅面数/属性计数/测试通过数/跳过数），不得四舍五入或"约"。
- 真机步骤前后 `tasklist` 核查输出归档（无 ZWCAD 残留进程/无并行会话）。
- 审计证据：`audit verify`（哈希链）+ 必要时 `audit verify --anchor`（HMAC 锚）+
  `audit package`（manifest 不含 llm 配置/密钥/锚密钥）；拒绝路径必须能出示
  `semantic-gate-refused` 记录原文（paramsJson 含 sheetId）。
- 视图复核截图入 `docs/verification/`（命名沿用 NN-描述.png 序号 convention）。
- 缺标本/缺模型步骤：显式记录 Skipped/harness-only 及原因，不得以合成结果冒充真实复核。
- 门禁口径：`dotnet test CadAgent.sln` 顺序两跑连绿（含真机条件测试），
  日志 `outputs/sheet-aware/final-gate1.log` / `final-gate2.log`。

## 5. 天正闸口实战节（防假 Green 切片 T4，2026-11-23 复核）

census（OnNotification）→ 闸口三分 → 六修改消费点拒绝留痕（T1-T3）落地后，用四份**真实原生
天正标本** + 法院标本实战重跑。证据目录 `outputs/tarch-gate-recombat/`（UTF-8 日志 + audit
params 摘录 + 哈希前后对照）。**标本只读硬约束**：.dwg 一律复制到证据目录/临时项目再 intake
（working 副本/sidecar 全落副本侧）；法院目录零副作用（仅读复制，源哈希前后一致
`61214068…cae277`，目录文件清单不变；其既有 `.working.dxf` 为 2026-11-22 旧会话遗留，
删除裁定仍归用户，本次不触碰）。

### 5.1 四标本 intake 期望表（skip-gated 测试钉住，本机实跑）

| 标本（outputs 副本） | level/exit | Entities | census 类数 | census 总数 | TCH_WALL | TCH_PIPE | 版本 |
|---|---|---|---|---|---|---|---|
| tarch-native/电施（强电）-2#厂房电力+普通照明平面图.dwg | Yellow / 2 | 3792 | 32 | **5985** | **1592** | 30 | AC1027 |
| tarch-native2/1#配套用房0928.dwg | Yellow / 2 | 3636 | 30 | **3257** | **968** | 4 | AC1018 |
| tarch-native3/2号厂房1021_t9.dwg | Yellow / 2 | 4464 | 30 | **5707** | **1594** | 30 | AC1018 |
| tarch-native4/S-1#配套用房_t8.dwg | Yellow / 2 | 1501 | 28 | **3806** | 401 | **994** | AC1018 |

- 四份均图内 Unknowns=0（对象图盲区实证）+ Entities>0 + census 非空 → **Yellow**（非 Red 非假 Green）。
  无 Entities=0 标本 → 真实 Red 路径仍仅合成夹具覆盖（诚实缺口，待补标本）。
- 计数与 `outputs/api-probe/tarch-probe-out.txt` 探针交叉一致（s4 TCH_PIPE 994/PIPEFITTING 597/
  EQUIPMENT 514/WALL 401 逐项吻合）；s3 与 T1 报告 5707/30 类一致。
  **更正**：切片任务书所记"电施 census 5707/30 类"实为 s3 数字——电施实证 5985/32 类
  （TCH_WALL 1592 吻合），以本表为准（`census-summary.txt`）。
- 自动化钉住：`tests/CadAgent.Cli.Tests/UnparsedGateCliTests.cs`
  `RealNativeTArch_IntakeYellow_CensusMatchesProbe`（SkippableTheory×4）+
  `RealNativeTArch_RunRefused_ReadOnlyAudit` + `RealNativeTArch_ExportRefused_NoDerivedOutput`
  （共 7 用例；缺标本显式 Skipped）。TDD 红相以变异核查实证：s3 TCH_WALL 期望临时 1594→1593
  → FAIL（Expected 1593/Actual 1594，`t4-red-mutation.log`），还原 → 7/7 绿 0 跳过
  （`t4-green-filtered.log`）。

### 5.2 拒绝留痕核对步骤（电施副本，五消费点实战）

```
dotnet <cli> intake <dwg副本> --project <p>          # exit 2 Yellow + census stdout
dotnet <cli> run plan-addline.json --drawing <dwg副本> --project <p>   # exit 1
dotnet <cli> llm add/use（dummy profile，闸先于 LLM，不触网）
dotnet <cli> agent --drawing <dwg副本> --intent 画一条线 --yes --project <p>  # exit 1
printf '画一条线\n:status\n:quit\n' | dotnet <cli> repl --drawing <dwg副本> --yes --project <p>
dotnet <cli> export --drawing <dwg副本> --out derived.dwg --project <p>  # exit 1，无衍生产物
dotnet <cli> skill run examples/skills/sample-institute --template lay-line ...  # exit 1
dotnet <cli> audit verify --project <p>              # 链绿
```

核对结果（`audit-p1-refusal-params.txt`，audit.db params_json 直读）：

- run/agent/repl/skill 各 1 条 `run-rejected`（Source 分别为 run/agent/repl/skill）+ export 1 条
  `export-rejected`；params 均 **level=Yellow、unparsedCount=5985**、guidance 含
  **"图形导出"/"T3"/"只读审查"**（T3 指引文案核对通过）；before_hash==after_hash（拒绝零变更）。
- **tx-start/tx-commit=0、llm-call=0**（agent/repl 闸先于 LLM；dummy profile 未触网）。
- repl 拒绝后会话存活：`:status` 正常输出 entities=3792 undo=0（只读命令不受闸）。
- working 副本与源 .dwg 副本 SHA-256 前后逐字节一致（`refusal-hashes-{before,after}.txt` diff 空）。
- 四项目 + 法院项目 `audit verify` 全绿（p1=11 条、p2/p3/p4=1 条、p-court=2 条）。

### 5.3 法院标本分类（决定性更正）

`outputs/tarch-court/电气总平面图.dwg`（复制自法院归档目录，只读）intake → **Yellow / exit 2**：
Entities=3860（全原生对象图）+ **census 1836 对象 / 13 类**（TCH_DIMENSION2 550、TCH_TEXT 523、
TCH_WALL 482、TCH_OPENING 117、TCH_COLUMN 64、TCH_MULTILEADER 40、TCH_SPACE 31、
TCH_AXIS_LABEL 12、TCH_DRAWINGNAME 8、TCH_RECTSTAIR 3、TCH_EQUIPMENT 3、TCH_SHEET 2、
TCH_BLOCK_INSERT 1），AC1027（`intake-court.json` / `court-classification.txt`）。

- **2026-11-22 "法院标本=T3 导出态/Green" 判定作废**：该判定基于对象图（代理 0/3860 全原生），
  正是 census 切片所修的盲区——通知通道实证文件仍含 1836 个未解析 TCH 原生对象
  （类表残留之外**确有对象级 UnknownEntity 读取**）。法院标本为**部分解析混合态**，非纯 T3。
- 拒绝实证：法院副本 run add-line → exit 1 + `run-rejected level=Yellow unparsedCount=1836`
  + 哈希不变 + 验链绿（`run-court-refused-err.txt` / `audit-court-refusal-params.txt`）。

### 5.4 🟢 闭环重取裁定

**裁定：真实 🟢 重取不可得（法院标本 Yellow）。** 依据 = 5.3 census 1836 未解析对象 →
闸口全图级拦截修改，🟢 闭环（scan→map→run→verify 的修改链）在法院标本上不可达。
诚实注记：

- 当前 🟢 证据 = **合成 E2E**（SheetAwareAgentE2ETests 双幅面 fake LLM + LightingBenchmark/
  LabelE2E 等）；2026-11-22 电施"🟢 闭环"为 census-blind 盲子集证据，已作废（spec §12 在案）。
- **真实 🟢 需用户 T3 转换动作**：在天正中【文件布图→图形导出】保存 T3 后重新 intake
  （期望 census 空 → Green），再走 scan→人工 map 确认→幅面闸口操作→verify。
  本次**未执行 map、未伪造任何人工确认**（Yellow 标本上 map 无意义且修改被闸）。
- 四份原生标本均 Yellow → 同样不可作真实 🟢 载体；🟢 真实重取统一待用户 T3 导出件。

### 5.5 残余缺口 R9（实战新发现，留 T5/用户裁定，本切片不修）

**陈旧 working 副本 + 新项目 = 假 Green 旁路**：census sidecar 以项目键控，working DXF 以源
键控且丢失 TCH 通知——防假 Green 切片**之前**物化的 working（无 sidecar）在新项目 intake
时走 load-if-exists 分支，census 不可恢复 → Green 放行修改。复现（`bypass-demo/`，全程副本）：
电施 .dwg + 其旧 working 复制到演示目录 → 新项目 intake **exit 0 Green**（unparsed 字段省略）
→ run add-line **exit 0 tx-commit**（本应 Yellow 拒绝）。对照：同一 .dwg 无 working 首载 →
Yellow 5985。影响面：outputs/tarch-native、tarch-native2 与法院目录的既有旧 working 均属此态
（法院目录旧 working 删除裁定归用户）。候选修复方向：sidecar 随 working 源旁持久化
（源哈希键控）或 sidecar 缺失时强制 DWG 通知重读——留 T5 裁定，不在 T4 范围。

### 5.6 证据清单（outputs/tarch-gate-recombat/）

`build-cli.log`；`intake-s{1..4}.json` + `census-summary.txt`；`t4-red-mutation.log` /
`t4-green-filtered.log`；`plan-addline.json`；`{run,agent,repl,export,skill}-refused*.{json,txt}`；
`refusal-hashes-{before,after}.txt`；`audit-p1-refusal-params.txt`；`verify-p{1..4}.json`；
`intake-court.json` / `court-classification.txt` / `run-court-refused*` /
`audit-court-refusal-params.txt` / `verify-court.json`；`bypass-demo/`（R9 复现）。
法院副本与项目：`outputs/tarch-court/`。
