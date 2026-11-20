namespace CadAgent.Core;

public sealed class SnapshotStore
{
    private readonly string _root;
    public SnapshotStore(string rootDir) => _root = rootDir;

    public string Take(string sourcePath, string txId)
    {
        string dir = Path.Combine(_root, txId);
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, target, overwrite: true);
        return target;
    }

    public string Find(string txId)
    {
        string dir = Path.Combine(_root, txId);
        if (!Directory.Exists(dir))
            throw new FileNotFoundException($"事务 {txId} 无快照", dir);
        var files = Directory.GetFiles(dir);
        if (files.Length == 0)
            throw new FileNotFoundException($"事务 {txId} 快照目录为空", dir);
        return files[0];
    }

    public void Restore(string txId, string targetPath) =>
        File.Copy(Find(txId), targetPath, overwrite: true);
}