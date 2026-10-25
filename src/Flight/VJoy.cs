using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using EliteStudio.App;
using Microsoft.Win32;

namespace EliteStudio.Flight;

// vJoy, the virtual joystick a stick shaper feeds (Joystick Gremlin today; Gimbalyn, Studio's own, next). Elite has no
// curves for sticks, only one dead zone per axis, so a stick's movement can only reach the game reshaped through a
// virtual stick. vJoy is a driver the player installs themselves: Studio never installs it, and looks for it only when
// Studio Flight's Options page asks (a player with no stick never loads any of it).
//
// The search has two halves. VJoyFinder reads only what Windows keeps (its list of installed programs, the vJoy folder,
// the driver's service and the device's state) and never loads vJoy's library. VJoyProbe loads that library and asks
// it the rest (running? which version? device 1 free, and with which axes?); it runs in a short-lived second copy of
// Studio's program (EliteStudio.exe --vjoy-probe <dll>), so a fault in an old or odd vJoy can never take Studio down,
// and the library is never held open while the player upgrades vJoy. Nothing here takes, moves or resets vJoy.
//
// Only folders on this PC's own fixed disks are ever looked in, loaded from or started from: never a network share or a
// mapped drive (a library loaded from one would run someone else's code as the player, and merely looking at a share
// sends the player's Windows sign-in to it).

/// <summary>
/// What Windows says about vJoy, read without loading its library.
/// </summary>
/// <param name="Name">Its entry in Windows' list of installed programs ("vJoy Device Driver 2.1.9.1"); null when there is none.</param>
/// <param name="Version">That entry's version ("2.1.9.1").</param>
/// <param name="Folder">Where it is installed, as listed (else the folder its library was found in).</param>
/// <param name="Dll">vJoy's 64-bit library (x64\vJoyInterface.dll) where one was found; null for none.</param>
/// <param name="DllFrom">Where that library was found: "listed" (the folder Windows lists), "chosen" (the one the player picked) or "default" (Program Files\vJoy).</param>
/// <param name="Service">The driver's service is registered with Windows.</param>
/// <param name="DeviceSeen">Windows has a vJoy device at all (present or not).</param>
/// <param name="DevicePresent">That device is present now.</param>
/// <param name="Problem">Windows' problem code for it: 0 none, 22 disabled, 28 no driver, 52 driver blocked as unsigned.</param>
/// <param name="RebootPending">vJoy's installer is waiting for Windows to restart.</param>
/// <param name="Configure">vJoy's own settings program (x64\vJoyConf.exe), where one was found.</param>
public sealed record VJoyInstall(string? Name, string? Version, string? Folder, string? Dll, string? DllFrom, bool Service, bool DeviceSeen,
    bool DevicePresent, int Problem, bool RebootPending, string? Configure)
{
    public static readonly VJoyInstall None = new(null, null, null, null, null, false, false, false, 0, false, null);
}

/// <summary>What vJoy's library said when the helper asked it (device 1 only: the one Elite and Gremlin use).</summary>
public sealed record VJoyProbeResult
{
    /// <summary>Why it could not be asked (the library would not load, lacks a function, or crashed); null when it answered.</summary>
    public string? Problem { get; init; }
    /// <summary>The driver runs and a vJoy device is enabled.</summary>
    public bool Enabled { get; init; }
    /// <summary>The driver's version as vJoy numbers it: 0x219 is 2.1.9; 0 when no vJoy device answered.</summary>
    public int Version { get; init; }
    /// <summary>The library's own version, when it says; it may differ from the driver's (that usually still works).</summary>
    public int DllVersion { get; init; }
    /// <summary>The library and the driver are the same version; null when the library cannot say.</summary>
    public bool? DriverMatch { get; init; }
    /// <summary>Device 1: 0 held by the asking program, 1 free, 2 held by another program, 3 not set up, 4 vJoy could not say.</summary>
    public int Status { get; init; } = 4;
    /// <summary>The program holding device 1, when another one does ("joystick_gremlin").</summary>
    public string? Owner { get; init; }
    /// <summary>Device 1's axes by the game's letters (X, Y, Z, RX, RY, RZ, U = first slider, V = second): there or not. Empty when device 1 is not set up or its state unknown.</summary>
    public Dictionary<string, bool> Axes { get; init; } = [];
    /// <summary>Device 1's buttons.</summary>
    public int Buttons { get; init; }
    /// <summary>The vJoy devices set up (1 to 16).</summary>
    public int[] Devices { get; init; } = [];
}

/// <summary>The verdict the Options page shows: a state, and what it was made from.</summary>
/// <param name="State">
/// "ready"; "notFound" (also when only leftovers of an old vJoy remain: <paramref name="Leftovers"/>); "broken" (installed,
/// library missing); "reboot"; "disabled" (Windows problem 22); "blocked" (52, driver not signed for this Windows);
/// "noDriver" (28, or no service); "problem" (another code, in <paramref name="Problem"/>); "absent" (installed, no device);
/// "probeFailed" (vJoy could not be asked, or could not say how device 1 is: <paramref name="Detail"/>); "notRunning";
/// "tooOld"; "noDevice" (device 1 not set up); "missingAxes" (<paramref name="Missing"/>); "inUse" (held by <paramref name="Owner"/>).
/// </param>
/// <param name="FromBinds">The axes needed are the ones Elite's flight bindings use on vJoy (false: none are on vJoy yet, so the usual three).</param>
/// <param name="Chosen">The folder the player picked by hand, while one is kept in the settings.</param>
public sealed record VJoyState(string State, string? Detail, int Problem, bool Leftovers, string? Version, string? Folder, string? Dll,
    string? Configure, string? Owner, string[] Need, bool FromBinds, string[] Missing, bool? DriverMatch, string? Chosen, VJoyProbeResult? Probe)
{
    /// <summary>The oldest driver that works (2.1.8, as Joystick Gremlin also requires).</summary>
    public const int OldestVersion = 0x218;

    /// <summary>The axes a stick shaper feeds when Elite has none of its flight axes on vJoy yet: roll, pitch, yaw.</summary>
    public static readonly string[] DefaultAxes = ["X", "Y", "RZ"];

    /// <summary>
    /// What to tell the player, from what Windows said and what vJoy's library said (null when it was not asked). Windows'
    /// own word comes first: a driver Windows blocked or disabled cannot answer the library truthfully.
    /// </summary>
    public static VJoyState Judge(VJoyInstall found, VJoyProbeResult? probe, IReadOnlyList<string> need, bool fromBinds = true, string? chosen = null)
    {
        string[] needed = [.. need.Distinct()];
        VJoyState Say(string state, string? detail = null, string[]? missing = null) =>
            new(state, detail, found.Problem, false, found.Version, found.Folder, found.Dll, found.Configure, probe?.Owner, needed, fromBinds,
                missing ?? [], probe?.DriverMatch, string.IsNullOrEmpty(chosen) ? null : chosen, probe);

        // vJoy is installed when Windows lists it, when the player pointed to it, or when its library sits beside a vJoy
        // device that is there now. A library, a service or a remembered device on its own is what an uninstall leaves behind.
        bool installed = found.Name is not null || (found.Dll is not null && (found.DllFrom == "chosen" || found.DevicePresent));
        if (!installed) return Say("notFound") with { Leftovers = found.Dll is not null || found.Service || found.DeviceSeen };
        if (found.RebootPending) return Say("reboot");
        if (found.Dll is null) return Say("broken");
        if (found.DevicePresent && found.Problem != 0)
            return Say(found.Problem switch { 22 => "disabled", 52 => "blocked", 28 => "noDriver", _ => "problem" });
        if (!found.Service) return Say("noDriver");
        if (probe is null) return Say("probeFailed", "Studio could not run its vJoy helper.");
        if (probe.Problem is not null) return Say("probeFailed", probe.Problem);
        if (!probe.Enabled) return Say(found.DeviceSeen && !found.DevicePresent ? "absent" : "notRunning");
        if (probe.Version is > 0 and < OldestVersion) return Say("tooOld");
        if (probe.Status == 3) return Say("noDevice");
        if (probe.Status is not (0 or 1 or 2)) return Say("probeFailed", "vJoy could not say how its device 1 is.");
        string[] missing = [.. needed.Where(axis => !probe.Axes.GetValueOrDefault(axis))];
        if (missing.Length > 0) return Say("missingAxes", missing: missing);
        if (probe.Status == 2) return Say("inUse");
        return Say("ready");
    }

    /// <summary>The vJoy axes Elite's flight bindings use (the game's letters), or the usual three when none are on vJoy.</summary>
    public static (string[] Axes, bool FromBinds) AxesFor(FlightBinds? binds)
    {
        string[] used = binds is null ? [] : [.. binds.Axes.Values
            .Where(a => a.Device.Equals("vJoy", StringComparison.OrdinalIgnoreCase) && VJoyProbe.Usages.ContainsKey(a.Axis))
            .Select(a => a.Axis).Distinct()];
        return used.Length > 0 ? (used, true) : (DefaultAxes, false);
    }
}

/// <summary>Finds vJoy from what Windows keeps, without loading its library (see the top of this file).</summary>
public static class VJoyFinder
{
    /// <summary>vJoy's installer's own name for itself in Windows' list of installed programs (2.1.9.1 and 2.2.x alike).</summary>
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8E31F76F-74C3-47F1-9550-E041EEDC5FBB}_is1";
    const string HardwareId = "VID_1234&PID_BEAD";

    /// <summary>Looks. <paramref name="chosen"/> is a folder the player picked by hand ("" or null for none).</summary>
    public static VJoyInstall Look(string? chosen)
    {
        using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        (string? name, string? version, string? listed) = Listed(machine);
        (string? dll, string? from) = (null, null);
        foreach ((string folder, string kind) in Folders(listed, chosen))
            if (LibraryIn(folder) is { } path) { (dll, from) = (path, kind); break; }
        string? x64 = dll is null ? null : Path.GetDirectoryName(dll);
        string? configure = ((string?[])[x64, listed is null ? null : Path.Combine(listed, "x64")])
            .Where(f => f is not null && LocalFolder(f)).Select(f => Path.Combine(f!, "vJoyConf.exe")).FirstOrDefault(File.Exists);
        bool service = Exists(machine, @"SYSTEM\CurrentControlSet\Services\vjoy");
        (bool seen, bool present, int problem) = Device(machine);
        return new VJoyInstall(name, version, listed ?? (x64 is not null ? Path.GetDirectoryName(x64) : null), dll, from, service, seen, present, problem,
            RebootPending(machine), configure);
    }

    /// <summary>The folders vJoy may be in, in the order they are trusted: the one Windows lists, the one the player picked, the usual one.</summary>
    static IEnumerable<(string Folder, string Kind)> Folders(string? listed, string? chosen)
    {
        if (!string.IsNullOrWhiteSpace(listed)) yield return (listed, "listed");
        if (!string.IsNullOrWhiteSpace(chosen)) yield return (chosen, "chosen");
        foreach (string? root in (string?[])[Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)])
            if (!string.IsNullOrEmpty(root)) yield return (Path.Combine(root, "vJoy"), "default");
    }

    /// <summary>
    /// A folder on one of this PC's own fixed disks: never a share (\\server\...), a device path (\\?\, \\.\) or a mapped
    /// network drive, which could hand Studio someone else's library, and which Windows would sign in to.
    /// </summary>
    internal static bool LocalFolder(string? folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\", StringComparison.Ordinal) || folder.StartsWith("//", StringComparison.Ordinal))
                return false;
            return Path.GetPathRoot(folder) is { Length: > 0 } root && new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (Exception problem) when (problem is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The 64-bit library in a vJoy folder, or in its x64 folder when the player picked that one itself; null for none (or a folder not on this PC).</summary>
    internal static string? LibraryIn(string folder)
    {
        if (!LocalFolder(folder)) return null;
        try
        {
            foreach (string path in (string[])[Path.Combine(folder, "x64", "vJoyInterface.dll"), Path.Combine(folder, "vJoyInterface.dll")])
                if (File.Exists(path) && !string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "x86", StringComparison.OrdinalIgnoreCase)) return path;
        }
        catch (ArgumentException) { }   // a folder name Windows does not accept
        return null;
    }

    /// <summary>
    /// Whether a folder the player picked looks like vJoy's own (its driver files or its settings program in it, in its x64
    /// folder, or one up), so that any stray copy of the library does not pass for vJoy; and why not, when it does not.
    /// </summary>
    internal static string? WhyNotVJoy(string folder)
    {
        if (!LocalFolder(folder)) return "Choose a folder on this PC's own disk, not on a network share or a mapped drive.";
        if (string.Equals(Path.GetFileName(folder.TrimEnd('\\')), "x86", StringComparison.OrdinalIgnoreCase))
            return "That is vJoy's 32-bit folder. Choose the vJoy folder above it.";
        if (LibraryIn(folder) is null) return "That folder has no vJoy library (vJoyInterface.dll, in it or in its x64 folder). Choose the folder vJoy is installed in, often C:\\Program Files\\vJoy.";
        string?[] places = [folder, Path.Combine(folder, "x64"), Path.GetDirectoryName(folder.TrimEnd('\\'))];
        bool vjoy = places.Any(p => p is not null && ((string[])["vjoy.inf", "vJoy.sys", "vJoyConf.exe", "vJoyInstall.exe"]).Any(f => File.Exists(Path.Combine(p, f))));
        return vjoy ? null : "That folder has a vJoy library but not vJoy itself (no driver files or Configure vJoy beside it). Choose the folder vJoy is installed in.";
    }

    /// <summary>vJoy's entry in Windows' list of installed programs: its own key, else any entry named vJoy. Only a folder on this PC counts.</summary>
    static (string? Name, string? Version, string? Folder) Listed(RegistryKey machine)
    {
        foreach (RegistryKey? key in Entries(machine))
            using (key)
                if (key?.GetValue("DisplayName") is string name && name.StartsWith("vJoy", StringComparison.OrdinalIgnoreCase))
                {
                    string? folder = (key.GetValue("InstallLocation") as string)?.TrimEnd('\\');
                    return (name, key.GetValue("DisplayVersion") as string, LocalFolder(folder) ? folder : null);
                }
        return (null, null, null);
    }

    static IEnumerable<RegistryKey?> Entries(RegistryKey machine)
    {
        yield return Open(machine, UninstallKey);
        foreach (string list in (string[])[@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"])
        {
            using RegistryKey? all = Open(machine, list);
            foreach (string sub in all?.GetSubKeyNames() ?? []) yield return Open(all!, sub);
        }
    }

    /// <summary>
    /// The vJoy device as Windows' device list has it: seen at all, present now, and its problem code. Its instances are
    /// looked up by hardware id in the registry and asked about with Windows' device functions (no library of vJoy's).
    /// </summary>
    static (bool Seen, bool Present, int Problem) Device(RegistryKey machine)
    {
        var instances = new List<string>();
        using (RegistryKey? hid = Open(machine, @"SYSTEM\CurrentControlSet\Enum\ROOT\HIDCLASS"))
            foreach (string sub in hid?.GetSubKeyNames() ?? [])
            {
                using RegistryKey? key = Open(hid!, sub);
                if (key?.GetValue("HardwareID") is string[] ids && ids.Any(id => id.Contains(HardwareId, StringComparison.OrdinalIgnoreCase)))
                    instances.Add(@"ROOT\HIDCLASS\" + sub);
            }
        // The driver's own list of the devices it runs, for when the device list could not be read.
        using (RegistryKey? running = Open(machine, @"SYSTEM\CurrentControlSet\Services\vjoy\Enum"))
            if (running?.GetValue("Count") is int count)
                for (int i = 0; i < count; i++)
                    if (running.GetValue(i.ToString(System.Globalization.CultureInfo.InvariantCulture)) is string id && !instances.Contains(id, StringComparer.OrdinalIgnoreCase)) instances.Add(id);
        (bool seen, bool present, int problem) = (instances.Count > 0, false, 0);
        foreach (string id in instances)
        {
            if (CM_Locate_DevNodeW(out uint node, id, 0) != 0) continue;   // not present now: a device Windows only remembers
            if (CM_Get_DevNode_Status(out _, out uint code, node, 0) != 0) continue;
            if (!present || problem != 0) (present, problem) = (true, (int)code);   // a working one wins over one with a problem
        }
        return (seen, present, problem);
    }

    static bool RebootPending(RegistryKey machine)
    {
        using RegistryKey? once = Open(machine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce");
        return once?.GetValueNames().Any(name => name.Contains("vJoy", StringComparison.OrdinalIgnoreCase)) == true;
    }

    static bool Exists(RegistryKey machine, string path)
    {
        using RegistryKey? key = Open(machine, path);
        return key is not null;
    }

    /// <summary>A key opened to read, or null when it is not there or this user may not read it.</summary>
    static RegistryKey? Open(RegistryKey parent, string path)
    {
        try
        {
            return parent.OpenSubKey(path, writable: false);
        }
        catch (Exception problem) when (problem is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
}

/// <summary>
/// The helper's side: loads vJoy's library and asks it about device 1, then writes the answer as JSON to its output. Run
/// only in the short-lived second copy (EliteStudio.exe --vjoy-probe &lt;dll&gt;); it asks, and never takes, moves or resets vJoy.
/// </summary>
public static class VJoyProbe
{
    /// <summary>The command-line option that makes Studio's program answer as the helper.</summary>
    public const string Option = "vjoy-probe";

    /// <summary>vJoy's numbers for the axes, by the game's letters (U and V are the sliders).</summary>
    internal static readonly IReadOnlyDictionary<string, uint> Usages = new Dictionary<string, uint>
    {
        ["X"] = 0x30, ["Y"] = 0x31, ["Z"] = 0x32, ["RX"] = 0x33, ["RY"] = 0x34, ["RZ"] = 0x35, ["U"] = 0x36, ["V"] = 0x37,
    };

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NoArgs();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate short Short();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Match(out ushort dll, out ushort driver);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int OfDevice(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int OfAxis(uint id, uint usage);

    /// <summary>The helper itself: asks, writes the answer, and leaves with 0 (the answer says what went wrong, if anything).</summary>
    public static int Run(string? dll)
    {
        QuietCrashes();
        VJoyProbeResult answer;
        try
        {
            answer = Ask(dll ?? "");
        }
        catch (Exception problem)
        {
            answer = new VJoyProbeResult { Problem = "vJoy's library failed when asked: " + problem.Message };
        }
        Console.Out.Write(JsonSerializer.Serialize(answer, Json));
        Console.Out.Flush();
        return 0;
    }

    /// <summary>
    /// For a process that loads vJoy's library (this helper, and Gimbalyn): should the library crash, the process just ends
    /// at once, with no "stopped working" box from Windows (perhaps over the game, and keeping a crashed Gimbalyn alive and
    /// holding vJoy until the box is closed), nor a "missing disk" or "cannot find" one while the library loads.
    /// </summary>
    internal static void QuietCrashes()
    {
        try
        {
            _ = WerSetFlags(WerNoUi);
            _ = SetErrorMode(GetErrorMode() | SemFailCriticalErrors | SemNoGpFaultErrorBox | SemNoOpenFileErrorBox);
        }
        catch (Exception problem) when (problem is EntryPointNotFoundException or DllNotFoundException) { }
    }

    /// <summary>An axis is there only on a plain yes (1 or more): vJoy answers its errors with negative numbers.</summary>
    internal static bool AxisThere(int answer) => answer > 0;

    static VJoyProbeResult Ask(string dll)
    {
        if (!Path.IsPathFullyQualified(dll) || !Path.GetFileName(dll).Equals("vJoyInterface.dll", StringComparison.OrdinalIgnoreCase)
            || !VJoyFinder.LocalFolder(Path.GetDirectoryName(dll)) || !File.Exists(dll))
            return new VJoyProbeResult { Problem = "That is not vJoy's library: " + dll };
        if (!NativeLibrary.TryLoad(dll, out IntPtr library))
            return new VJoyProbeResult { Problem = "Windows would not load vJoy's library (it may be damaged, or a 32-bit one)." };
        T? Find<T>(string name) where T : Delegate =>
            NativeLibrary.TryGetExport(library, name, out IntPtr at) ? Marshal.GetDelegateForFunctionPointer<T>(at) : null;

        NoArgs? enabled = Find<NoArgs>("vJoyEnabled");
        Short? version = Find<Short>("GetvJoyVersion");
        OfDevice? status = Find<OfDevice>("GetVJDStatus");
        OfAxis? axis = Find<OfAxis>("GetVJDAxisExist");
        if (enabled is null || version is null || status is null || axis is null)
            return new VJoyProbeResult { Problem = "vJoy's library lacks the functions Studio asks it (vJoyEnabled, GetvJoyVersion, GetVJDStatus, GetVJDAxisExist)." };

        bool on = enabled() != 0;
        int driver = (ushort)version();
        bool? match = null;
        int dllVersion = 0;
        if (Find<Match>("DriverMatch") is { } driverMatch)
        {
            match = driverMatch(out ushort ofDll, out _) != 0;
            dllVersion = ofDll;
        }
        int[] devices = [.. Enumerable.Range(1, 16).Where(id => status((uint)id) is 0 or 1 or 2)];
        int state = status(1);
        // Only a device that is set up and known has axes worth asking about (not set up, or vJoy could not say: none).
        bool known = state is 0 or 1 or 2;
        Dictionary<string, bool> axes = known ? Usages.ToDictionary(u => u.Key, u => AxisThere(axis(1, u.Value))) : [];
        int buttons = known ? Find<OfDevice>("GetVJDButtonNumber")?.Invoke(1) ?? 0 : 0;
        return new VJoyProbeResult
        {
            Enabled = on, Version = driver, DllVersion = dllVersion, DriverMatch = match, Status = state,
            Owner = state == 2 ? OwnerName(Find<OfDevice>("GetOwnerPid")?.Invoke(1) ?? 0) : null,
            Axes = axes, Buttons = Math.Max(0, buttons), Devices = devices,
        };
    }

    /// <summary>The program holding device 1, by its process id ("joystick_gremlin"); null when vJoy could not say.</summary>
    static string? OwnerName(int pid)
    {
        if (pid <= 0) return null;
        try
        {
            using Process owner = Process.GetProcessById(pid);
            return owner.ProcessName;
        }
        catch (Exception problem) when (problem is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;   // gone since, or not one this user may look at
        }
    }

    const uint WerNoUi = 0x20;   // WER_FAULT_REPORTING_NO_UI
    const uint SemFailCriticalErrors = 0x0001, SemNoGpFaultErrorBox = 0x0002, SemNoOpenFileErrorBox = 0x8000;

    [DllImport("kernel32.dll")]
    static extern int WerSetFlags(uint flags);

    [DllImport("kernel32.dll")]
    static extern uint GetErrorMode();

    [DllImport("kernel32.dll")]
    static extern uint SetErrorMode(uint mode);
}

/// <summary>Studio's side of the helper: runs it, waits for its answer (a few seconds at most), and reads it.</summary>
public static class VJoyHelper
{
    /// <summary>How long the helper may take before it is stopped and the answer counts as "could not ask".</summary>
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);

    /// <summary>Runs the helper on <paramref name="dll"/>; null when Studio's own program cannot be found.</summary>
    public static async Task<VJoyProbeResult?> Ask(string dll)
    {
        // Only Studio's own program answers as the helper (a test host, for one, would not).
        string? program = Environment.ProcessPath;
        if (program is null || !Path.GetFileNameWithoutExtension(program).Equals("EliteStudio", StringComparison.OrdinalIgnoreCase)) return null;
        var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        start.ArgumentList.Add("--" + VJoyProbe.Option);
        start.ArgumentList.Add(dll);
        try
        {
            using Process helper = Process.Start(start) ?? throw new InvalidOperationException("the helper did not start");
            Task<string> output = helper.StandardOutput.ReadToEndAsync();
            using var limit = new CancellationTokenSource(Wait);
            try
            {
                await helper.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                helper.Kill(entireProcessTree: true);
                return new VJoyProbeResult { Problem = "vJoy did not answer within a few seconds." };
            }
            string text = await output;
            // The helper always answers and leaves with 0; no answer and another exit code means vJoy's library crashed it.
            if (string.IsNullOrWhiteSpace(text))
                return new VJoyProbeResult { Problem = helper.ExitCode != 0
                    ? $"vJoy's library crashed when Studio asked it (exit code 0x{helper.ExitCode:X8})."
                    : "The vJoy helper gave no answer." };
            return JsonSerializer.Deserialize<VJoyProbeResult>(text, VJoyProbe.Json) ?? new VJoyProbeResult { Problem = "The vJoy helper gave no answer." };
        }
        catch (Exception problem) when (problem is Win32Exception or InvalidOperationException or JsonException or IOException)
        {
            Log.Error("Studio Flight: the vJoy helper failed", problem);
            return new VJoyProbeResult { Problem = "Studio's vJoy helper failed: " + problem.Message };
        }
    }
}
