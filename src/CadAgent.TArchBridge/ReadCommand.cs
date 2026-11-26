using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using CadAgent.TArchBridge.Protocol;

[assembly: CommandClass(typeof(CadAgent.TArchBridge.Commands))]

namespace CadAgent.TArchBridge
{
    /// <summary>
    /// 桥内命令集（partial：REPLACE 等后续命令在独立文件扩展）。
    /// 只读写宿主给定的通道目录；不改配置、不动图纸。
    /// </summary>
    public partial class Commands
    {
        /// <summary>读取当前图纸全部 TArch 实体：递归炸开基元 / 包围盒 / XData / 符号块。</summary>
        [CommandMethod("CADAGENT_TARCH_READ")]
        public void ReadCmd()
        {
            // 消息面仍走活动文档编辑器（命令行输出与目标文档无关）；目标文档按请求名解析（见下）。
            var active = Application.DocumentManager.MdiActiveDocument;
            var ed = active.Editor;
            string channel = BridgeFiles.ChannelDir();
            // 通道不可用（env 未设置/空串/目录不存在）：只提示，不抛异常、不写任何文件。
            if (string.IsNullOrEmpty(channel) || !System.IO.Directory.Exists(channel))
            {
                ed.WriteMessage("\nCADAGENT_TARCH_READ: 通道不可用（CADAGENT_TARCH_CHANNEL 未设置或目录不存在）\n");
                return;
            }

            PendingRequest? pending = null;
            var result = new BridgeResult { Status = "ok" };
            try
            {
                pending = BridgeFiles.NextPending(channel);
                if (pending == null) { ed.WriteMessage("\nCADAGENT_TARCH_READ: 无待处理请求\n"); return; }
                // PendingRequest 为具名 struct（net45 无 ValueTuple）：一律用 .Value.ReqPath/.Id 具名访问。
                var p = pending.Value;
                var req = BridgeRequest.Parse(System.IO.File.ReadAllText(p.ReqPath));
                // M10（复审修复波）：命令只服务 op=read——错 op 请求一律拒绝（不碰库）。
                req.RequireOp("read");
                // 目标文档按请求 dwg 名在 DocumentManager 内解析（不再用 MdiActiveDocument）：
                // 宿主 Documents.Open 打开新图后未必激活它——真机 T5（elec-tdbe-01）实证活动文档
                // 仍是 Drawing1.dwg，旧"错图"守卫把正确打开的请求误判为错图。按名解析让"打开即读、
                // 与激活状态无关"；请求的图确实未打开时仍响亮失败（ResolveTargetDocument 抛出）。
                Document doc = ResolveTargetDocument(req.Dwg);
                ReadInto(result, doc.Database, req.MaxDepth, req.IncludeBlockDefs);
            }
            catch (System.Exception ex)
            {
                result.Status = "fail";
                result.Error = ex.GetType().Name + ": " + ex.Message;
                if (pending == null)
                {
                    // 连请求都未定位（扫描通道即失败）：没有可写的 res-<id>，只提示。
                    ed.WriteMessage("\nCADAGENT_TARCH_READ: 通道扫描失败——" + result.Error + "\n");
                    return;
                }
            }

            try { BridgeFiles.WriteResultAtomic(channel, pending.Value.Id, result.Write()); }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nCADAGENT_TARCH_READ: 写结果失败——" + ex.GetType().Name + ": " + ex.Message + "\n");
                return;
            }
            ed.WriteMessage("\nCADAGENT_TARCH_READ done\n");
        }

        /// <summary>
        /// 按请求的 dwg 名解析**目标文档**（READ / EXPLODE_REPLACE 共用）。
        /// 用 DocumentManager 按文件名匹配，而非 <c>MdiActiveDocument</c>：宿主侧的
        /// <c>Documents.Open</c> 只负责打开，不保证把新文档置为活动文档——真机证据（T5
        /// elec-tdbe-01）是 Open 成功后活动文档仍为 Drawing1.dwg，任何"以活动文档为准"的命令
        /// 都会读错图或误报"错图"。按名解析把"命令服务哪张图"与"当前窗口是哪张图"解耦。
        /// 匹配口径：<see cref="System.IO.Path.GetFileName(string)"/> + OrdinalIgnoreCase（与宿主
        /// 下发 <c>Path.GetFileName(drawingPath)</c> 的请求口径一致）；未找到即抛——请求的图确实
        /// 没打开时必须响亮失败，绝不悄悄退回活动文档。
        /// </summary>
        internal static Document ResolveTargetDocument(string requestedDwg)
        {
            string requestedBase = System.IO.Path.GetFileName(requestedDwg);
            foreach (Document doc in Application.DocumentManager)
            {
                if (string.Equals(System.IO.Path.GetFileName(doc.Name), requestedBase, StringComparison.OrdinalIgnoreCase))
                    return doc;
            }
            throw new InvalidOperationException("目标文档未打开: " + requestedDwg);
        }

        internal static void ReadInto(BridgeResult result, Database db, int maxDepth, bool includeBlockDefs)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    string rx; try { rx = ent.GetRXClass().Name; } catch { continue; }
                    if (!rx.StartsWith("TDb", StringComparison.Ordinal)) continue;
                    string handle = id.Handle.ToString();
                    double[] bbox = null;
                    try
                    {
                        var ext = ent.GeometricExtents;
                        double[] box = { ext.MinPoint.X, ext.MinPoint.Y, ext.MinPoint.Z, ext.MaxPoint.X, ext.MaxPoint.Y, ext.MaxPoint.Z };
                        // A1（边界补齐）：六分量全近零=退化 extents（零尺寸/无几何实体）——不得冒充
                        // "0,0,0..0,0,0"的可用包围盒；bbox 置 null（ENTITY 行两列为空）+ 落 FAIL 事实，
                        // 消费侧据 FailReason 区分"读不到"与"读到的就是零"。
                        // 判据已下沉协议层（ExtentsPolicy.IsDegenerate）：桥与宿主/测试共用同一份口径。
                        if (ExtentsPolicy.IsDegenerate(box)) result.Fails.Add(new FailDto(handle, "extents:degenerate"));
                        else bbox = box;
                    }
                    catch (System.Exception ex) { result.Fails.Add(new FailDto(handle, "extents:" + ex.GetType().Name)); }
                    var prims = new List<PrimDto>();
                    bool fully = TryExplodeDeep(ent, handle, 0, maxDepth, prims, result);
                    result.Evidence.Add(new EvidenceDto(handle, rx, ent.GetType().Name, ent.Layer, bbox, fully));
                    foreach (var p2 in prims.Take(64)) result.Prims.Add(p2);
                    if (prims.Count > 64) result.Prims.Add(new PrimDto(handle, -1, "PRIM-TRUNCATED", new[] { prims.Count.ToString() }));
                    try
                    {
                        var xd = ent.XData;
                        if (xd != null) foreach (TypedValue tv in xd) result.XData.Add(new XDataDto(handle, tv.TypeCode, Convert.ToString(tv.Value, CultureInfo.InvariantCulture)));
                    }
                    catch (System.Exception ex) { result.Fails.Add(new FailDto(handle, "xdata:" + ex.GetType().Name)); }
                }
                // B1（边界补齐）：块定义内 TDb 聚合**独立段**（BDEF）——与上方模型空间实例段
                // （ENTITY，按句柄）互不混同；定义计数不是实例计数。
                // A3（技术债批次A）：块定义段按需读取（请求 bdef=1）。未索取时**不**输出任何 BDEF 行——
                // "本轮没读块定义"与"读了但零定义"是不同事实，不得用空段互相冒充。
                if (includeBlockDefs) CollectBlockDefs(result, db, tr);
                tr.Commit();
            }
        }

        /// <summary>
        /// B1：块定义 TDb 聚合。遍历 BlockTable 记录**收集扫描行**（块名, 类, 是否布局块），
        /// 再交协议层 <see cref="BlockDefAggregator.Aggregate"/> 做布局块剔除 / 按 (块名, 类) 计数 /
        /// Ordinal 定序（口径下沉：桥与宿主/测试共用同一份判据）。
        /// 嵌套块只统计**直接**实体（不递归展开）。
        /// </summary>
        internal static void CollectBlockDefs(BridgeResult result, Database db, Transaction tr)
        {
            // B0（技术债批次B）：扫描行用具名记录（协议层 BlockDefScanRow）——元组签名需要
            // net45 System.ValueTuple（该垫片已随批次B删除）；具名记录两腿同源、零垫片。
            var rows = new List<BlockDefScanRow>();

            var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            if (bt == null) return;
            foreach (ObjectId btrId in bt)
            {
                BlockTableRecord btr;
                try { btr = tr.GetObject(btrId, OpenMode.ForRead) as BlockTableRecord; }
                catch { continue; }
                if (btr == null) continue;

                string blockName;
                try { blockName = btr.Name; }
                catch { continue; }
                if (string.IsNullOrEmpty(blockName)) continue;
                // M-1（修复波1）：布局块判定以 BTR.IsLayout 为主判据（模型/图纸空间布局在任何
                // 块名约定下都不会被当块定义计数）；名称前缀保留为兜底（IsLayout 读取异常时
                // 仍能挡住 *Model_Space/*Paper_Space*）。判定结果随行下传，剔除由聚合器统一执行。
                bool isLayout = false;
                try { isLayout = btr.IsLayout; }
                catch { /* 取不到按非布局，交由名称兜底 */ }
                if (!isLayout && string.Equals(blockName, "*Model_Space", StringComparison.OrdinalIgnoreCase))
                    isLayout = true;
                if (!isLayout && blockName.StartsWith("*Paper_Space", StringComparison.OrdinalIgnoreCase))
                    isLayout = true;
                // C-I4（技术债批次C）：布局块在**收集侧**即早退——不构建扫描行（不枚举其实体，
                // 省掉模型/图纸空间的全量遍历）。聚合器内的 IsLayout 跳过保留为双保险
                //（扫描行也可能来自其它来源/未来调用方）。
                if (isLayout) continue;

                foreach (ObjectId entId in btr)
                {
                    Entity e;
                    try { e = tr.GetObject(entId, OpenMode.ForRead) as Entity; }
                    catch { continue; }
                    if (e == null) continue;
                    string rx;
                    try { rx = e.GetRXClass().Name; }
                    catch { continue; }
                    if (!rx.StartsWith("TDb", StringComparison.Ordinal)) continue;

                    rows.Add(new BlockDefScanRow(blockName, rx, isLayout));
                }
            }

            foreach (var d in BlockDefAggregator.Aggregate(rows))
                result.BlockDefs.Add(new BlockDefDto(d.BlockName, d.RxClass, d.Count));
        }

        // 递归炸开：产物继续炸到无 TDb；返回是否"完整"（>0 产物且全非 TDb 且无异常）。
        // bounds 非空时在同一次递归里累计叶数与其 bbox 并集（REPLACE 的包含性判据复用本次结果，不再二次炸开）。
        internal static bool TryExplodeDeep(Entity ent, string handle, int depth, int maxDepth, List<PrimDto> outp, BridgeResult result, LeafBounds bounds = null)
        {
            if (depth > maxDepth) { result.Fails.Add(new FailDto(handle, "递归超深")); return false; }
            DBObjectCollection exploded = null;
            try
            {
                exploded = new DBObjectCollection();
                ent.Explode(exploded);
                // M10（PR#30 终审）：零产物也算一次失败事实——否则该行只有 FullyExplodable=false
                // 而 FAIL 列表为空（复核侧看不出"为何不全炸开"）。文案不落 递归超深/非实体产物
                // 前缀，故不改变 NestedTdbCount（残留）口径。
                if (exploded.Count == 0) { result.Fails.Add(new FailDto(handle, "炸开产物为空")); return false; }
                bool fully = true;
                foreach (DBObject o in exploded)
                {
                    var e2 = o as Entity;
                    if (e2 == null) { result.Fails.Add(new FailDto(handle, "非实体产物:" + o.GetType().Name)); fully = false; continue; }
                    string rx2; try { rx2 = e2.GetRXClass().Name; } catch { rx2 = "?"; }
                    if (rx2.StartsWith("TDb", StringComparison.Ordinal))
                    {
                        fully &= TryExplodeDeep(e2, handle, depth + 1, maxDepth, outp, result);
                    }
                    else
                    {
                        outp.Add(new PrimDto(handle, depth, rx2, GeomTokens(e2)));
                        if (bounds != null) bounds.Add(e2);
                        if (e2 is BlockReference br) result.Blocks.Add(new BlockDto(handle, br.Name, new[] { br.Position.X, br.Position.Y, br.Position.Z }));
                    }
                }
                return fully;
            }
            catch (System.Exception ex)
            {
                // A2（边界补齐 / PR32 更正）：几何双通道 eInvalidInput（实证 TDbSymbArrow）= 炸开请求
                // 被宿主以"输入无效"拒绝。**不得**再记作 no-geometry：reviewer 实证 TDbRectStair 有几何
                // 却因嵌套炸开同样落 eInvalidInput——"无几何"是过度断言，故原因改为
                // explode:eInvalidInput（爆炸阶段+状态码，可复核且不猜几何有无）。
                // 注：.NET 枚举元素名为 InvalidInput（ObjectARX 同名值写作 eInvalidInput；
                // 2014 反射核实无 e 前缀成员）。其余异常文案逐字不变。
                var acadEx = ex as Autodesk.AutoCAD.Runtime.Exception;
                if (acadEx != null && acadEx.ErrorStatus == ErrorStatus.InvalidInput)
                    result.Fails.Add(new FailDto(handle, "explode:eInvalidInput"));
                else
                    result.Fails.Add(new FailDto(handle, "explode:" + ex.GetType().Name));
                return false;
            }
            finally { if (exploded != null) foreach (DBObject o in exploded) { try { o.Dispose(); } catch { } } }
        }

        internal static string[] GeomTokens(Entity e)
        {
            var line = e as Line;
            if (line != null) return new[] { "start=" + F(line.StartPoint), "end=" + F(line.EndPoint) };
            var arc = e as Arc;
            if (arc != null) return new[] { "center=" + F(arc.Center), "r=" + N(arc.Radius, "0.###"), "ang=" + N(arc.StartAngle, "0.######") + ".." + N(arc.EndAngle, "0.######") };
            var circ = e as Circle;
            if (circ != null) return new[] { "center=" + F(circ.Center), "r=" + N(circ.Radius, "0.###") };
            var txt = e as DBText;
            if (txt != null) return new[] { "text=" + OneLine(txt.TextString), "pos=" + F(txt.Position) };
            var mt = e as MText;
            if (mt != null) return new[] { "mtext=" + mt.Contents.Replace("\t", " ").Replace("\r", " ").Replace("\n", "\\n"), "loc=" + F(mt.Location) };
            var pl = e as Polyline;
            if (pl != null)
            {
                var s = new List<string> { "verts=" + pl.NumberOfVertices.ToString(CultureInfo.InvariantCulture) };
                for (int i = 0; i < pl.NumberOfVertices && i < 32; i++) s.Add(F(pl.GetPoint3dAt(i)));
                return s.ToArray();
            }
            try { var x = e.GeometricExtents; return new[] { "extents=" + F(x.MinPoint) + ".." + F(x.MaxPoint) }; }
            catch { return new[] { "geom=?" }; }
        }

        /// <summary>
        /// 协议契约：单个 token 内不得含字面 TAB/CR/LF——TAB 会撕裂 PRIM 列，CR/LF 会伪造记录。
        /// 文本类实体一律降格为空格（MText 的字面换行另行转义为 <c>\n</c> 以保留段落语义）。
        /// </summary>
        internal static string OneLine(string s)
            => s.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");

        /// <summary>数字一律按不变文化格式化（宿主 Parse 亦 Invariant；中文区域的小数点/千分位会污染协议）。</summary>
        internal static string N(double v, string format) => v.ToString(format, CultureInfo.InvariantCulture);

        internal static string F(Autodesk.AutoCAD.Geometry.Point3d p)
            => N(p.X, "0.###") + "," + N(p.Y, "0.###") + "," + N(p.Z, "0.###");
    }
}
