namespace CadAgent.Core.Tests;

public class LlmProfileTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"llmcfg-{Guid.NewGuid():N}.json");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private static ProviderProfile P(string name = "云端主力", string endpoint = "https://api.example.com/v1") =>
        new(name, endpoint, "model-x", true, DataPolicy.CloudAllowed, new ProfileLimits());

    [Fact]
    public void Validate_BadEndpoint_Throws()
    {
        Assert.Throws<InvalidProfileException>(() => ProfileValidator.Validate(P(endpoint: "ftp://x")));
        Assert.Throws<InvalidProfileException>(() => ProfileValidator.Validate(P(endpoint: "not a uri")));
    }

    [Fact]
    public void Validate_EmptyName_Throws()
    {
        Assert.Throws<InvalidProfileException>(() => ProfileValidator.Validate(P(name: "")));
    }

    [Fact]
    public void Store_UpsertReplace_SetActive_Remove_RoundTrip()
    {
        var store = LlmConfigStore.Load(_path);
        store.Upsert(P());
        store.Upsert(P("本地保密", "http://localhost:11434/v1"));
        store.SetActive("本地保密");
        store.Save();
        var loaded = LlmConfigStore.Load(_path);
        Assert.Equal(2, loaded.Profiles.Count);
        Assert.Equal("本地保密", loaded.ActiveProfileName);
        loaded.Remove("云端主力");
        Assert.Single(loaded.Profiles);
    }

    [Fact]
    public void SetActive_Unknown_Throws()
    {
        var store = LlmConfigStore.Load(_path);
        Assert.Throws<InvalidProfileException>(() => store.SetActive("nope"));
    }

    [Fact]
    public void Save_ContainsNoSecretFields()
    {
        var store = LlmConfigStore.Load(_path);
        store.Upsert(P());
        store.Save();
        string json = File.ReadAllText(_path);
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
    }
}
