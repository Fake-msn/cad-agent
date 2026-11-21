# CAD 智能体终端 · 读回迭代环 实现计划（计划 3i = 补核心 A）

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans（Side chat 内联）。

**Goal:** 闭合设计核心环"读图→决策→操作→**读回→迭代**"：执行成功后 post-summary+变更 diff 喂回 LLM 验证，满足→Executed；不满足→纠正计划（确认+审计+执行）→再验证；有界（默认 2 轮）。用户裁定 A→B，C 并入 B 基准（3j 待写）。

**Architecture:** AgentOrchestrator 执行成功分支后加验证环；AgentPlanParser.ExtractVerification（{"verdict":"ok"} 或纠正计划；空 steps 计划视为 ok）；RunAsync 加 postSummaryProvider 可选 seam（CLI 传 DrawingSummarizer，Core 默认最小 diff：前后哈希+stepsDone+kinds）；OrchestrationResult 加可选 VerificationRounds；构造器 maxVerificationRounds=2。

**Spec:** spec §3 混合式核心环；§12 3h 后缺口记录（读回环未闭合）。

## Global Constraints

- 验证环每轮：EnsureBudget 前置（耗尽→Executed+agent-verify budget-skipped 审计，执行已提交不谎报失败）；llm-call 逐轮审计（attempt 递增）；agent-verify 审计 verdict/round。
- 纠正计划：kind 白名单+人工确认（拒绝→RejectedByHuman）；执行失败→Escalated（有界，不嵌套重规划）。
- 验证响应不可解析→Escalated+agent-verify unparseable（不信任完成）；轮耗尽→Escalated+rounds-exhausted。
- **契约变更 sanctioned**：Executed 现含验证调用——既有测试 usage/llm-call 计数断言同步更新（一次性执行→环）。
- 530 既有测试经 sanctioned 更新后绿；feat: 前缀；署名 env；EOF。

## 任务

T1 ExtractVerification + 单测（verdict ok/fix/空计划 ok/不可解析抛）→commit
T2 Orchestrator 验证环 + AgentReadbackLoopTests 8 场景 + 既有测试 sanctioned 更新→commit
T3 CLI agent/repl postSummaryProvider + stdout verificationRounds + E2E 更新→commit
T4 spec §12 3i + 记忆 + 推送 + PR（base=symbol-import）；3j（B COM 绘制+C 生成式基准）计划待写记录

## 自审

- ScriptedClient 队列空默认 {"steps":[]} → 空计划视为 ok → 既有 happy 路径仅 usage 断言需更新（15→30）。
- 验证环在初始失败重规划路径**之后**（重规划成功执行后也进验证环）。
- postSummaryProvider 为 null 时 Core 最小 diff 不引用 Data（Core 无厂商/摘要依赖保持）。