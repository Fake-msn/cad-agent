using System;
using System.Collections.Generic;
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
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            string channel = BridgeFiles.ChannelDir();
            if (channel == null) { ed.WriteMessage("\nCADAGENT_TARCH_READ: 缺少 CADAGENT_TARCH_CHANNEL\n"); return; }
            var pending = BridgeFiles.NextPending(channel);
            if (pending == null) { ed.WriteMessage("\nCADAGENT_TARCH_READ: 无待处理请求\n"); return; }
            var p = pending.Value;
            var result = new BridgeResult { Status = "ok" };
            try
            {
                var req = BridgeRequest.Parse(System.IO.File.ReadAllText(p.ReqPath));
                var doc = Application.DocumentManager.MdiActiveDocument;
                if (!string.Equals(System.IO.Path.GetFileName(doc.Name), System.IO.Path.GetFileName(req.Dwg), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("错图: 当前文档 " + doc.Name + " ≠ " + req.Dwg);
                ReadInto(result, doc.Database, req.MaxDepth);
            }
            catch (System.Exception ex)
            { result.Status = "fail"; result.Error = ex.GetType().Name + ": " + ex.Message; }
            BridgeFiles.WriteResultAtomic(channel, p.Id, result.Write());
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
                        if (xd != null) foreach (TypedValue tv in xd) result.XData.Add(new XDataDto(handle, tv.TypeCode, Convert.ToString(tv.Value)));
                    }
                    catch (System.Exception ex) { result.Fails.Add(new FailDto(handle, "xdata:" + ex.GetType().Name)); }
                }
                tr.Commit();
            }
        }

        // 递归炸开：产物继续炸到无 TDb；返回是否"完整"（>0 产物且全非 TDb 且无异常）。
        internal static bool TryExplodeDeep(Entity ent, string handle, int depth, int maxDepth, List<PrimDto> outp, BridgeResult result)
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
            if (arc != null) return new[] { "center=" + F(arc.Center), "r=" + arc.Radius.ToString("0.###"), "ang=" + arc.StartAngle.ToString("0.######") + ".." + arc.EndAngle.ToString("0.######") };
            var circ = e as Circle;
            if (circ != null) return new[] { "center=" + F(circ.Center), "r=" + circ.Radius.ToString("0.###") };
            var txt = e as DBText;
            if (txt != null) return new[] { "text=" + txt.TextString, "pos=" + F(txt.Position) };
            var mt = e as MText;
            if (mt != null) return new[] { "mtext=" + mt.Contents.Replace("\n", "\\n"), "loc=" + F(mt.Location) };
            var pl = e as Polyline;
            if (pl != null)
            {
                var s = new List<string> { "verts=" + pl.NumberOfVertices };
                for (int i = 0; i < pl.NumberOfVertices && i < 32; i++) s.Add(F(pl.GetPoint3dAt(i)));
                return s.ToArray();
            }
            try { var x = e.GeometricExtents; return new[] { "extents=" + F(x.MinPoint) + ".." + F(x.MaxPoint) }; }
            catch { return new[] { "geom=?" }; }
        }

        internal static string F(Autodesk.AutoCAD.Geometry.Point3d p)
            => p.X.ToString("0.###") + "," + p.Y.ToString("0.###") + "," + p.Z.ToString("0.###");
    }
}
