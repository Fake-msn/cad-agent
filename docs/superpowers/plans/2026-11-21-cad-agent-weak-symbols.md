# CAD 智能体终端 · 50786 弱电符号矢量化 实现计划（计划 3g）

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans（Side chat 内联）。

**Goal:** GB/T 50786-2012 表4.1.3-1..6 弱电系统图形符号 **143 个**矢量化入 electrical skill 包（续 3f 强电 164）。

**Architecture:** 复用 standards-src/_symbolgen 注册表模式；前缀按表：com-/fire-/tv-/pa-/sec-/bas-；symbols.yaml 追加（emit 全注册表含强电？——**裁定：弱电独立 symbols-weak.yaml**，避免与强电 yaml 互相覆盖；SkillPackage 加载 symbols/ 目录下多 yaml？核实 SkillSet 符号加载是否扫目录——若仅 symbols.yaml 单文件，则弱电并入同一 symbols.yaml（emit 时合并强电注册表引用列表））。**执行时先核实加载路径再定**。

**Spec:** spec §12 3f deferred 承接；extraction-electrical.md 矢量化补记。

## Global Constraints

- 同 3f：近似重建注记/水印忽略/名称取说明列/形式2 不建双块/读回测试/515 既有绿/feat: 前缀/署名 env/EOF。
- 缺裁切页核实：p34/p39 空表 v1+v2 均 0 行＝占位块，无缺失符号（已核）。
- class：弱电符号默认 Auxiliary；扬声器/广播喇叭→Auxiliary（非 Lighting）；火灾声光警报→Auxiliary。

## 分批（表→页→数）

W1 com p33 #1-17｜W2 fire p34-37 #1-38｜W3 tv p38 #1-21｜W4 pa p40 #1-7 + sec p41-43 #1-37｜W5 bas p44-45 #1-23

## 任务

T1 核实符号加载路径 + W1 com（读 p33 裁切转录 17）→测试→commit
T2 W2 fire（p34-37 四裁切 38）→测试→commit
T3 W3 tv（p38 21）→测试→commit
T4 W4 pa+sec（p40-43 四裁切 44）→测试→commit
T5 W5 bas（p44-45 23）→测试→commit
T6 全套件+spec §12 3g+extraction 补记+记忆+推送+PR（base=50786-symbols）

## 自审

- 符号加载路径未核实＝T1 首步（SkillSet/SymbolMeta 加载代码 grep symbols.yaml）。
- 143 为清单推算（145 编号行含 2 行形式格文本），逐批以实际转录数为准，终测断言总数=实际。