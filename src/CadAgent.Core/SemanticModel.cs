namespace CadAgent.Core;

// T3：Equipment/Device 追加于枚举末尾（JSON 字符串枚举兼容；既有成员数值序不扰动）
public enum SemanticClass { Unknown, Wall, Door, Window, Column, Axis, Annotation, Room, Auxiliary, Lighting, Equipment, Device }

public enum Provenance { NativeObject, LayerInference, GeometryInference, LlmInference, HumanConfirmed }

// R6（T4 前移）：末尾可选 SheetId——既有构造点全兼容；旧 JSON 无此字段加载=null（legacy 无幅面归属）
public sealed record SemanticAnnotation(string TargetHandle, SemanticClass Class, Provenance Provenance, double Confidence, string Evidence, string? SheetId = null);

public sealed record LayerMapping(string LayerName, SemanticClass Class, string ConfirmedBy, DateTimeOffset Ts, string? SheetId = null);

public sealed record AmbiguousLayer(string LayerName, int EntityCount, IReadOnlyList<string> SampleHandles, string NameHint);
