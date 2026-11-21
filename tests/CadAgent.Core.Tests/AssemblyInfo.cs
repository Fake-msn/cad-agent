// 同 Cli.Tests：ClearAllPools 进程级操作与集合并行互斥，禁用套件内并行。
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]