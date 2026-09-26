using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Arcademia.Link
{
    internal sealed class LoopbackListener : IDisposable
    {
        private const int MaxRequestBytes = 16 * 1024;
        private readonly TcpListener _listener;
        private readonly string _path;

        public LoopbackListener(int port, string path)
        {
            _path = string.IsNullOrEmpty(path) ? "/" : path.StartsWith("/") ? path : "/" + path;
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        public int Port { get; }

        public string RedirectUri => $"http://127.0.0.1:{Port}{_path}";

        public async Task<Dictionary<string, string>> WaitForCallbackAsync(
            Func<Dictionary<string, string>, string> page,
            CancellationToken ct
        )
        {
            using (ct.Register(() => _listener.Stop()))
            {
                while (true)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is ObjectDisposedException || ex is SocketException || ex is InvalidOperationException)
                    {
                        ct.ThrowIfCancellationRequested();
                        throw;
                    }

                    using (client)
                    {
                        var stream = client.GetStream();
                        string target;
                        try
                        {
                            target = await ReadTargetAsync(stream, ct).ConfigureAwait(false);
                        }
                        catch (IOException)
                        {
                            continue;
                        }

                        if (target == null)
                            continue;

                        var queryStart = target.IndexOf('?');
                        var path = queryStart < 0 ? target : target.Substring(0, queryStart);
                        if (!string.Equals(path, _path, StringComparison.Ordinal))
                        {
                            await WriteAsync(stream, 404, "Not found", ct).ConfigureAwait(false);
                            continue;
                        }

                        var query = ParseQuery(queryStart < 0 ? "" : target.Substring(queryStart + 1));
                        await WriteAsync(stream, 200, page(query), ct).ConfigureAwait(false);
                        return query;
                    }
                }
            }
        }

        private static async Task<string> ReadTargetAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new byte[4096];
            var received = new MemoryStream();
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                while (received.Length < MaxRequestBytes)
                {
                    var read = await stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    received.Write(buffer, 0, read);
                    var text = Encoding.ASCII.GetString(received.ToArray());
                    var end = text.IndexOf("\r\n", StringComparison.Ordinal);
                    if (end < 0)
                        continue;
                    var parts = text.Substring(0, end).Split(' ');
                    return parts.Length >= 2 && parts[0] == "GET" ? parts[1] : null;
                }
            }
            return null;
        }

        private static async Task WriteAsync(NetworkStream stream, int status, string html, CancellationToken ct)
        {
            var body = Encoding.UTF8.GetBytes(html);
            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\n"
                    + "Content-Type: text/html; charset=utf-8\r\n"
                    + $"Content-Length: {body.Length}\r\n"
                    + "Cache-Control: no-store\r\n"
                    + "Connection: close\r\n\r\n"
            );
            await stream.WriteAsync(head, 0, head.Length, ct).ConfigureAwait(false);
            await stream.WriteAsync(body, 0, body.Length, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in query.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var key = Uri.UnescapeDataString((eq < 0 ? pair : pair.Substring(0, eq)).Replace('+', ' '));
                var value = eq < 0 ? "" : Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                result[key] = value;
            }
            return result;
        }

        public void Dispose()
        {
            try
            {
                _listener.Stop();
            }
            catch (SocketException) { }
        }
    }
}
