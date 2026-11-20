using System.Security.Principal;

namespace CadAgent.Core;

public enum Role { Draw, Review, Approve }

public sealed record OperatorIdentity(string Account, Role Role);

public interface IIdentityProvider
{
    OperatorIdentity Current { get; }
}

public sealed class WindowsIdentityProvider : IIdentityProvider
{
    private readonly RoleConfig _config;
    public WindowsIdentityProvider(RoleConfig config) => _config = config;

    public OperatorIdentity Current
    {
        get
        {
            string account = WindowsIdentity.GetCurrent().Name;
            return new OperatorIdentity(account, _config.RoleFor(account));
        }
    }
}