// 无 AutoCAD 托管程序集（AcMgd / AcDbMgd / AcCoreMgd 任一缺失）时本工程唯一编译的源文件：
// 让构建在任意机器上仍能产出空程序集，真实桥代码由 CADAGENT_ACMG_DIR 下三件套齐备时才编入。
// （AcCoreMgd 亦为必需：CommandMethod/CommandClass 等 Autodesk.AutoCAD.Runtime 特性在 AutoCAD 2013+ 已拆入其中。）
namespace CadAgent.TArchBridge
{
    internal static class NoAcadRefsStub
    {
    }
}
