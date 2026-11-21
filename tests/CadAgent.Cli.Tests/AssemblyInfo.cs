// ClearAllPools 是进程级操作，与 xUnit 集合并行互斥（并行 Dispose 会清掉其他测试正在用的池化连接，
// 导致偶发 SQLITE 连接失效→命令 exit 1）。测试套件内禁用并行；跨程序集仍并行（独立进程）。
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]