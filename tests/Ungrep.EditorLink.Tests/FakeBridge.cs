using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Ungrep.EditorLink.Tests;

/// <summary>
/// A stand-in for the editor bridge: a loopback HTTP/1.1 server (one request per connection)
/// answering from <see cref="Handle"/>, and a project folder whose `bridge.json` points at it.
/// </summary>
internal sealed class FakeBridge : IDisposable
{
    public const string Token = "0123456789abcdef0123456789abcdef";

    private readonly TcpListener _listener;

    public FakeBridge()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        new Thread(Accept) { IsBackground = true }.Start();
    }

    public int Port { get; }

    /// <summary>Path and query in, status and body out.</summary>
    public Func<string, (int Status, string Body)> Handle { get; set; } = _ => (404, "{\"error\":\"unknown request\"}");

    public ConcurrentQueue<string> Requests { get; } = new();

    public ConcurrentQueue<string?> Tokens { get; } = new();

    /// <summary>The bodies of `POST` requests, in order.</summary>
    public ConcurrentQueue<byte[]> Bodies { get; } = new();

    /// <summary>Writes `bridge.json` for this bridge into <paramref name="projectRoot"/>.</summary>
    public void Publish(string projectRoot, string bridge = "0.1.0", int pid = 4242, string token = Token)
    {
        string path = BridgeClient.DiscoveryPath(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"bridge\":\"" + bridge + "\",\"port\":" + Port.ToString(CultureInfo.InvariantCulture)
            + ",\"token\":\"" + token + "\",\"pid\":" + pid.ToString(CultureInfo.InvariantCulture) + ",\"unity\":\"6000.3.14f1\"}");
    }

    public static string Status(string state, int compile, int errors = 0) =>
        "{\"state\":\"" + state + "\",\"unity\":\"6000.3.14f1\",\"bridge\":\"0.1.0\",\"compile\":" + compile.ToString(CultureInfo.InvariantCulture)
        + ",\"errors\":" + errors.ToString(CultureInfo.InvariantCulture) + ",\"warnings\":0,\"console\":7}";

    public void Dispose() => _listener.Stop();

    private void Accept()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = _listener.AcceptTcpClient();
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(_ => Serve(client));
        }
    }

    private void Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                var head = new StringBuilder();
                var buffer = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && stream.Read(buffer, 0, 1) == 1)
                {
                    head.Append((char)buffer[0]);
                }

                string[] lines = head.ToString().Split("\r\n");
                string target = lines[0].Split(' ')[1];
                Requests.Enqueue(target);
                Tokens.Enqueue(lines.FirstOrDefault(l => l.StartsWith("X-UG-Token:", StringComparison.OrdinalIgnoreCase))?["X-UG-Token:".Length..].Trim());
                if (lines.FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) is { } length)
                {
                    var body = new byte[int.Parse(length["Content-Length:".Length..].Trim(), CultureInfo.InvariantCulture)];
                    for (int read = 0; read < body.Length;)
                    {
                        int got = stream.Read(body, read, body.Length - read);
                        read += got > 0 ? got : throw new IOException("the body ended early");
                    }

                    Bodies.Enqueue(body);
                }

                (int status, string answer) = Handle(target);
                byte[] bytes = Encoding.UTF8.GetBytes(answer);
                string response = "HTTP/1.1 " + status.ToString(CultureInfo.InvariantCulture) + " X\r\nContent-Type: application/json\r\nContent-Length: "
                    + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\nConnection: close\r\n\r\n";
                stream.Write(Encoding.ASCII.GetBytes(response));
                stream.Write(bytes);
            }
            catch (IOException)
            {
                // The client gave up (a timeout test).
            }
        }
    }
}
