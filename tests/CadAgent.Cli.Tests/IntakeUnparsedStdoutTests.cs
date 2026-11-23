using System.Text.Json;
using CadAgent.Data;
using CadAgent.Data.Tests;

namespace CadAgent.Cli.Tests;

/// <summary>
/// Fix Round 1（审查 Important#1 钉住）：intake stdout 内嵌整个 DrawingSummary，
/// 无 TCH 图纸必须"逐字不变"——空 census 不得多出 "Unparsed": [] 字段；
/// 有 census（真实原生天正标本，skip-gated）时必须含 Unparsed 数组。
/// </summary>
public class IntakeUnparsedStdoutTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"cadagent-intake-unparsed-{Guid.NewGuid():N}");
    public IntakeUnparsedStdoutTests() => Directory.CreateDirectory(_project);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_project, true);
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter();
        var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    [Fact]
    public void Intake_PlainDxf_Stdout_HasNoUnparsedField()
    {
        string drawing = DxfFixture.CreateTempDxf(DxfFixture.AddSampleLine);
        try
        {
            var (code, stdout, _) = Run("intake", drawing);
            Assert.Equal(0, code);
            using var doc = JsonDocument.Parse(stdout);
            var summary = doc.RootElement.GetProperty("summary");
            // 逐字不变：空 census → Unparsed=null → WhenWritingNull 省略整个字段（不得出现 "Unparsed": []）
            Assert.False(summary.TryGetProperty("Unparsed", out _));
            // 既有字段口径不变（加性字段不得挤掉/改动旧字段）
            Assert.Equal("Green", doc.RootElement.GetProperty("level").GetString());
            Assert.True(summary.TryGetProperty("SourcePath", out _));
            Assert.True(summary.TryGetProperty("Unknowns", out _));
            Assert.True(summary.TryGetProperty("Entities", out _));
        }
        finally
        {
            File.Delete(drawing);
        }
    }

    [SkippableFact]
    public void Intake_RealNativeTArch_Stdout_IncludesUnparsed()
    {
        string path = @"E:\随机任务 ·Qoder·\outputs\tarch-native3\2号厂房1021_t9.dwg";
        Skip.If(!File.Exists(path), "真实原生天正标本不在本机，跳过");
        // census 只产生于 DWG 首载通知通道（working DXF 无 TCH）：清除既有副本强制 LoadDwg，保证幂等。
        string working = DrawingSession.WorkingPathFor(path);
        if (File.Exists(working)) File.Delete(working);
        try
        {
            var (code, stdout, _) = Run("intake", path);
            // T2 闸口三分：图内 Unknowns=0（ACadSharp 暴露盲区）但 census 非空且有原生实体
            // → Yellow（exit 2）+ 只读审查 guidance。修改拒绝/agent 闸语义属 T3，不在本测试断言面。
            Assert.Equal(2, code);
            using var doc = JsonDocument.Parse(stdout);
            Assert.Equal("Yellow", doc.RootElement.GetProperty("level").GetString());
            Assert.Contains("只读审查", doc.RootElement.GetProperty("guidance").GetString());
            var summary = doc.RootElement.GetProperty("summary");
            Assert.True(summary.TryGetProperty("Unparsed", out var unparsed));
            Assert.Equal(JsonValueKind.Array, unparsed.ValueKind);
            Assert.True(unparsed.GetArrayLength() > 1000); // 实证 TotalCount=5707（逐对象展开口径）
            bool hasWall = false;
            foreach (var u in unparsed.EnumerateArray())
            {
                // 通知侧 brief 口径：Handle=""、ObjectName=CppClassName（与图内 Unknowns 分离）
                Assert.Equal("", u.GetProperty("Handle").GetString());
                if (u.GetProperty("CppClassName").GetString() == "TCH_WALL") hasWall = true;
            }
            Assert.True(hasWall);
        }
        finally
        {
            // 测试产物清理：标本目录恢复只读原状（仅 .dwg 本体；源 never written）
            if (File.Exists(working)) File.Delete(working);
        }
    }
}
