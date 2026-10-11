using System.IO.Pipes;
using System.Text.Json.Nodes;

namespace UnityGraph.Bench.Tests;

/// <summary>
/// The stdio MCP client against an in-process server over pipes: initialize, tool calls (text
/// blocks joined, other blocks named, errors as exit 1), the server's own requests answered with
/// "method not found", stray lines skipped, `tools/list` paged and cached, and a timed-out call's
/// late answer not taken for the next call's.
/// </summary>
public sealed class McpClientTests : IDisposable
{
    private readonly AnonymousPipeServerStream _toServer = new(PipeDirection.In);
    private readonly AnonymousPipeServerStream _toClient = new(PipeDirection.Out);
    private readonly List<string> _received = [];
    private readonly Task _server;
    private readonly McpClient _client;
    private int _listCalls;

    public McpClientTests()
    {
        var clientOut = new AnonymousPipeClientStream(PipeDirection.Out, _toServer.ClientSafePipeHandle);
        var clientIn = new AnonymousPipeClientStream(PipeDirection.In, _toClient.ClientSafePipeHandle);
        _server = Task.Run(Serve);
        _client = new McpClient(new StreamWriter(clientOut), new StreamReader(clientIn));
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Wait(TimeSpan.FromSeconds(5));
        _toServer.Dispose();
        _toClient.Dispose();
    }

    private void Serve()
    {
        using var reader = new StreamReader(_toServer);
        using var writer = new StreamWriter(_toClient) { AutoFlush = true, NewLine = "\n" };
        JsonNode? slow = null;
        while (reader.ReadLine() is { } line)
        {
            lock (_received)
            {
                _received.Add(line);
            }

            JsonNode message = JsonNode.Parse(line)!;
            JsonNode? id = message["id"]?.DeepClone();
            string? method = message["method"]?.GetValue<string>();
            JsonNode? Result(JsonNode result) => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };
            switch (method)
            {
                case "initialize":
                    writer.WriteLine(Result(new JsonObject { ["protocolVersion"] = McpClient.ProtocolVersion })!.ToJsonString());
                    break;
                case "tools/list":
                    _listCalls++;
                    writer.WriteLine((message["params"]?["cursor"] is null
                        ? Result(new JsonObject { ["tools"] = new JsonArray(new JsonObject { ["name"] = "echo" }), ["nextCursor"] = "2" })
                        : Result(new JsonObject { ["tools"] = new JsonArray(new JsonObject { ["name"] = "ask", ["inputSchema"] = new JsonObject() }) }))!.ToJsonString());
                    break;
                case "tools/call":
                    string tool = message["params"]!["name"]!.GetValue<string>();
                    if (tool == "slow")
                    {
                        slow = id;
                        break;
                    }

                    if (slow is not null)
                    {
                        writer.WriteLine(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = slow, ["result"] = Text("late") }.ToJsonString());
                        slow = null;
                    }

                    if (tool == "ask")
                    {
                        writer.WriteLine("""{"jsonrpc":"2.0","id":"s1","method":"roots/list"}""");
                        writer.WriteLine("""{"jsonrpc":"2.0","method":"notifications/message","params":{"level":"info"}}""");
                        writer.WriteLine("server log line, not JSON");
                    }

                    writer.WriteLine((tool switch
                    {
                        "boom" => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32000, ["message"] = "boom" } },
                        "fail" => Result(new JsonObject { ["isError"] = true, ["content"] = Text("bad")["content"]!.DeepClone() }),
                        _ => Result(new JsonObject
                        {
                            ["content"] = new JsonArray(
                                new JsonObject { ["type"] = "text", ["text"] = message["params"]!["arguments"]?["msg"]?.GetValue<string>() ?? tool },
                                new JsonObject { ["type"] = "image", ["data"] = "AAAA" }),
                        }),
                    })!.ToJsonString());
                    break;
            }
        }
    }

    private static JsonObject Text(string text) =>
        new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };

    [Fact]
    public void A_call_joins_text_blocks_and_names_the_others()
    {
        CallAnswer answer = _client.Call("echo", """{"msg":"hello"}""", TimeSpan.FromSeconds(5));

        Assert.Equal(("hello\n[image]\n", 0), (answer.Text, answer.Exit));
        Assert.Contains(Received(), l => l.Contains("\"method\":\"notifications/initialized\"", StringComparison.Ordinal));
    }

    // The fake server may still be adding stray lines while a test reads what it got.
    private List<string> Received()
    {
        lock (_received)
        {
            return [.. _received];
        }
    }

    [Fact]
    public void Tool_errors_and_protocol_errors_answer_with_exit_1()
    {
        CallAnswer fail = _client.Call("fail", "{}", TimeSpan.FromSeconds(5));
        CallAnswer boom = _client.Call("boom", "{}", TimeSpan.FromSeconds(5));

        Assert.Equal(("bad\n", 1), (fail.Text, fail.Exit));
        Assert.Equal(("MCP error -32000: boom", 1), (boom.Text, boom.Exit));
    }

    [Fact]
    public void The_servers_own_requests_are_refused_and_stray_lines_skipped()
    {
        CallAnswer answer = _client.Call("ask", "{}", TimeSpan.FromSeconds(5));

        Assert.Equal("ask\n[image]\n", answer.Text);
        // The refusal goes out without waiting, so the fake server may read it after the call returns.
        Assert.True(SpinWait.SpinUntil(
            () => Received().Exists(l => l.Contains("\"id\":\"s1\"", StringComparison.Ordinal) && l.Contains("-32601", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5)), "the server never got the refusal of its own request");
    }

    [Fact]
    public void The_tool_list_is_read_page_by_page_once()
    {
        IReadOnlyList<(string Name, string Json)> tools = _client.Tools();

        Assert.Equal(["echo", "ask"], tools.Select(t => t.Name));
        Assert.Equal("""{"name":"ask","inputSchema":{}}""", tools[1].Json);
        Assert.Same(tools, _client.Tools());
        Assert.Equal(2, _listCalls);
    }

    [Fact]
    public void A_late_answer_to_a_timed_out_call_is_not_taken_for_the_next()
    {
        CallAnswer timedOut = _client.Call("slow", "{}", TimeSpan.FromMilliseconds(200));
        CallAnswer next = _client.Call("echo", """{"msg":"fresh"}""", TimeSpan.FromSeconds(5));

        Assert.Equal((1, "MCP server did not answer within 0 s"), (timedOut.Exit, timedOut.Text));
        Assert.Equal("fresh\n[image]\n", next.Text);
    }
}
