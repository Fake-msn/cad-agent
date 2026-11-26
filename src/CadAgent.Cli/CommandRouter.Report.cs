using System.Text.Json;
using CadAgent.Core;
using CadAgent.Core.Cad;
using CadAgent.Data;
using CadAgent.Data.Rules;
using CadAgent.Data.Semantic;
using CadAgent.Report;

namespace CadAgent.Cli;

/// <summary>
/// report 命令族（审图报告导出）。与 check 同一评估路径（规则集/overlay/裁定 → RuleEngine），
/// 数据全量（问题/跳过/裁定/签认/审计验链/可选缩略图）→ ReviewReportExporter（CLI/View 共用）。
/// 导出成功后追加 report-exported 审计；审计写入失败→撤销已写报告（无审计不留产物）。
/// 只读不改图纸：前后哈希一致。退出码 0=导出成功；用法/评估/生成/审计失败=1。
/// </summary>
public static partial class CommandRouter
{
    private static int Report(string[] args, string[] positional, string agentDir, string session,
        TextWriter stdout, TextWriter stderr)
    {
        if (positional.Length < 2)
        {
            stderr.WriteLine("report 需要子命令: export");
            return 1;
        }
        return positional[1] switch
        {
            "export" => ReportExport(args, agentDir, session, stdout, stderr),
            _ => UnknownReportSubcommand(positional[1], stderr)
        };
    }

    private static int UnknownReportSubcommand(string sub, TextWriter stderr)
    {
        stderr.WriteLine($"未知 report 子命令: {sub}（可用: export）");
        return 1;
    }

    private static int ReportExport(string[] args, string agentDir, string session,
        TextWriter stdout, TextWriter stderr)
    {
        string drawingPath = RequireOption(args, "--drawing");
        string? outOpt = GetOption(args, "--out");
        string? pngOpt = GetOption(args, "--png");
        // 用法参数校验先于审计与加载（用法错误不留痕，镜像 rule adjudicate 校验序）
        byte[]? thumbnail = null;
        if (pngOpt is not null)
        {
            if (!File.Exists(pngOpt))
            {
                stderr.WriteLine($"缩略图文件不存在: {pngOpt}");
                return 1;
            }
            thumbnail = File.ReadAllBytes(pngOpt);
        }
        var ruleSet = LoadRuleSet(args, stderr);

        using var audit = OpenAudit(agentDir);
        var op = Identity(agentDir).Current;
        var drawing = LoadOrReject(() => LoadWithCensus(drawingPath, agentDir), drawingPath,
            session, audit, op, stderr, source: "report");
        if (drawing is null) return 1;
        string overlayPath = SemanticOverlayPath(agentDir);
        var overlay = File.Exists(overlayPath)
            ? LoadOrReject(() => SemanticOverlay.Load(overlayPath), overlayPath,
                session, audit, op, stderr, source: "report")
            : new SemanticOverlay();
        if (overlay is null) return 1;
        bool overlayPresent = overlay.Annotations.Count > 0;
        if (!overlayPresent)
            stderr.WriteLine("提示: 无语义标注，轴号/标高规则未评估（请先 semantic scan/map）");
        string adjPath = AdjudicationsPath(agentDir);
        var adjudications = LoadOrReject(() => AdjudicationSet.Load(adjPath), adjPath,
            session, audit, op, stderr, source: "report");
        if (adjudications is null) return 1;

        CheckSummary summary = StandardsEngine().Evaluate(ruleSet.AllRules, drawing, overlay, adjudications);
        WriteStructuredSkips(summary, stderr);
        int mandatoryViolations = summary.Issues.Count(i => i.Force == RuleForce.Mandatory);

        // 审计摘要取"导出前"时点（本次 report-exported 另记一条）
        var all = audit.ReadAll();
        bool chainOk = audit.VerifyChain(out string chainError);
        var auditSummary = new ReportAuditSummary(all.Count, chainOk, chainOk ? null : chainError,
            all.Count > 0 ? all[^1].Seq : 0, all.Count > 0 ? all[^1].Hash : null);

        var issuedAt = DateTimeOffset.Now;
        string projectDir = Path.GetDirectoryName(Path.GetFullPath(agentDir))!;
        string outPath = outOpt ?? ReviewReportExporter.DefaultPath(projectDir, drawingPath, issuedAt);

        var input = new ReviewReportInput
        {
            DrawingPath = drawing.SourcePath,
            ProjectDir = projectDir,
            Identity = new ReportIdentity(op.Account, op.Role.ToString()),
            IssuedAt = issuedAt,
            DrawingHash = drawing.ComputeHash(),
            RulePackVersion = ruleSet.RulePackVersion,
            AdjudicationHash = adjudications.Fingerprint(),
            OverlayPresent = overlayPresent,
            Issues = summary.Issues,
            RulesRun = summary.RulesRun,
            RulesSkipped = summary.RulesSkipped,
            Skipped = summary.StructuredSkips ?? Array.Empty<SkipReason>(),
            Adjudications = adjudications.Items,
            Signoffs = ReportSignoffReader.Load(Path.Combine(agentDir, "signoffs.json")),
            Audit = auditSummary,
            ThumbnailPng = thumbnail,
            RuleVerifications = ruleSet.AllRules.ToDictionary(
                r => r.Id, r => r.Source.Verification, StringComparer.Ordinal)
        };

        ReviewReportResult result;
        try
        {
            result = ReviewReportExporter.Export(input, outPath);
        }
        catch (Exception ex)
        {
            stderr.WriteLine(ex.Message);
            return 1;
        }

        try
        {
            // 审计先行（spec §9 无审计旁路）：失败即撤销本次产物（与 View 共用 helper）。
            ReportAuditCommit.Commit(audit, new AuditEntry(DateTimeOffset.UtcNow,
                op.Account, op.Role.ToString(), session, "report", "report-exported", drawingPath,
                JsonSerializer.Serialize(new
                {
                    outPath = result.Path,
                    issueCount = summary.Issues.Count,
                    mandatoryViolations,
                    auditChainOk = result.AuditChainOk,
                    thumbnail = thumbnail is not null,
                    rulesRun = summary.RulesRun,
                    rulesSkipped = summary.RulesSkipped
                }),
                input.DrawingHash, input.DrawingHash, "", ""), result.Path);
        }
        catch (ReviewReportException ex)
        {
            stderr.WriteLine(ex.Message);
            return 1;
        }

        stdout.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            path = result.Path,
            issueCount = summary.Issues.Count,
            mandatoryViolations,
            auditChainOk = result.AuditChainOk
        }, SemanticJsonOpts));
        return 0;
    }
}
