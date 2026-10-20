using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using EliteStudio.Flight;

namespace EliteStudio.Gimbalyn;

/// <summary>
/// Feeds vJoy through vJoy's own library, loaded from vJoy's install folder (found as Studio Flight's vJoy card finds it, and
/// only on this PC's own disks), so the library always matches the driver. Only the calls that work across vJoy versions
/// are used: one axis at a time, and the range read from vJoy itself. vJoy's own reset is never called: it sends some
/// axes to an end, not the middle. This runs only in the Gimbalyn process (never inside Studio).
/// </summary>
public sealed class VJoyFeeder : IVJoyOutput
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NoArgs();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate short Short();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int OfDevice(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void ForDevice(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int OfAxis(uint id, uint usage);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int AxisValue(uint id, uint usage, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SetValue(int value, uint id, uint usage);

    readonly Func<string?> folder;
    NoArgs? enabled;
    Short? version;
    OfDevice? status, acquire, ownerPid;
    ForDevice? relinquish;
    OfAxis? axisExists;
    AxisValue? axisMin, axisMax;
    SetValue? setAxis;

    /// <param name="folder">vJoy's folder when picked by hand ("" or null: where Windows lists it).</param>
    public VJoyFeeder(Func<string?> folder) => this.folder = folder;

    /// <summary>Loads vJoy's library once; null when loaded, else why it could not be.</summary>
    string? Load()
    {
        if (setAxis is not null) return null;
        VJoyInstall found = VJoyFinder.Look(folder());
        if (found.Dll is not { } dll) return "vJoy is not installed (its library was not found).";
        if (!NativeLibrary.TryLoad(dll, out IntPtr library)) return "Windows would not load vJoy's library.";
        T? Find<T>(string name) where T : Delegate => NativeLibrary.TryGetExport(library, name, out IntPtr at) ? Marshal.GetDelegateForFunctionPointer<T>(at) : null;
        (enabled, version, status, acquire, ownerPid) = (Find<NoArgs>("vJoyEnabled"), Find<Short>("GetvJoyVersion"), Find<OfDevice>("GetVJDStatus"),
            Find<OfDevice>("AcquireVJD"), Find<OfDevice>("GetOwnerPid"));
        (relinquish, axisExists, axisMin, axisMax) = (Find<ForDevice>("RelinquishVJD"), Find<OfAxis>("GetVJDAxisExist"), Find<AxisValue>("GetVJDAxisMin"),
            Find<AxisValue>("GetVJDAxisMax"));
        SetValue? set = Find<SetValue>("SetAxis");
        if (enabled is null || version is null || status is null || acquire is null || relinquish is null || axisExists is null || axisMin is null || axisMax is null || set is null)
            return "vJoy's library lacks the functions Gimbalyn feeds it with.";
        setAxis = set;
        return null;
    }

    public VJoyTake Take(int device)
    {
        if (Load() is { } why) return new VJoyTake(false, Problem: why);
        if (enabled!() == 0) return new VJoyTake(false, Problem: "vJoy is installed but not running (tick \"Enable vJoy\" in Configure vJoy).");
        if ((ushort)version!() is > 0 and < VJoyState.OldestVersion) return new VJoyTake(false, Problem: "This vJoy is too old: 2.1.8 or newer is needed.");
        switch (status!((uint)device))
        {
            case 0: return new VJoyTake(true);   // already this program's
            case 1: return acquire!((uint)device) != 0 ? new VJoyTake(true) : new VJoyTake(false, Owner(device), $"vJoy device {device} could not be taken.");
            case 2: return new VJoyTake(false, Owner(device) ?? "another program");
            default: return new VJoyTake(false, Problem: $"vJoy device {device} is not set up (add it in Configure vJoy).");
        }
    }

    /// <summary>The program holding a device, by name; null when vJoy cannot say.</summary>
    string? Owner(int device)
    {
        int pid = ownerPid?.Invoke((uint)device) ?? 0;
        if (pid <= 0) return null;
        try
        {
            using Process holder = Process.GetProcessById(pid);
            return holder.ProcessName;
        }
        catch (Exception problem) when (problem is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    public bool Owns(int device) => status is not null && status((uint)device) == 0;

    public (int Min, int Max)? Range(int device, string axis)
    {
        if (setAxis is null || !VJoyProbe.Usages.TryGetValue(axis, out uint usage) || !VJoyProbe.AxisThere(axisExists!((uint)device, usage))) return null;
        // A range vJoy cannot tell is its usual one (2.1.x and 2.2.x alike).
        bool known = axisMin!((uint)device, usage, out int min) != 0 & axisMax!((uint)device, usage, out int max) != 0 && max > min;
        return known ? (min, max) : (0, 32767);
    }

    public bool Set(int device, string axis, int raw) =>
        setAxis is not null && VJoyProbe.Usages.TryGetValue(axis, out uint usage) && setAxis(raw, (uint)device, usage) != 0;

    public void Release(int device) => relinquish?.Invoke((uint)device);

    /// <summary>
    /// Studio's way out when Gimbalyn ended mid-turn (EliteStudio.exe --gimbalyn-centre): takes the device Gimbalyn fed if
    /// nobody holds it, sets the axes it routed (every axis the device has, when none are named) to the middle, and lets go.
    /// A device another program holds is that program's, and is left alone.
    /// </summary>
    public static string CentreOnce(IVJoyOutput output, int device, IReadOnlyCollection<string> axes)
    {
        VJoyTake take = output.Take(device);
        if (!take.Taken) return take.Owner is not null ? $"left alone: {take.Owner} holds vJoy device {device}" : "not centred: " + take.Problem;
        IEnumerable<string> which = axes.Count > 0 ? axes : GimbalynProfile.VJoyAxes;
        int centred = 0, failed = 0;
        foreach (string axis in which)
            if (output.Range(device, axis) is { } range) { if (output.Set(device, axis, Engine.ToRaw(0, range.Min, range.Max))) centred++; else failed++; }
        output.Release(device);
        return $"centred {centred} axes of vJoy device {device}" + (failed > 0 ? $"; {failed} would not take it" : "");
    }
}