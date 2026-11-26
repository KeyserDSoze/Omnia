using System.Text.Json;
using System.Text.Json.Nodes;
using Tao.Application;
using Tao.Domain;
using Xunit;

namespace Tao.Domain.Tests;

public sealed class McpServerTests
{
    private static readonly McpCaller Alice = new("tenant-1", "alice");

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static async Task<JsonObject?> Call(McpServer server, string json, McpCaller? caller = null) =>
        await server.HandleAsync(Parse(json), caller ?? Alice, default);

    private static McpServer Server(RecordingConsent? consent = null, params IMcpTool[] tools) =>
        new(tools.Length == 0 ? [new EchoTool()] : tools, consent ?? new RecordingConsent());

    [Theory]
    [InlineData("2025-06-18", "2025-06-18")]
    [InlineData("2025-03-26", "2025-03-26")]
    [InlineData("2024-11-05", "2024-11-05")]
    [InlineData("1999-01-01", "2025-06-18")]
    public async Task InitializeNegotiatesTheVersionAndOffersOnlyTools(string asked, string answered)
    {
        var response = await Call(Server(), $$$$"""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"{{{{asked}}}}","capabilities":{},"clientInfo":{"name":"c","version":"1"}}}""");
        var result = response!["result"]!.AsObject();
        Assert.Equal(answered, result["protocolVersion"]!.GetValue<string>());
        Assert.Equal(["tools"], result["capabilities"]!.AsObject().Select(p => p.Key));
        Assert.Equal("tao", result["serverInfo"]!["name"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(result["instructions"]!.GetValue<string>()));
        Assert.Equal(1, response["id"]!.GetValue<int>());
    }

    [Fact]
    public async Task PingAnswersWithAnEmptyResultAndTheIdAsSent()
    {
        var response = await Call(Server(), """{"jsonrpc":"2.0","id":"abc-1","method":"ping"}""");
        Assert.Equal("abc-1", response!["id"]!.GetValue<string>());
        Assert.Empty(response["result"]!.AsObject());
    }

    [Fact]
    public async Task NotificationsAndClientResponsesGetNoReply()
    {
        Assert.Null(await Call(Server(), """{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        Assert.Null(await Call(Server(), """{"jsonrpc":"2.0","method":"anything/else"}"""));
        Assert.Null(await Call(Server(), """{"jsonrpc":"2.0","id":7,"result":{}}"""));
        Assert.Null(await Call(Server(), """{"jsonrpc":"2.0","id":7,"error":{"code":-1,"message":"x"}}"""));
    }

    [Fact]
    public async Task ToolsAreListedWithTheirSchemaAndAsReadOnly()
    {
        var response = await Call(Server(), """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var tool = Assert.Single(response!["result"]!["tools"]!.AsArray())!.AsObject();
        Assert.Equal(("echo", "Echo", "Repeats the text."), (tool["name"]!.GetValue<string>(), tool["title"]!.GetValue<string>(), tool["description"]!.GetValue<string>()));
        Assert.Equal("object", tool["inputSchema"]!["type"]!.GetValue<string>());
        Assert.True(tool["annotations"]!["readOnlyHint"]!.GetValue<bool>());
        Assert.False(tool["annotations"]!["destructiveHint"]!.GetValue<bool>());
        Assert.False(tool["annotations"]!["openWorldHint"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ACallRunsForTheSignedCallerOnlyAfterTheConsentCheck()
    {
        var consent = new RecordingConsent();
        var response = await Call(Server(consent), """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo","arguments":{"text":"ciao"}}}""");
        var result = response!["result"]!.AsObject();
        Assert.False(result["isError"]!.GetValue<bool>());
        Assert.Equal("alice@tenant-1: ciao", result["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("ciao", result["structuredContent"]!["echo"]!.GetValue<string>());
        Assert.Equal([("tenant-1", "alice", "mcp", "mcp.access")], consent.Checks);
    }

    [Fact]
    public async Task WithoutConsentTheToolDoesNotRunAndTheAnswerIsAToolError()
    {
        var tool = new EchoTool();
        var response = await Call(Server(new RecordingConsent { Refuse = true }, tool),
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"echo","arguments":{}}}""");
        var result = response!["result"]!.AsObject();
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.StartsWith("consent_required", result["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(0, tool.Runs);
    }

    [Fact]
    public async Task AnUnknownToolIsInvalidParams()
    {
        var response = await Call(Server(), """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"delete_everything"}}""");
        Assert.Equal(-32602, response!["error"]!["code"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":3}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"echo","arguments":[1]}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":5,"method":"tools/call"}""")]
    public async Task MalformedCallParametersAreInvalidParams(string json)
    {
        var response = await Call(Server(), json);
        Assert.Equal(-32602, response!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task BadArgumentsAreAToolErrorTheModelCanReadAndFix()
    {
        var response = await Call(Server(null, new EchoTool { Throw = new ArgumentException("text_too_long") }),
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"echo","arguments":{}}}""");
        var result = response!["result"]!.AsObject();
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Equal("invalid_arguments: text_too_long", result["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnUnexpectedFailureNeverLeaksItsMessage()
    {
        var response = await Call(Server(null, new EchoTool { Throw = new InvalidOperationException("password=hunter2 at db-host-7") }),
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"echo","arguments":{}}}""");
        var result = response!["result"]!.AsObject();
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Equal("tool_failed", result["content"]![0]!["text"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("resources/list")]
    [InlineData("prompts/list")]
    [InlineData("completion/complete")]
    [InlineData("tools/delete")]
    public async Task MethodsTaoDoesNotOfferAreMethodNotFound(string method)
    {
        var response = await Call(Server(), $$"""{"jsonrpc":"2.0","id":9,"method":"{{method}}"}""");
        Assert.Equal(-32601, response!["error"]!["code"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""[{"jsonrpc":"2.0","id":1,"method":"ping"}]""")]
    [InlineData("\"ping\"")]
    [InlineData("""{"id":1,"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"1.0","id":1,"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":5}""")]
    [InlineData("""{"jsonrpc":"2.0","id":{"a":1},"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":null,"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1}""")]
    public async Task MalformedRequestsAreInvalidRequest(string json)
    {
        var response = await Call(Server(), json);
        Assert.Equal(-32600, response!["error"]!["code"]!.GetValue<int>());
        Assert.Null(response["id"]);
    }

    [Fact]
    public async Task AVeryLongToolTextIsCut()
    {
        var response = await Call(Server(null, new EchoTool { Text = new string('x', 200_000) }),
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"echo","arguments":{}}}""");
        Assert.True(response!["result"]!["content"]![0]!["text"]!.GetValue<string>().Length <= McpServer.MaxTextLength);
    }

    private sealed class RecordingConsent : IConsentGuard
    {
        public bool Refuse { get; init; }
        public List<(string Tenant, string Subject, string Source, string Purpose)> Checks { get; } = [];

        public Task EnsureActiveAsync(string tenantId, string ownerSubject, string source, string purpose, CancellationToken ct)
        {
            Checks.Add((tenantId, ownerSubject, source, purpose));
            return Refuse ? Task.FromException(new ConsentRequiredException()) : Task.CompletedTask;
        }
    }

    private sealed class EchoTool : IMcpTool
    {
        public int Runs { get; private set; }
        public Exception? Throw { get; init; }
        public string? Text { get; init; }
        public string Name => "echo";
        public string Title => "Echo";
        public string Description => "Repeats the text.";
        public JsonObject InputSchema => new() { ["type"] = "object", ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } } };

        public Task<McpToolResult> CallAsync(JsonElement arguments, McpCaller caller, CancellationToken ct)
        {
            Runs++;
            if (Throw is not null) throw Throw;
            var text = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("text", out var t) ? t.GetString() : "";
            return Task.FromResult(new McpToolResult(Text ?? $"{caller.Subject}@{caller.Tenant}: {text}", new JsonObject { ["echo"] = text }));
        }
    }
}
