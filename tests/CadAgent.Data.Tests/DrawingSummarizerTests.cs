using CSMath;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadAgent.Core;

namespace CadAgent.Data.Tests;

/// <summary>
/// 3b-i T2：LLM 读图文本摘要（视觉 deferred）——有界、确定性、含语义叠加层摘要。
/// </summary>
public class DrawingSummarizerTests : IDisposable
{
    private readonly string _path;
    public DrawingSummarizerTests() => _path = DxfFixture.CreateTempDxf(doc =>
    {
        doc.Layers.Add(new Layer("WALL"));
        doc.Entities.Add(new Line { StartPoint = (XYZ)new XY(0, 0), EndPoint = (XYZ)new XY(100, 0),
            Layer = doc.Layers.First(l => l.Name == "WALL") });
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(0, 0), Value = "客厅", Height = 5 });
        doc.Entities.Add(new TextEntity { InsertPoint = (XYZ)new XY(10, 0), Value = "卧室", Height = 5 });
    });
    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Summarize_ContainsLayersCountsTexts()
    {
        string s = DrawingSummarizer.Summarize(DrawingSession.Load(_path), null);
        Assert.Contains("WALL", s);
        Assert.Contains("客厅", s);
        Assert.Contains("卧室", s);
        Assert.Contains("Line", s);
        Assert.Contains("无语义叠加层", s);
    }

    [Fact]
    public void Summarize_WithOverlay_IncludesSemantics()
    {
        var overlay = new SemanticOverlay();
        overlay.ConfirmMapping(new LayerMapping("WALL", SemanticClass.Wall, "u", DateTimeOffset.UnixEpoch));
        string s = DrawingSummarizer.Summarize(DrawingSession.Load(_path), overlay);
        Assert.Contains("Wall", s);
        Assert.Contains("HumanConfirmed", s);
    }

    [Fact]
    public void Summarize_Truncation_Bounded()
    {
        string s = DrawingSummarizer.Summarize(DrawingSession.Load(_path), null, maxChars: 50);
        Assert.True(s.Length <= 60);
        Assert.Contains("截断", s);
    }

    [Fact]
    public void Summarize_Deterministic()
    {
        var session = DrawingSession.Load(_path);
        Assert.Equal(DrawingSummarizer.Summarize(session, null), DrawingSummarizer.Summarize(session, null));
    }
}