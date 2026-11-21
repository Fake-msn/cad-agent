# CAD 智能体终端 · COM 绘制执行 + 生成式真机基准 实现计划（计划 3j = 补核心 B，C 并入）

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans（Side chat 内联）。

**Goal:** 兑现"agent 操作 CAD 绘制"本义：步骤计划在**活 ZWCAD 会话**内逐步执行（COM 绘制操作），文档模型降为审计影子副本；C 生成式绘制基准并入（LLM 产线/文字几何计划→真机落图→读回验证）。

**Architecture:** ICadRuntime 增绘制操作（AddLine/AddText/AddCircle/AddInsert/BlockDef/EntityCount）；ZwcadRuntime dynamic COM ModelSpace 实现（STA 队列既有）；**Data.ComReplayService：计划级 COM 重放**（kind→COM 操作映射，含 layout-lighting 网格/label-luminaires 六边形/insert-symbol 块定义），逐 step 审计 com-replay；CLI run/agent/repl/skill run 加 --live：tx 提交后重放至活会话，重放失败→响亮审计+stderr 警示（影子副本仍为审计事实源，保真读回闸口守分歧）；--live 导出从活会话 SaveAs。

**Spec:** spec §3 厂商适配器一等公民/保真锚；§12 3i 缺口 B/C；用户质疑"只导入矢量图形脱离核心"。

## Global Constraints

- 重放映射与 handler 几何**同源复用**（网格/六边形数学提取共享 helper，不双写漂移）。
- 重放失败不静默：com-replay-failed 审计+stderr；影子副本审计链不变；读回计数闸口守影子/厂商分歧。
- 真机测试 Trait("Category","RequiresZwcad")；离线 fake runtime 单测覆盖映射。
- 546 既有绿；feat: 前缀；署名 env；EOF。

## 任务

T1 ICadRuntime 绘制操作契约 + ZwcadRuntime COM 实现 + 真机冒烟（AddLine/AddText/AddCircle/EntityCount）→commit
T2 几何 helper 提取共享 + ComReplayService（fake runtime 单测：add-line/add-text/insert-block/layout-lighting/label-luminaires/insert-symbol 映射+失败路径）→commit
T3 CLI --live 接线（run/agent/repl/skill run）+ 离线 E2E（fake runtime 重放审计+--live 导出路径）→commit
T4 生成式真机基准（C 并入）：fake LLM 产 add-line/add-text 生成式计划→agent --live→ZWCAD 落图→SaveAs DWG→ACadSharp 读回断言→com-replay 审计+验链；spec §12 3j+记忆+推送+PR（base=readback-loop）

## 自审

- handler _cast DrawingSession_ 不动（影子路径不变）；COM 重放是计划级第二执行通道，审计逐 step 留痕——"操作必经审计管线"对两通道均成立。
- insert-symbol COM 块定义：Blocks.Add+块内 AddLine/AddCircle/AddText（ZWCAD 镜像 AutoCAD API，真机验证；不支持则响亮失败不静默降级）。
- 网格/六边形数学共享：从 LightingHandler/LabelLuminairesHandler 提取 static geometry helper（Data.Handlers.GeometryKit），handler 与 replay 共用。