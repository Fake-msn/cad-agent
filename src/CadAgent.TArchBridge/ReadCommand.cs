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
                    try { var ext = ent.GeometricExtents; bbox = new[] { ext.MinPoint.X, ext.MinPoint.Y, ext.MinPoint.Z, ext.MaxPoint.X, ext.MaxPoint.Y, ext.MaxPoint.Z }; }
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
                tr.Commit();
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
                if (exploded.Count == 0) return false;
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
            { result.Fails.Add(new FailDto(handle, "explode:" + ex.GetType().Name)); return false; }
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
