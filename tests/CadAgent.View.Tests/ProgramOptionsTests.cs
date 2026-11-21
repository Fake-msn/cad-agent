namespace CadAgent.View.Tests;

/// <summary>
/// T10：Program 命令行解析单测——--skill 可重复、按出现序；尾随无值忽略
/// （与 CLI GetOptionValues 同口径）。不启动 Avalonia lifetime。
/// </summary>
public class ProgramOptionsTests
{
    [Fact]
    public void SkillDirs_Repeatable_InOrder()
    {
        var dirs = Program.GetOptionValues(
            new[] { "--drawing", "f.dxf", "--skill", "a", "--project", "p", "--skill", "b" }, "--skill");
        Assert.Equal(new[] { "a", "b" }, dirs);
    }

    [Fact]
    public void SkillDirs_None_Empty()
    {
        Assert.Empty(Program.GetOptionValues(new[] { "--drawing", "f.dxf" }, "--skill"));
    }

    [Fact]
    public void SkillDirs_TrailingWithoutValue_Ignored()
    {
        var dirs = Program.GetOptionValues(new[] { "--skill", "a", "--skill" }, "--skill");
        Assert.Equal(new[] { "a" }, dirs);
    }
}
