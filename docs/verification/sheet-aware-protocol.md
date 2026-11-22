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
