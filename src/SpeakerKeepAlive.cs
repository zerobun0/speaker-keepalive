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
// The tone ducks under other apps' peaks so tone + music never clips (hardware-volume devices).
//
// Also (2.0): remembers the output you picked and each output's volume, and puts them back when
// Windows or FxSound change them by themselves (device plugged in, monitors waking, sleep, restart).
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

    // Undocumented but long-stable (Windows 7..11) interface used by every "switch default device" tool.
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigCo { }

    [Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPolicyConfig
    {
        int GetMixFormat(); int GetDeviceFormat(); int ResetDeviceFormat(); int SetDeviceFormat(); int GetProcessingPeriod();
        int SetProcessingPeriod(); int GetShareMode(); int SetShareMode(); int GetPropertyValue(); int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
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
        public double Ceiling = 0.95;         // tone + other apps' peaks are kept under this (full scale = 1)
        public double DuckReleaseSeconds = 15;// how slowly the tone comes back after other audio gets quiet
        public bool RememberOutput = true;    // switch back to the user's output when Windows/FxSound change it by themselves
        public bool RememberVolume = true;    // put each output's volume back when it jumps by itself
        public string VolumeCapDevice = "";   // outputs whose name contains this start capped... ("" = no cap)
        public double VolumeCapPercent = 10;  // ...at this volume, unless the user sets them higher

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
                        case "ceiling": c.Ceiling = Math.Max(0.1, Math.Min(1, double.Parse(v, inv))); break;
                        case "duckreleaseseconds": c.DuckReleaseSeconds = Math.Max(0.1, double.Parse(v, inv)); break;
                        case "rememberoutput": c.RememberOutput = v == "1" || v.ToLowerInvariant() == "true"; break;
                        case "remembervolume": c.RememberVolume = v == "1" || v.ToLowerInvariant() == "true"; break;
                        case "volumecapdevice": c.VolumeCapDevice = v; break;
                        case "volumecappercent": c.VolumeCapPercent = Math.Max(0, Math.Min(100, double.Parse(v, inv))); break;
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

        // Make a device the Windows default output (console + multimedia; communications is left alone).
        // FxSound notices within a few seconds, switches its own output to that device and takes the default back.
        public static bool SetDefault(string id)
        {
            try
            {
                var pc = (IPolicyConfig)new PolicyConfigCo();
                int a = pc.SetDefaultEndpoint(id, 0), b = pc.SetDefaultEndpoint(id, 1);
                Marshal.ReleaseComObject(pc);
                return a == 0 && b == 0;
            }
            catch { return false; }
        }

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

        // Peak meters for every session on the device that isn't ours (caller releases them).
        public static List<IAudioMeterInformation> OtherMeters(IMMDevice d, int selfPid)
        {
            var list = new List<IAudioMeterInformation>();
            IAudioSessionManager2 mgr = null; IAudioSessionEnumerator en = null;
            try
            {
                mgr = Activate<IAudioSessionManager2>(d);
                if (mgr.GetSessionEnumerator(out en) != 0) return list;
                int n; en.GetCount(out n);
                for (int i = 0; i < n; i++)
                {
                    object s; if (en.GetSession(i, out s) != 0 || s == null) continue;
                    var c2 = s as IAudioSessionControl2; var meter = s as IAudioMeterInformation; int pid = -1;
                    if (c2 != null) c2.GetProcessId(out pid);
                    if (meter != null && pid != selfPid) list.Add(meter); else Marshal.ReleaseComObject(s);
                }
            }
            catch { }
            finally { if (en != null) Marshal.ReleaseComObject(en); if (mgr != null) Marshal.ReleaseComObject(mgr); }
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
        readonly IMMDevice dev;
        readonly IAudioClient client; readonly IAudioRenderClient render; readonly IAudioEndpointVolume vol;
        readonly AutoResetEvent evt = new AutoResetEvent(false);
        readonly int channels, rate, bufferFrames; readonly bool isFloat; readonly int bytesPerSample;
        readonly float[] fbuf; readonly byte[] bbuf;   // reused every period: no per-sample allocations
        double phase, amp; readonly Config cfg; readonly int selfPid;
        public double LastVolumeDb; public double LastAmp; public bool Muted;

        // Peak meters of every other app's session on this device, refreshed once a second.
        List<IAudioMeterInformation> others = new List<IAudioMeterInformation>();
        DateTime nextMeters = DateTime.MinValue;
        readonly Stopwatch duckClock = Stopwatch.StartNew();
        double duckPeak; public double LastDuckPeak;

        public ToneStream(IMMDevice dev, Config cfg, int selfPid)
        {
            this.cfg = cfg; this.dev = dev; this.selfPid = selfPid;
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
            if (isFloat && bytesPerSample == 4) fbuf = new float[bufferFrames * channels]; else bbuf = new byte[bufferFrames * channels * bytesPerSample];
            Guid g = typeof(IAudioRenderClient).GUID; object o;
            Check(client.GetService(ref g, out o), "GetService"); render = (IAudioRenderClient)o;
            Fill(bufferFrames, 0); // prime with silence
            Check(client.Start(), "Start");
            Log.W(string.Format("stream open on \"{0}\" ({1} Hz, {2} ch, {3}-bit {4}, buffer {5} frames)",
                DeviceName, rate, channels, bytesPerSample * 8, isFloat ? "float" : "pcm", bufferFrames));
        }

        static void Check(int hr, string what) { if (hr != 0) throw new COMException(what, hr); }

        void ReleaseMeters() { foreach (var m in others) try { Marshal.ReleaseComObject(m); } catch { } others.Clear(); }

        // Desired generated amplitude so that (amplitude * endpoint gain) == target level,
        // limited so that tone + everything else on the device never exceeds full scale.
        // With HARDWARE endpoint volume (Realtek) the digital mix is sent at full scale and the
        // codec attenuates afterwards, so a big tone plus music clips / hits the limiter = crackle.
        double DesiredAmp(bool fadingOut)
        {
            var now = DateTime.Now;
            if (now >= nextMeters) { nextMeters = now.AddSeconds(1); ReleaseMeters(); others = Audio.OtherMeters(dev, selfPid); }
            // Headroom: sum of other sessions' peaks (upper bound of their mix), held with a slow release
            // so gaps between songs don't let the tone climb back and clip the next song's first beat.
            double dt = duckClock.Elapsed.TotalSeconds; duckClock.Restart();
            double peakNow = 0;
            foreach (var m in others) { float p; try { if (m.GetPeakValue(out p) == 0) peakNow += p; } catch { } }
            duckPeak = Math.Max(peakNow, duckPeak * Math.Exp(-dt / cfg.DuckReleaseSeconds));
            LastDuckPeak = duckPeak;
            if (fadingOut) return 0;
            float db; bool mute;
            if (vol.GetMasterVolumeLevel(out db) != 0) db = 0;
            vol.GetMute(out mute); Muted = mute; LastVolumeDb = db;
            if (mute) return 0; // muted on purpose: let it be quiet
            double a = Math.Pow(10, (cfg.TargetDbfs - db) / 20.0);
            double room = Math.Max(0, cfg.Ceiling - duckPeak);
            return Math.Min(Math.Min(a, cfg.MaxAmplitude), room);
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
            // Rising: ~150 ms ramp (no clicks). Falling: ~5 ms (volume went up / music arrived, back off right away).
            double up = 1.0 / (0.150 * rate), down = 1.0 / (0.005 * rate);
            for (int f = 0; f < frames; f++)
            {
                if (amp < target) amp = Math.Min(target, amp + (target - amp) * up + 1e-7);
                else if (amp > target) amp = Math.Max(target, amp - (amp - target) * down - 1e-7);
                double s = amp * Math.Sin(phase);
                phase += step; if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
                for (int c = 0; c < channels; c++)
                {
                    if (fbuf != null) { fbuf[f * channels + c] = (float)s; continue; }
                    int o = (f * channels + c) * bytesPerSample;
                    if (bytesPerSample == 2) { int v = (int)(s * 32767); bbuf[o] = (byte)v; bbuf[o + 1] = (byte)(v >> 8); }
                    else if (bytesPerSample == 3) { int v = (int)(s * 8388607); bbuf[o] = (byte)v; bbuf[o + 1] = (byte)(v >> 8); bbuf[o + 2] = (byte)(v >> 16); }
                    else if (bytesPerSample == 4) { int v = (int)(s * 2147483647.0); bbuf[o] = (byte)v; bbuf[o + 1] = (byte)(v >> 8); bbuf[o + 2] = (byte)(v >> 16); bbuf[o + 3] = (byte)(v >> 24); }
                }
            }
            if (fbuf != null) Marshal.Copy(fbuf, 0, buf, frames * channels);
            else Marshal.Copy(bbuf, 0, buf, frames * channels * bytesPerSample);
            LastAmp = amp;
            return render.ReleaseBuffer(frames, 0) == 0;
        }

        public void Dispose()
        {
            try { client.Stop(); } catch { }
            try { Marshal.ReleaseComObject(render); } catch { }
            try { Marshal.ReleaseComObject(client); } catch { }
            try { Marshal.ReleaseComObject(vol); } catch { }
            ReleaseMeters();
            evt.Dispose();
        }
    }

    // ---------------------------------------------------------------- render thread
    // Owns the tone stream so the slow housekeeping on the main thread (device scans, file reads,
    // switching outputs) can never make it miss an audio period.
    static class Renderer
    {
        public static volatile string WantId;   // device to keep alive, null = fade out and stop
        public static volatile string Who;      // app whose sound turned us on (for the log)
        public static volatile bool Reset;      // rebuild the stream (after sleep)

        [DllImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", CharSet = CharSet.Unicode)]
        static extern IntPtr AvSetMmThreadCharacteristics(string task, ref int index);

        public static void Start(Config cfg, int selfPid)
        {
            var t = new Thread(() => Run(cfg, selfPid)) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "render" };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        static void Run(Config cfg, int selfPid)
        {
            try { int i = 0; AvSetMmThreadCharacteristics("Audio", ref i); } catch { }
            ToneStream s = null; DateTime nextStatus = DateTime.Now.AddMinutes(1);
            while (true)
            {
                try
                {
                    if (Reset) { Reset = false; if (s != null) { s.Dispose(); s = null; } }
                    string want = WantId;
                    if (s != null && want != null && want != s.DeviceId) { s.Dispose(); s = null; } // output switched
                    if (s == null)
                    {
                        if (want == null) { Thread.Sleep(50); continue; }
                        IMMDevice d;
                        if (Audio.Enumerator().GetDevice(want, out d) != 0 || d == null) { Thread.Sleep(500); continue; }
                        s = new ToneStream(d, cfg, selfPid);
                        Log.W("keep-alive ON (sound from " + (Who ?? "?") + ")");
                    }
                    bool fading = want == null;
                    if (!s.Pump(fading)) { s.Dispose(); s = null; Thread.Sleep(1000); continue; }
                    if (fading && s.IsSilent) { s.Dispose(); s = null; Log.W("keep-alive OFF (idle " + cfg.HangSeconds + "s or locked)"); continue; }
                    if (DateTime.Now >= nextStatus)
                    {
                        nextStatus = DateTime.Now.AddMinutes(5);
                        Log.W(string.Format("status: \"{0}\" vol {1:0.0} dB{2}, tone amp {3:0.00000} -> {4:0.0} dBFS at speaker, other apps peak {5:0.00}",
                            s.DeviceName, s.LastVolumeDb, s.Muted ? " MUTED" : "", s.LastAmp,
                            s.LastAmp > 0 ? 20 * Math.Log10(s.LastAmp) + s.LastVolumeDb : double.NegativeInfinity, s.LastDuckPeak));
                    }
                }
                catch (Exception ex)
                {
                    Log.W("render error: " + ex.Message);
                    if (s != null) { try { s.Dispose(); } catch { } s = null; }
                    Thread.Sleep(2000);
                }
            }
        }
    }

    // ---------------------------------------------------------------- output + volume memory
    // Windows switches the default output when a device appears (headset dongle, monitor HDMI audio
    // after the screens wake), and FxSound follows it AND mirrors its own slider onto the new device,
    // so the speakers come back on a different output and/or at a random volume. This remembers what
    // the user picked and puts it back when a change happens "by itself".
    class OutputGuard
    {
        const double OutputWindowSec = 12;   // after startup / unlock / wake / device plugged: changes are automatic
        const double VolumeWindowSec = 5;    // after any output switch: volume jumps are automatic
        const int MaxRestores = 3;           // per trigger, in case something keeps overriding us

        readonly Config cfg; readonly string path;
        readonly Dictionary<string, double> vols = new Dictionary<string, double>();
        string preferred, lastEffective, lastSet;
        DateTime outUntil = DateTime.MinValue, volUntil = DateTime.MinValue, lastRestore = DateTime.MinValue;
        int restores; volatile string pending;
        IAudioEndpointVolume volCtl; string volCtlId;

        public OutputGuard(Config cfg, string dir)
        {
            this.cfg = cfg; path = Path.Combine(dir, "SpeakerKeepAlive.state");
            try
            {
                if (File.Exists(path))
                    foreach (var line in File.ReadAllLines(path))
                    {
                        int eq = line.IndexOf('='); if (eq < 0 || line.StartsWith("#")) continue;
                        string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                        if (k == "output") preferred = v;
                        else if (k.StartsWith("volume ")) vols[k.Substring(7)] = double.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
                    }
            }
            catch (Exception ex) { Log.W("state file unreadable, starting fresh: " + ex.Message); }
        }

        void Save()
        {
            try
            {
                var sb = new System.Text.StringBuilder("# SpeakerKeepAlive memory: last output you picked and each output's volume (0..1)\r\n");
                if (preferred != null) sb.Append("output=").Append(preferred).Append("\r\n");
                foreach (var kv in vols) sb.Append("volume ").Append(kv.Key).Append('=').Append(kv.Value.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)).Append("\r\n");
                File.WriteAllText(path + ".tmp", sb.ToString());
                if (File.Exists(path)) File.Replace(path + ".tmp", path, null); else File.Move(path + ".tmp", path);
            }
            catch (Exception ex) { Log.W("could not save state: " + ex.Message); }
        }

        // Thread-safe: called from SystemEvents callbacks.
        public void Trigger(string why) { pending = why; }

        void Open(DateTime now, string why)
        {
            outUntil = now.AddSeconds(OutputWindowSec); volUntil = now.AddSeconds(OutputWindowSec); restores = 0;
            Log.W("watching for automatic output/volume changes (" + why + ")");
        }

        static string Pct(double v) { return Math.Round(v * 100).ToString("0") + "%"; }

        public void Tick(DateTime now, IMMDevice target, List<IMMDevice> all)
        {
            var p = pending; if (p != null) { pending = null; Open(now, p); }

            var ids = new List<string>(); foreach (var d in all) ids.Add(Audio.Id(d)); ids.Sort();
            string set = string.Join("|", ids.ToArray());
            if (lastSet != null && set != lastSet) Open(now, "devices changed");
            lastSet = set;

            string eff = target == null ? null : Audio.Id(target);
            bool switched = eff != lastEffective;
            if (switched && lastEffective != null && now.AddSeconds(VolumeWindowSec) > volUntil) volUntil = now.AddSeconds(VolumeWindowSec);
            lastEffective = eff;
            if (eff == null) return;

            // ---- output memory
            if (cfg.RememberOutput)
            {
                if (preferred == null) { preferred = eff; Save(); Log.W("remembered output \"" + Audio.Name(target) + "\" (first run)"); }
                else if (now < outUntil)
                {
                    if (preferred != null && eff != preferred && ids.Contains(preferred) && restores < MaxRestores
                        && (now - lastRestore).TotalSeconds > 6)
                    {
                        restores++; lastRestore = now;
                        string name = "?"; foreach (var d in all) if (Audio.Id(d) == preferred) name = Audio.Name(d);
                        bool ok = Audio.SetDefault(preferred);
                        Log.W("output changed by itself to \"" + Audio.Name(target) + "\", switching back to \"" + name + "\"" + (ok ? "" : " (FAILED)"));
                        outUntil = now.AddSeconds(OutputWindowSec); volUntil = outUntil;
                    }
                }
                else if (switched && preferred != eff) // only a real switch by the user, not a fallback after unplugging
                {
                    preferred = eff; Save();
                    Log.W("remembered output \"" + Audio.Name(target) + "\"");
                }
            }

            // ---- volume memory for the output that is actually playing
            if (cfg.RememberVolume)
            {
                if (volCtlId != eff)
                {
                    if (volCtl != null) try { Marshal.ReleaseComObject(volCtl); } catch { }
                    volCtl = Audio.Activate<IAudioEndpointVolume>(target); volCtlId = eff;
                }
                float curF; if (volCtl.GetMasterVolumeLevelScalar(out curF) != 0) return;
                double cur = curF, mem;
                bool capped = cfg.VolumeCapDevice.Length > 0 && Audio.Name(target).IndexOf(cfg.VolumeCapDevice, StringComparison.OrdinalIgnoreCase) >= 0;
                double cap = cfg.VolumeCapPercent / 100.0;
                if (!vols.TryGetValue(eff, out mem))
                {
                    mem = capped ? Math.Min(cur, cap) : cur; vols[eff] = mem; Save();
                    Log.W("first time on \"" + Audio.Name(target) + "\": volume " + Pct(mem) + (capped ? " (capped at " + Pct(cap) + " until you set it higher)" : ""));
                    if (Math.Abs(cur - mem) > 0.004) volCtl.SetMasterVolumeLevelScalar((float)mem, IntPtr.Zero);
                }
                else if (Math.Abs(cur - mem) > 0.004)
                {
                    if (now < volUntil)
                    {
                        volCtl.SetMasterVolumeLevelScalar((float)mem, IntPtr.Zero);
                        Log.W("volume on \"" + Audio.Name(target) + "\" jumped to " + Pct(cur) + " by itself, put back to " + Pct(mem));
                    }
                    else { vols[eff] = cur; Save(); } // the user moved it
                }
            }
        }

        public string Describe(List<IMMDevice> all)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var d in all)
            {
                string id = Audio.Id(d); double v;
                sb.AppendLine(string.Format("  {0}{1}{2}", Audio.Name(d), id == preferred ? "  <- remembered output" : "",
                    vols.TryGetValue(id, out v) ? "  remembered volume " + Pct(v) : ""));
            }
            return sb.ToString();
        }
    }

    // ---------------------------------------------------------------- main loop
    static class Program
    {
        const string Version = "2.0.0";
        static volatile bool locked, resumed;
        static Mutex instanceMutex; // static so the GC can never release the single-instance lock
        static readonly int SelfPid = Process.GetCurrentProcess().Id;

        // Find the device that physically drives the speakers.
        static IMMDevice ResolveTarget(Config cfg, List<IMMDevice> all, out IMMDevice defaultDev, out string why)
        {
            defaultDev = null; why = "";
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
                string text;
                using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs)) text = r.ReadToEnd();
                var m = Regex.Match(text, "name=\"output_device_name\"\\s+val=\"([^\"]*)\"");
                return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value) : null;
            }
            catch { return null; }
        }

        // --diag: snapshot of every output, its volume, and every app session with its live peak.
        static void Diag(string dir, Config cfg)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("SpeakerKeepAlive " + Version + " diagnostics  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            var all = Audio.ActiveRenderDevices();
            IMMDevice def; string why;
            var t = ResolveTarget(cfg, all, out def, out why);
            sb.AppendLine("default output : " + (def == null ? "(none)" : Audio.Name(def)));
            sb.AppendLine("keep-alive on  : " + (t == null ? "(none)" : Audio.Name(t)) + "   [" + why + "]");
            sb.AppendLine("memory:"); sb.Append(new OutputGuard(cfg, dir).Describe(all));
            sb.AppendLine();
            foreach (var d in all)
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
            bool created; instanceMutex = new Mutex(true, "Local\\SpeakerKeepAlive.SingleInstance", out created);
            if (!created) return 0;
            var cfg = Config.Load(Path.Combine(dir, "SpeakerKeepAlive.ini"));
            Log.W(string.Format("start v{0}  freq={1}Hz target={2}dBFS maxAmp={3} ceiling={4} hang={5}s alwaysOn={6} device={7} rememberOutput={8} rememberVolume={9} cap={10}@{11}%",
                Version, cfg.FrequencyHz, cfg.TargetDbfs, cfg.MaxAmplitude, cfg.Ceiling, cfg.HangSeconds, cfg.AlwaysOn, cfg.Device,
                cfg.RememberOutput, cfg.RememberVolume, cfg.VolumeCapDevice, cfg.VolumeCapPercent));

            var guard = new OutputGuard(cfg, dir);
            guard.Trigger("startup");
            SystemEvents.SessionSwitch += (s, e) =>
            {
                if (e.Reason == SessionSwitchReason.SessionLock) { locked = true; Log.W("session locked"); }
                else if (e.Reason == SessionSwitchReason.SessionUnlock) { locked = false; Log.W("session unlocked"); guard.Trigger("unlock"); }
            };
            SystemEvents.PowerModeChanged += (s, e) =>
            {
                if (e.Mode == PowerModes.Resume) { resumed = true; Log.W("resumed from sleep"); guard.Trigger("wake from sleep"); }
            };

            Renderer.Start(cfg, SelfPid);
            string lastWhy = null, targetId = null, lastWho = null; DateTime lastSound = DateTime.MinValue;
            DateTime nextResolve = DateTime.MinValue, nextActivity = DateTime.MinValue;
            IMMDevice target = null, defDev = null;

            while (true)
            {
                try
                {
                    var now = DateTime.Now;
                    if (resumed) { resumed = false; Renderer.Reset = true; nextResolve = now; }

                    if (now >= nextResolve)
                    {
                        nextResolve = now.AddMilliseconds(500);
                        var all = Audio.ActiveRenderDevices();
                        string why; var t = ResolveTarget(cfg, all, out defDev, out why);
                        string id = t == null ? null : Audio.Id(t);
                        if (why != lastWhy || id != targetId)
                        {
                            Log.W("target: " + (t == null ? "(none)" : "\"" + Audio.Name(t) + "\"") + "  [" + why + "]");
                            lastWhy = why;
                        }
                        targetId = id; target = t;
                        try { guard.Tick(now, t, all); } catch (Exception ex) { Log.W("guard error: " + ex.Message); }
                    }

                    if (now >= nextActivity)
                    {
                        nextActivity = now.AddMilliseconds(500);
                        string who = null;
                        bool sound = (defDev != null && Audio.HasRealSound(defDev, SelfPid, out who))
                                  || (target != null && Audio.HasRealSound(target, SelfPid, out who));
                        if (sound) { lastSound = now; lastWho = who; }
                    }

                    bool wanted = target != null && !(cfg.PauseWhenLocked && locked)
                                  && (cfg.AlwaysOn || (now - lastSound).TotalSeconds < cfg.HangSeconds);
                    Renderer.Who = lastWho;
                    Renderer.WantId = wanted ? targetId : null;
                    Thread.Sleep(100);
                }
                catch (Exception ex)
                {
                    Log.W("error: " + ex.Message);
                    nextResolve = DateTime.MinValue;
                    Thread.Sleep(2000);
                }
            }
        }
    }
}
