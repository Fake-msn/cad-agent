# CAD 智能体终端 · 50786 强电符号矢量化 实现计划（计划 3f）

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans（Side chat 内联）。

**Goal:** GB/T 50786-2012 表4.1.2 强电图样常用图形符号 **164 个编号符号**矢量化入 electrical skill 包（DXF+目录），补齐 3e 遗留的 50786 符号几何缺口（本切片＝4.1.2；4.1.3-x 弱电/4.1.4 线型留后续切片）。

**Architecture:** 复用 standards-src/_symbolgen 临时生成器（ACadSharp 3.8.35，gitignored）扩展 50786 模块：注册表 (id,name,en,class,geometry) 单一事实源→生成 elec-NNN.dxf + emit symbols.yaml；证据＝vlm 重转整表裁切（_reparse/gb50786/images/，清单 _table-inventory.txt）；逐页目视转录几何（**忽略斜向水印**；[YS84] 等 OCR 噪声格忽略；名称取说明列）。

**Spec:** spec §12 3e 追加（50786 矢量化 deferred→本切片承接）；extraction-electrical.md（条文底稿）。

## Global Constraints

- 近似重建诚实注记：非精确描摹；复合符号（变压器组等）取主形式要素+描述注记简化；形式2 不单独建块（描述注记）。
- class：灯具/LED/信号灯/应急灯/荧光灯/格栅灯/投光/聚光→Lighting；其余 Auxiliary。
- 名称逐字自 HTML 说明列（中文+英文）；序号=表内编号 1-164；文件名 elec-{id:000}.dxf。
- 513 既有测试持续绿；electrical skill validate 保持 0；feat: 前缀；git 署名环境变量；EOF 换行。
- 推送保留 env 代理（用户裁定：本网络直连推送不可靠，代理运行时用代理）。

## 分批边界（页→编号）

B1 p10-13 #1-27｜B2 p14-17 #28-49｜B3 p18-21 #50-78｜B4 p22-25 #79-111｜B5 p26-29 #112-142｜B6 p30-32 #143-164

## 共享接口（T1 建立，后续批追加）

- Program.cs 50786 模块：`List<(int Id, string Name, string En, string Cls, string Note, Action<CadDocument> Geo)> Reg50786`；helpers 既有 L/C/Tip/Arrow/ArcSeg/Cross + 新增 `Rect(d,x0,y0,x1,y1)`、`T(d,x,y,height,text)`（TextEntity）、`HalfCircle(d,cx,cy,r,up)`（ArcSeg 0-180/180-360）。
- 输出：`examples/skills/electrical/symbols/elec-NNN.dxf` + `symbols.yaml`（emit 自注册表：name/file/class/description=En+Note）。
- 测试 `tests/CadAgent.Cli.Tests/ElectricalSymbolsTests.cs`：目录文件数==注册表数（yaml 行数解析）+ 每文件 DxfReader 读回 entities>0 + electrical skill validate exit 0。批内先断言当前批次数，T7 改 164。
- 几何毫米级示意比例（圆 r1.5-2.5、线长 6-10 惯例，同 244 符号）。

**转录示例（B1 #1-4，p10 裁切已核对）**：
1 导线组：横线(-5,0)-(5,0)+三斜 tick（形式1）；形式2=横线+斜杠+数字3（注记）
2 软连接：横线+中部两半圆凸波（ArcSeg 上凸×2）
3 端子：圆 r1.5
4 端子板：Rect(-4,-1.5,4,1.5)+5 竖隔线

---

### Task 1: 生成器 50786 模块 + B1（#1-27）
读 p10/p11/p12/p13 裁切→转录 27 符号→生成+emit yaml→读回测试（27）→Commit `feat: electrical symbol vectorization batch 1 (50786 table 4.1.2 #1-27)`

### Task 2: B2（#28-49）
读 p14-p17→转录（变压器/互感器族：绕组半圆+铁心线；46/47 描述缺失按图形+注记命名）→测试（49）→Commit batch 2

### Task 3: B3（#50-78）
读 p18-p21→转录（变换器方框+斜线/触点族：斜动合线+半圆延时弧等）→测试（78）→Commit batch 3

### Task 4: B4（#79-111）
读 p22-p25→转录（断路器/熔断器/继电器线圈/表计/信号/电站/防雷/电缆线路族）→测试（111）→Commit batch 4

### Task 5: B5（#112-142）
读 p26-p29→转录（导线标识/配线方向/接线盒/启动器方框文字/插座/开关族）→测试（142）→Commit batch 5

### Task 6: B6（#143-164）
读 p30-p32→转录（按钮/灯/应急标志灯/荧光灯/格栅灯/投光/聚光/风扇）→测试（164）→Commit batch 6

### Task 7: 收尾
全套件→spec §12 3f 记录（164 符号/近似边界/弱电 deferred）→extraction-electrical.md 补矢量化注记→记忆→推送+PR 菜单（堆叠 base=feat/cad-agent-244-interior）

## 自审记录

- p23 无表题曾漏滤→清单已含（85-93）；46/47 说明列空/注4→按图形转录+注记"说明列缺失，名称据图形推定"（诚实）。
- 水印斜线贯穿符号格→转录只取符号本体笔画。
- 形式2 差异大的（#1/#22/#37 等）描述注记，不建双块（YAGNI）。
- emit yaml 覆盖手写：electrical/symbols/ 目录新建（原包无 symbols）。