namespace CadAgent.Core;

public sealed class SnapshotStore
{
    private readonly string _root;
    public SnapshotStore(string rootDir) => _root = rootDir;

    public string Take(string sourcePath, string txId)
    {
        ValidateTxId(txId);
        string dir = Path.Combine(_root, txId);
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, target, overwrite: true);
        return target;
    }

    public string Find(string txId)
    {
        ValidateTxId(txId);
        string dir = Path.Combine(_root, txId);
        if (!Directory.Exists(dir))
            throw new FileNotFoundException($"事务 {txId} 无快照", dir);
        var files = Directory.GetFiles(dir);
        if (files.Length == 0)
            throw new FileNotFoundException($"事务 {txId} 快照目录为空", dir);
        if (files.Length > 1)
            throw new InvalidOperationException($"事务 {txId} 快照目录含多个文件");
        return files[0];
    }

    public void Restore(string txId, string targetPath) =>
        File.Copy(Find(txId), targetPath, overwrite: true);

    private static void ValidateTxId(string txId)
    {
        if (string.IsNullOrWhiteSpace(txId))
            throw new ArgumentException("事务 ID 不能为空", nameof(txId));
        if (Path.IsPathRooted(txId))
            throw new ArgumentException("事务 ID 不得为根路径", nameof(txId));
        if (txId.Contains('\\') || txId.Contains('/') || txId.Contains(".."))
            throw new ArgumentException("事务 ID 不得含路径分隔符或 ..", nameof(txId));
    }
}
