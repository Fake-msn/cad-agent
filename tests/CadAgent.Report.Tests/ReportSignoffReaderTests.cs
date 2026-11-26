using System.Text.Json;
using CadAgent.Report;

namespace CadAgent.Report.Tests;

public sealed class ReportSignoffReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cadagent-signoff-" + Guid.NewGuid().ToString("N"));

    public ReportSignoffReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(ReportSignoffReader.Load(Path.Combine(_dir, "nope.json")));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmpty()
    {
        string path = Path.Combine(_dir, "signoffs.json");
        File.WriteAllText(path, "{ not json");

        Assert.Empty(ReportSignoffReader.Load(path));
    }

    [Fact]
    public void Load_DuplicateTxOperator_KeepsLastEntry()
    {
        string path = Path.Combine(_dir, "signoffs.json");
        var t1 = DateTimeOffset.Parse("2026-11-25T10:00:00+08:00");
        var t2 = DateTimeOffset.Parse("2026-11-25T11:00:00+08:00");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Items = new object[]
            {
                new { TxId = "tx1", Operator = "a", Role = "Review", Approved = false, Comment = "旧", Ts = t1 },
                new { TxId = "tx1", Operator = "A", Role = "Review", Approved = true, Comment = "新", Ts = t2 }
            }
        }));

        var items = ReportSignoffReader.Load(path);

        var only = Assert.Single(items);
        Assert.True(only.Approved);
        Assert.Equal("新", only.Comment);
    }
}
