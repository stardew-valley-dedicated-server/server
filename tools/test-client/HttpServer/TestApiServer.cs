using System.Net;
using System.Text;
using System.Text.Json;
using StardewModdingAPI;

namespace JunimoTestClient.HttpServer;

/// <summary>
/// Simple HTTP server for test automation API.
/// </summary>
public class TestApiServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly IMonitor _monitor;
    private readonly CancellationTokenSource _cts;
    private readonly Dictionary<string, Func<HttpListenerRequest, object?>> _getRoutes;
    private readonly Dictionary<string, Func<HttpListenerRequest, object?>> _postRoutes;
    private Task? _listenTask;

    public int Port { get; }

    public TestApiServer(int port, IMonitor monitor)
    {
        Port = port;
        _monitor = monitor;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _cts = new CancellationTokenSource();
        _getRoutes = new Dictionary<string, Func<HttpListenerRequest, object?>>();
        _postRoutes = new Dictionary<string, Func<HttpListenerRequest, object?>>();
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
    /// Register a GET endpoint that returns raw content (not JSON-wrapped).
    /// </summary>
    public void GetRaw(string path, Func<HttpListenerRequest, (string content, string contentType)> handler)
    {
        _rawGetRoutes[path.TrimStart('/')] = handler;
    }

    private readonly Dictionary<string, Func<HttpListenerRequest, (string content, string contentType)>> _rawGetRoutes = new();

    /// <summary>
    /// Start listening for HTTP requests.
    /// </summary>
    public void Start()
    {
        try
        {
            _listener.Start();
            _listenTask = ListenAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to start HTTP server: {ex.Message}", LogLevel.Error);
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleRequest(context), ct);
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
                _monitor.Log($"Error accepting request: {ex.Message}", LogLevel.Error);
            }
        }
    }

    private void HandleRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var path = request.Url?.AbsolutePath.TrimStart('/') ?? "";

        _monitor.Log($"{request.HttpMethod} /{path}", LogLevel.Trace);

        try
        {
            // Check raw routes first (for OpenAPI, etc.)
            if (request.HttpMethod == "GET" && _rawGetRoutes.TryGetValue(path, out var rawHandler))
            {
                var (content, contentType) = rawHandler(request);
                SendRaw(response, content, contentType);
                return;
            }

            object? result = null;
            var found = false;

            if (request.HttpMethod == "GET" && _getRoutes.TryGetValue(path, out var getHandler))
            {
                result = getHandler(request);
                found = true;
            }
            else if (request.HttpMethod == "POST" && _postRoutes.TryGetValue(path, out var postHandler))
            {
                result = postHandler(request);
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
            _monitor.Log($"Error handling {request.HttpMethod} /{path}: {ex.Message}", LogLevel.Error);
            SendJson(response, new { error = ex.Message }, 500);
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
        _monitor.Log("Test API server stopped", LogLevel.Info);
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
        _listener.Close();
    }
}
