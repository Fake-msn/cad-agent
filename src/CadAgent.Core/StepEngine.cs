namespace CadAgent.Core;

public interface IDrawingSession
{
    string SourcePath { get; }
    string ComputeHash();
    void SaveWorking();
    void Reload();
}