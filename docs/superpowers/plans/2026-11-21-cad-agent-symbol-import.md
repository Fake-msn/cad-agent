# CAD 智能体终端 · 跨文档符号导入 实现计划（计划 3h）

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans（Side chat 内联）。

**Goal:** skill 符号 DXF 真正落图：insert-symbol 第九处理器（按名解析 skill 包符号→读源 DXF→建块/复用块→Insert），清除 spec §12 长期 deferred 项。

**Architecture:** Data ImportSymbolHandler(IReadOnlyList<SkillPackage>)——名解析 OrdinalIgnoreCase 首包命中；源 DXF 经 DxfReader 读取；**支持类型白名单 Line/Circle/Arc/TextEntity 重建克隆**（不赌 Clone 的外来表引用；库内容即此四类；其余类型响亮失败）；块名=SymbolMeta.Name，已存在则复用（不重复建块）；Insert 带 scale/rotation/layer。CLI：Engine 加 extraHandlers 可选参；run/skill run/agent/repl 带 --skill 时注入（agent/repl 新增 --skill 选项，加性）；KindDocs 加行。interior-244 加 steps/place-luminaire 模板。

**Spec:** spec §12 跨文档符号导入 deferred 承接；3f/3g 符号库 324+17 就绪。

## Global Constraints

- 几何导入不声明 ISemanticAware（语义确认仍走 scan/map；导入本身不 bypass 闸口管线——经 StepEngine 审计/快照）。
- 不支持类型/缺文件/未知符号名→StepResult 失败（响亮，不静默）；块复用幂等。
- 515 既有测试绿；feat: 前缀；署名 env；EOF。
- 安全：符号名仅经 skill 包 symbols.yaml 解析（不接受任意路径参数）——边界受控。

## 任务

T1 处理器+单测（electrical 包真符号：端子 elec-003；块复用；scale/rotation/layer；未知符号/缺文件/不支持类型失败）→commit
T2 CLI 接线（Engine extraHandlers；run/skill run/agent/repl --skill 注入；KindDocs）+CLI 测试（run 计划导入审计；agent fake 计划带/不带 --skill）→commit
T3 interior-244 place-luminaire 模板 + E2E（skill run 导入 3 筒灯→签认→验链）+ spec §12 deferred 清除 + 记忆 + 推送 + PR（base=weak-symbols）

## 自审

- 重建克隆 vs Entity.Clone：Clone 存在但外来 Layer/LineType 表引用风险未验证→白名单重建（库内容四类全覆盖），诚实注记边界。
- 块名中文（SymbolMeta.Name）DXF 块名 unicode——ACadSharp 写读往返既有中文文本先例，块名待 T1 往返测试验证。
- agent/repl --skill 加性：无 --skill 时 insert-symbol 未注册→计划响亮失败（既有未注册 kind 路径）。