using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CadAgent.TArchBridge.Protocol;

/// <summary>实体证据：句柄、分类、包装类型、图层、包围盒（min+max 共 6 值）与可炸开判定。</summary>
public sealed record EvidenceDto(
    string Handle,
    string RxClass,
    string? WrapperType,
    string? Layer,
    double[]? Bbox,
    bool FullyExplodable);

/// <summary>炸开所得基元：所属句柄、递归深度、分类与几何 token。</summary>
public sealed record PrimDto(string Handle, int Depth, string RxClass, string[] GeomTokens);

/// <summary>实体 XData 键值对。</summary>
public sealed record XDataDto(string Handle, int TypeCode, string Value);

/// <summary>符号块引用：句柄、块名与插入点。</summary>
public sealed record BlockDto(string Handle, string Name, double[] Position);

/// <summary>块定义内 TDb 聚合（边界补齐 B1，<c>BDEF</c> 记录）：块名、类名与**定义内**实体计数。
/// 与 ENTITY（模型空间实例，按句柄）是分离事实：定义计数 ≠ 实例计数（同一块可零实例可多实例）。</summary>
public sealed record BlockDefDto(string BlockName, string RxClass, int Count);

/// <summary>读取失败记录：句柄与原因。</summary>
public sealed record FailDto(string Handle, string Reason);

/// <summary>原地替换结果：保留句柄、新基元数、产物路径与包围盒校验结论。</summary>
public sealed record ReplaceDto(string Handle, int NewCount, string SavedPath, bool BboxMatch, double Tolerance);

/// <summary>
/// 桥→宿主结果（TSV 记录流，首行 <c>status\tok|fail</c>）；
/// 畸形记录一律 <see cref="FormatException"/>，绝不空结果扮成功。
/// </summary>
public sealed class BridgeResult
{
    private const string Num = "0.###";

    /// <summary>整体状态：ok 或 fail。</summary>
    public string Status { get; set; } = "ok";

    /// <summary>失败时的异常摘要（type: msg）。</summary>
    public string? Error { get; set; }

    /// <summary>实体证据记录。</summary>
    public List<EvidenceDto> Evidence { get; } = new List<EvidenceDto>();

    /// <summary>炸开基元记录。</summary>
    public List<PrimDto> Prims { get; } = new List<PrimDto>();

    /// <summary>XData 记录。</summary>
    public List<XDataDto> XData { get; } = new List<XDataDto>();

    /// <summary>符号块引用记录。</summary>
    public List<BlockDto> Blocks { get; } = new List<BlockDto>();

    /// <summary>块定义 TDb 聚合记录（B1：与 ENTITY 实例段分离）。</summary>
    public List<BlockDefDto> BlockDefs { get; } = new List<BlockDefDto>();

    /// <summary>失败记录。</summary>
    public List<FailDto> Fails { get; } = new List<FailDto>();

    /// <summary>替换结果（REPLACED 行与 VERIFY 行合并而成）。</summary>
    public ReplaceDto? Replace { get; set; }

    /// <summary>序列化为 TSV 记录流（行尾 <c>\n</c>）。</summary>
    public string Write()
    {
        var sb = new StringBuilder();
        sb.Append("status\t").Append(TsCodec.Escape(Status)).Append('\n');
        if (Error != null) sb.Append("error\t").Append(TsCodec.Escape(Error)).Append('\n');

        foreach (var e in Evidence)
        {
            sb.Append("ENTITY\t").Append(TsCodec.Escape(e.Handle)).Append('\t')
              .Append(TsCodec.Escape(e.RxClass)).Append('\t')
              .Append(TsCodec.Escape(e.WrapperType ?? string.Empty)).Append('\t')
              .Append(TsCodec.Escape(e.Layer ?? string.Empty)).Append('\t')
              .Append(e.Bbox == null ? string.Empty : Point(e.Bbox, 0)).Append('\t')
              .Append(e.Bbox == null ? string.Empty : Point(e.Bbox, 3)).Append('\t')
              .Append(e.FullyExplodable ? '1' : '0').Append('\n');
        }

        foreach (var p in Prims)
        {
            sb.Append("PRIM\t").Append(TsCodec.Escape(p.Handle)).Append('\t')
              .Append(p.Depth.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(TsCodec.Escape(p.RxClass)).Append('\t')
              .Append(TsCodec.Escape(string.Join("\t", p.GeomTokens))).Append('\n');
        }

        foreach (var x in XData)
            sb.Append("XData\t").Append(TsCodec.Escape(x.Handle)).Append('\t')
              .Append(x.TypeCode.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(TsCodec.Escape(x.Value)).Append('\n');

        foreach (var b in Blocks)
            sb.Append("BLOCK\t").Append(TsCodec.Escape(b.Handle)).Append('\t')
              .Append(TsCodec.Escape(b.Name)).Append('\t')
              .Append(Point(b.Position, 0)).Append('\n');

        foreach (var d in BlockDefs)
            sb.Append("BDEF\t").Append(TsCodec.Escape(d.BlockName)).Append('\t')
              .Append(TsCodec.Escape(d.RxClass)).Append('\t')
              .Append(d.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');

        foreach (var f in Fails)
            sb.Append("FAIL\t").Append(TsCodec.Escape(f.Handle)).Append('\t')
              .Append(TsCodec.Escape(f.Reason)).Append('\n');

        if (Replace != null)
        {
            sb.Append("REPLACED\t").Append(TsCodec.Escape(Replace.Handle)).Append('\t')
              .Append(Replace.NewCount.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(TsCodec.Escape(Replace.SavedPath)).Append('\n');
            sb.Append("VERIFY\t").Append(TsCodec.Escape(Replace.Handle)).Append('\t')
              .Append(Replace.BboxMatch ? '1' : '0').Append('\t')
              .Append(Replace.Tolerance.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>逐行按首列分派解析；空行跳过，未知记录 <see cref="FormatException"/>。</summary>
    public static BridgeResult Parse(string text)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));

        var result = new BridgeResult();
        bool hasStatus = false;
        bool hasReplaced = false, hasVerify = false;
        string? repHandle = null, repPath = null, verHandle = null;
        int repCount = 0;
        bool verMatch = false;
        double verTol = 0;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.EndsWith("\r") ? raw.Substring(0, raw.Length - 1) : raw;
            if (line.Length == 0) continue;
            var f = TsCodec.SplitLine(line);

            switch (f[0])
            {
                case "status":
                    Expect(f, 2);
                    result.Status = f[1];
                    hasStatus = true;
                    break;

                case "error":
                    Expect(f, 2);
                    result.Error = f[1];
                    break;

                case "ENTITY":
                    Expect(f, 8);
                    result.Evidence.Add(new EvidenceDto(
                        f[1], f[2], NullIfEmpty(f[3]), NullIfEmpty(f[4]),
                        ParseBbox(f[5], f[6]), ParseFlag(f[7])));
                    break;

                case "PRIM":
                    Expect(f, 5);
                    result.Prims.Add(new PrimDto(
                        f[1], ParseInt(f[2], "depth"), f[3],
                        // net45 无 Array.Empty<T>（.NET 4.6 引入），故用等价的空数组字面量。
                        f[4].Length == 0 ? new string[0] : f[4].Split('\t')));
                    break;

                case "XData":
                    Expect(f, 4);
                    result.XData.Add(new XDataDto(f[1], ParseInt(f[2], "code"), f[3]));
                    break;

                case "BLOCK":
                    Expect(f, 4);
                    result.Blocks.Add(new BlockDto(f[1], f[2], ParsePoint(f[3])));
                    break;

                case "BDEF":
                    Expect(f, 4);
                    // PR32：定义内实体计数 >=0 为协议不变量——负值无可解释语义，响亮拒绝。
                    int bdefCount = ParseInt(f[3], "blockDefCount");
                    if (bdefCount < 0) throw new FormatException($"blockDefCount 非法（不得为负）: {f[3]}");
                    result.BlockDefs.Add(new BlockDefDto(f[1], f[2], bdefCount));
                    break;

                case "FAIL":
                    Expect(f, 3);
                    result.Fails.Add(new FailDto(f[1], f[2]));
                    break;

                case "REPLACED":
                    Expect(f, 4);
                    hasReplaced = true;
                    repHandle = f[1];
                    repCount = ParseInt(f[2], "newCount");
                    repPath = f[3];
                    break;

                case "VERIFY":
                    Expect(f, 4);
                    hasVerify = true;
                    verHandle = f[1];
                    verMatch = ParseFlag(f[2]);
                    verTol = ParseDouble(f[3], "tolerance");
                    break;

                default:
                    throw new FormatException($"未知记录: {f[0]}");
            }
        }

        if (!hasStatus) throw new FormatException("缺少 status 记录");

        if (hasReplaced || hasVerify)
            result.Replace = new ReplaceDto(
                repHandle ?? verHandle ?? string.Empty,
                hasReplaced ? repCount : 0,
                repPath ?? string.Empty,
                hasVerify && verMatch,
                hasVerify ? verTol : 0);

        return result;
    }

    private static void Expect(string[] fields, int count)
    {
        if (fields.Length != count)
            throw new FormatException($"{fields[0]} 记录列数非法: 期望 {count}，实际 {fields.Length}");
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private static bool ParseFlag(string s) => s switch
    {
        "1" => true,
        "0" => false,
        _ => throw new FormatException($"0/1 标记非法: {s}"),
    };

    private static int ParseInt(string s, string name)
    {
        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            throw new FormatException($"{name} 非法: {s}");
        return v;
    }

    private static double ParseDouble(string s, string name)
    {
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            throw new FormatException($"{name} 非法: {s}");
        return v;
    }

    /// <summary>解析 "x,y,z" 三元组。</summary>
    private static double[] ParsePoint(string s)
    {
        var parts = s.Split(',');
        if (parts.Length != 3) throw new FormatException($"坐标非法: {s}");
        return new[]
        {
            ParseDouble(parts[0], "x"), ParseDouble(parts[1], "y"), ParseDouble(parts[2], "z"),
        };
    }

    /// <summary>两点包围盒合并为 6 值；任一点为空串则整体不可读（null）。</summary>
    private static double[]? ParseBbox(string min, string max)
    {
        if (min.Length == 0 || max.Length == 0) return null;
        var lo = ParsePoint(min);
        var hi = ParsePoint(max);
        return new[] { lo[0], lo[1], lo[2], hi[0], hi[1], hi[2] };
    }

    /// <summary>取 3 值点写入 "x,y,z"（0.### 不变文化）。</summary>
    private static string Point(double[] p, int offset)
    {
        if (p.Length < offset + 3) throw new ArgumentException("坐标数组长度不足", nameof(p));
        return Format(p[offset]) + "," + Format(p[offset + 1]) + "," + Format(p[offset + 2]);
    }

    private static string Format(double v) => v.ToString(Num, CultureInfo.InvariantCulture);
}
