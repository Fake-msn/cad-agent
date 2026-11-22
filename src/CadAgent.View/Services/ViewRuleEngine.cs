using CadAgent.Core;
using CadAgent.Data.Checks;

namespace CadAgent.View.Services;

/// <summary>
/// 视图规范引擎装配（Issues 刷新与 Edit 回归共用）——逐字镜像 CLI
/// CommandRouter.StandardsEngine 注册列表与重复 CheckType 装配守卫。
/// </summary>
public static class ViewRuleEngine
{
    /// <summary>
    /// 规范引擎装配——逐字镜像 CLI CommandRouter.StandardsEngine 的注册列表
    /// （builtin 5 类 + 电气/模型 5 类）与重复 CheckType 装配守卫，
    /// 防止 RuleEngine 字典 last-wins 静默丢失检查器。
    /// </summary>
    public static RuleEngine Create()
    {
        IRuleChecker[] checkers =
        {
            new TextHeightChecker(), new LineweightChecker(), new AxisLetterChecker(),
            new ElevationChecker(), new LayerNameChecker(),
            new TextHeightMinChecker(), new CableFormatChecker(), new SlotCodeChecker(),
            new LuminaireFormatChecker(), new FileNamingChecker()
        };
        var duplicate = checkers.GroupBy(c => c.CheckType, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"重复注册检查器 CheckType: {duplicate.Key}");
        return new RuleEngine(checkers);
    }
}
