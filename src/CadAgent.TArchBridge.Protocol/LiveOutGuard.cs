using System;
using System.IO;

namespace CadAgent.TArchBridge.Protocol;

/// <summary>
/// liveout 前置守卫（M3，PR#30 终审修复波）：把原桥内 <c>Commands.GuardLiveOut</c> 的**纯路径逻辑**
/// 上移到协议层（netstandard2.0/net45 双目标、可被 net8 测试直接引用）——桥 net45 工程无法在
/// 测试工程（net8）里直接引用，固守桥内时该守卫不可测。
/// 判据（与原实现逐条一致，中文文案逐字保留）：非空；全路径不得等于当前文档路径或请求 dwg 路径
/// （OrdinalIgnoreCase）；父目录必须存在。任一不满足返回中文错误（null=通过）。
/// 纯函数：仅 <see cref="Path.GetFullPath(string)"/> 与 <see cref="Directory.Exists(string)"/> 只读探测，
/// 不开事务、不碰图、不写任何文件（宿主/桥两侧都可安全调用）。
/// </summary>
public static class LiveOutGuard
{
    /// <summary>校验 liveout 路径；返回 null=通过，非 null=中文错误消息（桥按 <c>InvalidOperationException</c> 抛出）。</summary>
    public static string? Check(string liveOut, string? currentDocPath, string? requestDwgPath)
    {
        if (string.IsNullOrEmpty(liveOut)) return "liveout 为空";
        string live;
        try { live = Path.GetFullPath(liveOut); }
        catch (Exception ex) { return "liveout 路径非法: " + ex.GetType().Name + ": " + ex.Message; }

        string? docFull = null;
        if (!string.IsNullOrEmpty(currentDocPath))
        {
            try { docFull = Path.GetFullPath(currentDocPath!); } catch { docFull = null; }
        }
        if (docFull != null && string.Equals(live, docFull, StringComparison.OrdinalIgnoreCase))
            return "liveout 不得等于当前文档路径: " + live;

        string? srcFull = null;
        if (!string.IsNullOrEmpty(requestDwgPath))
        {
            try { srcFull = Path.GetFullPath(requestDwgPath!); } catch { srcFull = null; }
        }
        if (srcFull != null && string.Equals(live, srcFull, StringComparison.OrdinalIgnoreCase))
            return "liveout 不得等于源图路径: " + live;

        string? dir;
        try { dir = Path.GetDirectoryName(live); }
        catch (Exception ex) { return "liveout 父目录无法解析: " + ex.GetType().Name; }
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return "liveout 父目录不存在: " + (string.IsNullOrEmpty(dir) ? "（无目录部分）" : dir);
        return null;
    }
}
