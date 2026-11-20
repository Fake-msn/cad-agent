using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CadAgent.Core;

public sealed class RoleViolationException : Exception
{
    public RoleViolationException(string message) : base(message) { }
}

public sealed class RoleConfig
{
    private readonly Dictionary<string, Role> _byAccount;
    private RoleConfig(Dictionary<string, Role> byAccount) => _byAccount = byAccount;

    public static RoleConfig Parse(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        var raw = deserializer.Deserialize<RoleFile>(yaml) ?? new RoleFile();
        var map = new Dictionary<string, Role>(StringComparer.OrdinalIgnoreCase);
        if (raw.Roles != null)
            foreach (var (account, role) in raw.Roles)
                map[account] = Enum.Parse<Role>(role, ignoreCase: true);
        return new RoleConfig(map);
    }

    public static RoleConfig Load(string yamlPath) =>
        File.Exists(yamlPath) ? Parse(File.ReadAllText(yamlPath)) : Parse("roles: {}");

    public Role RoleFor(string account) =>
        _byAccount.TryGetValue(account, out var role) ? role : Role.Draw;

    private sealed class RoleFile
    {
        public Dictionary<string, string>? Roles { get; set; }
    }
}

public sealed class RoleChecker
{
    public void Ensure(OperatorIdentity op, Role required)
    {
        if (op.Role < required)
            throw new RoleViolationException(
                $"账号 {op.Account} 角色 {op.Role} 不满足所需角色 {required}");
    }
}