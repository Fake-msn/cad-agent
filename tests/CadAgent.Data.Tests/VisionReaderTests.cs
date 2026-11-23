using CadAgent.Core;
using CadAgent.Data.Vision;

namespace CadAgent.Data.Tests;

/// <summary>视觉读图 fake 客户端（不触网）：固定 content 返回，记录 ImageUrl 供 data-url 断言。</summary>
internal sealed class FakeVisionClient : ILlmClient
{
    private readonly string _content;
    public string? LastImageUrl;
    public FakeVisionClient(string content) => _content = content;
    public Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default)
    { LastImageUrl = request.ImageUrl; return Task.FromResult(new ChatResponse(_content, new TokenUsage(100, 50), "fake-vl")); }
    // ILlmClient 其余成员固定返回（本切片不消费模型列表）
    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(new[] { "fake-vl" });
}

/// <summary>
/// T1：VisionReader 瓦片读图（fake 可测）：PNG→data-url→claims 解析。
/// 诚实降级：非 JSON→ParseError+空 claims；缺字段 claim 跳过不抛。
/// </summary>
public class VisionReaderTests
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    [Fact]
    public async Task ReadTile_ValidJson_ClaimsParsed_ImageUrlSent()
    {
        var client = new FakeVisionClient("{\"claims\":[{\"text\":\"深照型灯\",\"region\":\"左上\",\"confidence\":0.9}]}");
        var reader = new VisionReader(client);
        var read = await reader.ReadTileAsync(Png, "S1", (0, 0, 100, 100));
        Assert.Single(read.Claims);
        Assert.Equal("深照型灯", read.Claims[0].Text);
        Assert.Null(read.ParseError);
        Assert.StartsWith("data:image/png;base64,", client.LastImageUrl);
        Assert.Equal(150, read.Usage.PromptTokens + read.Usage.CompletionTokens);
    }

    [Fact]
    public async Task ReadTile_JsonEmbeddedInProse_Extracted()
    {
        var client = new FakeVisionClient("读图如下：{\"claims\":[{\"text\":\"AL1\",\"region\":\"中\",\"confidence\":0.8}]} 以上。");
        var read = await new VisionReader(client).ReadTileAsync(Png, "S1", (0, 0, 1, 1));
        Assert.Single(read.Claims);
        Assert.Null(read.ParseError);
    }

    [Fact]
    public async Task ReadTile_Malformed_ParseErrorEmptyClaims()
    {
        var client = new FakeVisionClient("我无法读取该图。");
        var read = await new VisionReader(client).ReadTileAsync(Png, "S1", (0, 0, 1, 1));
        Assert.Empty(read.Claims);
        Assert.NotNull(read.ParseError);
    }

    [Fact]
    public async Task ReadTile_ClaimMissingFields_SkippedNotThrow()
    {
        var client = new FakeVisionClient("{\"claims\":[{\"text\":\"X\"},{\"text\":\"Y\",\"region\":\"r\",\"confidence\":0.7}]}");
        var read = await new VisionReader(client).ReadTileAsync(Png, "S1", (0, 0, 1, 1));
        Assert.Single(read.Claims); // 缺 confidence/region 的 claim 跳过（诚实：不完整不种子）
    }

    [Fact]
    public async Task ReadTile_ConfidenceOverflow_SkippedNotThrow()
    {
        // R5 钉住：1e999 语法合法 JSON 但 double 不可表示——旧实现 GetDouble 抛 FormatException 逸出 ReadTileAsync（RED 实证）
        var client = new FakeVisionClient(
            "{\"claims\":[{\"text\":\"X\",\"region\":\"r\",\"confidence\":1e999}," +
            "{\"text\":\"Y\",\"region\":\"r\",\"confidence\":0.9}]}");
        var read = await new VisionReader(client).ReadTileAsync(Png, "S1", (0, 0, 1, 1));
        Assert.Single(read.Claims); // 溢出 claim 与缺字段同通道跳过，不抛；合法 0.9 保留
        Assert.Equal("Y", read.Claims[0].Text);
        Assert.Equal(0.9, read.Claims[0].Confidence);
        Assert.Null(read.ParseError);
    }

    [Theory]
    [InlineData("5.0")]
    [InlineData("-0.3")]
    public async Task ReadTile_ConfidenceOutOfRange_Skipped(string conf)
    {
        // R5：confidence 超出 [0,1] → 与缺字段同通道跳过（不流向种子）；合法 0.9 保留
        var client = new FakeVisionClient(
            "{\"claims\":[{\"text\":\"X\",\"region\":\"r\",\"confidence\":" + conf + "}," +
            "{\"text\":\"Y\",\"region\":\"r\",\"confidence\":0.9}]}");
        var read = await new VisionReader(client).ReadTileAsync(Png, "S1", (0, 0, 1, 1));
        Assert.Single(read.Claims);
        Assert.Equal("Y", read.Claims[0].Text);
        Assert.Null(read.ParseError);
    }

    [Theory]
    [InlineData("{\"claims\":\"nope\"}")]
    [InlineData("{\"claims\":{\"text\":\"X\"}}")]
    public async Task ReadTile_ClaimsNotArray_ParseErrorEmptyClaims(string content)
    {
        // 降级分支覆盖：claims 非数组（字符串/对象）→ ParseError + 空 claims（诚实降级）
        var read = await new VisionReader(new FakeVisionClient(content)).ReadTileAsync(Png, "S1", (0, 0, 1, 1));
        Assert.Empty(read.Claims);
        Assert.NotNull(read.ParseError);
    }

    [Fact]
    public async Task ReadTile_ExtractedJsonInvalid_ParseErrorEmptyClaims()
    {
        // 降级分支覆盖：首{尾}提取后仍非法 JSON → JsonException catch 路径 → ParseError + 空 claims
        var client = new FakeVisionClient("读图：{broken} 完毕");
        var read = await new VisionReader(client).ReadTileAsync(Png, "S1", (0, 0, 1, 1));
        Assert.Empty(read.Claims);
        Assert.NotNull(read.ParseError);
        Assert.Contains("解析失败", read.ParseError); // 钉住走的是 JsonException 通道而非"非 JSON"通道
    }

    [Fact]
    public async Task ReadTile_NonObjectArrayElements_Skipped()
    {
        // 降级分支覆盖：claims 数组含非对象元素（数字/字符串/null）→ 跳过；合法对象保留
        var client = new FakeVisionClient(
            "{\"claims\":[42,\"s\",null,{\"text\":\"Y\",\"region\":\"r\",\"confidence\":0.9}]}");
        var read = await new VisionReader(client).ReadTileAsync(Png, "S1", (0, 0, 1, 1));
        Assert.Single(read.Claims);
        Assert.Equal("Y", read.Claims[0].Text);
        Assert.Null(read.ParseError);
    }

    [Fact]
    public async Task ReadTile_ConfidenceNonNumber_Skipped()
    {
        // 降级分支覆盖：confidence 非数字（字符串）→ 跳过；合法 0.9 保留
        var client = new FakeVisionClient(
            "{\"claims\":[{\"text\":\"X\",\"region\":\"r\",\"confidence\":\"high\"}," +
            "{\"text\":\"Y\",\"region\":\"r\",\"confidence\":0.9}]}");
        var read = await new VisionReader(client).ReadTileAsync(Png, "S1", (0, 0, 1, 1));
        Assert.Single(read.Claims);
        Assert.Equal("Y", read.Claims[0].Text);
        Assert.Null(read.ParseError);
    }
}
