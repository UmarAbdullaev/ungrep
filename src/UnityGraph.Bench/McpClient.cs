using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UnityGraph.Bench;

/// <summary>
/// A minimal MCP client over stdio (newline-delimited JSON-RPC 2.0): `initialize`,
/// `tools/list` and `tools/call`, which is all a step needs. The server's own requests (roots,
/// sampling) are answered with "method not found"; its notifications are skipped.
/// </summary>
public sealed class McpClient : IMcpSession
{
    public const string ProtocolVersion = "2025-06-18";

    private static readonly TimeSpan _startTimeout = TimeSpan.FromSeconds(60);

    private readonly TextWriter _input;
    private readonly TextReader _output;
    private readonly Process? _process;
    private readonly object _lock = new();
    private int _nextId = 1;
    private Task<string?>? _pendingLine;
    private IReadOnlyList<(string Name, string Json)>? _tools;

    /// <summary>Talks to a started server process; its stderr is drained so it never blocks.</summary>
    public McpClient(Process process)
        : this(process.StandardInput, process.StandardOutput, process)
    {
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
    }

    /// <summary>Talks over any pair of streams; tests use pipes to an in-process server.</summary>
    public McpClient(TextWriter input, TextReader output, Process? process = null)
    {
        _input = input;
        _output = output;
        _process = process;
        JsonNode result = Request("initialize", new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "ug-bench", ["version"] = "1" },
        }, _startTimeout);
        _ = result;
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
    }

    public CallAnswer Call(string tool, string argumentsJson, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            JsonNode result = Request("tools/call", new JsonObject
            {
                ["name"] = tool,
                ["arguments"] = JsonNode.Parse(argumentsJson),
            }, timeout);
            bool isError = result["isError"]?.GetValue<bool>() ?? false;
            return new CallAnswer(Text(result), isError ? 1 : 0, watch.Elapsed);
        }
        catch (McpException e)
        {
            return new CallAnswer(e.Message, 1, watch.Elapsed);
        }
    }

    public IReadOnlyList<(string Name, string Json)> Tools()
    {
        if (_tools is not null)
        {
            return _tools;
        }

        var tools = new List<(string, string)>();
        string? cursor = null;
        do
        {
            var parameters = new JsonObject();
            if (cursor is not null)
            {
                parameters["cursor"] = cursor;
            }

            JsonNode page = Request("tools/list", parameters, _startTimeout);
            foreach (JsonNode? tool in page["tools"]?.AsArray() ?? [])
            {
                if (tool?["name"]?.GetValue<string>() is { } name)
                {
                    tools.Add((name, tool.ToJsonString()));
                }
            }

            cursor = page["nextCursor"]?.GetValue<string>();
        }
        while (cursor is not null);

        return _tools = tools;
    }

    public void Dispose()
    {
        try
        {
            _input.Close();
        }
        catch (IOException)
        {
            // The server is gone already.
        }

        if (_process is not null)
        {
            if (!_process.WaitForExit(2000))
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Ended between the wait and the kill.
                }
            }

            _process.Dispose();
        }
    }

    /// <summary>The text blocks of a tool result, joined; other blocks are named, not counted.</summary>
    private static string Text(JsonNode result)
    {
        var text = new StringBuilder();
        foreach (JsonNode? block in result["content"]?.AsArray() ?? [])
        {
            string type = block?["type"]?.GetValue<string>() ?? "";
            if (type == "text")
            {
                text.Append(block!["text"]?.GetValue<string>()).Append('\n');
            }
            else
            {
                text.Append('[').Append(type).Append("]\n");
            }
        }

        return text.ToString();
    }

    private JsonNode Request(string method, JsonObject parameters, TimeSpan timeout)
    {
        lock (_lock)
        {
            int id = _nextId++;
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
            var deadline = Stopwatch.StartNew();
            while (true)
            {
                TimeSpan left = timeout - deadline.Elapsed;
                string line = ReadLine(left > TimeSpan.Zero ? left : TimeSpan.Zero)
                    ?? throw new McpException($"MCP server closed the connection during {method}");
                JsonNode? message;
                try
                {
                    message = JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                    continue; // a stray log line on stdout
                }

                if (message is not JsonObject obj)
                {
                    continue;
                }

                if (obj["method"] is not null)
                {
                    if (obj["id"] is { } requestId)
                    {
                        Send(new JsonObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = requestId.DeepClone(),
                            ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "method not found" },
                        });
                    }

                    continue;
                }

                if (obj["id"] is not { } answerId || answerId.GetValueKind() != JsonValueKind.Number || answerId.GetValue<int>() != id)
                {
                    continue; // an answer to an earlier, timed-out request
                }

                if (obj["error"] is { } error)
                {
                    throw new McpException($"MCP error {error["code"]}: {error["message"]?.GetValue<string>()}");
                }

                return obj["result"] ?? new JsonObject();
            }
        }
    }

    private string? ReadLine(TimeSpan timeout)
    {
        _pendingLine ??= _output.ReadLineAsync();
        if (!_pendingLine.Wait(timeout))
        {
            throw new McpException($"MCP server did not answer within {timeout.TotalSeconds:0} s");
        }

        string? line = _pendingLine.Result;
        _pendingLine = null;
        return line;
    }

    private void Send(JsonObject message)
    {
        _input.Write(message.ToJsonString());
        _input.Write('\n');
        _input.Flush();
    }

}

/// <summary>A failed MCP request: the server's error, a closed pipe or a timeout.</summary>
public sealed class McpException(string message) : Exception(message);
