using CadAgent.Llm;

namespace CadAgent.Cli.Tests;

/// <summary>
/// 合并前审查 Warning#3 回归：--key-stdin 从标准输入读密钥（不进 argv/shell history）。
/// </summary>
public class KeyStdinTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), $"keystdin-{Guid.NewGuid():N}");
    private readonly TextReader _origIn = Console.In;

    public KeyStdinTests() => Directory.CreateDirectory(_project);
    public void Dispose()
    {
        Console.SetIn(_origIn);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_project, true);
    }

    private (int code, string stdout, string stderr) Run(params string[] args)
    {
        var outW = new StringWriter(); var errW = new StringWriter();
        int code = CommandRouter.Run(args.Append("--project").Append(_project).ToArray(), outW, errW);
        return (code, outW.ToString(), errW.ToString());
    }

    [Fact]
    public void Add_KeyStdin_StoredViaDpapi()
    {
        Console.SetIn(new StringReader("sk-stdinkey12345678\n"));
        var (code, _, err) = Run("llm", "add", "--name", "p", "--endpoint", "https://api.test/v1",
            "--model", "m", "--key-stdin");
        Assert.True(code == 0, err);
        var protector = new DpapiKeyProtector(Path.Combine(_project, ".cadagent", "keys"));
        Assert.Equal("sk-stdinkey12345678", protector.Retrieve("p"));
    }

    [Fact]
    public void Add_KeyStdin_Empty_Fails()
    {
        Console.SetIn(new StringReader("\n"));
        var (code, _, err) = Run("llm", "add", "--name", "p", "--endpoint", "https://api.test/v1",
            "--model", "m", "--key-stdin");
        Assert.Equal(1, code);
        Assert.Contains("--key-stdin", err);
    }
}
