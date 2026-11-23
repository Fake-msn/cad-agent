namespace CadAgent.Data.Vision;

/// <summary>
/// 离屏渲染 seam（T6）：幅面瓦片矩形→PNG 字节。Data 不引用 View（无环）——
/// 视图/CLI harness 注入实现（视图侧经 RenderModelBuilder 裁剪+Skia 离屏编码）；
/// 视觉读图调用方（VisionReader，CLI/视图 harness）经本接口取瓦片快照，
/// 再经 Core ILlmClient（ImageUrl）读图产出 VisionReadResult，交 VisionCrossChecker 矢量交叉验证。
/// rect=世界坐标矩形（模型幅面）或纸面矩形（布局幅面，与 SheetRecord.FrameBounds 同口径）；
/// w/h=目标像素尺寸（SheetTiler 字高反算保证文字≥targetTextPx 可读）。
/// </summary>
public interface IRenderSnapshot
{
    byte[] RenderPng(string sheetId, (double MinX, double MinY, double MaxX, double MaxY) rect, int w, int h);
}
