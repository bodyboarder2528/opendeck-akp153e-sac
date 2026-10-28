// Audio output switcher for the Jarvis AKP153E plugin (Jarvis, 28-10-2026). plugin.cjs runs it with:
//   powershell -NoProfile -NonInteractive -Command "Add-Type -Path audio.cs; exit [JarvisAudio]::Run('<cmd>', '<arg>')"
//   list           every active playback device: "<id>\t<name>\t*" (* = current default)
//   next [names]   switch to the next active device, optionally only among those whose name contains one of the
//                  '|'-separated words; prints the new default's name
//   current        prints the default playback device's name
// Uses Windows' own audio COM interfaces (MMDevice API + the undocumented-but-stable IPolicyConfig that Windows'
// Sound settings use), so nothing unsigned runs. C# 5 (Windows PowerShell's compiler).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class JarvisAudio {
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] class PolicyConfigCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr iface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }
    // Method order matters (vtable): the first ten are placeholders we never call.
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig {
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(IntPtr a);
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    }
    [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public int pid; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; }

    const int eRender = 0, DEVICE_STATE_ACTIVE = 1;

    static string Name(IMMDevice d) {
        IPropertyStore ps;
        if (d.OpenPropertyStore(0, out ps) != 0) return "?";
        var key = new PROPERTYKEY { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 };   // PKEY_Device_FriendlyName
        PROPVARIANT v;
        if (ps.GetValue(ref key, out v) != 0 || v.vt != 31 /* VT_LPWSTR */) return "?";
        return Marshal.PtrToStringUni(v.p);
    }

    class Dev { public string Id, Name; }
    static List<Dev> Active(IMMDeviceEnumerator en) {
        var list = new List<Dev>();
        IMMDeviceCollection col; int n;
        Marshal.ThrowExceptionForHR(en.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out col));
        col.GetCount(out n);
        for (int i = 0; i < n; i++) {
            IMMDevice d; string id;
            if (col.Item(i, out d) != 0 || d.GetId(out id) != 0) continue;
            list.Add(new Dev { Id = id, Name = Name(d) });
        }
        return list;
    }
    static string DefaultId(IMMDeviceEnumerator en) {
        IMMDevice d; string id;
        if (en.GetDefaultAudioEndpoint(eRender, 1 /* eMultimedia */, out d) != 0 || d.GetId(out id) != 0) return null;
        return id;
    }
    static void SetDefault(string id) {
        var pc = (IPolicyConfig)new PolicyConfigCom();
        // console + multimedia only: the communications device (Discord etc., e.g. the headset's Chat channel) stays put
        for (int role = 0; role < 2; role++) Marshal.ThrowExceptionForHR(pc.SetDefaultEndpoint(id, role));
    }

    public static int Run(string cmd, string arg) {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        var devs = Active(en);
        string cur = DefaultId(en);
        if (cmd == "list") {
            foreach (var d in devs) Console.WriteLine(d.Id + "\t" + d.Name + (d.Id == cur ? "\t*" : ""));
            return 0;
        }
        if (cmd == "current") {
            foreach (var d in devs) if (d.Id == cur) { Console.WriteLine(d.Name); return 0; }
            Console.WriteLine("none"); return 0;
        }
        if (cmd == "set") {   // set <id>: also used to test the switch without changing anything (set to the current one)
            SetDefault(arg); Console.WriteLine("ok"); return 0;
        }
        if (cmd == "next") {
            var pool = new List<Dev>();
            var words = string.IsNullOrEmpty(arg) ? new string[0] : arg.Split('|');
            foreach (var d in devs) {
                bool keep = words.Length == 0;
                foreach (var w in words) if (w.Trim().Length > 0 && d.Name.IndexOf(w.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) keep = true;
                if (keep) pool.Add(d);
            }
            if (pool.Count == 0) { Console.WriteLine("none"); return 1; }
            int at = pool.FindIndex(d => d.Id == cur);
            var next = pool[(at + 1) % pool.Count];
            SetDefault(next.Id);
            Console.WriteLine(next.Name);
            return 0;
        }
        Console.Error.WriteLine("unknown command " + cmd);
        return 2;
    }
}
