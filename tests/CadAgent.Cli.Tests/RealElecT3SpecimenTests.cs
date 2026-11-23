using System.Text.Json;
using CadAgent.Core;
using CadAgent.Data;

namespace CadAgent.Cli.Tests;

/// <summary>
/// 真实电气 T3 标本 🟢 准入闭环实证（skip-gated）——首个真实 Green 准入证据钉住。
///
/// 背景（跨专业根因，用户核定）：1#配套用房为天正建筑专业绘制经电气插件导出——他专业对象
/// 原样保留代理（建筑件 TExport 部分转换 2769/29 类仍 Yellow，见 RealT3SpecimenTests）；
/// **同专业导出全炸开**：电气图 16#1116.dwg（天正建筑专业对象残留 398/17 类，含设备类
/// TCH_EQUIPMENT 17）经电气 TExport → 16#1116_t3.dwg census 0 → intake **Green**（exit 0）。
/// 实测数字（outputs/tarch-elec/intake-native.log / intake-t3.log / census-comparison.md）：
/// native Yellow：Entities 1615 / Unknowns 0 / census 398（TCH_WALL 139 / TCH_TEXT 94 /
/// TCH_COLUMN 55 / TCH_EQUIPMENT 17 …全 17 类）；T3 Green：Entities 2565 / Unknowns 0 /
/// unsupported [] / unparsed 键缺如（census 0 → null → WhenWritingNull 省略）。
///
/// 断言稳健口径（镜像 RealT3SpecimenTests fix wave 先例）：不赌精确总数/类数——
/// Green 面钉 exit 0 + unsupported 0 + census 键缺如 + sidecar 不落；
/// Yellow 面钉 exit 2 + census&gt;0 + TCH_ 类存在 + TCH_EQUIPMENT 存在（电气设备类特征，
/// 残留类整体为天正建筑专业类——跨专业根因实证，非电气专业 TCH 类）。
///
/// 实现先行口径（诚实记录）：census 感知准入闸口已于防假 Green 切片（UG-T1..T5）交付，
/// 本测试为真实标本实证钉住（非驱动新实现）——镜像 RealT3SpecimenTests/SheetTilerTests
/// "实现先行、实证钉住非驱动"先例。
///
/// 标本只读硬约束：.dwg 一律复制到临时项目再 intake（working/sidecar 全落临时目录，
/// 源目录零触碰）；副本字节前后一致；缺标本显式 Skipped 不假装。
/// </summary>
public class RealElecT3SpecimenTests : IDisposable
{
    private const string T3SpecimenRelative = "outputs/tarch-elec/16#1116_t3.dwg";
    private const string NativeSpecimenRelative = "outputs/tarch-elec/16#1116.dwg";

    private readonly string _project = Path.Combine(Path.GetTempPath(), $"realelec-{Guid.NewGuid():N}");

    public RealElecT3SpecimenTests() => Directory.CreateDirectory(_project);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_project, true);
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    /// <summary>复制真实标本到临时项目（只读源零副作用）；缺标本→Skip。</summary>
    private string CopySpecimen(string relative)
    {
        string src = Path.Combine(RepoRoot(), relative);
        Skip.If(!File.Exists(src), $"真实电气标本不在本机，跳过: {relative}");
        string dst = Path.Combine(_project, Path.GetFileName(src));
        File.Copy(src, dst);
        return dst;
    }

    [SkippableFact]
    public void RealElecT3Specimen_IntakeGreen_CensusZero()
    {
        // 首个真实 🟢 准入实证：同专业（电气）TExport 全炸开 → census 0 → Green。
        // 此前真实 🟢 证据仅合成 E2E（盲子集证据已作废，spec §12）；本测试钉住真实件准入面。
        string specimen = CopySpecimen(T3SpecimenRelative);
        byte[] sourceBefore = File.ReadAllBytes(specimen);

        var (code, stdout, _) = Run("intake", specimen);

        // T2 三分：原生实体 + census 0 → Green（exit 0），非 Yellow 非 Red。
        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal("Green", doc.RootElement.GetProperty("level").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("unsupported").GetArrayLength());
        Assert.Equal("", doc.RootElement.GetProperty("guidance").GetString());
        // census 0 → unparsed 键缺如（null → WhenWritingNull 省略，legacy stdout 契约）。
        Assert.False(doc.RootElement.TryGetProperty("unparsed", out _));
        var summary = doc.RootElement.GetProperty("summary");
        Assert.True(summary.GetProperty("Entities").GetArrayLength() > 0); // 炸开 plain 实体（实测 2565，不赌精确值）
        Assert.Equal(0, summary.GetProperty("Unknowns").GetArrayLength()); // 对象图盲区 0
        // Green 不落 census sidecar（LoadWithCensus 仅 TotalCount>0 回写）；Load 诚实 Empty。
        Assert.False(File.Exists(Path.Combine(_project, ".cadagent", "unparsed.json")));
        Assert.Equal(0, UnparsedCensusStore.Load(_project, specimen).TotalCount);
        // 标本只读：副本字节前后一致（intake 不改源 .dwg）。
        Assert.Equal(sourceBefore, File.ReadAllBytes(specimen));
    }

    [SkippableFact]
    public void RealElecNative_IntakeYellow()
    {
        // native 对照：电气图内天正建筑专业对象经电气插件导出原样保留代理 → Yellow。
        // 残留类全部为建筑专业 TCH 类（跨专业根因实证）；TCH_EQUIPMENT 为设备类特征
        // （实测 17，不赌精确值）——任务口径"TCH 电气类存在"按实测诚实钉住为设备类存在。
        string specimen = CopySpecimen(NativeSpecimenRelative);
        byte[] sourceBefore = File.ReadAllBytes(specimen);

        var (code, stdout, _) = Run("intake", specimen);

        Assert.Equal(2, code);
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal("Yellow", doc.RootElement.GetProperty("level").GetString());
        string guidance = doc.RootElement.GetProperty("guidance").GetString()!;
        Assert.Contains("只读审查", guidance);
        Assert.Contains("图形导出", guidance);
        var summary = doc.RootElement.GetProperty("summary");
        Assert.True(summary.GetProperty("Entities").GetArrayLength() > 0); // 有原生实体（Yellow 非 Red 前提）
        Assert.Equal(0, summary.GetProperty("Unknowns").GetArrayLength());
        // census 残留稳健钉住：非空 + TCH_ 前缀类存在 + TCH_EQUIPMENT 设备类存在。
        var classes = doc.RootElement.GetProperty("unparsed");
        Assert.True(classes.GetArrayLength() > 0);
        int total = 0, equip = 0; bool anyTch = false;
        foreach (var c in classes.EnumerateArray())
        {
            int n = c.GetProperty("Count").GetInt32();
            total += n;
            string name = c.GetProperty("CppClassName").GetString()!;
            if (name.StartsWith("TCH_", StringComparison.Ordinal)) anyTch = true;
            if (name == "TCH_EQUIPMENT") equip = n;
        }
        Assert.True(total > 0); // 实测 398/17 类，不赌精确值
        Assert.True(anyTch);
        Assert.True(equip > 0); // TCH_EQUIPMENT 残留（实测 17）
        // sidecar 回写（LoadWithCensus）：二次加载 census 总数与 stdout 一致。
        Assert.Equal(total, UnparsedCensusStore.Load(_project, specimen).TotalCount);
        // 标本只读：副本字节前后一致。
        Assert.Equal(sourceBefore, File.ReadAllBytes(specimen));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CadAgent.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
