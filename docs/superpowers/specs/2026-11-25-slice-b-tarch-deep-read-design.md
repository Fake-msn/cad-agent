# 切片 B：TArch .NET 深度读（Explode 中心线/符号块/XData）+ explode-native 放行 设计

> 日期：2026-11-25 ｜ 基线：main 49d0054（切片 A 已合并）｜ 路径：架构级（brainstorming 七节逐节用户认可）
> 上游实证：PR#28 探针（outputs/api-probe/tarch-dotnet-probe/report.md）——NETLOAD 托管桥经 ImpEntity/ImpCurve 取 GeometricExtents 341/343 + Explode 图元级几何/文本/符号块/XData。
> 产品方向：天正图纸完全解析（2026-11-25 用户决策）。

## 0. 用户裁定（四问 + 方案 + 七节认可）

- **Q1 NETLOAD 弹窗**：TRUSTEDPATHS 免弹窗（一次配置，2014+ 全版本有效；切片内只做预检+指引，实际配置变更由用户执行、执行前单独确认）。
- **Q2 推导范围**：三类全入（中心线/符号块/XData），分任务实现。
- **Q3 闸口语义**：理解 + 新放行证据（不止沉淀语义）。
- **Q4 放行证据形态**：**新增操作 kind `explode-native`**（桥内事务：炸开→校验→替换→SaveAs(.live.dwg)）+ 作为切片 A 句柄步骤的**附加证据源**（参数门 ∨ 炸开门）。
- **方案**：方案一——桥＝受控执行器（读 + explode-native 事务均在桥内）；否决"桥只读+宿主重放"（保真损失/非原子）与"免 COM 批处理"（黑盒/与附着互斥）。
- **用户修订**（七节评审中）：① 不设"绝不尝试 NETLOAD"，改为"允许尝试 + 看门狗 + 误拒明文指引"；② NETLOAD 等待总上限 **3600s**（人工授权动作，人在场未必及时）。

## 1. 架构与组件边界（§1 认可）

纯逻辑与 AutoCAD 内胶水分层：

| 组件 | 位置/框架 | 职责 |
|---|---|---|
| `CadAgent.TArchBridge.Protocol` | 新建，**netstandard2.0** | 请求/结果 **TSV** DTO 与解析/序列化（手写，零依赖）；net45 桥与 net8 宿主共用、可直测 |
| `CadAgent.TArchBridge` | 新建，**net45**（AcMgd/AcDbMgd 19.1 HintPath，`CADAGENT_ACMG_DIR`；引用缺失→编译空桩保持 soln 绿） | 两个**无参命令** `CADAGENT_TARCH_READ` / `CADAGENT_TARCH_EXPLODE_REPLACE`：从 `%CADAGENT_TARCH_CHANNEL%` 读请求、写结果+`.done`；递归 Explode、XData、extents、事务替换、SaveAs |
| `AcadRuntime` 扩展 | Adapters.Acad，net8 | 桥通道：TRUSTEDPATHS 预检（信息性）→ 启动注入 channel 环境变量 → NETLOAD（看门狗：3600s 总上限/20s 无进展检安全框一次指引/误拒归因 `native-load-denied`）→ SendCommand 命令 → 有界轮询 `.done`（命令 120s）→ 解析 |
| `TarchDerive` | Data.Semantic，net8 | 直墙中心线（平行面线对+TotalWidth 匹配→中线；弧墙证据不足→null）、符号块前缀候选披露、XData 并入既有 NativeEntities、NativeExplode 标注种子 |
| overlay 扩展 | Core | 新表 `ExplodeEvidence`（JSON 加性 null 归一）+ `Provenance.NativeExplode`（0.9） |
| CLI | Cli | `semantic scan --explode`（可与 `--native` 并用）；stdout/审计加性键 |
| 准入扩展 | Cli/Data | `NativeGateRefinement` 证据源扩为「参数门 ∨ 炸开门」；新 kind `explode-native` |

**边界**：桥不写审计、不写源件、不改配置；审计/读回/零写入裁决全在宿主；admission 只对已证实体放行；census Yellow 不变。

**构建约束**：桥 net45 使用 `Microsoft.NETFramework.ReferenceAssemblies` 包；AcMgd/AcDbMgd 经 `CADAGENT_ACMG_DIR` 环境变量 HintPath（不入库）；缺引用时工程编译唯一空桩文件（`NoAcadRefsStub`），解决方案始终可构建可测。

## 2. 桥协议（§2 认可）

**会话航道**：宿主 `/p` 启动时注入环境变量 `CADAGENT_TARCH_CHANNEL=<tempDir>`（避免命令行中文/空格引用脆弱，探针教训）。目录内按操作：

```
req-<id>.txt   ← 宿主写请求（id 单调递增）
res-<id>.txt   ← 桥写结果
res-<id>.done  ← 桥最后原子写（宿主轮询）
```

**请求**（TSV `key\tvalue`）：`op`(read|explode-replace)、`dwg`（当前文档期望名，防错图）、`handle`（explode-replace 必填，hex）、`maxdepth`（默认 4）、`liveout`（explode-replace 必填，`.live.dwg` 路径）。

**结果**（TSV 记录，首行 `status\tok|fail`）：
- `ENTITY\thandle\trxClass\twrapperType\tlayer\tminX,minY,minZ\tmaxX,maxY,maxZ\tfullyExplodable\t0|1`
- `PRIM\thandle\tdepth\trxClass\tgeom…`（line start/end、arc、circle、text 内容、block 名+插入点、hatch 面积；超过 N=64/实体→`PRIM-TRUNCATED`）
- `XData\thandle\tcode\tvalue`；`BLOCK\thandle\tname\tpos`
- `FAIL\thandle\treason`（eInvalidInput / 零 extents / 递归超深 / 非实体产物）
- explode-replace 额外：`REPLACED\thandle\tnewCount\tsavedPath`、`VERIFY\thandle\tbboxMatch\ttolerance`
- 桥内异常：`status fail` + `error\t<type: msg>`——**绝不空结果扮成功**

**时序**：宿主 `SendCommand("(command \"NETLOAD\" …)")`（STA 工作线程）→ SendCommand 命令名 → 轮询 `.done`（命令 120s / NETLOAD 3600s）→ 解析 → 失败即审计。错图（`dwg` 名不符）→ 桥拒绝。

## 3. 数据模型与 overlay 扩展（§3 认可）

```csharp
public sealed record ExplodePrimitive(string Kind, double[] Points, string? Text, string? BlockName);

public sealed record ExplodeEvidence(
    string Handle, string RxClass, string? WrapperType, string? Layer,
    double[]? Bbox,              // 6 点，与切片 A 同口径
    bool FullyExplodable,        // 递归到底全非 TDb 且产物>0
    int PrimitiveCount, int NestedTdbCount,
    IReadOnlyList<ExplodePrimitive> Primitives,   // 上限 64/实体
    IReadOnlyDictionary<string,string> XData,      // KEY→value（字符串化）
    string? CenterlineJson,      // 宿主推导：{kind:"line",p1,p2} 或 {kind:"arc",...}；null=证据不足
    DateTimeOffset ReadAt);
```

- overlay：`SemanticOverlay.ExplodeEvidence : List<ExplodeEvidence>`（JSON 加性；旧文件缺字段→null 归空；Upsert 键=Handle；原子写沿用）
- `Provenance` 末尾追加 `NativeExplode`（0.9）
- 标注种子：`FullyExplodable && TDbClassMap.Class≠Unknown`，且——参数面记录存在时要求 bbox 一致（容差 1e-3）；参数面记录缺失时不比较 bbox（仅凭炸开证据）→ Annotation(NativeExplode 0.9)；否则不落标注
- **XData 并入**：仅当该句柄已有 `NativeEntityParam` 记录时，以 `XData:<KEY>` 并入其 Params（无记录→只留 ExplodeEvidence 表，不建骨架）
- **TDbClassMap 保守扩类**：TDbDimension2→Annotation、TDbSpace→Room、TDbElevation→Annotation、TDbPolyPathArray→Unknown、**TDbOpening→Unknown**（门窗由 `$DorLib2D$` 候选披露）
- **符号块候选**：`TarchSymbolPrefixes`（`$DorLib2D$`→门窗候选、`$TWTSYS$`→设备系统符号候选）→ stdout `explodeSymbolCandidates` 加性披露；**不落 ≥0.8 标注**

## 4. 数据流（§4 认可）

### 4.1 读参路径（理解侧，`semantic scan --explode`）

```
CLI semantic scan --explode [--native]
  → ICadRuntime.ReadExplodeEvidence(string drawingPath)（接口默认空实现，Zwcad 不改）
      ├─ EnsureAppTArch(/p)（复用切片 A 启动+忙窗）
      ├─ TRUSTEDPATHS 预检（GetSystemVariable，信息性记录 trusted 状态）
      ├─ Open(path, ReadOnly=true) → 建 channel → 写 req → NETLOAD（看门狗）→ 命令 → 轮询 .done
      ├─ 解析 TSV → ExplodeEvidence 列表（FAIL 行→Partial 标记）
      └─ Close/清理 channel
  → TarchDerive.Enrich(overlay, evidences)
      ├─ Upsert ExplodeEvidence（幂等 by Handle；陈旧清理同 Seeder 先例）
      ├─ 种子规则 → Annotation(NativeExplode 0.9)
      ├─ XData 并入既有记录 Params（XData:KEY）
      └─ 直墙中心线推导 → CenterlineJson；符号块候选收集
  → overlay.Save；stdout/审计加性 explodeRead/explodeFully/explodePartial/explodeFailed/explodeSymbolCandidates
```

### 4.2 explode-native 执行路径（census Yellow 修改，新 kind）

```
run --live（Yellow）步骤 kind=explode-native params{handle}
  → 准入（NativeGateRefinement 扩展）：
     参数门（切片 A）Pass ∨ 炸开门：ExplodeEvidence 存在 && FullyExplodable && 本轮 READ 重读一致（RxClass+bbox）
     均不满足 → native-gate-refused（verdict：ExplodeNotRead / NotFullyExplodable / StaleExplode）
  → 执行（桥内单事务）：
     CADAGENT_TARCH_EXPLODE_REPLACE：校验 dwg 名 → 递归炸开到底 →
     校验产物非空/全非 TDb/bbox 并集≈原 bbox（容差 1e-3）→ 删除原 TDb → 插入基元 →
     db.SaveAs(liveout) → 结果 REPLACED+VERIFY；任何校验失败→Abort（不留半替换）
  → 宿主复核：桥 READ 确认原句柄消失、新基元计数=REPLACED
  → 审计 native-explode-step/committed/failed；源件零写入（Dispose 后 SHA 复核，切片 A finally 收口）
```

**附加证据**：切片 A 的 change-layer/move/delete 准入扩为「参数门 ∨ 炸开门」；改后读回仍按切片 A 期望（层名 OrdinalIgnoreCase / bbox 平移 / 句柄消失）。

## 5. 错误处理与诚实边界（§5 认可，含两次用户修订）

**信任与弹窗面**
- TRUSTEDPATHS 预检=信息性（记录 trusted 状态供诊断），**不阻断**尝试
- NETLOAD 在 STA 工作线程执行（SendCommand 同步阻塞不可避免），主逻辑并行看门狗：
  - 轮询 ① channel `.done`（500ms）；② acad 进程窗口枚举（节流 5s，匹配安全确认框作诊断增强）——任一进展刷新计时
  - **20s 无进展 → 无条件打印一次**明文指引（窗口检出与否均打印；检出时附"已检测到安全框"诊断）："若 AutoCAD 弹出安全确认框，请点击【加载】；如需永久免弹窗，将桥目录加入 TRUSTEDPATHS"——**继续等待**
  - **总上限 3600s**（常量 `NetLoadTimeout`）→ fail-closed + 审计 `native-load-timeout` + 指引
  - TRUSTEDPATHS 一次性配置（用户执行，执行前单独确认；模板，实际 `R19.1\ACAD-<id>\<profile>` 以本机注册表为准）：
    ```
    读取现值: reg query "HKCU\Software\Autodesk\AutoCAD\R19.1\ACAD-<id>\Profiles\<profile>\Variables" /v TRUSTEDPATHS
    追加桥目录（保留原值，引号分号分隔）:
      reg add "HKCU\...\Profiles\<profile>\Variables" /v TRUSTEDPATHS /t REG_SZ /d "\"<原值>;<桥目录>\"" /f
    回滚: reg add ... /d "<原值>" /f（或删除该值恢复默认）
    ```
    生效需重启 AutoCAD；SECURELOAD 保持默认 1，不降级
- **误拒/无权限**：NETLOAD 返回后桥命令未注册（未知命令）→ 明文排障清单（误点【不加载】/TRUSTEDPATHS 未含/SECURELOAD 阻止）+ 审计 `native-load-denied`
- 桥 DLL 缺失/空桩 → 明确"桥未构建"错误（先于 NETLOAD）

**桥执行面**
- 桥内异常→`status fail`+`error` 行，绝不空结果扮成功
- 递归超深/产物含非实体/bbox 并集不匹配→FAIL+`FullyExplodable=false`（部分证据诚实披露，可进表不放行）
- 命令 `.done` 超时（120s）→宿主 fail-closed+审计；`SendCommand` 异常同路
- 错图（dwg 名不符）→桥拒绝

**事务与回滚**
- explode-native：校验全过才删原实体；Commit 前失败→Abort；Commit 后 SaveAs 失败→源件未动、`.live.dwg` 可能半写→宿主席 SHA + `liveSha-unverifiable` fail-closed（切片 A 先例）
- 桥 SaveAs 仅写宿主给定 `liveout`；桥不写审计、不写源件

**诚实边界**
- TDbSymbArrow 双通道 eInvalidInput→永久 FAIL 披露
- 零 extents 块内文本/炸开 0→Partial 披露
- 弧墙中心线证据不足→CenterlineJson=null
- 符号块仅候选披露
- explode-native 新基元**无原生语义**（普通 CAD 图元）——审计与 overlay 如实记录"降级为普通图元"
- census Yellow 不变；炸开证据是附加通道，不改文件级解析结论

## 6. 测试策略（§6 认可）

**单元（net8，全 seam，无 AutoCAD）**
- Protocol TSV 往返（中文路径值/`\t` 转义/截断标记/畸形行显式错误）
- 桥通道编排（fake SendCommand/文件缝）：req 序号、`.done` 轮询、命令 120s、NETLOAD 3600s、误拒→native-load-denied、channel 清理
- TarchDerive：中心线（对匹配/单线/弧对不足→null）、XData 仅并入既有、符号块候选不落标注、Annotation 种子规则
- NativeGateRefinement 炸开门矩阵（证据缺/未全炸/bbox 漂移/重读不一致/全过）
- CLI：`--explode` 加性键与旧键逐字兼容；`explode-native` 准入（非白名单回落/无证据拒/证据全过放行）
- overlay：ExplodeEvidence JSON 加性/null 归一/幂等

**条件真机（`Category=RequiresAutoCAD`+`Collection("AcadLive")`，SkippableFact 四态）**
- 前置守卫：TRUSTEDPATHS 含桥目录 + 桥 DLL 存在 → 否则 Skip（原因含指引）
- T1 READ：16#1116 副本→343 条 TDb、21E00 extents 与切片 A 基准逐字（120300/130100/3000）；TDbSymbArrow FAIL 如实；XData HYDRANT_RADIUS=28
- T2 递归：TDbSpace NestedTdb>0 且 depth>0 产物记录
- T3 explode-native：副本上 21E00→原句柄消失、新基元数=REPLACED、bbox VERIFY 过、`.live.dwg` 存在、源件 SHA 前后一致
- T4 误拒路径：不伪造自动化，仅文档化
- 金丝雀全套沿用

**ISTQB 透镜**：错误面等价类（超时/误拒/桥崩溃/递归超深/非实体/错图）、拒绝面全 reason、Skip 诚实、加性纪律、无 `[Ignore]`。

## 7. 范围外（§7 认可）

弧墙中心线复杂形态；TDbSymbArrow 根因；零 extents 文本几何恢复；块定义内部 55 TDb（仅模型空间）；电气 TDbE\* 逐类核实；explode-native 批量/多实体；TRUSTEDPATHS 自动配置写入（含 `cadagent tarch trust` 辅助命令）；符号块→语义自动落标注；Autoloader 全局加载；多版本 AcMgd（按 19.1）；explode-native 后 overlay 语义迁移。

### 7.1 更正注记（T11 收口：实现事实与设计措辞差异，2026-11-25）

> 追加式更正（旧文 0 删）：以下条目在实现/真机实证中推翻或收紧了本文前六节措辞，实施以更正后口径为准；对照记录见 2026-11-20 主设计 §12「切片 B 收尾闭环」块。

- **(a) 桥通道目录＝实例级，非"每调用一目录"**（推翻 §4.1 流程序列）：AutoCAD 子进程只在 `CreateProcess` 时**快照**环境变量 `CADAGENT_TARCH_CHANNEL`；若每次调用新建目录，第二次调用写入的是子进程看不见的新目录（第二次桥操作必超时）。落地：`EnsureChannelDir` 在本实例**首次启动前**创建目录并注入变量，READ 与 EXPLODE_REPLACE 全程复用同一目录；目录删除与环境变量还原只在 `Dispose`（实例生命周期＝桥会话生命周期）；写请求前先放弃目录内"有 req 无 done"的历史请求（否则桥 `NextPending` 恒拾最早旧请求）。原 §4.1「Open → 建 channel → … → 清理 channel」的每调用形态不可行。
- **(b) explode-native bbox 判据＝包含性＋叶数一致，非"并集≈原 bbox 相等"**（更正 §4.2「bbox 并集≈原 bbox（容差 1e-3）」）：相等判据在真机必败——墙体原实体带高度（Z≈3000）而炸开面线在 Z=0，且面线 Y 止 130000 而 extents Y=130100（21E00 实证）。落地：叶集 bbox 并集 ⊆ 原 extents 外扩 1e-3（**仅比 X/Y**），且叶集非空、叶数>0、**替换叶数＝校验叶数**（同一次校验递归的确定性交叉核对）；任一不过→事务内 Abort，不留半替换。
- **(c) T2 递归措辞不可表达 → 收窄为条件样本披露**（更正 §6「T2 递归：TDbSpace NestedTdb>0 且 depth>0 产物记录」）：Core 模型内不可表达——`ExplodeEvidence.NestedTdbCount` 只统计**递归失败残留**（桥内"递归超深/非实体产物"的失败叶，成功路径恒 0），`ExplodePrimitive` 无 depth 字段。落地：证据中存在任一 NestedTdbCount>0 的行→断言其 >0 且 `FullyExplodable=false`（自洽），标本无此类行→Skip（如实，不伪造残留）；"嵌套全量炸开"改由**桥侧递归证据**承担（`PrimDto` 带 depth 入 TSV；真机 T1 fully=284/343 即递归到底成功计数），T2 仅保留样本条件披露，不单独作递归充分性证据。
- **(d) TRUSTEDPATHS 读取＝注册表优先，非先建 COM 实例**（更正 §5「TRUSTEDPATHS 预检」与 §4.1 步骤）：原 `GetSystemVariable`/COM 实读须 `/p` 拉起整实例，而启动初始化期 `Quit` 被忽略 → 孤儿 acad.exe（真机实证）。落地：`AcadRuntime.ReadTrustedPathsFromRegistry`（HKCU `…\R19.1\ACAD-*\Profiles\TElec20V5\Variables` 的 REG_SZ，只读、零实例、零 COM）优先；注册表无该值才回退 COM **文档面** `GetVariable("TRUSTEDPATHS")`（活动文档取用同走忙重试）。SECURELOAD 仍保持默认 1（不降级）。
- **(e) "不支持硬件加速"信息框关窗＝环境新增事实，非对 §5 弹窗面的收敛**（§5 弹窗面原文保持有效）：该框是本机核显指令集限制的启动层**信息性**提示（非 AutoCAD 缺陷通用现象），**用户已永久关闭**弹出；`AcadWindows.DismissDialogs`（本方 PID + 标题子串「不支持硬件加速」+ 仅 `PostMessage(WM_CLOSE)`，绝不按键/点击/结束进程）保留为机器特定纵深防御。残留风险（复审在案）：标题子串若用户**主框架**标题恰含该短语可能被命中（缓解：仅本方 PID、仅 WM_CLOSE、窗口类校验延后）。
- **附注：SendCommand 语义补充**（§2「时序」）：桥**全部**发送点（NETLOAD／注册探针／正式命令）经同一缝走忙重试（`RPC_E_CALL_REJECTED` 0x80010001，TArch 启动窗真机实证；被拒＝命令未执行故重试无副作用，与既有 30s 预算共享）；"是否已注册"改用**探针握手**判定（写 `op=read`/`dwg="__probe__"` 请求→桥因图纸名不符必然回 `status fail`+done；10s 内无 done→`native-load-denied`），不依赖 SendCommand 回显（ActiveX 口径返回 void）。
