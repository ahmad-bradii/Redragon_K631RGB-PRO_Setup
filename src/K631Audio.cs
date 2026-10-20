// Music mode: listens to what the PC is playing (WASAPI loopback on the default output device),
// splits it into 16 frequency bands and publishes the band levels to the page ~30 times a second.
// Only the 16 levels leave this class; no audio is recorded or stored.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace K631Live
{
    class AudioMeter
    {
        public const int Bands = 16;
        const int FftSize = 1024;
        const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        const int AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
        const double MinPeak = 1.5;   // auto-gain floor, so silence doesn't get amplified into flicker

        readonly EventHub hub;
        readonly object gate = new object();
        readonly ManualResetEvent started = new ManualResetEvent(false);
        volatile bool running;
        volatile int generation;   // each Start gets its own listener; an older one finishing can't stop a newer one
        volatile string lastError;
        volatile string style = "wave";
        volatile bool toKeyboard = true;
        volatile string keyboardError;
        readonly MusicRenderer renderer = new MusicRenderer();
        bool streaming;            // live frames are being sent (audio thread only)

        public AudioMeter(EventHub hub)
        {
            this.hub = hub;
        }

        public bool IsRunning { get { return running; } }
        public string LastError { get { return lastError; } }

        public string Style
        {
            get { return style; }
            set
            {
                if (Array.IndexOf(MusicRenderer.Styles, value) < 0) throw new ArgumentException("Unknown music style.");
                style = value;
                hub.Publish("music", StateJson());
            }
        }

        // Whether the real keyboard follows the music (live frames), not just the preview.
        public bool ToKeyboard
        {
            get { return toKeyboard; }
            set
            {
                toKeyboard = value;
                keyboardError = null;   // switching it on again retries after an error
                hub.Publish("music", StateJson());
            }
        }

        public string StateJson()
        {
            Dictionary<string, object> state = new Dictionary<string, object>();
            state["on"] = running;
            state["style"] = style;
            state["keys"] = toKeyboard;
            state["keysError"] = keyboardError;
            return new JavaScriptSerializer().Serialize(state);
        }

        public void Start()
        {
            lock (gate)
            {
                if (running) return;
                lastError = null;
                running = true;
                generation++;
                started.Reset();
                Thread worker = new Thread(Run);
                worker.IsBackground = true;
                worker.SetApartmentState(ApartmentState.MTA);
                worker.Start(generation);
                started.WaitOne(3000);   // report device errors to the caller
            }
        }

        public void Stop()
        {
            running = false;
        }

        void Run(object state)
        {
            int myGeneration = (int)state;
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            IAudioClient client = null;
            IAudioCaptureClient capture = null;
            IntPtr format = IntPtr.Zero;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                Check(enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out device));
                Guid clientId = typeof(IAudioClient).GUID;
                object activated;
                Check(device.Activate(ref clientId, 23 /* CLSCTX_ALL */, IntPtr.Zero, out activated));
                client = (IAudioClient)activated;

                Check(client.GetMixFormat(out format));
                int channels = Marshal.ReadInt16(format, 2);
                int rate = Marshal.ReadInt32(format, 4);
                int bits = Marshal.ReadInt16(format, 14);
                if (bits != 32 && bits != 16) throw new NotSupportedException(bits + "-bit audio output is not supported.");

                Check(client.Initialize(0 /* shared */, AUDCLNT_STREAMFLAGS_LOOPBACK, 10000000, 0, format, IntPtr.Zero));
                Guid captureId = typeof(IAudioCaptureClient).GUID;
                object service;
                Check(client.GetService(ref captureId, out service));
                capture = (IAudioCaptureClient)service;
                Check(client.Start());

                hub.Publish("music", StateJson());
                started.Set();
                Listen(capture, channels, rate, bits == 32, myGeneration);
                client.Stop();
                if (streaming) EndKeyboardMusic();
            }
            catch (Exception ex)
            {
                if (myGeneration == generation) lastError = "Couldn't listen to the PC's sound: " + ex.Message;
            }
            finally
            {
                started.Set();
                if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
                foreach (object com in new object[] { capture, client, device, enumerator })
                    if (com != null) Marshal.ReleaseComObject(com);
                if (streaming) EndKeyboardMusic();
                if (myGeneration == generation)
                {
                    running = false;
                    hub.Publish("music", StateJson());
                }
            }
        }

        void Listen(IAudioCaptureClient capture, int channels, int rate, bool isFloat, int myGeneration)
        {
            float[] ring = new float[FftSize];
            int ringPos = 0;
            float[] floats = new float[0];
            short[] shorts = new short[0];
            double[] re = new double[FftSize], im = new double[FftSize];
            int[] edges = BandEdges(rate);
            double peak = MinPeak;
            Stopwatch clock = Stopwatch.StartNew();
            long lastPublish = 0, lastData = 0, noClientsSince = -1;

            while (running && myGeneration == generation)
            {
                uint packet;
                Check(capture.GetNextPacketSize(out packet));
                while (packet > 0)
                {
                    IntPtr data;
                    uint frames, flags;
                    ulong devicePosition, qpcPosition;
                    Check(capture.GetBuffer(out data, out frames, out flags, out devicePosition, out qpcPosition));
                    int count = (int)frames * channels;
                    bool silent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                    if (isFloat && !silent)
                    {
                        if (floats.Length < count) floats = new float[count];
                        Marshal.Copy(data, floats, 0, count);
                    }
                    else if (!silent)
                    {
                        if (shorts.Length < count) shorts = new short[count];
                        Marshal.Copy(data, shorts, 0, count);
                    }
                    for (int f = 0; f < frames; f++)
                    {
                        float sum = 0;
                        if (!silent)
                            for (int c = 0; c < channels; c++)
                                sum += isFloat ? floats[f * channels + c] : shorts[f * channels + c] / 32768f;
                        ring[ringPos] = sum / channels;
                        ringPos = (ringPos + 1) % FftSize;
                    }
                    Check(capture.ReleaseBuffer(frames));
                    lastData = clock.ElapsedMilliseconds;
                    Check(capture.GetNextPacketSize(out packet));
                }

                long now = clock.ElapsedMilliseconds;
                // Loopback delivers nothing while nothing plays; treat that as silence.
                if (now - lastData > 150) Array.Clear(ring, 0, FftSize);

                if (now - lastPublish >= 33)
                {
                    lastPublish = now;
                    for (int i = 0; i < FftSize; i++)
                    {
                        double window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1));
                        re[i] = ring[(ringPos + i) % FftSize] * window;
                        im[i] = 0;
                    }
                    Fft(re, im);

                    double[] level = new double[Bands];
                    double loudest = 0;
                    for (int b = 0; b < Bands; b++)
                    {
                        for (int k = edges[b]; k < edges[b + 1]; k++)
                            level[b] = Math.Max(level[b], Math.Sqrt(re[k] * re[k] + im[k] * im[k]));
                        loudest = Math.Max(loudest, level[b]);
                    }
                    // Automatic gain: jump up to loud passages, ease back down over a few seconds,
                    // so both quiet and loud music fill the whole range.
                    peak = Math.Max(MinPeak, Math.Max(loudest, peak * 0.995));

                    double[] levels = new double[Bands];
                    StringBuilder json = new StringBuilder("{\"b\":[");
                    for (int b = 0; b < Bands; b++)
                    {
                        levels[b] = Math.Min(1.0, Math.Sqrt(level[b] / peak));
                        if (b > 0) json.Append(',');
                        json.Append((int)Math.Round(levels[b] * 100));
                    }

                    // One frame for both the keyboard and the preview, so they always match.
                    byte[] frame = renderer.Render(style, levels);
                    if (toKeyboard && keyboardError == null)
                    {
                        try
                        {
                            K631Lighting.SendLiveFrame(frame);
                            streaming = true;
                        }
                        catch (LightingException ex)
                        {
                            keyboardError = ex.Message;
                            streaming = false;
                            hub.Publish("music", StateJson());
                        }
                    }
                    else if (!toKeyboard && streaming)
                    {
                        EndKeyboardMusic();
                    }

                    json.Append("],\"k\":").Append(streaming ? "true" : "false").Append(",\"f\":\"");
                    foreach (byte value in frame) json.Append(value.ToString("x2"));
                    hub.Publish("audio", json.Append("\"}").ToString());
                }

                // Stop by itself once no page or overlay has been connected for 5 seconds.
                if (hub.ClientCount > 0) noClientsSince = -1;
                else if (noClientsSince < 0) noClientsSince = now;
                else if (now - noClientsSince > 5000) running = false;

                Thread.Sleep(10);
            }
        }

        // Gives the keyboard its own effect back after live frames.
        void EndKeyboardMusic()
        {
            streaming = false;
            try { K631Lighting.EndLive(); }
            catch (Exception ex)
            {
                keyboardError = "Couldn't hand the keyboard back to its effect: " + ex.Message;
                hub.Publish("music", StateJson());
            }
        }

        // FFT bin boundaries for 16 bands spaced evenly on a log scale from 40 Hz to 16 kHz.
        static int[] BandEdges(int rate)
        {
            int[] edges = new int[Bands + 1];
            double top = Math.Min(16000, rate / 2.0);
            for (int b = 0; b <= Bands; b++)
            {
                double freq = 40 * Math.Pow(top / 40, (double)b / Bands);
                edges[b] = Math.Min(FftSize / 2, (int)Math.Round(freq * FftSize / rate));
                if (b > 0 && edges[b] <= edges[b - 1]) edges[b] = Math.Min(FftSize / 2, edges[b - 1] + 1);
            }
            return edges;
        }

        // In-place iterative radix-2 FFT.
        static void Fft(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    double t = re[i]; re[i] = re[j]; re[j] = t;
                    t = im[i]; im[i] = im[j]; im[j] = t;
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double angle = -2 * Math.PI / len, wr = Math.Cos(angle), wi = Math.Sin(angle);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = a + len / 2;
                        double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                        double next = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = next;
                    }
                }
            }
        }

        static void Check(int hr)
        {
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioClient
        {
            int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
            int GetBufferSize(out uint frames);
            int GetStreamLatency(out long latency);
            int GetCurrentPadding(out uint frames);
            int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            int GetMixFormat(out IntPtr format);
            int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            int Start();
            int Stop();
            int Reset();
            int SetEventHandle(IntPtr handle);
            int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioCaptureClient
        {
            int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
            int ReleaseBuffer(uint frames);
            int GetNextPacketSize(out uint frames);
        }
    }
}
