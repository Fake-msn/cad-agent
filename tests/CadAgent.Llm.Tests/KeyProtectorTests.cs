using CadAgent.Llm;

namespace CadAgent.Llm.Tests;

public class KeyProtectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"keys-{Guid.NewGuid():N}");
    public KeyProtectorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void StoreRetrieve_RoundTrip_NotPlaintext()
    {
        var p = new DpapiKeyProtector(_dir);
        p.Store("prof", "sk-secret-12345");
        Assert.Equal("sk-secret-12345", p.Retrieve("prof"));
        var files = Directory.GetFiles(_dir);
        Assert.Single(files);
        Assert.DoesNotContain("sk-secret-12345", File.ReadAllText(files[0]));
    }

    [Fact]
    public void Retrieve_Missing_Null()
    {
        Assert.Null(new DpapiKeyProtector(_dir).Retrieve("nope"));
    }

    [Fact]
    public void Delete_Removes()
    {
        var p = new DpapiKeyProtector(_dir);
        p.Store("prof", "k");
        p.Delete("prof");
        Assert.Null(p.Retrieve("prof"));
    }

    [Theory]
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    [InlineData("C:\\x")]
    public void Store_PathTraversal_Rejected(string name)
    {
        Assert.Throws<ArgumentException>(() => new DpapiKeyProtector(_dir).Store(name, "k"));
    }

    [Fact]
    public void ResolveKey_EnvOverridesStore()
    {
        var p = new DpapiKeyProtector(_dir);
        p.Store("prof", "stored");
        try
        {
            Environment.SetEnvironmentVariable("CADAGENT_LLM_API_KEY", "env-key");
            Assert.Equal("env-key", DpapiKeyProtector.ResolveKey(p, "prof"));
        }
        finally { Environment.SetEnvironmentVariable("CADAGENT_LLM_API_KEY", null); }
        Assert.Equal("stored", DpapiKeyProtector.ResolveKey(p, "prof"));
    }
}