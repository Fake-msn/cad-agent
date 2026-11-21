using CadAgent.Core;
using CadAgent.Core.Llm;

namespace CadAgent.Core.Tests;

/// <summary>
/// 3b-i T1 (§8.5)：项目硬约束 > profile 策略 > 首次云端确认；localhost 一等公民。
/// </summary>
public class EgressPolicyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"egress-{Guid.NewGuid():N}");
    public EgressPolicyTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private static ProviderProfile P(DataPolicy policy, string endpoint = "https://api.test/v1") =>
        new("p", endpoint, "m", false, policy, new ProfileLimits());

    [Fact]
    public void ProjectLocalOnly_BlocksEverything()
    {
        File.WriteAllText(Path.Combine(_dir, "local-only.txt"), "");
        var policy = new EgressPolicy(_dir);
        Assert.Equal(EgressDecision.BlockedLocalOnly, policy.Evaluate(P(DataPolicy.CloudAllowed), true));
        Assert.Equal(EgressDecision.BlockedLocalOnly, policy.Evaluate(P(DataPolicy.CloudAllowed, "http://localhost:11434/v1"), true));
    }

    [Fact]
    public void LocalProfile_RemoteEndpoint_Blocked_LocalhostAllowed()
    {
        var policy = new EgressPolicy(_dir);
        Assert.Equal(EgressDecision.BlockedLocalOnly, policy.Evaluate(P(DataPolicy.LocalOnly), true));
        Assert.Equal(EgressDecision.Allowed, policy.Evaluate(P(DataPolicy.LocalOnly, "http://localhost:11434/v1"), true));
    }

    [Fact]
    public void CloudProfile_DrawingData_FirstConsentThenAllowed()
    {
        var policy = new EgressPolicy(_dir);
        Assert.Equal(EgressDecision.NeedsFirstCloudConsent, policy.Evaluate(P(DataPolicy.CloudAllowed), true));
        policy.ConfirmCloudConsent();
        Assert.True(policy.ConsentGiven);
        Assert.Equal(EgressDecision.Allowed, policy.Evaluate(P(DataPolicy.CloudAllowed), true));
        Assert.True(new EgressPolicy(_dir).ConsentGiven);
    }

    [Fact]
    public void CloudProfile_NonDrawingData_NoConsentNeeded()
    {
        Assert.Equal(EgressDecision.Allowed, new EgressPolicy(_dir).Evaluate(P(DataPolicy.CloudAllowed), false));
    }

    [Fact]
    public void CloudProfile_Localhost_NoConsentNeeded()
    {
        Assert.Equal(EgressDecision.Allowed,
            new EgressPolicy(_dir).Evaluate(P(DataPolicy.CloudAllowed, "http://127.0.0.1:11434/v1"), true));
    }
}