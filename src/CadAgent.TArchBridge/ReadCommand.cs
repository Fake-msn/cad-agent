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
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;
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
                if (!string.Equals(System.IO.Path.GetFileName(doc.Name), System.IO.Path.GetFileName(req.Dwg), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("错图: 当前文档 " + doc.Name + " ≠ " + req.Dwg);
                ReadInto(result, doc.Database, req.MaxDepth);
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

        internal static void ReadInto(BridgeResult result, Database db, int maxDepth)
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
                        if (IsDegenerateExtents(box)) result.Fails.Add(new FailDto(handle, "extents:degenerate"));
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
                CollectBlockDefs(result, db, tr);
                tr.Commit();
            }
        }

        /// <summary>A1：退化包围盒判据——六分量 |v| 全 &lt; 1e-6 即退化（零尺寸/无几何）。</summary>
        internal static bool IsDegenerateExtents(double[] box)
        {
            for (int i = 0; i < box.Length; i++)
                if (Math.Abs(box[i]) >= 1e-6) return false;
            return true;
        }

        /// <summary>
        /// B1：块定义 TDb 聚合。遍历 BlockTable 记录（跳过 *Model_Space 与布局块 *Paper_Space*），
        /// 对每个块定义内的 TDb 实体按 (块名, 类) 计数；嵌套块只统计**直接**实体（不递归展开）。
        /// 输出按 (块名, 类) Ordinal 定序，保证同一图纸多次读取行序稳定。
        /// </summary>
        internal static void CollectBlockDefs(BridgeResult result, Database db, Transaction tr)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            var classes = new Dictionary<string, string>(StringComparer.Ordinal);

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
                if (string.Equals(blockName, "*Model_Space", StringComparison.OrdinalIgnoreCase)) continue;
                if (blockName.StartsWith("*Paper_Space", StringComparison.OrdinalIgnoreCase)) continue;

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

                    // 具名 struct 键（net45 无 ValueTuple）：块名与类名以 NUL 连接（两者不含 NUL）。
                    string key = blockName + "\u0000" + rx;
                    if (!counts.ContainsKey(key))
                    {
                        counts[key] = 0;
                        names[key] = blockName;
                        classes[key] = rx;
                    }
                    counts[key] = counts[key] + 1;
                }
            }

            foreach (string key in counts.Keys
                .OrderBy(k => names[k], StringComparer.Ordinal)
                .ThenBy(k => classes[k], StringComparer.Ordinal))
            {
                result.BlockDefs.Add(new BlockDefDto(names[key], classes[key], counts[key]));
            }
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
                // A2（边界补齐）：几何双通道 eInvalidInput（实证 TDbSymbArrow）= 无几何可炸，
                // 落独立原因 no-geometry:eInvalidInput（宿主据此如实披露"无几何"而非泛化异常）；
                // 其余异常文案逐字不变。注：.NET 枚举元素名为 InvalidInput
                //（ObjectARX 同名值写作 eInvalidInput；2014 反射核实无 e 前缀成员）。
                var acadEx = ex as Autodesk.AutoCAD.Runtime.Exception;
                if (acadEx != null && acadEx.ErrorStatus == ErrorStatus.InvalidInput)
                    result.Fails.Add(new FailDto(handle, "no-geometry:eInvalidInput"));
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
