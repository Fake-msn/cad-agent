# CAD 智能体终端 · 244 室内标准落地 实现计划（计划 3e）

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans（Side chat 内联）。

**Goal:** 用户裁定两项落地：①JGJ/T 244-2011 真实条文规则包入库；②灯具编号步骤（设备索引符号，A.4.12 深度第一步）。

**Architecture:** label-luminaires 第八处理器（正六边形索引符号+编号文本，ISemanticAware=[Lighting]——Lighting 类首次实战）；room-name 新检查器（5.2.5）；interior-244 skill 包（可查规则+NoChecker 结构化跳过的 VISUAL/深度规则+17类灯具符号目录+编号步骤模板）。

**Spec:** spec §12 3d 记录；standards-src/extraction-244.md D1-D12（条文逐字来源）。

## Global Constraints

- 244 正文零必须/严禁→**全部 recommended/informational，零 mandatory**；excerpt 逐字自 extraction-244.md；verification: source-text（封面齐全已确证）。
- 不编造：灯具图例几何 VISUAL deferred（MinerU effort=medium 关闭图像分析所致，high 重转为用户动作）；品种代号归院标（3.6.5 条文说明）——prefix 为处理器参数无默认；244 不规定图层名——aliases 空。
- 索引符号几何（3.6.6-4）：正六边形+水平内径线、细实线（0.25mm）、长轴 8~12mm 参数校验、内注编号+品种代号。
- 编号确定性：Insert 按 Handle 升序，prefix+序号；文本字高≥2.5mm（50001 字母数字底线）。
- 492 既有测试持续绿；TDD；feat: 前缀；git 署名环境变量；EOF 换行。

## 文件结构

```
src/CadAgent.Data/Handlers/LabelLuminairesHandler.cs   第八处理器
src/CadAgent.Data/Checks/RoomNameChecker.cs            room-name 检查器
src/CadAgent.Cli/CommandRouter.cs                      StandardsEngine 注册（加性）
src/CadAgent.Cli/CommandRouter.Agent.cs                KindDocs 加行
examples/skills/interior-244/{skill.yaml,rules/*.yaml,symbols/symbols.yaml,steps/label-luminaires.yaml,aliases.yaml}
tests/...
```

---

### Task 1: label-luminaires 处理器

**Interfaces:**
- `LabelLuminairesHandler : IStepHandler, ISemanticAware`，Kind="label-luminaires"，RequiredSemantics=[Lighting]
- params {prefix(必填), block?(过滤块名), start?(默认1), symbolSize?(默认10，[8,12]校验), layer?(符号实体层，缺省=被编号 Insert 的层)}
- 每个匹配 Insert（Handle 升序）：六边形中心=(InsertPoint.X+size, InsertPoint.Y+size)；外接圆半径=size/2，顶点角 0°/60°/…/300°（长轴水平=size）；6 边 Line+水平内径线 Line（LineWeight=(LineWeightType)25）；TextEntity "{prefix}{n}" Height=max(2.5, size/4)，InsertPoint=(cx - len*height*0.35, cy - height/2)（近似居中，确定性公式）
- 无匹配 Insert→失败"无灯具 Insert 可编号"；prefix 缺失/空→失败；symbolSize 越界→失败（引 3.6.6-4）
- TargetHandle=首个编号文本句柄
- HandlerRegistry 第八；HandlerTests 注册表断言 7→8（sanctioned）；KindDocs 加行

**测试（LightingLabelHandlerTests）：** 2 Insert→L1/L2 文本+14 Line（12边+2内径）+字高2.5+长轴10（顶点距）+线宽25；symbolSize 13/7→失败；block 过滤；start=5→L5/L6；缺 prefix→失败；无 Insert→失败；RequiredSemantics 含 Lighting；Registry 8。

步骤：RED→实现→GREEN+全套件→Commit `feat: label-luminaires handler - hexagon equipment index symbols per JGJ/T 244-2011 3.6.6-4`

### Task 2: room-name 检查器（5.2.5）

**Interfaces:**
- `RoomNameChecker : IRuleChecker`，CheckType="room-name"；RoomDetector.Detect 非空→无 issue；空→1 issue（Message 含"5.2.5"与"房间的名称或编号"，TargetHandle=""，DetectionBasis 注记：房名模式列表启发式，编号圆圈格式 VISUAL deferred）
- StandardsEngine 注册加性（重复守卫既有）

**测试（RoomNameCheckerTests）：** 有房名→空；无房名→1 issue；CheckType。CLI 测试在 T3 包就绪后。

步骤：RED→实现→GREEN+全套件→Commit `feat: room-name checker per JGJ/T 244-2011 5.2.5`

### Task 3: interior-244 skill 包

**Files:**
- skill.yaml：name 室内装饰装修制图（JGJ/T 244-2011）, version 1.0.0, discipline interior, drawingType any；description 注明：全 recommended；图例几何 VISUAL deferred（MinerU effort medium 关闭图像分析，high 重转待用户）；品种代号归院标 skill。
- rules/（excerpt 逐字自 extraction-244.md，verification source-text，force recommended）：
  - R-244-5.2.5.yaml：check room-name（T2 检查器）；modal 应
  - R-244-4.0.9.yaml：check luminaire-legend（**NoChecker 结构化跳过**＝VISUAL deferred 诚实登记）；suggestion 含 17 类目录+几何待视觉关联
  - R-244-3.6.5.yaml：check equipment-index-symbol（NoChecker deferred；几何参数在 suggestion：正六边形/细实线/8~12mm/编号+品种代号；label-luminaires 处理器已实现生成，检查器留后续）
  - R-244-A.4.12.yaml：check luminaire-layout-depth（NoChecker deferred；suggestion 深度清单：位置/定位尺寸/材料名称/编号/做法+设备协调）
  - R-244-4.0.3.yaml：check custom-legend-governance（NoChecker deferred；自编图例不得重复+须说明）
- symbols/symbols.yaml：17 类灯具（name 逐字表4.0.9，file "deferred/{拼音或序号}.dxf" 占位——文件存在性不强制既有裁定，class Lighting，description 注明图例几何待 MinerU high 重转+视觉关联）
- steps/label-luminaires.yaml：templates name label-luminaires, params [prefix], sequence [{kind: label-luminaires, params: {prefix: "{{prefix}}"}}]
- aliases.yaml：aliases: []（244 不规定图层名，不编造）

**测试（Interior244PackageTests）：** skill validate exit 0；check --skill 包：无房名图纸→R-244-5.2.5 issue（recommended，exit 0）+4 条 NoChecker 跳过（stderr 含 luminaire-legend/equipment-index-symbol/luminaire-layout-depth/custom-legend-governance）；有房名→该规则无 issue；skill run label-luminaires --args {"prefix":"L"} 需 Lighting 语义（scan+map E-LITE→Lighting 或 lighting skill 别名）→编号生成。

步骤：写包+测试→GREEN+全套件→Commit `feat: interior-244 skill package - real JGJ/T 244-2011 clauses (recommended) + luminaire catalog + label template`

### Task 4: 编号 E2E + 承接记录

**Files:** tests/CadAgent.Cli.Tests/LightingLabelE2ETests.cs；spec §12；extraction-244.md 补 MinerU 核查结论

**Interfaces:**
- E2E：底图（墙+房名+LUM块）→scan lighting skill→map Room→agent 布灯（fake LLM）→**再 scan**（E-LITE 层 Insert 获 Lighting 别名 0.8）→skill run label-luminaires --skill interior-244 --args {"prefix":"L"}→6 文本 L1..L6+42 Line（6×7）→验链；闸口变体：不再 scan（无 Lighting）→agent fake LLM label 计划→Escalated+semantic-gate-refused
- spec §12 追加 3e：编号处理器（Lighting 闸口首次实战）/room-name 检查器/interior-244 包（NoChecker 诚实登记 VISUAL/深度规则）/MinerU 核查结论（hybrid+ocr_enable=true+effort medium 自动关图像分析＝图例缺失根因；high 重转为用户动作）
- 诚实边界：索引符号检查器未建（生成已有，校验 deferred）；图例几何待重转；文本近似居中；编号未含定位尺寸/材料/做法（A.4.12 余项 deferred）

步骤：E2E→全套件→spec/extraction→Commit `test: luminaire labeling E2E with Lighting gate; docs: plan-3e completion + MinerU effort root cause`

## 自审记录

- Lighting 闸口时序：scan 在布灯前→E-LITE Insert 尚未存在→别名标注无目标；**布灯后必须再 scan** 才有 Lighting 0.8（E2E 已含）。
- NoChecker 跳过是既定机制（CommandRouter.cs:551 注释先例）——deferred 规则登记不破坏 check 退出码（recommended 不影响 exit）。
- SymbolMeta file 非空即可（存在性不强制，2b-ii-b T9 裁定）——deferred 占位合法。
- 注册表断言 7→8 sanctioned 更新（HandlerTests.Registry_ContainsAllSevenKinds→Eight）。
- TextEntity 对齐 API 不赌——近似居中确定性公式（诚实注记）。