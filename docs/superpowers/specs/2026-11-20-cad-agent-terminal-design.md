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
- **渲染 deferred（不假装已渲染）**：尺寸标注实体（Dimension）、填充（Hatch）、线型虚实（dash 图案）、OCS 镜像/挤出变换、颜色精确表（ACI 全表映射当前简化）——RenderModelBuilder 跳过并计数，后续 slice 承接。
- **headless 覆盖范围**：View 232 测试（VM 纯逻辑单测 + Avalonia.Headless.XUnit 真实 Skia 帧控件测试）：渲染/画布/图层/问题/审计+diff/签认/纠偏+裁定/编辑全面板；T10 增重入闸（注入会话闸持闸制造确定性 in-flight）、--skill 三 VM 接线、WCAG 对比度计算、ClipToBounds、名字区双击隔离。真机视觉不依赖 headless 断言的部分走 §12 真机协议。
- **真机验证结果（docs/verification/plan4-ui-protocol.md + 22 PNG）**：启动渲染/缩放/fit/图层隔离/问题引用/手动画线+撤销/时间线高亮+Diff/签认 Review 成功（异操作者 tx）+自审拒绝+Draw 禁用/纠偏 Scan+Map/裁定升级联动/CLI --skill 直通 均**已验证**；真机发现并 TDD 修复两缺陷（画布未裁剪溢出相邻面板、图层名双击被 Toggle 消费不隔离）。**手动验证待执行**（诚实标注）：中键拖拽平移真机手感（computer-use middle-drag 注入限制，headless 已覆盖逻辑）、Text/Move/Delete/ChangeLayer/InsertBlock 真机手感（headless 全覆盖）、高 DPI/多显示器、大图性能。两验证项目审计链 CLI verify 绿（9/7 条）。
- **计划3（side chat）承接状态注记**：LLM provider profile 模型+无密钥配置存储（3a-T1，CadAgent.Llm 脚手架）已在并行 side 会话交付（分支 feat/cad-agent-llm-config，commit b5f822b），**本分支不含**；计划3 其余（LLM caller/推断 Upsert 守卫、中望 COM 适配器、保真导出+审计包哈希链外部锚、厂商支持矩阵、步级 undo 扩展）仍开放，视图侧接口（IIdentityProvider/会话闸/审计存储）已为其预留注入点。
- **T9 承接遗留（仍开放）**：审计时间线编辑后不 live-rebuild（构造期快照，重开可见新 tx——真机协议第 6 项即按重开验证）；Text 字高 UI 输入未暴露（固定 3.5，VM wrapper 已支持 height）；Move 仅 Line/Text（处理器契约，圆等移动诚实 step-failed）；InsertBlock scale/rotation UI 未暴露（VM 已支持）；Delete 两段式工具栏确认（无模态对话框基础设施）。

**计划4最终审查修复波关闭（2026-11-22 记录；3 Important 全实现 + 打包小项，全套件 597 测试绿）**：

- **①视图编辑边界准入闸（§4.2 硬要求兑现）**：EditViewModel.ApplyCore 在 StepEngine.Run 前镜像 CLI RunPlan——GateDetector.Inspect(session.Summarize(), EmptySupportMatrix)，Red→run-rejected 审计（Source="view"，params{level,unsupportedCount} 镜像 CLI 字段，前后哈希一致）+ T3 指引拒绝执行（无 tx）；每次 ApplyAsync 都检。Yellow 放行+结果警示分支与 CLI 逐字对齐（EmptySupportMatrix 下任何 unknown 均 unsupported，Yellow 当前不可达——支持矩阵点亮后生效）。Red 夹具经 ACadSharp ProxyEntity DxfWriter/DxfReader 往返实证可行。
- **②驳回签认→快照回滚（§4.1⑥/§5.4 明文兑现）**：SignoffViewModel 增可选 RollbackTx 回调（MainWindow 接 EditViewModel.UndoAsync→StepEngine.Rollback，无旁路）；approved=false 且 tx-start SnapshotRef 非空→签认审计+落盘后 await 回滚（rollback 审计由引擎留痕）；回滚失败→ErrorMessage 提示但签认记录不回撤（审计先行）。加性门禁：仅已提交事务可签认（TxGroup.HasCommit；failed/open→"只能签认已提交事务"，零副作用）。TxGroup/AuditEntryVm 加性暴露 SnapshotRef/HasSnapshot。
- **③损坏状态文件优雅降级（T10 承诺兑现，§9 不崩 UI）**：MainWindow 构造三处 try/catch——audit.db 损坏→空时间线+"审计库损坏，时间线不可用"；roles.yaml 损坏→默认 Draw 角色+提示；signoffs.json 损坏→空签认存储+提示（后续 Save 原子写覆盖修复）。提示汇总入 MainWindow.ErrorMessage + 可见 StateNote 文本块；窗口照常打开、图纸照常渲染。注：audit.db 损坏时写审计占位首次 Append 仍会失败（审计先行不变量——编辑被拒并报错，不静默绕过审计）。
- **打包小项**：SetActiveTool 补 _editBusy 硬闸（在途编辑期间工具切换忽略，RaiseEvent 绕过 IsEnabled 亦被挡）；plan4-ui-protocol.md 悬空"附录 A"引用改为内联等价描述。
- **仍延迟的 minors（记录在案，不阻塞）**：issues-refresh 审计 parity（视图 Refresh 不留 check 审计，CLI check 留痕）；view run-regression（视图编辑成功后不做内置规则回归警告，CLI run 有）；_diffBusy 结构性守卫（Diff 加载期间仅 IsEnabled 禁用，未加标志位硬闸）；Issues.RefreshAsync in-flight 并发守卫（按钮禁用为 UI 态）；空图层不显示于图层面板（LayersViewModel 以 renderItems 构造，无实体层不出现）；计划预览（④纠偏计划视图预览依赖计划3 LLM caller）。其余各 Task parked minors 见控制者 ledger。
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

**幅面感知读图切片完成（sheet-aware T1-T7，2026-11-22 记录；全套件 950 测试连绿两次含 13 Zwcad 真机）**：
- **T1 Sheet 登记簿**：图框块引用检测（块名含 图框/图签/BORD 或携带 图号 属性）→ FrameBounds（块几何经插入变换展开，嵌套递归+深度守卫）+ 标题栏属性 + 框内/近邻幅面名文字关联；无图框→单默认幅面 S0（legacy 兼容）；SheetIdForPoint 实体归属（框外=null 远端残元不混入）。真实标本 9 幅面名/日期/版本号实跑钉住。
- **T2 层名归一+块遍历**：BlockWalker 单次遍历（bound xref 嵌套实体世界坐标+后缀句柄路径+归一层名 x$0$WALL→WALL）供全部检测器共用；插入变换数学 consolidation 于 Core.Cad.Affine（View RenderModel/T1 FrameBounds 同源，R1）；底图墙对经块遍历可达（真实底图在 bound xref 内）。
- **T3 属性 NativeObject 种子**：BlockAttributeSeeder 块属性→语义（A=深照型灯→Lighting 等），Provenance.NativeObject 0.95 确定性原生数据优先于图层推断；Upsert 键=handle+provenance 与 HumanConfirmed 共存；R7 陈旧种子每 pass 清理。真实标本 Lighting==1364/Equipment 147 实跑钉住。
- **T4 幅面闸口**：ISemanticContextGate 三参 CheckContext(required, targetHandle, sheetId)（DIM 默认委托两参，fake 不破坏）；标注 SheetId stamp（检测器/推断/种子/人工映射派生全链）；plan params.sheetId 契约（StepEngine 提取→三参校验）——A 幅面确认不得放行 B 幅面操作，跨幅拒绝 semantic-gate-refused 审计含 sheetId；无 sheetId=legacy 全局行为保持。
- **T5 布局视口+幅面 UI**：布局视口幅面登记（SheetId="L"+视口句柄，ViewTarget/ViewHeight 纸面变换 ViewportTransform）；视图幅面选择器+BuildViewport 裁剪（默认首幅面，sanctioned 行为变更 R4）；**布局幅面只读硬闸**（纸面编辑非本期语义，编辑工具硬禁+在途编辑切幅守卫 R8）；空布局/纸边界视口不登记（诚实）。
- **T6 视觉交叉管道**：SheetTiler 幅面字高中位数反算瓦片（1 overview+≤6 tiles）；IRenderSnapshot seam（Data 不引用 View 无环）；VisionCrossChecker 声称仅以本幅面矢量 GT 佐证才种子（LlmInference 0.9，跨幅跑偏拒绝=幻觉拦截）；短值（<3 字符）包含匹配守卫+命中优先级+多幅面已种子 claim 假歧义抑制（R9）；VisionAmbiguous 入 ScanResult 不静默。
- **T7 agent 联动 E2E + 收尾**：SheetAwareAgentE2ETests 双幅面合成夹具 fake LLM 全链——sheetId=S2 跑偏→tx failed+拒绝审计含 sheetId+Escalated 零落图；sheetId=S1→Executed+tx-commit+agent-verify 绿+验链；legacy 无 sheetId 全局放行；semantic scan stdout 加性 sheets 摘要 {sheetId,name,number}（agent 计划幅面取值依据）。InsertBlockTests CS0184 死分支警告清理（反射判定保意图）。
- **验证协议**：docs/verification/sheet-aware-protocol.md（真实图纸复核步骤：9 幅面名/属性计数 1364/底图墙对经块遍历/跨幅拒绝/空布局诚实注记/视觉管道 harness 含 IRenderSnapshot 注入示例+证据归档要求）。门禁日志 outputs/sheet-aware/final-gate1.log / final-gate2.log（950=Core 207+Data 265+Cli 173+View 262+Llm 30+Zwcad 13，0 跳过——真实标本 skip-gated 3 测试本机实跑；前后 tasklist 核查无 ZWCAD 残留）。
- **剩余边界（明示不假装）**：①代理 🔴 路径无真实标本——两份标本（原生 DWG+工作 DXF）准入均 Green 代理实体 0，🔴 拒绝仅合成 ProxyEntity 夹具覆盖；②空布局视口无内容标本——视口真实内容 E2E 缺口（合成 Viewport 夹具钉住登记/裁剪/纸面映射）；③视觉管道真实模型 E2E 留 harness——IRenderSnapshot 实现+VL reader 未接生产 CLI（fake seam 测试为自动化闸口）；④纸面编辑 deferred（布局幅面只读硬闸，不做纸面→模型逆映射）；⑤TwistAngle 与非矩形视口裁剪 deferred（视口数学仅平移+缩放）；⑥图框块内部几何编辑在 Sheets 指纹外（块定义内容变化需 Reload 失效）；⑦signoff.RollbackTx 不经布局只读闸——快照回滚作用于模型 tx 与视图当前幅面无关，与"布局只读"预期轻微张力（回滚是审计事实源操作非纸面编辑，注记在案）；⑧属性表硬编码（AttributeSemantics skill 覆写留后续切片）；⑨瓦片零重叠骑缝文字可能截半（~10% 重叠边距留后续）。

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
