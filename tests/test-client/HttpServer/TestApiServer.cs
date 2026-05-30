using System.Net;
using System.Text;
using System.Text.Json;
using JunimoTestClient.Diagnostics;
using StardewModdingAPI;

namespace JunimoTestClient.HttpServer;

/// <summary>
/// Simple HTTP server for test automation API.
/// </summary>
public class TestApiServer : IDisposable
{
    private HttpListener _listener;
    private readonly IMonitor _monitor;
    private readonly CancellationTokenSource _cts;
    private readonly Dictionary<string, Func<HttpListenerRequest, object?>> _getRoutes;
    private readonly Dictionary<string, Func<HttpListenerRequest, object?>> _postRoutes;
    private readonly Dictionary<string, Func<HttpListenerRequest, object?>> _deleteRoutes;
    private Task? _listenTask;

    private const int MaxStartRetries = 3;
    private const int RetryDelayMs = 500;

    public int Port { get; }
    public bool IsListening { get; private set; }

    public TestApiServer(int port, IMonitor monitor)
    {
        Port = port;
        _monitor = monitor;
        _listener = CreateListener(port);
        _cts = new CancellationTokenSource();
        _getRoutes = new Dictionary<string, Func<HttpListenerRequest, object?>>();
        _postRoutes = new Dictionary<string, Func<HttpListenerRequest, object?>>();
        _deleteRoutes = new Dictionary<string, Func<HttpListenerRequest, object?>>();
    }

    private static HttpListener CreateListener(int port)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://+:{port}/");
        return listener;
    }

    /// <summary>
    /// Register a GET endpoint handler.
    /// </summary>
    public void Get(string path, Func<HttpListenerRequest, object?> handler)
    {
        _getRoutes[path.TrimStart('/')] = handler;
    }

    /// <summary>
    /// Register a POST endpoint handler.
    /// </summary>
    public void Post(string path, Func<HttpListenerRequest, object?> handler)
    {
        _postRoutes[path.TrimStart('/')] = handler;
    }

    /// <summary>
    /// Register a DELETE endpoint handler.
    /// </summary>
    public void Delete(string path, Func<HttpListenerRequest, object?> handler)
    {
        _deleteRoutes[path.TrimStart('/')] = handler;
    }

    /// <summary>
    /// Register a GET endpoint that returns raw content (not JSON-wrapped).
    /// </summary>
    public void GetRaw(string path, Func<HttpListenerRequest, (string content, string contentType)> handler)
    {
        _rawGetRoutes[path.TrimStart('/')] = handler;
    }

    private readonly Dictionary<string, Func<HttpListenerRequest, (string content, string contentType)>> _rawGetRoutes = new();

    /// <summary>
    /// Start listening for HTTP requests.
    /// Retries with a fresh HttpListener on failure. .NET 6 on Linux can race
    /// internally during Start(), leaving a dangling async accept callback that
    /// crashes the process if we just Close() the failed listener.
    /// </summary>
    public void Start()
    {
        for (var attempt = 1; attempt <= MaxStartRetries; attempt++)
        {
            try
            {
                _listener.Start();
                _listenTask = ListenAsync(_cts.Token);
                IsListening = true;
                return;
            }
            catch (Exception ex)
            {
                _monitor.Log(
                    $"Failed to start HTTP server (attempt {attempt}/{MaxStartRetries}): {ex.Message}",
                    LogLevel.Error);

                // Close the failed listener and create a fresh instance.
                // The old listener's internal state is corrupted; reusing it
                // won't help, and its dangling async accept callback will fire
                // harmlessly against the closed socket.
                try { _listener.Close(); } catch { /* best effort */ }

                if (attempt < MaxStartRetries)
                {
                    Thread.Sleep(RetryDelayMs);
                    _listener = CreateListener(Port);
                }
            }
        }

        _monitor.Log("HTTP server failed to start after all retries", LogLevel.Error);
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleRequest(context));
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                // Expected when stopping
            }
            catch (ObjectDisposedException)
            {
                // Listener was disposed
                break;
            }
            catch (Exception ex)
            {
                // The accept loop continues after this — the listener is still live.
                // LogLevel.Error would trip ServerContainer's \b(ERROR|FATAL)\b regex
                // and silently fail unrelated tests; keep this at Warn.
                _monitor.Log($"Error accepting request: {ex.Message}", LogLevel.Warn);
            }
        }
    }

    // High-frequency polling endpoints that should not be logged
    private static readonly HashSet<string> QuietEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        "chat/history"
    };

    private void HandleRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var method = request.HttpMethod;
        var path = request.Url?.AbsolutePath.TrimStart('/') ?? "";

        // Bind the inbound correlation id so ClientEventLog emissions inside
        // the handler carry the same requestId used by the harness and the
        // server mod. Echo the id on the response for round-trip confirmation.
        var requestId = request.Headers["X-Request-Id"];
        if (!string.IsNullOrEmpty(requestId))
        {
            try { response.Headers["X-Request-Id"] = requestId; }
            catch { /* best effort — never fail a request to echo a header */ }
        }

        using var _correlationScope = ClientRequestContext.Bind(requestId);

        // Per-request stopwatch fed into the http_served event in the finally
        // block. Captured here so early-return paths still emit a duration.
        var servedStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var isQuiet = QuietEndpoints.Contains(path);

        try
        {
            // Check raw routes first (for OpenAPI, etc.)
            if (method == "GET" && _rawGetRoutes.TryGetValue(path, out var rawHandler))
            {
                var (content, contentType) = rawHandler(request);
                SendRaw(response, content, contentType);
                return;
            }

            object? result = null;
            var found = false;

            if (method == "GET" && _getRoutes.TryGetValue(path, out var getHandler))
            {
                result = getHandler(request);
                found = true;
            }
            else if (method == "POST" && _postRoutes.TryGetValue(path, out var postHandler))
            {
                result = postHandler(request);
                found = true;
            }
            else if (method == "DELETE" && _deleteRoutes.TryGetValue(path, out var deleteHandler))
            {
                result = deleteHandler(request);
                found = true;
            }

            if (!found)
            {
                SendJson(response, new { error = "Not found", path }, 404);
                return;
            }

            SendJson(response, result ?? new { ok = true });
        }
        catch (Exception ex)
        {
            _monitor.Log($"Error handling {method} /{path}: {ex.Message}", LogLevel.Error);
            SendJson(response, new { error = ex.Message }, 500);
        }
        finally
        {
            // Client-side mirror of server-mod's http_served (see
            // ApiService.cs:1584-1604). Carries the same requestId as the
            // test-harness http_request event, making cross-side correlation
            // programmatic. QuietEndpoints (currently /chat/history) are
            // skipped to keep the event log bounded under high-frequency
            // polling. Exceptions in the emit path are swallowed — never let
            // instrumentation fail a request close.
            if (!isQuiet)
            {
                try
                {
                    servedStopwatch.Stop();
                    int statusCode;
                    try { statusCode = response.StatusCode; }
                    catch { statusCode = 0; } // response already closed/disposed
                    ClientEventLog.Emit("http_served", new
                    {
                        method,
                        path = "/" + path,
                        status = statusCode,
                        durationMs = servedStopwatch.ElapsedMilliseconds
                    });
                }
                catch { /* never let instrumentation fail a request close */ }
            }
        }
    }

    private static void SendJson(HttpListenerResponse response, object data, int statusCode = 200)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        response.Headers.Add("Access-Control-Allow-Origin", "*");

        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        var bytes = Encoding.UTF8.GetBytes(json);

        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
        response.OutputStream.Close();
    }

    private static void SendRaw(HttpListenerResponse response, string content, string contentType, int statusCode = 200)
    {
        response.StatusCode = statusCode;
        response.ContentType = contentType;
        response.Headers.Add("Access-Control-Allow-Origin", "*");

        var bytes = Encoding.UTF8.GetBytes(content);
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
        response.OutputStream.Close();
    }

    /// <summary>
    /// Read JSON body from POST request.
    /// </summary>
    public static T? ReadBody<T>(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        var body = reader.ReadToEnd();
        return JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    public void Stop()
    {
        _cts.Cancel();
        _listener.Stop();

        // Await the listen task so its exceptions are observed
        if (_listenTask != null)
        {
            try { _listenTask.GetAwaiter().GetResult(); }
            catch { /* expected; listener was stopped */ }
            _listenTask = null;
        }

        _monitor.Log("Test API server stopped", LogLevel.Info);
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
        _listener.Close();
    }
}
