# CAD 施工图智能体终端 设计规格

日期：2026-11-20
状态：设计定稿（六节逐节经用户确认），待实现计划
场景：大型设计单位制图规范化提效（非监理审图）

---

## 1. 背景与目标

开发一款通过 CLI 驱动 CAD 绘制/完善工程施工图的智能体终端。总纲：**过程可审计、步骤可定义、结果可复用**。

六项硬要求：

1. 读懂既有图纸标注符号，可在既有图纸上补充完善
2. 对绘制不明确的区域能提示并纠偏
3. 修改过程留痕并可撤回
4. 明确审查责任主体人员
5. 按既有制图标准保障规范，可创建专属自定义 skill
6. 理解天正等插件套件的标注；无法完整理解时要求用户转 T3 格式导入，绝不拿信息残缺的图纸直接操作

核心工作模式：既有图纸的审查、纠偏与补充完善，而非从零绘制。第二期基准任务：建筑底图上 AI 布灯点位。

## 2. 决策基线（调查证据支撑）

| 维度 | 决策 |
|---|---|
| 智能体形态 | 混合式：LLM 主导读图/理解/规划/迭代，确定性引擎执行并留痕 |
| 技术栈 | C#/.NET 8 一体化（方案 B，用户拍板） |
| 开源底座 | ACadSharp（MIT）：DWG/DXF 读入、内部模型、DXF 写出 |
| 可视化 | Avalonia（MIT）自建轻量编辑器 + 审计可视化 |
| 厂商关系 | 厂商无关但厂商一等公民：适配器层接入中望/AutoCAD/天正，不锁死任何一家 |
| 开发测试环境 | 本机 ZWCAD 2025，COM 无界面驱动已真机验证（启动→建图→画线→退出全通过） |
| 天正策略 | 三级准入闸口（信息完整度判据）+ 语义重建兜底 + 解析支持矩阵渐进点亮 |
| 责任认定 | Windows 系统账号 + 本地角色配置（绘制/审查/批准），操作自动盖章 |
| 标准覆盖 | 国标底线内置 + 院标自定义 skill；规则效力分级，解释权归人 |
| 第一期切片 | 审图+纠偏闭环（准入→读图→问题清单→纠偏→留痕撤回→审查签认→保真导出） |
| 集成方向 | 出站：项目通过适配器接入第三方，不对外暴露服务接口 |

调查关键事实（来源见附录）：

- AutoCAD 开放 AutoLISP/COM/.NET/ObjectARX/VBA 五套接口；天正无公开对象级 API，其 TCH_* 自定义对象在无天正环境时仅剩代理实体
- T3 导出本质是炸开为普通图元，几何完整但组件语义不可逆丢失；图层名保留
- 中望官方提供 LISP/VBA/COM/.NET/ZRX 五套接口；本机 COM ProgID `ZWCAD.Application`（2025）注册可用
- ACadSharp 社区已逆向天正 tchkernal.arx：PR #1275 已合并（天正文本对象）；PR #1337 推进中（TCH_TEXT/TCH_MTEXT/TCH_DRAWINGNAME，样例 13/13 与 T3 导出一致）；墙/门窗等建筑对象未解析；仅 AC1021 布局验证，AC1027 布局不匹配（维护者明示版本风险）
- 本机中望为试用授权（LocalTrial_*.asr），正式使用前需确认期限；开发机无天正授权

## 3. 总体架构

```
CadAgent（.NET 8 解决方案）
├── CadAgent.Cli          终端入口：REPL + 单条命令；拉起可视化窗口
├── CadAgent.Core         步骤引擎 / 审计 / 撤回 / 责任 / skill / 规范校验（厂商无关）
├── CadAgent.Llm          LLM 编排：读图/规划/迭代；只产出步骤计划 JSON
├── CadAgent.View         轻量编辑器 + 审计可视化（Avalonia）
├── CadAgent.Data         ACadSharp 数据层：读入 / 内部模型 / DXF 写出 / 准入检测
├── CadAgent.Adapters.*   厂商适配器（一等核心组件）：Zwcad COM 首个 / AutoCAD COM / 天正运行时预留
└── CadAgent.Standards    国标规则模块 + 院标 skill 包（YAML）
```

四条硬边界：

1. **厂商无关，但厂商是一等公民**。Core/View/Data 不引用厂商库；不锁死任何一家厂商/版本。生产配置建议至少一个厂商运行时在场（中望或 AutoCAD 均可）。无厂商时降级纯开源模式，界面显式标注"保真度未验证"。
2. **LLM 主导作业，引擎审计执行**。LLM 读图（实体数据+渲染图像）→理解意图→生成操作步骤→经引擎执行并读回结果→迭代或提请人工确认。唯一约束：一切操作（LLM 的、手动的、skill 的）必经审计管线，无旁路。产品是智能体终端，不是 CAD 工作台。
3. **保真双闸口**。导入：ACadSharp 解析 + 厂商运行时打开验证（实体数/关键几何比对）。导出：内部模型→DXF→厂商 CAD 另存 DWG（厂商写入器兜底）；无厂商时交付 DXF，或 ACadSharp DWG 写入+往返校验+风险标注。内部工作格式 DXF（开源读写最成熟的交集）。
4. **真文件回归**。工作环境真实图纸（多版本 AutoCAD/天正/中望）建保真测试库，导入导出每版必测。

视图定位：基础手动编辑（选择/移动/删除/画线/文字/改图层属性/插入符号图块）+ 修改高亮 + 审计时间线回放 + 前后对比 + 审查签认 + 图层纠偏 + 规则裁定。复杂流程走 skill。手动操作 = 用户生成的步骤，同走引擎与审计管线。

外部修改检测：工作人员在厂商 CAD 里改图保存后，文件哈希比对发现变更→重新读入→审计链记录"外部修改"事件→受影响语义区域标记"待复核"，复核前拦截相关操作。

## 4. 核心数据流

### 4.1 七步闭环

```
①准入 → ②读图 → ③问题清单 → ④纠偏计划 → ⑤执行留痕 → ⑥审查签认 → ⑦保真导出
```

- **①准入**：三级闸口（见 4.2），记录准入事件（文件哈希、来源、操作人、时间）
- **②读图**：ACadSharp 解析→内部模型（实体/图层/块/文字/标注）→语义重建（见 4.3）→LLM 拿结构化摘要+渲染图像形成理解报告
- **③问题清单**：规则引擎扫描（硬性问题）+ LLM 语义判断（不明确区域）→位置/严重度/建议修法/标准来源，视图定位高亮
- **④纠偏计划**：用户勾选问题→LLM 生成步骤计划 JSON→schema 校验+角色权限检查→视图预览，人工确认后执行
- **⑤执行留痕**：事务前快照→逐步执行→每步盖章→每步读回验证→视图实时高亮；失败停在当步，可整体回滚
- **⑥审查签认**：前后对比→审查角色逐项/批量签认或驳回→盖章；驳回走快照回滚（回滚也留痕）
- **⑦保真导出**：DXF→厂商另存 DWG→往返校验→交付物+审计包

三个统一：手动编辑、skill 流程、LLM 作业走同一条⑤⑥管线；撤回统一靠快照；第二期布灯只是④的计划内容变化，管线零改动。

### 4.2 三级准入闸口（判据＝信息完整度，非"是否 T3"）

| 级别 | 条件 | 处置 |
|---|---|---|
| 🟢 放行 | 普通/T3 图纸（几何完整）；或原生天正图且图中所有对象类均在已验证解析矩阵内 | 正常作业；T3 仍须语义重建（见 4.3） |
| 🟡 受限 | 部分可解析（如文本可读、墙不可读） | 只读审查已解析部分；未解析区域标禁触区，依赖缺失语义的操作被引擎拦截；全量编辑建议转 T3 |
| 🔴 拒收 | 仅代理图形/布局版本未验证/无法解析 | 拒收 + T3 转换指引（天正"图形导出→T3"操作步骤） |

解析支持矩阵（对象类 × 天正版本 × DWG 版本）并入厂商支持矩阵体系。升级证据＝真实天正图"原生解析 vs 同图 T3 导出"交叉比对，逐类逐版本点亮。策略：不赌社区进度，成果来一批验证点亮一批。未知实体全程保留原名，导出往返校验确保不静默丢对象。

### 4.3 语义重建层（T3/炸开图纸的兜底）

三路信号叠加，从几何反推语义：

1. **图层/块名约定**（确定性）：T3 保留天正标准图层名（WALL/WINDOW/DOOR/COLUMN/AXIS/PUB_* 等）
2. **几何模式启发式**（确定性）：墙厚间距平行线对→墙段；墙体开口弧线+线段→门；点划线网格+轴号圆圈→轴网；闭合多段线+填充→柱/房间
3. **LLM 视觉推理**（概率性）：渲染图+实体数据→房间边界与功能、门窗位置、标注体系

产出**语义叠加层**（一等交付物）：每个语义标注带置信度+来源（原生对象/图层推断/几何推断/LLM 推断/人工确认）；低置信区域提请人工确认或纠正；随项目持久化、版本化、入审计链；一次重建确认，后续所有任务复用。

操作与语义需求挂钩：每类操作声明所需语义（布灯需房间边界+功能，改标注需标注体系）；引擎执行前查叠加层覆盖度与置信度，不足则提示补确认，绝不盲操作。

诚实边界：推断语义≠原生语义。T3 上可做几何级作业（画线/插图块/改文字）；构件参数级操作（如参数化改墙厚）明确告知需原生文件+天正运行时适配器，不假装能做。

### 4.4 不明确图层/块的人工纠偏流程

真实图纸存在大量绘图者自定义图层/块名，标准库只是起点。

- **检测**：图层/块名与"国标图层库+院标 skill 别名库"比对，未命中进不明确清单，附证据（该层实体几何统计、渲染缩略图、名称线索）
- **纠偏交互**：点击条目→视图隔离显示该层全部实体（其余淡出）→LLM 可建议映射但必须人工确认→指派语义类别（墙/门/轴网/标注/辅助线/忽略…）→置信度标"人工确认"（最高级）；未知图块同理（显示块内容→指派符号语义）
- **持久化与复用**：项目级映射表随语义叠加层存储、入审计链；可提升为院标 skill 别名库，下个项目直接命中
- **禁触**：未映射图层拦截语义依赖操作；几何级操作可做但带警示
- **双重身份**：自定义图层同时列为图层规范化问题项，可选执行"图层归正"修正（重命名/合并到院标图层），走同一条审计+撤回管线

## 5. 审计 / 撤回 / 责任

### 5.1 审计日志

- SQLite WAL 模式，哈希链式追加（每条含前一条哈希，篡改断链，可独立验链），只增不改
- 记录字段：序号、时间戳、操作人（Windows 账号）+角色、会话、来源（LLM/手动/skill/外部）、操作类型、目标实体句柄、参数 JSON、前后状态哈希、快照引用、所属计划
- 覆盖全事件：准入、图层映射确认、规则裁定、计划批准、每步执行/失败、撤回、审查签认/驳回、导出、外部修改检测、LLM 用量

### 5.2 存储与并发模型

- 每项目独立 DB 文件（项目间零竞争）
- 每项目单写入通道（审计事件经串行化队列写入；哈希链头无竞争是链完整性的正确性要求）
- 图纸级事务锁：同一图纸同时只有一个活动事务；多图纸可并行
- 核心只依赖 `IAuditStore` 接口；后期服务端化可换 PostgreSQL 实现，审计逻辑/哈希链/审计包格式不动

### 5.3 责任绑定

- 角色配置（本地 roles 文件）：Windows 账号→绘制/审查/批准
- **LLM 作业的责任人＝批准计划的人**：计划确认时盖章，其下每步记录"执行者=引擎，责任人=计划批准者"
- 职责分离：绘制者不能自审自批（引擎在签认前强制校验角色）；手动编辑按操作人角色盖章
- 外部修改：哈希比对发现→记录事件+检测人→受影响语义区域"待复核"，复核前拦截相关操作

### 5.4 撤回

- **事务级**：每个批准的计划/编辑批次执行前快照图纸文件；撤回=恢复快照（跨会话可靠，不依赖 CAD undo 栈）
- **步级**：会话内模型 undo 栈细粒度回退；会话关闭后事务快照兜底
- 撤回本身是审计事件（谁撤的、撤到哪、为什么）；驳回项自动走撤回

### 5.5 审计包

导出时打包：日志+快照+签认记录+语义叠加层+图层映射表+规则裁定集+验链工具。第三方不依赖本产品即可验证"谁改的、改了什么、谁批的、链有没有断"。

## 6. 规范引擎与 skill 机制

### 6.1 双层规范引擎

- **国标底线**（内置 C# 规则模块）：GB/T 50001 房屋建筑制图统一标准、GB/T 50104 建筑制图、GB/T 50786 电气制图等，编码为对内部模型+语义叠加层的确定性检查
- **院标 skill**（YAML 包）：图层命名与别名、线宽约定、标注样式、符号图库、图签规则、项目专属约定
- 检查时点：③问题清单生成时；⑤执行后回归校验（修正不得引入新违规）

### 6.2 规则效力分级与解释权归人

国标是给人读的，软规则（推荐）被当硬规则执行会导致输出偏离需求。决策权归用户。

规则 schema（每条必带）：

```
source:  标准号 + 条款号 + 原文摘录（如 GB/T 50001-2017 §4.2.3）
force:   强制 / 推荐 / 参考（条款级而非标准级）
scope:   适用条件（图类/专业/项目类型）
```

执行决策三分：

| 效力 | 引擎行为 | 用户权力 |
|---|---|---|
| 强制 | 拦截，列为必须处理项 | 修正，或显式豁免（盖章：谁、依据什么理由） |
| 推荐 | 只呈现建议，不自动执行 | 采纳/忽略/修改后采纳；忽略是正常决策不是违规 |
| 参考 | 仅展示 | 自由处置 |

- **引用义务**：问题清单与步骤计划中每条约束必附标准来源段落；LLM 可附"如何影响制图"的解释，明确标注为"LLM 解读"，与标准原文分离
- **解读纠偏与沉淀**：用户纠正 LLM 理解偏差→记录解读裁定（来源条款+裁定+理由，盖章）→可选沉淀为专属规则（项目/院 skill）或公开规则（共享库，带版本与审核）；裁定入审计链
- 院标 skill 规则同样必须声明效力与来源；LLM 起草 skill 时提取条款引用与效力分级，人工审阅重点核效力，干跑前拦住分级错误

### 6.3 skill 包结构

```
skill.yaml     元数据：名称/版本/适用专业与图类/依赖
rules/         校验规则（含 source/force/scope）
steps/         参数化步骤模板（声明式操作序列）
symbols/       符号图块库（DXF 块 + 语义类别 + 插入规则）
aliases/       图层/块别名映射（人工纠偏成果沉淀）
```

### 6.4 自定义 skill 创建流程

自然语言描述或院标文档投入→LLM 起草 skill YAML→人工审阅→样例图干跑验证→激活。项目确认过的图层映射可一键提升进院标 skill 别名库。

### 6.5 可复现性契约

每步执行记录 skill 版本。**同一图纸 + 同一 skill 版本 + 同一计划 + 同一解读裁定集 = 同一结果**。这是"结果可复用"的严格定义，也是审计复核基础。黄金测试自动化守卫此契约。

### 6.6 布灯 skill（第二期基准）

symbols/ 灯具图块；steps/ "房间识别→按功能布点→插入图块→标注"模板；rules/ 间距/布置校验。LLM 从语义叠加层取房间功能填参数，引擎确定性执行。验证架构泛化能力：同一条管线，不换架构。

## 7. 交互形态

- **CLI 双入口**：交互式 REPL（自然语言+命令混用，智能体主入口）；单条命令可脚本化（`intake` / `check` / `run` / `export` / `audit verify` / `llm …`）
- **视图**（Avalonia）：画布渲染、图层面板、问题清单、计划预览、前后对比、审计时间线回放、审查签认、图层纠偏、规则裁定——全部人工交互的落点
- **出站集成**：项目通过适配器层接入第三方（CAD 厂商 COM/.NET、LLM 服务、未来其他工程软件接口）；新增第三方=新增适配器实现。CLI 是用户与脚本入口，不对外暴露服务接口
- **LLM 可降级**：未配置/不可用时确定性功能照常（准入、规则校验、手动编辑、参数化 skill 执行），语义推断/计划生成明示不可用

## 8. LLM 接入、鉴权与配置

### 8.1 配置模型（provider profile）

```
profile: 名称（如"云端主力"/"本地保密"）
  endpoint:   OpenAI 兼容 base URL
  model:      模型名（自动抓取，见 8.3）
  auth:       鉴权方式（默认 API Key Bearer；处理器可扩展 OAuth/订阅授权）
  caps:       能力声明（视觉支持——读渲染图必需）
  limits:     超时 / 单次 token 上限 / 会话预算 / token 套餐
  dataPolicy: 数据出境策略（仅本地 / 允许云端）
```

多 profile 并存、按会话/任务切换；读图任务强制视觉能力，不满足的 profile 不可选。

### 8.2 密钥安全（硬规则）

- API Key 不落明文配置文件：Windows DPAPI（当前用户 scope）加密存储，配置只存引用；支持环境变量注入（脚本/CI）
- 密钥永不进审计日志、审计包、错误报告（日志管线统一脱敏）
- 配置导出不含密钥；`http://` 端点警示（localhost 本地模型除外）

### 8.3 配置体验（一步到位）

- 填好 endpoint+key 后自动调 `/v1/models` 抓取模型列表，下拉选择；视觉能力经元数据+实弹探针判定
- 内置推荐预设（主流 OpenAI 兼容服务商+本地 Ollama）：选预设→粘贴 key→自动抓模型→推荐默认款→连通探针→完成
- 本地零配置：自动探测 localhost 常见端口（Ollama 等），探到即生成"本地保密"profile，无 key 直接用
- 首次启动向导给"推荐配置"按钮
- CLI：`cadagent llm add / list / test / use`；`test` 做实弹连通性+能力探针；视图设置面板为同一套配置的图形化入口

### 8.4 token 套餐与预算

profile 可设 token 套餐（总额度、重置周期、会话/单任务上限）；用量计量实时对照套餐，接近耗尽预警，超额按策略降级或停用。订阅制鉴权（OAuth）处理器预留，第一期落 API Key。

### 8.5 数据出境管控

- 图纸内容（实体数据+渲染图）发往哪个端点显式受控：profile 级数据策略（仅本地/允许云端）
- 首次向云端端点发送图纸数据前明确告知并确认；项目级可设"仅本地模型"硬约束
- 本地模型 profile（Ollama 等 localhost）是一等公民

### 8.6 用量与责任

每会话 token 预算帽+用量计量显示；审计记录每次计划的 profile/模型/token 用量——谁批准的计划、花了多少算力，可追溯。

## 9. 错误处理（全部入审计）

| 故障 | 处置 |
|---|---|
| 步骤执行失败 | 停在当步，保留现场+快照，结构化错误回传 LLM 重规划或提请人工；绝不静默继续 |
| COM/厂商崩溃 | 适配器健康检查+瞬态重试；崩溃后重启适配器，事务从快照恢复；COM 操作 STA 串行化 |
| 解析异常 | 未知实体保留原名不丢弃；文件损坏准入即拒收+诊断报告 |
| LLM 输出非法 | schema 拒绝→带错误反馈重试（次数上限）→升级人工；超时/成本有帽 |
| 保真闸口不过 | 导出拦截，出具差异报告 |
| 密钥/端点故障 | 探针报错定位（网络/鉴权/模型不存在），LLM 功能降级，确定性功能不受影响 |

## 10. 测试策略

- **单元（TDD）**：规则引擎、哈希链、步骤引擎、语义启发式，fixture 驱动
- **集成**：中望 COM 真机冒烟（开→改→存→验），本机跑，标注"需 CAD 环境"
- **保真回归库**：工作环境真实图纸（多版本 AutoCAD/天正/中望）；导入导出往返比对（实体数/关键几何）；天正原生解析 vs T3 交叉验证 fixture——厂商支持矩阵的证据源
- **黄金测试**：同图+同 skill 版本+同计划+同裁定集→输出逐字节一致
- **视图**：关键流程手动测试规程+截图基线（UI 无法全自动化，如实标注）

## 11. 厂商支持矩阵（一等交付物）

应用内适配器状态面板 + 随产品清单。条目：厂商/版本范围/适配器版本/验证状态（已验证/兼容未验证/不支持）/验证日期/测试图纸覆盖。

初始状态：

| 厂商 | 版本 | 状态 | 证据 |
|---|---|---|---|
| 中望 ZWCAD | 2025 | 已验证（COM 基础驱动） | 2026-11-20 本机真机探针：无界面启动/建图/画线/退出 |
| AutoCAD | 各版本 | 兼容未验证 | 适配器待实现 |
| 天正 | T20/T30 等 | 不支持（运行时适配器预留） | 开发机无授权 |
| 天正原生解析 | AC1021 文本类 | 社区已合并，待本项目验证点亮 | ACadSharp PR #1275/#1337 |

规则：未验证版本打开文件即警示；"已验证"必须有真机测试证据；矩阵状态由保真回归库证据驱动升级。

## 12. 分期与开放项

**第一期（审图+纠偏闭环）**：准入闸口（含天正检测）→读图+语义重建+图层纠偏→问题清单（国标底线+示例院标 skill）→LLM 纠偏计划→执行留痕+两级撤回→审查签认→保真导出+审计包；CLI REPL+单条命令；Avalonia 视图（渲染/基础编辑/审计回放/签认/纠偏）；LLM 配置全套；中望 COM 适配器；厂商支持矩阵。

**第二期基准**：建筑底图 AI 布灯（语义重建复用+布灯 skill）；AutoCAD COM 适配器；天正原生解析按社区进度验证点亮。

**开放项（不阻塞设计，阻塞部分实现）**：

- 院标文档/图层规范/符号图库：用户尚未提供；skill 机制先用示例院标跑通，真实院标到位后编码替换
- 保真回归测试库：需用户从工作环境提供真实图纸（多版本 AutoCAD/天正/中望，含原生天正图及其 T3 导出配对）
- 中望授权：本机为试用版，正式开发/测试前确认期限或正式授权
- 天正原生解析：跟踪 ACadSharp issue #1131 / PR #1337 进度，不赌时间表

**计划1执行裁定遗留（后续计划必须承接，2026-11-20 基础内核完工时记录）**：

- **审计链外部锚（计划3审计包设计约束）**：当前哈希链无外部锚——尾截断与全链重算伪造不可被 VerifyChain 检测；VerifyChain==true 仅意味着"未部分篡改"。计划3须 DB 外持久化头哈希/周期签名 checkpoints。
- 审计边界残余：rollback 的 Load 失败（先于文件名匹配检查）与身份/roles.yaml 解析失败仍走通用 catch 无审计记录——计划2补全。
- 损坏的已存在 .dwg 工作副本现在永久 Load 失败（持久状态裁定的有意后果）——再生/修复 UX 属计划2/3。
- 哈希投影未含 MText 内容（MText : Entity，非 TextEntity）；投影外属性变更不入哈希——随处理器扩展补齐。
- handle 契约：处理器仅接受数字 JSON，Summarize 暴露字符串——计划3 LLM 计划编写层统一。
- 跨进程审计写入串行化（BEGIN IMMEDIATE 或项目级 mutex）——计划3；当前每实例锁+PK 显式冲突，无静默链分叉。
- Yellow 闸口语义级拦截（禁触区/操作语义需求检查）——计划2；计划1仅 run 边界拒 Red + Yellow 警示。
- CA1416 Windows 平台 API 构建警告——平台守卫或 TargetPlatform 标注，计划2顺带。
- 真实 .dwg 夹具已验证可行（ACadSharp 3.8.35 DwgWriter→DwgReader 往返）——计划3保真库可直接用。

**计划2a完成（语义理解层，2026-11-20 记录；端到端闭环测试 scan→map→show→rescan→audit verify→run 全绿）**：

- 已交付：语义叠加层（semantic.json，版本递增）、天正约定库+图层推断（LayerInference 0.8）、墙体平行线对/轴号圆+数字/门弧启发式（GeometryInference：墙对/轴号 0.6、门弧 0.5）、组合扫描器+不明确图层证据清单、CLI semantic scan/show/map（map 人工确认 HumanConfirmed 1.0，需 --drawing 取实体句柄）、操作级语义闸口（StepEngine 加性可选参数，计划1行为零改动）。
- **操作级语义闸口已落地**：run 边界按操作语义需求检查叠加层；实体级区域覆盖检查（禁触区精确到实体/坐标范围）留计划3/4。
- **LLM 推断留计划3**：Provenance.LlmInference 枚举已预留，当前无任何 LLM 推断路径。
- **启发式 O(n²) 空间索引留后续**：墙对检测按角度分桶后两两比对，大图性能待空间索引优化。
- **约定库不冒充 GB 权威**：图层映射来自天正惯例+常见自定义别名，属推断信号（0.8），非国标判据。
- **处理器语义需求声明随计划3布灯处理器引入**：当前处理器语义需求第一 slice 全空（机制先行，测试处理器验证闸口）。
- **检测器守卫模式三处重复待共享 helper**：WallPair/Axis/DoorArc 各自实现"高置信度不降级"守卫，待提取共享。
- **map 空图层静默成功待边界打磨**：对不存在/无实体的图层执行 map 当前静默成功（写入映射但无标注），错误提示留后续。

**计划3设计承接（2a 最终 whole-branch 审查修复轮记录，2026-11-20）**：

- **ISemanticGate 需上下文重载（实体级检查）**：当前 Check(required) 仅做叠加层存在性检查；计划3布灯处理器需实体级/区域级校验（禁触区精确到实体/坐标范围），接口须加带图纸上下文的重载。
- **semantic.json 需独立 schema 版本**：现 Version 是写计数器（每次 Save +1），不是格式版本；计划3引入独立 schemaVersion 字段以支持格式演进/迁移。
- **Upsert 守卫随 LLM caller 落地**：HumanConfirmed=1.0/证据必填等 Upsert 守卫当前无调用侧强制；计划3 LLM 推断路径接入时一并落地。
- **map 空图层静默成功在 LLM 建议映射时升级为拒绝**：人工 CLI map 空图层暂容忍；计划3 LLM 建议映射路径必须拒绝不存在/无实体的图层（防幻影映射）。

**计划2b-i完成（规范引擎与裁定，2026-11-20 记录；端到端闭环测试 scan→map→check→adjudicate 升级/豁免→run+回归警告→audit verify→裁定持久化复用 全绿，全套件 162 测试）**：

- 已交付：规则模型+YAML 加载校验（Rule/RuleSource/CheckSpec）、裁定模型与原子持久化（AdjudicationSet，同 RuleId 最新裁定生效）、规则引擎（豁免优先→pending-original 跳过→检查器隔离→生效强制力覆写）、5 个检查器（字高系列/线宽组/轴号字母/标高格式/图层命名）、7 条 GB/T 50001-2017 内置规则（嵌入资源分发）、CLI check / rule adjudicate / rule list（审计先行+LoadOrReject 守卫）、run 回归检查（tx 成功后评估内置规则，mandatory 违规→stderr 警告+审计 check-run Source="run-regression"，**不自动回滚——决策权归人**；回归自身故障不影响已提交事务）。
- **效力映射裁定**：GB/T 推荐性国标条文默认 recommended（必须/严禁→mandatory、应/不得→recommended/high、宜→recommended/medium；本批 7 条无 mandatory），默认零拦截；升级为 mandatory 的权力归院标 skill/人工裁定（rule adjudicate --force），豁免权同归人（--exempt，理由必填入审计）。check 退出码：0=无 mandatory 违规，2=有 mandatory 违规，recommended/informational 不影响退出码。
- **7 条规则来源与核验状态**：R-50001-4.0.1/5.0.2/8.0.4/11.8.4/11.8.5/13.0.1/13.0.2，条文逐字摘录自 extraction-50001.md（控制者已 grep 核对原文行），verification 全为 source-text（条文级核验）；标准身份（GB/T 50001-2017 现行有效性）为 external-corroborated，待原件确认。pending-original 数值（视觉依赖表：表 4.0.1 线宽组明细之外的附录B推荐层名、专业代码白名单等）未入库，不假装已检。
- **摘录归一化（引用保真说明，2b-i 最终审查修复轮记录）**：摘录归一化：LaTeX 标记→Unicode（√2/mm）、列表序号省略、弯引号→直引号；实质逐字，详见 standards-src/extraction-50001.md F15。
- **检测启发式边界（诚实降级，消息注明）**：轴号/标高检查依赖 overlay 语义标注（Axis/Annotation 类，人工 map 或推断），无标注不检；字高按 hanVector/other 两系列并集判定，字体归属细化留后续；出图比例换算（14.0.4）留计划3；11.8.4 allowSitePlan2 图类判定留后续（2 位小数降级 Low 提示人工确认总平面图）；13.0.2 专业代码白名单/附录B推荐名核验暂缓（视觉依赖表）。
- **跳过原因 stderr 通道**：规则跳过（豁免/pending-original/无检查器/检查器异常）逐条写 stderr（"跳过: " 前缀）并以 skippedReasons 入审计 paramsJson，stdout JSON 契约不含跳过规则 Id（豁免展示移除契约）；结构化跳过报告留 2b-ii。
- **电气/模型规则包+skill 机制=2b-ii**：本批仅建筑制图通则 7 条；电气/给排水/暖通等专业规则包与院标 skill 包机制（§6.3/§6.4）在 2b-ii 交付。
- **MinerU API 录入=后续能力**：规范 PDF→结构化条文的 MinerU API 自动录入属后续能力；当前条文经人工摘录+控制者核对入库。

**计划2b-ii-a完成（skill 机制，2026-11-21 记录；端到端闭环测试 示例 skill 副本→skill validate→scan --skill 别名命中→check --skill exit 2→豁免裁定→check exit 0→人工 map→promote 升版→audit verify（semantic-promoted）→同裁定集重跑 check 指纹一致 全绿，全套件 240 测试）**：

- 已交付：skill 包结构（skill.yaml 清单+rules/*.yaml+aliases.yaml）与严格加载校验（未知键拒绝、version digits.digits.digits、包内规则 Id 重复拒绝、异常带文件名+内层异常）；SkillSet 合并与全局规则 Id 守卫（builtin×skill/skill×skill 冲突→用法错误 stderr+exit 1，校验先于任何副作用、不审计）；别名接入语义扫描（优先级 人工确认>skill 别名>天正约定库，别名命中层不再不明确，扫描与不明确清单判定精确互逆）；检查器硬化（未知 mode→ArgumentException 引擎隔离记账、标高候选窗扩展覆盖 0/4+ 位小数、参数 null 守卫+容差 epsilon）；结构化跳过报告（Exempt/PendingOriginal/NoChecker/CheckerError/NotEvaluable 五类；overlay 依赖检查器无语义标注时记 NotEvaluable，不得与"已查且干净"不可区分；stderr"跳过: [类别] 规则Id: 消息"通道+审计 skippedReasons 结构化数组，stdout 契约不变、旧文案子串兼容）；裁定指纹（规范化 JSON SHA256，与存储顺序无关、对任一字段敏感）+规则包版本（内置常量|name@version 按包序）入 check/run-regression 审计 paramsJson（复现证据）；孤儿裁定拒绝（ruleId 须在 builtin+skill 加载集）；裁定 Load 去重（同 RuleId latest-wins）；CLI skill list/validate、check --skill、scan --skill（--skill 可重复、出现序）、semantic promote（项目人工确认映射→skill 别名 upsert+manifest patch 升版+semantic-promoted 审计；校验先于任何写入、version 行锚定列 0，flow-style 等无 version 行→exit 1 零变更）。
- **示例 skill 非真实院标**：examples/skills/sample-institute（示例院标 1.0.0，1 条 mandatory 图层字符集规则 SK-SAMPLE-001+别名 W-示例墙→Wall）为入库示例，条文系虚构演示，仅用于机制演示与端到端测试，不得用于真实审图；真实院标文档/图层规范待用户提供后编码替换（开放项不变）。E2E 将示例复制到临时项目操作，promote 只改副本、仓库示例零污染（测试断言）。
- **steps/symbols/skill run+电气模型规则数据=2b-ii-b**：steps/symbols 子目录当前容忍不解析；run 回归仍 builtin-only（run 不带 --skill）；电气/给排水/暖通等专业规则包与模型规则数据在 2b-ii-b 交付。
- **LLM 起草 skill=计划3**：当前 skill 包均为人工编写/CLI promote 沉淀；LLM 起草院标 skill（含 Upsert 守卫调用侧强制、幻影映射拒绝）属计划3。
- **2b-i 遗留状态（本计划承接后仍开放）**：全角＋标高候选窗缺口（候选正则仅 ASCII [+-]，全角符号不进入检查，扩字符类待裁定）；1 位小数标高报 High 已追认（与 11.8.4"注写到小数点后第三位"意图一致），补测试留后续；CommandRouter 已增至 942 行，skill 族提取独立文件留后续；promote aliases upsert 经 YamlDotNet 整体重序列化（原注释与额外未知键丢弃，计划规定行为，代码已注）。

**计划2b-ii-b完成（电气 skill 端到端，2026-11-21 记录；端到端闭环测试 电气包 check（合规缆线不成 issue+槽位/字高违规引用 GB/T 50786-2012 条款，全 recommended exit 0）→rule adjudicate 升级槽位规则 mandatory→check exit 2→skill run lay-line 画线+insert-lum 插入图纸内 LUM 块（请求审计先于执行）→audit verify 绿→check 审计 rulePackVersion 含电气包@1.0.0、adjudicationHash 随裁定集变化 全绿，全套件 337 测试）**：

- 已交付：CommandRouter skill 族提取（T1 纯移动行为不变：CommandRouter.cs 980→725 行 + CommandRouter.Skills.cs 265 行，245 测试守卫；T8 增 skill run 后两文件现 781+381 行）；遗留小项批关闭（aliases 全数字 class 拒绝+同包重复 layerName 拒绝、RuleLoader.LoadYaml 内层异常保留、CA1416 程序集级 SupportedOSPlatform("windows") 标注、尾随无值 --skill 警告不退出、builtin 治理测试钉 7 规则内容 SHA256——内容变更强制 RulePackVersion 修订升版）；5 新检查器（text-height-min/cable-format/slot-code-whitelist/luminaire-format/file-naming，候选保守、DetectionBasis 诚实边界、CE/CL 一码多义按槽位消歧）；电气包 4 规则（GB/T 50786-2012：5.1.3 最小字高/4.1.5 序号8 缆线格式/3.4.3 槽位白名单/4.1.5 序号5 灯具格式）+模型包 1 规则（JGJ/T 448-2018 5.4.3 文件命名），条文逐字摘录 TEXT 可核实（extraction-electrical.md H3/D7/D8/D9、extraction-model.md R1），情态冲突双录（3.4.3"应" vs 4.2.1"宜"，按较严应执行；force recommended 承载，mandatory 升级走人工裁定——E2E 演示）；步骤模板 steps/*.yaml（占位符整串 \z 精确替换、缺参/未知占位符拒绝、数值保持数值）+ skill run（模板实例化→StepEngine 执行，审计/快照/闸口全走既有引擎**无旁路**，请求审计 skill-run 先于执行；run --skill 入口校验+tx-start/tx-commit 审计 rulePackVersion 装饰器，不带 --skill 行为逐字不变）；symbols/symbols.yaml 符号元数据（严格校验、文件存在性不校验）+ insert-block 第六处理器（图纸内块引用、缺块失败留痕；跨文档符号导入留计划3）。
- **VISUAL deferred 清单（不假装已检）**：符号本体图形规则、分数排版（分子/分母上下排版）、参照代号 4.2.4 子类白名单（D12，一码多义+庞大）、光源种类联动、专业代码白名单（GB/T 51301）——全部显式 deferred，检查器 DetectionBasis 注记在案。
- **示例符号元数据**：sample-institute/symbols/symbols.yaml（LUM→lum.dxf，class Auxiliary——SemanticClass 尚无 Lighting 成员，布灯语义类随计划3布灯处理器引入；lum.dxf 未随包提供，存在性不校验，跨文档导入计划3）。示例 skill 仍非真实院标，E2E 只改临时副本、仓库示例零污染（测试断言；开放项不变）。
- **已知遗留（计划3承接）**：ComputeHash Insert 投影仅基字段（InsertPoint/scale/rotation 变更不入哈希，随布灯处理器补齐）；run --skill 的 tx-failed 记录 paramsJson 仍裸 planId（装饰器仅覆盖 tx-start/tx-commit）；全角＋标高候选窗缺口（2b-i 遗留仍开放）；数字 class 空白绕过（" 3 " 含空白绕过全数字守卫后 Enum.TryParse 修剪接受）；insert-block scale 无零/负值校验（非数值抛错走引擎失败结果）；skill run 成功路径不做 run-regression 回归检查、run --skill 回归仍 builtin-only（第一 slice 裁定，回归口径留后续）。
- **最终审查修复波关闭（2026-11-21）**：cable-format 候选收紧 `^[A-Z]{2,4}(?=[-\d])` + DetectionBasis 误旗边界披露（WDZ-YJY 多段前缀/MR100×50 尺寸形仍可能误旗，recommended 级 disclosed）；slot-code 3 段纯字母尾码按白名单成员资格定槽（∈methods∉positions→方式槽；双属/双不属→不判，宁漏勿滥）——原"裸字母方式码误槽"遗留关闭；RuleLoader.LoadDirectory 内层异常保留；CA1416 经 Directory.Build.props 统一程序集属性全仓清零（Core/AssemblyInfo.cs 移除避免重复）。全套件 342 测试。
- **2b-ii 全部完成→计划3就绪**：skill 机制（2b-ii-a）+电气/模型规则数据、步骤模板与符号元数据（2b-ii-b）齐备；计划3 承接 LLM 配置与鉴权（§8）、中望 COM 适配器、保真导出+审计包（含哈希链外部锚）、厂商支持矩阵（§11）、步级 undo，及上列已知遗留与 2a/2b-i 开放项。

**计划4完成（Avalonia 视图，2026-11-22 记录；T1-T10 全交付，全套件 579 测试绿；真机验证 22 截图存证 docs/verification/）**：

- 已交付：CLI `view` 进程拉起（Cli 不 using Avalonia，项目引用仅复制输出；T10 增 `--skill` 可重复直通，包校验先于拉起）；渲染第一 slice（Line/Circle/Arc/LwPolyline/Polyline2D/Text/MText/Insert 块展开含变换，图层色/真彩解析）；画布 pan/zoom/fit（滚轮锚点缩放/中键平移/视口重适配，T10 补 ClipToBounds 裁剪）；图层面板（可见性/色样/计数/双击隔离/Show all，T10 行重构修复名字区双击隔离）；问题清单（in-process check 镜像 CLI、效力徽章 T10 WCAG 修正深红/深灰、条款引用/摘录/建议/检测依据/跳过原因 tooltip、点击定位高亮居中）；审计时间线（只读回放、tx 分组 commit/failed、选择高亮、Diff 前后快照叠加 added/removed/changed、无 audit.db 不建库）；审查签认（Role>=Review 闸、自审禁止、signoffs.json + review-signed 审计、时间最新签认生效）；纠偏面板（Scan 证据清单/人工 Map HumanConfirmed 1.0/Isolate，overlay RMW 串行化）；裁定面板（镜像 CLI 校验序、孤儿拒绝、裁定成功→issues 自动联动）；手动编辑（Select/Line/Text/Move/Delete/ChangeLayer/InsertBlock/Undo **全经 EditViewModel→StepEngine，无直接 Doc 修改、无审计旁路**：view-edit 请求审计先于 tx，语义闸口镜像 CLI，失败 Reload 无部分保存；共享会话闸编辑与后台读互斥；T10 补 _editBusy 结构性重入闸——在途编辑期间画布点击/工具栏事件一律忽略）；布局收尾（状态栏图纸名/计数/缩放/选择/审计摘要、工具栏全按钮 tooltip、窗口标题 CadAgent—图纸名）。
- **渲染 deferred（不假装已渲染）**：尺寸标注实体（Dimension）、填充（Hatch）、线型虚实（dash 图案）、OCS 镜像/挤出变换——RenderModelBuilder 跳过并计数，后续 slice 承接。**更正（2026-11-22）**：颜色精确表（ACI 全表）已关闭——9ce52ba 落地 Color.GetIndexRGB 全 256 表映射（RenderModel.cs ToRgb 实证），原"当前简化"句过时移除。**视图收尾切片更新（2026-11-22）**：Dimension（匿名块展开）/Hatch（边界+实心 EvenOdd 填充）/dash（LineType 段→DashStyle）/OCS（任意轴算法、镜像精确）已落地；**剩余 deferred 边界**：Hatch 图案线生成、复杂线型 shapes/文字段实线降级、倾斜法向圆/弧 72 段 tessellation 投影近似与 Insert 投影近似、无块 Dimension 诚实计数——详见 §12 末尾闭环注记。
- **headless 覆盖范围**：View 232 测试（VM 纯逻辑单测 + Avalonia.Headless.XUnit 真实 Skia 帧控件测试）：渲染/画布/图层/问题/审计+diff/签认/纠偏+裁定/编辑全面板；T10 增重入闸（注入会话闸持闸制造确定性 in-flight）、--skill 三 VM 接线、WCAG 对比度计算、ClipToBounds、名字区双击隔离。真机视觉不依赖 headless 断言的部分走 §12 真机协议。
- **真机验证结果（docs/verification/plan4-ui-protocol.md + 22 PNG）**：启动渲染/缩放/fit/图层隔离/问题引用/手动画线+撤销/时间线高亮+Diff/签认 Review 成功（异操作者 tx）+自审拒绝+Draw 禁用/纠偏 Scan+Map/裁定升级联动/CLI --skill 直通 均**已验证**；真机发现并 TDD 修复两缺陷（画布未裁剪溢出相邻面板、图层名双击被 Toggle 消费不隔离）。**手动验证待执行**（诚实标注）：中键拖拽平移真机手感（computer-use middle-drag 注入限制，headless 已覆盖逻辑）、Text/Move/Delete/ChangeLayer/InsertBlock 真机手感（headless 全覆盖）、高 DPI/多显示器、大图性能。两验证项目审计链 CLI verify 绿（9/7 条）。
- **计划3承接状态注记（2026-11-22 更正）**：原"side chat 仅交付 3a-T1 脚手架、计划3 其余仍开放"注记已过时——计划3 全部切片（3a-3j：LLM 配置鉴权全套/语义闸口/COM 适配器/保真导出+审计包/厂商矩阵/读回验证环/COM 绘制+生成式真机基准）已由 side chat 完成并合并 main（PR#3~#15，2026-11-21/22），计划4 视图（PR#16）与 CUIX 串行化修复（PR#17）亦已合并；视图侧预留注入点（IIdentityProvider/会话闸/审计存储）已被 T9/T10 消费。LLM caller（AgentOrchestrator + ILlmClient + OpenAiClient）就绪——④纠偏计划视图预览的阻塞已解除，承接见视图收尾切片。
- **T9 承接遗留（仍开放）**：审计时间线编辑后不 live-rebuild（构造期快照，重开可见新 tx——真机协议第 6 项即按重开验证）；Text 字高 UI 输入未暴露（固定 3.5，VM wrapper 已支持 height）；Move 仅 Line/Text（处理器契约，圆等移动诚实 step-failed）；InsertBlock scale/rotation UI 未暴露（VM 已支持）；Delete 两段式工具栏确认（无模态对话框基础设施）。

**计划4最终审查修复波关闭（2026-11-22 记录；3 Important 全实现 + 打包小项，全套件 597 测试绿）**：

- **①视图编辑边界准入闸（§4.2 硬要求兑现）**：EditViewModel.ApplyCore 在 StepEngine.Run 前镜像 CLI RunPlan——GateDetector.Inspect(session.Summarize(), EmptySupportMatrix)，Red→run-rejected 审计（Source="view"，params{level,unsupportedCount} 镜像 CLI 字段，前后哈希一致）+ T3 指引拒绝执行（无 tx）；每次 ApplyAsync 都检。Yellow 放行+结果警示分支与 CLI 逐字对齐（EmptySupportMatrix 下任何 unknown 均 unsupported，Yellow 当前不可达——支持矩阵点亮后生效）。Red 夹具经 ACadSharp ProxyEntity DxfWriter/DxfReader 往返实证可行。
- **②驳回签认→快照回滚（§4.1⑥/§5.4 明文兑现）**：SignoffViewModel 增可选 RollbackTx 回调（MainWindow 接 EditViewModel.UndoAsync→StepEngine.Rollback，无旁路）；approved=false 且 tx-start SnapshotRef 非空→签认审计+落盘后 await 回滚（rollback 审计由引擎留痕）；回滚失败→ErrorMessage 提示但签认记录不回撤（审计先行）。加性门禁：仅已提交事务可签认（TxGroup.HasCommit；failed/open→"只能签认已提交事务"，零副作用）。TxGroup/AuditEntryVm 加性暴露 SnapshotRef/HasSnapshot。
- **③损坏状态文件优雅降级（T10 承诺兑现，§9 不崩 UI）**：MainWindow 构造三处 try/catch——audit.db 损坏→空时间线+"审计库损坏，时间线不可用"；roles.yaml 损坏→默认 Draw 角色+提示；signoffs.json 损坏→空签认存储+提示（后续 Save 原子写覆盖修复）。提示汇总入 MainWindow.ErrorMessage + 可见 StateNote 文本块；窗口照常打开、图纸照常渲染。注：audit.db 损坏时写审计占位首次 Append 仍会失败（审计先行不变量——编辑被拒并报错，不静默绕过审计）。
- **打包小项**：SetActiveTool 补 _editBusy 硬闸（在途编辑期间工具切换忽略，RaiseEvent 绕过 IsEnabled 亦被挡）；plan4-ui-protocol.md 悬空"附录 A"引用改为内联等价描述。
- **仍延迟的 minors（记录在案，不阻塞）**：issues-refresh 审计 parity（视图 Refresh 不留 check 审计，CLI check 留痕）；view run-regression（视图编辑成功后不做内置规则回归警告，CLI run 有）；_diffBusy 结构性守卫（Diff 加载期间仅 IsEnabled 禁用，未加标志位硬闸）；Issues.RefreshAsync in-flight 并发守卫（按钮禁用为 UI 态）；空图层不显示于图层面板（LayersViewModel 以 renderItems 构造，无实体层不出现）；计划预览（④纠偏计划视图预览依赖计划3 LLM caller）。其余各 Task parked minors 见控制者 ledger。**[已闭环 2026-11-22]** 上列 minors 全部由视图收尾切片（Tasks 5-10）闭环，见 §12 末尾「计划4 延迟项承接闭环」注记；本句保留作历史记录。
**计划3a 完成（硬前置 + LLM 基础设施，2026-11-21 记录；全套件 411 测试）**：
- 硬前置六项关闭（本侧_chat T1-T5）：ComputeHash Insert 投影（point/scale/rotation/block）；SemanticClass.Lighting + ISemanticContextGate 实体级闸口（加性，targetHandle 精确到实体，null 退化操作级）；嵌套 params 递归占位替换契约；handle 契约字符串/数字统一；跨进程审计 BEGIN IMMEDIATE + busy 重试。
- LLM 基础设施（采纳主会话计划 2026-11-21-cad-agent-llm-infrastructure.md 为执行权威，本侧接管内联执行 Task 2-8）：ProviderProfile/LlmConfigStore（主会话 Task 1 已交付）；DpapiKeyProtector（CurrentUser 加密 + env CADAGENT_LLM_API_KEY 优先 + 穿越守卫）；ILlmClient 契约 + OpenAiClient（mock 测试，401/403→LlmAuthException，错误消息脱敏）；Discovery/Presets（OpenAI/DashScope/Ollama）/HttpEndpointProbe/GuessVisionCapable 启发式；LlmProbe（补全 + 1×1 PNG 视觉探针，ChatRequest.ImageUrl 加性）；UsageMeter（会话预算帽 + 套餐窗口重置）；CLI llm add/list/use/remove/test（LlmClientFactory 可注入，审计 llm-profile-added/used/removed/probe 全无密钥）；E2E 全生命周期密钥安全（配置全文扫描 + 审计链 + env override）。
- **3b 承接**：编排（读图→计划生成→确认循环→执行→读回迭代）；llm-call 审计 + UsageMeter 接线；数据出境首次确认执行（EgressPolicy 模型未建——3b 补）；vision 渲染（3b 文本摘要读图先行，视觉渲染留 3c COM 导出/计划4）；签认/步级 undo。
- **3c 承接**：中望 COM 适配器（ICadRuntime）+ 保真导出 + 审计包（含哈希链外部锚）+ 厂商支持矩阵。
- 诚实边界：DPAPI Windows-only（跨平台留后续）；vision heuristic 保守（最终靠探针）；套餐窗口第一 slice 从首次 Record 起算；Ollama 探测 heuristic；llm test 实弹冒烟待用户配置真实 key。

**计划3b-i 完成（编排循环核心，2026-11-21 记录；全套件 438 测试）**：
- EgressPolicy（§8.5）：项目 local-only.txt 硬约束 > profile 策略 > 首次云端确认（cloud-consent.json 持久化）；localhost 一等公民。
- DrawingSummarizer：有界确定性文本摘要（图层/实体计数/文本清单前50/块名/语义标注×来源计数+人工映射含 HumanConfirmed 标记）；视觉渲染留 3c/计划4。
- AgentPlanParser：```json 围栏/裸 JSON 提取 + planId 缺省注入 + StepPlanParser schema + kind 白名单（LLM 幻觉挡在引擎外）。
- AgentOrchestrator：出境闸→预算前置→LLM 计划→校验→人工确认→StepEngine 执行→失败一次重规划（错误反馈）→仍失败 Escalated；llm-call 审计（model/usage/attempt/planId，无图纸内容无密钥）；agent-run/egress-blocked/egress-consent/plan-rejected 全事件。
- CLI agent 命令：--yes 自动确认（测试路径）/ stdin 交互确认（手动冒烟待真实终端）；LlmClientFactory 注入；六处理器 kindDocs。
- E2E：多步计划执行+审计全链+consent 持久化（二次调用不再询问）+配置/审计全文密钥扫描。
- **3b-ii 承接**：审查签认（review 角色+职责分离+驳回回滚）；步级 undo（会话内模型 undo 栈）；REPL 交互会话。
- 诚实边界：交互确认 stdin 路径测试不覆盖（--yes 全测）；重规划上限 1 次（裁定）；意图文本不入审计（隐私）；读图仅文本摘要（视觉 deferred）。

**计划3b-ii 完成（签认+步级撤回+REPL，2026-11-21 记录；全套件 453 测试）**：
- IDrawingSession 加性 CaptureState/RestoreState（内存 DXF 往返，不触磁盘）；ModelUndoStack 会话内快照栈；StepEngine 加性 undoStack 参数+UndoLastStep（审计 step-undone，空栈 false）。
- review signoff：Review 角色强制；职责分离（签认人≠执行人，SnapshotRef 含 txId 溯源）；防重复签认；驳回→既有快照回滚（review-rejected+rollback 双留痕）；批准不改图纸。
- REPL 交互会话：持久 DrawingSession+undo 栈；意图→编排（egress 确认与计划确认两道独立 stdin 闸门）；:undo/:rollback/:status/:quit；repl-session 起止审计；StringReader 注入全离线测试。
- 测试稳定性根因修复：ClearAllPools 进程级与 xUnit 集合并行互斥→Cli/Core 测试程序集禁用套件内并行（三连跑全绿验证）。
- **3b 全部完成→3c 就绪**：中望 COM 适配器（ICadRuntime）+保真导出+审计包（含哈希链外部锚）+厂商支持矩阵；3d 布灯基准。
- 诚实边界：步级 undo 会话内（跨会话事务快照兜底既有）；REPL 视觉反馈留计划4；IdentityOverride 仅测试注入。

**计划3c 完成（COM 适配+保真导出+审计包+厂商矩阵，2026-11-21 记录；全套件 475 测试含 3 真机）**：
- ICadRuntime 厂商无关契约（Core）；ZwcadRuntime 独立适配器项目：dynamic COM（ProgID ZWCAD.Application，不编译期引用 interop DLL）+ 专用 STA 线程任务队列串行化 + CadRuntimeException + Restart 崩溃重启。
- 保真导出 ExportService：厂商 Open→SaveAs DWG→ACadSharp 读回计数闸口（不过闸拒绝+审计）；COM 崩溃 Restart 一次重试；无厂商→DXF 降级交付+显式风险审计（不假装等价）；CLI export + RuntimeFactory 注入。
- 审计外部锚 AnchorService：HMACSHA256 + DPAPI CurrentUser 密钥；每次 Append 更新 anchor.json；验锚检测尾截断（DB head seq < anchor seq）与伪造（HMAC 不符）；CLI audit verify --anchor + audit package（manifest+内容，**不含 llm 配置/密钥/锚密钥**）。
- 厂商支持矩阵 vendor-support.json：ZWCAD 2025 **Verified（2026-11-21 真机证据：GetInfo/Open/SaveAs/Close + DXF→DWG 读回往返 + 保真 E2E）**；AutoCAD CompatibleUnverified（适配器未实现）；TArch Unsupported（预留）；vendor list + export gating 警示+审计（不阻止，决策权归人）。
- **3d 布灯基准就绪**；计划4 Avalonia 视图待写。
- 诚实边界：锚密钥本机 DPAPI（跨机验锚需密钥分发留后续）；往返校验第一 slice 实体计数+文本抽样（几何逐点留后续）；SaveAs 默认格式（版本参数留后续）；AutoCAD 适配器未实现。

**计划3d 完成（布灯基准——第二期场景泛化验证，2026-11-21 记录；全套件 492 测试含 3 真机）**：
- RoomDetector（Data/Semantic）：房名文本精确匹配模式列表（客厅/卧室/厨房等13类）→锚点；区域=锚点 10m 半径内 Wall 类线（叠加层标注或图层约定）端点 bbox；无墙/退化 bbox→6m×6m 默认区域显式 Assumed=true；Room 标注 GeometryInference 0.6（低于闸口 0.8，须人工确认）；集成 SemanticScanner（剪枝前）+ DrawingSummarizer 房间段（LLM 规划依据）。
- layout-lighting 第七处理器：确定性网格（x+spacing/2 起步、不越界），每点 Insert(block)，可选 layer；ISemanticAware 声明 RequiredSemantics=[Room]——**语义闸口首次实战**：scan 推断 0.6 拒执行（semantic-gate-refused 留痕→有界重规划一次仍拒→Escalated），人工 map 确认 1.0 放行；点位数上限 10000 防脏参数；块不存在/非正参数→失败结果。KindDocs agent/repl 共用更新。
- 灯具 skill 包 examples/skills/lighting（示例院标，非真实）：layer-name 规则 + E-LITE→Lighting 别名（SemanticClass.Lighting 既有）；**GB 50034 未提供——间距/照度不编造可执行条款**，仅 suggestion 注记人工审查项。
- 基准 E2E 全链（离线 fake LLM）：建筑底图→scan（Wall/Room 推断）→摘要房间段（6000×4000 非 assumed）→map 确认→agent 布灯 6 点位落 E-LITE→审查签认（职责分离）→降级导出读回 6 Insert→审计包 manifest chainVerified+anchorStatus Ok（无密钥泄漏）→验锚通过；第二链：驳回→事务回滚→灯具清零（要求③在布灯场景复验）。
- **计划3 全部完成（3a/3b/3c/3d）→计划4 Avalonia 视图就绪**（渲染+基础手动编辑+审计回放+签认纠偏 GUI）。
- 诚实边界：房间区域为 bbox 启发式（精确多边形重建留后续）；照度计算未做（设计标准缺失）；灯具符号跨文档导入仍未做（块须已在图纸内，既有 deferred）；房名模式列表为起点（真实图纸自定义房名走人工纠偏流程）；基准用 fake LLM（真实 API key 冒烟为用户动作）。

**计划3e 完成（JGJ/T 244-2011 落地：真实条文规则包+灯具编号，2026-11-21 记录；全套件 512 测试含 3 真机）**：
- **244 标准核验**：用户提供 MinerU JSON+原件 PDF；封面+住建部公告第1053号+备案号 J1216-2011 齐全（四份中首份封面齐全），74页与官方书目一致，现行有效未被替代。正文零"必须/严禁"→入库规则全 recommended/informational，零 mandatory。提取底稿 standards-src/extraction-244.md（D1-D12 逐字+页码）。
- **MinerU 图例缺失根因核查（用户质疑触发）**：四份 JSON 元数据均 `_backend=hybrid, _ocr_enable=true, _effort=medium, v3.4.4`——hybrid=VLM+原生文本提取混合后端（多模态模型已参与），OCR 已启用；**根因＝effort medium 自动关闭图像分析**（MinerU 3.x 文档化行为），非"未选 VLM/未调 OCR"。补救＝effort high/xhigh 重转 244/50786 开启图像分析→图例视觉关联（用户侧 MinerU 服务动作；项目侧 MinerU API 后续能力）。
- **label-luminaires 第八处理器**：设备索引符号（3.6.5/3.6.6-4）——正六边形+水平内径线、细实线 W25、长轴 8~12mm 参数校验、内注编号+品种代号；prefix 必填无默认（品种代号标准明示归院标，3.6.5 条文说明）；编号确定性（Insert Handle 升序）；字高≥2.5mm；**ISemanticAware=[Lighting]——Lighting 类首次闸口实战**（无确认拒/别名 0.8 放行）。
- **room-name 检查器（5.2.5）**：房名/编号存在性（RoomDetector 同口径启发式）；编号圆圈6mm/字高比较/房间名称表 VISUAL deferred（DetectionBasis 注记）；StandardsEngine 注册。
- **interior-244 skill 包（真实条文）**：R-244-5.2.5（room-name 可查）+ R-244-4.0.9/3.6.5/A.4.12/4.0.3（**NoChecker 结构化跳过诚实登记** VISUAL/深度 deferred，跳过不静默）；symbols 17 类灯具目录（表4.0.9 名称逐字，class Lighting，几何占位待重转）；steps/label-luminaires 模板；aliases 空（244 不规定图层名，不编造）。
- **编号 E2E**：布灯→**再 scan**（时序关键：布灯前 scan 无 E-LITE Insert 可标）→skill run 编号 6 符号 L1..L6→签认→验链；闸口变体无 Lighting→Escalated+semantic-gate-refused。
- **244 重新论证结论（用户要求）**：3d 控制性布点有标准依据（A.4.4-8"可"）；房名信号=5.2.5 要求；缺口＝生产灯具布置图属顶棚镜像视图（5.1.2）+A.4.12 深度余项（定位尺寸/材料/做法/设备协调）+图例几何（VISUAL）+顶棚标高 CH（3.10.8）。
- 诚实边界：索引符号合规检查器未建（生成已有，校验 deferred）；文本近似居中（精确排版留视图层）；A.4.12 深度余项 deferred；灯具图例几何待 MinerU high 重转+视觉关联；跨文档符号导入仍 deferred。

**3e 追加：灯具图例几何补齐（2026-11-21，MinerU vlm 重转后；全套件 513 测试含 3 真机）**：
- 用户提供 API token（仅内存使用不落盘）→ v4 API model_version=vlm 重转 244/50786（图像分析启用）；CDN 下载 Python openssl 被阻断→curl schannel 绕过（无代理）；结果包+整表裁切入库 standards-src/_reparse/（gitignored）。
- v2 content_list 表块 image_source 整表栅格＝图例几何载体（HTML 单元格仍空，视觉关联必需，预判证实）；行列线检测裁切 20 单格→**17 类灯具图例逐类目视核对**（名称↔图形映射确认，含变体）。
- **矢量重建 17 类 DXF**（examples/skills/interior-244/symbols/luminaire-01..17.dxf，ACadSharp 生成+读回测试）；symbols.yaml 占位转真实文件+几何描述+来源链注记。近似边界：非精确描摹/涂黑与波浪折线近似/变体未单独建块/毫米级示意比例。
- 50786 符号表裁切同批就绪（表4.1.2 等 60+ 表）——302 符号矢量化 deferred 独立切片。
- MinerU API 录入能力首跑通（token 环境变量注入、批次上传/轮询/下载重试）——后续新标准录入可复用该管线。

**计划3f 完成（50786 表4.1.2 强电符号矢量化 164/164，2026-11-21 记录；全套件 515 测试含 3 真机）**：
- 6 批逐页目视转录（p10-p32 整表裁切，忽略斜向水印；[YS84] 等 OCR 噪声格忽略；名称取说明列中文+英文）→ elec-001..164.dxf 入 examples/skills/electrical/symbols/ + symbols.yaml 目录（emit 自生成器注册表，单一事实源）。
- 覆盖：导线/连接/端子族、二极/晶体/电机/变压器/互感器族、变换器/触点/开关/保护器件族、表计/信号/电站/防雷/电缆线路族、配线标识/插座/开关平面族、按钮/灯/应急/荧光/格栅/投光/聚光/风扇族。灯具类 class=Lighting（15/97/148-163），其余 Auxiliary。
- 读回测试：目录条目数==DXF 文件数==164 + 每文件 DxfReader entities>0 + electrical skill validate 保持 0。
- 诚实边界：近似重建非精确描摹；涂黑/斜线填充以密集线近似；形式2 不单独建块（description 注记）；复合符号（#16/#42/#93 等）取主要素简化注记；45/46/47 说明列共用按图形区分命名；4.1.3-x 弱电符号表/4.1.4 线型符号**留后续切片**（裁切已就绪）。

**计划3g 完成（50786 弱电符号矢量化 144/144，2026-11-21 记录；全套件 515 测试含 3 真机；符号总 308）**：
- 表4.1.3-1 通信/综合布线 17 + 4.1.3-2 火灾报警 38 + 4.1.3-3 有线电视/卫星 21 + 4.1.3-4 广播 8 + 4.1.3-5 安防 37 + 4.1.3-6 设备监控 23 = **144**；连续编号 elec-165..308 并入 electrical symbols.yaml（表边界注释）。
- **MinerU 漏提取兜底**：p34/p39 续表在 v1/v2 均 0 行（空占位块）——zip 内 origin.pdf 经 pypdfium2 整页渲染（150dpi）恢复全部漏行（com#13-17、tv#11-21 等）；整页渲染 p33-45 入库 _reparse/gb50786/pages/（gitignored）。**管线教训：表裁切可能截断/漏提，整页渲染是核对兜底**。
- 火灾注1/注2 字母替代清单（C/Z/G/S；RS/RD/I/O/P/T/SI/M/SB/D/FI/CRT/FPA/MT/BO/TP）、广播注1/注2（C/R/W；A/PRA/AP）入 description。
- 诚实边界：同 3f 近似重建口径；湿度/压差等形式1/2 字母差异取形式1+注记；**4.1.4 线型符号仍 deferred**（p45 表已渲染就绪，3 行起）。

**3g 追加：MinerU 重摘核实（2026-11-21，用户要求；批次 932d27ce）**：
- 用户指出源 PDF 弱电表非空——证实。重跑 vlm 摘取：按表合计 17/38/21/8/37/23 与首跑及转录**完全一致**；归一化名称多重集对比**零残差**（144=144）。
- **漏提取真相修正**：p34/p39 的 0 行表块＝**重复碎片块**（两跑稳定复现）；续表行实际**合并进首页表块 HTML**（p33 含 17 行名、p38 含 21 行名）——名称从未缺失；**截断的是 image_source 裁切**（止于页边界），故几何须整页渲染兜底（此前结论"名称漏提取"修正为"裁切截断+碎片块"）。
- 核实发现并修正 1 处转录词序错误：#19 源文"复合式**感温感烟**火灾探测器"（原目录误作"感烟感温"）→ 目录已修正再生成。
- 管线定论：MinerU 表 HTML 名称可信（两跑一致）；表裁切几何不可信（截断）；整页渲染（pypdfium2）为几何核对唯一兜底。

**3g 追加 W6：表4.1.4 线型符号 16/16（2026-11-21；符号总 324；515 测试绿）**：
- 信号/控制/应急照明/保护接地/接地/接闪/电话/数据/有线电视/广播/视频/综合布线/消防电话/50V以下电源/直流电源 15 类＝实线+上标字母（形式2 嵌标 —X— 注记）；#16 光缆＝横线+中圆斜杠（∅近似）。
- elec-309..324；**50786 图形符号全表覆盖完成**（4.1.2 强电164 + 4.1.3-1..6 弱电144 + 4.1.4 线型16 = 324）；4.1.5 标注方式/4.2.x 文字符号为非图形（既有格式规则覆盖部分，其余 deferred）。

**计划3h 完成（跨文档符号导入，2026-11-21 记录；全套件 530 测试含 3 真机；§12 长期 deferred 项清除）**：
- **insert-symbol 第九处理器**（Data ImportSymbolHandler）：skill 包 symbols.yaml 按名解析（OrdinalIgnoreCase 首包命中；**不接受任意路径参数**）→源 DXF 读取→**白名单重建克隆 Line/Circle/Arc/TextEntity**（不赌 Entity.Clone 的外来表引用；Arc:Circle 继承序注意）→建块（块名=符号名，中文块名往返验证）/复用块幂等→Insert(scale/rotation/layer)；未知符号/缺文件/不支持类型响亮失败（先校验后变更）。
- CLI 接线：Engine extraHandler 可选参；run/agent/repl 经 --skill 注入（**agent/repl allowedKinds 同步含注入 kind**，否则计划解析期拒）；skill run 包目录为位置参数→SkillRunImportHandler(skillDir+--skill 去重)；KindDocs 加行。无 --skill 时 insert-symbol 不注册→解析期 Failed（诚实）。
- interior-244 加 steps/place-luminaire 模板（symbol/x/y）；E2E：建筑底图→scan/map Room→模板导入 3 筒灯（1 块 3 Insert）→签认→验链；未知符号失败零变更。
- 几何导入不声明 ISemanticAware（语义确认仍 scan/map）；执行经 StepEngine 审计/快照管线无旁路。
- **符号库 324+17 自此真正落图**；诚实边界：白名单四类之外类型拒收（随库扩展再扩）；重建克隆非精确 Clone（属性子集：几何+文本值/高/旋转）。

**计划3i 完成（补核心 A：读回迭代环，2026-11-21 记录；全套件 546 测试含 3 真机）**：
- **核心环闭合**："读图→决策→操作→**读回→迭代**"——执行成功后 post-summary+前后哈希 diff 喂回 LLM 验证（AgentOrchestrator.VerifyLoopAsync）；{"verdict":"ok"}→Executed；纠正计划→白名单+人工确认+执行→再验证；有界默认 2 轮（构造器可配）。
- 诚实降级：验证轮预算耗尽→Executed+agent-verify budget-skipped 审计（执行已提交不谎报失败）；验证响应不可解析/纠正执行失败/轮耗尽→Escalated 交人工（不信任完成）；逐轮 llm-call（attempt 递增）+agent-verify（verdict/round）审计。
- CLI seam：agent/repl 注入 postSummaryProvider=完整 DrawingSummarizer 摘要（Core 默认最小 diff 不引用 Data）；stdout 加 verificationRounds。
- **契约变更 sanctioned**：Executed 含验证调用——旧一次性执行测试的 usage/Calls 断言同步更新；fake 客户端按 system prompt"验证智能体"识别验证请求回 verdict ok。
- 缺口 B（活 CAD 执行）/C（生成式验证，并入 B 基准）→**计划 3j 待写**（用户裁定 A→B，C 并入 B）。

**计划3j 完成（补核心 B+C：COM 绘制执行+生成式真机基准，2026-11-21 记录；全套件 559 测试含 5 真机）**：
- **ICadRuntime 绘制操作**：AddLine/AddText/AddCircle/AddInsert/EnsureBlock/EntityCount；ZwcadRuntime dynamic COM 真机核定签名：**Blocks.Add(origin, Name)**（与 AutoCAD 文档序相反）、**ModelSpace.InsertBlock**（非 AddInsert）、**赋不存在层须先 Layers.Add**（0x80200014 根因）。
- **ComReplayService 计划级 COM 重放**（第二执行通道）：kind→COM 映射（生成类 add-line/add-text/add-circle/insert-block/layout-lighting/label-luminaires/insert-symbol）；几何与 handler 同源 GeometryKit（网格/六边形/文本放置共享，防双写漂移）；编辑类 kind 响亮不支持（第一 slice）。
- **--live 接线**（run/agent/repl/skill run）：tx 提交后从**事务前快照**开活会话→重放→逐 step com-replay 审计→**读回计数闸口**（vendor vs 影子，分歧=com-replay-divergence 响亮失败）→SaveAs <drawing>.live.dwg→com-replay-committed；无厂商/重放失败/异常→审计+stderr+exit 1。影子副本仍为审计事实源（双通道均经审计管线）。
- **add-circle 第十处理器**（生成式绘制补全；注册表 9 静态+insert-symbol 动态）。
- **C 生成式真机基准**：LLM 读摘要产轴线/房间名/柱圆**生成式几何计划**（非符号导入）→agent --live→活 ZWCAD 落图→live.dwg ACadSharp 读回断言（AXIS 层线/客厅文字/r250 圆/7 实体）→com-replay×3+committed+agent-verify+验链+密钥不入审计。
- 诚实边界：重放第一 slice 仅生成类（编辑类 delete/move/change-layer 不支持响亮失败）；读回闸口为计数级（几何逐点比对仍 deferred）；--live 交付为 .live.dwg 旁路文件（不覆盖源/工作副本）。
- **用户核心质疑三缺口全部闭合**：读回环(3i)/活 CAD 执行(3j)/生成式验证(3j 基准)。计划 4 视图就绪。

**合并前质量门禁（2026-11-21，code-review-csharp 扩展审查 main..3j 全量 82 文件 +5860 行；用户裁定修 Critical+Warning）**：
- 审查结论：1 Critical / 3 Warning / 4 Info；无安全漏洞类 Critical（密钥 DPAPI/参数化 SQL/出境闸/哈希链+HMAC 锚核查通过）。
- **Critical#1 已修**：DrawingSession.SaveWorking 改 tmp+Move 原子写（原 File.Create 先截断，崩溃可损持久工作副本）；回归 SaveWorkingAtomicTests。
- **Warning#2 已修**：AnchorStatus 增 Stale（db 超前锚=commit→anchor.Update 崩溃窗口尾记录无锚保护）；Verify 显式 Stale 不谎报 Ok；CLI verify --anchor 警示 exit 0（同 Missing 先例）；审计包 manifest 如实记 Stale；回归 MergeGateReviewFixesTests.AnchorLag。
- **Warning#3 已修**：llm add 增 --key-stdin（标准输入读密钥，不进 argv/shell history）；--key 便利路径保留（暴露面注记）；回归 KeyStdinTests。
- **Warning#4 已修**：SkillPackage.LoadSymbols 符号 file 穿越守卫（.. / rooted / 领分隔符拒绝；包内相对子目录允许）；回归三场景。
- **Info 注记（不修，宿主化时处理）**：①OpenAiClient 每实例 HttpClient（CLI 短命进程可接受，宿主化改 IHttpClientFactory）；②CLI 边界 GetAwaiter().GetResult() 有意同步（无同步上下文）；③EgressPolicy loopback 集偏严（fail-safe 方向正确，可选 IPAddress.IsLoopback 完备化）；④COM 通道 BlockText 丢 rotation（当前符号库 rotation=0 无实际分歧，扩旋转符号时补）。
- 修复后全套件 **566 测试连绿两次含 5 真机**（首跑单败为瞬态文件锁竞争，复跑稳定）。
- **ZWCAD"套件损坏"弹窗核查（用户报告）**：崩溃重启机制为计划内（spec §9 Restart+重试），弹窗本身非预期——根因 Dispose/Restart 带开文档 Quit 致 ZWCAD 判异常终止；已修先 Close(false) 再 Quit（dc031fa→acb99f6）。

- **AutoCAD 适配器切片交付（2026-11-22，feat/cad-agent-autocad-adapter）**：AcadRuntime 全接口 dynamic COM 实现（CompatibleUnverified——开发机孤儿 ProgID 注册无 LocalServer32，真机验证 deferred 至工作环境，协议 docs/verification/autocad-adapter-protocol.md）。PR#17 CUIX 守卫机制参数化为 Core.Cad.VendorGuard+VendorProfile（厂商无关，ZwcadRuntime 行为保持重构）；CuixLocator 双厂商 CUIX 定位。CLI CADAGENT_RUNTIME 选择运行时（默认 zwcad；未知值硬错误不静默降级）。Blocks.Add 签名序经 VendorProfile.BlocksAddNameFirst 参数化（AutoCAD 文档序 Name 先，待真机探针核定——ZWCAD 实测序相反的前车之鉴）。GetInfo 守卫超时→Available=false→DXF 降级（安全侧，忙≠不可用语义细化 deferred）。accoreconsole 真无头路径 deferred（暴露面更小，守卫机制可复用）。矩阵 Verified 升级须工作环境真机证据（spec §11 硬约束）。
**视图收尾切片（feat/cad-agent-view-closure，2026-11-22）**：

- **计划4 延迟项承接闭环（2026-11-22，feat/cad-agent-view-closure）**：渲染 Dimension（匿名块展开，块缺失诚实计数）/Hatch（边界+实心 EvenOdd 填充；图案线生成 deferred）/dash（LineType 段→DashStyle，ByLayer 回退+LineTypeScale；复杂线型实线降级 deferred shapes）/OCS（任意轴算法，镜像精确；倾斜法向圆/弧 72 段 tessellation 投影近似、Insert 投影近似——明示边界）；ACI 全表已关闭（9ce52ba，前更正）。T9 遗留全闭环：审计 live-rebuild（Rebuild+TimelineChanged）/Text 字高输入/Move 八类实体（处理器契约扩展+KindDocs 共享提取）/InsertBlock 缩放旋转输入（度→弧度）/Delete 模态确认（ConfirmDialog+headless seam）。minors 闭环：issues-refresh 审计 parity（view-check check-run 镜像 CLI 字段）/view run-regression（view-regression，观察性不回滚+嵌套审计守卫）/_diffBusy 硬闸/RefreshAsync in-flight 硬闸/空图层（图层表并集）。④纠偏计划预览落地（AgentOrchestrator confirmPlan/confirmCloudEgress 回调接线，Core 零改动；View→Llm 引用；insert-symbol 视图侧 deferred）。真机手动验证按 docs/verification/view-closure-protocol.md 执行（证据+限制注记）。
- **真机验证结果摘要（2026-11-22，19 截图存证 docs/verification/view-closure-*.png）**：五工具（Text 字高7/Move 圆/Delete 模态取消+确认/ChangeLayer/InsertBlock 缩放2旋转90）+渲染视觉（Dimension/Hatch 岛/dash 三档/镜像 OCS 圆对/空图层）+审计 live-rebuild 5 tx+issues parity（6 issues/1 mandatory/3 skipped）+大图 10000 items（Build avg 5.6ms、编辑刷新 wall ≤1.21s）+双显示器 per-monitor DPI（125%↔100%）+审计链 CLI verify 绿（26/5 条）均**已验证**；**限制诚实标注**：中键拖拽注入限制（headless 覆盖逻辑）、150% 缩放切换被权限拦截待人工（125% 非整数已验证）、Ollama 不可得→计划预览走无 profile 引导核对（云端出境同意真机首启待真实密钥）**[已更新 2026-11-22：用户 key 解锁，云端全链已真机验证，见下条]**、MARK 十字块旋转视觉自对称不可区分、帧率无插桩为主观判定。真机零应用缺陷发现。
- **云端全链验证闭环（2026-11-22，用户 DashScope key 解锁）**：CLI agent 全链（出境同意→计划确认→执行→读回验证 1 轮 ok，Executed tx 7a2fd44a，add-line 与意图一致，实体 16→17 读回精确，audit verify 绿 14 条）+ 视图计划预览真机全链（computer-use：同意面板→预览 add-text→确认执行 Executed tx 31694a2f→画布 25→26 items+时间线 live-rebuild+问题刷新；拒绝路径 RejectedByHuman 无 tx；实体 17→18 TEXT 读回精确；audit verify 绿 25 条；7 截图 docs/verification/view-closure-plan-preview-*.png）。密钥 DPAPI 全程不入日志/截图/审计（密钥前缀 grep 零命中）；视图同意面板经移存 CLI 同意文件触发（EgressPolicy 按 agentDir 持久化，自然路径亦实证）。原「计划预览仅引导验证/云端首启待真实密钥」限制解除；零应用缺陷。证据：outputs/view-closure/agent-chain.log 等 + task-11-report.md Addendum + view-closure-protocol.md 执行记录追加。
- **全分支最终审查修复波闭环（2026-11-22，feat/cad-agent-view-closure，5 Important + 2 顺手修）**：审查 verdict「With fixes」一波修复，不推翻已有闭环声明，追加边界与降级不变量补强：
  - **Imp#1 Move 处理器 OCS 反向**：Circle/Arc/MText/Insert/LwPolyline/Polyline2D/TextEntity 坐标为 OCS 原值（ACadSharp 3.8.35 不转换），此前 WCS dx/dy 直施→镜像法向实体实际位移 -dx。修：EditHandlers 私有静态 `OcsDelta`（任意轴算法 Data 侧实现，注释互指 View RenderModel.OcsMapper 防双处漂移）将 WCS delta 逆投影到 OCS 再施加。**边界明示**：镜像法向 N=(0,0,-1) 精确逆映射（Ax=(-1,0,0),Ay=(0,1,0)→odx=-dx,ody=dy）；**倾斜法向为面内投影近似**（WCS delta 投影到 OCS XY 轴取面内分量、丢弃法向分量，与渲染侧倾斜投影近似同口径）。钉住测试：镜像 Circle Center=(5,5,0) dx=3,dy=4→OCS(2,9)/WCS(-2,9)；镜像 LwPolyline 顶点同；默认法向既有 Theory 全绿（行为保持）。
  - **Imp#2 PlanPreview 前奏崩 UI**：LlmConfigStore.Load/DpapiKeyProtector.ResolveKey/SemanticOverlay.Load 此前在 try/catch 外，损坏 llm-profiles.json/semantic.json 抛 JsonException 经 MainWindow async void 崩进程（违反 §9 降级不变量）。修：前奏整体纳入 GenerateAsync try/catch→ErrorMessage+Status="Failed"+RunCompleted（不抛）；OnGeneratePlanClick 加防御性 catch→错误通道（镜像既有 async void 惯例）。钉住测试：垃圾字节 llm-profiles.json/semantic.json→GenerateAsync ErrorMessage 非空不抛。
  - **Imp#3 子图元后缀句柄不可编辑**：Insert/Dimension 子图元渲染句柄为 "{parent}:{i}"，编辑入口直传→EditViewModel.WithHandle ulong.TryParse 失败「非法句柄」，块/标注几何视图内 Move/Delete/ChangeLayer 不可达。修：三编辑入口统一经 `ParentHandle`（Split(':')[0]）剥后缀到顶层句柄。**语义明示=编辑父实体/块整体**（子图元随父平移/删除/改层）；**独立子图元编辑 deferred**（后续切片）。钉住 headless 测试：Insert 子图元选中→Move→父 Insert 移动 tx 成功；Dimension 子图元→Delete→父标注删除。
  - **Imp#4 PlanExecuted 闸旁路竞态**：此前持会话闸内触发 PlanExecuted→MainWindow Post 全视图刷新排队到 UI 线程执行时闸已释/被另一编辑持有，刷新绕闸与并发手动编辑 Doc 枚举竞态。修：executed 标志内层记录，PlanExecuted?.Invoke() 移到会话闸 Release 之后（外层 try 内、finally 前）——释闸先于事件。钉住测试：事件触发瞬间闸 WaitAsync(0) 成功（确定性）+ 既有 PlanExecuted 刷新测试时序不变仍绿。
    - **[合并前 ISTQB I3 更正，2026-11-22]** 上条"刷新绕闸与并发手动编辑 Doc 枚举竞态"的修复宣称过度：Imp#4 仅重排序（释闸先于事件），并未消除竞态——PlanPreview 执行不置 _editBusy，释闸后 RefreshAfterEdit 仍在 UI 线程无闸枚举 Doc，窄窗竞态仍在。准确口径：**释闸先于事件；刷新侧取闸仍 deferred（窄窗竞态跟踪）**。真修复=刷新侧取闸，post-merge 优先项（见 view-closure 计划执行记录 tracked deferred 小节）。代码注释同步追加式更正（PlanPreviewViewModel/EditViewModel，旧文不删）。
  - **Imp#5 §4.1④ 部分交付明示**：④纠偏计划预览的「问题清单勾选→意图（含 recommended 采纳）」交互为**后续切片**；**当前交付为 mandatory 填充（OnFillIntentFromIssues 拼接 ruleId+建议）+ 自由文本意图**，不宣称完全兑现 §4.1④ 勾选交互。
  - **顺手修 T9 IsFinite 守卫**：字高/缩放/旋转三处解析加 `double.IsFinite`（NaN/Infinity→fallback 3.5/1/0；∞ 曾通过 >0 检查、NaN/∞ 入 JsonSerializer 抛异常致编辑静默失败）。钉住测试：rotation "NaN"→审计 args rotation=0；scale/height "Infinity"→1/3.5。
  - **顺手修 T10 预览按钮轮次禁用**：确认/拒绝点击后立即禁用（_planConfirmPending 复位），至下次 PreviewReady 处理才重新启用——防纠正循环二次 confirmPlan 时确认到尚未显示的旧计划。钉住 headless 测试：确认后 ConfirmPlanButton/RejectPlanButton IsEnabled=false。
  - 其余 deferred minors（Polyline2D OCS 渲染缺口/Hatch 命中/ByBlock 口径/SignoffTxSelector/Rebuild 标签+选中/Issues 失败口径+refresh 后 rebuild/egress 专测/OCE/退出码/计数脚注等）triage defer-post-merge，不动。门禁：全 sln 连绿两次（见 outputs/view-closure/final-gate{1,2}.log）闭合 T11 形式口径 merge-blocker。
- **合并前 ISTQB 修复波（PR#19，2026-11-22，用户裁定"先修后合"：I3/I5/I2 本波修，I1/I4 tracked deferred）**：
  - **I3 过度声明措辞更正**：见上 Imp#4 更正子条（追加式，旧文不删）——释闸先于事件；刷新侧取闸仍 deferred（窄窗竞态跟踪）。
  - **I5 倾斜/退化 OCS 法向代表测试（双侧）**：既有测试仅镜像 (0,0,-1)+默认；补倾斜 (1,0,1)/√2 代表——OcsDelta 面内投影近似钉住（odx=4, ody=-3/√2；WCS 位移 (1.5,4)≠(3,4)，法向分量丢弃）+ OcsMapper 椭圆 tessellation 投影近似钉住（72 段、投影中心 (-5/√2,5)、半轴 3/√2 与 3、首点像素探针）；退化 (0,0,0)→Normalize 回落 (0,0,1) 双侧钉住（探针核定：ACadSharp DxfWriter 零法向圆整体不落盘→经内存实体钉住防御路径；倾斜法向 DXF 往返保持原值不归一）。**近似口径钉住，非精确数学**——I1 Core 共享提取时须同步审读这些数值断言。
  - **I2 Issues 失败路径审计 parity（controller 裁定：实现，§9 权威）**：IssuesViewModel.Evaluate 失败（裁定/overlay 损坏等）此前仅 ErrorMessage 无留痕，与 EditViewModel.RunRegressionCheck 故障入审计口径不一致。修：Evaluate 失败路径镜像 RunRegressionCheck 写 check-run（Source="view-check"、params{Error,overlayPresent} 字段逐字一致、前后哈希空串、嵌套守卫不重抛、留痕后 rethrow 降级不变）。钉住测试：损坏 adjudications/semantic 夹具→ErrorMessage+check-run 审计（Error 非空）+VerifyChain 绿；成功路径 parity 既有测试保持绿。
  - **I1/I4 tracked deferred**：I1 OCS 数学双源（OcsMapper/OcsDelta 两份私有实现，建议提取 Core 共享）/I4 出境同意正向无自动化测试（安全关键，真机 B1 补位）——post-merge 优先序见 view-closure 计划执行记录"合并前 ISTQB 审查 tracked deferred"小节（含刷新侧取闸/Cancel 接线）。

**幅面感知读图切片完成（sheet-aware T1-T7，2026-11-22 记录；全套件 950 测试连绿两次含 13 Zwcad 真机）**：
- **T1 Sheet 登记簿**：图框块引用检测（块名含 图框/图签/BORD 或携带 图号 属性）→ FrameBounds（块几何经插入变换展开，嵌套递归+深度守卫）+ 标题栏属性 + 框内/近邻幅面名文字关联；无图框→单默认幅面 S0（legacy 兼容）；SheetIdForPoint 实体归属（框外=null 远端残元不混入）。真实标本 9 幅面名/日期/版本号实跑钉住。
- **T2 层名归一+块遍历**：BlockWalker 单次遍历（bound xref 嵌套实体世界坐标+后缀句柄路径+归一层名 x$0$WALL→WALL）供全部检测器共用；插入变换数学 consolidation 于 Core.Cad.Affine（View RenderModel/T1 FrameBounds 同源，R1）；底图墙对经块遍历可达（真实底图在 bound xref 内）。
- **T3 属性 NativeObject 种子**：BlockAttributeSeeder 块属性→语义（A=深照型灯→Lighting 等），Provenance.NativeObject 0.95 确定性原生数据优先于图层推断；Upsert 键=handle+provenance 与 HumanConfirmed 共存；R7 陈旧种子每 pass 清理。真实标本 Lighting==1364/Equipment 147 实跑钉住。
- **T4 幅面闸口**：ISemanticContextGate 三参 CheckContext(required, targetHandle, sheetId)（DIM 默认委托两参，fake 不破坏）；标注 SheetId stamp（检测器/推断/种子/人工映射派生全链）；plan params.sheetId 契约（StepEngine 提取→三参校验）——A 幅面确认不得放行 B 幅面操作，跨幅拒绝 semantic-gate-refused 审计含 sheetId；无 sheetId=legacy 全局行为保持。
- **T5 布局视口+幅面 UI**：布局视口幅面登记（SheetId="L"+视口句柄，ViewTarget/ViewHeight 纸面变换 ViewportTransform）；视图幅面选择器+BuildViewport 裁剪（默认首幅面，sanctioned 行为变更 R4）；**布局幅面只读硬闸**（纸面编辑非本期语义，编辑工具硬禁+在途编辑切幅守卫 R8）；空布局/纸边界视口不登记（诚实）。
- **T6 视觉交叉管道**：SheetTiler 幅面字高中位数反算瓦片（1 overview+≤6 tiles）；IRenderSnapshot seam（Data 不引用 View 无环）；VisionCrossChecker 声称仅以本幅面矢量 GT 佐证才种子（LlmInference 0.9，跨幅跑偏拒绝=幻觉拦截）；短值（<3 字符）包含匹配守卫+命中优先级+多幅面已种子 claim 假歧义抑制（R9）；VisionAmbiguous 入 ScanResult 不静默。
- **T7 agent 联动 E2E + 收尾**：SheetAwareAgentE2ETests 双幅面合成夹具 fake LLM 全链——sheetId=S2 跑偏→tx failed+拒绝审计含 sheetId+Escalated 零落图；sheetId=S1→Executed+tx-commit+agent-verify 绿+验链；legacy 无 sheetId 全局放行；semantic scan stdout 加性 sheets 摘要 {sheetId,name,number}（agent 计划幅面取值依据）。InsertBlockTests CS0184 死分支警告清理（反射判定保意图）。
- **验证协议**：docs/verification/sheet-aware-protocol.md（真实图纸复核步骤：9 幅面名/属性计数 1364/底图墙对经块遍历/跨幅拒绝/空布局诚实注记/视觉管道 harness 含 IRenderSnapshot 注入示例+证据归档要求）。门禁日志 outputs/sheet-aware/final-gate1.log / final-gate2.log（950=Core 207+Data 265+Cli 173+View 262+Llm 30+Zwcad 13，0 跳过——真实标本 skip-gated 3 测试本机实跑；前后 tasklist 核查无 ZWCAD 残留）。
- **剩余边界（明示不假装）**：①代理 🔴 路径无真实标本——两份标本（原生 DWG+工作 DXF）准入均 Green 代理实体 0，🔴 拒绝仅合成 ProxyEntity 夹具覆盖；②空布局视口无内容标本——视口真实内容 E2E 缺口（合成 Viewport 夹具钉住登记/裁剪/纸面映射）；③视觉管道真实模型 E2E 留 harness——IRenderSnapshot 实现+VL reader 未接生产 CLI（fake seam 测试为自动化闸口）；④纸面编辑 deferred（布局幅面只读硬闸，不做纸面→模型逆映射）；⑤TwistAngle 与非矩形视口裁剪 deferred（视口数学仅平移+缩放）；⑥图框块内部几何编辑在 Sheets 指纹外（块定义内容变化需 Reload 失效）；⑦signoff.RollbackTx 不经布局只读闸——快照回滚作用于模型 tx 与视图当前幅面无关，与"布局只读"预期轻微张力（回滚是审计事实源操作非纸面编辑，注记在案）；⑧属性表硬编码（AttributeSemantics skill 覆写留后续切片）；⑨瓦片零重叠骑缝文字可能截半（~10% 重叠边距留后续）。**③已于 vision-wiring T1-T4 闭合（2026-11-23，见下方"视觉管道生产接线完成"块）——原文保留为历史记录。**

**视觉管道生产接线完成（vision-wiring T1-T4，2026-11-23 记录；全套件 1057 测试连绿两次含 13 Zwcad 真机 + 1 真实云端 E2E）**：

- **已交付**：VisionReader（T1，瓦片 PNG→ILlmClient ImageUrl→结构化 claims；解析失败诚实降级 ParseError+空 claims 不编造；R5 confidence TryGetDouble+[0,1] 范围闸，非法置信度与缺字段同通道跳过）；RenderSnapshotProvider（T2，Avalonia.Headless 离屏 Skia 真实帧 PNG，IRenderSnapshot 生产实现；R6 瓦片级 CropToFrameIntersect 几何相交裁剪——穿越瓦片长导线保留，幅面级锚点裁剪语义不变）；View `--vision-scan` headless 入口（T3，先于窗口分支：census 闸 **Red 拒（视觉亦拒）/Yellow 只读视觉放行**→profile/DPAPI 解析→EgressPolicy 出境闸（`--confirm-egress` 显式确认，缺则 egress-blocked 拒绝留痕不静默发送；local-only 硬约束同 agent 口径）→瓦片预渲染（R7 线程契约：渲染全部完成前不 await）→逐瓦读图 llm-call 每瓦留痕→VisionCrossChecker 本幅面矢量交叉种子→**无 seeded 不写 overlay**（防空覆盖）→vision-scan 留痕+stdout JSON）；CLI `semantic scan --vision` relay 真 View 进程（T3，Cli 不 using Avalonia，stdout/stderr/退出码 UTF-8 转发）；**T4 真实标本端到端**（DashScope qwen3-vl-plus，1#配套用房 Yellow census 3257，skip-gated 自动化 `RealVisionScanE2ETests` + 手动实战证据 `outputs/vision-wiring/`）。
- **边界闭合**：幅面感知剩余边界③"视觉管道真实模型 E2E 留 harness——IRenderSnapshot 实现+VL reader 未接生产 CLI"**自此闭合**（vision-wiring 计划/任务书所称"边界②视觉生产接线"为同一事项，编号口径注记）：生产接线+真实模型 E2E 均落地，relay 真进程路径真实行使（T3 遗留缺口关闭）。
- **R9 顺带修（T4）**：`semantic scan --vision` 与 `--skill` 并存→stderr 用法拒绝 exit 1（relay 无别名通道，--skill 静默丢弃=诚实缺口；拒绝先于一切副作用，check --skill 冲突先例）；--vision 无 --skill 正常 relay。
- **T4 成本闸（加性）**：`--max-tiles N` 瓦片预算（CLI 直通 View；跨幅面按产瓦序取前 N；**截断必须披露**——stderr 提示+stdout `tilesTotal`/`maxTiles`+审计 payload 同字段；无预算全量行为逐字保持）。实战标本 12 幅面共产 **82 瓦**，预算 6 瓦实读（云端成本上限绑定约束）。
- **真实端到端数字（2026-11-23，outputs/vision-wiring/）**：exit 0 / level Yellow / tiles 6（tilesTotal 82）/ claims 14 / seeded 0 / ambiguous 14 / usage 15960+598 tokens / parseErrors 0；审计 9 条（egress-blocked 1 + egress-consent 1 + llm-call 6 + vision-scan 1）验链绿；**无 flag 拒绝对照** exit 1 + egress-blocked + 零 llm-call；**local-only profile 对照**（合成副本改 dataPolicy）exit 1 + egress-blocked + 零 llm-call；密钥 stdout/审计全文 grep `sk-` 零命中；标本源/副本 SHA-256 前后一致（法院目录零触碰）。
- **GT 抽查（幻觉拦截实证）**：14 claims 中 **4 条无矢量 GT 命中全拦截**（设计院名先验填充／"地址→项目"标签误读／"校对人→校审人"字形误读／签章区观察不种子）；10 条 GT 精确吻合但图签/材料文字无语义类→保守不种子（宁缺勿滥）；5/6 瓦模型返回空 claims（不可读瓦片拒绝编造，与 T1 实验一致）。seeded=0→overlay 未落盘（防空覆盖规则实证）。
- **剩余边界（明示不假装）**：①视图交互按钮路径 deferred（headless `--confirm-egress` 为唯一生产视觉入口；**R3：生产由人工权衡数据出境后手动给 flag**，测试经用户授权行使该通道，不伪造人工确认语义）；②claim.Region 瓦片几何校验 deferred（region 当前仅描述文本，佐证走幅面级矢量 GT）；③bulge 弧保守限（RenderModel 弧段以直弦近似——视觉输入弧几何非精确；GT 交叉验证用真实矢量不受影响）；④真实设备类种子未实证（本轮 overview 图签区 claims 无语义类；LlmInference 0.9 种子通道由 fake/合成夹具钉住，真实设备种子待可读瓦片命中——诚实缺口）；⑤profile 默认 timeoutSeconds **已按用户裁定 60→600（2026-11-23）**，生产 profile 同步 600；视觉长输出超时缺口闭合（E2E 不再 override）；⑥瓦片零重叠骑缝（幅面感知⑨既有）；⑦多幅面预算分配策略（当前按产瓦序前 N，幅面均衡覆盖留后续）。
- **门禁**：`outputs/vision-wiring/gate1.log` / `gate2.log`（**1057**=Llm 30+Core 208+Data 309+View 289+Zwcad 13+Cli 208，0 跳过——真实标本 skip-gated 与真实云端 E2E 本机实跑；前后 tasklist 核查无 ZWCAD 残留）。验证协议：`docs/verification/sheet-aware-protocol.md` §6 视觉接线实战节。

**tile-coverage 切片完成（T1-T3，2026-11-23 记录；全套件 1082 测试连绿两次含 13 Zwcad 真机 + 每门禁 1 真实云端 E2E）**：

- **根因三重实证**（`outputs/api-probe/tile-coverage-out.txt`，真实标本 1#配套用房0928）：①微字峰——尺寸/轴号字高 2.4 世界单位（477 个）主导字高中位数 → regionW≈280=幅宽 0.27%，5/6 瓦空（all=0/content=0）；②幻影锚——锚点 X/Y 独立中位数合成落空区（L 形锚簇图签瓦不中图签）；③单锚偏移网格——以单锚中心偏移铺格不覆盖大幅面内容簇。before 基线：claims 14 / seeded 0 / ambiguous 14，coverage≈0.003（6 瓦×0.27% 幅宽；vision-wiring 实战存档 `outputs/vision-wiring/p1-vision-stdout.json`）。
- **T1 修复**（SheetTiler）：字高间隙分割（distinct 排序相邻倍率≥8 判双峰→上峰计数加权中位数；单峰保持旧中位数口径逐字不变——实证间隙 2.7→150=55× vs 峰内≤1.6×，裕度充分）+ 密度格网选瓦（瓦落内容实体密集格、空格不选；R5 单遍实体分桶 O(N+命中格数)，格网维度 long 运算防整型溢出，同分按 (cx,cy) 升序钉住）+ 图签瓦改锚层实体真实 bbox 中心 + **10% 骑缝重叠边距（vision-wiring 剩余边界⑥＝幅面感知⑨"瓦片零重叠骑缝文字可能截半"自此闭合）** + TilingResult.ContentCoverage 披露（被≥1 非 overview 瓦覆盖的 inSheet walked 实体比例）。
- **T2 披露**（VisionScanMode stdout 加性 coverage/regionW/heightBasis）：coverage=实体数加权聚合 sum(coverage_i×inSheet_i)/sum(inSheet_i)（0 权幅面跳过）；regionW/heightBasis 取首幅非 none 值；**截断前全量切瓦口径**——--max-tiles 预算截断进一步降低实际已读覆盖，tilesTotal/maxTiles 另行披露，不假装已读=全覆盖。双幅面加权聚合+首非 none basis 由 fake 夹具直接钉住（T2 遗留 minor 闭合）。
- **T3 真实对照**（`outputs/tile-coverage/`，combat-t3 副本 + --confirm-egress --max-tiles 6，DashScope qwen3-vl-plus）：exit 0 / Yellow / tiles 6（tilesTotal 82）/ **claims 14→77 / seeded 0 / ambiguous 14→54 / completionTokens 598→2742**（before 5/6 瓦空 claims 仅 5 token → after 6/6 瓦产 claims）/ **coverage≈0.003→0.741074**（加权聚合，探针与 stdout 精确一致）/ regionW 280→75767.5 / heightBasis=bimodal-upper；**首幅 ContentCoverage 0.003→1.0000**（603/603 实体覆盖、空瓦 0；skip-gated 测试 RealSpecimen_TileCoverage_ContentCovered 钉住 >0.5 + 空瓦≤1）。诚实披露：纯微字详图幅面4/6（单峰 median regionW=280）6 瓦上限下覆盖率≈0.09，不假装全覆盖。
- **GT 抽查**（`outputs/tile-coverage/t3-gt-spotcheck.md`）：新增可读瓦 claims（图纸升版记录表/英文图签行/证书编号/签署栏多岗位）与矢量 GT 逐字吻合；20/54 无 GT 命中全拦截（含凭空项目名"君悦新居"幻觉、设计院名先验填充变体、688→666 数字误读）——**可读≠不幻觉，交叉验证闸不可省**；地址行 before 误读"项目：成都…"被拦 → 本轮逐字正确读出"地址：成都市天府大道中段688号大源国际中心"。
- **审计/安全/成本**：审计 8 条验链绿 + 外部锚通过；密钥 stdout/stderr/审计全文 grep `sk-` 零命中；标本源/副本 SHA-256 前后一致（源目录零触碰，working/sidecar 全落副本目录）；云端总瓦披露：对照 1 跑 + 门禁 2 跑（各含真实云端 E2E）= 3 跑×6 瓦 = **18 瓦**（每跑≤6 瓦绑定约束保持）。
- **剩余（明示不假装）**：vision-wiring 剩余边界④真实设备类种子未实证**保持开放**——本轮 seeded=0（GT 吻合 claims 均图签/注记文字无语义类；"电井/配电间/给水"等设备类 claim 无矢量文字命中被拦），待可读瓦命中设备类 GT 后复核；⑦多幅面预算分配策略仍开放（本轮 6 瓦全落幅面1，按产瓦序前 N；coverage 披露为截断前口径）。
- **门禁**：`outputs/tile-coverage/gate1.log` / `gate2.log`（**1082**=Llm 30+Core 217+Data 319+View 293+Zwcad 13+Cli 210，0 跳过——真实标本 skip-gated 与真实云端 E2E 本机实跑；前后 tasklist 核查无 ZWCAD 残留）。

**块级纠偏切片完成（block-fingerprint T1-T5，2026-11-23 记录；全套件 1246 测试连绿两次含 10 Zwcad 真机 + 每门禁 1 真实云端 E2E）**：

- **已交付（T1-T4）**：BlockFingerprint（LINE/CIRCLE/ARC 白名单→bbox 平移归零+最长边归一+量化 1e-3+规范排序 SHA256；结构签名相容=三类计数逐等+|Aspect 差|≤0.2；贪心逐类最近邻平均距离，线方向反转不变/弧角差权重 0.5；NearThreshold=0.15、Confidence=0.9−2d）→ BlockSymbolMatcher 三级（精确自动 GeometryInference 0.9 marker="几何指纹精确匹配:" / 近似建议只披露不自动 / 无命中高频≥3 歧义 reason∈{代理图形不可读,无匹配}；符号读失败/超白名单跳过计数不整体失败；排除 *Model/*Paper/$LinePat$/$TCHSYS$/空定义/图框 AttDef+bbox≥500）→ SemanticScanner 加性 symbolPackages 末位可选参 + scan stdout 四加性键 blockAutoSeeded/blockSuggestions/blockAmbiguous(+guidance)/symbolsSkipped（旧键逐字不变）+ 审计加性计数 → semantic map-block 复核制（重提取指纹精确或 d≤0.15 否则拒绝留痕；HumanConfirmed 1.0 落该块全部 walked Insert 含嵌套后缀句柄；六 reason 拒绝面 semantic-map-block-rejected；overlay-only before==after；Yellow 豁免沿用 semantic map 先例不调 GateDetector）。
- **T5 真实标本实证三级数字**（电施强电副本 + electrical 324 符号，`outputs/block-fingerprint/combat-evidence.md` / `combat/scan-stdout.json`，exit 0）：**blockAutoSeeded=102 / blockSuggestions=16（候选 30）/ blockAmbiguous=68 / symbolsSkipped=0**（324 全量 loaded）；旧键 annotations=4450 / ambiguousLayers=130 / sheets=9。自动种子 7 块定义精确命中落 102 实例（信号线路×52 / 端子×38 / 物件(方形)×10 / 应急疏散指示标志灯×2）；歧义 reason 分布 无匹配×33 + 代理图形不可读×35。map-block 真实确认一例：`$equip$00002501`→双联单控开关（复核 exact=false d=0.099≤0.15，122 实例 HumanConfirmed 1.0，overlay-only DWG 字节不变，audit verify 绿）；re-scan 保留 122 HumanConfirmed + 三级确定性重算一致 + 验链绿（3 条）。
- **阈值校准（R3，`t5-probe/probe-out.txt`）**：R3 强制探针条件="近似建议全空且目视有相似块"**未触发**（建议 16 块非空）→ **NearThreshold=0.15 保持不动（不静默改阈）**。校准证据：非精确可读块 148 到最近相容符号距离桶 [0,0.05]=5 / (0.05,0.10]=10 / (0.10,0.15]=1 / (0.15,0.30]=18 / >0.30=52，另 62 块无相容签名；globalMin=0.0035（LG-QQ1→电锁按键）。0.15 边界区分 16 建议块与 18 近失块（含 $equip$00002832 d=0.1883），上调增误报风险，保持。
- **诚实边界更正（实证钉死，追加式不删旧注记）**：
  - **§13 "$equip$00002832×1364 代理图形不可读" 预期更正**：实战该块定义暴露最小可读几何占位（hist=AttributeDefinition:2,Circle:1,Line:2,LwPolyline:1,Point:4），fp 非 null（lines=2,circles=1），最近相容 GB 符号=信号灯/灯 d=0.1883>0.15 → **reason=无匹配**（非代理图形不可读）。reason="代理图形不可读" 仅适用块定义**无白名单原生几何**者（fp NULL，如 $TelecSys$00000152=AttDef+Insert）；部分 $equip$ 块（00002928/00002501）暴露几何→落建议。真实测试断言 reason 合法集 {代理图形不可读,无匹配} + $equip$00002832 高频披露入歧义不静默丢弃 + 绝不自动种子，不赌具体块的具体 reason。
  - **§13 "GB 精确命中可能 0" 更正**：实战精确命中**非 0**（102 实例/7 块定义）——语料虽以 TArch $equip$+自定义块为主，仍含可精确匹配的标准符号块（端子/信号线路等）。三级价值在建议+人工映射两翼的判断保持，但不宣称命中必 0。
  - **§13.2 "闸下保证" 措辞更正**：Confidence=0.9−2d 仅 **d≥0.05 时 conf≤0.8**；d<0.05 → conf>0.8（实战 30 候选中 17 个 conf≥0.8，Confidence(0.05)=0.80 精确）。"近似永不自动落种子"是**结构保证**（近似档只披露不落标注，与置信值无关），非全值域公式闸下。T5 真实测试 brief 原断言 `c.Confidence<0.8` 过度，更正为公式一致性 + 值域 [0.6,0.9] + 三级互斥（建议块不落自动种子）；T1 钉住 Confidence(0)=0.9/Confidence(0.15)=0.6<0.8 保持，T2 合成夹具 d≈0.065 闸下断言保持。阈值与公式均未改（R3）。
- **剩余 deferred（明示不假装）**：View 块级纠偏面板（CLI 先行切片先例）；建议持久化（scan 确定性可重算，map-block 复核制）；子块递归指纹（嵌套经自身定义匹配，§13.6）；裸旗标统一/数字 class 空白绕过（2b-ii-b 既有遗留，本切片未触及）；高频自定义块（如 `2` 86 线+10 弧）无相容签名→静默/无匹配，结构签名预过滤的召回边界（宁缺勿滥）。
- **spec §13 文字窄化明示（R9 裁定，最终审查修复波追加披露；后续切片补齐）**：①结构签名"圆半径比"未入 Compatible——现行相容判据=三类计数逐等+|Aspect 差|≤0.2，半径差只入 Distance，方向安全预过滤比 §13 文字更宽；②歧义层 histogram/bbox 诊断字段未输出——BlockAmbiguous 现为四字段（BlockName/Instances/Reason/SampleHandles，guidance 为 stdout 序列化附加），无 §13 设想的直方图/bbox 诊断；③NaN/∞ 实体剔除披露计数未实现——Extract 内静默整条剔除，无剔除计数键。三处均为实现窄于 spec 文字的诚实披露，非行为回归；追加式旧文 0 删。
- **门禁**：`outputs/block-fingerprint/gate1.log` / `gate2.log`（**1246**=Llm 33+Core 235+Data 358+Acad 9+View 365+Zwcad 10+Cli 236，**4 跳过**=Acad 真机无 AutoCAD 运行时；0 失败；真实标本 skip-gated 与真实云端 E2E 本机实跑；前后 tasklist 核查无 ZWCAD 残留；两跑数字一致无 flake）。

**§4.1④ 勾选→纠偏意图交互补全（feat/cad-agent-issue-check-intent，2026-11-23 记录；PR#19 Imp#5 deferred 闭合）**：

- **勾选→意图交互落地**：IssueEntry 新增 IsChecked INPC 属性（核心字段 get-only/构造签名不变）+ 问题行 CheckBox（TwoWay 绑定，徽章/RuleId 仍在 Expander）。OnFillIntentFromIssues **checked 优先**：有勾选→intent=勾选项（mandatory+recommended 混合），行格式 `{RuleId} [{Force}]: {Suggestion}`（Force 枚举名原文）；**零勾选兜底**保持既有 mandatory 自动填充（旧格式 `{RuleId}: {Suggestion}` 逐字不变）+ 状态栏提示「未勾选问题，已按 mandatory 自动填充」。Imp#5 所称「后续切片」自此交付，§4.1④「用户勾选问题→LLM 生成步骤计划→预览→人工确认执行」字面交互闭合。
- **rule-adopted 采纳审计 kind（View 侧新增写入，Core AuditEntry 既有 API 消费，检查器逻辑零改动）**：勾选项含 Recommended 时经 MainWindow 既有写审计实例（writeAudit/signoffIdentity 同源）追加——Source=`rule`（镜像 rule-adjudicated 先例）、Session=`view` 常量口径、Target=图纸路径、params=`{adopted:[{ruleId,clause,standard}],count}`（**仅 Recommended 勾选项**——Mandatory 是义务非采纳，不入采纳审计）；BeforeHash==AfterHash==drawing.ComputeHash()（填充不改图纸）；**每次填充动作各留一条**（append-only 决策日志，不去重）。审计先行（镜像 AdjudicationViewModel）：写失败→行内错误、中止填充，不崩 UI。零勾选兜底/Mandatory-only 勾选无 rule-adopted 条目。
- **忽略=未勾选正常决策不留痕**（§6.2 recommended 采纳/忽略/修改后采纳口径）：未勾选问题不产生任何审计条目；修改后采纳经意图框自由文本编辑承载（既有能力）。
- **跨刷新勾选保持（会话级，不持久化）**：IssuesViewModel 持勾选 key 集（key=RuleId+"|"+Targets 逗号连接），Refresh 重建条目按 key 恢复 IsChecked；取消勾选同步移除 key；关闭窗口即清（不落盘）。
- **门禁**：View.Tests 全量 376 绿 + 全 sln **1214**/0 失败/4 跳过（基线 main 1203 + 本切片 11 新测试；TDD 先红后绿，证据 outputs/issue-check-intent/）。
- **修复注记（fixwave2，2026-11-23 合并前 ISTQB 修复波，追加式更正）**：
  - **I1 CheckKey 碰撞修复**：勾选 key 加 Message 区分因子——key=RuleId+"|"+Targets 逗号连接+"|"+Message。标高规则 mode 省略（skill 自定义规则）时同一标高文本产两条 issue（小数位 vs 符号），同 RuleId 同 Targets，旧 key 碰撞致「勾其一刷新后两条全勾（意图混入未采纳 + adopted 重复/count 失真）/取消其一两条全丢（漏记采纳）」。同 rule+targets+message 的真重复视为同一条（去重语义，代码注释明示）。覆盖：IssuesViewModelTests.Refresh_SameRuleSameTargets_DifferentMessage_CheckKeysIndependent + MainWindowHeadlessTests.FillIntent_SameRuleTargetsCollision_CheckedOne_AdoptedCountOne_CheckPreserved。
  - **I2 填充入口结构闸 + 措辞更正**：OnFillIntentFromIssues 首行加 `_editBusy/_diffBusy/PlanPreview.IsBusy` 结构闸（镜像 OnGeneratePlanClick；busy→不填充不审计）；UpdatePlanPanel/SetEditBusy busy 时 FillIntentButton 同步禁用。**措辞更正**：implement-report §6.1 原「未新增防护也未恶化」类表述更正为——**新增读点（TryAppendRuleAdoptedAudit 的 ComputeHash）已加结构闸（_editBusy/_diffBusy/IsBusy）**，在途编辑/计划编排期间填充入口一律拒绝、该读点不可达（读点本身仍无会话闸，与 I4 同边界，入口结构闸闭合在途竞态窗）。覆盖：FillIntent_DuringInFlightEdit_BusyGate_NoFillNoAudit（持闸制造在途编辑 + RaiseEvent 绕过禁用态 → 哨兵不变 + 无 rule-adopted）。
  - **M1/M2**：指针点击测试改点第二行复选框并断言第二条目翻转（闭合「绑定正确条目」透镜，含有界布局确定性等待）；新增 Informational-only 勾选测试（入意图带 [Informational] 标签 + 不入采纳审计，第三分支钉住）。
  - **M3 门禁数字更正**：上门禁注记 376/1214/11 为 feature 提交（4f6f136）时点口径；Minor 修复提交（34072c9）后终态为 **377/1215/12**。fixwave2 终态：View.Tests **381**/0/0 + 全 sln **1219**/0 失败/4 跳过（+4 新测试：I1 VM 碰撞/I1 采纳 count/I2 busy 闸/M2 Informational；ZWCAD 前后 tasklist 无残留；证据 outputs/issue-check-intent/fixwave2-*.log）。

**块级纠偏诊断披露补全（feat/cad-agent-block-diagnostics，2026-11-23 记录；R9 窄化②③闭合，追加式旧文 0 删）**：

- **② 歧义诊断字段闭合（§13.2 字面"实体直方图, bbox"）**：BlockAmbiguous 加性末位 `Histogram`（块定义**全部实体**类型计数，GetType().Name **含非白名单**——诊断全量口径，与指纹白名单剔除口径区分；按 Type Ordinal 排序）+ `Bbox`（**可空**——PointsOf 口径复用：线端点/圆外接两角/多段线顶点/文本插入点，AttDef : TextEntity 插入点亦参与【探针实证】；整条非有限守卫与 Extract 一致——任一点 NaN/∞ 该实体整条跳过；零点→null，Hatch-only 代理块即 null）。**JSON 序列化陷阱钉死**：Histogram/Bbox 用**命名 record**（`BlockHistEntry(Type,Count)`/`BlockBbox(MinX,MinY,MaxX,MaxY)`，namespace 级 public 同既有 records 模式）——ValueTuple 的 Item1/Item2 是字段非属性，System.Text.Json 序列化为 {}；E2E 断言 histogram[0] 为对象且 type 非空防退化。stdout blockAmbiguous 项加性 histogram/bbox（旧字段 block/instances/reason/sampleHandles/guidance 逐字不变）。
- **③ NaN/∞ 剔除披露闭合（§13.4 字面"剔除+披露"）**：`Fingerprint` 加性末位 `DroppedNonFinite`（Extract Finite 守卫命中整条剔除计 1）+ `Extract(entities, out int droppedNonFinite)` 加性重载（旧单参签名逐字保持委托；**null 返回路径剔除数不丢**——NaN-only 代理块仍披露）；`BlockMatchResult` 加性末位 `NonFiniteDropped`（matcher 内**块+符号全部 Extract 调用** dropped 累计，含跳过符号与代理块的 null 路径；无符号早退路径 0）；stdout 加性键 `nonFiniteDropped` **恒在**（无包 0）+ 审计 semantic-scan params 加性 `nonFiniteDropped`。NaN 夹具经 DxfWriter/DxfReader 往返保 NaN（探针实证），E2E 全链 stdout/审计双通道=1 钉住；histogram 全量（NaN 线仍计数）与指纹/bbox 剔除口径区分钉住。
- **① 圆半径比签名仍 deferred（明示不做）**：Compatible 判据保持三类计数逐等+|Aspect 差|≤0.2，半径差只入 Distance——本切片仅 scan 诊断披露面，匹配行为零改动；View/CLI map-block 逻辑零改动；Scan 签名/旧 stdout 键/审计旧字段逐字不变（records 加性末位，构造点仅 matcher 内部同步）。
- **门禁**：`outputs/block-diagnostics/gate-sln.log`（**1292**=Llm 33+Core 235+Data 379+Acad 9+View 381+Zwcad 10+Cli 245，**4 跳过**=Acad 真机无 AutoCAD 运行时；0 失败；基线 main 1278 + 本切片 14 新测试【T1 指纹 4+T2 匹配器 7+E2E 3】，真实标本 skip-gated 本机实跑【+Histogram 非空/排序断言】；TDD 先红后绿【E2E 运行时红 3+Data 编译红】；ZWCAD 前后 tasklist 无残留）。
- **门禁数字更正（micro fix wave，追加式旧文 0 删）**：上行数字为 feature 提交（3ecca6d）时点口径；审查时点（76ac080）终态 **1294/0 失败/3 跳过/15 新测试**（Data 380；Acad 10 过/3 跳=本机 AutoCAD 运行时可用性变化，非代码问题——PR#22 M3 先例同类；证据 `outputs/block-diagnostics/final-gate.log`）；本波 M2 再 +1 退化 null dropped 判别测试 → Data **381**（本波 Data.Tests 全量复跑绿），对应全 sln **1295/16 新测试**（Acad 过/跳随本机运行时可用性口径同上）。

**真实 🟢 闭环重取尝试（2026-11-23，用户天正电气 5.0 转换件；诚实记录 Yellow，🟢 重取仍未闭合）**：

- **背景**：上条"通知感知准入闸口交付闭环"剩余边界⑤"真实 🟢 闭环待用户天正【文件布图→图形导出】T3 转换件重跑（期望 census 空 Green）"。用户提供转换件 `outputs/tarch-t3/1#配套用房0928_t3.dwg`（1.3MB，源 D: 零触碰副本；6 SHX 字体存档 fonts/，ACadSharp 解析不依赖字体），预期 T3=plain 实体（天正对象炸开）census 0 → intake Green。
- **实测结论（非预期，诚实钉住不假装 Green）**：该转换件**仅重存未炸开**——SHA256 与 native2 原图不同（db36cfcf… vs b0b63d48…，体积 1323713 vs 1422223 字节），但对象图 **3636 实体句柄与 native2 逐一一致**、版本同 AC1018；census **残留 3257/30 类（TCH_WALL 968/TCH_DIMENSION2 459/TCH_TEXT 355/TCH_OPENING 324/TCH_ELEVATION 278…）**，与 native2 重普查（`outputs/tarch-gate-recombat/intake-s2.json`）**逐类一致**。故 intake **Yellow（exit 2）**，非 Green；TCH 自定义对象全部保留，"T3 转换"未真正炸开（天正电气 5.0 图形导出未勾选炸开/或仅重存）。
- **scan 对照（零炸开实证）**：无 --skill 基线 **annotations=639（Wall 513/Column 48/Axis 38/Annotation 22/Window 18）/ ambiguous=153 / sheets=12**，与旧 native2 盲子集 scan（`outputs/tarch-native2/scan-stats.txt` 639/153/12）**完全一致**——几何/文字保留度 100%（因对象图未变，非"炸开产更多 plain 实体"的预期变化）。--skill electrical 对照：annotations=727（基线 639 + **88 Auxiliary 块几何指纹自动种子** GeometryInference 0.9）/ ambiguous=153 / sheets=12 / blockSuggestions=17 / blockAmbiguous=56 / symbolsSkipped=0。
- **tx 闭环判定（被 Yellow 阻断，非映射缺失）**：旧用户批准 7 层映射（`outputs/tarch-native2/.cadagent/semantic.json`，2026-11-22：构造柱-何俊→Column / E-立门窗→Window / A-WINDOW→Window / A-Dim-Symb→Annotation / CR-建-Dims-IDEN→Annotation / COLU_DE_NUM→Annotation / A-Anno-Note→Annotation）层名在 T3 **全部保留**（映射裁定可复用）；但 census 残留 → Yellow → **gate-at-run 在语义闸之前全图级拦截修改**，tx 闭环被 census 阻断。实证：add-line plan run → **run-rejected level=Yellow unparsedCount=3257**（guidance 含 T3 指引）+ working/副本字节不变 + 验链绿（3 条：intake/semantic-scan/run-rejected）。**未执行 map、未伪造人工确认、未跑 label-luminaires tx**（Yellow 下任何 tx 必拒，复用映射无益于闭环）。
- **旧盲子集证据作废的重取闭合状态**：🟢 重取**仍未闭合**——本次转换件非真正炸开件，census 非 0，无法承载真实 Green 闭环；需用户在天正【文件布图→图形导出】**勾选炸开/真正导出 T3 plain 实体**（census 0）后重跑。本次诚实记录 Yellow + 类分布，不假装 Green（spec ⑥）。当前 🟢 证据仍为合成 E2E（SheetAwareAgentE2E/LightingBenchmark 等）。
- **新增 skip-gated 测试（实现先行实证钉住，非驱动）**：`tests/CadAgent.Cli.Tests/RealT3SpecimenTests.cs` 2 测试——`RealT3Specimen_IntakeYellow_CensusResidualMatchesNative`（钉住 Yellow exit 2 + census 3257/30 类 + TCH_WALL 968 + 实体 3636 + Unknowns 0 + sidecar 回写 + 副本字节一致）/ `RealT3Specimen_RunRefused_YellowGateBeforeSemantics_ReadOnlyAudit`（钉住 run-rejected Yellow unparsedCount 3257 + working/副本字节不变 + 验链绿）。census 闸口已于 UG 切片交付，本测试首跑即绿（2/2，标本在本机实跑非跳过），镜像 SheetTilerTests/UnparsedGateCliTests"实现先行、实证钉住非驱动"先例；缺标本显式 Skipped。
- **标本只读**：源/副本 SHA-256 前后一致（intake/scan/run-refusal 全程零触碰，working/sidecar 全落临时项目）；证据 `outputs/tarch-t3/`（intake.json/census-distribution.txt/scan-stats.txt/scan-electrical-stats.txt/mapping-reuse-check.txt/audit-records.txt/hashes-{before,after-scan,after-run}.txt/audit-evidence/）。
- **门禁**：Cli.Tests 全量 + 全 sln 一次（基线 1279 + 本切片 2 新测试；Acad 真机跳过口径保持——RUNASADMIN 阻断未解除，勿触 AutoCAD COM；ZWCAD 前后 tasklist 核查无残留）。

**TExport T3 部分转换实证 + intake 用法守卫（fix wave，2026-11-23 记录；追加式更正，旧文 0 删）**：

- **标本替换实证（部分转换，非全炸开）**：用户将 `outputs/tarch-t3/1#配套用房0928_t3.dwg` 替换为真天正电气 5.0【文件布图→图形导出】TExport 件（旧"另存为"件存档 `1#配套用房0928_t3-resave-not-exploded.dwg` 留作对照）。实测 **census 残留 2769/29 类（TCH_WALL 962）**，实体 3636/Unknowns 0/AC1018（证据 `outputs/tarch-t3/intake-positional.log`）——较旧另存为件（3257/30 类，TCH_WALL 968）**部分转换**（488 对象/1 类被转换）但未全炸开 → intake 仍 **Yellow（exit 2）**，非 Green。**🟢 闭环仍未闭合**：待真正全炸开件（天正炸开选项/分解对象路径待用户核定）后重取。上条"逐类一致/仅重存"结论仅对旧另存为件成立，对现 TExport 件以本条为准。
- **测试口径更正（稳健断言，不赌精确总数）**：`RealT3SpecimenTests` 原钉 3257/30/968/3636 精确值，标本替换后红。改稳健口径——Yellow exit 2 + census>0 + TCH_WALL 类存在 + sidecar 回写总数与 stdout 一致 + run-rejected 留痕（unparsedCount>0）+ working/副本只读字节一致 + 验链绿；测试名 `RealT3Specimen_IntakeYellow_CensusResidualMatchesNative` → `…CensusResidualPartialConversion`（现标本与 native2 不再逐类一致，旧名不成立）；两变体数字（3257/30 与 2769/29）及来源注释留档。
- **intake 用法守卫修复（根因已核定）**：误用选项形态 `intake --drawing x --project y`（图纸路径应为 positional：`intake <path> --project y`）→ StripOptionValues 剥选项后 positional=["intake"] → CommandRouter intake 分支 `positional[1]` 裸抛 IndexOutOfRange（Run 兜底打印原生英文消息 exit 1，无用法指引）。修：intake 分支 positional.Length<2 → stderr「用法: intake <图纸路径> [--project <目录>]」+ exit 1，守卫先于 Intake/OpenAudit（**audit.db 不创建**——用法错误不留审计，RequireOption/semantic 子命令守卫先例；.cadagent 目录由 Run 统一创建属既有行为）。**仅修 intake 分支**——run/rollback 等其他命令同型问题留后续切片（不扩 scope）。覆盖：`CommandRouterTests.Intake_MisusedOptionForm_UsageError_ExitOne_NoAuditDb` + `Intake_NoPositional_UsageError_ExitOne_NoAuditDb`（先红后绿，RED 证据 `outputs/tarch-t3/fixwave-red.log`）。
- **门禁**：Cli.Tests 全量 **246**/0 失败/0 跳过 + 全 sln 一次 **1283 通过/0 失败/3 跳过**（基线 1281 + 2 新测试；Llm 33/Core 235/Data 368/View 381/Cli 246/Zwcad 10 真机实跑/Acad 10+3 跳过）。Acad 真机跳过口径保持（勿触 AutoCAD COM——用户会话可能开着）；ZWCAD 前后 tasklist 无残留；post-tasklist 出现 acad.exe（pre 无）——Acad 3 真机测试均 skip-gated 未激活 COM，判定为用户运行期间自开会话，零触碰诚实记录。证据 `outputs/tarch-t3/fixwave-{red,green-targeted,cli,sln}.log` / `fixwave-tasklist-{pre,post}.txt`。

**真实电气 T3 🟢 准入闭环首证（2026-11-24 记录；跨专业根因核定 + 同专业全炸开实证；追加式，旧文 0 删）**：

- **跨专业根因（用户核定，实证成立）**：1#配套用房为**天正建筑专业绘制**经电气插件导出——他专业对象原样保留代理（TExport 部分转换 2769/29 类仍 Yellow，上条注记）；**同专业导出全炸开**：电气图 `16#1116.dwg`（native Yellow：census **398/17 类**，残留全部为天正建筑专业类 TCH_WALL 139/TCH_TEXT 94/TCH_COLUMN 55/…含设备类 TCH_EQUIPMENT 17，Entities 1615/Unknowns 0/AC1027）经**电气 TExport** → `16#1116_t3.dwg` census **0** → intake **Green（exit 0）**（Entities **2565**/Unknowns 0/unsupported []/unparsed 键缺如/AC1018）。标本对副本化 `outputs/tarch-elec/`（源 D: 零触碰）；census 对照 `census-comparison.md`（398 代理炸开为 plain 实体 +950，Line +607/Text +194 为主）。
- **真实 🟢 准入闭环状态更新（准入面闭合，tx 面待裁定）**：旧"真实 🟢 闭环待用户 T3 转换/待全炸开件"状态自此**准入面闭合**——首个真实 Green 准入证据（此前真实 🟢 仅合成 E2E，盲子集证据作废口径保持）。**tx 闭环仍开放**：此为新图纸，无用户先前批准映射（两 combat 项目 semantic.json Mappings=[]、HumanConfirmed 0；controller proj-t3 无 semantic.json），tx 留待用户对不明确层的映射裁定，本 take 未执行 map、未伪造确认、未跑任何 tx。
- **scan 实战（T3 副本临时项目，基线 vs --skill examples/skills/electrical）**：基线 **annotations=1686**（Wall 773/Annotation 767/Axis 88/Door 44/Equipment 9/Window 3/Device 2；provenance LayerInference 858/GeometryInference 817/**NativeObject 11**——电气块属性种子"照明配电箱"等，建筑件 0）/ **ambiguous=71** / **sheets=1（S0 单幅面**——图框为炸开线元无图框 Insert，诚实记录）；electrical 对照 **annotations=1774**（+88 Auxiliary）/ ambiguous=71 / sheets=1 / **blockAutoSeeded=88（全部「端子」几何指纹精确匹配 0.9）/ blockSuggestions=9（$equip$* 箱柜/摄像机等，d 0~0.137）/ blockAmbiguous=40（无匹配 15 + 代理图形不可读 25）/ symbolsSkipped=0**。与建筑 T3 件（639/153/12；electrical 727/17/56）对照：标注 +1047（全炸开可扫实体更多 + NativeObject 种子）、不明确层 71 vs 153、幅面 1 vs 12。**诚实注记**：census 0（模型空间全炸开）≠ 所有块定义可读——25 块块内零白名单几何（fp null 走"代理图形不可读"同构分支，非 census 代理残留）；披露侧 guidance"建议天正 T3 转换后重扫"对已 T3 件属误导文案（既有拼接逻辑，留后续切片不扩 scope）。
- **top 不明确层（供 controller 转用户裁定，entityCount 降序前 5）**：█HVAC█签名 17466 / 0 层 16255 / 2D_A建筑-L屋顶 4113 / 0dt 1161 / A建筑-铝板 480（全 71 层清单 `outputs/tarch-elec/ambiguous-top.txt` 前 15 + scan JSON 全量）。
- **新增 skip-gated 测试（实现先行实证钉住，非驱动）**：`tests/CadAgent.Cli.Tests/RealElecT3SpecimenTests.cs` 2 测试——`RealElecT3Specimen_IntakeGreen_CensusZero`（Green exit 0 + unsupported 0 + **unparsed 键缺如** + Green 不落 sidecar + 副本字节一致）/ `RealElecNative_IntakeYellow`（Yellow exit 2 + census>0 + TCH_ 类存在 + TCH_EQUIPMENT 设备类存在 + sidecar 回写一致 + 副本字节一致；任务口径"TCH 电气类"按实测诚实钉住为设备类——残留类整体为建筑专业类，跨专业根因实证）。稳健口径不赌精确总数（镜像 RealT3SpecimenTests fix wave 先例）；census 闸口已于 UG 切片交付，首跑即绿（2/2 本机实跑非跳过），诚实记录非红绿驱动。既有 RealT3SpecimenTests（建筑件 Yellow 证据）零改动。
- **标本只读**：源副本 SHA-256 全程一致（`specimen-sha256-{before,after-scan,after}.txt`）；working/sidecar 全落临时项目；两 combat 验链绿（各 2 条 intake/semantic-scan，scan before==after）。
- **门禁**：Cli.Tests 全量 **248**/0 失败/0 跳过 + 全 sln 一次 **1285 通过/0 失败/3 跳过**（基线 1283 + 2 新测试；Llm 33/Core 235/Data 368/View 381/Cli 248/Zwcad 10 真机实跑/Acad 10+3 跳过）。Acad 真机跳过口径保持（RUNASADMIN 阻断未解除，勿触 AutoCAD COM）；ZWCAD 前后 tasklist 无残留；acad.exe PID 22628 前后同在（用户自开会话，零触碰）。证据 `outputs/tarch-elec/`（census-comparison.md/scan-stats.txt/block-fingerprint-tiers.txt/ambiguous-top.txt/mapping-reuse-check.txt/audit-records.txt/gate-{cli,sln}.log/tasklist-{pre,post}-sln.txt/closure-report.md）。

**真实电气 T3 🟢 全闭环收尾（2026-11-24 记录；用户批准 34 层映射 + tx 闭环实证；追加式，旧文 0 删）**：

- **用户裁定（2026-11-24 批准，ask_user_reply 记录；映射来源=用户本轮批准，不伪造确认）**：34 层 `semantic map` 于**新副本临时项目** `%TEMP%\elec-t3-closure-34` 逐层执行（不污染既有 probe 证据项目；源零触碰）。裁定表摘要——**Annotation 17**：█HVAC█签名/PUB_TEXT/公用-图框/TEL_TEXT/C-FRAM/TEL_LEAD/电子签名/公用-图框-说明/DZQM/预留给水点/建筑-电气-说明/表格/LIGHTNING_DIM/DIM_SYMB/DIM_ELEV/照明平面文字层/DIM-箱柜；**Auxiliary 9**：2D_A建筑-L屋顶/A建筑-铝板/SHTLN/G总图-R市政道路/PUB_HATCH/建筑-空调/S_BEAM/Defpoints/E-BACKGROUND；**Axis 2**：建筑-轴标/AXIS_TEXT；**Column 1**：COLU；**Equipment 2**：C-EQUP/WIRE-避雷；**Lighting 1**：WIRE-照明；**Device 2**：0dt/LWIRE。**5 层不映射（用户裁定混合/块级领地）**：0/符号/A建筑-C门窗/C/qd。实测 **34/34 exit 0**，审计 semantic-map 34 条 + 验链绿；层名 █/中文经 Git Bash UTF-8→dotnet 参数编码实证无损（SemanticMap 层名比较 OrdinalIgnoreCase 口径核实，CommandRouter.cs:776）。
- **tx 闭环（Green 闸放行实证；上条"tx 面待裁定"状态自此闭合）**：`run` add-text（目标层=已映射 Annotation 层 DIM_SYMB）exit 0 ok:true——**Green 闸不再 run-rejected**（对照建筑 T3 件 Yellow run-rejected 证据）；tx-start/step/tx-commit 审计**修改哈希 before≠after**（5224C993…→9EE9B0AB…）；check-run 回归 7 规则 0 强制违规；**rollback 可用实证**：working 副本 SHA-256 复原至 run 前字节一致 + rollback 留痕验链绿（全链 42 条：intake 1/semantic-scan 2/semantic-map 34/tx-start 1/step 1/tx-commit 1/check-run 1/rollback 1）。诚实观察：rollback 的 before_hash（CC1F3958…，reload 后文档哈希）与 tx-commit after_hash（9EE9B0AB…，内存文档哈希）不一致——新增实体 save/reload 哈希口径既有差异，复原判定以 tx-start.before + 文件字节一致为准（留后续切片，不扩 scope）。
- **scan 复核（映射后 re-scan，--skill examples/skills/electrical）**：Mappings 34 保留 + HumanConfirmed 保留（map 后即时 **2382**——SemanticMap 顶层句柄口径；re-scan 后 **27713**——LayerInference 人工映射优先传播至嵌套遍历实体，两口径如实记录；建筑-轴标/COLU 等层实体嵌套于块内、顶层 Insert 在他层，map 即时 HC 升级仅及顶层句柄，映射仍除歧义）。**不明确层 71→38**（任务预期 71−34=37 **诚实更正不成立**：PUB_TEXT/AXIS_TEXT 为约定库已覆盖层、本不在 71 不明确清单内，34 层中实际不明确层 32；另 xref 依赖层 新块$0$TEL_TEXT 经归一名兜底被 TEL_TEXT 映射覆盖（"x$0$WALL"→WALL 既有口径，ConventionLibrary.cs:138-139）→ 71−32−1=38）。**块指纹种子保持**：blockAutoSeeded 88/建议 9/块歧义 40/symbolsSkipped 0（与映射前逐值一致）。
- **新增 skip-gated 测试（实现先行实证钉住，非驱动）**：`RealElecT3SpecimenTests.RealElecT3Specimen_TxClosure_MapRunCommitRollback`——intake Green→scan→map DIM_SYMB（HumanConfirmed>0）→run Green 放行（无 run-rejected + tx 级 before≠after + 验链绿）→rollback 哈希复原 + 副本字节一致。首跑即绿 1/1（本机实跑非跳过）；既有测试零改动。
- **标本只读**：源副本 SHA-256 全程一致（402f9d16…，`specimen-sha256-closure-{before,after}.txt`）；tx 改的是 working 副本不是源 .dwg（副本 .dwg 字节亦一致）。
- **门禁**：Cli.Tests 全量 **249**/0 失败/0 跳过 + 全 sln **1286 通过/0 失败/3 跳过**（基线 1285 + 1 新测试；Llm 33/Core 235/Data 368/View 381/Cli 249/Zwcad 10 真机实跑/Acad 10+3 跳过）。**诚实记录**：首次全 sln View headless 测试 `IssueRow_CheckboxPointerClick_TogglesEntryIsChecked` 失败一次（本切片零触 View；隔离重跑绿 + 第二次全 sln 全绿 → 判定并行负载 flaky，两次日志均留档 gate-sln-closure{,-2}.log）。Acad 真机跳过口径保持（勿触 AutoCAD COM）；ZWCAD 前后 tasklist 无残留；acad.exe PID 22628 前后同在（用户自开会话零触碰）。证据 `outputs/tarch-elec/`（closure-report.md §9/map-34-results.txt/closure-*.{json,log,txt}/audit-evidence-closure/）。

**AutoCAD 2014 真机验证续轮（2026-11-24 记录；部分验证 3 绿/1 环境败 + 新根因核定；追加式，旧文 0 删；矩阵不升级）**：

- **Open 根因更正（续代理报告 H2 细化）**：前续代理报告判「AC1027 版本标签被拒」——本轮实证更正为 **ACadSharp 3.8.35 DxfWriter 输出内容不兼容 AutoCAD 2014 读取器，与版本标签无关**：① `doc.Header.Version=AC1015` 降级（写后 $ACADVER 自检确为 AC1015）仍被拒 0x80200157「解密数据时出错」；② 手写最小 AC1015（HEADER+TABLES+ENTITIES，ASCII 路径）亦被拒；③ **手写最小 R12（仅 ENTITIES+LINE）真开**（doc.Name/ActiveDocument 证实，f4/f5）；④ AutoCAD 自带 SaveAs 的 AC1015/R12 DXF 同环境可开（前代理 fmtreopen 证据）。→ AcadLive 夹具改**手写最小 R12 DXF**（仅 AcadLiveTests 本地；共享 DxfFixture 默认行为零改动，ZWCAD/Data/Cli/View 依赖不受影响）。SaveAs 输出为 COM 原生 DWG（非 DXF），读回经 DwgReader 不受此限制。**生产含义**：CLI DXF 降级路径产物若经 ACadSharp 写入，AutoCAD 2014 打开受限（后续切片评估写入器替代/版本口径，本轮不扩 scope）。
- **适配器忙拒绝增强（含承接前代理未提交启动重试，一并入 commit）**：真机核定 AutoCAD 忙时（启动窗口/Open 后再生窗口/退出窗口）IDispatch 以**调用结果**形式返回 RPC_E_CALL_REJECTED(0x80010001)/RETRYLATER(0x8001010A)——RPC 层消息过滤器兜不住此形态。修复三层：① **Run 调度层统一有界重试**（60×500ms=30s 上限；沿 InnerException 链判定含 CadRuntimeException 包装与 MissingMemberException GetIDsOfNames 形态；拒绝=调用未执行，重试无副作用重复；非忙错误一次即止不吞诊断）；② **Dispose/Restart 忙拒绝上抛重试**（原内层全吞→Quit 未生效→进程残留→下一测试守卫拒绝，f12/f13 实证修复）；③ ComMessageFilter 双拒绝型（ISHANDLED 0/RETRYLATER 2）有界重试。新增 seam 单测 6 个（过滤器判定×3/SaveAs 忙重试/非忙不重试/Dispose Quit 忙重试），AcadRuntimeTests **17/17 绿**。
- **真机结果（f18 终录）**：AcadLive **3 绿/1 环境败**——GetInfo（Version=**19.1s (LMS Tech)**）/OpenSaveAs（R12→DWG 往返实体数 1 保持，DwgReader 读回）/UserProfileCuix_ZipIntact 绿；**DrawingOps 环境败**（EntityCount/ModelSpace dispatch 抛 TYPE_E_CANTLOADLIBRARY 0x80029C4A，f16/f18 两次复现）。
- **新环境根因（核定，非适配器 bug）**：HKLM TypeLib `{AB513495-C0DC-4E03-AD6E-E851E08428D1}`（AutoCAD/ObjectDBX Common 19.0）**全部 lcid 路径指向 `C:\Program Files\Autodesk\Autodesk Sync\RealDwg\axdb19*.tlb`——该目录仅 tbb.dll/tbbmalloc.dll，tlb 不存在**（f17 只读核查）；真实 tlb 在 `C:\Program Files\Common Files\Autodesk Shared\`。ModelSpace/Blocks 等 ObjectDBX dispatch 需加载该类型库→必败。前代理 HKCU 覆盖键正是修此缺陷（重定向 Common Files），用户环境恢复移除后缺陷复现。**修复需用户授权重建 HKCU 覆盖或修正 HKLM 注册（本轮未授权，零触碰）**。
- **Blocks.Add 签名序探针：未核定（被 TypeLib 缺陷阻断，EnsureBlock 未执行到）**——`VendorProfiles.Acad.BlocksAddNameFirst=true` 原值保持，无实测依据不翻转（ZWCAD 前车之鉴口径不变）。
- **SaveAs 运行时锚（部分）**：COM Open R12→SaveAs 产出 `ops-anchor.dwg` 15847 字节 + Close→Quit 进程退出（f20）；OpenSaveAs 测试 DwgReader 读回实体数 1 绿。**COM 重开 ModelSpace.Count 计数锚被 TypeLib 缺陷阻断**（f20 如实记录）；人工目视 deferred（无 5 实体 ops.dwg 产物——DrawingOps 未走完）。
- **CUIX 金丝雀：PASS**——SHA256 复算 `F355D54FADA770E303BA277CFA1F9CEDCD74914A0EFE928C9E95FDE06E633F63` 与备份逐字一致（368411 字节，f19）；多轮真机运行期间零损坏。
- **门禁（逐套件串行，每套件前后 tasklist）**：**1296 通过/1 失败（DrawingOps 环境败）/0 跳过**（Llm 33/Core 235/Data 368/View 381/Cli 249/Zwcad 10 真机实跑/Acad 20+1 环境败）。View 首跑 `IssueRow_CheckboxPointerClick_TogglesEntryIsChecked` flaky 一次（隔离重跑绿 + View 全套重跑 381 全绿，与上切片同口径判定并行负载 flaky，f21/f22/f23 留档）。运行期间 3 次自家自动化残留实例均 **COM Quit 清零**（f7/f10/f13，用户已授权；无一次强杀）；每套件前后 tasklist 零残留。
- **矩阵判定：不升级**（非全绿 + Blocks.Add 未核定）——AutoCAD 维持 **CompatibleUnverified**，vendor-support.json 零改动。**解锁路径**：用户授权 TypeLib 注册修复（HKCU 覆盖重建或 HKLM 路径修正）→ 重跑 DrawingOps + Blocks.Add 探针核定 → 4 绿后按协议升级 Verified（versionRange 实测 R19.1）。
- 证据 `outputs/autocad-verify-20261123/`（f1-f23 系列 + scripts/ + final-verify-report.md + MANIFEST 追加）。

**AutoCAD 2014 真机验证 🟢 全绿收尾（2026-11-24 记录；TypeLib 修复落地 + Blocks.Add 探针核定 + 矩阵升级 Verified；追加式，旧文 0 删）**：

- **TypeLib 修复落地（用户批准，HKCU-only，HKLM 零触碰）**：controller 批准布局=HKCU TypeLib `{AB513495-C0DC-4E03-AD6E-E851E08428D1}\1.0\{0,804}\{win32,win64}` → `C:\Program Files\Common Files\Autodesk Shared\axdb19chs.tlb`。**实测落地偏差核定（g1/g2）**：本轮开跑前注册态仅有 `1.0` 版本级默认值，**缺 lcid/platform 叶键**——HKCR 合并视图叶键仍解析 HKLM RealDwg 坏值，g1 重跑 DrawingOps 仍抛 0x80029C4A（GetInfo 亦败一次，29s≈30s 忙重试上限耗尽，推断冷启动慢；g3/g4 复绿，无代码改动）。按批准布局补全 4 叶键（scripts/g2-complete-typelib-hkcu.ps1，前后注册态 g2/g2b 留档）后 dispatch 全通。回滚指令不变：`Remove-Item -Recurse HKCU:\Software\Classes\TypeLib\{AB513495-C0DC-4E03-AD6E-E851E08428D1}`（c3-typelib-fix-record.txt）。**环境依赖明示**：移除覆盖即复现 0x80029C4A（HKLM 坏值仍在，未修）。
- **Blocks.Add 签名序探针：核定=(origin, Name)，与 Autodesk 文档序相反，与 ZWCAD 实测一致**——TypeLib 修复后 g3 DrawingOps 推进至 EnsureBlock，`blocks.Add(blockName, Pt(0,0))`（文档序 Name 先）抛 `Could not convert argument 0 for call to Add`（CallSite 实证 ComObject,String,Double[] 参数 0 转换失败→首参非字符串=点）。翻 `VendorProfiles.Acad.BlocksAddNameFirst=false` 重跑 **g4 全绿**。ZWCAD 前车之鉴口径兑现：不赌文档，两厂商签名序均真机核定。注释同步更新（VendorProfile/AcadRuntime/AcadLiveTests）。
- **AcadLive 4/4 真机绿（g4 终录）**：GetInfo（Version=**19.1s (LMS Tech)**，R19.1.18.0.0）/ DrawingOps（AddLine/AddText 中文「生成式标注」/AddCircle/EnsureBlock SYM 三基元+幂等/AddInsert→SaveAs DWG→DwgReader 读回 **5 实体**含中文文本与 SYM Insert）/ OpenSaveAs（手写 R12→DWG 往返实体数 1 保持）/ CUIX 金丝雀。**SaveAs 人工目视仍 deferred**（测试产物临时目录用后即清，本轮未产持久 ops.dwg；COM 读回闸绿）。
- **CUIX 金丝雀：PASS**——前置/后置 SHA256 均 `F355D54FADA770E303BA277CFA1F9CEDCD74914A0EFE928C9E95FDE06E633F63`，与备份逐字一致（g5）；全程零 CAD 残留进程（每轮前后 tasklist）。
- **门禁（全 sln 一次）**：**1297 通过/0 失败/0 跳过**（Llm 33/Core 235/Data 368/View 381/Cli 249/Zwcad 10 真机实跑/**Acad 21=17 单测+4 真机全实跑不再跳过**）。诚实口径：controller 预期「4 跳过」未出现——本机 AutoCAD/ZWCAD/真实标本条件全满足，skip-gated 测试全实跑，0 跳过优于预期。矩阵/断言改动后终态全 sln 复跑亦 1297/0/0（g7）。
- **矩阵升级：Verified**——`src/CadAgent.Cli/vendor-support.json` AutoCAD：status→**Verified**、verifiedAt→**2026-11-24**、versionRange→**"19.1"**（实测 GetInfo 前缀；VendorSupport.VersionMatches=StartsWith 语义，"19.1s (LMS Tech)" 命中）、testCoverage 含探针核定结论+R12 夹具+TypeLib 修复注记。**未实测版本（含原标 2024-2026）不再收录**→export gating 照旧警示+审计（Export_UnverifiedVendor_WarnsAndAudits 以 AutoCAD 2025 验证此路径仍绿）。VendorCommandTests 矩阵断言同步（CompatibleUnverified 条目已不存在，改钉 AutoCAD Verified+TArch Unsupported）。
- **本轮不做（明示）**：天正运行时读取（Task B deferred）；HKLM 修正（用户未授权，HKCU 覆盖绕开）；ACadSharp DXF 写入器替代（生产含义注记沿用上条，后续切片）。
- 证据 `outputs/autocad-verify-20261123/`（g1-g7 系列 + scripts/g2 + matrix-upgrade-report.md + MANIFEST 追加）。

**天正运行时读取实证（Task B，2026-11-24 记录；AutoCAD COM × 16#1116.dwg 原生副本只读打开；追加式，旧文 0 删；不扩 scope 实现，仅注记）**：

- **执行口径**：Task A 全绿收尾后（AcadLive 4/4、矩阵 Verified 19.1、TypeLib HKCU 覆盖在位），outputs/api-probe 新增 `tarch-read` 探针（裸 dynamic COM + InvokeMember，不入 sln/git）；`Documents.Open(副本, ReadOnly=true)` 1.6s 返回无模态框；ModelSpace 1958 实体全量读 ObjectName/Layer/Handle（0 失败）+ 块定义 1084 个定义级实体 24105 + 属性抽样 7 实体×6 属性（预算 ≤10×6 内）。进程纪律：前后 tasklist 0 acad，自家实例 Close(false)→Quit 正常退出未用 taskkill；CUIX 哈希与 Task A 备份逐字一致；源件/副本 SHA256 前后均 63503c2f…302c6（只读零写入）。
- **TCH 真实名不可见（天正未加载，诚实记录）**：运行时 ObjectName 直方图 7 类=AcDbBlockReference×993 / **AcDbZombieEntity×343** / AcDbLine×278 / AcDbPolyline×158 / AcDbText×158 / AcDbCircle×17 / AcDbHatch×11。COM 自动化按默认 profile **<<未命名配置>>** 启动（ActiveProfile 实测），支持路径无 Tangent、进程 C:\Tangent 模块数=0。加载机制只读核实：天正**不经**注册表 Applications LoadCtrls autoload（HKCU 仅 AcMr/HKLM 仅 Autodesk 自带）；实际通道=**TElec20V5 profile** 支持路径首位 `c:\tangent\telect20v5\sys19x64` + 该目录 **acad.rx**（22 个 tch_*/tel_* ARX），日常经 TGStart.exe 启动。故 TCH_* 运行时呈 **AcDbZombieEntity**（僵尸类，非 AcDbProxyEntity），原 17 类类名经 ObjectName 不可分辨；「天正加载后 TCH 真实名可见」分支本轮未实证（不擅自切 profile/arxload，加载配置零改动）。
- **census 对照精确闭合**：**343（ModelSpace 僵尸）+55（块定义僵尸）=398=文件级 census 398**；plain 六类计数（993/278/158/158/17/11）运行时与文件级逐字相等；ModelSpace.Count 1958=文件级 Entities 1615+343。**信息互补钉死**：文件级 census 有 17 类 TCH 类名但 Handle 全空；运行时僵尸有 **Handle/Layer 锚定**但类名归一、无几何——两通道合并才覆盖「类名分布+空间锚定」。块定义僵尸 55 分布：建筑-轴标 22/PUB_TEXT 23/PUB_HATCH 6/公用-图框-说明 1/预留给水点 1/A建筑-C门窗 1/TEL_TEXT 1。
- **COM 属性可达性边界**：僵尸 ObjectName/Handle/Layer **可读**（如 Handle=321A/C公用-符号）；Coordinates/TextString/Area 等几何与 TArch 自定义属性一律 **DISP_E_UNKNOWNNAME 0x80020006**（名称级拒绝，非忙拒绝非崩溃）——天正未加载时 IDispatch 仅暴露实体基类属性。plain 实体属性全可达（Line/Polyline/Text/Circle/Insert 几何 OK，Hatch Area OK）。
- **层内容跨核**：█HVAC█签名层 MS 顶层 **0** 实体、块定义级 11640（Line 11048 为主）——scan 递归口径 17466 的巨量内容实为块内 plain Line（签名图块），运行时可见可解析非天正盲区；0 层 MS 仅 7、块定义级 10229（Line 9559/Ellipse 231…）；天正僵尸主聚集 **0dt 层 259**（MS 672 中）+A建筑-门窗-编号 47 等。口径注记：scan 按 Insert 递归展开（实例级）vs 块定义定义级各计一次，数值不可直接相等，跨核价值在类型构成与盲区定位。
- **探针技术核定（可复用）**：AutoCAD 集合 **Item 是 dispatch 方法**（InvokeMethod 形态），GetProperty 形态抛 DISP_E_MEMBERNOTFOUND 0x80020003（本轮实证）；Open 后再生窗口忙拒绝（0x80010001）须**全调用面**有界重试（GetVariable/ModelSpace 亦需覆盖，非仅 Open/SaveAs）。
- **后续 native 理解路径建议（仅注记，不实现）**：①运行时 COM 读取作为 **census Yellow 件的补充理解通道**——增量价值=398 盲区对象的 Handle+Layer 锚定（只读理解层面缓解「未解析区不可定位」，不改变 Yellow 闸口语义）；②类名分辨仍依赖文件级 census；若需运行时真实 TCH 名/几何/自定义属性须天正加载会话（用户决策：TGStart/TElec20V5 profile 启动后 COM 附着，或授权切换 ActiveProfile；许可/HASP 与模态框风险须用户在场批准）；③生产接线 deferred（本轮仅实证）。
- 证据 `outputs/tarch-elec/runtime-read/`（report.md/census-runtime-comparison.md/autoload-mechanism.txt/objectname-histogram.txt/layer-{modelspace,blockdefs}.txt/property-reachability.txt/probe-run.log+attempt{1,2,3}/session.txt/specimen-sha256-{pre,post}.txt/tasklist-{pre,post}.txt/cuix-hash-post.txt）。

**天正 profile 实例运行时探针 + 产品方向转向（2026-11-25 记录；路径 B 用户批准；追加式，旧文 0 删）**：

- **探针执行（路径 B）**：自动化 `acad.exe /p TElec20V5` 起独立天正实例（用户 AutoCAD 已关，进程清零核实）；ActiveProfile=TElec20V5、**36 Tangent 模块加载、AcDbZombieEntity=0**；只读打开 16#1116.dwg 副本（ReadOnly=true），读毕 Close+Quit 自家实例；CUIX/源件/副本哈希前后一致，0 残留；「日积月累」提示框出现仅记录未点、ROT 自主注册无需用户点击、无许可/HASP 框。证据 `outputs/api-probe/tarch-profile-probe/`（report.md + 直方图/属性表/census 跨核）。
- **真实类名可见——TDb\* C++ 类名（非 census TCH_\* 标签）**：MS 1958/21 类（vs 僵尸通道 7 类）；**TDbWall×139 / TDbText×68 / TDbColumn×55 / TDbSpace×17 / TDbEquipment×17 / TDbSymbMultiLeader×15…** MS TDb 合计 343；plain 六类（993/278/158/158/17/11）两通道不变。
- **census 跨核精确闭合**：MS 343 + 块定义 55 = **398 = 文件级 census**；**17 TDb 类 ↔ 17 TCH 类逐类计数精确闭合**（TDbWall↔TCH_WALL 139 等）。映射为跨通道按计数+语义推断（非单 API 双名，诚实注记）。
- **核心结论：加载天正解决「类名分辨」，未解决「COM 几何可达」**——7 TDb\* 对象 ObjectName/Handle/Layer 全 OK，但 **Coordinates/TextString/Area 全 FAIL DISP_E_UNKNOWNNAME 0x80020006**（3 plain 对照全 OK）。**天正几何/自定义属性须 ObjectARX C++/.NET API，非 COM IDispatch**——COM 通道天花板实证钉死。
- **产品方向转向（用户决策 2026-11-25）**：产品逻辑需**天正图纸完全解析**（不止 Yellow 只读审查）；既然 COM 几何不可达，**启动 TArch .NET API 几何探索**（ObjectARX/.NET 通道读天正真实几何/参数），**在另一独立对话窗口立项推进**（本会话仅记录与交接）。census 闸口/ Yellow 语义保持至完全解析落地；运行时天正会话通道（类名分辨+Handle/Layer 锚定）作为过渡补充理解。
- **交接上下文（供独立会话）**：天正加载机制=TElec20V5 profile + c:\tangent\telect20v5\sys19x64\acad.rx（22 ARX）；TDb↔TCH 17 类映射见探针报告；COM 几何拒绝实证 0x80020006；AutoCAD 2014 R19.1 + TypeLib HKCU 覆盖（axdb19chs.tlb）在位；.NET API 探索须核 T20 电气 V5.0 是否暴露 .NET 程序集（Tangent.*.dll）/ObjectARX 头文件可用性，许可/HASP 风险须用户在场。

**切片 A 收尾闭环（COM 原生读参 + 逐实体精准松绑，feat/cad-agent-slice-a-com-params，2026-11-25 记录；T1-T10 全交付、T10 条件真机转绿，全套件门禁见本块末行；追加式，旧文 0 删；设计=docs/superpowers/specs/2026-11-24-slice-a-com-parameter-design.md）**：

- **COM 原生读参通道（T1-T7 交付，T10 真机固化）**：`ICadRuntime.ReadNativeParams(string? drawingPath)`——`path!=null` 形态自 `acad.exe /p "TElec20V5"` 起天正实例（ROT `GetActiveObject` 附着，非 COM 激活）`Documents.Open(原图, ReadOnly=true)`（不另建副本）→ **Gate1（Version 非空 + Documents.Count≥1）/Gate2（ModelSpace.Count>0）** 就绪等待→逐实体读参→读完即关；`null` 形态读当前活文档（native-live 会话内读回）。**忙窗协议生产化**：真机实证忙拒绝以 *调用结果* 形式返回 RPC_E_CALL_REJECTED(0x80010001)/RPC_E_SERVERCALL_RETRYLATER(0x8001010A)（启动窗口 ~7-16s、Open 后 ~1-3s 复现），生产以 `RetryBusy` **单基元粒度**有界重试（60×500ms；拒绝=未执行，复合方法不得整段重试）+ ComMessageFilter 双拒绝型兜底；Close/Quit 同走重试。**语义**：ObjectName 分类 `TDb*` → TDbClassMap 语义类 + 自定义参数读取；**UNKNOWNNAME(0x80020006)=属性不存在**（记缺省、不重试、单属性缺不降 ReadOk）；**空读≠不存在**（重试至非空，耗尽→`ReadOk=false` 占位，绝不静默当"无此实体"）。overlay 加性 `NativeEntities`（Provenance.NativeParameter 0.95 种子）。
- **逐实体精准松绑（T8/T9 交付）**：run 主闸在 census **Yellow** 时的精准旁路——须 `--live` + 计划**全部**步骤 kind ∈ {change-layer, move, delete} 且带 `handle`；每句柄先过 `NativeGateRefinement`（∈NativeEntities + ReadOk + **本轮 live 重读一致**（ObjectName/bbox/Params，stale 防护））→ 全 Pass 才准入；执行器=**native-live 独执行**（会话顺序：path 形态读参拉起 TArch → Open **原图** → 句柄改层/平移/删除 → null 形态读回 → 终态聚合校验（delete 支配 / change-layer 末步骤名 OrdinalIgnoreCase / move 位移和）→ **源件 SHA256 前后比对（SaveAs/Close/运行时 Dispose 后复核，证据窗口闭合；空 SHA / liveSha 不可验 fail-closed）零写入** → 唯一写出 `&lt;drawing&gt;.live.dwg`）。审计 native-gate-pass/refused、native-live-step/committed/failed 全链留痕；`MalformedHandle`/`InvalidLayer` 准入期拒绝；句柄歧义（全数字 hex 如 "21000"）按 overlay 命中形态归一。**kind 词汇归一（2026-11-26 最终复审修复）**：产品计划 kind `move-entity`/`delete-entity`（HandlerRegistry 词汇）与 Replay 通道 case `move`/`delete` 两套全收，准入克隆时改写为运行时词汇（ComReplayService case 零改动）——修复原本"产品计划永远进不了 native-live"的词汇裂缝。
- **T10 真机证据（`outputs/slice-a-t10/`，真实电气标本 16#1116.dwg 临时副本；源件零触碰）**：①`ReadNativeParams(副本)` 首读 **records=343（MS TDb 合计，非 TDb 按设计跳过）**，TDbWall **21E00** LeftWidth=**100** / Height=**3000** / Volume=**2.895** 逐字闭合（spike 基准）、Class=Wall、ReadOk=true、Layer=**0dt**；耗时 **32.9s**（含冷启动/附着/Gate1/Open/Gate2 + 1958 实体遍历，仅 343 条 TDb 记录）。②**创建后首读（忙窗 A 注入，无额外 sleep）**同样全量返回（**32.98s**、ReadOk 全 true、21E00 在列）——生产重试吸收实证。③句柄层改读回（副本）：ChangeLayerByHandle(21E00, **C公用-符号**（记录中既有层，非当前层）)→重读 Layer 相等；MoveByHandle(21E00, 10, 0)→重读 bbox **x 分量精确 +10**（127749.99584354367→127759.99584354367 等；y/z 不变，1e-6 容差）；④DeleteByHandle(**DEADBEEF**)（删 21E00 之前）→ `CadRuntimeException: 句柄不存在于模型空间: DEADBEEF`；⑤DeleteByHandle(**21E00**) 最后→重读缺该句柄（**343→342**）。金丝雀：源件 SHA256 **63503c2f…302c6** 前后一致；CUIX SHA256 **f355d54f…633f63**（本机仅 1 个 profile 文件；计数以 gate 日志 cuixCount=1 为准）前后一致；副本字节前后一致（无 SaveAs）；**tasklist 前后 0 acad**（自家实例 Dispose 内 Close(false)+Quit+WaitForProcessExit，零强杀）。三真机测试耗时 35s/45s/1m44s。
- **T10 真机核定催生的两处生产修复（诚实记录：原实现假定被真机推翻，同批修复已入提交）**：①**/p 启动路径解析**——原硬编码裸 `"acad.exe"` 假定 PATH 可解析，真机首跑 3/3 败于 `Win32Exception 系统找不到指定的文件`（Workstation 安装不写 PATH）；修复=解析链：注册表 `AcadLocation`（HKLM/HKCU `SOFTWARE\Autodesk\AutoCAD\R*\ACAD-*`，版本键降序）→ `%ProgramFiles%\Autodesk\AutoCAD *\acad.exe` 目录扫描 → 兜底裸名（旧行为保持）；`/p` 参数构造抽纯函数单测钉住。②**GetBoundingBox 出参 by-ref 回写**——原实现传 `double[3]` 占位未标 `ParameterModifier` 为 by-ref，COM **不回写** → 读回恒为**全零假 bbox**（handle-ops 真机首跑 `move bbox 分量0 before=0 after=0` 即此根因）；修复=spike phasea 同款 `object[] 占位 + ParameterModifier[0..1]=true`，非常规返回形态→null 诚实降级（不以零冒充）。
- **诚实边界（明示不假装）**：COM 读参**不补全文件级解析**——census 仍 Yellow（磁盘 TDb 仍是 UnknownEntity，17 TCH 类 + 398 盲区分布不变），运行时读参只是**理解侧补充通道**（类名/参数/Handle/Layer 锚定）；逐实体松绑**非整图 Green**（未读参/ReadOk=false/未命中实体仍禁触，生成类与全图级修改仍全图级拒绝）；松绑**仅句柄寻址三步**（change-layer/move/delete），文件级 TDb 编辑通道仍范围外；TDb **几何**（坐标/面积/文本）经 COM 不可达（DISP_E_UNKNOWNNAME，上一块探针已实证），本切片只用**自定义参数面**（LeftWidth/Height/Volume 等）与标准基类面。
- **披露（不假装已验）**：`nativeReadFailed` 真机近零——**口径更正（2026-11-26 最终复审）**：整读失败（Gate/遍历抛）→ `native-read-failed` 审计 + 异常（CLI 计数归 0 并降级），不以空读静默充数；逐实体 `ReadOk=false` 仅属性/ObjectName 返回 null 通道可达（生产近不可达，防御占位，本轮真机 0 失败）；**Gate2 空图边界**——ModelSpace.Count==0 时 Gate2 耗尽→"天正实例未就绪（Gate2 超时）"响亮拒绝（空图纸被当作未就绪，语义偏保守，非静默空读）；**bbox 尽力读**——GetBoundingBox 面缺失/形态异常→null 且 ReadOk 仍 true（与文件级渲染 bbox 口径可能不同，不得混用），Move 读回在 bbox 不可得时退化为 params 不变（审计 readback=reread-bbox-null）。
- **门禁**：`outputs/slice-a-t10/gate-sln-2.log`（**1445 通过 / 0 失败 / 0 跳过** = Llm 33 + Core 250 + Data 405 + **Acad 65**（含 3 T10 真机 + 4 AcadLive 真机 + 1 ExportSnapshot 真机，**0 跳过**——本机 AutoCAD/标本条件满足，skip-gated 全实跑；其余为单测）+ View 381 + **Zwcad 16 真机** + Cli 295；第二次全 sln 连绿）。首跑 `gate-sln.log` 1444 通过 / **1 失败**=View `PlanPreviewViewModelTests.ConfirmPlan_Executes_TxCommitted` teardown `audit.db` 文件锁（并行负载 flaky——隔离复跑 381/381 绿，与本 §12 既有 View flaky 口径一致，非本切片改动，两次日志均留档）。门禁前后 tasklist 均 **0 acad**；源件 SHA256 **63503c2f…302c6** 与 CUIX **f355d54f…633f63** 前后一致（`outputs/slice-a-t10/tasklist-{pre,post-gate}.txt`、`specimen-sha256-{pre,post}.txt`、`cuix-sha-{pre,post}.txt`）。

**切片 A 最终整分支复审修复波（2026-11-26 记录；feat/cad-agent-slice-a-com-params；追加式，旧文 0 删）**：

- **修复范围（复审 12 findings 全闭合，代码+测试+文档同波）**：①**kind 词汇归一**——产品计划 kind `move-entity`/`delete-entity`（HandlerRegistry/EditHandlers 词汇）与准入白名单/Replay case 的 `move`/`delete` 曾互斥（LLM 计划永远进不了 native-live）；修复=准入全收五 kind + Replay 克隆时 kind 归一改写（ComReplayService case 零改动），产品词汇 CLI 测试钉住。②**零写入证据窗口闭合**——源件 SHA 复核从"Open 期间"移到 SaveAs/Close/**运行时 Dispose（内部 Close+Quit+等待进程退出）之后**；交付件 `liveSha` 不可验证→`native-live-failed{liveSha-unverifiable}`+exit1（保留 .live.dwg 作证据但诚实报失败）；新增恶意/缺陷 SaveAs 回写源件回归（post-write 复核捕获→failed exit1）。③**读参原图只读**——`ReadNativeParams(path)` 改 `Documents.Open(原图, ReadOnly=true)`（不另建副本，兑现设计承诺；执行侧 `rt.Open` 保持读改写）+ 已有打开文档守卫（"已有打开的图纸"）。④**ROT 附着归属校验**——附着实例 HWND vs 本方启动进程主窗句柄，不符→只清本方进程、**绝不 Quit 他方实例**、_app 置空并响亮拒绝（归属判定缝单测注入 true/false）。⑤**拒绝面 CLI 测试**——overlay 缺失/损坏、ReadIncomplete、空 SHA 四分支。⑥**验证边界（诚实记录）**：逐实体松绑真机 E2E=运行时层真机绿（`AcadNativeLiveTests` 3 真机）+ CLI 准入/交付层=fake E2E；**整链单次真机 E2E（overlay→live→读回一次跑通）未跑**，不假装已验。⑦**文档口径更正**——`ReadOk=false` 可达性（整读失败→native-read-failed 审计+异常；逐实体 ReadOk=false 仅属性/ObjectName 返回 null 通道可达，生产近不可达）、nativeSkipped 未实现→三键、acad 解析链（每根注册表隔离/x86 目录候选/信任边界注记）、`--native` 提示可配通道（CADAGENT_RUNTIME=acad）、native-live 成功 stdout 收据 `{"ok":true,outPath,handles}`。
- **门禁**：`outputs/fixwave-final/gate-sln.log` — **1456 通过 / 0 失败 / 0 跳过**（= Llm 33 + Core 250 + Data 405 + View 381 + Zwcad 16 真机 + Cli 303（295+8 新）+ **Acad 68**（65+3 新，含 3 T10 真机 + 4 AcadLive 真机 + 1 ExportSnapshot 真机全实跑，0 跳过）；本波新增 11 测试——Cli +8 / Acad +3；Acad 真机链含新归属校验/ReadOnly 读参路径，全绿）。
- **代价与边界**：Dispose 后哈希使 native-live 成功路径多一次进程退出等待（正确性优先）；归属校验在 HWND 不可判定时 fail-open 到既有行为（守卫 EnsureNoForeignInstance 仍为第一防线，本校验为纵深防御）。

**切片 B 收尾闭环（天正 .NET 深度读 + explode-native 放行，feat/cad-agent-slice-b-tarch-deep，2026-11-25 记录；T1-T11 全交付、T10 条件真机转绿；追加式，旧文 0 删；设计=docs/superpowers/specs/2026-11-25-slice-b-tarch-deep-read-design.md，实现口径更正见其 §7.1）**：

- **桥通道闭环（真机固化；R17/R18 根因修复入内）**：
  - **NETLOAD 经 TRUSTEDPATHS 免弹窗**：用户批准的**一次性配置**（实际写入值见 `outputs/trustedpaths-after.txt`：`C:\Tangent\TElecT20V5\SYS;C:\Tangent\TElecT20V5\SYS19x64;C:\Tangent\TElecT20V5\lisp;"E:\随机任务 ·Qoder·\src\CadAgent.TArchBridge\bin\Debug\net45"`）；**SECURELOAD 保持默认 1**——不降级、不自动写配置（变更由用户执行）。
  - **部署口径（消除歧义，ISTQB 合并前审查修复波 I3）**：NETLOAD 的信任判定针对**部署中桥 DLL 所在目录**，不是仓库源码目录。测试经 `AcadRuntime.BridgeDllOverride` 指向仓库构建产物 `src/CadAgent.TArchBridge/bin/<Debug|Release>/net45`——**当前 TRUSTEDPATHS 值信任的正是这个「测试」桥目录**，不代表生产已就绪；**生产必须把 `CadAgent.TArchBridge.dll` 与 `CadAgent.TArchBridge.Protocol.dll` 部署到 CLI 可执行目录（`AppContext.BaseDirectory`）并信任该目录，或设环境变量 `CADAGENT_BRIDGE_DLL` 指向桥 DLL 并信任其目录**（桥路径解析顺序：`BridgeDllOverride` → `CADAGENT_BRIDGE_DLL` → 部署目录）。桥可用性由 `AcadRuntime.CheckBridgeAvailability` 在 Open/NETLOAD 之前预检（缺失/空桩/不受信 → `native-bridge-unavailable` 拒绝，绝不进 3600s 安全框）。**预检覆盖两条深读路径**（PR#30 终审 I1）：native-live **与** `semantic scan --explode` 同样生效——后者不可用时整块跳过深读 + 审计 `explode-read-failed{reason,dllPath,guidance}` + stderr 指引 + **exit 0 降级**（理解侧诚实降级；预检在**运行时构造之前**，连实例都不建），绝不进 3600s 安全框。
  - **TRUSTEDPATHS 读取＝注册表优先**：`HKCU\Software\Autodesk\AutoCAD\R19.1\ACAD-*\Profiles\TElec20V5\Variables` 的 REG_SZ（只读、**零实例、零 COM**）；注册表无该值才回退 COM **文档面** `GetVariable("TRUSTEDPATHS")`（活动文档取用同走忙重试）。根因（真机）：COM 实读必须先 `/p` 拉起整实例，而启动初始化期 `Quit` 被忽略 → 孤儿 acad.exe，随后守卫只会自跳过"残留进程"，真因被残留掩盖。
  - **桥 SendCommand 全部忙重试**：NETLOAD／注册探针／正式命令三处发送经同一注入缝走 `RetryBusy`（`RPC_E_CALL_REJECTED` 0x80010001，TArch 启动窗真机 run7 实证；被拒＝命令未执行故重试无副作用；与既有 30s 预算共享）。
  - **通道目录＝实例级**：`EnsureChannelDir` 在本实例**首次启动前**创建目录并注入 `CADAGENT_TARCH_CHANNEL`（子进程只在 `CreateProcess` 时**快照**环境变量），READ 与 EXPLODE_REPLACE 全程复用；目录删除与环境变量还原只在 `Dispose`；写请求前先放弃目录内"有 req 无 done"的历史请求（否则桥 `NextPending` 恒拾最早旧请求→第二次桥操作必超时）。
  - **误拒检测＝探针握手**：NETLOAD 返回后写 `op=read`/`dwg="__probe__"`（与当前文档名必然不符）请求并发送 READ 命令；桥必然回 `status fail`+done → 已注册；10s 内无 done → `native-load-denied` 明文三因（误点【不加载】/TRUSTEDPATHS 未含/SECURELOAD 阻止）。不依赖 SendCommand 回显（ActiveX 口径返回 void）。
  - **看门狗与硬超时**：NETLOAD 总上限 **3600s**（人工授权动作，人在场未必及时）+ **20s 无进展无条件打印一次**明文指引（检出安全框时附诊断，仍继续等待）；命令等待 **120s**。**硬超时只终止本方** `/p` 启动的进程（既有 `TarchProcessCleanup` 缝，不触 COM、绝不碰他方实例），阻塞中的 COM 异常随后包装为"NETLOAD 超时（3600s）：已终止本方 AutoCAD 会话"。

- **真机证据与断言强度分级**（`outputs/trx6/bridge6.trx` + `outputs/bridge-live-run8.log`；3 通过/1 设计性跳过，1m36s，收尾 0 acad）：
  - **T1 Passed（18.0s）**：`evidence=343`（探针基线 343）、`tdb=343`、`fully=284`、`notFully=59`、`elapsed=14.9s`。**断言强度**：计数仅断言 **≥300**（343 为**观测值**，精确相等只记日志——标本/天正版本可变属设计内噪声）。`21E00` TDbWall `wrapper=ImpCurve prims=4 bbox=(127749.996,120300,0,127849.996,130100,3000)`——断言 RxClass 逐字 + bbox 逐分量 **1e-2** 容差（桥以 `0.###` 三位小数输出）。`HYDRANT_RADIUS=28` 命中多个 TDbEquipment（3EB5D/3EB5F/…/3EB7D）——断言**任一行等于 28**（不锁句柄、不锁行数）。
  - **A4 Passed（23.3s）**：TDbSymbArrow `rows=4`、`notFully=2`，`FAIL handle=321A/328F`（`prims=0 nested=0 bbox=null`）——断言 **≥1 行 FullyExplodable=false**（行数/句柄只记日志）；标本无样本→Skip（不伪造残留）。
  - **T3 Passed（55.2s）**：`REPLACED handle=21E00 newCount=4 bboxMatch=True tolerance=0.001`；`entityCount 1958→1961`（断言 after == before−1+newCount）；读回 `records=342 containsWall=False`（断言原句柄消失）；`.live.dwg` **3,182,297 B** 落盘（断言**存在且 >0**，字节数为观测值）；源件/副本 SHA 与 **acad.CUIX f355d54f…633f63** 前后逐字一致；收尾 **0 acad 残留**（硬断言）。
  - **T2 NotExecuted by design**（Skip"标本无嵌套 TDb 样本"）——措辞更正见下条。

- **T2 递归措辞更正（设计 §6 不可表达 → 范围收窄，已同步设计 §7.1(c)）**：原"T2 递归：TDbSpace NestedTdb>0 且 depth>0 产物记录"在 Core 模型内**不可表达**——`ExplodeEvidence.NestedTdbCount` 只统计**递归失败残留**（桥内"递归超深/非实体产物"的失败叶；成功路径恒 0），`ExplodePrimitive` 无 depth 字段。故 T2 收窄为**样本条件披露**（nested 行存在→断言 >0 且 `FullyExplodable=false` 自洽；无样本→Skip 如实）。**嵌套全量炸开由桥侧递归证据承担**（`PrimDto` 带 depth 入 TSV；T1 `fully=284/343` 即递归到底成功计数）——T2 不单独作递归充分性证据。

- **explode-native 语义（落地口径，含两处设计更正）**：
  - **递归叶替换**：一次递归炸开到底取叶集（>0 且全非 TDb）→ 事务内删原实体 + **逐叶 append**（绝不把 TDb 中间层写进空间）；**替换叶数须等于校验叶数**（同一次校验递归的确定性交叉核对）；任一校验不过→事务未 Commit（自动回滚），不留半替换。
  - **bbox 判据更正**（设计 §4.2"并集≈原 bbox 相等 1e-3" → **包含性 + 叶数一致**）：叶集并集 ⊆ 原 extents 外扩 1e-3，且**仅比 X/Y**——墙体原实体带高度（Z≈3000）而炸开面线在 Z=0，且面线 Y 止 130000 而 extents Y=130100（21E00 实证），相等判据对该类必败。
  - **准入更正**（R16）：change-layer/move/delete 与 explode-native 同用合并判据（参数门 ∨ 炸开门）；**仅凭炸开门**放行时有一例例外——**change-layer 拒绝**（层名读回无 before/after 参数面记录，放行后必然不可对账），move 的期望 bbox 回退炸开证据、delete 只验句柄消失，故放行并逐句柄落 `explodeGateHandles` 披露；合并判据永不返回参数门拒因（炸开门拒因优先），审计并行落 `paramsVerdict`（R14）。
  - **零写入与语义降级**：源件 SHA256 复核窗口移至 SaveAs/Close/**运行时 Dispose 之后**（空 SHA/`liveSha` 不可验→fail-closed）；唯一写出 `<drawing>.live.dwg`；explode-native 新基元**无原生语义**（降级为普通 CAD 图元，审计/overlay 如实记录）。

- **环境事实（机器特定，不冒充通用能力）**：**"不支持硬件加速"信息框＝本机核显指令集限制**的启动层**信息性**提示（**用户已永久关闭**该弹出）；`AcadWindows.DismissDialogs`（本方 PID + 标题子串「不支持硬件加速」+ **仅 `PostMessage(WM_CLOSE)`**，绝不按键/点击/结束进程）保留为**机器特定纵深防御**。复审在案两项残留风险：① 标题 needle 子串若用户**主框架**标题恰含该短语可能被命中（缓解：仅本方 PID、仅 WM_CLOSE，窗口类校验延后）；② EXPLODE_REPLACE 的忙重试理论上存在"拒绝在命令实际执行后才到达"的重复执行窗口（**未观测**，留观察）。

- **诚实边界（不假装已验）**：TDbSymbArrow 双通道 `eInvalidInput` → **永久 FAIL**（321A/328F 实证）；符号块**仅候选披露**（不落 ≥0.8 标注）；XData 同键**只取首个值**（1001=天正键名、后续值行归入该键的天正排布属**图纸依赖**口径，异排布图纸会缺值）；`CenterlineJson` 为**首线方向归一**的通用轴语义（非墙轴专用；首产物为封口线→证据不足 null）；**TRUSTEDPATHS 为本机一次性配置前提**（未配置/不含桥目录→Skip 并给指引，绝不伪造绿）；SECURELOAD 保持默认 1；census Yellow 不变（炸开证据是**附加通道**，不改文件级解析结论）。

- **门禁**：`outputs/slice-b-t11/gate-sln-attempt4.log` — **1589 通过 / 1 失败 / 1 跳过**（= TArchBridge.Tests 13 + Llm 33 + Core 260 + Data 443 + View 380 + Zwcad 16 真机实跑 + Cli 327 + **Acad 117**（含桥 4 真机：T1/A4/T3 实跑 + T2 设计性 Skip））。唯一失败＝View `SemanticViewModelTests.Map_WithoutOverlay_Fails_NoAuditNoFile` teardown `audit.db` 文件锁（**并行负载 flaky**，与 §12 既有 View flaky 同口径，非本切片改动）→ 该工程隔离复跑 **381/381 绿**（`outputs/slice-b-t11/view-isolated.log`）⇒ 合并口径 **1590 通过 / 0 失败 / 1 跳过**；门禁前后 `tasklist` **0 acad**（`outputs/slice-b-t11/tasklist-{pre,post-gate4}.txt`）。真机测试将 `NetLoadTimeout` 覆写 **10min**（生产 3600s 不变，`AcadBridgeLiveTests` 局部覆写以免本机/CI 挂死一小时）。
- **门禁过程诚实记录（三次前序失败均为环境噪声，零代码改动）**：① attempt1 11 例失败——本会话中途被中止的一次构建遗留**自家** acad.exe 孤儿，真机用例全部被 `EnsureNoForeignInstance` 拒绝（"检测到已有 AutoCAD 进程"）；清理该孤儿（`CloseMainWindow`→无窗口→`Stop-Process` **自家 PID**，记录 `FORCE_KILLED_OWN`）后重跑，日志留档 `gate-sln-attempt1-orphan.log`。② attempt2 4 例失败、Acad 隔离复跑 1 例失败——桥类与前一真机类交接时 `GetActiveObject` 返回刚退出实例的残留注册 → "ROT 附着实例非本方启动（归属校验失败）"（`AcadAttach.AttachOnly` 的既有归属校验，非切片 B 代码路径）。③ attempt3 1 例失败——同类类间交接 ROT 残留（A4 读炸开证据），重跑归零。**三者非产品缺陷**（生产单实例无类间交接面），但暴露**真机用例类间交接**在并行项目负载下的稳定性风险；最终复审波据此为归属校验加**有界重试**（10s 窗口，见下条），attempt2/attempt3/acad-isolated 日志同目录留档。
- **最终整分支复审修复波（2026-11-26 记录；代码+测试+文档同波；追加式，旧文 0 删）**：
  - **F1 失败文案阶段感知**：`ExplodeReplaceByHandle` 成功=桥内 `db.SaveAs` 已交付 `.live.dwg`——此后读回/收尾/异常失败文案改为「已产出 .live.dwg 但未通过读回校验，按失败退出；原图零写入」，`native-live-failed` 审计加性带 `liveOutExists`；无 REPLACED（准入/op/malformed）保持「未交付 .live.dwg，原图零写入」旧文案。CLI fake 同步镜像桥写盘（readback-fail 用例改断言文件存在、op-fail 用例断言文件不存在）。
  - **F2 explode-last 由桥交付、混合计划宿主收尾复写**：末步 kind==`explode-native` → 宿主跳过 SaveAs（桥已在替换事务内交付终态，消除双写）；否则保留单次宿主 SaveAs（覆盖炸开后仍有句柄步改动保存后状态的混合计划）——**同路径 SaveAs=AutoCAD SAVE 语义，真机待验**。
  - **F3 ROT 归属校验有界重试 + 判定精确化**：归属判定改「窗口→PID」（`AcadWindows.WindowProcessId`）——本方实例的闪屏/主框架窗口差异不再伪失败；仍不符则按 10s 有界窗口（`OwnershipRetryWindow`，1s 节拍）重试 AttachOnly+归属，耗尽才沿用原响亮拒绝；全程不 Quit 他方实例、本方启动进程句柄保留。
  - **F4 20s 指引诊断拼接**：NETLOAD 监视线程按节拍探测安全框（首发立即 + 500ms），命中至少一次 → 指引文案附加「（已检测到安全框）」（只一次；未检出/快速返回文案逐字不变）。
  - **F5 文档口径更正**：门禁工程标签 `TArchBridge.Protocol 13` → `TArchBridge.Tests 13`；前序失败三处（attempt1 11 例/attempt2 4 例/attempt3 1 例）；设计 §7.1(a) 只引 §4.1、§7.1(e) 改「环境新增事实」；真机测试 `NetLoadTimeout` 覆写 **10min**（生产 3600s 不变）。
  - **门禁**：`outputs/fixwave-gate-sln-2.log` — **1596 通过 / 0 失败 / 1 跳过**（= TArchBridge.Tests 13 + Llm 33 + Core 260 + Data 443 + View 381 + Zwcad 16 真机实跑 + Cli 328 + **Acad 123**（122 通过 / 1 跳过——桥 4 真机：T1/A4/T3 实跑绿 + T2 设计性 Skip））；本波 +7 测试（Cli +1 / Acad +6）；全量 1596。门禁前后 `tasklist` **0 acad**。诚实记录：本波**首跑**（`outputs/fixwave-gate-sln.log`）Acad 1 例失败＝`BusyWindow_…` 命中既有类间交接 ROT 残留（10s 重试窗内未恢复），据此加「窗口→PID」精确归属后**第二次全 sln 全绿**——残留竞态收窄但未根除，记观察项。

**切片 B ISTQB 合并前审查修复波（2026-11-26 记录；代码+测试+文档同波；追加式，旧文 0 删）**：

- **I1 按需深读（最重要）**：native-live 曾**无条件**先 `ReadExplodeEvidence`——参数门全放行的纯句柄计划也被拖进桥通道：桥未构建即得到"桥未构建"伪失败，桥目录未受信则 NETLOAD 安全框可阻塞至 **3600s**。修复=会话开路序改为 `ReadNativeParams(path)` **最先**（拉起 TArch profile、页内只读读完即关）→ 逐 step 参数门结论 → **仅当**（计划含 `explode-native` ∨ 任一参数门未放行）才深读并使用 `EvaluateCombined`；纯参数门放行计划**全程不触桥**（切片A 契约逐字保持，零 NETLOAD）。深读需求确定后、Open/NETLOAD 之前做 `AcadRuntime.CheckBridgeAvailability` 预检（纯只读：路径解析 + 空桩扫描 + TRUSTEDPATHS 判定），不可用 → 加性审计 `native-bridge-unavailable{reason: dll-missing|stub|untrusted, dllPath, guidance}` + stderr 指引 + exit 1，**零运行时副作用**（不建实例/不 Open/不 NETLOAD）。**口径分层（PR#30 终审 M2）**：「零运行时副作用」只对**静态可判**的 explode-native 计划成立（预检在运行时构造之前）；深读需求若由**参数门未放行**触发，预检发生在 `ReadNativeParams(path)`（拉起 TArch、实例已存在）之后、Open/NETLOAD 之前——拒绝时**实例已存在**，故不得声称"零实例"（只是不 Open、不 NETLOAD）。`semantic scan --explode` 的同一预检同样在**运行时构造之前**（拒绝＝整块跳过、不建实例）。`native-gate-pass` 加性披露 `deepRead`（深读会话另带 `trustedPaths` 快照）。
- **I2 空桩识别**：桥判定从"文件存在"升级为"存在 + 非空桩"——全文件 ASCII/UTF-16 双编码扫描：真桥含 `CADAGENT_TARCH_READ` / `CADAGENT_TARCH_EXPLODE_REPLACE` **任一**即非空桩；两者皆无且含 `NoAcadRefsStub` 才判空桩（文案逐字"桥未构建（空桩产物，需 CADAGENT_ACMG_DIR 与 AcMgd/AcDbMgd/AcCoreMgd 引用）"）。**真机实证**：本机真桥产物元数据里同样出现 `NoAcadRefsStub` 字串——只按 stub 标记判空桩会**误杀真桥**，判据必须是"无 TArch 标记 ∧ 有 stub 标记"（双编码亦因 .NET 用户串堆为 UTF-16）。
- **I3 生产可配路径 + 部署口径**：桥 DLL 解析顺序=`BridgeDllOverride` → 环境变量 `CADAGENT_BRIDGE_DLL` → `AppContext.BaseDirectory/CadAgent.TArchBridge.dll`；§12 桥通道块已加"部署口径"子条（当前 TRUSTEDPATHS 值覆盖的是**仓库测试桥目录**，生产须部署桥+协议 DLL 到 CLI 目录并信任该目录，或设 `CADAGENT_BRIDGE_DLL` 并信任其目录）。
- **I4 装载失败归因**：Bridge 异常消息含"NETLOAD 被拒绝"/"NETLOAD 超时" → 加性审计 `native-load-denied` / `native-load-timeout`（各带 reason/error；generic `native-live-failed` 照旧保留，stderr 原文 + exit 1 不变）——误拒（改信任配置）与硬超时（查实例健康）处置面分开。
- **I5 会话序测试失真**：旧断言"首个运行时调用=read-native:path"与生产（无条件深读=read-explode 最先）不符，且被 `ICadRuntime` 的默认实现掩盖（fake 未覆写 `ReadExplodeEvidence`，深读静默返回空表）。修复=fake 如实记录该调用并可配置"一调即抛"，测试钉两场景：① 全参数门放行 → 无 read-explode、`deepRead=false`（生产若再无条件深读必炸）；② 参数门漂移 → 顺序 read-native:path → read-explode → open。
- **I6 覆盖补齐（+35 测试）**：`BridgeRequest.Parse` 6 类拒绝（缺 dwg/未知键/坏 maxdepth/单列行/replace 缺 handle/replace 缺 liveout）、`BridgeResult.Parse` 5 类拒绝（缺 status/列数/0-1 标记/整数/浮点）、炸开读回两分支（`NewCount≤0`、`BboxMatch=false`）、编排（命令超时——新增 `BridgeChannel.CommandWait` 测试缝可压到毫秒级；桥缺 REPLACED）、`ExplodeReplaceByHandle` 三守卫（无打开文档/空句柄/空 liveout）。
- **M1**：`Dispose` 的通道目录清理移到 `WaitForProcessExit` **之后**（Quit 返回≠进程退出，桥进程可能仍持目录句柄；先删会与退出尾段竞态）。
- **M5**：`explode-read-failed` 审计键 `Error` → `error`（与 native-* 条目同口径，消费侧按键名可取）。
- **M6**：`SemanticOverlay.UpsertExplode` 句柄比较改 `OrdinalIgnoreCase`（同一实体的 `21E00`/`21e00` 曾并存两行，消费侧可能读到陈旧行）。
- **M8 口径更正**：上一波"+6 测试（Cli +1 / Acad +5）"→"+7（Cli +1 / Acad +6）；全量 1596"。
- **M9 拒绝载荷语义**：`native-gate-refused.verdict` 自本切片起=**合并判据结论**，参数门结论在 `paramsVerdict`；`change-layer-requires-params` 载荷加性带 `refused:true`（verdict 仍如实保留 Pass，拒绝原因在 reason）——消费侧不再靠 reason 猜"verdict=Pass 为何被拒"。
- **M10 桥命令 op 对账**：新增 `BridgeRequest.RequireOp`——READ 只接受 `op=read`、EXPLODE_REPLACE 只接受 `op=explode-replace`（通道目录复用/旧请求残留时防串道执行错 op），拒绝即 status fail 且不碰库；协议单测钉住。
- **门禁**：`outputs/istqb-fixwave/gate-sln.log` — **1631 通过 / 0 失败 / 1 跳过**（= TArchBridge.Tests 27 + Llm 33 + Core 261 + Data 443 + View 381 + Zwcad 16 真机实跑 + Cli 336 + **Acad 135**（134 通过 / 1 跳过——桥 4 真机：T1/A4/T3 实跑绿 + T2 设计性 Skip））；本波 +35 测试（Cli +8 / Acad +12 / TArchBridge.Tests +14 / Core +1）；门禁前后 `tasklist` **0 acad**（中途一次被中止的重跑遗留的**自家** acad.exe 已按 PID 清理后重跑全绿）。

**切片 B PR#30 合并前 ISTQB 终审修复波（2026-11-26 记录；代码+测试+文档同波、单提交；追加式，旧文 0 删）**：

- **I1 `semantic scan --explode` 桥可用性预检（本轮 Important）**：修复前 `--explode` 先构造运行时再深读——桥缺失/空桩得到伪失败，桥目录不受信则 NETLOAD 安全框可阻塞至 **3600s**，最终以泛化 `explode-read-failed` 降级。现 `--explode` 块在**任何运行时构造之前**调用 `AcadRuntime.CheckBridgeAvailability`（纯只读：DLL 路径解析 + 空桩字节扫描 + TRUSTEDPATHS 判定；**零实例、零 COM**）：不可用 → 审计 `explode-read-failed{reason: dll-missing|stub|untrusted, dllPath, guidance}` + stderr 指引 + 四计数 0 + **exit 0**（理解侧诚实降级，整块跳过）；可用 → 照旧。测试：三态 Theory（`BridgeDllOverride`/`TrustedPathsSource` 缝）→ `RuntimeFactory` 未被调用、审计 reason/dllPath/guidance 齐全、exit 0、计数 0、无证据行。设计 §1/§4.1/§5 的「信息性、不阻断」措辞同步更正为**硬门**（设计 §7.1(f)）。
- **M1 文档口径**：设计 §4.1 流程步骤、§5「信任与弹窗面」首条、§1 组件表 → 硬门；§7.1(f) 追加更正（native-live 不受信→exit 1；`--explode` 不受信→跳过+留痕+exit 0）。
- **M2 「零运行时副作用」口径分层**：**仅** explode-native 计划的静态预检可称零副作用（在运行时构造之前）；深读需求若由**参数门未放行**触发，预检在 `ReadNativeParams(path)`（实例已存在）之后、Open/NETLOAD 之前——拒绝时不得声称"零实例"（只是不 Open/不 NETLOAD）；`--explode` 预检同样在运行时构造之前。
- **M3 `GuardLiveOut` 可测化**：纯路径逻辑上移协议层 `LiveOutGuard.Check(liveOut, currentDocPath, requestDwgPath) → string?`（netstandard2.0/net45 双目标；net45 桥工程不可被 net8 测试引用，固守桥内即不可测）；桥侧 `Commands.GuardLiveOut` 只做"错误→`InvalidOperationException`"形态转换，**中文文案逐字不变**。测试（TArchBridge.Tests）：等于当前文档（大小写变体）、等于请求 dwg、父目录不存在、空串 4 类 + 正例。
- **M4 `liveOutExists` 判定收紧**：旧判据 `replacedExecuted && File.Exists`——桥内 `db.SaveAs` 成功但 `WriteResultAtomic`/结果丢失时 REPLACED 回不到宿主，会被误报"未交付"。改为 **文件存在 ∧ mtime ≥ 会话起点**（`sessionStartUtc` 在运行时创建/桥 NETLOAD 之前捕获，上一轮残留文件不计）；阶段文案同步 `replacedExecuted || LiveOutExists()`。测试：① 写盘后抛（结果丢失）→ `liveOutExists=true` + "已产出"文案；② 残留旧文件 + 本轮无写盘 → `false` + "未交付"文案；既有两个 F1 用例（fake 在替换时写盘）断言不变。
- **M5 盘根信任显式化**：`IsTrustedDirectory` 对 `C:\`/`C:/` 的"整盘信任"改为**显式判据**（目标同盘即命中），不再依赖 `"C:" + StartsWith("C:\\")` 的隐式归一；同时残缺形态 `C:`（无分隔符）**不再**被 `GetFullPath` 解析成"该盘当前目录"而悄悄放行——按非法条目处理（fail-closed）。测试：根命中/他盘不命中/目标亦为盘根/`C:` 不命中/普通目录精确+子目录+同前缀不命中。
- **M6 多产品注册表择优**：`ReadTrustedPathsFromRegistry` 不再"首个非 null 即返回"——抽纯函数 `SelectTrustedPathsCandidate(subkeys, probe)`：优先"含目标 profile（TElec20V5）且有值"的子键，无 profile 命中回退首个非 null（旧行为兜底）；候选按 `Ordinal` 定序（枚举序不承诺稳定）。测试：第二键 profile 命中被选 / 无命中回退 / 全无 null / 空串为有效值。
- **M7 TarchDerive 陈旧清理口径**：句柄集比较 `Ordinal` → `OrdinalIgnoreCase`（对齐 `UpsertExplode`）：同一实体大小写变体的存活行原地刷新、未读行照旧回收。测试：`21e00`（旧）+ `21e01`（旧）→ 本轮只读 `21E00` ⇒ 单行且句柄为本轮形态。
- **M8 `--explode` 运行时为空**：`RuntimeFactory` 返回 null 时旧实现静默跳过（计数全 0 无任何提示）；补 stderr「未配置运行时（RuntimeFactory=null），--explode 跳过」（异常/不支持厂商路径已各自提示，不重复）。测试钉住该行 + 计数 0 + 不记失败审计。
- **M9 失败不覆盖磁盘证据（注释）**：`--explode` 失败路径不 Save overlay——磁盘保留上一轮证据行（不覆盖、不清理）；消费侧须以本轮 `semantic-scan.explodeRead=0` + `explode-read-failed` 为准，不得把磁盘旧行当本轮新证据。
- **M10 零产物留痕**：桥 `TryExplodeDeep` 炸开零产物 → 追加 `FAIL 炸开产物为空`（此前该行只有 `FullyExplodable=false` 而 FAIL 列表为空，复核侧看不出原因）；文案不落 `递归超深`/`非实体产物` 前缀，故 `NestedTdbCount`（残留上界）口径不变。
- **M11 部分失败释放**：`AppendLeafOrRecurse` 先入 `owned`、待 `AppendEntity` **与** `AddNewlyCreatedDBObject` 皆成功才摘除——中途失败产物仍由调用方 finally `Dispose`（旧实现 AppendEntity 后即摘除，后一调用抛错则该对象无人释放）。
- **门禁**：`outputs/pr30-final-fix/gate-sln.log` — **1645 通过 / 0 失败 / 1 跳过**（= TArchBridge.Tests 32 + Llm 33 + Core 261 + Data 444 + View 381 + Zwcad 16 真机实跑 + Cli 342 + **Acad 137**（136 通过 / 1 跳过——桥 4 真机：T1/A4/T3 实跑绿 + T2 设计性 Skip））；本波 +14 测试（TArchBridge.Tests +5 / Cli +6 / Acad +2 / Data +1）。**诚实记录**：全 sln 单次跑 View 1 例失败（`MainWindowHeadlessTests.IssueRow_CheckboxPointerClick_TogglesEntryIsChecked` 的布局确定性前置 `laidOut` 断言，headless 排列时序）→ 该工程隔离复跑先复现 1 次、再复跑 **381/381 绿**（单测过滤单跑恒绿）⇒ 合并口径 **1645 通过 / 0 失败 / 1 跳过**；日志 `outputs/pr30-final-fix/view-isolated{,-2}.log`。门禁前后 `tasklist` **0 acad**。

**切片B 边界补齐（A1 退化 extents / A2 SymbArrow 无几何 / B1 块定义 TDb 独立段 / B2 文档口径，feat/cad-agent-boundary-completion，2026-11-27 记录；追加式，旧文 0 删）**：

- **A1 退化 extents → bbox=null + 显式 FAIL**：桥 `ReadInto` 在 `GeometricExtents` 成功后判六分量 |v| 全 < 1e-6 —— 命中即 `ENTITY` 行 bbox 两列留空（=null）并追加 `FAIL <handle> extents:degenerate`。动机：退化实体（无几何/零尺寸，spike 在 16#1116 实证 47 行）此前被写成立方全零包围盒 `0,0,0..0,0,0`，消费侧无法区分「读不到」与「读到的就是零」。异常路径（`extents:<ExType>`）逐字不变。宿主侧 `ExplodeEvidence.FailReason` 透出该原因（见 A2）。**修复波1（I-2）**：`TarchDerive` 种子条件增加退化判据——`FailReason` 以 `extents:degenerate` 起头（含前缀细分原因）的证据行**不落 NativeExplode 0.9**：旧实现 bbox=null 时静默跳过 bbox 比对，等于把「包围盒不可验」当「已验证一致」；保守口径=不种子（证据行本身保留供人工复核），其余（Fully+类已知+bbox 一致）照旧种子。
- **A2 SymbArrow 无几何原因化 + 宿主透出**：桥 `TryExplodeDeep` 捕获到 `Autodesk.AutoCAD.Runtime.Exception` 且 `ErrorStatus == InvalidInput`（ObjectARX 名 `eInvalidInput`；2014 反射核实 .NET 枚举元素无 `e` 前缀）→ 追加 `FAIL no-geometry:eInvalidInput`（此前落 `explode:<ExType>` 的泛化文案）；其余异常文案逐字不变。宿主 `ExplodeEvidence` 增可选尾参 `FailReason`（JSON 加性：旧 overlay 反序列化得 null），`MapExplodeEvidence` 取该句柄**首条** FAIL 行原因。
- **A2 诚实边界（方向文字）**：`TDbSymbArrow` 的**几何**双通道仍是 eInvalidInput（炸开永久 FAIL、bbox 不可得）；其**方向文字**若需读，仍走既有 COM `--native` 通道（`ReadNativeParams`），本轮不为它新增 COM 补采。
- **B2 文档口径（零尺寸文字内容，仅文档，无 COM 补采）**：退化 extents 的文字类实体（bbox=null）其**文字内容**不因 A1 丢失——内容面事实以 `--native`（COM 参数/属性通道）为准；`--explode` 证据只说明「无可用包围盒」。本轮 B2 = 明示该分工，不新增 COM 通道（YAGNI，避免与切片A 读参面重复）。
- **B1 块定义 TDb = 独立段（不是实例计数）**：桥 READ 在模型空间循环后遍历 `BlockTable`（**`BlockTableRecord.IsLayout` 为主判据**跳过模型/图纸空间布局块；`*Model_Space` 与 `*Paper_Space*` 名称前缀保留为兜底），对每个块定义内的 TDb 实体按 (块名, 类) 聚合 → 协议行 `BDEF\t<blockName>\t<rxClass>\t<count>`。**定义计数 ≠ 实例计数**：同一块可零实例可多实例，两段互不推导；嵌套块只统计定义内的**直接**实体（不递归展开，嵌套深度不做二次解释）。宿主链路：`BridgeResult.BlockDefs` → `AcadRuntime.MapBlockDefs` → `Core.BlockDefTdbEntry` → `SemanticOverlay.BlockDefTdb`（JSON 加性、Load 归一空表）→ `TarchDerive.Enrich(overlay, evidences, blockDefs)` **每轮 --explode 整表替换**（重扫刷新；空/缺省清空）；**非 --explode 扫描保留上轮定义段**（与 ExplodeEvidence 表同语义：Enrich 只在深读路径调用，普通扫描不得凭「本轮没读块定义」清空磁盘事实；stdout/审计仍只反映本轮读数）。CLI `semantic scan --explode` stdout 加性键 `blockDefTdb`（{blockName,rxClass,count}）+ 审计加性 `blockDefTdbCount`。块定义段**只披露不造标注**、不进证据表。伴生读数缓存：`ICadRuntime.ReadExplodeBlockDefs(path)` 与 `ReadExplodeEvidence(path)` 同一次桥会话（`AcadRuntime` 实例缓存归一化路径 + 本轮块定义表；**Close 成功之后**才写缓存，读/Close 失败不填充；`Dispose` 清空缓存——缓存生命周期=实例/桥会话生命周期；缓存未命中响亮拒绝，绝不隐式二次开图）。native-live 不取块定义段（无消费面）。
- **标识符与标本**：电气标本两文件置于 `outputs/specimens/elec-tdbe/`（local-only 不入库，含 `.sha256`/`.origin.txt`）——`elec-tdbe-01.dwg`（模型空间实例面；`TDbECabletry`/`TDbEFitting` 类证据）与 `elec-tdbe-02.dwg`（块定义面：`BDEF` 行集中于电气类）。测试目录可经环境变量 `CADAGENT_ELEC_TDBE_SPECIMEN_DIR` 覆盖（默认仓库相对目录）。真机骨架：`AcadBridgeLiveTests.T5_ElecTdbeClasses_Read`（T5：两类各 ≥1 行，缺失 Skip + 直方图）、`T6_BlockDefTdb_SeparateSection`（T6：块定义非空 + 含电气类 + 不混入实例句柄集 + (块名,类) 唯一）、`T7_DegenerateExtents_NullBbox`（T7：`extents:degenerate` ≥40 行（spike 实证 47）、其 Bbox 全 null、反向断言无非空全零 bbox）；三者均 `SkippableFact` + 既有四道守卫 + CUIX/acad 零残留/源副本 SHA 金丝雀，本轮只编译不实跑（控制器侧执行）。**修复波1（M-6）**：T5/T6 不再依赖 16#1116 的 `_fx.SpecimenReady`——环境闸拆出 `needsWallSpecimen` 参数，电类用例只由 `CreateElecCopy` 按 elec-tdbe-01/02 自身存在性 Skip；收尾金丝雀改用 T5/T6 专用 `AssertElecCanaries`（CUIX + acad 零残留；副本 SHA 用例内联断言），避免 16#1116 缺失时误 Skip/误断言。T6 的语义边界由 fake 测试覆盖（Data `Enrich_BlockDefs_DisclosureOnly_DoesNotTouchAnnotationsOrNativeEntities`、Cli `ScanExplode_BlockDefTdb_SeparateSection_AdditiveKeys`）。
- **门禁（修复波1）**：门禁**含桥构建**——`dotnet build src/CadAgent.TArchBridge/CadAgent.TArchBridge.csproj`（net45）**0 警告 / 0 错误**（`outputs/boundary-completion/bridge-build-fixwave1.log`）；`dotnet test CadAgent.sln --filter "Category!=RequiresAutoCAD"` — **1671 通过 / 0 失败 / 0 跳过**（`outputs/boundary-completion/fixwave1-gate-sln-2.log`；较基座 1666 +5：Data +2 退化不种子 Theory 两例 / Acad +2 缓存生命周期 / Cli +1 非 --explode 保留定义段）。本波只跑非真机门禁，T5/T6/T7 真机骨架**未实跑**（真机执行留控制器侧）。
- **sidecar 清理**：本轮不做（可选项留后续；既有 `outputs/archive-sidecar-cleanup/` 证据与策略不变）。

## 13. 块级纠偏清单（块几何指纹匹配 skill 符号库，2026-11-23 设计批准）

**动机（语料实证）**：用户日常图纸为**无属性 plain 块语料**——两真实标本块普查 `withAttr=0`（电施强电 246 非空块/105 匿名；1#配套用房 235/87，`outputs/api-probe/block-census-out.txt`），§4 块属性 NativeObject 种子（Tag=="A" 路径）对该语料零命中。块级纠偏＝几何指纹匹配 skill 符号库（324 强电/弱电 + 17 灯具矢量化 DXF）产三级输出，落 §4.1② 不明确区域人工纠偏的块级落点。

**诚实边界（实证钉死）**：电施 `$equip$00002832`×1364 等天正设备块**块定义无可读几何**（代理图形不暴露，census Yellow 领地）——指纹不可匹配，不假装；此类块入歧义清单标 `reason="代理图形不可读"` + T3 指引。指纹真实目标＝有实几何的自定义 plain 块（如 `2` 96 实体 10.2×27.3、`2021_0722_204741` 700×700 Circle×22）与标准符号块。GB 符号库精确命中在该语料可能稀少——三级输出价值在建议+人工映射两翼，不赌命中率。

### 13.1 决策基线（用户批准）

- **落种子分级**：精确指纹命中→自动 `GeometryInference` 0.9（高于语义闸 0.8、低于人工 1.0，仿 LayerInference 0.8 确定性先例）；近似命中→**只建议不自动**，人工确认→`HumanConfirmed` 1.0；无命中高频块→块级歧义清单待人工映射。
- **扫描范围**：全块定义（含 `*U` 匿名块——T3 导出态几何常落匿名块）；排除 `*Model`/`*Paper` 空间块、空定义、图框块（**AttributeDefinition 存在且 bbox 最长边≥500**——实证图框 841~1486 vs 符号库≤10）、`$LinePat$`/`$TCHSYS$` 系统前缀块。符号库范围＝项目已加载 skill 包（与 insert-symbol 同口径，`--skill` 注入同路）。
- **方案选定 A**：归一化矢量指纹+三级匹配（纯 Data 层，无渲染依赖）；否决 B 纯精确（丢近似翼，与分级决策矛盾）/C 位图 pHash（渲染依赖+分辨率敏感+失矢量精度，341 符号库过度工程）。

### 13.2 指纹与匹配算法

- **指纹提取**（`BlockFingerprint`，Data/Semantic，纯静态）：块定义**直接几何**（不递归子块——嵌套符号实例经其自身块定义匹配）仅 LINE/CIRCLE/ARC（与 insert-symbol 白名单同口径；TEXT 不入哈希——注记文字随实例变化，留作辅助证据）→ bbox 平移归零 + **最长边归一**（退化 bbox 安全：13 单轴符号实证）→ 坐标量化 round(1e-3) → 实体元组规范排序 → SHA256 大写 hex（借鉴 AdjudicationSet.Fingerprint 模式）。
- **结构签名**（近似预过滤）：实体类型直方图 + bbox 宽高比 + 圆半径比签名；签名相容才入几何距离计算。
- **近似距离**：归一化几何逐实体匹配平均距离 d∈[0,1]；**初始阈值 0.15**（真实标本实战校准，校准证据入执行记录）；置信度 = **0.9 − 2d**（d=阈值 0.15 时置信 0.6，低于闸 0.8 → 近似永不自动落种子，数学保证）。
- **精确层**：哈希相等→该块全部 Insert 实例落 `GeometryInference` 0.9，evidence="几何指纹精确匹配: {符号名}"。
- **近似层**：d≤阈值→建议（blockName, candidates[符号名, d, confidence] 按 d 升序全列不静默择一, instanceCount, sampleHandles≤5）。
- **歧义层**：无命中且 instanceCount≥**3**（默认，`semantic scan --min-block-instances <n>` 可配）→块级歧义（blockName, instanceCount, 实体直方图, bbox, sampleHandles, reason）；代理无几何块 reason="代理图形不可读"+T3 指引。

### 13.3 组件与数据流

- 新 `src/CadAgent.Data/Semantic/BlockFingerprint.cs`（提取/签名/距离）+ `BlockSymbolMatcher.cs`（消费 SkillSet 符号库→索引→三级输出）；符号 DXF 读取复用 SkillPackage.LoadSymbols + 白名单校验，**读失败/超白名单实体→该符号诚实跳过+计数披露**（不整体失败）。
- `SemanticScanner.Scan` 信号链：BlockAttributeSeeder 之后插入块指纹信号；HumanConfirmed 跳过不变；re-scan 先清陈旧块级 GeometryInference（仿 NativeObject 清理先例），HumanConfirmed 保留。
- CLI `semantic scan` stdout **加性键** blockAutoSeeded/blockSuggestions/blockAmbiguous（旧字段逐字不变）；审计 semantic-scan params 加性计数。
- 新 CLI `semantic map-block --block <name> --symbol <symbolName> [--class <cls>]`：确认时**重提取指纹复核**（精确或 d≤阈值，否则拒绝留痕 exit 非零）→ HumanConfirmed 1.0 落该块全部实例（class 默认取符号 meta.Class，--class 覆盖须合法枚举）→ 审计 **semantic-map-block**（before==after 哈希，overlay-only）。**Yellow 闸豁免沿用 semantic map 先例**（只改 overlay 不改图纸）；单写入通道+原子写复用。
- 建议**不持久化**（scan 确定性可重算，map-block 复核制）；View 面板 deferred（CLI 先行切片先例）。

### 13.4 错误处理与并发

- 符号库空/未加载→三级输出空+披露（不报错）；块定义读取异常→该块跳过+计数；量化/归一数值守卫 IsFinite（NaN/∞ 实体坐标→该实体剔除+披露）。
- map-block 失败面：块不存在/符号不存在/指纹不符/class 非法→审计拒绝（semantic-map-block-rejected）+stderr+exit 非零；overlay 写失败→既有降级链。
- 并发：scan 只读；map-block 经项目单写入通道（哈希链有序追加）；与在途编辑 tx 互斥沿用会话闸。

### 13.5 测试与验证

- 合成夹具 TDD：精确克隆命中 0.9 / 扰动近似建议（d>0 不自动）/ TEXT 差异仍精确 / 图框排除 / 系统前缀排除 / 退化 bbox 归一 / 多候选排序不静默择一 / 置信公式闸下保证 / map-block 确认→HumanConfirmed+闸过 / 复核不符拒绝留痕 / re-scan 清陈旧保 HumanConfirmed / Yellow 豁免 parity / 空库披露 / 代理块 reason 标注。
- 真实标本 skip-gated 实战（电施副本，源零触碰）：三级输出诚实期望（精确命中可能 0）+ $equip$ 代理标注 + 高频自定义块入歧义；before/after 证据存档 outputs/block-fingerprint/。
- 门禁全 sln 连绿两次；§12 追加闭环注记（含阈值校准证据）。

### 13.6 不做（明示）

位图渲染/pHash；建议持久化；View 面板（deferred）；近似自动种子；$equip$ 代理语义硬解析（census/T3 领地）；子块递归指纹（嵌套经自身定义匹配）；图框块语义种子。

## 附录：调查证据来源

- Autodesk 官方编程接口文档：https://help.autodesk.com/cloudhelp/2026/CHS/AutoCAD-Customization/files/GUID-E6429154-36DF-4D84-8ABC-9FCA15B66158.htm
- AutoCAD API 总览：https://aps.autodesk.com/developer/overview/autocad-api
- 中望官方开发文档：https://www.zwsoft.cn/support/zwcad-devdoc
- 中望二次开发接口简介：https://confluence.zwcad.com/pages/viewpage.action?pageId=263914249
- 天正官方产品说明：http://tangent.com.cn/cpzhongxin/jianzhu/528.html
- 天正代理对象机制：https://baijiahao.baidu.com/s?id=1802388504690620369
- T3 导出本质（炸开）：https://wap.zol.com.cn/ask/x_22624319.html
- ACadSharp 天正原生解析 issue #1131：https://github.com/DomCR/ACadSharp/issues/1131
- 天正文本对象 PR #1275（已合并）：https://github.com/DomCR/ACadSharp/pull/1275
- 天正实体扩展 PR #1337（推进中，含版本风险记录）：https://github.com/DomCR/ACadSharp/pull/1337
- 开源 DWG/DXF 库对比：https://m.blog.csdn.net/ultramand/article/details/161655656 、https://www.cnblogs.com/znlgis/p/19466681
- 本机证据：ZWCAD 2025 COM 真机探针（2026-11-20）；安装目录 .NET 互操作程序集；试用授权文件
- **ZWCAD CUIX 竞态事故与修复（2026-11-22）**：真机测试4类xUnit默认并行→多ZWCAD实例共享单用户profile→CUIX加载/保存竞态损坏→启动弹模态修复框（挂起无人值守自动化）。历史根因(a)带开文档Quit已由side chat修（Close→Quit）；根因(b)并发竞态本次修：真机测试串行化（xunit.runner.json parallelizeTestCollections=false + [Collection("ZwcadLive")]）+ ZwcadRuntime.EnsureNoForeignInstance 启动守卫（已有实例等待10s退出否则拒绝，ProcessNames seam可测）。**约束：真机测试期间不得并行其他ZWCAD自动化/人工会话**。证据：outputs/cuix-evidence-20261122/（备份+哈希清单；局限注记：保留字节为竞态后clean write，损坏中间态已被覆盖，根因链=截图+时间戳+并行配置+ZCR残留）。

- **天正准入实战（2026-11-22，真实标本🟢闭环）**：两份转换态标本（1#配套用房0928 建筑/电施强电-2#厂房）跑通 scan→不明确清单→人工映射（用户批准 7 层→3128 HumanConfirmed 1.0）→re-scan→闸口放行（电施 Lighting 1364 NativeObject 0.95 块属性种子）→label-luminaires tx-commit→**confinement 实证 328 编号全落声明幅面零跨幅**→audit verify 绿；拒绝侧跨幅（sheetId 作用域 reason）+未确认语义（Room）双拒绝留痕。证据 outputs/tarch-native2/real-combat-evidence.txt。**缺口**：🔴拒收+T3指引/🟡部分解析无原生标本——四份归档件均转存态（Proxy/Unknown 全 0、无 TArch XData、算量/提清单软件转存痕迹；方法学三重验证：DwgReader 保留 Proxy 往返实证+源码 readUnlistedType→UnknownEntity+递归块表）。**边界澄清+语料特征（用户澄清 2026-11-22 修正）**：日常接触图纸多为**普通图元+自定义图层**制图（工作人员不使用天正预装图例/属性块的习惯），故无 TCH 自定义对象/属性块属**语料特征而非必转存**（此前"算量转存链"推断过度，已修正）；此类图纸对象级即较高信息完整度，Green 判定真实有效。语义重建主力=几何启发式（实战 Wall 513 命中）+层约定+人工映射（3128 确认）+视觉交叉验证；块属性种子为加分项（有属性块时生效）。**后续候选**：块名任意且无属性→块级纠偏清单（块几何指纹匹配 skill 符号库），与层纠偏同流程。🔴/ 路径服务于使用天正自定义对象的其他语料（本语料罕见），合成证据在案。

- **天正原生性决定性修正（2026-11-22，用户中望观察触发）**：四份标本**均为原生天正图**（OnNotification 普查：每份数千 TCH_* 被读为 UnknownEntity，如电施 TCH_WALL 1592/TCH_OPENING 650；水施 TCH_PIPE 994）。**ACadSharp 3.8.35 对象图不暴露 UnknownEntity**（doc.Entities/全块表普查 0，读取器解析后丢弃出对象图）→ intake/scan/render 对 TCH 对象全盲 → **Green 为假 Green（spec ⑥ 违反，产品缺陷）**；渲染静默缺失天正墙体/门窗/管道。此前"转存态/算量链/语料特征 plain-entity"结论**错误已作废**（4c39e81/fb789e2 框架被本条取代）；🟢 闭环证据为盲子集上的，修复后重跑。**修复通道**：ICadReader.OnNotification 逐类 UnknownEntity 计数→闸口判 🟡（原生图元可解析+TCH 未解析）→只读审查+语义操作全图级拦截（对象图无句柄/几何，未解析区不可定位，诚实降级）+T3 指引；🔴/🟡 实战现有真实标本。后续切片：通知感知准入闸口+渲染缺失警示+🟡拦截语义重跑闭环。

- **通知感知准入闸口交付闭环（防假 Green 切片 T1-T5，2026-11-23 记录；全套件 1007 测试连绿两次含 13 Zwcad 真机）**：上条修复通道已交付。**census 通道**：DwgReader.OnNotification 逐类 UnknownEntity 计数（UnparsedCensus）→sidecar `.cadagent/unparsed.json` 源哈希键控（路径+SHA256 匹配方有效；陈旧/损坏/源缺失→诚实 Empty）→旧 working 三分（R9 旁路闭合）：persisted 非空直载不重读源／源 .dwg 在→从源通知一次性重建 census（只取 census，working 绝不重物化）+回写 sidecar／源缺失→拒绝"无法核验完整性"（intake/run/repl/agent/export/skill/rollback 各消费点留痕）。**三分判据（R6 口径）**：Red=无原生实体（Entities.Count==0 且全未支持）；Yellow=有原生+未解析（census 非空或图内 Unknowns）→只读审查+修改/语义操作**全图级拦截**（census 无句柄/几何，未解析区不可定位，诚实降级）+T3 指引；Green 放行——无 census Green stdout 逐字保持（Unparsed null-omit）。**六修改消费点拒绝留痕**：run/skill run/agent/repl/view-edit/export 全经 LoadWithCensus+GateRefusesRun 单点（agent/repl 闸先于 LLM、export 闸先于 RuntimeFactory 零衍生产物；run-rejected/export-rejected params 含 level/unparsedCount/guidance，前后哈希一致）；**rollback/review 驳回留痕豁免**（R7 撤回处置语义：快照恢复非新增操作，Yellow 允许；R10 rollback LoadWithCensus persisted 直载）。T5 顺手修：①视图降级 headless 钉住（.dwg working 降级 sidecar census 恢复→ErrorMessage/StateNote 未解析警示+R9 加载失败优雅降级不进入渲染/编辑，变异 RED 实证）；②review/check 裸 Load→LoadWithCensus 等价一致性（只读路径享受 persisted census 直载，行为保持测试绿）。**真实标本实证（T4）**：四原生标本 intake 全 Yellow——电施 5985/32 类（TCH_WALL 1592）/1#配套 3257/30 类/2号厂房 5707/30 类/S-1#配套 3806/28 类（TCH_PIPE 994）；电施五消费点拒绝留痕核对+五项目 audit verify 绿；**法院标本 Yellow 1836/13 类**（TCH_DIMENSION2 550/TCH_TEXT 523/TCH_WALL 482…）→2026-11-22"T3 导出态/Green"判定**作废**（对象图盲区）。证据 outputs/tarch-gate-recombat/ + docs/verification/sheet-aware-protocol.md §5。**🟢 真实重取待用户 T3 转换**：真实 Green 载体不可得（法院+四标本全 Yellow），当前 🟢 证据=合成 E2E（SheetAwareAgentE2E/LightingBenchmark 等）；未执行 map、未伪造人工确认。**门禁**：outputs/tarch-gate-recombat/final-gate{1,2}.log（1007=Core 207+Data 288+Cli 200+View 269+Llm 30+Zwcad 13，0 跳过；真机前后 tasklist 核查无 ZWCAD 残留/无并行会话）。**剩余边界（明示不假装）**：①真实 Red 标本缺——四份均 Entities>0，🔴 拒绝仅合成 ProxyEntity 夹具覆盖；②视觉管道生产接线留 harness——IRenderSnapshot 实现+VL reader 未接生产 CLI（fake seam 测试为自动化闸口）；③块级纠偏清单候选——块名任意且无属性→块几何指纹匹配 skill 符号库，与层纠偏同流程（后续切片）；④纸面编辑 deferred（布局幅面只读硬闸，不做纸面→模型逆映射）；⑤真实 🟢 闭环待用户天正【文件布图→图形导出】T3 转换件重跑（期望 census 空 Green→scan→人工 map→幅面闸口操作→verify）。
- **AutoCAD 崩溃报告核查（2026-11-24，用户提供 acminidump.dmp/dumpdata.zip）**：崩溃文档=z1-no-tables.dxf（首轮真机调试期 ACadSharp 3.8.35 所写 DXF 变体），会话 7 秒即崩，异常 0xE0000001（AutoCAD 内部 fatal raise，非访问违规），崩溃线程栈归因 **accore.dll（DXF I/O）→acdb19.dll→axdb.dll** 解析管线；97 会话 3 次非清洁退出对应调试期+授权 taskkill。**根因=AutoCAD 2014 DXF 解析器对 ACadSharp 写入内容不兼容致崩溃**（版本标签降级 AC1015 仍拒/仍崩，内容级不兼容）。**修复状态**：测试路径已修（AcadLive 夹具改手写 R12，不再喂 ACadSharp DXF 给 AutoCAD；4/4 绿+1297×2 清洁退出实证）；生产路径无 ACadSharp DXF→AutoCAD 流（live 导出经厂商存 DWG；working.dxf 仅 ACadSharp 自身回读）。**残余风险（诚实注记）**：用户若手动以 AutoCAD 2014 打开 .working.dxf（ACadSharp 产物）可能触发同款崩溃——厂商解析器健壮性问题，非我方可修；缓解=文档警示+后续评估 working 文件格式兼容写出。崩溃分析证据 outputs/autocad-verify-20261123/crash-analysis.md。
- **崩溃注记更正 + acad 输入闸 fail-closed（2026-11-25，PR#24 合并前 ISTQB 审查修复波 I1/I2，追加式更正，旧文 0 删）**：上条"生产路径无 ACadSharp DXF→AutoCAD 流"声明**被证伪，特此更正**——.dwg 源 SourcePath=working.dxf（ACadSharp DxfWriter 产物，DrawingSession.SaveWorking），**export**（CommandRouter.Export→ExportService.Export→_runtime.Open(sourceDxf)）与 **--live**（StepEngine 事务前快照=working DXF 同源拷贝→CommandRouter.Live rt.Open(snap)）两条生产流把 ACadSharp DXF 喂厂商 COM Open；CADAGENT_RUNTIME=acad + AutoCAD 2014 = 崩溃/拒绝输入（矩阵 Verified 后 19.1 警示已移除，export gating 不再拦截）。**修复（fail-closed 代码闸）**：CommandRouter 单点闸 AcadWorkingDxfGateRefuses（镜像 GateRefusesRun 先例，export/--live 共用）——acad 且输入为 working 派生 DXF（路径==DrawingSession.WorkingPathFor(源) 或 .working.dxf 后缀；--live 快照 basename 同源判定；.dxf 源恒真——引擎原地重写）→**先于运行时创建拒绝**（不激活 COM，拒绝路径无厂商进程启动）+ export-rejected/com-replay-failed 审计留痕（reason=acad-working-dxf）+ guidance（"AutoCAD 2014 无法解析 ACadSharp 写入的 DXF（内容级不兼容，实证崩溃 0xE0000001）；请用默认 zwcad 运行时或 T3 转换件"），exit 非零；zwcad 路径逐字不受影响（读取器兼容，真机实证）。**诚实披露**：闸拒绝路径本身清洁（无 COM 激活）；但崩溃若经旁路发生（闸外代码路径/用户手动打开 working.dxf），非清洁退出→CUIX 回写竞态风险仍适用（PR#17 机制）；acad 不可用时的 DXF 降级交付在 acad 显式选择下同样被闸拒绝（降级产物即 ACadSharp DXF，交付即残余崩溃风险，fail-closed 不旁路）。**I2 重试粒度更正**：AcadRuntime 整 lambda 忙重试降为**单 COM 基元调用**粒度（Run 调度层不再重试；Open/SaveAs/Close/GetInfo/EntityCount 单调用入口自包装；AddLine/AddText/AddCircle/AddInsert=AddX+SetLayer 逐基元组合已重试基元，移除整段外层重试——后段拒绝→整段重试会重复实体）；EnsureBlock 重入补全（exists 时比对块内实体数与期望基元数：完整→复用幂等保持；不一致→清除残留基元重建，仅本方命名块安全；中途拒绝耗尽不再静默部分块）；原 :63 "重试无副作用重复之虞"过度声明更正为单调用口径；MissingMember 消息匹配脆弱面注释披露（M3：区域/绑定器版本失配→漏判忙拒绝当普通错误上抛，安全侧失败不重复副作用）。**边界单测（fake seam 非真机）**：acad+working.dxf→拒绝+审计/不激活 COM、zwcad 同输入放行、第 N 次基元忙拒绝→实体数正确不重复、EnsureBlock 中途耗尽→重入补全完整、Restart/Dispose Quit 忙拒绝耗尽→有界上抛/有界尝试。**tracked 后续**：①working 文件兼容写出格式评估（ACadSharp DXF 内容级不兼容根因未修，本闸为 interim fail-closed）；②M4 run/rollback 裸 positional 参数（tracked 不修）。**门禁口径**：Acad 4 真机测试本轮不跑（环境敏感+闸先于 COM 激活不影响真机已验证路径，注记留待合并后或下轮重跑）；非真机全量绿。修复报告 outputs/autocad-verify-20261123/fixwave-i1i2-report.md。
- **EnsureBlock 重名碰撞守卫（2026-11-25，复审微修复波，追加式更正，旧文 0 删）**：上条 I2 "仅本方命名块……清除安全"为注释假设非代码约束——生产 insert-symbol（ComReplayService:145）传 skill meta.Name，与用户图纸自有块重名（如 "DOOR"）时重入补全会**删除用户块内容**（旧行为静默复用非破坏）。修复：AcadRuntime 实例持 `_createdBlocks`（HashSet，OrdinalIgnoreCase 与符号匹配同口径；Blocks.Add 创建成功即入集合、先于基元填充——中途耗尽残留亦属本方）；重入时名在集合→count 比对/补全重建（I2 语义保持）；**名不在集合（预存用户块）→复用早退不 Delete（恢复旧非破坏行为）**；Open 换文档清空集合（跨文档同名块非本文档会话残留）。单测钉住：预存同名 count 不一致→复用无 Delete（含大小写变体）+换文档同名预存→复用；既有中途耗尽重入补全/完整幂等测试保持绿。**tracked**：Zwcad EnsureBlock 对齐（现为恒静默复用，无删除风险但亦无本方残留补全）仍 tracked 不修。门禁：Acad.Tests 单测 25/25 + Cli.Tests 260/260 绿（真机/COM 本轮不跑）。
- **acad export/--live DWG 快照路径落地（2026-11-25，用户批准设计，解除 acad fail-closed 停用，追加式更正，旧文 0 删）**：上条 I1 "interim fail-closed 闸"的 tracked 后续①（working 文件兼容写出格式评估）已落地——**DWG 快照路径**取代无条件拒绝。**探针实证**（outputs/api-probe/compat-probe-report.md）：ACadSharp DwgWriter AC1027 产物 AutoCAD 2014 直接 COM Open（0.5s，CJK 保真）；真实标本 t3（AC1018/2565 实体）DwgReader→DwgWriter→Open 2.5s 零差异（96 层/129 块/类型直方图全保真）；DxfWriter AC1015 被拒 0x80200157（内容级不兼容，弃用喂 acad）。**实现**：CommandRouter.ConvertAcadDwgSnapshot seam（working 派生 DXF→DxfReader 读入→`doc.Header.Version=AC1027`→DwgWriter 写独立 GUID 临时子目录 .dwg）+ CleanupAcadDwgSnapshot（用后即清整目录，best-effort）。**版本设置方式经 API 核实**：DwgWriter 构造仅 (path/stream, CadDocument)，DwgWriterConfiguration 无 Version 属性——版本唯一来源 doc.Header.Version（反射 + 往返实证）。**闸口径升级**：AcadWorkingDxfGateRefuses 判定谓词逐字不变（仍识别 working 派生）；export（CommandRouter.Export）/--live（CommandRouter.Live）调用点改为**先转换 DWG 快照**——成功放行（ExportService.Open / rt.Open 喂 DWG 快照，非 working DXF）；**转换失败仍 fail-closed 拒绝**（先于运行时创建不激活 COM）+ export-rejected/com-replay-failed 审计（reason=acad-working-dxf + conversionError）+ guidance，exit 非零（安全侧保持）；acad working 派生 + 无厂商降级亦拒绝（降级产物即 ACadSharp DXF，fail-closed 不旁路，镜像旧闸口径）。**非 working 派生输入（原生 .dwg）闸 false→不转换直接 Open 不变；zwcad 路径零改动**（闸 false，DXF 路径回归钉住）。**CJK 钉 AC1027（抽检实证 outputs/acad-dwg-snapshot/cjk-probe）**：DwgWriter 在 AC1015/AC1018 写中文有损（「中文标注测试」→??????），AC1027/AC1032 保真；DxfWriter 同口径（AC1015/AC1018 有损，AC1027/AC1032 保真）——故快照版本固定 AC1027（2014 可读上限，AC1032 默认会被 2014 拒）。**预存限制诚实披露（非本切片引入，约束 DxfWriter 不修/working 格式不变）**：AC1015/AC1018 源经 SaveWorking（DxfWriter 按源版本写）已在 working.dxf 丢 CJK，快照读 working.dxf 继承该损失——钉 AC1027 仅保证快照步不再新增损失，不能恢复 SaveWorking 已丢的 CJK；AC1027/AC1032 源 working.dxf 含 CJK→快照保真。**测试（TDD 先红后绿）**：AcadDwgSnapshotTests 8 例（export/--live 快照=.dwg 后缀+AC1027 头+CJK 保真+用后即清；export/--live 转换失败 fail-closed 拒绝+审计+guidance+不激活 COM；acad working+无厂商降级拒绝；zwcad 同输入不转换 DXF 路径不变；原生 .dwg 闸 false 不转换等价类；CJK AC1018 有损/AC1027 保真往返抽检）；AcadWorkingDxfGateTests 改写为转换失败注入（fail-closed 安全侧保持）；**skip-gated 真机** AcadExportSnapshotLiveTests.Export_Acad_WorkingDxfSnapshot_RoundTripCjk 绿（AcadLive Collection 串行，AutoCAD 2014 R19.1，CADAGENT_RUNTIME=acad 驱动生产路径，working CJK DXF→DWG 快照→真实 Open→SaveAs→DwgReader 读回 CJK 保真+1 实体，20s）。**门禁**：Cli.Tests 270/270 + Acad.Tests 30/30（含 5 真机：AcadLive 4 + 新 export 快照 1，0 跳过）+ Zwcad.Tests 10/10 真机串行；全 sln 1340/1340 绿（0 失败 0 跳过，真机前后 tasklist 无 acad/zwcad 残留）。**DXF 写入器内容级不兼容在案**（DxfWriter 产物喂 AutoCAD 2014 拒/崩，根因未修，DWG 快照为兼容写出通道）。实现报告 outputs/acad-dwg-snapshot/implement-report.md。
- **acad DWG 快照微修复波（2026-11-25，复审 I1/I2/M4/M5，追加式更正，旧文 0 删）**：①**I1 转换器部分失败临时目录泄漏修**——ConvertAcadDwgSnapshot CreateDirectory 后写出抛时调用方拿不到快照路径（acadSnapDir 仍 null，外层 finally 清理不可达）→%TEMP% 孤儿 GUID 目录+部分 .dwg 永不清理；修=转换器内部 try/catch 失败 best-effort Directory.Delete 自清再上抛（fail-closed 拒绝语义逐字保持）；新增 WriteAcadDwgSnapshot 写出步 seam（Action<Stream,CadDocument>，供测试注入"建目录后抛"），单测钉住失败后无新增孤儿目录+拒绝审计+不激活 COM。②**I2 export 保真锚漂移修**——ExportService.Export sourceCount 以快照为锚，working→快照转换丢实体时 export 双闸口（源==导出）看不见（--live 有影子计数兜底，export 无）；修=CommandRouter.Export 转换成功后、运行时创建（Open）前校验 **working↔快照实体计数相等**，不等/读回失败（-1）→fail-closed 拒绝+export-rejected 审计（reason=snapshot-count-mismatch+guidance+workingEntities/snapshotEntities）+临时快照清理，不激活 COM；单测（fake 转换器返回空文档快照→拒绝）钉住；ExportService sourceCount 处补锚口径注释（Core 行为零改动）。③**M4**：AcadExportSnapshotLiveTests Assert.Equal(1, Count())→Assert.Single（xUnit2013 警告清零）。④**M5**：上条测试计数"7 例"更正为 8 例（漏列 Export_Acad_WorkingDxf_NoVendor_DegradedRefused，矩阵同步更正）；本波 +2 例→AcadDwgSnapshotTests 10 例。**门禁（本波不跑真机/COM）**：Cli.Tests 273/273 + Acad.Tests 单测 25/25 + 全 sln 1333/1333 一次绿（filter 排除 RequiresAutoCAD/RequiresZwcad，0 失败 0 跳过，tasklist 无 acad/zwcad 进程；Cli 基线实测 271——上条门禁 270 少计 1 例，本波 +2 后 273）。**tracked 不修**：M3（审计 target 临时路径）/M6（CJK 损失断言强化）。证据 outputs/acad-dwg-snapshot/fixwave-{red,green,cli,acad,sln}.log；报告追加 implement-report.md。
- **acad DWG 快照微修复波二（2026-11-24，复审 I-1/M-1/M-4，追加式更正，旧文 0 删）**：①**I-1 live 影子计数分歧拒绝零负测试补**——本 PR 使 acad live 首次经可能丢实体的转换，com-replay-divergence 分支（CommandRouter.Live vendor vs 影子读回闸）承重却无测试；补 fake 非 COM 负测试（真实转换器成功 + fake 厂商 EntityCount 错数≠drawing.Doc.Entities.Count→exit1 + com-replay-divergence 审计（vendorCount/shadowCount）+ 无 .live.dwg 交付 + VerifyChain + 临时快照已清）。**实现先行实证钉住（口径如实记录）**：分歧分支已存在，该负测试首跑即绿（非 TDD 红）。②**M-1 Live 清理序修**——原 finally 清理在 using(rt) Dispose 之前：失败路径厂商文档仍打开（文件锁）→Directory.Delete 因锁失败（best-effort 吞掉）→%TEMP% 孤儿 GUID 目录；修=清理移到 Dispose 之后（外层 finally 包 using，镜像 Export runtime?.Dispose()→Cleanup 序）；TDD 先红后绿：持快照文件句柄直到 Dispose 的 fake（FileShare.Read 模拟 COM 文档打开）+ 分歧失败路径目录差集断言——修前红（孤儿目录实证）→修后绿。③**M-4 live acad 转换成功+无厂商清理行专测补**——真实转换器成功 + RuntimeFactory null→拒绝口径按现状（exit1 + 无可用厂商运行时 + com-replay-failed，不降级交付）+ 快照目录已清（%TEMP% cadagent-acad-snap-* 差集为空）+ VerifyChain；该行已存在，首跑即绿（实证钉住）。**测试计数**：AcadDwgSnapshotTests 10→13 例（矩阵同步）。**门禁（本波不跑真机/COM）**：Cli.Tests 276/276 + Acad.Tests 单测 25/25 + 全 sln 1336/1336 一次绿（filter 排除 RequiresAutoCAD/RequiresZwcad，0 失败 0 跳过，tasklist 前后无 acad/zwcad 进程）。**预存披露**：%TEMP% 遗留 3 个历史孤儿快照目录（2026-11-24 13:58 早期轮次所建，非本波）不在本波清理范围；本波 RED 轮孤儿已按精确路径清除，GREEN 后无新增。证据 outputs/acad-dwg-snapshot/micro2-{red,green,cli,acad,sln}.log；报告追加 implement-report.md。
- **TArch .NET API 几何探索闭环（2026-11-24，§12:662 立项独立会话，THROWAWAY spike，src/ 零改动，追加式注记，旧文 0 删）**：天正完全解析双通道实证成立（生产接线 deferred）。**静态盘点**：T20 电气 V5.0 安装目录（C:\Tangent\TElecT20V5，profile 支持路径别名 telect20v5 内容同一性实证）**无托管程序集**（全 DLL/33 ARX native 判定）、**无 ObjectARX SDK/头文件**；AutoCAD 2014 .NET 宿主 AcMgd/AcDbMgd/AcCoreMgd 19.1 齐备（NETLOAD 通道，探针须 .NET Framework 4.x）。**重大更正（双证钉死）**：天正自带 native COM 服务器（tch10_com19/PipeBaseCom19/Pipewire19.dll）内嵌 **70+ ICom\* 自定义 DISPATCH 接口**（类型库 LoadTypeLib 只读提取 13012 行；IComWall 137 成员；电气 TDbEEquipment 等含 ObjectDBX\ActiveXCLSID 映射；注册为 ARX 加载期动态）——**profile 探针报告 §3「COM 几何不可达」结论限定更正**：不可达仅限标准 AutoCAD 名（Coordinates/TextString/Area 不在 ICom* 面）；live 重轮实证 **TDbWall COM LeftWidth=100/RightWidth=0/Height=3000/Volume=2.895/IsArc=直墙 + GetBoundingBox**、TDbText Text="A1+1/2 1:100"、TDbColumn Width=500/Volume=0.750、TDbEquipment PScale/LRotation 全可达。**Phase B .NET 通道（NETLOAD TarchGeomProbe.dll，用户批准在场）**：TDb* marshaling 为 **ImpEntity/ImpCurve**（TDbWall/Dimension2/PolyPathArray=ImpCurve，余 ImpEntity；无需天正托管包装，基类 API 经 C++ 虚函数直达）——**GeometricExtents 341/343 成功**（15 类 14 类全绿，仅 TDbSymbArrow 2/4 eInvalidInput；真实 3D 含墙高 3000）；**Explode 27/29 样本成功**：TDbWall→4 墙面线、TDbColumn→Hatch+4 边框线、TDbText→AcDbText 真实文本、TDbOpening→$DorLib2D$ 门窗库块、TDbEquipment→$TWTSYS$ 符号块+**XData HYDRANT_RADIUS=28**、TDbSpace→嵌套 TDbText；XData/扩展字典墙柱文本样本 null。**跨通道闭合**：COM GetBoundingBox 与 .NET extents 逐字一致；MS 1958/TDb 343 与 census 逐字闭合。**NETLOAD 安全弹窗证伪注记**：「2014 理论上无签名弹窗」假设被证伪——未签名程序集 NETLOAD 弹安全确认框（用户证实手动允许，SendCommand 阻塞 45 分钟），自动化须用户在场或评估 trusted paths（本轮不改配置）。**纪律**：三轮真机（run1 脚本 bug 失败留痕：CLSIDFromProgID 误标 oleaut32 实为 ole32+中文路径字面量 PS5.1 ANSI 损坏；run2 -AttachExisting 复用；run3 新起）串行、只读副本、CUIX/源件/副本哈希前后=基准、0 厂商残留、无 taskkill、临时目录用后即清。**剩余边界（明示）**：TDbSymbArrow 几何双通道同拒；部分块内 TDbText extents=0/炸开 0；墙中心线无直接属性（须炸开墙面线+宽度推导）；run2 COM 读空根因未明（run3 正常，不猜）；PS COM 适配器未知名返空不抛错（"OK 空"≠存在）。**后续候选**：census Yellow 件双通道理解/完全解析生产接线切片（COM 参数面免 NETLOAD 无弹窗为轻量首选，.NET Explode 补图元级几何）。证据 outputs/api-probe/tarch-dotnet-probe/（report.md + report-static.md + dotnet-probe-output.txt + phase-a-com-custom.txt + typelib-*.txt + 金丝雀）。
- **Zwcad EnsureBlock 对齐落地（2026-11-24，用户批准设计，追加式更正，旧文 0 删）**：上条「EnsureBlock 重名碰撞守卫」注记 tracked 的"Zwcad EnsureBlock 对齐（现为恒静默复用，无删除风险但亦无本方残留补全）"**已闭环**——两厂商 EnsureBlock 语义统一（created-set + 重入补全）。**实现（逐字镜像 Acad 已审查语义）**：ZwcadRuntime 实例持 `_createdBlocks`（HashSet，OrdinalIgnoreCase 与符号匹配同口径）；Open 换文档清空集合（跨文档同名块非本文档会话残留）；exists 且名不在集合（预存用户块）→复用早退**绝不 Delete**；exists 且名在集合→块内实体数与期望基元数比对：完整→复用幂等保持，不一致→**倒序 Delete 残留基元重建**（仅本方创建块，块定义保留 origin 不变引用不受影响）；Blocks.Add 成功即入集合（先于基元填充，中途失败残留亦属本方）；Item 宽 catch 与 Acad 现状一致（**收窄仍 tracked 不扩**）。Zwcad 无忙重试基础设施（RetryBusy 为 Acad 独有，本波不引入）——单次 COM 失败即上抛，残留形态与 Acad 中途耗尽同构。**测试（TDD）**：fake seam 单测 4 例镜像 Acad——中途失败残留→重入补全（Delete=1/Add=1/基元 2 不重复/尝试 4 计数钉住，**先红后绿**：旧实现静默早退 Expected 2 Actual 1）；预存同名 count 不一致→复用无 Delete（含大小写变体）/完整幂等/Open 换文档清空 3 例旧非破坏行为下**首跑即绿（实证钉住，口径如实记录）**。**真机实证（ZWCAD 2025，ZwcadLive Collection 串行，RequiresZwcad trait）**：中途失败以"不支持的块基元"确定性模拟（无需构造忙拒绝）→重入补全 SaveAs→DwgReader 读回块内 2 基元不重复+完整重入幂等；预存块非破坏（SaveAs→Close→Open 换文档会话→同名 count 1≠2 复用，读回用户 1 基元原样）。**ZWCAD 块 .Count/.Item(i)/.Delete() dynamic API 与 AutoCAD 同构经真机核定（不赌文档）**。**顺手调整披露**：ZwcadDrawingOpsTests 幂等探针由空基元集改同基元集——对齐语义下空集对本方创建块走清除重建通道，不再是幂等探针（Acad 侧 AcadLiveTests 同款空集调用按"Acad 零改动"约束不动，其断言不查块内容仍绿，旧语义注释留痕）。**约束遵守**：Acad 侧零改动；VendorProfile 不动；共享夹具不动。**门禁**：Zwcad.Tests 16/16（10→16：+4 单测 +2 真机；含 7 真机测试，0 跳过，真机前后 tasklist 无 ZWCAD/acad 进程，自家实例 Dispose Quit 清理）；全 sln 1347/1347 一次绿（filter 排除 RequiresAutoCAD 5 例——环境敏感先例口径，Zwcad 真机包含在内；0 失败 0 跳过）。证据 outputs/zwcad-ensureblock/（red/green-unit/zwcad-full/live-named/sln-gate.log）；实现报告 outputs/zwcad-ensureblock/implement-report.md。
