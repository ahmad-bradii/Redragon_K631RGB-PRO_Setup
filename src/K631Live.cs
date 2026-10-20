// K631 Live: reads key presses from the Redragon K631 (VID 258A, PID 0049) through
// Windows Raw Input and streams them to a local page at http://localhost:6310/.
// Written for the C# 5 compiler that ships with .NET Framework 4.x (see build.ps1).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace K631Live
{
    static class Program
    {
        public const int Port = 6310;
        public const string K631Id = "VID_258A&PID_0049";
        public static readonly string Url = "http://localhost:" + Port + "/";

        [STAThread]
        static void Main(string[] args)
        {
            bool all = Array.IndexOf(args, "--all") >= 0;
            bool noBrowser = Array.IndexOf(args, "--no-browser") >= 0;

            bool first;
            using (Mutex mutex = new Mutex(true, "Local\\K631Live", out first))
            {
                if (!first)
                {
                    OpenBrowser(Url);
                    return;
                }

                EventHub hub = new EventHub();
                AudioMeter audio = new AudioMeter(hub);
                WebServer server = new WebServer(Port, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web"), hub);
                server.Audio = audio;
                try
                {
                    server.Start();
                }
                catch (SocketException ex)
                {
                    MessageBox.Show("Port " + Port + " is already in use.\n\n" + ex.Message, "K631 Live",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                Application.EnableVisualStyles();
                KeyboardWatcher watcher = new KeyboardWatcher(hub, all);
                server.StatusProvider = watcher.StatusJson;
                using (TrayApp tray = new TrayApp(watcher))
                {
                    if (!noBrowser) OpenBrowser(Url);
                    Application.Run(tray);
                }
                audio.Stop();
                server.Stop();
            }
        }

        public static void OpenBrowser(string url)
        {
            try { Process.Start(url); }
            catch (Exception) { }
        }
    }

    // Fans events out to every connected /events client on its own thread,
    // so a slow browser tab never blocks the raw input message loop.
    class EventHub
    {
        readonly BlockingCollection<string> queue = new BlockingCollection<string>();
        readonly List<Stream> clients = new List<Stream>();
        readonly object gate = new object();

        public EventHub()
        {
            Thread sender = new Thread(SendLoop);
            sender.IsBackground = true;
            sender.Start();
            Thread pinger = new Thread(PingLoop);
            pinger.IsBackground = true;
            pinger.Start();
        }

        public void Publish(string eventName, string json)
        {
            queue.Add((eventName != null ? "event: " + eventName + "\n" : "") + "data: " + json + "\n\n");
        }

        public int ClientCount
        {
            get { lock (gate) return clients.Count; }
        }

        public void AddClient(Stream stream, string hello)
        {
            lock (gate)
            {
                if (Write(stream, hello)) clients.Add(stream);
            }
        }

        void SendLoop()
        {
            foreach (string message in queue.GetConsumingEnumerable())
            {
                lock (gate)
                {
                    for (int i = clients.Count - 1; i >= 0; i--)
                        if (!Write(clients[i], message)) clients.RemoveAt(i);
                }
            }
        }

        // Comment lines keep idle connections alive and let us notice closed tabs.
        void PingLoop()
        {
            while (true)
            {
                Thread.Sleep(15000);
                queue.Add(": ping\n\n");
            }
        }

        static bool Write(Stream stream, string message)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(message);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
                return true;
            }
            catch (Exception)
            {
                try { stream.Close(); } catch (Exception) { }
                return false;
            }
        }
    }

    // Minimal HTTP server on 127.0.0.1: serves web\index.html and the /events stream.
    class WebServer
    {
        readonly int port;
        readonly string webRoot;
        readonly EventHub hub;
        TcpListener listener;
        public Func<string> StatusProvider;
        public AudioMeter Audio;

        public WebServer(int port, string webRoot, EventHub hub)
        {
            this.port = port;
            this.webRoot = webRoot;
            this.hub = hub;
        }

        public void Start()
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            Thread accept = new Thread(AcceptLoop);
            accept.IsBackground = true;
            accept.Start();
        }

        public void Stop()
        {
            try { listener.Stop(); }
            catch (Exception) { }
        }

        void AcceptLoop()
        {
            while (true)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (Exception) { return; }
                ThreadPool.QueueUserWorkItem(Serve, client);
            }
        }

        void Serve(object state)
        {
            TcpClient client = (TcpClient)state;
            bool keepOpen = false;
            try
            {
                NetworkStream stream = client.GetStream();
                stream.ReadTimeout = 5000;
                stream.WriteTimeout = 2000;
                string[] request;
                Dictionary<string, string> headers;
                byte[] body;
                if (!ReadRequest(stream, out request, out headers, out body)) return;

                // Only answer requests addressed to this machine by name. This stops a web page
                // from reading the key stream through its own domain via DNS rebinding.
                if (!IsLocalHost(Header(headers, "host")))
                {
                    Respond(stream, "403 Forbidden", "text/plain", "Forbidden");
                    return;
                }

                string method = request[0];
                string path = request[1];
                int query = path.IndexOf('?');
                if (query >= 0) path = path.Substring(0, query);

                if (path == "/api/lighting")
                {
                    ServeLighting(stream, method, headers, body);
                    return;
                }
                if (path == "/api/layout" || path == "/api/layout/klc")
                {
                    ServeLayout(stream, method, path, headers, body);
                    return;
                }
                if (path == "/api/music")
                {
                    ServeMusic(stream, method, headers, body);
                    return;
                }
                if (method != "GET")
                {
                    Respond(stream, "405 Method Not Allowed", "text/plain", "Method not allowed");
                    return;
                }

                if (path == "/events")
                {
                    byte[] streamHead = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\n" +
                        "Cache-Control: no-cache\r\nConnection: keep-alive\r\n\r\n");
                    stream.Write(streamHead, 0, streamHead.Length);
                    string status = StatusProvider != null ? StatusProvider() : "{}";
                    hub.AddClient(stream, "retry: 1000\nevent: status\ndata: " + status + "\n\n");
                    keepOpen = true;
                    return;
                }
                if (path == "/" || path == "/index.html")
                {
                    string file = Path.Combine(webRoot, "index.html");
                    if (File.Exists(file))
                        Respond(stream, "200 OK", "text/html; charset=utf-8", File.ReadAllBytes(file));
                    else
                        Respond(stream, "404 Not Found", "text/plain", "web\\index.html was not found next to K631Live.exe");
                    return;
                }
                Respond(stream, "404 Not Found", "text/plain", "Not found");
            }
            catch (Exception) { }
            finally
            {
                if (!keepOpen) client.Close();
            }
        }

        // API calls change the keyboard or files, so they must come from our own page. A cross-site
        // request can't carry a custom header without a CORS preflight, which this server never approves.
        bool IsTrustedApiRequest(Dictionary<string, string> headers)
        {
            string origin = Header(headers, "origin");
            return Header(headers, "x-k631") == "1" && (origin == null || IsLocalHost(origin.Replace("http://", "")));
        }

        // Custom key layout: state\layout.json for the editor, export\K631-custom.klc for Windows.
        void ServeLayout(Stream stream, string method, string path, Dictionary<string, string> headers, byte[] body)
        {
            if (!IsTrustedApiRequest(headers))
            {
                Respond(stream, "403 Forbidden", "text/plain", "Forbidden");
                return;
            }

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string layoutFile = Path.Combine(baseDir, "state", "layout.json");
            JavaScriptSerializer json = new JavaScriptSerializer();
            Dictionary<string, object> result = new Dictionary<string, object>();
            try
            {
                if (path == "/api/layout" && method == "GET")
                {
                    Respond(stream, "200 OK", "application/json",
                        File.Exists(layoutFile) ? File.ReadAllText(layoutFile, Encoding.UTF8) : "null");
                    return;
                }
                if (path == "/api/layout" && method == "POST")
                {
                    string text = Encoding.UTF8.GetString(body);
                    if (json.Deserialize<Dictionary<string, object>>(text) == null) throw new InvalidDataException("Layout must be a JSON object.");
                    Directory.CreateDirectory(Path.GetDirectoryName(layoutFile));
                    File.WriteAllText(layoutFile, text, new UTF8Encoding(false));
                    result["ok"] = true;
                }
                else if (path == "/api/layout/klc" && method == "POST")
                {
                    string text = Encoding.UTF8.GetString(body);
                    if (!text.StartsWith("KBD\t", StringComparison.Ordinal)) throw new InvalidDataException("Not a keyboard layout file.");
                    string file = Path.Combine(baseDir, "export", "K631-custom.klc");
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    // Microsoft Keyboard Layout Creator reads .klc files as UTF-16 with a byte order mark.
                    File.WriteAllText(file, text, new UnicodeEncoding(false, true));
                    result["ok"] = true;
                    result["path"] = file;
                }
                else
                {
                    Respond(stream, "405 Method Not Allowed", "text/plain", "Method not allowed");
                    return;
                }
            }
            catch (Exception ex)
            {
                result["ok"] = false;
                result["error"] = ex.Message;
            }
            Respond(stream, "200 OK", "application/json", json.Serialize(result));
        }

        // Music mode on/off. Band levels then arrive on /events as "audio" messages.
        void ServeMusic(Stream stream, string method, Dictionary<string, string> headers, byte[] body)
        {
            if (!IsTrustedApiRequest(headers))
            {
                Respond(stream, "403 Forbidden", "text/plain", "Forbidden");
                return;
            }
            JavaScriptSerializer json = new JavaScriptSerializer();
            string error = null;
            if (method == "POST")
            {
                // Any of: {"on": bool, "style": "wave|bars|bloom|stream|pulse", "keys": bool}
                Dictionary<string, object> args = null;
                try { args = json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(body)); }
                catch (Exception) { }
                if (args == null) error = "Expected a JSON object.";
                else
                {
                    try
                    {
                        if (args.ContainsKey("style")) Audio.Style = Convert.ToString(args["style"]);
                        if (args.ContainsKey("keys")) Audio.ToKeyboard = Convert.ToBoolean(args["keys"]);
                        if (args.ContainsKey("on"))
                        {
                            if (Convert.ToBoolean(args["on"])) Audio.Start();
                            else Audio.Stop();
                        }
                    }
                    catch (ArgumentException ex) { error = ex.Message; }
                }
            }
            else if (method != "GET")
            {
                Respond(stream, "405 Method Not Allowed", "text/plain", "Method not allowed");
                return;
            }
            Dictionary<string, object> result = json.Deserialize<Dictionary<string, object>>(Audio.StateJson());
            if (error == null) error = Audio.LastError;
            result["ok"] = error == null;
            if (error != null) result["error"] = error;
            Respond(stream, "200 OK", "application/json", json.Serialize(result));
        }

        void ServeLighting(Stream stream, string method, Dictionary<string, string> headers, byte[] body)
        {
            if (!IsTrustedApiRequest(headers))
            {
                Respond(stream, "403 Forbidden", "text/plain", "Forbidden");
                return;
            }

            JavaScriptSerializer json = new JavaScriptSerializer();
            Dictionary<string, object> result;
            try
            {
                if (method == "GET")
                {
                    result = K631Lighting.GetState();
                }
                else if (method == "POST")
                {
                    Dictionary<string, object> args = json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(body));
                    if (args == null || !args.ContainsKey("mode")) throw new LightingException("Missing effect.");
                    object color;
                    args.TryGetValue("color", out color);
                    result = K631Lighting.SetState(Convert.ToInt32(args["mode"]), OptionalInt(args, "brightness"),
                        OptionalInt(args, "speed"), color as string);
                }
                else
                {
                    Respond(stream, "405 Method Not Allowed", "text/plain", "Method not allowed");
                    return;
                }
            }
            catch (Exception ex)
            {
                result = new Dictionary<string, object>();
                result["ok"] = false;
                result["error"] = ex is LightingException ? ex.Message : "Unexpected error: " + ex.Message;
            }
            Respond(stream, "200 OK", "application/json", json.Serialize(result));
        }

        static int? OptionalInt(Dictionary<string, object> args, string name)
        {
            object value;
            if (!args.TryGetValue(name, out value) || value == null) return null;
            return Convert.ToInt32(value);
        }

        bool IsLocalHost(string host)
        {
            return host != null &&
                (host.Equals("localhost:" + port, StringComparison.OrdinalIgnoreCase) ||
                 host.Equals("127.0.0.1:" + port, StringComparison.OrdinalIgnoreCase));
        }

        static string Header(Dictionary<string, string> headers, string name)
        {
            string value;
            return headers.TryGetValue(name, out value) ? value : null;
        }

        static bool ReadRequest(Stream stream, out string[] request, out Dictionary<string, string> headers, out byte[] body)
        {
            request = null;
            headers = new Dictionary<string, string>();
            body = new byte[0];

            byte[] buffer = new byte[16384];
            int length = 0;
            int headEnd = -1;
            while (headEnd < 0)
            {
                if (length == buffer.Length) return false;
                int read = stream.Read(buffer, length, buffer.Length - length);
                if (read <= 0) return false;
                length += read;
                headEnd = Encoding.ASCII.GetString(buffer, 0, length).IndexOf("\r\n\r\n", StringComparison.Ordinal);
            }

            string[] lines = Encoding.ASCII.GetString(buffer, 0, headEnd).Split(new string[] { "\r\n" }, StringSplitOptions.None);
            request = lines[0].Split(' ');
            if (request.Length < 2) return false;
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0) headers[lines[i].Substring(0, colon).Trim().ToLowerInvariant()] = lines[i].Substring(colon + 1).Trim();
            }

            int contentLength = 0;
            string declared;
            if (headers.TryGetValue("content-length", out declared) && (!int.TryParse(declared, out contentLength) || contentLength < 0 || contentLength > 65536))
                return false;
            body = new byte[contentLength];
            int have = Math.Min(length - (headEnd + 4), contentLength);
            Array.Copy(buffer, headEnd + 4, body, 0, have);
            while (have < contentLength)
            {
                int read = stream.Read(body, have, contentLength - have);
                if (read <= 0) return false;
                have += read;
            }
            return true;
        }

        static void Respond(Stream stream, string status, string type, string body)
        {
            Respond(stream, status, type, Encoding.UTF8.GetBytes(body));
        }

        static void Respond(Stream stream, string status, string type, byte[] body)
        {
            byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: " + type +
                "\r\nContent-Length: " + body.Length + "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            stream.Write(head, 0, head.Length);
            stream.Write(body, 0, body.Length);
        }
    }

    // Hidden window that receives WM_INPUT for every keyboard, even in the background,
    // and forwards only the K631's keys (unless "all keyboards" is on).
    class KeyboardWatcher : NativeWindow, IDisposable
    {
        const int WM_INPUT_DEVICE_CHANGE = 0x00FE;
        const int WM_INPUT = 0x00FF;
        const uint RID_INPUT = 0x10000003;
        const uint RIDI_DEVICENAME = 0x20000007;
        const uint RIM_TYPEKEYBOARD = 1;
        const uint RIDEV_INPUTSINK = 0x00000100;
        const uint RIDEV_DEVNOTIFY = 0x00002000;
        const ushort RI_KEY_BREAK = 1;
        const ushort RI_KEY_E0 = 2;
        const ushort RI_KEY_E1 = 4;

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTHEADER
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RAWKEYBOARD
        {
            public ushort MakeCode;
            public ushort Flags;
            public ushort Reserved;
            public ushort VKey;
            public uint Message;
            public uint ExtraInformation;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTDEVICELIST
        {
            public IntPtr hDevice;
            public uint dwType;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

        [DllImport("user32.dll")]
        static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder data, ref uint size);

        [DllImport("user32.dll")]
        static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[] list, ref uint count, uint size);

        // Set-1 scan codes (0xE0 prefix folded into the high byte) to KeyboardEvent.code names,
        // which is what the layout page uses to find each key.
        static readonly Dictionary<int, string> ScanCodes = new Dictionary<int, string>
        {
            { 0x01, "Escape" }, { 0x02, "Digit1" }, { 0x03, "Digit2" }, { 0x04, "Digit3" }, { 0x05, "Digit4" },
            { 0x06, "Digit5" }, { 0x07, "Digit6" }, { 0x08, "Digit7" }, { 0x09, "Digit8" }, { 0x0A, "Digit9" },
            { 0x0B, "Digit0" }, { 0x0C, "Minus" }, { 0x0D, "Equal" }, { 0x0E, "Backspace" }, { 0x0F, "Tab" },
            { 0x10, "KeyQ" }, { 0x11, "KeyW" }, { 0x12, "KeyE" }, { 0x13, "KeyR" }, { 0x14, "KeyT" },
            { 0x15, "KeyY" }, { 0x16, "KeyU" }, { 0x17, "KeyI" }, { 0x18, "KeyO" }, { 0x19, "KeyP" },
            { 0x1A, "BracketLeft" }, { 0x1B, "BracketRight" }, { 0x1C, "Enter" }, { 0x1D, "ControlLeft" },
            { 0x1E, "KeyA" }, { 0x1F, "KeyS" }, { 0x20, "KeyD" }, { 0x21, "KeyF" }, { 0x22, "KeyG" },
            { 0x23, "KeyH" }, { 0x24, "KeyJ" }, { 0x25, "KeyK" }, { 0x26, "KeyL" }, { 0x27, "Semicolon" },
            { 0x28, "Quote" }, { 0x29, "Backquote" }, { 0x2A, "ShiftLeft" }, { 0x2B, "Backslash" },
            { 0x2C, "KeyZ" }, { 0x2D, "KeyX" }, { 0x2E, "KeyC" }, { 0x2F, "KeyV" }, { 0x30, "KeyB" },
            { 0x31, "KeyN" }, { 0x32, "KeyM" }, { 0x33, "Comma" }, { 0x34, "Period" }, { 0x35, "Slash" },
            { 0x36, "ShiftRight" }, { 0x37, "NumpadMultiply" }, { 0x38, "AltLeft" }, { 0x39, "Space" },
            { 0x3A, "CapsLock" }, { 0x3B, "F1" }, { 0x3C, "F2" }, { 0x3D, "F3" }, { 0x3E, "F4" },
            { 0x3F, "F5" }, { 0x40, "F6" }, { 0x41, "F7" }, { 0x42, "F8" }, { 0x43, "F9" }, { 0x44, "F10" },
            { 0x45, "NumLock" }, { 0x46, "ScrollLock" }, { 0x54, "PrintScreen" }, { 0x56, "IntlBackslash" },
            { 0x57, "F11" }, { 0x58, "F12" },
            { 0xE010, "MediaTrackPrevious" }, { 0xE019, "MediaTrackNext" }, { 0xE01C, "NumpadEnter" },
            { 0xE01D, "ControlRight" }, { 0xE020, "AudioVolumeMute" }, { 0xE022, "MediaPlayPause" },
            { 0xE024, "MediaStop" }, { 0xE02E, "AudioVolumeDown" }, { 0xE030, "AudioVolumeUp" },
            { 0xE035, "NumpadDivide" }, { 0xE037, "PrintScreen" }, { 0xE038, "AltRight" }, { 0xE047, "Home" },
            { 0xE048, "ArrowUp" }, { 0xE049, "PageUp" }, { 0xE04B, "ArrowLeft" }, { 0xE04D, "ArrowRight" },
            { 0xE04F, "End" }, { 0xE050, "ArrowDown" }, { 0xE051, "PageDown" }, { 0xE052, "Insert" },
            { 0xE053, "Delete" }, { 0xE05B, "MetaLeft" }, { 0xE05C, "MetaRight" }, { 0xE05D, "ContextMenu" }
        };

        // Media keys sometimes arrive with no scan code, only a virtual key.
        static readonly Dictionary<int, string> VirtualKeys = new Dictionary<int, string>
        {
            { 0xAD, "AudioVolumeMute" }, { 0xAE, "AudioVolumeDown" }, { 0xAF, "AudioVolumeUp" },
            { 0xB0, "MediaTrackNext" }, { 0xB1, "MediaTrackPrevious" }, { 0xB2, "MediaStop" },
            { 0xB3, "MediaPlayPause" }
        };

        readonly EventHub hub;
        readonly Dictionary<IntPtr, bool> isK631 = new Dictionary<IntPtr, bool>();
        volatile bool k631Present;
        volatile bool allKeyboards;

        public KeyboardWatcher(EventHub hub, bool all)
        {
            this.hub = hub;
            allKeyboards = all;
            CreateHandle(new CreateParams());

            RAWINPUTDEVICE[] devices = new RAWINPUTDEVICE[1];
            devices[0].usUsagePage = 0x01;
            devices[0].usUsage = 0x06;
            devices[0].dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY;
            devices[0].hwndTarget = Handle;
            if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            RefreshPresence();
        }

        public bool AllKeyboards
        {
            get { return allKeyboards; }
            set
            {
                allKeyboards = value;
                hub.Publish("status", StatusJson());
            }
        }

        public string StatusJson()
        {
            return "{\"k631\":" + (k631Present ? "true" : "false") + ",\"all\":" + (allKeyboards ? "true" : "false") + "}";
        }

        public void Dispose()
        {
            DestroyHandle();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT)
            {
                OnInput(m.LParam);
            }
            else if (m.Msg == WM_INPUT_DEVICE_CHANGE)
            {
                isK631.Clear();
                RefreshPresence();
            }
            base.WndProc(ref m);
        }

        void OnInput(IntPtr rawInput)
        {
            uint headerSize = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));
            uint size = 0;
            GetRawInputData(rawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize);
            if (size == 0) return;

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(rawInput, RID_INPUT, buffer, ref size, headerSize) != size) return;
                RAWINPUTHEADER header = (RAWINPUTHEADER)Marshal.PtrToStructure(buffer, typeof(RAWINPUTHEADER));
                if (header.dwType != RIM_TYPEKEYBOARD) return;
                RAWKEYBOARD keyboard = (RAWKEYBOARD)Marshal.PtrToStructure(
                    new IntPtr(buffer.ToInt64() + headerSize), typeof(RAWKEYBOARD));

                bool fromK631 = IsK631(header.hDevice);
                if (fromK631 || allKeyboards) Publish(keyboard, fromK631);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        void Publish(RAWKEYBOARD keyboard, bool fromK631)
        {
            if (keyboard.MakeCode == 0xFF || (keyboard.Flags & RI_KEY_E1) != 0) return;
            bool e0 = (keyboard.Flags & RI_KEY_E0) != 0;
            // Windows wraps some keys in fake Shift presses (E0 2A / E0 36); they are not real keys.
            if (e0 && (keyboard.MakeCode == 0x2A || keyboard.MakeCode == 0x36)) return;

            int scan = (e0 ? 0xE000 : 0) | keyboard.MakeCode;
            string code;
            if (!ScanCodes.TryGetValue(scan, out code) && !VirtualKeys.TryGetValue(keyboard.VKey, out code))
                code = "";

            bool up = (keyboard.Flags & RI_KEY_BREAK) != 0;
            hub.Publish(null, "{\"type\":\"" + (up ? "up" : "down") + "\",\"code\":\"" + code +
                "\",\"scan\":" + scan + ",\"vk\":" + keyboard.VKey + ",\"k631\":" + (fromK631 ? "true" : "false") + "}");
        }

        bool IsK631(IntPtr device)
        {
            // Injected input (SendInput) has no device handle, so it never counts as the K631.
            if (device == IntPtr.Zero) return false;
            bool result;
            if (!isK631.TryGetValue(device, out result))
            {
                result = DeviceName(device).ToUpperInvariant().Contains(Program.K631Id);
                isK631[device] = result;
            }
            return result;
        }

        void RefreshPresence()
        {
            bool found = false;
            uint itemSize = (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST));
            uint count = 0;
            GetRawInputDeviceList(null, ref count, itemSize);
            if (count > 0)
            {
                RAWINPUTDEVICELIST[] list = new RAWINPUTDEVICELIST[count];
                uint got = GetRawInputDeviceList(list, ref count, itemSize);
                if (got != uint.MaxValue)
                {
                    for (int i = 0; i < got && !found; i++)
                        found = list[i].dwType == RIM_TYPEKEYBOARD &&
                            DeviceName(list[i].hDevice).ToUpperInvariant().Contains(Program.K631Id);
                }
            }
            k631Present = found;
            hub.Publish("status", StatusJson());
        }

        static string DeviceName(IntPtr device)
        {
            uint chars = 0;
            GetRawInputDeviceInfo(device, RIDI_DEVICENAME, null, ref chars);
            if (chars == 0) return "";
            StringBuilder name = new StringBuilder((int)chars);
            if (GetRawInputDeviceInfo(device, RIDI_DEVICENAME, name, ref chars) == uint.MaxValue) return "";
            return name.ToString();
        }
    }

    class TrayApp : ApplicationContext
    {
        readonly NotifyIcon icon;
        readonly KeyboardWatcher watcher;

        public TrayApp(KeyboardWatcher watcher)
        {
            this.watcher = watcher;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Open K631 Live", null, delegate { Program.OpenBrowser(Program.Url); });
            menu.Items.Add("Copy OBS overlay URL", null, delegate { Clipboard.SetText(Program.Url + "?obs=1"); });
            ToolStripMenuItem all = new ToolStripMenuItem("Show keys from all keyboards");
            all.CheckOnClick = true;
            all.Checked = watcher.AllKeyboards;
            all.CheckedChanged += delegate { watcher.AllKeyboards = all.Checked; };
            menu.Items.Add(all);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit", null, delegate { ExitThread(); });

            icon = new NotifyIcon();
            icon.Icon = SystemIcons.Application;
            icon.Text = "K631 Live - " + Program.Url;
            icon.ContextMenuStrip = menu;
            icon.DoubleClick += delegate { Program.OpenBrowser(Program.Url); };
            icon.Visible = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                icon.Visible = false;
                icon.Dispose();
                watcher.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
