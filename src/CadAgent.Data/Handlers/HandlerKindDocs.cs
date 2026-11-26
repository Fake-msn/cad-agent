namespace CadAgent.Data.Handlers;

/// <summary>步骤类型参数契约文本（CLI agent 与视图计划预览共用；LLM system prompt 消费）。</summary>
public static class HandlerKindDocs
{
    public const string Default = """
        add-line: 画直线 {x1,y1,x2,y2: number, layer?: string}
        add-text: 写单行文字 {x,y: number, text: string, height: number(mm), layer?: string}
        add-circle: 画圆 {x,y: number, radius: number(mm), layer?: string}
        delete-entity: 删除实体 {handle: number|string}
        move-entity: 移动实体 {handle: number|string, dx,dy: number}（Line/Text/Circle/Arc/MText/Insert/LwPolyline/Polyline2D）
        change-layer: 改图层 {handle: number|string, layer: string}（不存在则创建）
        set-text: 改文字/实例属性值 {handle: number|string, text: string}（≤4096 字符；空串=清空；TEXT/ATTRIB 禁换行，MText 可多行；支持 space/layout/attribute 限定）
        set-attribute: 改实例属性值 {handle: number|string, value: string} 或 {insert: number|string, tag: string, value: string}（仅模型/纸面顶层 Insert.Attributes；tag 大小写敏感；同 tag 多条须改用句柄）
        insert-block: 插入图纸内已有块 {block: string, x,y: number, scale?: number, rotation?: number(弧度)}
        layout-lighting: 房间网格布灯 {x,y,width,depth,spacing: number(mm), block: string, layer?: string}（需已确认 Room 语义；点位=区域内 spacing 网格）
        label-luminaires: 灯具编号/设备索引符号 {prefix: string(品种代号,归院标), block?: string, start?: number, symbolSize?: number(8~12mm,默认10), layer?: string}（需已确认 Lighting 语义；正六边形符号 JGJ/T 244-2011 3.6.6-4）
        insert-symbol: 跨文档导入 skill 符号并插入 {symbol: string(符号名,须 --skill 包内), x,y: number, scale?: number, rotation?: number(弧度), layer?: string}（建块/复用块；Line/Circle/Arc/Text 白名单）
        通用可选参数 sheetId: string（幅面 ID=图框 Insert 句柄）——携带时语义闸口按该幅面作用域校验（跨幅未确认→拒绝留痕）；缺省=legacy 全局作用域
        """;
}
