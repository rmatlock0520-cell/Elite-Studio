using System.Net.Http;
using System.Text;
using System.Text.Json;
using EliteStudio.App;
using EliteStudio.Core;
using EliteStudio.Flight;

namespace EliteStudio.Tests;

/// <summary>
/// Looking for vJoy without vJoy: every state the Stick setup page can show, judged from stand-ins for what Windows keeps
/// and for what vJoy's library answers; the axes needed; the folder picked by hand; the routes, also over real HTTP; and
/// the helper's own answer.
/// </summary>
public sealed class VJoyTests : IDisposable
{
    readonly string temp = Directory.CreateTempSubdirectory("EliteStudioTests-").FullName;

    public void Dispose() => Directory.Delete(temp, recursive: true);

    static readonly string[] Three = ["X", "Y", "RZ"];

    /// <summary>vJoy installed as on the owner's PC: listed, library and settings program there, driver and device running.</summary>
    static readonly VJoyInstall Installed = new("vJoy Device Driver 2.1.9.1", "2.1.9.1", @"C:\Program Files\vJoy", @"C:\Program Files\vJoy\x64\vJoyInterface.dll",
        DllFrom: "listed", Service: true, DeviceSeen: true, DevicePresent: true, Problem: 0, RebootPending: false, Configure: @"C:\Program Files\vJoy\x64\vJoyConf.exe");

    /// <summary>What vJoy's library answers: by default 2.1.9 running, device 1 free with all eight axes.</summary>
    static VJoyProbeResult Answer(int status = 1, int version = 0x219, bool enabled = true, string? owner = null, params string[] lacking) => new()
    {
        Enabled = enabled, Version = version, DllVersion = version, DriverMatch = true, Status = status, Owner = owner, Buttons = 8, Devices = [1],
        Axes = ((string[])["X", "Y", "Z", "RX", "RY", "RZ", "U", "V"]).ToDictionary(a => a, a => !lacking.Contains(a)),
    };

    static VJoyState Judge(VJoyInstall found, VJoyProbeResult? probe) => VJoyState.Judge(found, probe, Three);

    [Fact]
    public void Every_state_the_page_shows_is_told_apart()
    {
        Assert.Equal("ready", Judge(Installed, Answer()).State);
        Assert.Equal("notFound", Judge(VJoyInstall.None, null).State);
        // what an uninstall leaves behind (a service, a remembered device, a library left in Program Files) is not vJoy
        foreach (VJoyInstall leftover in (VJoyInstall[])[VJoyInstall.None with { Service = true, DeviceSeen = true },
            VJoyInstall.None with { Dll = Installed.Dll, DllFrom = "default" }, VJoyInstall.None with { Dll = Installed.Dll, DllFrom = "default", Service = true }])
        {
            VJoyState left = Judge(leftover, Answer());
            Assert.Equal(("notFound", true), (left.State, left.Leftovers));
        }
        // ...but a library the player pointed to, or one beside a vJoy device that is there now, is
        Assert.Equal("ready", Judge(VJoyInstall.None with { Dll = Installed.Dll, DllFrom = "chosen", Service = true, DevicePresent = true, DeviceSeen = true }, Answer()).State);
        Assert.Equal("ready", Judge(Installed with { Name = null, DllFrom = "default" }, Answer()).State);
        Assert.Equal("reboot", Judge(Installed with { RebootPending = true }, Answer()).State);
        Assert.Equal("broken", Judge(Installed with { Dll = null, DllFrom = null }, null).State);
        // Windows' own word about the device comes before anything the library says
        Assert.Equal("disabled", Judge(Installed with { Problem = 22 }, Answer(enabled: false)).State);
        Assert.Equal("blocked", Judge(Installed with { Problem = 52 }, Answer()).State);
        Assert.Equal("noDriver", Judge(Installed with { Problem = 28 }, Answer()).State);
        VJoyState odd = Judge(Installed with { Problem = 10 }, Answer());
        Assert.Equal(("problem", 10), (odd.State, odd.Problem));
        Assert.Equal("noDriver", Judge(Installed with { Service = false }, Answer()).State);
        // the library could not be asked: no helper, or a problem in its answer (said word for word)
        Assert.Equal("probeFailed", Judge(Installed, null).State);
        VJoyState failed = Judge(Installed, new VJoyProbeResult { Problem = "Windows would not load vJoy's library." });
        Assert.Equal(("probeFailed", "Windows would not load vJoy's library."), (failed.State, failed.Detail));
        Assert.Equal("absent", Judge(Installed with { DevicePresent = false }, Answer(enabled: false)).State);
        Assert.Equal("notRunning", Judge(Installed, Answer(enabled: false)).State);
        Assert.Equal("tooOld", Judge(Installed, Answer(version: 0x216)).State);
        Assert.Equal("noDevice", Judge(Installed, Answer(status: 3) with { Axes = [] }).State);
        // vJoy could not say how device 1 is: never "ready", and never advice about axes it could not report
        Assert.Equal("probeFailed", Judge(Installed, Answer(status: 4)).State);
        Assert.Equal("probeFailed", Judge(Installed, Answer(status: 4) with { Axes = [] }).State);
        VJoyState lacks = Judge(Installed, Answer(lacking: ["RZ", "U"]));
        Assert.Equal("missingAxes", lacks.State);
        Assert.Equal(new[] { "RZ" }, lacks.Missing);   // only the axes needed count
        VJoyState held = Judge(Installed, Answer(status: 2, owner: "joystick_gremlin"));
        Assert.Equal(("inUse", "joystick_gremlin"), (held.State, held.Owner));
        // a library that says its version differs from the driver's still works: told, not stopped
        VJoyState mismatched = Judge(Installed, Answer() with { DriverMatch = false });
        Assert.Equal(("ready", (bool?)false), (mismatched.State, mismatched.DriverMatch));
    }

    [Fact]
    public void An_axis_is_there_only_on_a_plain_yes()
    {
        Assert.True(VJoyProbe.AxisThere(1));
        foreach (int no in (int[])[0, -2, -10]) Assert.False(VJoyProbe.AxisThere(no));   // vJoy's errors are negative
    }

    [Fact]
    public void The_axes_needed_are_the_ones_Elite_uses_on_vJoy_else_the_usual_three()
    {
        using var game = new PracticeGame();
        game.Copy("Custom.4.2.binds", game.BindingsDir);
        game.Copy("StartPreset.4.start", game.BindingsDir);
        FlightBinds? binds = FlightBinds.Load(game.BindingsDir, null);
        Assert.NotNull(binds);
        (string[] axes, bool fromBinds) = VJoyState.AxesFor(binds);
        Assert.Equal(new[] { "RZ", "X", "Y" }, axes.Order());   // pitch Y, roll X, yaw Rz; the throttle is on the Logitech
        Assert.True(fromBinds);
        (string[] usual, bool bound) = VJoyState.AxesFor(null);
        Assert.Equal(Three, usual);
        Assert.False(bound);
        // the page words it by where the axes came from
        Assert.False(VJoyState.Judge(Installed, Answer(), Three, fromBinds: false).FromBinds);
    }

    [Fact]
    public void A_folder_picked_by_hand_is_kept_only_when_it_is_a_whole_path_on_a_drive()
    {
        Assert.Equal(@"D:\Tools\vJoy", (new FlightSettings { VJoyFolder = @"D:\Tools\vJoy\" }).Cleaned().VJoyFolder);
        foreach (string? bad in (string?[])[null, "", "vJoy", @"..\vJoy", "C:", @"C:\a|b", new string('x', 300), @"\\server\share\vJoy", @"\\?\C:\vJoy", "//server/share"])
            Assert.Equal("", (new FlightSettings { VJoyFolder = bad! }).Cleaned().VJoyFolder);
    }

    [Fact]
    public void Only_folders_on_this_PCs_own_disks_are_looked_in()
    {
        Assert.True(VJoyFinder.LocalFolder(temp));   // the test's own folder, on a fixed disk
        foreach (string? far in (string?[])[null, "", "vJoy", @"\\server\share\vJoy", @"\\?\UNC\server\share", @"\\.\C:\vJoy", "//server/share", @"\\host@SSL\DavWWWRoot\v"])
            Assert.False(VJoyFinder.LocalFolder(far));
        Assert.Null(VJoyFinder.LibraryIn(@"\\server\share\vJoy"));
    }

    [Fact]
    public void The_library_is_found_in_a_vJoy_folder_or_its_x64_folder_but_never_the_32_bit_one()
    {
        string vjoy = Path.Combine(temp, "vJoy");
        Directory.CreateDirectory(Path.Combine(vjoy, "x64"));
        Directory.CreateDirectory(Path.Combine(vjoy, "x86"));
        File.WriteAllText(Path.Combine(vjoy, "x86", "vJoyInterface.dll"), "");
        Assert.Null(VJoyFinder.LibraryIn(vjoy));
        Assert.Null(VJoyFinder.LibraryIn(Path.Combine(vjoy, "x86")));   // picked by hand: still not the 32-bit one
        File.WriteAllText(Path.Combine(vjoy, "x64", "vJoyInterface.dll"), "");
        Assert.Equal(Path.Combine(vjoy, "x64", "vJoyInterface.dll"), VJoyFinder.LibraryIn(vjoy));
        Assert.Equal(Path.Combine(vjoy, "x64", "vJoyInterface.dll"), VJoyFinder.LibraryIn(Path.Combine(vjoy, "x64")));
        Assert.Null(VJoyFinder.LibraryIn("vJoy"));
    }

    [Fact]
    public void A_folder_picked_by_hand_must_be_vJoys_own_not_a_stray_copy_of_its_library()
    {
        string vjoy = Path.Combine(temp, "vJoy");
        Directory.CreateDirectory(Path.Combine(vjoy, "x64"));
        Directory.CreateDirectory(Path.Combine(vjoy, "x86"));
        Assert.Contains("no vJoy library", VJoyFinder.WhyNotVJoy(vjoy));
        File.WriteAllText(Path.Combine(vjoy, "x64", "vJoyInterface.dll"), "");
        Assert.Contains("not vJoy itself", VJoyFinder.WhyNotVJoy(vjoy));   // a library alone is not vJoy
        File.WriteAllText(Path.Combine(vjoy, "vjoy.inf"), "");
        Assert.Null(VJoyFinder.WhyNotVJoy(vjoy));
        Assert.Null(VJoyFinder.WhyNotVJoy(Path.Combine(vjoy, "x64")));   // its x64 folder, with the driver files one up
        Assert.Contains("32-bit", VJoyFinder.WhyNotVJoy(Path.Combine(vjoy, "x86")));
        Assert.Contains("network", VJoyFinder.WhyNotVJoy(@"\\server\share\vJoy"));
    }

    [Fact]
    public async Task The_route_looks_with_the_folder_kept_asks_the_library_only_when_there_is_one_and_pages_cannot_set_the_folder()
    {
        using var game = new PracticeGame();
        game.Copy("Custom.4.2.binds", game.BindingsDir);
        game.Copy("StartPreset.4.start", game.BindingsDir);
        using GameWatcher watcher = game.Watch();
        watcher.Poll();
        string file = Path.Combine(temp, "settings.json");
        File.WriteAllText(file, "{\"flight\":{\"vJoyFolder\":\"C:\\\\Tools\\\\vJoy\"}}");   // kept from an earlier pick
        using var web = new WebServer(temp);
        var looked = new List<string?>();
        int asked = 0;
        VJoyInstall found = VJoyInstall.None;
        using var flight = new FlightApi(web, new Settings(file), watcher.State, null, game.BindingsDir, null,
            gremlinProfile: () => null, sticks: _ => [], held: _ => false,
            findVJoy: chosen => { looked.Add(chosen); return found; },
            askVJoy: _ => { asked++; return Task.FromResult<VJoyProbeResult?>(Answer(lacking: "RZ")); });

        (int status, JsonElement json) = await Ask(web, "GET", "/vjoy");
        Assert.Equal((200, "notFound", 0), (status, json.GetProperty("state").GetString(), asked));
        Assert.Equal(@"C:\Tools\vJoy", looked[^1]);
        Assert.Equal(@"C:\Tools\vJoy", json.GetProperty("chosen").GetString());

        found = Installed;
        (status, json) = await Ask(web, "GET", "/vjoy");
        Assert.Equal((200, "missingAxes", 1), (status, json.GetProperty("state").GetString(), asked));
        Assert.Equal(new[] { "RZ" }, json.GetProperty("missing").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(new[] { "RZ", "X", "Y" }, json.GetProperty("need").EnumerateArray().Select(e => e.GetString()).Order());
        Assert.True(json.GetProperty("fromBinds").GetBoolean());

        // a page cannot point Studio at a folder: only Windows' folder picker sets it
        await Ask(web, "POST", "/settings", "{\"vJoyFolder\":\"D:\\\\Elsewhere\"}");
        await Ask(web, "GET", "/vjoy");
        Assert.Equal(@"C:\Tools\vJoy", looked[^1]);
        // forgetting it looks again only where Windows says
        (status, json) = await Ask(web, "POST", "/vjoy/forget", "{}");
        Assert.Equal(200, status);
        Assert.Equal("", looked[^1]);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("chosen").ValueKind);
        // a headless run has no window to show the folder picker over; Configure vJoy where it is not found
        (status, _) = await Ask(web, "POST", "/vjoy/folder", "{}");
        Assert.Equal(409, status);
        found = Installed with { Configure = null };
        (status, _) = await Ask(web, "POST", "/vjoy/configure", "{}");
        Assert.Equal(404, status);
        // vJoy is never part of what every flight page loads
        (_, json) = await Ask(web, "GET", "");
        Assert.False(json.TryGetProperty("vjoy", out _));
    }

    [Fact]
    public async Task Over_real_HTTP_the_cards_buttons_are_answered_by_their_routes()
    {
        using var game = new PracticeGame();
        game.Copy("Custom.4.2.binds", game.BindingsDir);
        game.Copy("StartPreset.4.start", game.BindingsDir);
        using GameWatcher watcher = game.Watch();
        watcher.Poll();
        using var web = new WebServer(temp);
        using var flight = new FlightApi(web, new Settings(Path.Combine(temp, "settings.json")), watcher.State, null, game.BindingsDir, null,
            gremlinProfile: () => null, sticks: _ => [], held: _ => false,
            findVJoy: _ => Installed with { Configure = null }, askVJoy: _ => Task.FromResult<VJoyProbeResult?>(Answer()));
        Assert.True(web.Start(47850));
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        async Task<int> Post(string route, string? json)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{web.Port}/api/flight/vjoy/{route}");
            request.Headers.Add("X-Studio-Token", web.Token);
            if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await http.SendAsync(request);
            return (int)response.StatusCode;
        }
        // as the card sends them (an empty JSON body): each reaches its route
        Assert.Equal(404, await Post("configure", "{}"));
        Assert.Equal(409, await Post("folder", "{}"));
        Assert.Equal(200, await Post("forget", "{}"));
        // with no body at all the server turns a POST away before any route: the reason the card always sends one
        Assert.Equal(400, await Post("configure", null));
    }

    static async Task<(int Status, JsonElement Json)> Ask(WebServer web, string method, string route, string body = "")
    {
        WebResponse answer = await web.Handle(new WebRequest(method, "/api/flight" + route, new Dictionary<string, string>(), body));
        return (answer.Status, JsonDocument.Parse(answer.Body).RootElement);
    }

    [Fact]
    public void The_helper_option_takes_a_path_with_spaces()
    {
        Dictionary<string, string> options = Program.ParseOptions(["--vjoy-probe", @"C:\Program Files\vJoy\x64\vJoyInterface.dll"]);
        Assert.Equal(@"C:\Program Files\vJoy\x64\vJoyInterface.dll", options[VJoyProbe.Option]);
    }

    [Fact]
    public void The_helper_answers_in_JSON_and_refuses_anything_but_vJoys_library()
    {
        TextWriter before = Console.Out;
        var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Assert.Equal(0, VJoyProbe.Run(@"C:\Windows\notepad.exe"));
            Assert.Equal(0, VJoyProbe.Run(@"\\server\share\vJoy\x64\vJoyInterface.dll"));   // never from a share
        }
        finally
        {
            Console.SetOut(before);
        }
        string[] answers = output.ToString().Split("}{");
        Assert.Equal(2, answers.Length);
        foreach (VJoyProbeResult? answer in answers.Select((a, i) => JsonSerializer.Deserialize<VJoyProbeResult>((i == 0 ? a + "}" : "{" + a), VJoyProbe.Json)))
        {
            Assert.NotNull(answer);
            Assert.StartsWith("That is not vJoy's library", answer.Problem);
            Assert.False(answer.Enabled);
        }
    }

    [Fact]
    public void Looking_on_this_PC_reads_only_and_never_fails()
    {
        // Whatever this PC has: the search must answer, not throw (no vJoy, a vJoy, or a user who may not read some keys).
        VJoyInstall found = VJoyFinder.Look(null);
        Assert.True(found.Dll is null || File.Exists(found.Dll));
        Assert.True(found.Problem >= 0);
    }
}
