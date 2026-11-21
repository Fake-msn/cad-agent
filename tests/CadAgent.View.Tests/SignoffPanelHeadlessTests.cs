using System.Text.Json;
using ACadSharp.Entities;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CadAgent.Core;
using CadAgent.Data;
using CadAgent.Data.Handlers;
using CadAgent.Data.Tests;
using CadAgent.View.Services;
using CadAgent.View.ViewModels;
using CSMath;
using Point = Avalonia.Point; // ACadSharp.Entities.Point 冲突

namespace CadAgent.View.Tests;

/// <summary>
/// Plan 4 Task 7 headless coverage ([AvaloniaFact]): MainWindow signoff panel
/// builds (tx selector + comment + approve/reject + per-tx status). Default
/// identity (no roles.yaml -> Draw) disables both sign buttons; an injected
/// Review identity enables them and a real mouse click on Approve/Reject
/// persists the signoff, appends the hash-chained review-signed audit record
/// and updates the per-tx status. Self-review (tx operator == current account)
/// surfaces the refusal message inline; no selection -> guidance error.
/// </summary>
public class SignoffPanelHeadlessTests
{
    private sealed class Fixture : IDisposable
    {
        public string ProjectDir { get; }
        public string DxfPath { get; }
        public DrawingSession Session { get; }
        public SqliteAuditStore Audit { get; }
        public string Tx1Id { get; }
        public string Tx2Id { get; }
        public string SignoffPath => Path.Combine(ProjectDir, ".cadagent", "signoffs.json");

        public Fixture()
        {
            ProjectDir = Path.Combine(Path.GetTempPath(), "cadagent-signoff-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(ProjectDir, ".cadagent"));
            DxfPath = DxfFixture.CreateTempDxf(doc =>
                doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0) }));
            Session = DrawingSession.Load(DxfPath);
            Audit = new SqliteAuditStore(Path.Combine(ProjectDir, ".cadagent", "audit.db"));
            var snapshots = new SnapshotStore(Path.Combine(ProjectDir, ".cadagent", "snapshots"));
            var engine = new StepEngine(Audit, snapshots,
                new FixedIdentity(new OperatorIdentity("tester", Role.Draw)),
                new RoleChecker(), HandlerRegistry.All());
            Tx1Id = engine.Run(Plan("p1",
                ("add-line", """{"x1":0,"y1":60,"x2":100,"y2":60}""")), Session, "s1").TxId;
            Tx2Id = engine.Run(Plan("p2",
                ("add-text", """{"x":1,"y":2,"text":"NEW","height":3.5}""")), Session, "s1").TxId;
        }

        public void Dispose()
        {
            Audit.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(DxfPath);
            if (Directory.Exists(ProjectDir)) Directory.Delete(ProjectDir, true);
        }
    }

    private sealed class FixedIdentity : IIdentityProvider
    {
        public FixedIdentity(OperatorIdentity id) => Current = id;
        public OperatorIdentity Current { get; }
    }

    private static StepPlan Plan(string planId, params (string Kind, string Json)[] steps)
    {
        var specs = new List<StepSpec>();
        foreach ((string kind, string json) in steps)
        {
            using var doc = JsonDocument.Parse(json);
            specs.Add(new StepSpec(kind, doc.RootElement.Clone()));
        }
        return new StepPlan(planId, specs);
    }

    private static (MainWindow Window, Fixture F) ShowWindow(IIdentityProvider? identity = null)
    {
        var f = new Fixture();
        var vm = new MainViewModel(f.DxfPath, f.ProjectDir);
        Assert.Null(vm.ErrorMessage);
        var window = new MainWindow(vm, identity);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, f);
    }

    private static void Cleanup(MainWindow window, Fixture f)
    {
        window.Close();
        f.Dispose();
    }

    /// <summary>Real left click through the pointer pipeline (T6 pattern).</summary>
    private static void Click(MainWindow window, Button button)
    {
        Point p = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void SignoffPanel_Builds_ButtonsDisabledForDrawRole()
    {
        // 无 roles.yaml → WindowsIdentityProvider 默认 Draw → 按钮禁用。
        (MainWindow window, Fixture f) = ShowWindow();
        try
        {
            Assert.NotNull(window.Signoff);
            Assert.False(window.Signoff!.CanSign);
            Assert.NotNull(window.SignoffTxSelector);
            Assert.Equal(2, window.SignoffTxSelector!.ItemCount);
            Assert.NotNull(window.SignoffComment);
            Assert.NotNull(window.SignoffStatus);
            Assert.NotNull(window.ApproveButton);
            Assert.NotNull(window.RejectButton);
            Assert.False(window.ApproveButton!.IsEnabled);
            Assert.False(window.RejectButton!.IsEnabled);
            Assert.False(window.SignoffError!.IsVisible);
        }
        finally { Cleanup(window, f); }
    }

    [AvaloniaFact]
    public void ReviewRole_ApproveClick_PersistsSignoffAndAudit()
    {
        (MainWindow window, Fixture f) = ShowWindow(new FixedIdentity(new OperatorIdentity("reviewer", Role.Review)));
        try
        {
            Assert.True(window.Signoff!.CanSign);
            Assert.True(window.ApproveButton!.IsEnabled);
            Assert.True(window.RejectButton!.IsEnabled);

            window.SignoffTxSelector!.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("unsigned", window.SignoffStatus!.Text);
            window.SignoffComment!.Text = "looks good";
            Click(window, window.ApproveButton);

            Assert.False(window.SignoffError!.IsVisible);
            ReviewSignoff s = Assert.Single(window.Signoff!.Signoffs);
            Assert.Equal(f.Tx1Id, s.TxId);
            Assert.Equal("reviewer", s.Operator);
            Assert.True(s.Approved);
            Assert.Equal("looks good", s.Comment);
            Assert.True(File.Exists(f.SignoffPath));
            Assert.Single(new ReviewSignoffStore(f.SignoffPath).Items);

            AuditRecord last = f.Audit.ReadAll()[^1];
            Assert.Equal("review-signed", last.Entry.Kind);
            Assert.Equal("review", last.Entry.Source);
            Assert.Equal("reviewer", last.Entry.Operator);
            Assert.True(f.Audit.VerifyChain(out string error), error);

            Assert.Contains("approved ✓", window.SignoffStatus.Text);
            Assert.Contains("reviewer", window.SignoffStatus.Text);
            Assert.Single(window.Signoff.UnsignedTx); // tx2 仍未签认
        }
        finally { Cleanup(window, f); }
    }

    [AvaloniaFact]
    public void ReviewRole_RejectClick_ShowsRejectedStatus()
    {
        (MainWindow window, Fixture f) = ShowWindow(new FixedIdentity(new OperatorIdentity("reviewer", Role.Review)));
        try
        {
            window.SignoffTxSelector!.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            window.SignoffComment!.Text = "wrong layer";
            Click(window, window.RejectButton!);

            Assert.False(window.SignoffError!.IsVisible);
            ReviewSignoff s = Assert.Single(window.Signoff!.Signoffs);
            Assert.Equal(f.Tx2Id, s.TxId);
            Assert.False(s.Approved);
            Assert.Contains("rejected ✗", window.SignoffStatus!.Text);
        }
        finally { Cleanup(window, f); }
    }

    [AvaloniaFact]
    public void SelfReview_Click_ShowsRefusalMessage()
    {
        // tx 责任人 "tester" 持 Review 角色 → 自审拒绝，行内错误，不落签认。
        (MainWindow window, Fixture f) = ShowWindow(new FixedIdentity(new OperatorIdentity("tester", Role.Review)));
        try
        {
            Assert.True(window.ApproveButton!.IsEnabled); // 角色门禁通过，自审校验在签认时
            window.SignoffTxSelector!.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            Click(window, window.ApproveButton);

            Assert.True(window.SignoffError!.IsVisible);
            Assert.Contains("绘制者不能自审", window.SignoffError.Text);
            Assert.Empty(window.Signoff!.Signoffs);
            Assert.False(File.Exists(f.SignoffPath));
            Assert.DoesNotContain(f.Audit.ReadAll(), r => r.Entry.Kind == "review-signed");
        }
        finally { Cleanup(window, f); }
    }

    [AvaloniaFact]
    public void NoSelection_Click_ShowsGuidanceError()
    {
        (MainWindow window, Fixture f) = ShowWindow(new FixedIdentity(new OperatorIdentity("reviewer", Role.Review)));
        try
        {
            Click(window, window.ApproveButton!);
            Assert.True(window.SignoffError!.IsVisible);
            Assert.Contains("Select a transaction", window.SignoffError.Text);
            Assert.Empty(window.Signoff!.Signoffs);
        }
        finally { Cleanup(window, f); }
    }
}
