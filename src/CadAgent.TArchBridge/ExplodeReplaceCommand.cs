using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CadAgent.TArchBridge.Protocol;

namespace CadAgent.TArchBridge
{
    /// <summary>
    /// EXPLODE_REPLACE：把模型空间指定句柄的 TArch 实体就地替换为其递归炸开产物。
    /// 校验链——错图→句柄存在→递归炸开完整（全非 TDb）→bbox 并集≈原 bbox→事务内删原实体+添产物
    /// →Commit→另存 liveout。任一校验失败即未 Commit（自动回滚）并回 fail 结果。
    /// 只写通道目录与宿主给定的 liveout；不改配置、不动源图。
    /// </summary>
    public partial class Commands
    {
        private const double BboxTolerance = 1e-3;

        /// <summary>原地炸开替换当前图纸的待处理请求。</summary>
        [CommandMethod("CADAGENT_TARCH_EXPLODE_REPLACE")]
        public void ExplodeReplaceCmd()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;
            string channel = BridgeFiles.ChannelDir();
            // 通道不可用（env 未设置/空串/目录不存在）：只提示，不抛异常、不写任何文件。
            if (string.IsNullOrEmpty(channel) || !Directory.Exists(channel))
            {
                ed.WriteMessage("\nCADAGENT_TARCH_EXPLODE_REPLACE: 通道不可用（CADAGENT_TARCH_CHANNEL 未设置或目录不存在）\n");
                return;
            }

            PendingRequest? pending = null;
            var result = new BridgeResult { Status = "ok" };
            try
            {
                pending = BridgeFiles.NextPending(channel);
                if (pending == null) { ed.WriteMessage("\nCADAGENT_TARCH_EXPLODE_REPLACE: 无待处理请求\n"); return; }
                // PendingRequest 为具名 struct（net45 无 ValueTuple）：一律用 .Value.ReqPath/.Id 具名访问。
                var p = pending.Value;
                var req = BridgeRequest.Parse(File.ReadAllText(p.ReqPath));
                if (!string.Equals(Path.GetFileName(doc.Name), Path.GetFileName(req.Dwg), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("错图: 当前文档 " + doc.Name + " ≠ " + req.Dwg);
                if (req.Handle == null || req.LiveOut == null)
                    throw new InvalidOperationException("请求缺少 handle/liveout");
                result.Replace = ReplaceCore(doc.Database, req.Handle, req.LiveOut, req.MaxDepth);
            }
            catch (System.Exception ex)
            {
                result.Status = "fail";
                result.Error = ex.GetType().Name + ": " + ex.Message;
                if (pending == null)
                {
                    // 连请求都未定位（扫描通道即失败）：没有可写的 res-<id>，只提示。
                    ed.WriteMessage("\nCADAGENT_TARCH_EXPLODE_REPLACE: 通道扫描失败——" + result.Error + "\n");
                    return;
                }
            }

            try { BridgeFiles.WriteResultAtomic(channel, pending.Value.Id, result.Write()); }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nCADAGENT_TARCH_EXPLODE_REPLACE: 写结果失败——" + ex.GetType().Name + ": " + ex.Message + "\n");
                return;
            }
            ed.WriteMessage("\nCADAGENT_TARCH_EXPLODE_REPLACE done\n");
        }

        /// <summary>
        /// 事务内替换核心：校验通过才删除原实体并提交，随后另存 liveout。
        /// 校验失败抛异常——using 事务未 Commit 即自动回滚，库内不留半份替换。
        /// </summary>
        internal static ReplaceDto ReplaceCore(Database db, string handle, string liveOut, int maxDepth)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                ObjectId targetId = ObjectId.Null;
                Entity target = null;
                foreach (ObjectId id in ms)
                {
                    if (!string.Equals(id.Handle.ToString(), handle, StringComparison.OrdinalIgnoreCase)) continue;
                    targetId = id;
                    target = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    break;
                }
                if (target == null) throw new InvalidOperationException("句柄不存在: " + handle);

                Extents3d orig;
                try { orig = target.GeometricExtents; }
                catch (System.Exception ex) { throw new InvalidOperationException("原实体无 extents: " + ex.GetType().Name); }

                // 校验 1：递归炸开完整（产物 > 0 且全非 TDb）。
                var prims = new List<PrimDto>();
                var scratch = new BridgeResult();
                bool fully = TryExplodeDeep(target, handle, 0, maxDepth, prims, scratch);
                if (!fully || prims.Count == 0)
                    throw new InvalidOperationException("炸开不完整: " + string.Join(";", scratch.Fails.Select(f => f.Reason)));
                var residue = prims.FirstOrDefault(x => x.RxClass.StartsWith("TDb", StringComparison.Ordinal));
                if (residue != null) throw new InvalidOperationException("炸开残留 TDb: " + residue.RxClass);

                // 校验 2：bbox 并集 ≈ 原 bbox，仅比 X/Y——墙体原实体带高度（Z≈3000），炸开的面线在 Z=0，
                // Z 参与必然误判不匹配。并集取本次校验炸开产物，算完即弃（唯一一次校验炸开）。
                Extents3d union = default(Extents3d);
                bool first = true;
                DBObjectCollection verify = new DBObjectCollection();
                try
                {
                    target.Explode(verify);
                    foreach (DBObject o in verify)
                    {
                        var e2 = o as Entity;
                        if (e2 == null) continue;
                        try
                        {
                            var b = e2.GeometricExtents;
                            if (first) { union = b; first = false; }
                            else union = Union(union, b);
                        }
                        catch { /* 个别产物无 extents：不参与并集，不因此判失败 */ }
                    }
                }
                finally
                {
                    // 校验产物是临时对象（未入事务、未入空间），必须全部释放。
                    foreach (DBObject o in verify) { try { o.Dispose(); } catch { } }
                }

                bool bboxMatch = first || (Math.Abs(union.MinPoint.X - orig.MinPoint.X) <= BboxTolerance
                    && Math.Abs(union.MinPoint.Y - orig.MinPoint.Y) <= BboxTolerance
                    && Math.Abs(union.MaxPoint.X - orig.MaxPoint.X) <= BboxTolerance
                    && Math.Abs(union.MaxPoint.Y - orig.MaxPoint.Y) <= BboxTolerance);
                if (!bboxMatch) throw new InvalidOperationException("bbox 不匹配（并集≠原实体）");

                // 替换：真实产物须归属当前事务——重新炸开并把产物逐个 AppendEntity 进模型空间；
                // 所有权经 AddNewlyCreatedDBObject(o, true) 移交，此后产物不得再 Dispose。
                var msW = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                var replacement = new DBObjectCollection();
                target.Explode(replacement);
                int newCount = 0;
                foreach (DBObject o in replacement)
                {
                    var e2 = o as Entity;
                    if (e2 == null) { o.Dispose(); continue; }   // 非实体产物入不了空间：就地释放，不计数
                    msW.AppendEntity(e2);
                    tr.AddNewlyCreatedDBObject(e2, true);
                    newCount++;
                }
                if (newCount == 0) throw new InvalidOperationException("炸开产物为空，拒绝删除原实体");

                ((Entity)tr.GetObject(targetId, OpenMode.ForWrite)).Erase();
                tr.Commit();

                try { db.SaveAs(liveOut, DwgVersion.Current); }
                catch (System.Exception ex)
                {
                    // 库内替换已生效但 liveout 未写：如实回 fail（宿主按源图 SHA 零写判定保护源文件）。
                    throw new InvalidOperationException("SaveAs 失败（库内已替换、liveout 未写）: " + ex.GetType().Name + ": " + ex.Message);
                }
                return new ReplaceDto(handle, newCount, liveOut, true, BboxTolerance);
            }
        }

        private static Extents3d Union(Extents3d a, Extents3d b)
        {
            var min = new Point3d(Math.Min(a.MinPoint.X, b.MinPoint.X), Math.Min(a.MinPoint.Y, b.MinPoint.Y), Math.Min(a.MinPoint.Z, b.MinPoint.Z));
            var max = new Point3d(Math.Max(a.MaxPoint.X, b.MaxPoint.X), Math.Max(a.MaxPoint.Y, b.MaxPoint.Y), Math.Max(a.MaxPoint.Z, b.MaxPoint.Z));
            return new Extents3d(min, max);
        }
    }
}
