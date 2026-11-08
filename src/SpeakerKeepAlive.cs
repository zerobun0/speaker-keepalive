// SpeakerKeepAlive - keeps powered speakers / DACs from auto-standby at low Windows volume.
//
// Why: Windows volume is applied to the real output device (and FxSound mirrors its slider onto
// it), so at low volume the line-level signal reaching the speakers is tiny (-60..-80 dBFS).
// Speaker auto-standby circuits read that as "no signal" and power the amp down mid-song.
//
// What: plays an inaudible 20 Hz sine on the REAL output endpoint (resolving FxSound to the device
// it forwards to), with amplitude continuously compensated for that endpoint's attenuation, so the
// level at the speaker input stays constant regardless of volume. Runs only while other apps are
// actually producing sound (plus a hang time), pauses on lock, survives device changes and sleep.
//
// Build: csc /target:winexe /out:SpeakerKeepAlive.exe SpeakerKeepAlive.cs   (.NET Framework 4.x)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace SpeakerKeepAlive
{
    // ---------------------------------------------------------------- COM interop (Core Audio)
    [StructLayout(LayoutKind.Sequential)]
    public struct PROPERTYKEY { public Guid fmtid; public int pid; public PROPERTYKEY(Guid g, int p) { fmtid = g; pid = p; } }

    [StructLayout(LayoutKind.Explicit)]
    public struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; [FieldOffset(8)] public long l; }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceCollection { [PreserveSig] int GetCount(out int n); [PreserveSig] int Item(int i, out IMMDevice d); }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int n);
        [PreserveSig] int GetAt(int i, out PROPERTYKEY k);
        [PreserveSig] int GetValue(ref PROPERTYKEY k, out PROPVARIANT v);
    }

    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr p); int UnregisterControlChangeNotify(IntPtr p);
        int GetChannelCount(out int n); int SetMasterVolumeLevel(float db, IntPtr ctx);
        int SetMasterVolumeLevelScalar(float v, IntPtr ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float v);
        int SetChannelVolumeLevel(int c, float db, IntPtr ctx); int SetChannelVolumeLevelScalar(int c, float v, IntPtr ctx);
        int GetChannelVolumeLevel(int c, out float db); int GetChannelVolumeLevelScalar(int c, out float v);
        int SetMute(bool m, IntPtr ctx);
        [PreserveSig] int GetMute(out bool m);
    }

    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioMeterInformation { [PreserveSig] int GetPeakValue(out float peak); }

    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out int frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out int frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long def, out long min);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr h);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }

    [Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(int frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(int frames, int flags);
    }

    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr g, int f, out IntPtr c); int GetSimpleAudioVolume(IntPtr g, int f, out IntPtr v);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator e);
    }

    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int n);
        [PreserveSig] int GetSession(int i, [MarshalAs(UnmanagedType.IUnknown)] out object s);
    }

    [Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        int GetDisplayName(out IntPtr s); int SetDisplayName(string s, IntPtr g); int GetIconPath(out IntPtr s);
        int SetIconPath(string s, IntPtr g); int GetGroupingParam(out Guid g); int SetGroupingParam(ref Guid g, IntPtr c);
        int RegisterAudioSessionNotification(IntPtr n); int UnregisterAudioSessionNotification(IntPtr n);
        int GetSessionIdentifier(out IntPtr s); int GetSessionInstanceIdentifier(out IntPtr s);
        [PreserveSig] int GetProcessId(out int pid);
    }

    // ---------------------------------------------------------------- helpers
    static class Log
    {
        static readonly object Gate = new object();
        public static string PathName;
        public static void W(string msg)
        {
            lock (Gate)
            {
                try
                {
                    var fi = new FileInfo(PathName);
                    if (fi.Exists && fi.Length > 1024 * 1024) { File.Copy(PathName, PathName + ".old", true); File.Delete(PathName); }
                    File.AppendAllText(PathName, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
                }
                catch { }
            }
        }
    }

    class Config
    {
        public double FrequencyHz = 20;      // sub-audible; desktop speakers can't reproduce it audibly
        public double TargetDbfs = -32;       // keep-alive level at the speaker input (after Windows volume)
        public double MaxAmplitude = 0.9;     // safety cap on the generated sample amplitude (before volume)
        public int HangSeconds = 300;         // keep running this long after the last real sound
        public bool PauseWhenLocked = true;
        public bool AlwaysOn = false;         // ignore activity detection, run whenever unlocked
        public string Device = "auto";        // "auto" or a substring of the output's friendly name

        public static Config Load(string path)
        {
            var c = new Config();
            if (!File.Exists(path)) return c;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";") || !line.Contains("=")) continue;
                var k = line.Substring(0, line.IndexOf('=')).Trim().ToLowerInvariant();
                var v = line.Substring(line.IndexOf('=') + 1).Trim();
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                try
                {
                    switch (k)
                    {
                        case "frequencyhz": c.FrequencyHz = double.Parse(v, inv); break;
                        case "targetdbfs": c.TargetDbfs = double.Parse(v, inv); break;
                        case "maxamplitude": c.MaxAmplitude = Math.Max(0, Math.Min(1, double.Parse(v, inv))); break;
                        case "hangseconds": c.HangSeconds = int.Parse(v, inv); break;
                        case "pausewhenlocked": c.PauseWhenLocked = v == "1" || v.ToLowerInvariant() == "true"; break;
                        case "alwayson": c.AlwaysOn = v == "1" || v.ToLowerInvariant() == "true"; break;
                        case "device": c.Device = v; break;
                    }
                }
                catch { Log.W("config: bad value for " + k + ": " + v); }
            }
            return c;
        }
    }

    static class Audio
    {
        public static readonly PROPERTYKEY FriendlyName = new PROPERTYKEY(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
        const int CLSCTX_ALL = 23;

        public static IMMDeviceEnumerator Enumerator() { return (IMMDeviceEnumerator)new MMDeviceEnumeratorCo(); }

        public static string Name(IMMDevice d)
        {
            IPropertyStore ps;
            if (d.OpenPropertyStore(0, out ps) != 0 || ps == null) return "?";
            try
            {
                var key = FriendlyName; PROPVARIANT v;
                if (ps.GetValue(ref key, out v) != 0) return "?";
                return v.vt == 31 && v.p != IntPtr.Zero ? Marshal.PtrToStringUni(v.p) : "?";
            }
            finally { Marshal.ReleaseComObject(ps); }
        }

        public static string Id(IMMDevice d) { string id; d.GetId(out id); return id; }

        public static T Activate<T>(IMMDevice d) where T : class
        {
            Guid g = typeof(T).GUID; object o;
            int hr = d.Activate(ref g, CLSCTX_ALL, IntPtr.Zero, out o);
            if (hr != 0) throw new COMException("Activate " + typeof(T).Name, hr);
            return (T)o;
        }

        public static List<IMMDevice> ActiveRenderDevices()
        {
            var list = new List<IMMDevice>(); IMMDeviceCollection c;
            if (Enumerator().EnumAudioEndpoints(0, 1, out c) != 0) return list;
            int n; c.GetCount(out n);
            for (int i = 0; i < n; i++) { IMMDevice d; if (c.Item(i, out d) == 0) list.Add(d); }
            return list;
        }

        // Peak of any audio session on the device that isn't ours and is actually producing sound.
        public static bool HasRealSound(IMMDevice d, int selfPid, out string who)
        {
            who = null;
            IAudioSessionManager2 mgr = null; IAudioSessionEnumerator en = null;
            try
            {
                mgr = Activate<IAudioSessionManager2>(d);
                if (mgr.GetSessionEnumerator(out en) != 0) return false;
                int n; en.GetCount(out n);
                for (int i = 0; i < n; i++)
                {
                    object s; if (en.GetSession(i, out s) != 0 || s == null) continue;
                    try
                    {
                        var c2 = s as IAudioSessionControl2; var meter = s as IAudioMeterInformation;
                        if (c2 == null || meter == null) continue;
                        int state, pid; c2.GetState(out state); c2.GetProcessId(out pid);
                        if (state != 1 || pid == selfPid) continue;            // 1 = AudioSessionStateActive
                        float peak; meter.GetPeakValue(out peak);
                        if (peak > 1e-5f)
                        {
                            try { who = Process.GetProcessById(pid).ProcessName; } catch { who = "pid " + pid; }
                            return true;
                        }
                    }
                    finally { Marshal.ReleaseComObject(s); }
                }
                return false;
            }
            catch { return false; }
            finally { if (en != null) Marshal.ReleaseComObject(en); if (mgr != null) Marshal.ReleaseComObject(mgr); }
        }
    }

    // ---------------------------------------------------------------- the keep-alive stream
    class ToneStream : IDisposable
    {
        public readonly string DeviceId, DeviceName;
        readonly IAudioClient client; readonly IAudioRenderClient render; readonly IAudioEndpointVolume vol;
        readonly AutoResetEvent evt = new AutoResetEvent(false);
        readonly int channels, rate, bufferFrames; readonly bool isFloat; readonly int bytesPerSample;
        double phase, amp; readonly Config cfg;
        public double LastVolumeDb; public double LastAmp; public bool Muted;

        public ToneStream(IMMDevice dev, Config cfg)
        {
            this.cfg = cfg;
            DeviceId = Audio.Id(dev); DeviceName = Audio.Name(dev);
            vol = Audio.Activate<IAudioEndpointVolume>(dev);
            client = Audio.Activate<IAudioClient>(dev);
            IntPtr fmt; Check(client.GetMixFormat(out fmt), "GetMixFormat");
            try
            {
                ushort tag = (ushort)Marshal.ReadInt16(fmt, 0);
                channels = Marshal.ReadInt16(fmt, 2); rate = Marshal.ReadInt32(fmt, 4);
                int bits = Marshal.ReadInt16(fmt, 14); bytesPerSample = bits / 8;
                isFloat = tag == 3;
                if (tag == 0xFFFE) // WAVEFORMATEXTENSIBLE: SubFormat GUID at offset 24
                {
                    var b = new byte[16]; Marshal.Copy(new IntPtr(fmt.ToInt64() + 24), b, 0, 16);
                    isFloat = new Guid(b) == new Guid("00000003-0000-0010-8000-00aa00389b71");
                }
                // 0x40000 = AUDCLNT_STREAMFLAGS_EVENTCALLBACK ; 0 duration lets the engine choose its period
                Check(client.Initialize(0, 0x40000, 0, 0, fmt, IntPtr.Zero), "Initialize");
            }
            finally { Marshal.FreeCoTaskMem(fmt); }
            Check(client.SetEventHandle(evt.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
            int bf; Check(client.GetBufferSize(out bf), "GetBufferSize"); bufferFrames = bf;
            Guid g = typeof(IAudioRenderClient).GUID; object o;
            Check(client.GetService(ref g, out o), "GetService"); render = (IAudioRenderClient)o;
            Fill(bufferFrames, 0); // prime with silence
            Check(client.Start(), "Start");
            Log.W(string.Format("stream open on \"{0}\" ({1} Hz, {2} ch, {3}-bit {4}, buffer {5} frames)",
                DeviceName, rate, channels, bytesPerSample * 8, isFloat ? "float" : "pcm", bufferFrames));
        }

        static void Check(int hr, string what) { if (hr != 0) throw new COMException(what, hr); }

        // Desired generated amplitude so that (amplitude * endpoint gain) == target level.
        double DesiredAmp(bool fadingOut)
        {
            if (fadingOut) return 0;
            float db; bool mute;
            if (vol.GetMasterVolumeLevel(out db) != 0) db = 0;
            vol.GetMute(out mute); Muted = mute; LastVolumeDb = db;
            if (mute) return 0; // muted on purpose: let it be quiet
            double a = Math.Pow(10, (cfg.TargetDbfs - db) / 20.0);
            return Math.Min(a, cfg.MaxAmplitude);
        }

        // Pump one engine period. Returns false when the device is gone and the stream must be rebuilt.
        public bool Pump(bool fadingOut)
        {
            evt.WaitOne(200);
            int pad; int hr = client.GetCurrentPadding(out pad);
            if (hr != 0) { Log.W("stream lost (0x" + hr.ToString("X8") + ")"); return false; }
            int frames = bufferFrames - pad;
            if (frames <= 0) return true;
            return Fill(frames, DesiredAmp(fadingOut));
        }

        public bool IsSilent { get { return amp < 1e-6; } }

        bool Fill(int frames, double target)
        {
            IntPtr buf; int hr = render.GetBuffer(frames, out buf);
            if (hr != 0) { Log.W("GetBuffer failed (0x" + hr.ToString("X8") + ")"); return false; }
            double step = 2 * Math.PI * cfg.FrequencyHz / rate;
            // Rising: ~150 ms ramp (no clicks). Falling: ~5 ms (volume went up, back off right away).
            double up = 1.0 / (0.150 * rate), down = 1.0 / (0.005 * rate);
            int stride = channels * bytesPerSample;
            var tmp = new byte[frames * stride];
            for (int f = 0; f < frames; f++)
            {
                if (amp < target) amp = Math.Min(target, amp + (target - amp) * up + 1e-7);
                else if (amp > target) amp = Math.Max(target, amp - (amp - target) * down - 1e-7);
                double s = amp * Math.Sin(phase);
                phase += step; if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
                for (int c = 0; c < channels; c++)
                {
                    int o = f * stride + c * bytesPerSample;
                    if (isFloat) { var b = BitConverter.GetBytes((float)s); Buffer.BlockCopy(b, 0, tmp, o, 4); }
                    else if (bytesPerSample == 2) { short v = (short)(s * 32767); tmp[o] = (byte)v; tmp[o + 1] = (byte)(v >> 8); }
                    else if (bytesPerSample == 3) { int v = (int)(s * 8388607); tmp[o] = (byte)v; tmp[o + 1] = (byte)(v >> 8); tmp[o + 2] = (byte)(v >> 16); }
                    else if (bytesPerSample == 4) { int v = (int)(s * 2147483647.0); var b = BitConverter.GetBytes(v); Buffer.BlockCopy(b, 0, tmp, o, 4); }
                }
            }
            Marshal.Copy(tmp, 0, buf, tmp.Length);
            LastAmp = amp;
            return render.ReleaseBuffer(frames, 0) == 0;
        }

        public void Dispose()
        {
            try { client.Stop(); } catch { }
            try { Marshal.ReleaseComObject(render); } catch { }
            try { Marshal.ReleaseComObject(client); } catch { }
            try { Marshal.ReleaseComObject(vol); } catch { }
            evt.Dispose();
        }
    }

    // ---------------------------------------------------------------- main loop
    static class Program
    {
        static volatile bool locked, resumed;
        static readonly int SelfPid = Process.GetCurrentProcess().Id;

        // Find the device that physically drives the speakers.
        static IMMDevice ResolveTarget(Config cfg, out IMMDevice defaultDev, out string why)
        {
            defaultDev = null; why = "";
            var all = Audio.ActiveRenderDevices();
            if (cfg.Device != "auto")
            {
                foreach (var d in all) if (Audio.Name(d).IndexOf(cfg.Device, StringComparison.OrdinalIgnoreCase) >= 0) { why = "config Device=" + cfg.Device; Audio.Enumerator().GetDefaultAudioEndpoint(0, 0, out defaultDev); return d; }
                why = "configured device not present";
                return null;
            }
            IMMDevice def;
            if (Audio.Enumerator().GetDefaultAudioEndpoint(0, 0, out def) != 0 || def == null) { why = "no default output"; return null; }
            defaultDev = def;
            string defName = Audio.Name(def);
            if (defName.IndexOf("FxSound", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // FxSound is a virtual device; it forwards to the output named in its settings file.
                string fxOut = FxSoundOutputName();
                if (fxOut != null)
                    foreach (var d in all) if (string.Equals(Audio.Name(d), fxOut, StringComparison.OrdinalIgnoreCase)) { why = "FxSound -> " + fxOut; return d; }
                why = "FxSound output \"" + fxOut + "\" not found";
                return null;
            }
            why = "default output";
            return def;
        }

        static string FxSoundOutputName()
        {
            try
            {
                var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FxSound", "FxSound.settings");
                if (!File.Exists(p)) return null;
                var m = Regex.Match(File.ReadAllText(p), "name=\"output_device_name\"\\s+val=\"([^\"]*)\"");
                return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value) : null;
            }
            catch { return null; }
        }

        // --diag: snapshot of every output, its volume, and every app session with its live peak.
        static void Diag(string dir, Config cfg)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("SpeakerKeepAlive diagnostics  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            IMMDevice def; string why;
            var t = ResolveTarget(cfg, out def, out why);
            sb.AppendLine("default output : " + (def == null ? "(none)" : Audio.Name(def)));
            sb.AppendLine("keep-alive on  : " + (t == null ? "(none)" : Audio.Name(t)) + "   [" + why + "]");
            sb.AppendLine();
            foreach (var d in Audio.ActiveRenderDevices())
            {
                float db = 0, sc = 0; bool mute = false;
                try { var v = Audio.Activate<IAudioEndpointVolume>(d); v.GetMasterVolumeLevel(out db); v.GetMasterVolumeLevelScalar(out sc); v.GetMute(out mute); } catch { }
                double amp = Math.Min(cfg.MaxAmplitude, Math.Pow(10, (cfg.TargetDbfs - db) / 20.0));
                sb.AppendLine(string.Format("{0}\n    volume {1:0}% = {2:0.0} dB{3}   tone amp needed {4:0.0000} -> {5:0.0} dBFS at speaker",
                    Audio.Name(d), sc * 100, db, mute ? " MUTED" : "", amp, 20 * Math.Log10(amp) + db));
                try
                {
                    var mgr = Audio.Activate<IAudioSessionManager2>(d); IAudioSessionEnumerator en; mgr.GetSessionEnumerator(out en);
                    int n; en.GetCount(out n);
                    for (int i = 0; i < n; i++)
                    {
                        object s; en.GetSession(i, out s);
                        var c2 = (IAudioSessionControl2)s; int st, pid; c2.GetState(out st); c2.GetProcessId(out pid);
                        float pk; ((IAudioMeterInformation)s).GetPeakValue(out pk);
                        string nm; try { nm = pid == 0 ? "system sounds" : Process.GetProcessById(pid).ProcessName; } catch { nm = "?"; }
                        sb.AppendLine(string.Format("      session {0,-24} pid {1,-6} {2,-8} peak {3}", nm, pid,
                            st == 1 ? "active" : st == 0 ? "inactive" : "expired", pk > 0 ? (20 * Math.Log10(pk)).ToString("0.0") + " dBFS" : "-inf"));
                    }
                }
                catch { }
            }
            File.WriteAllText(Path.Combine(dir, "SpeakerKeepAlive-diag.txt"), sb.ToString());
        }

        [MTAThread]
        static int Main(string[] args)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            Log.PathName = Path.Combine(dir, "SpeakerKeepAlive.log");
            if (args.Length > 0 && args[0] == "--diag") { Diag(dir, Config.Load(Path.Combine(dir, "SpeakerKeepAlive.ini"))); return 0; }
            bool created; var mutex = new Mutex(true, "Local\\SpeakerKeepAlive.SingleInstance", out created);
            if (!created) return 0;
            var cfg = Config.Load(Path.Combine(dir, "SpeakerKeepAlive.ini"));
            Log.W(string.Format("start v1.0  freq={0}Hz target={1}dBFS maxAmp={2} hang={3}s alwaysOn={4} device={5}",
                cfg.FrequencyHz, cfg.TargetDbfs, cfg.MaxAmplitude, cfg.HangSeconds, cfg.AlwaysOn, cfg.Device));

            SystemEvents.SessionSwitch += (s, e) =>
            {
                if (e.Reason == SessionSwitchReason.SessionLock) { locked = true; Log.W("session locked"); }
                else if (e.Reason == SessionSwitchReason.SessionUnlock) { locked = false; Log.W("session unlocked"); }
            };
            SystemEvents.PowerModeChanged += (s, e) =>
            {
                if (e.Mode == PowerModes.Resume) { resumed = true; Log.W("resumed from sleep"); }
            };

            ToneStream stream = null; string lastWhy = null; DateTime lastSound = DateTime.MinValue;
            DateTime nextResolve = DateTime.MinValue, nextActivity = DateTime.MinValue, nextStatus = DateTime.Now.AddMinutes(1);
            string targetId = null; IMMDevice target = null, defDev = null; bool fading = false; string lastWho = null;

            while (true)
            {
                try
                {
                    var now = DateTime.Now;
                    if (resumed) { resumed = false; if (stream != null) { stream.Dispose(); stream = null; } nextResolve = now; }

                    if (now >= nextResolve)
                    {
                        nextResolve = now.AddSeconds(2);
                        string why; var t = ResolveTarget(cfg, out defDev, out why);
                        string id = t == null ? null : Audio.Id(t);
                        if (why != lastWhy || id != targetId)
                        {
                            Log.W("target: " + (t == null ? "(none)" : "\"" + Audio.Name(t) + "\"") + "  [" + why + "]");
                            lastWhy = why;
                        }
                        if (id != targetId && stream != null) { stream.Dispose(); stream = null; }
                        targetId = id; target = t;
                    }

                    if (now >= nextActivity)
                    {
                        nextActivity = now.AddMilliseconds(500);
                        string who = null;
                        bool sound = (defDev != null && Audio.HasRealSound(defDev, SelfPid, out who))
                                  || (target != null && Audio.HasRealSound(target, SelfPid, out who));
                        if (sound) { lastSound = now; if (who != lastWho) { lastWho = who; } }
                    }

                    bool wanted = target != null && !(cfg.PauseWhenLocked && locked)
                                  && (cfg.AlwaysOn || (now - lastSound).TotalSeconds < cfg.HangSeconds);

                    if (wanted && stream == null)
                    {
                        stream = new ToneStream(target, cfg); fading = false;
                        Log.W("keep-alive ON (sound from " + (lastWho ?? "?") + ")");
                    }
                    else if (wanted) fading = false;
                    else if (!wanted && stream != null && !fading) { fading = true; }

                    if (stream != null)
                    {
                        if (!stream.Pump(fading)) { stream.Dispose(); stream = null; nextResolve = DateTime.MinValue; Thread.Sleep(1000); continue; }
                        if (fading && stream.IsSilent) { stream.Dispose(); stream = null; fading = false; Log.W("keep-alive OFF (idle " + cfg.HangSeconds + "s or locked)"); }
                        if (stream != null && now >= nextStatus)
                        {
                            nextStatus = now.AddMinutes(5);
                            Log.W(string.Format("status: \"{0}\" vol {1:0.0} dB{2}, tone amp {3:0.00000} -> {4:0.0} dBFS at speaker",
                                stream.DeviceName, stream.LastVolumeDb, stream.Muted ? " MUTED" : "", stream.LastAmp,
                                stream.LastAmp > 0 ? 20 * Math.Log10(stream.LastAmp) + stream.LastVolumeDb : double.NegativeInfinity));
                        }
                    }
                    else Thread.Sleep(100);
                }
                catch (Exception ex)
                {
                    Log.W("error: " + ex.Message);
                    if (stream != null) { try { stream.Dispose(); } catch { } stream = null; }
                    nextResolve = DateTime.MinValue;
                    Thread.Sleep(2000);
                }
            }
        }
    }
}
