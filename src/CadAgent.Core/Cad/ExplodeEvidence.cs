namespace CadAgent.Core.Cad;

/// <summary>炸开后单基元（spec 切片B §3）：Core 零 COM——纯数据。
/// Points=平铺坐标（直线6/圆3/文字插入点3）；Text=文字内容；BlockName=块引用名。</summary>
public sealed record ExplodePrimitive(
    string Kind,
    double[] Points,
    string? Text,
    string? BlockName);

/// <summary>一实体一次深度炸开取证（spec 切片B §3）。Core 零 COM——纯数据。
/// FullyExplodable=递归炸开到叶全部成功；NestedTdbCount=炸开后残留 TDb 叶数（0=干净）；
/// CenterlineJson=通用轴（推导对任意≥2平行线成立；消费侧按 TDbWall/Class 过滤），可为 null；ReadAt=取证时刻；
/// FailReason=该句柄桥内**首条** FAIL 原因（边界补齐 A1/A2：<c>extents:degenerate</c>=退化包围盒、
/// <c>extents:Exception</c>=包围盒读取异常、<c>explode:eInvalidInput</c>=几何双通道拒绝炸开；
/// 无失败行=null）。
/// FailReasons=该句柄桥内**全部** FAIL 原因（按桥输出序，PR32 加性）：首条仍由 FailReason 承载，
/// 本字段供"首条之外还有哪些失败"的完整披露（如 SymbArrow 首条 extents:Exception、次条 explode:eInvalidInput）；
/// 无失败行=null（与旧 overlay 反序列化口径一致）。JSON 加性：旧 overlay 两字段均可为 null。</summary>
public sealed record ExplodeEvidence(
    string Handle,
    string RxClass,
    string? WrapperType,
    string? Layer,
    double[]? Bbox,
    bool FullyExplodable,
    int PrimitiveCount,
    int NestedTdbCount,
    IReadOnlyList<ExplodePrimitive> Primitives,
    IReadOnlyDictionary<string, string> XData,
    string? CenterlineJson,
    DateTimeOffset ReadAt,
    string? FailReason = null,
    IReadOnlyList<string>? FailReasons = null);

/// <summary>块定义内 TDb 实体聚合（边界补齐 B1）：**定义段**按 (块名, 类) 计数——
/// 与<see cref="ExplodeEvidence"/>（模型空间实例按句柄）是两条互不混同的事实线：
/// 块定义计数不是实例数（同一块可零实例可多实例），嵌套块只统计定义内的直接实体（不递归展开）。</summary>
public sealed record BlockDefTdbEntry(string BlockName, string RxClass, int Count);

/// <summary>句柄级炸开替换结果（spec 切片B §4）：BboxMatch=替换前后包围盒（仅 X/Y）容差内一致。</summary>
public sealed record ExplodeReplaceResult(
    string Handle,
    int NewCount,
    string SavedPath,
    bool BboxMatch,
    double Tolerance);
