using ACadSharp.Entities;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CadAgent.Core;
using CadAgent.Data.Tests;
using CadAgent.View.ViewModels;
using CSMath;

namespace CadAgent.View.Tests;

/// <summary>
/// Final review fix 3: corrupt state files must NOT crash window construction
/// (T10 promised degrade). MainWindow wraps each state load in try/catch:
/// corrupt audit.db -> empty timeline + note "审计库损坏，时间线不可用";
/// corrupt roles.yaml -> default Draw role + note; corrupt signoffs.json ->
/// empty signoff store + note. The window still opens and the drawing renders;
/// notes aggregate into MainWindow.ErrorMessage (and a visible StateNote block).
/// </summary>
public class CorruptStateHeadlessTests
{
    private sealed class Fixture : IDisposable
    {
        public string ProjectDir { get; }
        public string DxfPath { get; }
        public string AgentDir => Path.Combine(ProjectDir, ".cadagent");

        public Fixture()
        {
            ProjectDir = Path.Combine(Path.GetTempPath(), "cadagent-corrupt-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(AgentDir);
            DxfPath = DxfFixture.CreateTempDxf(doc =>
            {
                doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0) });
                doc.Entities.Add(new Line { StartPoint = new XYZ(0, 10, 0), EndPoint = new XYZ(100, 10, 0) });
            });
        }

        public void CorruptAuditDb() => File.WriteAllBytes(Path.Combine(AgentDir, "audit.db"),
            new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE, 0x42, 0x42, 0x42 }); // not a SQLite database

        public void CorruptRolesYaml() => File.WriteAllText(Path.Combine(AgentDir, "roles.yaml"),
            "roles: { 'someone': NotARealRole }"); // Enum.Parse<Role> throws

        public void CorruptSignoffs() => File.WriteAllText(Path.Combine(AgentDir, "signoffs.json"), "{ not json");

        public void Dispose()
        {
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

    private static MainWindow ShowWindow(Fixture f, IIdentityProvider? identity = null)
    {
        var vm = new MainViewModel(f.DxfPath, f.ProjectDir);
        Assert.Null(vm.ErrorMessage);
        var window = new MainWindow(vm, identity);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void CorruptAuditDb_WindowOpens_EmptyTimeline_NoteShown_DrawingRenders()
    {
        using var f = new Fixture();
        f.CorruptAuditDb();
        MainWindow window = ShowWindow(f, new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
        try
        {
            Assert.NotNull(window.Canvas);
            Assert.Equal(2, window.Canvas!.Items.Count); // drawing still renders
            Assert.NotNull(window.ErrorMessage);
            Assert.Contains("审计库损坏，时间线不可用", window.ErrorMessage);
            Assert.NotNull(window.StateNote);
            Assert.True(window.StateNote!.IsVisible);
            Assert.Contains("审计库损坏", window.StateNote.Text);
            Assert.Empty(window.Audit!.Transactions); // degraded to empty timeline
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CorruptRolesYaml_WindowOpens_DefaultDrawRole_NoteShown()
    {
        using var f = new Fixture();
        f.CorruptRolesYaml();
        // identity NOT injected -> the roles.yaml load path is exercised.
        MainWindow window = ShowWindow(f);
        try
        {
            Assert.NotNull(window.Canvas);
            Assert.Equal(2, window.Canvas!.Items.Count);
            Assert.NotNull(window.ErrorMessage);
            Assert.Contains("roles.yaml 损坏", window.ErrorMessage);
            Assert.Contains("Draw", window.ErrorMessage);
            Assert.False(window.Signoff!.CanSign); // degraded default role = Draw
            Assert.False(window.ApproveButton!.IsEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CorruptSignoffsJson_WindowOpens_EmptySignoffStore_NoteShown()
    {
        using var f = new Fixture();
        f.CorruptSignoffs();
        MainWindow window = ShowWindow(f, new FixedIdentity(new OperatorIdentity("reviewer", Role.Review)));
        try
        {
            Assert.NotNull(window.Canvas);
            Assert.Equal(2, window.Canvas!.Items.Count);
            Assert.NotNull(window.ErrorMessage);
            Assert.Contains("signoffs.json 损坏", window.ErrorMessage);
            Assert.Empty(window.Signoff!.Signoffs); // degraded to empty store
            Assert.True(window.Signoff.CanSign); // identity unaffected
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void AllStateCorrupt_WindowOpens_AllNotes_DrawingRenders()
    {
        using var f = new Fixture();
        f.CorruptAuditDb();
        f.CorruptRolesYaml();
        f.CorruptSignoffs();
        MainWindow window = ShowWindow(f);
        try
        {
            Assert.NotNull(window.Canvas);
            Assert.Equal(2, window.Canvas!.Items.Count);
            Assert.Contains("审计库损坏，时间线不可用", window.ErrorMessage);
            Assert.Contains("roles.yaml 损坏", window.ErrorMessage);
            Assert.Contains("signoffs.json 损坏", window.ErrorMessage);
            Assert.Empty(window.Audit!.Transactions);
            Assert.Empty(window.Signoff!.Signoffs);
            Assert.False(window.Signoff.CanSign);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void HealthyState_NoErrorMessage_NoStateNote()
    {
        using var f = new Fixture();
        MainWindow window = ShowWindow(f, new FixedIdentity(new OperatorIdentity("tester", Role.Draw)));
        try
        {
            Assert.Null(window.ErrorMessage);
            Assert.True(window.StateNote is null || !window.StateNote.IsVisible);
        }
        finally { window.Close(); }
    }
}
