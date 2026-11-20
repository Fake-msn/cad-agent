namespace CadAgent.Core.Tests;

public class RoleTests
{
    private const string Yaml = """
        roles:
          "TEST\\drawer": Draw
          "TEST\\reviewer": Review
          "TEST\\approver": Approve
        """;

    [Fact]
    public void Parse_MapsAccountsToRoles()
    {
        var config = RoleConfig.Parse(Yaml);
        Assert.Equal(Role.Review, config.RoleFor("TEST\\reviewer"));
        Assert.Equal(Role.Approve, config.RoleFor("TEST\\approver"));
    }

    [Fact]
    public void UnknownAccount_DefaultsToDraw()
    {
        var config = RoleConfig.Parse(Yaml);
        Assert.Equal(Role.Draw, config.RoleFor("TEST\\stranger"));
    }

    [Fact]
    public void Ensure_PassesForSufficientRole()
    {
        var checker = new RoleChecker();
        checker.Ensure(new OperatorIdentity("TEST\\reviewer", Role.Review), Role.Review);
    }

    [Fact]
    public void Ensure_ThrowsForInsufficientRole()
    {
        var checker = new RoleChecker();
        var ex = Assert.Throws<RoleViolationException>(() =>
            checker.Ensure(new OperatorIdentity("TEST\\drawer", Role.Draw), Role.Review));
        Assert.Contains("Review", ex.Message);
    }

    [Fact]
    public void WindowsIdentityProvider_UsesCurrentAccount()
    {
        var provider = new WindowsIdentityProvider(RoleConfig.Parse(Yaml));
        var current = provider.Current;
        Assert.False(string.IsNullOrEmpty(current.Account));
    }
}