using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Ungrep.Bridge
{
    /// <summary>A reply: HTTP status and a JSON body.</summary>
    internal sealed class Reply
    {
        public int Status;
        public string Body;

        /// <summary>The lines of an editor-ops answer, so a later step can add to them; null for other replies.</summary>
        public IReadOnlyList<string> Lines;

        public static Reply Ok(string json) => new Reply { Status = 200, Body = json };

        public static Reply Fail(int status, string error) => new Reply { Status = status, Body = new Json().Object().Field("error", error).EndObject().ToString() };
    }

    /// <summary>A request as the router sees it: path, query parameters and, for `POST /eval`, the body.</summary>
    internal sealed class Request
    {
        public string Path;
        public byte[] Body;
        public Dictionary<string, string> Query = new Dictionary<string, string>(StringComparer.Ordinal);

        public string Get(string name) => Query.TryGetValue(name, out string value) ? value : null;

        public static Request From(Uri url)
        {
            var request = new Request { Path = url.AbsolutePath };
            string query = url.Query.TrimStart('?');
            foreach (string pair in query.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                string name = Uri.UnescapeDataString((eq < 0 ? pair : pair.Substring(0, eq)).Replace('+', ' '));
                string value = eq < 0 ? "" : Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                request.Query[name] = value;
            }

            return request;
        }
    }

    /// <summary>
    /// Loopback HTTP listener. The listener thread checks access, answers `/status` from a
    /// snapshot, reads the body of `POST /eval`, and queues everything else for the main thread
    /// (<see cref="Pump"/>).
    /// </summary>
    internal sealed class Server
    {
        /// <summary>The largest `POST /eval` body: an assembly and its PDB.</summary>
        public const int MaxBody = 16 * 1024 * 1024;

        private const int MaxQueued = 64;

        private readonly string _token;
        private readonly Func<string> _status;
        private readonly ConcurrentQueue<KeyValuePair<HttpListenerContext, byte[]>> _queue = new ConcurrentQueue<KeyValuePair<HttpListenerContext, byte[]>>();
        private readonly List<KeyValuePair<HttpListenerContext, Func<Reply>>> _waiting = new List<KeyValuePair<HttpListenerContext, Func<Reply>>>();
        private HttpListener _listener;
        private Thread _thread;

        public Server(string token, Func<string> status)
        {
            _token = token;
            _status = status;
        }

        public int Port { get; private set; }

        /// <summary>Starts on <paramref name="preferredPort"/> when it is free, else on a port the OS picks.</summary>
        public void Start(int preferredPort)
        {
            Exception last = null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                int port = attempt == 0 && preferredPort > 0 ? preferredPort : FreePort();
                var listener = new HttpListener();
                listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                try
                {
                    listener.Start();
                }
                catch (Exception e) when (e is HttpListenerException || e is SocketException || e is InvalidOperationException)
                {
                    last = e;
                    listener.Close();
                    continue;
                }

                _listener = listener;
                Port = port;
                _thread = new Thread(Listen) { IsBackground = true, Name = "ungrep bridge" };
                _thread.Start();
                return;
            }

            throw new InvalidOperationException("no free loopback port for the bridge", last);
        }

        public void Stop()
        {
            HttpListener listener = _listener;
            _listener = null;
            if (listener == null)
            {
                return;
            }

            Reply stopping = Reply.Fail(503, "the bridge is stopping (domain reload or quit)");
            while (_queue.TryDequeue(out KeyValuePair<HttpListenerContext, byte[]> queued))
            {
                Send(queued.Key, stopping);
            }

            foreach (KeyValuePair<HttpListenerContext, Func<Reply>> waiting in _waiting)
            {
                Send(waiting.Key, stopping);
            }

            _waiting.Clear();

            try
            {
                listener.Close();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>
        /// Answers queued requests on the main thread. A handler returns a poll that gives null
        /// until its answer is ready (a refresh waits a few editor updates); polls run once per call.
        /// </summary>
        public void Pump(Func<Request, Func<Reply>> handle)
        {
            while (_queue.TryDequeue(out KeyValuePair<HttpListenerContext, byte[]> queued))
            {
                HttpListenerContext context = queued.Key;
                Func<Reply> poll;
                try
                {
                    Request request = Request.From(context.Request.Url);
                    request.Body = queued.Value;
                    poll = handle(request);
                }
                catch (Exception e)
                {
                    Reply failed = Reply.Fail(500, e.GetType().Name + ": " + e.Message);
                    poll = () => failed;
                }

                _waiting.Add(new KeyValuePair<HttpListenerContext, Func<Reply>>(context, poll));
            }

            for (int i = _waiting.Count - 1; i >= 0; i--)
            {
                Reply reply;
                try
                {
                    reply = _waiting[i].Value();
                }
                catch (Exception e)
                {
                    reply = Reply.Fail(500, e.GetType().Name + ": " + e.Message);
                }

                if (reply != null)
                {
                    Send(_waiting[i].Key, reply);
                    _waiting.RemoveAt(i);
                }
            }
        }

        public static void Send(HttpListenerContext context, Reply reply)
        {
            try
            {
                byte[] body = Encoding.UTF8.GetBytes(reply.Body);
                context.Response.StatusCode = reply.Status;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength64 = body.Length;
                context.Response.OutputStream.Write(body, 0, body.Length);
                context.Response.OutputStream.Close();
            }
            catch (Exception e) when (e is HttpListenerException || e is ObjectDisposedException || e is InvalidOperationException || e is IOException)
            {
                // The client went away.
            }
        }

        /// <summary>Access rules: the token must match and browsers (an `Origin` header) are refused.</summary>
        public static Reply Check(string token, string sentToken, string origin)
        {
            if (!string.IsNullOrEmpty(origin))
            {
                return Reply.Fail(403, "browser requests are refused");
            }

            return sentToken == token ? null : Reply.Fail(401, "missing or wrong X-UG-Token");
        }

        private void Listen()
        {
            while (true)
            {
                HttpListener listener = _listener;
                if (listener == null)
                {
                    return;
                }

                HttpListenerContext context;
                try
                {
                    context = listener.GetContext();
                }
                catch (Exception e) when (e is HttpListenerException || e is ObjectDisposedException || e is InvalidOperationException)
                {
                    return;
                }

                Reply refused = Check(_token, context.Request.Headers["X-UG-Token"], context.Request.Headers["Origin"])
                    ?? Method(context.Request.HttpMethod, context.Request.Url.AbsolutePath);
                if (refused != null)
                {
                    Send(context, refused);
                }
                else if (context.Request.Url.AbsolutePath == "/status")
                {
                    Send(context, Reply.Ok(_status()));
                }
                else if (_queue.Count >= MaxQueued)
                {
                    Send(context, Reply.Fail(503, "too many queued requests"));
                }
                else if (context.Request.HttpMethod == "GET")
                {
                    _queue.Enqueue(new KeyValuePair<HttpListenerContext, byte[]>(context, null));
                }
                else
                {
                    Queue(context);
                }
            }
        }

        /// <summary>`GET` for every request, `POST` only for `/eval`; null when allowed, else the `405`.</summary>
        public static Reply Method(string method, string path) =>
            method == "GET" || (method == "POST" && path == "/eval") ? null : Reply.Fail(405, "GET only (POST for /eval)");

        /// <summary>The whole body, or null when it is longer than <paramref name="max"/> bytes.</summary>
        public static byte[] ReadBody(Stream stream, long length, int max)
        {
            if (length > max)
            {
                return null;
            }

            using (var body = new MemoryStream(length > 0 ? (int)length : 0))
            {
                var chunk = new byte[81920];
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (body.Length + read > max)
                    {
                        return null;
                    }

                    body.Write(chunk, 0, read);
                }

                return body.ToArray();
            }
        }

        // A POST's body is read here, off the main thread; one over MaxBody is refused unread.
        private void Queue(HttpListenerContext context)
        {
            byte[] body;
            try
            {
                body = ReadBody(context.Request.InputStream, context.Request.ContentLength64, MaxBody);
            }
            catch (Exception e) when (e is HttpListenerException || e is ObjectDisposedException || e is IOException)
            {
                return;
            }

            if (body == null)
            {
                Send(context, Reply.Fail(413, "the body is over " + (MaxBody / (1024 * 1024)) + " MB"));
                return;
            }

            _queue.Enqueue(new KeyValuePair<HttpListenerContext, byte[]>(context, body));
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }
}
