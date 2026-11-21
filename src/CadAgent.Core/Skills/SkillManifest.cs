namespace CadAgent.Core;

/// <summary>skill 包清单（skill.yaml）：名称、版本（digits.digits.digits）、专业、图种、描述。</summary>
public sealed record SkillManifest(string Name, string Version, string Discipline, string DrawingType, string Description);

/// <summary>skill 包非法（清单缺失/字段为空/版本格式错误/包内规则 Id 重复/YAML 结构错误等）。保留内层异常。</summary>
public sealed class InvalidSkillException : Exception
{
    public InvalidSkillException(string message) : base(message) { }
    public InvalidSkillException(string message, Exception inner) : base(message, inner) { }
}
