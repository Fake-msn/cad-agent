# 项2 Plan-1：基础层（F）+ 纸面视图逆映射编辑模型（A）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 布局幅面从只读变为「视口内可编辑模型」：纸面空间完整渲染（含标题栏属性与视口框）、纸→模型逆映射、编辑经 StepEngine 全审计；先修复 `ComputeHash` 不覆盖纸面/块定义/属性的承重缺陷。

**Architecture:** 双空间统一渲染（纸面层+视口裁剪模型层+锁定视口框）→ 纯函数路由判定空间 → 模型空间编辑用 `ViewportTransform.PaperToModel` 逆映射后走现有 `EditViewModel→StepEngine` 管线。纸面空间实体编辑与块内编辑不在本计划（Plan-2/Plan-3）。

**Tech Stack:** C#/.NET 8；ACadSharp 3.8.35（只读对象图 + 可写集合）；Avalonia 11.3.2 headless（真实 Skia）；xUnit。

**Spec:** `docs/superpowers/specs/2026-11-26-item2-paper-subentity-design.md`（§2.1-2.4 = F；§3 的「视口内模型编辑」= A 本计划范围；§4.1-4.2 路由）

## Global Constraints

- 分支：`feat/cad-agent-view-deepread`（堆叠 PR#34，用户既定偏好）；不新增分支、不合并。
- Git 身份仅环境变量（逐命令）：`GIT_AUTHOR_NAME=Fake-msn GIT_AUTHOR_EMAIL=Fake-msn@users.noreply.github.com GIT_COMMITTER_NAME=Fake-msn GIT_COMMITTER_EMAIL=Fake-msn@users.noreply.github.com`；**禁止 `git config`**。
- 全部编辑经 StepEngine（tx/审计/快照）；本计划**不新增处理器**，仅现有 `add-line/add-text/move-entity/delete-entity/change-layer` 的坐标来源改变（仍传模型坐标）。
- `ComputeHash` 扩展后哈希值与旧版不同——不得写死哈希字面量断言；只断言「变化/稳定/确定性」。
- 属性渲染新增会改变含 ATTRIB 图纸的渲染项数——受影响断言按新事实修正并在 PR/注记披露。
- Windows 中文输出：dotnet 输出重定向 UTF-8 文件再读，不直接打印中文到 GBK 终端。
- 真实标本只读；测试夹具全部落 `%TEMP%` 并清理；SQLite 测试清理前 `SqliteConnection.ClearAllPools()`。
- 不删/不改用户 `TRUSTEDPATHS`、注册表、vendor 进程；本计划不涉 COM/真机。
- 门禁：每任务目标测试先红后绿；合并前全 sln（基线 1796 通过/1 skip；View 已知 audit.db teardown flake 隔离复跑留证）。

## 文件结构（本计划）

- Modify `src/CadAgent.Core/SheetModel.cs`（+`LayoutViewportContext`、`PaperToModel`）
- Modify `src/CadAgent.View/Rendering/RenderModel.cs`（RenderSpace/新字段/属性渲染/纸面构建/视口框/合成）
- Add `src/CadAgent.View/Editing/EditRouting.cs`（纯路由与上下文工厂）
- Modify `src/CadAgent.Data/DrawingSession.cs`（ComputeHash 全空间投影）
- Modify `src/CadAgent.View/MainWindow.cs`（布局上下文、工具矩阵、坐标换算、状态/守卫）
- Modify `tests/CadAgent.Core.Tests/SheetModel*`（若有）或新增 `LayoutViewportContextTests`
- Add `tests/CadAgent.View.Tests/EditRoutingTests.cs`
- Modify `tests/CadAgent.View.Tests/ViewportRenderTests.cs`、`SheetSelectorHeadlessTests.cs`
- Modify `tests/CadAgent.Data.Tests/DrawingSessionTests.cs`（或新增 HashProjectionTests）
- Modify `docs/superpowers/specs/2026-11-20-cad-agent-terminal-design.md` §12（闭环注记）

---

### Task 1: RenderItem 空间/来源字段（F）

**Files:**
- Modify: `src/CadAgent.View/Rendering/RenderModel.cs`
- Test: `tests/CadAgent.View.Tests/RenderModelTests.cs`

**Interfaces:**
- Produces: `public enum RenderSpace { ModelSpace, PaperSpace }`；`RenderItem` 追加可选参数（尾部）：`Space`、`SourceHandle`、`BlockName`、`ParentInsertChain`、`AttributeTag`、`Locked`（默认 `ModelSpace/null/null/null/null/false`）。

- [ ] **Step 1: 写失败测试**（RenderModelTests 追加）

```csharp
[Fact]
public void RenderItem_NewOptionalFields_DefaultToModelSpaceIdentity()
{
    var item = new RenderItem(RenderKind.Line, new[] { (0.0, 0.0), (1.0, 0.0) }, "0", 0xFFFFFF, null, 0, 0, "1A");
    Assert.Equal(RenderSpace.ModelSpace, item.Space);
    Assert.Null(item.SourceHandle);
    Assert.Null(item.BlockName);
    Assert.Null(item.ParentInsertChain);
    Assert.Null(item.AttributeTag);
    Assert.False(item.Locked);
}
```

- [ ] **Step 2: 跑测试确认编译失败**

Run: `dotnet test tests/CadAgent.View.Tests/CadAgent.View.Tests.csproj --filter "FullyQualifiedName~RenderItem_NewOptionalFields" --nologo`
Expected: 编译错误（`RenderSpace`/属性不存在）。

- [ ] **Step 3: 实现**

在 `RenderModel.cs` `RenderItem` 前加：

```csharp
/// <summary>渲染图元所属空间：布局幅面下纸面实体=PaperSpace，视口内模型=ModelSpace。</summary>
public enum RenderSpace { ModelSpace, PaperSpace }
```

`RenderItem` 参数表末尾追加：

```csharp
    bool Locked = false,
    RenderSpace Space = RenderSpace.ModelSpace,
    string? SourceHandle = null,
    string? BlockName = null,
    IReadOnlyList<string>? ParentInsertChain = null,
    string? AttributeTag = null);
```

（`Locked` 已在位——若已存在则保留，其余追加；位置参数兼容。）

- [ ] **Step 4: 绿并提交**

Run: `dotnet test tests/CadAgent.View.Tests/CadAgent.View.Tests.csproj --filter "FullyQualifiedName~RenderItem_NewOptionalFields" --nologo`
Expected: PASS。

```bash
git add src/CadAgent.View/Rendering/RenderModel.cs tests/CadAgent.View.Tests/RenderModelTests.cs
git commit -m "feat(view): RenderItem 增空间/来源字段（纸面/块子图元定位基础）"
```

---

### Task 2: Insert 属性渲染 + 来源戳记（F）

**Files:**
- Modify: `src/CadAgent.View/Rendering/RenderModel.cs`（`Append` 及其调用点）
- Test: `tests/CadAgent.View.Tests/RenderModelTests.cs`

**Interfaces:**
- Consumes: Task 1 字段。
- Produces: Insert 展开后属性文本（`AttributeEntity.Value/InsertPoint/Height/Rotation`）入图：`Handle="{insert}:attr:{tag}"`、`SourceHandle=ATTRIB 句柄`、`AttributeTag=tag`、`Space` 继承；所有由 `Append` 产生的图元 `SourceHandle ??= 自身 Handle`；块子图元带 `BlockName`（最内层）+ `ParentInsertChain`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void Build_InsertWithAttribute_RendersAttributeText_WithSourceHandleAndTag()
{
    string path = DxfFixture.CreateTempDxf(doc =>
    {
        var block = new BlockRecord("图框");
        block.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0) });
        doc.BlockRecords.Add(block);
        var ins = new Insert(block) { InsertPoint = new XYZ(10, 20, 0) };
        ins.Attributes.Add(new AttributeEntity
        {
            Tag = "图号", Value = "Z-E01", InsertPoint = new XYZ(5, 5, 0), Height = 2.5
        });
        doc.Entities.Add(ins);
    });
    try
    {
        var session = CadAgent.Data.DrawingSession.Load(path);
        var items = RenderModelBuilder.Build(session);
        var attr = Assert.Single(items, i => i.AttributeTag == "图号");
        Assert.Equal(RenderKind.Text, attr.Kind);
        Assert.Equal("Z-E01", attr.Text);
        Assert.EndsWith(":attr:图号", attr.Handle);
        Assert.NotNull(attr.SourceHandle);
    }
    finally { File.Delete(path); }
}
```

> 夹具说明：`RenderModelBuilder.Build` 需要 `DrawingSession`；View.Tests 用 `DxfFixture.CreateTempDxf` + `DrawingSession.Load`（既有测试同风格）。所需 using：`ACadSharp.Entities/Tables/Objects`、`CSMath`、`CadAgent.Data`、`CadAgent.Data.Tests`。

**Step 1b：已放置坐标 + 对齐锚点的数值断言**（修复轮要求；旧实现必红）

```csharp
[Fact]
public void Build_InsertAttribute_UsesPlacedCoordinates_NotBlockLocal()
{
    string path = DxfFixture.CreateTempDxf(doc =>
    {
        var block = new BlockRecord("B") { };
        block.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) });
        doc.BlockRecords.Add(block);
        var ins = new Insert(block)
        {
            InsertPoint = new XYZ(100, 200, 0), Rotation = Math.PI / 2,
            XScale = 1, YScale = 1
        };
        var attr = new AttributeEntity { Tag = "T", Value = "V" };
        attr.InsertPoint = new XYZ(150, 250, 0);
        attr.AlignmentPoint = new XYZ(150, 250, 0);
        ins.Attributes.Add(attr);
        doc.Entities.Add(ins);
        doc.Entities.Add(new TextEntity { InsertPoint = new XYZ(0, 0, 0), Value = "锚点", Height = 1 });
    });
    try
    {
        var items = RenderModelBuilder.Build(CadAgent.Data.DrawingSession.Load(path));
        var attrItem = Assert.Single(items, i => i.AttributeTag == "T");
        // ATTRIB 是"已放置"坐标：顶层 Insert 变换不得二次施加
        Assert.Equal(150, attrItem.Points[0].X, 6);
        Assert.Equal(250, attrItem.Points[0].Y, 6);
        Assert.NotNull(attrItem.SourceHandle);
        Assert.EndsWith(":attr:T", attrItem.Handle);
    }
    finally { File.Delete(path); }
}
```

```csharp
[Fact]
public void Build_AttributeNonDefaultAlignment_UsesAlignmentPoint()
{
    string path = DxfFixture.CreateTempDxf(doc =>
    {
        var block = new BlockRecord("B2");
        doc.BlockRecords.Add(block);
        var ins = new Insert(block) { InsertPoint = new XYZ(0, 0, 0) };
        var attr = new AttributeEntity
        {
            Tag = "C", Value = "居中",
            HorizontalAlignment = TextHorizontalAlignment.Center,
            VerticalAlignment = TextVerticalAlignmentType.Middle
        };
        attr.InsertPoint = new XYZ(0, 0, 0);
        attr.AlignmentPoint = new XYZ(5, 5, 0);
        ins.Attributes.Add(attr);
        doc.Entities.Add(ins);
    });
    try
    {
        var items = RenderModelBuilder.Build(CadAgent.Data.DrawingSession.Load(path));
        var attrItem = Assert.Single(items, i => i.AttributeTag == "C");
        Assert.Equal(5, attrItem.Points[0].X, 6);
        Assert.Equal(5, attrItem.Points[0].Y, 6);
    }
    finally { File.Delete(path); }
}
```

- [ ] **Step 2: 红**

Run: `dotnet test tests/CadAgent.View.Tests/CadAgent.View.Tests.csproj --filter "FullyQualifiedName~Build_InsertWithAttribute_RendersAttributeText" --nologo`
Expected: FAIL（找不到 `AttributeTag=="图号"`）。

- [ ] **Step 3: 实现**

`RenderModel.cs` 内新增私有上下文：

```csharp
private readonly record struct EmitContext(
    RenderSpace Space, string? SourceHandle, string? BlockName,
    IReadOnlyList<string>? InsertChain, string? AttributeTag);
```

`Append` 签名末尾加 `EmitContext? ctx = null`；函数体开头：

```csharp
EmitContext c = ctx ?? new EmitContext(RenderSpace.ModelSpace, null, null, null, null);
int startIndex = items.Count;
```

`switch` 之后（`Append` 末尾、`}` 前）统一戳记本次新产图元（子递归已自带戳记，不覆盖）：

```csharp
for (int i = startIndex; i < items.Count; i++)
{
    RenderItem it = items[i];
    if (it.SourceHandle is not null) { items[i] = it with { Space = c.Space }; continue; }
    items[i] = it with
    {
        Space = c.Space,
        SourceHandle = c.SourceHandle ?? it.Handle,
        BlockName = it.BlockName ?? c.BlockName,
        ParentInsertChain = it.ParentInsertChain ?? c.InsertChain,
        AttributeTag = it.AttributeTag ?? c.AttributeTag,
    };
}
```

Insert 分支改为：

```csharp
string insertHandle = handle;
var chain = (c.InsertChain ?? Array.Empty<string>())
    .Append(insert.Handle.ToString(CultureInfo.InvariantCulture)).ToArray();
foreach (Entity child in insert.Block.Entities)
{
    Append(child, items, ref skipped, composed, rot, hs, color, depth + 1,
        handle + ":" + childIndex++.ToString(CultureInfo.InvariantCulture),
        new EmitContext(c.Space, child.Handle.ToString(CultureInfo.InvariantCulture),
                        insert.Block.Name, chain, null));
}
foreach (AttributeEntity attr in insert.Attributes)
{
    // DXF ATTRIB 坐标是"已放置"坐标（INSERT 的 OCS 最终空间），不是块局部坐标——
    // 只承接父仿射 t，绝不能套 insert 自身的 composed 变换（全实例偏移上千单位的实证缺陷）。
    EmitAttribute(attr, items, t, rotationDeg, heightScale, color, depth + 1,
        insertHandle + ":attr:" + attr.Tag, c.Space);
}
```

专用属性发射（对齐锚点 + 已放置坐标；不改 plain TextEntity 既有口径）：

```csharp
private static void EmitAttribute(
    AttributeEntity attr, List<RenderItem> items, Affine t, double rotationDeg,
    double heightScale, uint? byBlockColor, int depth, string handle, RenderSpace space)
{
    // 默认对齐（左+基线）用插入点；其余对齐的语义锚点是 AlignmentPoint（真图标题栏多为居中）。
    bool defaultAlignment = attr.HorizontalAlignment == TextHorizontalAlignment.Left
        && attr.VerticalAlignment == TextVerticalAlignmentType.Baseline;
    XYZ anchor = defaultAlignment ? attr.InsertPoint : attr.AlignmentPoint;
    var (ocs, _) = OcsMapper(attr.Normal);
    (double ox, double oy) = ocs.Apply(anchor.X, anchor.Y);
    items.Add(new RenderItem(RenderKind.Text,
        new[] { t.Apply(ox, oy) },
        attr.Layer?.Name ?? "0", ResolveColor(attr, byBlockColor), attr.Value,
        Math.Abs(attr.Height) * heightScale,
        NormDeg(OcsRotation(ToDeg(attr.Rotation), ocs) + rotationDeg),
        handle, DashPattern: ResolveDash(attr),
        Space: space,
        SourceHandle: attr.Handle.ToString(CultureInfo.InvariantCulture),
        AttributeTag: attr.Tag));
}
```

- depth 参数仅用于签名对齐（属性不可再展开）；如实现中不需要则去掉。
- 戳记循环改为「已戳记（SourceHandle 非空）项只 `continue`」：子递归/属性自带的 Space 不被祖先覆盖（消除跨空间未来陷阱）。

Dimension 分支子递归同理传 `new EmitContext(c.Space, child.Handle.ToString(...), dim.Block.Name, chain, null)`（`chain` 同样追加 dim 句柄）。

`Build`/`BuildViewport` 顶层调用不传 ctx（默认模型空间）。

- [ ] **Step 4: 绿 + 全 RenderModelTests（含必要断言修正）**

Run: `dotnet test tests/CadAgent.View.Tests/CadAgent.View.Tests.csproj --filter "FullyQualifiedName~RenderModelTests" --nologo`
Expected: PASS。属性文本入图是**新事实**：若既有计数/文本断言失败，在本任务内按新事实修正（同一 commit 内完成，禁止留着红提交）。

- [ ] **Step 5: 提交**

```bash
git add src/CadAgent.View/Rendering/RenderModel.cs tests/CadAgent.View.Tests/RenderModelTests.cs
git commit -m "feat(view): Insert 属性文本渲染与图元来源戳记（纸面/块定位基础）"
```

---

### Task 3: 纸面空间渲染 + 视口框 + 布局合成（F）

**Files:**
- Modify: `src/CadAgent.View/Rendering/RenderModel.cs`
- Test: `tests/CadAgent.View.Tests/ViewportRenderTests.cs`

**Interfaces:**
- Produces:
  - `BuildPaperSpace(CadDocument doc, string layoutName) → IReadOnlyList<RenderItem>`（排除 Viewport，Space=PaperSpace）
  - `BuildViewportFrame(Viewport vp) → IReadOnlyList<RenderItem>`（4 线段，Space=PaperSpace，Locked=true，SourceHandle=视口句柄）
  - `ComposeLayout(doc, layoutName, Viewport? vp) → paper.Concat(model).Concat(frame)`

- [ ] **Step 1: 写失败测试**（ViewportRenderTests）

```csharp
[Fact]
public void ComposeLayout_IncludesPaperEntitiesAttributesAndLockedViewportFrame()
{
    string path = DxfFixture.CreateTempDxf(doc =>
    {
        doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1000, 0, 0) });
        var title = new BlockRecord("图框");
        title.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0) });
        // Ruling R8：ATTDEF 定义不渲染（模板），实例值仅经 Insert.Attributes
        title.Entities.Add(new AttributeDefinition { Tag = "DEF", Value = "DEFVAL" });
        doc.BlockRecords.Add(title);
        var layout = new ACadSharp.Objects.Layout("布局1");
        doc.Layouts.Add(layout);
        layout.AddViewport(new Viewport
        {
            Center = new XYZ(500, 350, 0), Width = 1000, Height = 700,
            ViewTarget = new XYZ(0, 0, 0), ViewHeight = 200
        });
        // AddViewport 之后 AssociatedBlock 才可用（既有夹具口径）
        var ins = new Insert(title) { InsertPoint = new XYZ(10, 10, 0) };
        ins.Attributes.Add(new AttributeEntity { Tag = "图号", Value = "Z-E01", InsertPoint = new XYZ(20, 20, 0), Height = 3 });
        layout.AssociatedBlock.Entities.Add(ins);
    });
    var session = CadAgent.Data.DrawingSession.Load(path);
    try
    {
        Viewport vp = session.Doc.Layouts.First(l => l.Name == "布局1").AssociatedBlock.Entities.OfType<Viewport>().First();
        var items = RenderModelBuilder.ComposeLayout(session.Doc, "布局1", vp);
        Assert.Contains(items, i => i.Space == RenderSpace.PaperSpace && i.AttributeTag == "图号" && i.Text == "Z-E01");
        var frame = items.Where(i => i.Locked).ToList();
        Assert.Equal(4, frame.Count);
        Assert.All(frame, f => Assert.Equal(RenderSpace.PaperSpace, f.Space));
        Assert.Contains(items, i => i.Space == RenderSpace.ModelSpace); // 视口内模型线
        Assert.DoesNotContain(items, i => i.Text == "DEFVAL");           // ATTDEF 定义不渲染
        // 顺序：纸面 → 模型 → 框（框在列表末尾）
        Assert.True(items[^1].Locked);
    }
    finally { File.Delete(path); }
}
```

- [ ] **Step 2: 红**（`ComposeLayout` 不存在）

Run: `dotnet test tests/CadAgent.View.Tests/CadAgent.View.Tests.csproj --filter "FullyQualifiedName~ComposeLayout_IncludesPaper" --nologo`

- [ ] **Step 3: 实现**

```csharp
public static IReadOnlyList<RenderItem> BuildPaperSpace(CadDocument doc, string layoutName)
{
    var layout = doc.Layouts?.FirstOrDefault(l => string.Equals(l.Name, layoutName, StringComparison.Ordinal));
    if (layout?.AssociatedBlock is null) return Array.Empty<RenderItem>();
    var items = new List<RenderItem>();
    int skipped = 0;
    var ctx = new EmitContext(RenderSpace.PaperSpace, null, null, null, null);
    foreach (Entity e in layout.AssociatedBlock.Entities)
        if (e is not Viewport)
            Append(e, items, ref skipped, Affine.Identity, 0, 1, null, 0, ctx: ctx);
    return items;
}

public static IReadOnlyList<RenderItem> BuildViewportFrame(Viewport vp)
{
    double minX = vp.Center.X - vp.Width / 2, maxX = vp.Center.X + vp.Width / 2;
    double minY = vp.Center.Y - vp.Height / 2, maxY = vp.Center.Y + vp.Height / 2;
    string h = vp.Handle.ToString(CultureInfo.InvariantCulture);
    RenderItem Seg(double x1, double y1, double x2, double y2) => new(
        RenderKind.Line, new[] { (x1, y1), (x2, y2) }, "0", 0x808080, null, 0, 0, h,
        Space: RenderSpace.PaperSpace, SourceHandle: h, Locked: true);
    return new[] { Seg(minX, minY, maxX, minY), Seg(maxX, minY, maxX, maxY),
                   Seg(maxX, maxY, minX, maxY), Seg(minX, maxY, minX, minY) };
}

public static IReadOnlyList<RenderItem> ComposeLayout(CadDocument doc, string layoutName, Viewport? vp)
{
    var list = new List<RenderItem>();
    list.AddRange(BuildPaperSpace(doc, layoutName));
    if (vp is not null) list.AddRange(BuildViewport(doc, vp));
    if (vp is not null) list.AddRange(BuildViewportFrame(vp));
    return list;
}
```

同时（Ruling R8）：块展开排除 `AttributeDefinition`（ATTDEF 是模板定义，不是可见图元；实例值仅经 `Insert.Attributes` 渲染），避免真实图框出现「定义默认值 + 实例值」重叠。实现：`Append` 开头 `if (e is AttributeDefinition) break;`（不计数 skipped——定义不是被跳过的实体，语义上不属于渲染对象）。测试在 ComposeLayout 用例的标题块内加入 `new AttributeDefinition { Tag="DEF", Value="DEFVAL" }`，断言 `Assert.DoesNotContain(items, i => i.Text == "DEFVAL")` 且实例属性文本仍在。

- [ ] **Step 4: 绿 + 提交**

Run: `dotnet test tests/CadAgent.View.Tests/CadAgent.View.Tests.csproj --filter "FullyQualifiedName~ViewportRenderTests" --nologo`
Expected: PASS（旧用例保持）。

```bash
git add src/CadAgent.View/Rendering/RenderModel.cs tests/CadAgent.View.Tests/ViewportRenderTests.cs
git commit -m "feat(view): 纸面空间渲染+锁定视口框+布局合成"
```

---

### Task 4: 纸→模型逆映射与视口上下文（F）

**Files:**
- Modify: `src/CadAgent.Core/SheetModel.cs`
- Add/Test: `tests/CadAgent.Core.Tests/LayoutViewportContextTests.cs`

**Interfaces:**
- Produces:
  - `ViewportTransform.PaperToModel(paper, viewTarget, viewHeight, paperCenter, paperHeight)`
  - `public sealed record LayoutViewportContext(string ViewportHandle, (double X,double Y) PaperCenter, double PaperWidth, double PaperHeight, double ViewHeight, (double X,double Y) ViewTarget, double TwistRadians)`，计算属性 `Scale`、`CanMapModel`、`PaperRect`。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void PaperToModel_RoundTrips_ModelToPaper()
{
    var model = (123.4, -56.7);
    var target = (100.0, 100.0);
    var center = (500.0, 350.0);
    var paper = ViewportTransform.ModelToPaper(model, target, 200, center, 700);
    var back = ViewportTransform.PaperToModel(paper, target, 200, center, 700);
    Assert.Equal(model.Item1, back.X, 9);
    Assert.Equal(model.Item2, back.Y, 9);
}

[Fact]
public void Context_TwistRefusesModelMapping_AndPaperRectMatchesViewport()
{
    var ctx = new LayoutViewportContext("5F", (500, 350), 1000, 700, 200, (0, 0), 0.3);
    Assert.False(ctx.CanMapModel);
    Assert.Equal(3.5, ctx.Scale, 9);
    Assert.Equal((0.0, 0.0, 1000.0, 700.0), ctx.PaperRect);
    Assert.True(new LayoutViewportContext("5F", (500, 350), 1000, 700, 200, (0, 0), 0).CanMapModel);
}
```

- [ ] **Step 2: 红** → 编译失败。

- [ ] **Step 3: 实现**

```csharp
/// <summary>纸/视高缩放；视高不足 1e-12 → 1（退化快路径）。paperHeight≤0 时返回 0，
/// 调用方须先经 LayoutViewportContext.IsDegenerate 拒绝（"正逆两向仍互逆"仅在不退化时成立）。</summary>
public static double ScaleOf(double paperHeight, double viewHeight) =>
    viewHeight > 1e-12 ? paperHeight / viewHeight : 1;

public static (double X, double Y) PaperToModel(
    (double X, double Y) paper, (double X, double Y) viewTarget, double viewHeight,
    (double X, double Y) paperCenter, double paperHeight)
{
    double s = ScaleOf(paperHeight, viewHeight);
    return (viewTarget.X + (paper.X - paperCenter.X) / s,
            viewTarget.Y + (paper.Y - paperCenter.Y) / s);
}

public sealed record LayoutViewportContext(
    string ViewportHandle,
    (double X, double Y) PaperCenter,
    double PaperWidth,
    double PaperHeight,
    double ViewHeight,
    (double X, double Y) ViewTarget,
    double TwistRadians)
{
    public const double MinViewHeight = 1e-12;

    /// <summary>退化视口（视高/纸宽/纸高非正）：上下文不可用于模型映射，装配方（View ContextFrom）须拒绝。</summary>
    public bool IsDegenerate =>
        !(ViewHeight > MinViewHeight) || !(PaperWidth > 0) || !(PaperHeight > 0);
    public double Scale => IsDegenerate ? 1 : ViewportTransform.ScaleOf(PaperHeight, ViewHeight);
    /// <summary>模型映射可用：非退化且 |Twist| ≤ 1e-9。非矩形裁剪 ACadSharp 3.8.35 无法识别，不在此判定。</summary>
    public bool CanMapModel => !IsDegenerate && Math.Abs(TwistRadians) <= 1e-9;
    public (double MinX, double MinY, double MaxX, double MaxY) PaperRect => (
        PaperCenter.X - PaperWidth / 2, PaperCenter.Y - PaperHeight / 2,
        PaperCenter.X + PaperWidth / 2, PaperCenter.Y + PaperHeight / 2);

    /// <summary>退化视口不做映射（返回 ViewTarget，有限值）；与 Scale=1 诊断口径自洽。</summary>
    public (double X, double Y) PaperToModel((double X, double Y) paper) =>
        IsDegenerate
            ? ViewTarget
            : ViewportTransform.PaperToModel(paper, ViewTarget, ViewHeight, PaperCenter, PaperHeight);
}
```

补充测试（修复轮要求）：方向金断言 + 退化 + twist 边界/负值：

```csharp
[Fact]
public void PaperToModel_GoldenDirection_NonCenterPoint()
{
    // s=3.5；纸面中心右移 350 → 模型右移 100
    var p = ViewportTransform.PaperToModel((850, 350), (0, 0), 200, (500, 350), 700);
    Assert.Equal(100, p.X, 9);
    Assert.Equal(0, p.Y, 9);
}

[Fact]
public void Context_DegenerateViewHeight_IsObservable_ScaleOne_MapsToTarget()
{
    var ctx = new LayoutViewportContext("1", (500, 350), 1000, 700, 0, (7, 8), 0);
    Assert.True(ctx.IsDegenerate);
    Assert.False(ctx.CanMapModel);
    Assert.Equal(1, ctx.Scale);
    Assert.Equal((7.0, 8.0), ctx.PaperToModel((500, 350)));
}

[Theory]
[InlineData(1e-9, true)]
[InlineData(-1e-9, true)]
[InlineData(1.0000001e-9, false)]
[InlineData(-1.0000001e-9, false)]
public void Context_TwistBoundary(double twist, bool canMap)
{
    var ctx = new LayoutViewportContext("1", (500, 350), 1000, 700, 200, (0, 0), twist);
    Assert.Equal(canMap, ctx.CanMapModel);
}

[Fact]
public void Context_PaperDimDegenerate_MapsToTargetFinite_ConsistentWithScale()
{
    var ctx = new LayoutViewportContext("1", (500, 350), 1000, 0, 200, (7, 8), 0);
    Assert.True(ctx.IsDegenerate);
    Assert.Equal(1, ctx.Scale);
    Assert.Equal((7.0, 8.0), ctx.PaperToModel((500, 350))); // 退化不产 Infinity/NaN
}

[Fact]
public void Context_NaNViewHeight_IsDegenerate_NaNTwistRefused()
{
    var ctx = new LayoutViewportContext("1", (500, 350), 1000, 700, double.NaN, (0, 0), 0);
    Assert.True(ctx.IsDegenerate);
    Assert.False(ctx.CanMapModel);
    Assert.False(new LayoutViewportContext("1", (500, 350), 1000, 700, 200, (0, 0), double.NaN).CanMapModel);
}

[Theory]
[InlineData(1e-12, true)]
[InlineData(1.0000001e-12, false)]
public void ScaleOf_Boundary(double viewHeight, bool isOne)
{
    Assert.Equal(isOne ? 1 : 700 / viewHeight, ViewportTransform.ScaleOf(700, viewHeight), 6);
}
```

- [ ] **Step 4: 绿 + 提交**

Run: `dotnet test tests/CadAgent.Core.Tests/CadAgent.Core.Tests.csproj --filter "FullyQualifiedName~LayoutViewportContextTests" --nologo`

```bash
git add src/CadAgent.Core/SheetModel.cs tests/CadAgent.Core.Tests/LayoutViewportContextTests.cs
git commit -m "feat(core): 纸→模型逆映射与布局视口上下文"
```

---

### Task 5: ComputeHash 全空间投影（F，承重）

**Files:**
- Modify: `src/CadAgent.Data/DrawingSession.cs`
- Test: `tests/CadAgent.Data.Tests/DrawingSessionHashTests.cs`（新增）

**Interfaces:**
- Produces: `ComputeHash()` 覆盖模型/纸面/块定义/属性；无公开签名变化。

- [ ] **Step 1: 写失败测试**

```csharp
private static DrawingSession NewSession(Action<CadDocument> populate)
{
    var doc = new CadDocument();
    populate(doc);
    return new DrawingSession(Path.Combine(Path.GetTempPath(), "hash-" + Guid.NewGuid().ToString("N") + ".dxf"), doc);
}

[Fact]
public void Hash_Changes_WhenPaperSpaceEntityMoves()
{
    var s = NewSession(doc =>
    {
        var layout = new ACadSharp.Objects.Layout("布局1");
        doc.Layouts.Add(layout);
        layout.AssociatedBlock.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) });
    });
    string before = s.ComputeHash();
    var line = s.Doc.Layouts.First(l => l.Name == "布局1").AssociatedBlock.Entities.OfType<Line>().First();
    line.StartPoint = new XYZ(5, 0, 0);
    Assert.NotEqual(before, s.ComputeHash());
}

[Fact]
public void Hash_Changes_WhenBlockDefinitionEntityMoves()
{
    var s = NewSession(doc =>
    {
        var b = new BlockRecord("图框");
        b.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) });
        doc.BlockRecords.Add(b);
    });
    string before = s.ComputeHash();
    s.Doc.BlockRecords.First(b => b.Name == "图框").Entities.OfType<Line>().First()
        .StartPoint = new XYZ(1, 1, 0);
    Assert.NotEqual(before, s.ComputeHash());
}

[Fact]
public void Hash_Changes_WhenInsertAttributeValueChanges()
{
    var s = NewSession(doc =>
    {
        var b = new BlockRecord("图框");
        doc.BlockRecords.Add(b);
        var ins = new Insert(b) { InsertPoint = new XYZ(0, 0, 0) };
        ins.Attributes.Add(new AttributeEntity { Tag = "图号", Value = "A" });
        doc.Entities.Add(ins);
    });
    string before = s.ComputeHash();
    s.Doc.Entities.OfType<Insert>().First().Attributes.First().Value = "B";
    Assert.NotEqual(before, s.ComputeHash());
}

[Fact]
public void Hash_IsStable_AcrossSaveWorkingReload_AndCaptureRestore()
{
    string path = DxfFixture.CreateTempDxf(doc =>
    {
        var layout = new ACadSharp.Objects.Layout("布局1");
        doc.Layouts.Add(layout);
        layout.AddViewport(new Viewport { Center = new XYZ(500, 350, 0), Width = 1000, Height = 700, ViewHeight = 200 });
        layout.AssociatedBlock.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(10, 0, 0) });
        var block = new BlockRecord("图框");
        block.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(1, 0, 0) });
        doc.BlockRecords.Add(block);
        var ins = new Insert(block) { InsertPoint = new XYZ(0, 0, 0) };
        ins.Attributes.Add(new AttributeEntity { Tag = "T", Value = "V" });
        doc.Entities.Add(ins);
    });
    try
    {
        var session = DrawingSession.Load(path);
        string h0 = session.ComputeHash();
        session.SaveWorking();
        session.Reload();
        Assert.Equal(h0, session.ComputeHash()); // B|/P| 排除依赖 BlockRecord.Layout 跨 DXF 往返保持
        byte[] state = session.CaptureState();
        session.RestoreState(state);
        Assert.Equal(h0, session.ComputeHash()); // undo 快照同口径
    }
    finally { File.Delete(path); } // .dxf 源：working 即自身
}

[Fact]
public void Hash_Changes_WhenInsertAttributeMoves()
{
    var s = NewSession(doc =>
    {
        var b = new BlockRecord("图框");
        doc.BlockRecords.Add(b);
        var ins = new Insert(b) { InsertPoint = new XYZ(0, 0, 0) };
        ins.Attributes.Add(new AttributeEntity { Tag = "图号", Value = "A" });
        doc.Entities.Add(ins);
    });
    string before = s.ComputeHash();
    s.Doc.Entities.OfType<Insert>().First().Attributes.First().InsertPoint = new XYZ(5, 5, 0);
    Assert.NotEqual(before, s.ComputeHash());
}
```

- [ ] **Step 2: 红**：前三个测试应失败（哈希不变）。

- [ ] **Step 3: 实现**（`DrawingSession.cs`）

```csharp
public string ComputeHash()
{
    var sb = new StringBuilder();
    foreach (var layer in Doc.Layers.OrderBy(l => l.Name, StringComparer.Ordinal))
        sb.Append(FormattableString.Invariant($"L|{layer.Name}|{layer.Color}")).Append('\n');

    foreach (var e in Doc.Entities.OrderBy(e => e.Handle))
        ProjectEntity(sb, "E", e);

    if (Doc.Layouts is not null)
    {
        foreach (var layout in Doc.Layouts.OrderBy(l => l.Name, StringComparer.Ordinal))
        {
            // 只投影纸面布局；模型布局（IsPaperSpace=false）与 E| 行是同一集合，重复投影浪费且语义错误。
            if (!layout.IsPaperSpace || layout.AssociatedBlock is null) continue;
            foreach (var e in layout.AssociatedBlock.Entities.OrderBy(e => e.Handle))
                ProjectEntity(sb, "P|" + layout.Name, e);
        }
    }

    foreach (var block in Doc.BlockRecords
                 .Where(b => b.Layout is null)
                 .OrderBy(b => b.Name, StringComparer.Ordinal))
    {
        if (string.IsNullOrEmpty(block.Name)) continue;
        foreach (var e in block.Entities.OrderBy(e => e.Handle))
            ProjectEntity(sb, "B|" + block.Name, e);
    }
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
}

private void ProjectEntity(StringBuilder sb, string prefix, Entity e)
{
    string head = $"{prefix}|{e.Handle}|{e.GetType().Name}|{e.Layer?.Name ?? "0"}|{e.Color}";
    sb.Append(head);
    switch (e)
    {
        case AttributeDefinition adef:
            sb.Append(FormattableString.Invariant(
                $"|ADEF|{adef.Tag}|{adef.Value}|{adef.Height:R}|{adef.Rotation:R}|{adef.InsertPoint.X:R}|{adef.InsertPoint.Y:R}"));
            break;
        case Line line:
            sb.Append(FormattableString.Invariant($"|{line.StartPoint.X:R}|{line.StartPoint.Y:R}|{line.StartPoint.Z:R}|{line.EndPoint.X:R}|{line.EndPoint.Y:R}|{line.EndPoint.Z:R}"));
            break;
        case TextEntity text:
            sb.Append(FormattableString.Invariant($"|{text.InsertPoint.X:R}|{text.InsertPoint.Y:R}|{text.InsertPoint.Z:R}|{text.Value}|{text.Height:R}"));
            break;
        case MText mt:
            sb.Append(FormattableString.Invariant($"|{mt.InsertPoint.X:R}|{mt.InsertPoint.Y:R}|{mt.Value}|{mt.Height:R}"));
            break;
        case LwPolyline lw:
            sb.Append(FormattableString.Invariant($"|{(lw.IsClosed ? 1 : 0)}"));
            foreach (var v in lw.Vertices)
                sb.Append(FormattableString.Invariant($"|{v.Location.X:R},{v.Location.Y:R}"));
            break;
        case Polyline2D p2:
            foreach (var v in p2.Vertices)
                sb.Append(FormattableString.Invariant($"|{v.Location.X:R},{v.Location.Y:R},{v.Location.Z:R}"));
            break;
        // Arc : Circle（ACadSharp 3.8.35）——必须先于 Circle，否则 CS8120 不可达。
        case Arc arc:
            sb.Append(FormattableString.Invariant($"|{arc.Center.X:R}|{arc.Center.Y:R}|{arc.Radius:R}|{arc.StartAngle:R}|{arc.EndAngle:R}"));
            break;
        case Circle circle:
            sb.Append(FormattableString.Invariant($"|{circle.Center.X:R}|{circle.Center.Y:R}|{circle.Radius:R}"));
            break;
        case Insert insert:
            sb.Append(FormattableString.Invariant($"|{insert.Block?.Name}|{insert.InsertPoint.X:R}|{insert.InsertPoint.Y:R}|{insert.XScale:R}|{insert.YScale:R}|{insert.ZScale:R}|{insert.Rotation:R}"));
            foreach (var attr in insert.Attributes.OrderBy(a => a.Tag, StringComparer.Ordinal))
                sb.Append(FormattableString.Invariant(
                    $"|A|{attr.Tag}|{attr.Value}|{attr.Layer?.Name ?? "0"}|{attr.InsertPoint.X:R},{attr.InsertPoint.Y:R}|{attr.Height:R}"));
            break;
        case Hatch hatch:
            sb.Append(FormattableString.Invariant($"|{hatch.IsSolid}|{hatch.Paths.Count}"));
            break;
        case UnknownEntity u:
            sb.Append(FormattableString.Invariant($"|{u.ObjectName}|{u.DxfClass?.CppClassName}"));
            break;
        case ProxyEntity p:
            sb.Append(FormattableString.Invariant($"|{p.ObjectName}|{p.DxfClass?.CppClassName}"));
            break;
    }
    sb.Append('\n');
}
```

> 注意：`BlockRecord.Layout is null` 用于排除 Model_Space/Paper_Space 关联块（其内容已在 `E|`/`P|` 行投影，避免双重投影）；块内 `AttributeDefinition` 由 `ProjectEntity` 的 ADEF 行覆盖（其 `Value` 为默认值）。若 `BlockRecord.Layout` 属性类型不匹配，按编译期实际类型调整（`Layout?`）。

- [ ] **Step 4: 绿 + 全 Data.Tests**

Run:
```
dotnet test tests/CadAgent.Data.Tests/CadAgent.Data.Tests.csproj --filter "FullyQualifiedName~DrawingSessionHashTests" --nologo
dotnet test tests/CadAgent.Data.Tests/CadAgent.Data.Tests.csproj --nologo
```
Expected: 新测 PASS；既有 Data 全绿（若有「哈希不变」断言依赖旧投影，按新事实修正并在 commit 说明——原则上均为相对断言）。

- [ ] **Step 5: 提交**

```bash
git add src/CadAgent.Data/DrawingSession.cs tests/CadAgent.Data.Tests/DrawingSessionHashTests.cs
git commit -m "feat(data): ComputeHash 覆盖纸面空间/块定义/属性值（承重修复）"
```

---

### Task 6: 路由纯函数 + 上下文工厂（A）

**Files:**
- Add: `src/CadAgent.View/Editing/EditRouting.cs`
- Test: `tests/CadAgent.View.Tests/EditRoutingTests.cs`

**Interfaces:**
- Produces:
```csharp
public enum EditSpaceIntent { Model, Paper, Locked, BlockChild, Unavailable }
public sealed record EditRoute(EditSpaceIntent Intent, bool CanMapModel, string? Reason);
public static class EditRouting
{
    public static EditRoute Route(LayoutViewportContext? vp, RenderItem? hit, (double X, double Y) paperPoint);
    public static LayoutViewportContext? ContextFrom(Viewport vp); // null when handle/scale degenerate
}
```
- `Route` 判定序（Plan-1 版）：`hit.Locked` → `Locked`；`hit.Space==ModelSpace` → `Model`（`vp` 为空或 twist → `Unavailable`+Reason）；`hit.Space==PaperSpace` → `Paper`（Plan-1 未启用→调用方提示）；未命中 → 在 `vp.PaperRect` 内 → `Model`，否则 `Paper`。
- `ContextFrom(vp)`：构造上下文后 `ctx.IsDegenerate ? null : ctx`（**唯一退化判据**，不得在 View 层重复 1e-12 字面量；且必须 `!vp.RepresentsPaper` 才可作为布局视口）；Handle=vp.Handle；TwistRadians=vp.TwistAngle。

- [ ] **Step 1: 写失败测试**（矩阵：模型命中/纸面命中/Locked/框内空点/框外空点/缺上下文/twist）

```csharp
[Fact]
public void Route_EmptyPointInsideViewport_IsModel()
{
    var vp = new LayoutViewportContext("1", (500, 350), 1000, 700, 200, (0, 0), 0);
    var r = EditRouting.Route(vp, null, (500, 350));
    Assert.Equal(EditSpaceIntent.Model, r.Intent);
    Assert.True(r.CanMapModel);
}

[Fact]
public void Route_EmptyPointOutsideViewport_IsPaper()
{
    var vp = new LayoutViewportContext("1", (500, 350), 1000, 700, 200, (0, 0), 0);
    Assert.Equal(EditSpaceIntent.Paper, EditRouting.Route(vp, null, (1200, 350)).Intent);
}

[Fact]
public void Route_Twist_ModelHitIsUnavailable_WithReason()
{
    var vp = new LayoutViewportContext("1", (500, 350), 1000, 700, 200, (0, 0), 0.3);
    var hit = new RenderItem(RenderKind.Line, new[] { (0.0, 0.0), (1.0, 0.0) }, "0", 0, null, 0, 0, "2",
        Space: RenderSpace.ModelSpace);
    var r = EditRouting.Route(vp, hit, (500, 350));
    Assert.Equal(EditSpaceIntent.Model, r.Intent);
    Assert.False(r.CanMapModel);
    Assert.Contains("旋转", r.Reason);
}

[Fact]
public void Route_LockedFrameBeatsSpace()
{
    var vp = new LayoutViewportContext("1", (500, 350), 1000, 700, 200, (0, 0), 0);
    var hit = new RenderItem(RenderKind.Line, new[] { (0.0, 0.0), (1.0, 0.0) }, "0", 0, null, 0, 0, "3",
        Space: RenderSpace.PaperSpace, Locked: true);
    Assert.Equal(EditSpaceIntent.Locked, EditRouting.Route(vp, hit, (0, 0)).Intent);
}
```

- [ ] **Step 2: 红 → Step 3 实现（按上述接口）→ Step 4 绿**

Run: `dotnet test tests/CadAgent.View.Tests/CadAgent.View.Tests.csproj --filter "FullyQualifiedName~EditRoutingTests" --nologo`

- [ ] **Step 5: 提交**

```bash
git add src/CadAgent.View/Editing/EditRouting.cs tests/CadAgent.View.Tests/EditRoutingTests.cs
git commit -m "feat(view): 编辑空间路由纯函数与视口上下文工厂"
```

---

### Task 7: MainWindow 布局编辑接线（A）

**Files:**
- Modify: `src/CadAgent.View/MainWindow.cs`
- Modify: `tests/CadAgent.View.Tests/SheetSelectorHeadlessTests.cs`（重写只读闸用例 + 新增逆映射用例）

**Interfaces:**
- Consumes: Tasks 3/4/6。
- Produces: 布局幅面下 Line/Text/InsertBlock/Move/Delete/ChangeLayer/Undo 可作用于视口内模型；纸面点击在建线/写字/插块与移动前置守卫中拒绝（Plan-1 未启用纸面编辑）；状态栏显示「（纸面视图：视口内可编辑模型）」。

- [ ] **Step 1: 写失败测试（headless）**

新增（复用现有 `LayoutDxf()` 夹具，s=700/200=3.5）：

```csharp
[AvaloniaFact]
public async Task LayoutSheet_LineToolInsideViewport_WritesModelLine_AtInverseCoords()
{
    (MainWindow window, string path, string projectDir) = Show(LayoutDxf(),
        new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
    try
    {
        window.SheetSelector!.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        // 纸面层（无纸面实体）+ 模型 1 线 + 视口框 4 线
        Assert.Equal(5, window.Canvas!.Items.Count);
        Assert.Equal(4, window.Canvas.Items.Count(i => i.Locked));

        ToolButton(window, "Line").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        ClickCanvasAtWorld(window, 500, 350); // 纸面中心 → 模型 (0,0)
        Assert.NotNull(window.Canvas.PendingPoint);
        await ActAndWaitForEditAsync(window, () => ClickCanvasAtWorld(window, 850, 350)); // → 模型 (100,0)
        Assert.Null(window.Edit!.ErrorMessage);
        Assert.Single(window.Edit.RecentTxIds);

        // 模型实体落在逆映射坐标（落盘回验）与纸面显示位置回验
        var saved = CadAgent.Data.DrawingSession.Load(path);
        Assert.Contains(saved.Doc.Entities.OfType<Line>(),
            l => Math.Abs(l.StartPoint.X) < 1e-6 && Math.Abs(l.EndPoint.X - 100) < 1e-6);
        var line = window.Canvas.Items.First(i => i.Kind == RenderKind.Line && !i.Locked);
        Assert.Equal(500, line.Points[0].X, 6);
        Assert.Equal(850, line.Points[1].X, 6);
    }
    finally { Cleanup(window, path, projectDir); }
}

[AvaloniaFact]
public void LayoutSheet_PaperClick_Refused_NoTx()
{
    (MainWindow window, string path, string projectDir) = Show(LayoutDxf(),
        new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
    try
    {
        window.SheetSelector!.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        ToolButton(window, "Line").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        ClickCanvasAtWorld(window, 1200, 350); // 视口外纸面
        Assert.Null(window.Canvas.PendingPoint);
        Assert.True(window.EditError!.IsVisible);
        Assert.Contains("纸面编辑", window.EditError.Text);
        Assert.Empty(window.Edit.RecentTxIds);
    }
    finally { Cleanup(window, path, projectDir); }
}
```

重写 `LayoutSheet_EditToolsHardDisabled_NoCrossSpaceWrite`（旧语义作废）为：

```csharp
[AvaloniaFact]
public async Task LayoutSheet_ViewportEditsAllowed_FrameAndPaperProtected()
{
    (MainWindow window, string path, string projectDir) = Show(LayoutDxf(),
        new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
    try
    {
        window.SheetSelector!.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        // 工具激活不再被整体拒绝
        ToolButton(window, "Line").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(EditTool.Line, window.Edit!.ActiveTool);
        // 视口框不可删：选中框线 → Delete 两次（确认 seam 注入 true）→ 拒绝
        window.Edit.ActiveTool = EditTool.Delete;
        window.Canvas!.Select(window.Canvas.Items.ToList().FindIndex(i => i.Locked));
        window.ConfirmDeleteOverride = _ => Task.FromResult(true);
        window.Edit.ActiveTool = EditTool.Delete;
        await ActAndWaitForEditAsync(window, () =>
            ToolButton(window, "Delete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        Assert.Empty(window.Edit.RecentTxIds);
        Assert.Contains("视口", window.EditError!.Text);
    }
    finally { Cleanup(window, path, projectDir); }
}
```

（`ConfirmDeleteOverride` 为 internal 属性，见既有测试用法；若签名不同按现有 seam 调整。）

- [ ] **Step 2: 红**（当前布局整体禁用/无纸面渲染）

Run: `dotnet test tests/CadAgent.View.Tests/CadAgent.View.Tests.csproj --filter "FullyQualifiedName~LayoutSheet_" --nologo`

- [ ] **Step 3: 实现（MainWindow）**

1. 字段：`private LayoutViewportContext? _layoutViewport;`（`using CadAgent.View.Editing;`）。
2. `ApplySheet`：计算 vp 与上下文；`ItemsForSheet` 布局分支改为 `RenderModelBuilder.ComposeLayout(_doc, sheet.LayoutName!, vp)`：

```csharp
var vp = sheet.LayoutName is null ? null : FindViewport(_doc, sheet.TitleAttributes.GetValueOrDefault("viewportHandle"));
_layoutViewport = vp is null ? null : EditRouting.ContextFrom(vp);
IReadOnlyList<RenderItem> items = sheet.LayoutName is null
    ? RenderModelBuilder.CropToFrame(_fullModelItems, sheet.FrameBounds)
    : RenderModelBuilder.ComposeLayout(_doc, sheet.LayoutName, vp);
```

3. 删除 `SetActiveTool` 的布局拒绝块；保留 `_layoutSheetActive` 供状态/其它判断。
4. 状态栏（`OnCanvasInvalidated`）：`_layoutSheetActive` 时 `status += "（纸面视图：视口内可编辑模型）"`。
5. 坐标换算辅助：

```csharp
private bool TryModelPoint(double px, double py, out double mx, out double my, string action)
{
    if (!_layoutSheetActive) { mx = px; my = py; return true; }
    if (_layoutViewport is null) { SetEditError("当前布局无有效视口，无法编辑模型"); mx = my = 0; return false; }
    if (!_layoutViewport.CanMapModel) { SetEditError("视口带旋转，纸→模型映射未支持；纸面编辑仍可用"); mx = my = 0; return false; }
    if (!InsidePaperRect(px, py)) { SetEditError($"纸面编辑将在后续切片提供；当前仅视口内可编辑模型（动作: {action}）"); mx = my = 0; return false; }
    (mx, my) = _layoutViewport.PaperToModel((px, py));
    return true;
}
private bool InsidePaperRect(double x, double y) => _layoutViewport is { } vp
    && x >= vp.PaperRect.MinX && x <= vp.PaperRect.MaxX
    && y >= vp.PaperRect.MinY && y <= vp.PaperRect.MaxY;
```

6. `OnCanvasToolClick`：
- Line：第一击 → `TryModelPoint` 校验但**预览锚点存纸坐标**（`Canvas.SetPendingPoint((wx, wy))`）；第二击 → 对首锚点与当前点都 `TryModelPoint`（首锚点失败→清 pending+错误），提交 `Edit.DrawLineAsync(mx1,my1,mx2,my2)`（模型坐标）。
- Text/InsertBlock：`TryModelPoint` 后以模型坐标提交。
- Move：命中后（仅模型命中可编辑；命中 Locked → 错误“视口边框只读”；命中 PaperSpace → 错误“纸面编辑将在后续切片提供”）；
  - 目标点击：`dx=(wx-bx), dy=(wy-by)`；布局时 `dx/=s; dy/=s`（s=`_layoutViewport!.Scale`；twist→拒绝）。
- Delete/ChangeLayer 选择：允许选中任何；执行前置守卫：
  - `OnDeleteClickAsync`：`Canvas.SelectedItem` 为 `Locked` → 拒绝“视口边框只读”；`Space==PaperSpace` → 提示后续切片；模型实体→ `ParentHandle` 后删除（布局下同样允许）。
  - `OnEditLayerSelected`：同上守卫后执行。
- 移除 Delete/Undo/ChangeLayer 的 `_layoutSheetActive` 整体拒绝块。

7. `SelectActiveTool` 之外的 `_layoutSheetActive` 守卫全部替换为上述按图元判定（grep 全部 10 处逐一处理）。
8. 既有用例修正（同一 commit）：`SheetSelector_LayoutSheet_BuildsViewportItems` 由「单线 1 项」改为「纸面 0 + 模型 1 + 视口框 4 = 5 项」，并断言 `4` 项 `Locked`；搜索其余对布局幅面项数的断言，按「纸面层+模型层+框」新事实更新。

- [ ] **Step 4: 绿（LayoutSheet 过滤）**

Run: `dotnet test tests/CadAgent.View.Tests/CadAgent.View.Tests.csproj --filter "FullyQualifiedName~LayoutSheet_" --nologo`

- [ ] **Step 5: 提交**

```bash
git add src/CadAgent.View/MainWindow.cs tests/CadAgent.View.Tests/SheetSelectorHeadlessTests.cs
git commit -m "feat(view): 布局视口内模型编辑（纸→模型逆映射+空间守卫）"
```

---

### Task 8: 回归修正、门禁与注记

**Files:**
- Modify: 受属性渲染/纸面合成影响的测试断言（以实测失败清单为准，如 `SheetSelector_LayoutSheet_BuildsViewportItems` 单线→5 项）
- Modify: `docs/superpowers/specs/2026-11-20-cad-agent-terminal-design.md` §12
- Modify: 本计划「执行记录」段（附录）

- [ ] **Step 1: 全量目标门禁**

```
dotnet test tests/CadAgent.Core.Tests/CadAgent.Core.Tests.csproj --nologo
dotnet test tests/CadAgent.Data.Tests/CadAgent.Data.Tests.csproj --nologo
dotnet test tests/CadAgent.View.Tests/CadAgent.View.Tests.csproj --nologo
dotnet test tests/CadAgent.Cli.Tests/CadAgent.Cli.Tests.csproj --nologo
```
Expected: 全绿；View 若命中已知 audit.db teardown flake → 隔离复跑并留证（不掩盖）。

- [ ] **Step 2: 全 sln 门禁**

`dotnet test CadAgent.sln --nologo` → 记录通过/跳过数（基线 1796/1skip；本计划新增用例使其增长）。

- [ ] **Step 3: spec §12 闭环注记**（追加一条，不删旧文）

内容：F 完成（RenderItem 空间字段/属性渲染/纸面合成/逆映射/哈希扩面）、A 完成（布局视口内模型编辑+守卫）、真实数字（测试数/门禁）、边界（纸面编辑 Plan-2、块编辑 Plan-3、twist 拒绝、非矩形不可检测）、哈希迁移注记。

- [ ] **Step 4: 提交**

```bash
git add docs/superpowers/specs/2026-11-20-cad-agent-terminal-design.md
git commit -m "docs(spec): 项2 Plan-1（F+A）闭环注记"
```

---

## 执行记录（执行中追加）

（待执行者按 SDD ledger 记录 ruling/commit/门禁数字。）
