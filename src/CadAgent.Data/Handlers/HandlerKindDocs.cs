namespace CadAgent.Data.Handlers;

/// <summary>步骤类型参数契约文本（CLI agent 与视图计划预览共用；LLM system prompt 消费）。</summary>
public static class HandlerKindDocs
{
    public const string Default = """
        add-line: 画直线 {x1,y1,x2,y2: number, layer?: string, space?: "paper", layout?: string}（space 仅支持 "paper" 且必须配 layout，写入该纸面布局，坐标为纸面坐标；缺省=模型空间；block 不接受——块定义新建不支持）
        add-text: 写单行文字 {x,y: number, text: string, height: number(mm), layer?: string, space?: "paper", layout?: string}（space/layout 同上；block 不接受——块定义新建不支持）
        add-circle: 画圆 {x,y: number, radius: number(mm), layer?: string, space?: "paper", layout?: string}（space/layout 同上；block 不接受——块定义新建不支持）
        delete-entity: 删除实体 {handle: number|string, block?: string(块名), space?: "paper", layout?: string, attribute?: true}（space 须配 layout；block=块名 → 删块定义内一层直接实体，全实例生效；属性定义 ATTDEF 不可删除；block 与 space/layout/attribute 互斥；匿名/外部参照/动态/未加载块拒绝）
        move-entity: 移动实体 {handle: number|string, dx,dy: number, block?: string(块名), space?: "paper", layout?: string, attribute?: true}（space 须配 layout；Line/Text/Circle/Arc/MText/Insert/LwPolyline/Polyline2D；block=块名 → 块定义内一层直接实体，dx/dy 为块局部增量（View 逆解世界/纸面增量；计划作者须直接给局部量），全实例生效；block 与 space/layout/attribute 互斥；匿名/外部参照/动态/未加载块拒绝）
        change-layer: 改图层 {handle: number|string, layer: string, block?: string(块名), space?: "paper", layout?: string, attribute?: true}（space 须配 layout；不存在则创建；block=块名 → 块定义内一层直接实体，全实例生效；block 与 space/layout/attribute 互斥；匿名/外部参照/动态/未加载块拒绝）
        set-text: 改文字/实例属性值 {handle: number|string, text: string, block?: string(块名), space?: "paper", layout?: string, attribute?: true}（space 须配 layout；≤4096 字符；空串=清空；TEXT/ATTRIB 禁换行，MText 可多行；block=块名 → 块定义内一层 TEXT/MTEXT 可改（ADEF 拒绝），全实例生效；块内 ATTRIB 不可达（仅一层直接实体，实例属性用 set-attribute）；block 与 space/layout/attribute 互斥；匿名/外部参照/动态/未加载块拒绝）
        set-attribute: 改实例属性值 {handle: number|string, value: string} 或 {insert: number|string, tag: string, value: string, space?: "paper", layout?: string}（space 须配 layout；value ≤4096、禁换行；insert 形态支持 space/layout；仅模型/纸面顶层 Insert.Attributes；tag 大小写敏感；同 tag 多条须改用句柄；block 不接受——块内属性编辑未支持）
        insert-block: 插入图纸内已有块 {block: string, x,y: number, scale?: number, rotation?: number(弧度), space?: "paper", layout?: string}（space 须配 layout → 写入该布局纸面；此处 block 为被插入块名，非块定义定位）
        layout-lighting: 房间网格布灯 {x,y,width,depth,spacing: number(mm), block: string, layer?: string}（需已确认 Room 语义；点位=区域内 spacing 网格）
        label-luminaires: 灯具编号/设备索引符号 {prefix: string(品种代号,归院标), block?: string, start?: number, symbolSize?: number(8~12mm,默认10), layer?: string}（需已确认 Lighting 语义；正六边形符号 JGJ/T 244-2011 3.6.6-4）
        insert-symbol: 跨文档导入 skill 符号并插入 {symbol: string(符号名,须 --skill 包内), x,y: number, scale?: number, rotation?: number(弧度), layer?: string}（建块/复用块；Line/Circle/Arc/Text 白名单）
        通用可选参数 sheetId: string（幅面 ID=图框 Insert 句柄）——携带时语义闸口按该幅面作用域校验（跨幅未确认→拒绝留痕）；缺省=legacy 全局作用域
        通用可选参数 attribute: true（仅 delete-entity/move-entity/change-layer/set-text 可用；attribute:true=仅匹配 Insert 属性，不匹配实体本身；set-attribute 句柄形态经解析器亦接受该标志，可省）
        通用可选参数 block: string（仅 delete-entity/move-entity/change-layer/set-text；块名 → 块定义内一层直接实体，全实例生效；move 的 dx/dy 为块局部增量；属性定义不可编辑（move/change-layer/delete/set-text 全部拒绝）；匿名/外部参照/动态/未加载块拒绝；与 space/layout/attribute 互斥；add-line/add-text/add-circle 与 set-attribute 不接受 block；layout-lighting/label-luminaires 的 block 另有语义（布灯块/符号块），见各自行）
        纸面隐含：新建族 add-line/add-text/add-circle/insert-block 与句柄编辑 delete-entity/move-entity/change-layer/set-text 及 set-attribute 的 insert+tag 形态，省略 space 仅给 layout 时均按该布局纸面解析（等价 space:"paper"）
        """;
}
