# 切片 A：COM 原生读参 + 逐实体精准松绑 设计

> 日期：2026-11-24 ｜ 基线：main 9f04cee ｜ 路径：架构级（brainstorming 五节逐节用户认可）
> 前序：可靠性 spike 闭合（outputs/api-probe/tarch-reliability-spike/report.md）——COM 读空根因=服务器忙窗间歇拒绝 IDispatch + PS 适配器静默转空；可靠附着/读取协议已验证 2/2。
> 上游：PR#28 双通道实证（COM ICom* 参数面可达 + .NET Explode）；产品方向=天正图纸完全解析。

## 0. 用户裁定（三问澄清 + 方案 + 五节认可）

- **范围**：理解侧读参 + **试探闸口松绑**（非仅理解）。
- **松绑边界**：**逐实体精准松绑**——不升整图 Green；仅 COM 已完整读参的 TDb 实体开放针对性修改；未解析区仍禁触，全图级修改仍拒。census 主闸保持，新增逐实体判据作补充。
- **参数载体**：**独立实体表**（NativeEntities 与 Annotations 并列），语义分类与原生参数分离存储。
- **方案**：**方案一**——扩展 AcadRuntime，ICadRuntime 加参数读方法；复用既有 STA 队列 + RetryBusy 忙窗重试框架。否决方案二（独立 TArchRuntime 重复框架/双所有权）与方案三（Data 直写 COM 违反厂商一等公民）。

## 1. 背景与目标

原生 TCH 图纸文件级 ACadSharp 读不到 TDb 实体（UnknownEntity 不暴露对象图）→ census Yellow → 只读。COM ICom* 自定义接口能读真值参数（墙 LeftWidth/Height/Volume/bbox、文本全文、柱 Volume、设备 PScale/LRotation 等），但只在厂商运行时内存中，且须 `ObjectName` 先分类（自定义名对非 TDb 实体恒 UNKNOWNNAME）。

**目标**：经 COM 读参补全 TDb 实体的原生参数与几何，落 overlay 独立表 `NativeEntities`，并对读参完整的实体精准开放修改（不升整图 Green，spec ⑥ 不拿信息残缺图纸操作的硬约束经逐实体判据满足而非绕过）。

## 2. 架构与组件边界（§1 认可）

| 组件 | 位置 | 职责 |
|---|---|---|
| `NativeEntityParam`（record） | Core | 一实体读参结果：Handle/ObjectName/Params/bbox/Class/ReadAt/ReadOk |
| `SemanticOverlay.NativeEntities` | Core | 独立表 List，与 Annotations 并列；JSON 加性（旧文件无此字段→null 归一空）；Save 原子写沿用 |
| `ICadRuntime.ReadNativeParams()` | Core 接口 | 新增只读方法返回 `IReadOnlyList<NativeEntityParam>`；默认实现=空（ZwcadRuntime 复用） |
| `AcadRuntime`（扩展） | Adapters.Acad | ①profile 感知启动（Process.Start /p + ROT 附着，新增分支）②ReadNativeParams：Gate1/Gate2 → 逐实体 ObjectName 分类 → TDb 自定义名读参 → 忙窗逐读重试 |
| `TDbClassMap` | Core | 静态映射 TDbWall→Wall/TDbColumn→Column/TDbEquipment→Equipment/TDbText→Annotation/TDbDrawingName→Annotation/TDbSymbArrow→Annotation；未知→Unknown（仍记参数） |
| `NativeParamSeeder` | Data.Semantic | 消费读参结果 → 句柄锚定写 overlay.NativeEntities（幂等 by Handle）→ Class!=Unknown&&ReadOk 同步产 Annotation(Provenance.NativeParameter 0.95) |
| `NativeGateRefinement` | Data | 逐实体松绑判据：目标句柄在 NativeEntities 且 ReadOk 且本轮 COM 重读校验一致 → 该实体允许修改（census 全图闸前的补充豁免） |
| CLI `semantic scan --native` | Cli | 触发 COM 读参→Seeder→overlay；stdout/审计加性 nativeRead/nativeSkipped/nativeClassified/nativeReadFailed |

**边界**：Core 零 COM（接口+record）；COM 细节全在 AcadRuntime；Data 只消费抽象结果；census 主闸不动，松绑是独立补充判据。

## 3. 数据模型与 overlay 扩展（§2 认可）

```csharp
public sealed record NativeEntityParam(
    string Handle,            // COM Handle 十六进制串（与文件级句柄同源，spike 实证 21E00）
    string ObjectName,        // "TDbWall"/"AcDbLine"…（IAcadEntity.ObjectName 标准属性）
    SemanticClass Class,      // TDbClassMap 映射；未知→Unknown（仍记参数不分类）
    IReadOnlyDictionary<string,string> Params,  // 自定义名读参字符串化保真
    double[]? Bbox,           // GetBoundingBox 六点 [minX,minY,minZ,maxX,maxY,maxZ]；不可读→null
    DateTimeOffset ReadAt,
    bool ReadOk);             // false=读参不完整（忙窗耗尽/ObjectName 不可读），松绑判据排除
```

- **overlay 扩展**：`SemanticOverlay.NativeEntities : List<NativeEntityParam>`；旧 overlay 文件无此字段→反序列化 null→归一为空 List（消费端免判空）；Save 沿用原子写（tmp+Move）。
- **幂等**：`UpsertNative(param)` by Handle（同句柄重读覆盖，含 ReadAt 更新）。
- **剪枝**：scan 重跑时 NativeEntities 按本轮 COM 读到的句柄集刷新；未读到的保留（COM 读参是增量非全量替换）；句柄已删的由人工/重扫清理。
- **Provenance**：枚举末尾追加 `NativeParameter`（置信度 0.95，同 NativeObject=厂商运行时真值）。Seeder 对 Class!=Unknown&&ReadOk 实体同步产 Annotation（TargetHandle=Handle, Evidence=$"COM 原生读参: {ObjectName}"），供语义闸口复用。
- **TDbClassMap**：初版覆盖 probe 实证类，新类增量补；未知类仍记 NativeEntities（Class=Unknown）但不产 Annotation。

## 4. 数据流（§3 认可）

### 4.1 读参路径（理解侧，census Yellow 只读豁免内）

```
CLI semantic scan --native
  → AcadRuntime.ReadNativeParams()
      ├─ EnsureApp（profile 感知分支）：
      │    需 TDb 读参 → Process.Start acad.exe /p "TElec20V5"
      │    → 1s 轮询 ROT GetActiveObject ≤180s → Gate1(Version 非空+docs.Count≥1, 重试)
      │    （既有 COM 激活路径保留给非天正场景；二者互斥由调用意图决定）
      ├─ Open(副本, ReadOnly) → 拒绝重试 → Gate2(ModelSpace.Count>0)
      ├─ 逐实体：ObjectName（RetryBusy）→ TDb*? → 是：按类读自定义名参数（逐读 RetryBusy，
      │    UNKNOWNNAME=该属性不存在→记缺省不重试）+ GetBoundingBox → NativeEntityParam
      │    非 TDb（AcDb*）→ 跳过（文件级已可读，无需 COM）
      └─ 返回 List<NativeEntityParam>
  → NativeParamSeeder.Seed(overlay, params)
      ├─ overlay.UpsertNative 逐条（by Handle）
      └─ Class!=Unknown && ReadOk → UpsertAnnotation(NativeParameter 0.95)
  → overlay.Save（原子写）
  → stdout/审计加性：nativeRead/nativeSkipped(非TDb)/nativeClassified/nativeReadFailed
```

### 4.2 逐实体松绑修改路径（census Yellow 下，仅 COM live 通道）

```
修改计划（目标=TDb 句柄，经 --live）
  → census 主闸 Level!=Green → 进 NativeGateRefinement 补充判据（而非直接拒）：
      目标句柄 ∈ overlay.NativeEntities 且 ReadOk 且本轮 COM 会话重读校验一致?
        （"一致"判据：本轮 live 会话重读该句柄的 ObjectName 与 bbox 与 overlay 记录逐字相等；
        数值参数按字符串化值相等比对；任一项不符或句柄在运行时已不存在→判不一致→拒）
        是 → 该实体放行（审计 native-gate-pass 留痕：句柄+读参哈希）
        否 → 维持 run-rejected（未解析区禁触，spec⑥不动）
  → 放行实体经 ComReplayService live 通道执行（ModelSpace 运行时实体可达）
  → 读回闸：修改后 COM 重读该实体参数，与计划期望值比对
```

**关键约束**：松绑**只在 live 通道**——文件级 `HandlerHelpers.Find` 走 `s.Doc.Entities`，TDb 实体文件级不可见（UnknownEntity 不暴露）→ 文件级 Find 找不到 TDb；必须经 ComReplayService 的 ModelSpace 运行时访问。重读校验是放行前提（防 stale overlay——图纸外部改动后旧读参不得作放行依据）。

## 5. 错误处理与诚实边界（§4 认可）

**忙窗与读取语义**（spike 根因生产化）：
- 每次 COM 读（ObjectName/参数/bbox/Count）经 `RetryBusy` 单调用重试——复用 AcadRuntime 既有 `IsBusyRejection`（0x80010001/0x8001010A + MissingMemberException 包装形态）。
- **空读≠不存在**：重试至非空；重试耗尽→该实体 `ReadOk=false`（不静默当"无此实体"），计入 `nativeReadFailed` 披露。
- **UNKNOWNNAME（0x80020006）=属性不存在**：该属性记缺省、**不重试**；逐属性独立判定，单属性缺不致整实体 ReadOk=false（除非 ObjectName 本身不可读）。

**附着与启动失败**：
- ROT 轮询超时（180s）/Gate1 耗尽→`CadRuntimeException`（明确"天正实例未就绪"），CLI 降级+警示，不假装读到空。
- /p 启动仅在本方无实例时；`VendorGuard.EnsureNoForeignInstance` 前置（禁并行铁律）；启动失败不残留进程（Process 跟踪+清理）。

**profile 感知启动两路径互斥**：COM 激活（既有）↔ /p 启动+ROT 附着（新增）由 `ReadNativeParams` 意图触发；一旦 /p 附着，本会话复用该 `_app`（不再 COM 激活）；Close/Quit 同走 RetryBusy（spike 实证 Close/Quit 也可被拒）。

**诚实边界**（不假装）：
- COM 读参在运行时内存，**不补全文件级解析**——census Yellow 不变，磁盘 TDb 仍是 UnknownEntity。
- 逐实体松绑**非整图 Green**；未读参/ReadOk=false/未命中实体**仍禁触**。
- 读参依赖天正实例在场（/p 启动或既有附着）；无天正环境→ReadNativeParams 返回空+警示，不报错（理解侧降级）。
- bbox 来自 `GetBoundingBox`（含天正自定义面），与文件级渲染 bbox 口径可能不同（弧/非均匀缩放近似）——标注来源为 COM，不与文件级混用。

## 6. 测试策略（§5 认可）

**单元测试**（fake seam，无真机）：
- `TDbClassMap`：TDbWall→Wall/未知→Unknown/大小写口径。
- `SemanticOverlay`：NativeEntities null 归一/UpsertNative 幂等（同 Handle 覆盖+ReadAt 更新）/JSON 往返（旧 overlay 无字段→空；新字段读写保真）/Save 原子写沿用。
- `NativeParamSeeder`：Class!=Unknown&&ReadOk→产 Annotation(NativeParameter 0.95)；Unknown 或 ReadOk=false→只记 NativeEntities 不产 Annotation；幂等重扫不重复。
- `NativeGateRefinement`：句柄∈NativeEntities&&ReadOk→放行；不在/ReadOk=false→拒；重读校验不一致→拒（stale 防护）。
- `AcadRuntime` 读参编排（fake COM seam，镜像现有 `AcadRuntimeTests` 的 ProgIdResolver/CreateComInstance 缝）：ObjectName 分类/TDb 读参/UNKNOWNNAME 记缺省不重试/忙窗重试耗尽→ReadOk=false/非 TDb 跳过。

**条件真机测试**（`Category=RequiresAutoCAD`，显式跳过无 AutoCAD，镜像 AcadLiveTests）：
- /p 启动+ROT 附着→ReadNativeParams 读真实 16#1116.dwg 副本：TDbWall LeftWidth=100/Height=3000/Volume=2.895 跨轮逐字闭合（spike 基准值）；ObjectName 分类 TDb vs AcDb 计数与 census 398 对照。
- 忙窗注入（启动后即读）→重试后全非空。
- CUIX/源件/副本 SHA 前后一致；tasklist 前后清零；自家实例 Close+Quit 清理。
- 逐实体松绑真机 E2E：TDbWall 句柄 NativeParameter 落 overlay→live 修改放行+读回；未读参实体仍拒。

**ISTQB 透镜**（合并前审查口径）：等价类（TDb/AcDb/未知 ObjectName）、边界（忙窗耗尽/UNKNOWNNAME 单属性 vs ObjectName）、异常双路径（重试成功/耗尽）、拒绝面全 reason 覆盖、100% 覆盖闸口判据（安全关键）。

**纪律**：真实标本 skip-gated 不入库；读参路径全程只读副本零写入；真机串行（xunit.runner.json + Collection）。

## 7. 范围外（明示不做，YAGNI）

- 切片 B：.NET Explode 中心线/符号块/XData 推导——另立切片。
- TDbSymbArrow 双通道拒 eInvalidInput 根因、块内零 extents 文本、块定义 55 TDb 实体、电气 TDbE* 真机核实——边界补齐留后续。
- 整图 Green 升级、阈值升 Green——本切片不做（用户裁定逐实体精准松绑）。
- 文件级 TDb 修改通道（Find 走 Doc.Entities）——松绑仅 live 通道，文件级 TDb 编辑非本期。
- 天正 profile 名/ARX 清单的自动发现——初版硬编码 TElec20V5（本机实证值），参数化留后续。
