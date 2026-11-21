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
