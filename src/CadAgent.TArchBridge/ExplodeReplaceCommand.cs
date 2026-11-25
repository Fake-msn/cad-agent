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
    /// 叶集包围盒累计器：递归炸开时逐叶累加，供 X/Y 包含性判据使用。
    /// 叶数 <see cref="Count"/> 与并集 <see cref="Box"/> 来自同一次校验递归（R10 一致性交叉核对）。
    /// </summary>
    internal sealed class LeafBounds
    {
        /// <summary>叶实体总数（含个别无 extents 的叶）。</summary>
        public int Count { get; private set; }

        /// <summary>是否至少有一个叶拿到了 extents。</summary>
        public bool HasExtents { get; private set; }

        /// <summary>叶集包围盒并集（无 extents 的叶不参与）。</summary>
        public Extents3d Box { get; private set; }

        /// <summary>登记一个叶实体。</summary>
        public void Add(Entity leaf)
        {
            Count++;
            Extents3d b;
            try { b = leaf.GeometricExtents; }
            catch { return; }   // 个别叶无 extents：不参与并集，不因此判失败
            if (!HasExtents) { Box = b; HasExtents = true; }
            else Box = Commands.UnionExtents(Box, b);
        }
    }

    /// <summary>
    /// EXPLODE_REPLACE：把模型空间指定句柄的 TArch 实体就地递归炸开为其叶产物。
    /// 校验链——错图→liveout 守卫→句柄存在→递归炸开完整（叶&gt;0 且全非 TDb）→
    /// 叶集 bbox 并集 ⊆ 原 extents 外扩 1e-3（仅 X/Y）→ 事务内删原实体+递归 append 叶产物→
    /// 叶数一致→Commit→另存 liveout。任一校验失败即未 Commit（自动回滚）并回 fail 结果。
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
                // M10（复审修复波）：命令只服务 op=explode-replace——错 op 请求一律拒绝（不碰库）。
                req.RequireOp("explode-replace");
                if (!string.Equals(Path.GetFileName(doc.Name), Path.GetFileName(req.Dwg), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("错图: 当前文档 " + doc.Name + " ≠ " + req.Dwg);
                if (req.Handle == null || req.LiveOut == null)
                    throw new InvalidOperationException("请求缺少 handle/liveout");
                // 当前文档路径与请求 dwg 路径都传入守卫：liveout 指向二者之一即为覆盖源图，必须拒绝。
                result.Replace = ReplaceCore(doc.Database, doc.Name, req.Dwg, req.Handle, req.LiveOut, req.MaxDepth);
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
        /// liveout 前置守卫（R11）：非空；全路径不得等于当前文档路径或请求 dwg 路径（OrdinalIgnoreCase）；
        /// 父目录必须存在。任一不满足即抛中文消息——在开事务前拦下，绝不碰图。
        /// M3（PR#30 终审修复波）：纯路径逻辑上移到协议层 <see cref="LiveOutGuard.Check"/>（net45 桥
        /// 工程无法被 net8 测试引用，固守桥内则守卫不可测）；此处只做"错误→异常"桥内形态转换，
        /// 判据与中文文案逐字不变。
        /// </summary>
        internal static void GuardLiveOut(string liveOut, string docPath, string sourceDwg)
        {
            string error = LiveOutGuard.Check(liveOut, docPath, sourceDwg);
            if (error != null) throw new InvalidOperationException(error);
        }

        /// <summary>
        /// 事务内替换核心：校验通过才提交，随后另存 liveout。
        /// 校验失败抛异常——using 事务未 Commit 即自动回滚，库内不留半份替换。
        /// </summary>
        internal static ReplaceDto ReplaceCore(Database db, string docPath, string sourceDwg, string handle, string liveOut, int maxDepth)
        {
            GuardLiveOut(liveOut, docPath, sourceDwg);

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

                // 校验 1：一次递归炸开到底，同时得到叶集（>0 且全非 TDb）、叶数与其 bbox 并集。
                var prims = new List<PrimDto>();
                var scratch = new BridgeResult();
                var bounds = new LeafBounds();
                bool fully = TryExplodeDeep(target, handle, 0, maxDepth, prims, scratch, bounds);
                if (!fully || prims.Count == 0)
                    throw new InvalidOperationException("炸开不完整: " + string.Join(";", scratch.Fails.Select(f => f.Reason)));
                // 保底（TryExplodeDeep 只登记非 TDb 叶，正常不可达）：一旦出现 TDb 叶即拒绝。
                var residue = prims.FirstOrDefault(x => x.RxClass.StartsWith("TDb", StringComparison.Ordinal));
                if (residue != null) throw new InvalidOperationException("炸开残留 TDb: " + residue.RxClass);
                int verificationLeafCount = prims.Count;

                // 校验 2（R10b）：包含性 + 一致性，仅比 X/Y——墙体原实体带高度（Z≈3000），炸开的面线在 Z=0，
                // Z 参与必然误判；探针实证墙 21E00 面线 Y 止 130000 而 extents Y=130100，故判据是
                // 「叶集并集 ⊆ 原 extents 外扩 1e-3」而非相等（相等判据对该类必败）。
                if (!bounds.HasExtents)
                    throw new InvalidOperationException("bbox 无法校验: 叶集 " + verificationLeafCount + " 个全部无 extents");
                bool bboxMatch = bounds.Box.MinPoint.X >= orig.MinPoint.X - BboxTolerance
                    && bounds.Box.MinPoint.Y >= orig.MinPoint.Y - BboxTolerance
                    && bounds.Box.MaxPoint.X <= orig.MaxPoint.X + BboxTolerance
                    && bounds.Box.MaxPoint.Y <= orig.MaxPoint.Y + BboxTolerance;
                if (!bboxMatch)
                    throw new InvalidOperationException("bbox 包含性校验失败: 叶集并集 "
                        + F(bounds.Box.MinPoint) + ".." + F(bounds.Box.MaxPoint)
                        + " 未落在原 extents " + F(orig.MinPoint) + ".." + F(orig.MaxPoint)
                        + " 外扩 " + N(BboxTolerance, "0.###") + " 之内（仅 X/Y）");

                // 替换（R10a）：先取首层产物（原实体 Erase 后再 Explode 不再可靠），再删原实体，
                // 再逐层下钻到叶逐个 AppendEntity——绝不把 TDb 中间层写进空间。替换叶数须与校验叶数一致。
                var firstLayer = new DBObjectCollection();
                target.Explode(firstLayer);
                if (firstLayer.Count == 0) throw new InvalidOperationException("炸开产物为空，拒绝删除原实体");
                ((Entity)tr.GetObject(targetId, OpenMode.ForWrite)).Erase();
                var msW = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                int leafCount = 0;
                // owned = 未入空间的中间层/被拒产物，统一在 finally 释放；已 AppendEntity 的不在其中。
                var owned = new List<DBObject>();
                try
                {
                    foreach (DBObject o in firstLayer)
                    {
                        var e2 = o as Entity;
                        if (e2 == null) { owned.Add(o); throw new InvalidOperationException("非实体产物: " + o.GetType().Name); }
                        AppendLeafOrRecurse(e2, 0, maxDepth, tr, msW, ref leafCount, owned);
                    }
                    if (leafCount != verificationLeafCount)
                        throw new InvalidOperationException("叶数不一致: 替换 " + leafCount + " ≠ 校验 " + verificationLeafCount);
                    tr.Commit();
                }
                finally
                {
                    foreach (DBObject o in owned) { try { o.Dispose(); } catch { } }
                }

                // 回退 2 参（R11）：19.1 AcDbMgd 无 3 参 (fileName, bBakAndRename, version) 重载，
                // 实测仅 SaveAs(string,DwgVersion) / SaveAs(string,bool,DwgVersion,SecurityParameters)
                // / SaveAs(string,SecurityParameters)；3 参调用编译报 CS7036。2 参重载不产 .bak、不改名。
                try { db.SaveAs(liveOut, DwgVersion.Current); }
                catch (System.Exception ex)
                {
                    // 库内替换已生效但 liveout 未写：如实回 fail（宿主按源图 SHA 零写判定保护源文件）。
                    throw new InvalidOperationException("SaveAs 失败（库内已替换、liveout 未写）: " + ex.GetType().Name + ": " + ex.Message);
                }
                return new ReplaceDto(handle, leafCount, liveOut, true, BboxTolerance);
            }
        }

        /// <summary>
        /// 处理某一层产物：非 TDb → 入模型空间并计数；仍为 TDb → 继续下钻（自身不入空间）。
        /// <paramref name="depth"/> 为该产物自身的层深（与 <see cref="TryExplodeDeep"/> 的 PrimDto.Depth 同口径）。
        /// M11（PR#30 终审）：<c>AppendEntity</c> 与 <c>AddNewlyCreatedDBObject</c> 是两次调用——
        /// 旧实现在 AppendEntity 后立即从 owned 摘除，若 AddNewlyCreatedDBObject 抛错，该产物既在
        /// 模型空间又无人释放（事务 Abort 时可能残留）。收紧为：入 owned → 两次调用**皆成功**才摘除；
        /// 中途失败对象仍在 owned → 调用方 finally 统一 Dispose。
        /// </summary>
        private static void AppendLeafOrRecurse(Entity e, int depth, int maxDepth, Transaction tr, BlockTableRecord ms, ref int count, List<DBObject> owned)
        {
            string rx;
            try { rx = e.GetRXClass().Name; } catch { rx = "?"; }
            if (rx.StartsWith("TDb", StringComparison.Ordinal))
            {
                owned.Add(e);   // 中间层 TDb 不入空间：递归后由调用方 finally 释放
                AppendLeavesDeep(e, depth + 1, maxDepth, tr, ms, ref count, owned);
                return;
            }
            owned.Add(e);
            ms.AppendEntity(e);
            tr.AddNewlyCreatedDBObject(e, true);
            owned.Remove(e);   // 两次调用皆成功：所有权归空间/事务，此后不得 Dispose
            count++;
        }

        /// <summary>
        /// 递归 append：把位于 <paramref name="depth"/> 层的 TDb 实体炸开，对每个产物走
        /// <see cref="AppendLeafOrRecurse"/>；非实体产物与超深一律抛中文异常（fail-closed）。
        /// </summary>
        private static void AppendLeavesDeep(Entity ent, int depth, int maxDepth, Transaction tr, BlockTableRecord ms, ref int count, List<DBObject> owned)
        {
            if (depth > maxDepth) throw new InvalidOperationException("递归超深: 深度 " + depth + " > 上限 " + maxDepth);
            var exploded = new DBObjectCollection();
            ent.Explode(exploded);
            if (exploded.Count == 0) throw new InvalidOperationException("中间层炸开为空（深度 " + depth + "），拒绝提交替换");
            foreach (DBObject o in exploded)
            {
                var e2 = o as Entity;
                if (e2 == null) { owned.Add(o); throw new InvalidOperationException("非实体产物: " + o.GetType().Name); }
                AppendLeafOrRecurse(e2, depth, maxDepth, tr, ms, ref count, owned);
            }
        }

        /// <summary>三点包围盒并集（X/Y/Z 全并；判据只用 X/Y）。</summary>
        internal static Extents3d UnionExtents(Extents3d a, Extents3d b)
        {
            var min = new Point3d(Math.Min(a.MinPoint.X, b.MinPoint.X), Math.Min(a.MinPoint.Y, b.MinPoint.Y), Math.Min(a.MinPoint.Z, b.MinPoint.Z));
            var max = new Point3d(Math.Max(a.MaxPoint.X, b.MaxPoint.X), Math.Max(a.MaxPoint.Y, b.MaxPoint.Y), Math.Max(a.MaxPoint.Z, b.MaxPoint.Z));
            return new Extents3d(min, max);
        }
    }
}
