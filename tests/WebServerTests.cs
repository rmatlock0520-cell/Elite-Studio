using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EliteStudio.App;
using WebRequest = EliteStudio.App.WebRequest;     // System.Net has older types with the same names
using WebResponse = EliteStudio.App.WebResponse;

namespace EliteStudio.Tests;

/// <summary>The local web server: first its routing rules without the network, then real requests on a free port.</summary>
public sealed partial class WebServerTests : IDisposable
{
    static readonly IReadOnlyDictionary<string, string> NoQuery = new Dictionary<string, string>();

    readonly string temp = Directory.CreateTempSubdirectory("studio-web-").FullName;
    readonly string webRoot;
    readonly WebServer web;
    readonly HttpClient http = new();

    public WebServerTests()
    {
        webRoot = Path.Combine(temp, "web");
        Directory.CreateDirectory(Path.Combine(webRoot, "ui", "img"));
        File.WriteAllText(Path.Combine(webRoot, "index.html"), "<meta name=\"studio-token\" content=\"__STUDIO_TOKEN__\"><p>shell</p>");
        File.WriteAllText(Path.Combine(webRoot, "ui", "index.html"), "<p>theme editor __STUDIO_TOKEN__</p>");
        File.WriteAllText(Path.Combine(webRoot, "ui", "page.css"), "body { color: red }");
        File.WriteAllText(Path.Combine(webRoot, "ui", "notes.txt"), "__STUDIO_TOKEN__ stays as it is outside .html");
        File.WriteAllBytes(Path.Combine(webRoot, "ui", "img", "dot.png"), [0x89, 0x50, 0x4E, 0x47, 0, 1, 2, 255]);
        File.WriteAllText(Path.Combine(webRoot, "ui", "tool.exe"), "not for serving");
        File.WriteAllText(Path.Combine(temp, "secret.txt"), "outside the web root");
        web = new WebServer(webRoot);
        web.Map("GET", "/api/echo", request => WebResponse.Json(new { request.Method, request.Query, request.Body }));
        web.Map("POST", "/api/echo", async request => { await Task.Yield(); return WebResponse.Json(new { said = request.Read<Said>().Text }); });
        web.Map("GET", "/api/broken", WebResponse (_) => throw new InvalidOperationException("It broke."));
        web.Map("GET", "/api/locked", WebResponse (_) => throw new IOException("The process cannot access the file 'x.ini' because it is being used by another process."));
        web.Map("GET", "/api/blocked", WebResponse (_) => throw new UnauthorizedAccessException("Access to the path 'x.ini' is denied."));
    }

    public void Dispose()
    {
        http.Dispose();
        web.Dispose();
        Directory.Delete(temp, recursive: true);
    }

    sealed class Said { public string Text { get; set; } = ""; }

    static string Text(WebResponse response) => Encoding.UTF8.GetString(response.Body);
    static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    Task<WebResponse> Get(string path) => web.Handle(new WebRequest("GET", path, NoQuery, ""));

    // ------------------------------------------------------------ routing, no network

    [Fact]
    public async Task Mapped_routes_answer_and_get_the_query_and_body()
    {
        var query = new Dictionary<string, string> { ["name"] = "a b" };
        WebResponse got = await web.Handle(new WebRequest("GET", "/api/echo", query, ""));
        Assert.Equal(200, got.Status);
        Assert.Equal("application/json; charset=utf-8", got.ContentType);
        Assert.Equal("a b", Json(Text(got)).GetProperty("query").GetProperty("name").GetString());

        WebResponse posted = await web.Handle(new WebRequest("POST", "/api/echo", NoQuery, "{\"text\":\"hello\"}"));
        Assert.Equal("hello", Json(Text(posted)).GetProperty("said").GetString());
    }

    [Fact]
    public async Task Ping_names_the_app_and_its_version()
    {
        JsonElement ping = Json(Text(await Get("/api/ping")));
        Assert.Equal("elite-studio", ping.GetProperty("app").GetString());
        Assert.Matches(@"^\d+\.\d+\.\d+$", ping.GetProperty("version").GetString());

        // And what this build has, so the pages can hide what it has not: everything on Windows, none of these on Linux.
        JsonElement features = ping.GetProperty("features");
        bool windows = OperatingSystem.IsWindows();
        Assert.Equal(windows ? "windows" : "linux", features.GetProperty("system").GetString());
        foreach (string part in new[] { "flight", "overlay", "navScan", "reloadKey", "trayIcon" })
            Assert.Equal(windows, features.GetProperty(part).GetBoolean());
    }

    [Theory]
    [InlineData("GET", "/api/nothing-here")]
    [InlineData("POST", "/api/broken")]   // right path, wrong method
    [InlineData("GET", "/api/ECHO")]      // paths are exact
    public async Task Unknown_api_routes_give_404_in_the_error_shape(string method, string path)
    {
        WebResponse response = await web.Handle(new WebRequest(method, path, NoQuery, ""));
        Assert.Equal(404, response.Status);
        Assert.False(Json(Text(response)).GetProperty("ok").GetBoolean());
        Assert.Equal("Unknown request.", Json(Text(response)).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("/api/broken", "It broke.")]
    [InlineData("/api/locked", "The process cannot access the file 'x.ini' because it is being used by another process. Another program (usually Elite) has this file open. Close it and try again.")]
    [InlineData("/api/blocked", "Access to the path 'x.ini' is denied. Windows or your antivirus is blocking Elite Studio from changing this file. If Elite is open, close it and try again.")]
    public async Task A_handler_that_throws_gives_500_with_its_message_and_a_hint_for_file_problems(string path, string error)
    {
        // Elsewhere the hint names no Windows and no antivirus.
        if (!OperatingSystem.IsWindows()) error = error.Replace("Windows or your antivirus is blocking", "Your system is blocking");
        WebResponse response = await Get(path);
        Assert.Equal(500, response.Status);
        Assert.False(Json(Text(response)).GetProperty("ok").GetBoolean());
        Assert.Equal(error, Json(Text(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_body_that_is_not_json_gives_500_not_a_crash()
    {
        WebResponse response = await web.Handle(new WebRequest("POST", "/api/echo", NoQuery, "{not json"));
        Assert.Equal(500, response.Status);
        Assert.False(Json(Text(response)).GetProperty("ok").GetBoolean());
    }

    // ------------------------------------------------------------ static files, no network

    [Fact]
    public async Task Pages_are_served_with_the_token_filled_in()
    {
        WebResponse shell = await Get("/");
        Assert.Equal(200, shell.Status);
        Assert.Equal("text/html; charset=utf-8", shell.ContentType);
        Assert.Equal($"<meta name=\"studio-token\" content=\"{web.Token}\"><p>shell</p>", Text(shell));

        Assert.Equal(Text(shell), Text(await Get("/index.html")));
        Assert.Equal($"<p>theme editor {web.Token}</p>", Text(await Get("/ui/")));
        Assert.Matches("^[0-9a-f]{32}$", web.Token);
    }

    [Fact]
    public async Task Other_files_are_served_untouched_with_their_content_type()
    {
        WebResponse css = await Get("/ui/page.css");
        Assert.Equal("text/css; charset=utf-8", css.ContentType);
        Assert.Equal("body { color: red }", Text(css));

        WebResponse png = await Get("/ui/img/dot.png");
        Assert.Equal("image/png", png.ContentType);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 1, 2, 255 }, png.Body);

        WebResponse notes = await Get("/ui/notes.txt");
        Assert.Equal("text/plain; charset=utf-8", notes.ContentType);
        Assert.StartsWith("__STUDIO_TOKEN__", Text(notes));

        Assert.Equal(OperatingSystem.IsWindows() ? 200 : 404, (await Get("/ui/PAGE.CSS")).Status);   // Windows file names ignore case; Linux's do not
        Assert.Equal(200, (await Get("/ui/img/dot%2Epng")).Status);    // escapes are decoded
    }

    [Theory]
    [InlineData("/ui/missing.css")]
    [InlineData("/ui/tool.exe")]     // exists, but not a kind of file a page needs
    [InlineData("/ui")]              // a folder is only served through its trailing slash
    [InlineData("/ui/img/")]         // no index.html inside
    [InlineData("/api")]
    public async Task Files_that_are_missing_or_not_for_pages_give_404(string path)
    {
        WebResponse response = await Get(path);
        Assert.Equal(404, response.Status);
        Assert.Equal("Not found", Text(response));
    }

    [Theory]
    [InlineData("/../secret.txt")]
    [InlineData("/ui/../../secret.txt")]
    [InlineData("/%2e%2e/secret.txt")]
    [InlineData("/ui/..%5C..%5Csecret.txt")]
    [InlineData("/ui/%2e%2e%2f%2e%2e%2fsecret.txt")]
    [InlineData("/C:/Windows/win.ini")]
    [InlineData("/C:%5CWindows%5Cwin.ini")]
    [InlineData("//localhost/c$/Windows/win.ini")]
    [InlineData("/%5C%5Clocalhost%5Cc$%5CWindows%5Cwin.ini")]
    [InlineData("/ui/notes.txt::$DATA")]
    [InlineData("/ui/notes.txt%00.png")]
    [InlineData("/ui/nul")]          // Windows device names resolve to somewhere else entirely
    [InlineData("/con.txt")]
    public async Task Paths_cannot_leave_the_web_root(string path)
    {
        WebResponse response = await Get(path);
        Assert.Contains(response.Status, new[] { 400, 403, 404 });
        Assert.DoesNotContain("outside the web root", Text(response));
        Assert.DoesNotContain("[fonts]", Text(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Only_GET_reads_files()
    {
        WebResponse response = await web.Handle(new WebRequest("POST", "/index.html", NoQuery, ""));
        Assert.Equal(404, response.Status);
    }

    [Fact]
    public void Query_strings_are_decoded_as_utf8_and_the_first_value_wins()
    {
        Dictionary<string, string> query = WebServer.ParseQuery("?name=2026-10-06_21-07-46%20Before%20save&id=Caf%C3%A9+x%2B&flag&id=second&=odd");
        Assert.Equal("2026-10-06_21-07-46 Before save", query["name"]);
        Assert.Equal("Café x+", query["id"]);
        Assert.Equal("", query["flag"]);
        Assert.Empty(WebServer.ParseQuery(""));
    }

    // ------------------------------------------------------------ over the network

    async Task<(HttpStatusCode Status, string Text, HttpResponseMessage Response)> Send(HttpMethod method, string path, string? token = null, HttpContent? body = null, string? host = null)
    {
        var request = new HttpRequestMessage(method, $"http://127.0.0.1:{web.Port}{path}") { Content = body };
        if (token is not null) request.Headers.Add("X-Studio-Token", token);
        if (host is not null) request.Headers.Host = host;
        HttpResponseMessage response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), response);
    }

    static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task A_real_request_gets_the_page_the_api_and_the_safety_headers()
    {
        Assert.True(web.Start(47850));
        Assert.InRange(web.Port, 47850, 47859);
        Assert.Equal($"http://localhost:{web.Port}/", web.Url);

        var page = await Send(HttpMethod.Get, "/");
        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Contains(web.Token, page.Text);
        Assert.Equal("no-store", page.Response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", page.Response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("text/html; charset=utf-8", page.Response.Content.Headers.ContentType?.ToString());

        // The same page under the other name for this PC.
        string viaName = await http.GetStringAsync(web.Url, TestContext.Current.CancellationToken);
        Assert.Equal(page.Text, viaName);

        var ping = await Send(HttpMethod.Get, "/api/ping");
        Assert.Equal(HttpStatusCode.OK, ping.Status);
        Assert.Equal("elite-studio", Json(ping.Text).GetProperty("app").GetString());

        var echo = await Send(HttpMethod.Get, "/api/echo?name=Caf%C3%A9%20one&x=1+2", web.Token);
        Assert.Equal(HttpStatusCode.OK, echo.Status);
        Assert.Equal("no-store", echo.Response.Headers.CacheControl?.ToString());
        Assert.Equal("Café one", Json(echo.Text).GetProperty("query").GetProperty("name").GetString());
        Assert.Equal("1 2", Json(echo.Text).GetProperty("query").GetProperty("x").GetString());

        var posted = await Send(HttpMethod.Post, "/api/echo", web.Token, JsonBody("{\"text\":\"héllo\"}"));
        Assert.Equal(HttpStatusCode.OK, posted.Status);
        Assert.Equal("héllo", Json(posted.Text).GetProperty("said").GetString());

        var css = await Send(HttpMethod.Get, "/ui/page.css");
        Assert.Equal("body { color: red }", css.Text);
        Assert.Equal("nosniff", css.Response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Requests_a_browser_could_be_tricked_into_are_refused()
    {
        Assert.True(web.Start(47850));

        // 403: the address bar said something other than this PC (another site pointing its name here).
        // Off Windows .NET's own listener turns away a name or port it was not started for itself (404, before
        // Studio sees the request).
        HttpStatusCode refusedHost = OperatingSystem.IsWindows() ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound;
        var foreign = await Send(HttpMethod.Get, "/", host: "evil.example");
        Assert.Equal(refusedHost, foreign.Status);
        Assert.DoesNotContain(web.Token, foreign.Text);   // the page, which holds the token, is not given out
        if (OperatingSystem.IsWindows()) Assert.Equal("Forbidden", foreign.Text);
        Assert.Equal(refusedHost, (await Send(HttpMethod.Get, "/api/ping", host: $"localhost:{web.Port + 100}")).Status);
        Assert.Equal(refusedHost, (await Send(HttpMethod.Get, "/api/echo", web.Token, host: "evil.example")).Status);

        // 401: no token, a wrong one, or one from before a restart.
        foreach (string? token in new[] { null, "", "0123456789abcdef0123456789abcdef", web.Token.ToUpperInvariant() })
        {
            var refused = await Send(HttpMethod.Get, "/api/echo", token);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.Status);
            Assert.False(Json(refused.Text).GetProperty("ok").GetBoolean());
            Assert.Equal("Elite Studio was restarted. Please reload this page.", Json(refused.Text).GetProperty("error").GetString());
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, "/api/nothing-here")).Status);   // checked before the route
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, "/api/events")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, "/api/echo?t=wrong")).Status);

        // The token also works in the address, for the event stream.
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"/api/echo?t={web.Token}")).Status);

        // 400: a POST that is not JSON (what a form on another website would send).
        var form = await Send(HttpMethod.Post, "/api/echo", web.Token, new StringContent("text=hello", Encoding.UTF8, "application/x-www-form-urlencoded"));
        Assert.Equal(HttpStatusCode.BadRequest, form.Status);
        Assert.Equal("Bad request.", Json(form.Text).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, "/api/echo", web.Token)).Status);

        // 404 and 500 arrive in the error shape with the safety headers too.
        var unknown = await Send(HttpMethod.Get, "/api/nothing-here", web.Token);
        Assert.Equal(HttpStatusCode.NotFound, unknown.Status);
        Assert.Equal("Unknown request.", Json(unknown.Text).GetProperty("error").GetString());
        var broken = await Send(HttpMethod.Get, "/api/broken", web.Token);
        Assert.Equal(HttpStatusCode.InternalServerError, broken.Status);
        Assert.Equal("It broke.", Json(broken.Text).GetProperty("error").GetString());
        Assert.Equal("no-store", broken.Response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task A_caller_that_does_not_connect_from_this_PC_itself_is_refused()
    {
        // Windows listens on every network card, so a request can arrive at this PC's network address
        // while claiming "Host: localhost". Calling that address from here is the nearest a test gets
        // to being another computer: the caller is then that address, not this PC's own loopback.
        IPAddress? network = Dns.GetHostAddresses(Dns.GetHostName(), AddressFamily.InterNetwork).FirstOrDefault(address => !IPAddress.IsLoopback(address));
        Assert.SkipWhen(network is null, "This PC has no network address to call from.");
        Assert.True(web.Start(47850));

        foreach (string path in new[] { "/", "/api/ping", $"/api/echo?t={web.Token}" })
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"http://{network}:{web.Port}{path}");
            request.Headers.Host = $"localhost:{web.Port}";
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, TestContext.Current.CancellationToken); }
            // Off Windows the server may listen on this PC's own loopback only; then such a call cannot even connect, which is refused all the same.
            catch (HttpRequestException) when (!OperatingSystem.IsWindows()) { continue; }
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("Forbidden", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Events_reach_every_open_stream_and_dispose_closes_them()
    {
        Assert.True(web.Start(47850));
        web.Send("state", new { early = true });   // nobody listening yet: nothing to do, nothing kept

        using StreamReader first = await OpenEvents();
        using StreamReader second = await OpenEvents();
        web.Send("state", new { docked = true, station = "Jameson Memorial" });
        web.Send("journal", new { lines = new[] { 1, 2 } });

        foreach (StreamReader stream in new[] { first, second })
        {
            Assert.Equal("event: state", await NextLine(stream));
            Assert.Equal("data: {\"docked\":true,\"station\":\"Jameson Memorial\"}", await NextLine(stream));
            Assert.Equal("", await NextLine(stream));
            Assert.Equal("event: journal", await NextLine(stream));
            Assert.Equal("data: {\"lines\":[1,2]}", await NextLine(stream));
            Assert.Equal("", await NextLine(stream));
        }

        // A page that went away is dropped at the next event without disturbing the others.
        first.Dispose();
        web.Send("state", new { n = 1 });
        web.Send("state", new { n = 2 });
        Assert.Equal("event: state", await NextLine(second));
        Assert.Equal("data: {\"n\":1}", await NextLine(second));

        web.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            while (await NextLine(second) is not null) { }
            throw new EndOfStreamException();
        });
    }

    async Task<StreamReader> OpenEvents()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{web.Port}/api/events?t={web.Token}");
        HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var reader = new StreamReader(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken), Encoding.UTF8);
        Assert.Equal(": connected", await NextLine(reader));
        Assert.Equal("", await NextLine(reader));
        return reader;
    }

    static async Task<string?> NextLine(StreamReader reader) =>
        await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    [Fact]
    public void A_taken_port_moves_on_to_the_next_and_ten_taken_ports_give_up()
    {
        const int first = 47870;
        var ours = new Dictionary<int, HttpListener>();
        try
        {
            for (int port = first; port < first + 10; port++)
            {
                var other = new HttpListener();
                other.Prefixes.Add($"http://localhost:{port}/");
                try { other.Start(); ours[port] = other; }
                catch (HttpListenerException) { other.Close(); }   // some other program has it: just as taken
            }
            Assert.False(web.Start(first));

            int freed = ours.Keys.Order().ElementAt(ours.Count / 2);
            ours[freed].Close();
            Assert.True(web.Start(first));
            Assert.Equal(freed, web.Port);
        }
        finally
        {
            foreach (HttpListener other in ours.Values) other.Close();
        }
    }

    [Fact]
    public async Task Without_a_message_loop_a_slow_request_does_not_hold_up_the_next()
    {
        var release = new TaskCompletionSource();
        web.Map("GET", "/api/slow", async _ => { await release.Task; return WebResponse.Text("slow done"); });
        Assert.True(web.Start(47850));

        Task<(HttpStatusCode Status, string Text, HttpResponseMessage Response)> slow = Send(HttpMethod.Get, "/api/slow", web.Token);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/echo", web.Token)).Status);
        Assert.False(slow.IsCompleted);
        release.SetResult();
        Assert.Equal("slow done", (await slow).Text);
    }
}
