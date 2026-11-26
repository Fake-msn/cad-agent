namespace CadAgent.Report;

/// <summary>审图报告生成/写入失败（字体缺失、PDF 渲染异常、磁盘写入失败）。</summary>
public sealed class ReviewReportException : Exception
{
    public ReviewReportException(string message) : base(message) { }
    public ReviewReportException(string message, Exception inner) : base(message, inner) { }
}
