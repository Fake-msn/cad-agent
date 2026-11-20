namespace CadAgent.Core;

public enum SemanticClass { Unknown, Wall, Door, Window, Column, Axis, Annotation, Room, Auxiliary }

public enum Provenance { NativeObject, LayerInference, GeometryInference, LlmInference, HumanConfirmed }

public sealed record SemanticAnnotation(string TargetHandle, SemanticClass Class, Provenance Provenance, double Confidence, string Evidence);

public sealed record LayerMapping(string LayerName, SemanticClass Class, string ConfirmedBy, DateTimeOffset Ts);

public sealed record AmbiguousLayer(string LayerName, int EntityCount, IReadOnlyList<string> SampleHandles, string NameHint);