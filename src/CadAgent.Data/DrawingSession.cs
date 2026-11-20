using System.Security.Cryptography;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CadAgent.Core;

namespace CadAgent.Data;

public sealed class DrawingSession : IDrawingSession
{
    public CadDocument Doc { get; private set; }
    public string SourcePath { get; }

    private DrawingSession(string path, CadDocument doc)
    {
        SourcePath = path;
        Doc = doc;
    }

    public static DrawingSession Load(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        CadDocument doc = ext switch
        {
            ".dxf" => LoadDxf(path),
            ".dwg" => DwgReader.Read(path),
            _ => throw new NotSupportedException($"不支持的图纸格式: {ext}")
        };
        return new DrawingSession(path, doc);
    }

    private static CadDocument LoadDxf(string path)
    {
        using var reader = new DxfReader(path);
        return reader.Read();
    }

    public string ComputeHash()
    {
        using var ms = new MemoryStream();
        DxfWriter.Write(ms, Doc);
        return Convert.ToHexString(SHA256.HashData(ms.ToArray()));
    }

    public void SaveWorking()
    {
        using var fs = File.Create(SourcePath);
        DxfWriter.Write(fs, Doc);
    }

    public void Reload() => Doc = LoadDxf(SourcePath);

    public DrawingSummary Summarize()
    {
        var layers = Doc.Layers.Select(l => l.Name).ToList();
        var entities = new List<EntityBrief>();
        var unknowns = new List<UnknownBrief>();
        foreach (var e in Doc.Entities)
        {
            string handle = e.Handle.ToString();
            string layer = e.Layer?.Name ?? "0";
            if (e is UnknownEntity u)
                unknowns.Add(new UnknownBrief(handle, u.ObjectName ?? "",
                    u.DxfClass?.CppClassName ?? "", u.DxfClass?.ApplicationName ?? ""));
            else if (e is ProxyEntity p)
                unknowns.Add(new UnknownBrief(handle, p.ObjectName ?? "",
                    p.DxfClass?.CppClassName ?? "", p.DxfClass?.ApplicationName ?? ""));
            else
                entities.Add(new EntityBrief(handle, e.GetType().Name, layer));
        }
        return new DrawingSummary(SourcePath, Doc.Header.Version.ToString(), layers, entities, unknowns);
    }
}