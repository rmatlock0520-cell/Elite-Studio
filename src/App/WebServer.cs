using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace EliteStudio.App;

/// <summary>One request to the local server, already read. Handlers never touch the network.</summary>
public sealed record WebRequest(string Method, string Path, IReadOnlyDictionary<string, string> Query, string Body)
{
    /// <summary>The JSON body as a class; a blank body gives that class's defaults.</summary>
    public T Read<T>() where T : new() =>
        string.IsNullOrWhiteSpace(Body) ? new T() : JsonSerializer.Deserialize<T>(Body, Settings.Json) ?? new T();
}

/// <summary>What a handler sends back.</summary>
public sealed record WebResponse(int Status, string ContentType, byte[] Body)
{
    public static WebResponse Json(object value, int status = 200) =>
        new(status, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(value, Settings.Json));

    public static WebResponse Text(string text, int status = 200) =>
        new(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));

    /// <summary>The error shape every page understands: {"ok":false,"error":"..."}.</summary>
    public static WebResponse Error(string message, int status = 500) => Json(new { ok = false, error = message }, status);
}

/// <summary>
/// The local web server every desktop page talks to. Answers this PC only.
///
/// Rules (the pages rely on these):
/// - Pages open at http://localhost:PORT/ (on Linux http://127.0.0.1:PORT/, see <see cref="UrlFor"/>). Requests that
///   do not come from this PC itself, or whose Host header is not localhost:PORT or 127.0.0.1:PORT, get 403.
/// - GET of anything outside /api/ serves a file from the web root. "/" and folder paths serve the
///   index.html inside. In .html files the text __STUDIO_TOKEN__ is replaced with <see cref="Token"/>.
///   Paths may not leave the web root. Responses are never cached (Cache-Control: no-store).
/// - /api/ping needs no token and answers {"app":"elite-studio","version":"...","features":{...}}, the features
///   being what this build has (<see cref="StudioFeatures"/>).
/// - Every other /api/ route needs the token: header X-Studio-Token, or query "t" (EventSource
///   cannot send headers). Wrong or missing token: 401 with the error shape. POST bodies must be
///   application/json, else 400.
/// - A handler that throws gives 500 with the error shape, and the problem is logged.
///   An unknown /api/ route gives 404 with the error shape.
/// - GET /api/events is a server-sent-events stream; <see cref="Send"/> pushes to every open stream.
/// - Handlers run on the thread that called <see cref="Start"/> when that thread has a message loop
///   (the app's UI thread, so they may touch game state and windows directly), and on the thread
///   pool otherwise (tests).
/// </summary>
public sealed class WebServer(string webRoot) : IDisposable
{
    /// <summary>Only these kinds of file are served; anything else in the web root answers 404.</summary>
    static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".txt"] = "text/plain; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".ico"] = "image/x-icon",
        // The look-alike fonts the Linux build carries for the Windows fonts the pages name first.
        [".ttf"] = "font/ttf",
        [".woff2"] = "font/woff2",
    };

    // An event ends at a blank line, so its JSON has to stay on one line.
    static readonly JsonSerializerOptions EventJson = new(Settings.Json) { WriteIndented = false };

    /// <summary>Events a page may fall behind by before its stream is dropped (it reconnects by itself).</summary>
    const int EventBacklog = 500;

    readonly string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(webRoot));
    readonly Dictionary<string, Func<WebRequest, Task<WebResponse>>> routes = [];
    readonly List<Channel<byte[]>> eventStreams = [];
    HttpListener? listener;

    public int Port { get; private set; }
    public string Token { get; } = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
    public string Url => UrlFor(Port);

    /// <summary>
    /// The address pages open at. Off Windows it is 127.0.0.1, not "localhost": .NET's listener there answers a name
    /// only on the one address it took for it (::1 or 127.0.0.1), while a browser looks the name up itself and may
    /// reach the other, where it would be told "not found".
    /// </summary>
    public static string UrlFor(int port) => OperatingSystem.IsWindows() ? $"http://localhost:{port}/" : $"http://127.0.0.1:{port}/";

    /// <summary>Registers a handler for an exact path, e.g. Map("GET", "/api/state", ...).</summary>
    public void Map(string method, string path, Func<WebRequest, Task<WebResponse>> handler) => routes[$"{method} {path}"] = handler;

    public void Map(string method, string path, Func<WebRequest, WebResponse> handler) =>
        Map(method, path, request => Task.FromResult(handler(request)));

    /// <summary>Starts listening on the first free port from firstPort to firstPort + 9. False if none was free.</summary>
    public bool Start(int firstPort)
    {
        for (int port = firstPort; port < firstPort + 10; port++)
        {
            var candidate = new HttpListener();
            // Both spellings of "this PC". Neither needs administrator rights (checked on Windows 10). Off Windows only
            // 127.0.0.1 (see UrlFor): "localhost" would also take ::1, which a Linux without IPv6 cannot listen on.
            if (OperatingSystem.IsWindows()) candidate.Prefixes.Add($"http://localhost:{port}/");
            candidate.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { candidate.Start(); }
            catch (HttpListenerException) { candidate.Close(); continue; }   // port taken: try the next
            listener = candidate;
            Port = port;
            // Not awaited: the loop carries on in the background. Its awaits come back to the thread
            // that called Start when that thread has a message loop (the app's UI thread), so
            // handlers run there; without one (tests) they run on the thread pool.
            _ = Accept(candidate);
            return true;
        }
        return false;
    }

    /// <summary>Routes a request without the network: static files and /api/ handlers, no Host or token checks. For tests.</summary>
    public async Task<WebResponse> Handle(WebRequest request)
    {
        if (!request.Path.StartsWith("/api/", StringComparison.Ordinal))
            return request.Method == "GET" ? await StaticFile(request.Path) : WebResponse.Text("Not found", 404);
        if (request.Path == "/api/ping") return WebResponse.Json(new { app = "elite-studio", version = Program.Version, features = Program.Features });
        if (!routes.TryGetValue($"{request.Method} {request.Path}", out var handler)) return WebResponse.Error("Unknown request.", 404);
        try
        {
            return await handler(request);
        }
        catch (Exception problem)
        {
            Log.Error($"{request.Method} {request.Path} failed", problem);
            return WebResponse.Error(problem.Message + Hint(problem.Message));
        }
    }

    /// <summary>Pushes one event to every page listening on /api/events: "event: name" + "data: json".</summary>
    public void Send(string eventName, object data)
    {
        lock (eventStreams)
        {
            if (eventStreams.Count == 0) return;
            byte[] message = Encoding.UTF8.GetBytes($"event: {eventName}\ndata: {JsonSerializer.Serialize(data, EventJson)}\n\n");
            // A full queue means that page stopped reading: end its stream instead of piling up memory.
            foreach (var stream in eventStreams)
                if (!stream.Writer.TryWrite(message)) stream.Writer.TryComplete();
        }
    }

    public void Dispose()
    {
        HttpListener? open = listener;
        listener = null;
        lock (eventStreams)
            foreach (var stream in eventStreams) stream.Writer.TryComplete();
        if (open is null) return;
        open.Stop();
        open.Close();
    }

    async Task Accept(HttpListener open)
    {
        while (true)
        {
            HttpListenerContext context;
            try { context = await open.GetContextAsync(); }
            catch (Exception stopped) when (stopped is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                if (listener == open) Log.Error("The local web server stopped by itself", stopped);
                return;
            }
            _ = Serve(context);   // not awaited, so a slow request never holds up the next one
        }
    }

    /// <summary>One request off the network: the checks a browser could get wrong, then the same routing as <see cref="Handle"/>.</summary>
    async Task Serve(HttpListenerContext context)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;
        try
        {
            // Windows listens for these addresses on every network card, so another computer could call
            // in and simply claim the right Host. Only a caller on this PC itself is answered.
            // Another website can also point its own name at this PC; a browser cannot fake the Host header.
            string host = request.Headers["Host"] ?? "";
            if (!IPAddress.IsLoopback(request.RemoteEndPoint.Address) || (host != $"localhost:{Port}" && host != $"127.0.0.1:{Port}"))
            {
                await Write(response, WebResponse.Text("Forbidden", 403));
                return;
            }

            string path = request.Url?.AbsolutePath ?? "/";
            Dictionary<string, string> query = ParseQuery(request.Url?.Query ?? "");
            if (path.StartsWith("/api/", StringComparison.Ordinal) && path != "/api/ping")
            {
                // The token changes every start, so a page left open from an earlier run is told to reload.
                if ((request.Headers["X-Studio-Token"] ?? query.GetValueOrDefault("t")) != Token)
                {
                    await Write(response, WebResponse.Error("Elite Studio was restarted. Please reload this page.", 401));
                    return;
                }
                // A form on another website cannot send this content type, so it cannot post here.
                if (request.HttpMethod == "POST" && !(request.ContentType ?? "").StartsWith("application/json", StringComparison.Ordinal))
                {
                    await Write(response, WebResponse.Error("Bad request.", 400));
                    return;
                }
                if (request.HttpMethod == "GET" && path == "/api/events") { await StreamEvents(response); return; }
            }

            string body;
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8)) body = await reader.ReadToEndAsync();
            await Write(response, await Handle(new WebRequest(request.HttpMethod, path, query, body)));
        }
        catch (Exception problem)
        {
            // A page going away mid-answer (HttpListenerException) and Studio closing are both normal.
            if (problem is not HttpListenerException && listener is not null)
                Log.Error($"{request.HttpMethod} {request.Url?.AbsolutePath} could not be answered", problem);
            response.Abort();
        }
    }

    static async Task Write(HttpListenerResponse response, WebResponse reply)
    {
        response.StatusCode = reply.Status;
        response.ContentType = reply.ContentType;
        NeverCacheOrGuess(response);
        response.ContentLength64 = reply.Body.Length;
        await response.OutputStream.WriteAsync(reply.Body);
        response.Close();
    }

    static void NeverCacheOrGuess(HttpListenerResponse response)
    {
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["X-Content-Type-Options"] = "nosniff";
    }

    /// <summary>Keeps one /api/events answer open and writes each <see cref="Send"/> message to it until the page goes away.</summary>
    async Task StreamEvents(HttpListenerResponse response)
    {
        var stream = Channel.CreateBounded<byte[]>(EventBacklog);
        lock (eventStreams) eventStreams.Add(stream);
        try
        {
            response.ContentType = "text/event-stream";
            NeverCacheOrGuess(response);
            response.SendChunked = true;
            // A first line straight away, so the page knows the stream is open before any event happens.
            await response.OutputStream.WriteAsync(": connected\n\n"u8.ToArray());
            await response.OutputStream.FlushAsync();
            // From here on the writing happens on the thread pool, off the thread that calls Send.
            await foreach (byte[] message in stream.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await response.OutputStream.WriteAsync(message).ConfigureAwait(false);
                await response.OutputStream.FlushAsync().ConfigureAwait(false);
            }
            response.Close();
        }
        finally
        {
            lock (eventStreams) eventStreams.Remove(stream);
        }
    }

    async Task<WebResponse> StaticFile(string urlPath)
    {
        string relative = Uri.UnescapeDataString(urlPath).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        if (relative.Contains("..") || relative.Contains(':') || relative.Contains('\0') || relative.StartsWith('\\'))
            return WebResponse.Text("Bad path", 400);
        if (relative.Length == 0 || relative.EndsWith(Path.DirectorySeparatorChar)) relative += "index.html";
        string file = Path.GetFullPath(Path.Combine(root, relative));
        if (!file.StartsWith(root + Path.DirectorySeparatorChar, Paths.FileNameCase)) return WebResponse.Text("Forbidden", 403);
        if (!ContentTypes.TryGetValue(Path.GetExtension(file), out string? contentType) || !File.Exists(file))
            return WebResponse.Text("Not found", 404);
        if (!contentType.StartsWith("text/html", StringComparison.Ordinal)) return new WebResponse(200, contentType, await File.ReadAllBytesAsync(file));
        // This is how a page learns the token: only a page served from here can call the /api/ routes.
        string page = await File.ReadAllTextAsync(file);
        return new WebResponse(200, contentType, Encoding.UTF8.GetBytes(page.Replace("__STUDIO_TOKEN__", Token)));
    }

    /// <summary>The part of a URL after "?", as names and values. The first value of a repeated name wins.</summary>
    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>();
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            values.TryAdd(Decode(parts[0]), parts.Length == 2 ? Decode(parts[1]) : "");
        }
        return values;

        static string Decode(string text) => Uri.UnescapeDataString(text.Replace('+', ' '));
    }

    /// <summary>The system's wording for the two usual file problems does not say what to do, so say it.</summary>
    static string Hint(string message)
    {
        if (message.Contains("denied", StringComparison.OrdinalIgnoreCase))
            return OperatingSystem.IsWindows()
                ? " Windows or your antivirus is blocking Elite Studio from changing this file. If Elite is open, close it and try again."
                : " Your system is blocking Elite Studio from changing this file. If Elite is open, close it and try again.";
        if (message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
            return " Another program (usually Elite) has this file open. Close it and try again.";
        return "";
    }
}
