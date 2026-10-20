// Lighting control for the Redragon K631, using only the protocol captured from the official
// Redragon app (see K631-protocol-notes.md). Every change is a read-modify-write of block C6:
//   1. feature report 5 (6 bytes, collection COL05):    05 83 C6 00 00 00   select block C6
//   2. feature report 6 (1032 bytes, collection COL06): read 06 83 C6 ...
//   3. feature report 6: write the same bytes with byte 1 = 03 and only the target bytes changed.
// Colours live in block C8, which the official app never reads back: it writes it from its own
// saved copy. We do the same with state\C8.bin, seeded from the last C8 write in the capture.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace K631Live
{
    class LightingException : Exception
    {
        public LightingException(string message) : base(message) { }
    }

    static class K631Lighting
    {
        const int CommandLength = 6;
        const int BlockLength = 1032;
        const byte ModeBlock = 0xC6;
        const byte ModeWrite = 0x03;
        const byte ModeRead = 0x83;
        const int SelfDefineFlagOffset = 0x14;  // 01 while effect 21 (self-define) is active
        const int ModeOffset = 0x15;
        const int FirstPairOffset = 0x26;   // pair for effect m: [0x26 + (m-1)*2] colour mode, [+1] speed<<4 | brightness
        const int PairedEffects = 20;       // effects 1-20 have a pair; 21 (self-define) does not
        const byte SelfDefine = 21;
        const byte Colourful = 0x07;        // colour mode: cycle colours; 0x00 = single colour from slot 0

        const byte ColorWrite = 0x08;
        const byte ColorBlock = 0xC8;
        const int FirstColorOffset = 0x1D;  // effect m: 7 RGB slots at 0x1D + (m-1)*21; slot 0 is the single colour
        const int ColorRecordLength = 21;
        // Effects with a colour choice (Cfg.ini LedOpt "random" and "color" columns).
        static readonly int[] ColorEffects = { 1, 2, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 18, 19, 20 };

        static readonly object gate = new object();
        static bool backedUp;

        public static Dictionary<string, object> GetState()
        {
            lock (gate)
            {
                using (Handles h = Handles.Open())
                    return Describe(ReadModeBlock(h), LoadColorBlock(false));
            }
        }

        // color: null = leave as is, "colourful" = cycle colours, "#rrggbb" = single colour.
        public static Dictionary<string, object> SetState(int mode, int? brightness, int? speed, string color)
        {
            if (mode < 1 || mode > 21) throw new LightingException("Effect must be 1-21.");
            if (brightness.HasValue && (brightness < 0 || brightness > 4)) throw new LightingException("Brightness must be 0-4.");
            if (speed.HasValue && (speed < 1 || speed > 4)) throw new LightingException("Speed must be 1-4.");
            byte[] rgb = null;
            if (color != null)
            {
                if (Array.IndexOf(ColorEffects, mode) < 0) throw new LightingException("This effect has no colour setting.");
                if (color != "colourful") rgb = ParseColor(color);
            }

            lock (gate)
            {
                using (Handles h = Handles.Open())
                {
                    byte[] before = ReadModeBlock(h);
                    Backup(before);

                    byte[] after = (byte[])before.Clone();
                    after[1] = ModeWrite;
                    after[SelfDefineFlagOffset] = (byte)(mode == SelfDefine ? 1 : 0);
                    after[ModeOffset] = (byte)mode;
                    if (mode <= PairedEffects)
                    {
                        int i = FirstPairOffset + (mode - 1) * 2 + 1;
                        int s = speed.HasValue ? speed.Value : before[i] >> 4;
                        int b = brightness.HasValue ? brightness.Value : before[i] & 0x0F;
                        after[i] = (byte)((s << 4) | b);
                        if (color != null) after[i - 1] = rgb == null ? Colourful : (byte)0x00;
                    }
                    // The official app never changes more than the self-define flag, the effect and one
                    // pair byte in a single write.
                    List<string> changed = Diff(before, after, 3, "C6");

                    byte[] colorBefore = null, colorAfter = null;
                    List<string> colorChanged = new List<string>();
                    if (rgb != null)
                    {
                        colorBefore = LoadColorBlock(true);
                        colorAfter = (byte[])colorBefore.Clone();
                        Array.Copy(rgb, 0, colorAfter, FirstColorOffset + (mode - 1) * ColorRecordLength, 3);
                        colorChanged = Diff(colorBefore, colorAfter, 3, "C8");
                    }

                    // Same order as the official app: colours (C8) first, then the mode block (C6).
                    if (colorChanged.Count > 0)
                    {
                        Write(h, colorAfter);
                        File.WriteAllBytes(ColorStatePath, colorAfter);
                        Thread.Sleep(60);
                    }
                    if (changed.Count > 0 || colorChanged.Count > 0)
                    {
                        Write(h, after);
                        Thread.Sleep(30);
                    }

                    Dictionary<string, object> state = Describe(ReadModeBlock(h), colorAfter ?? LoadColorBlock(false));
                    changed.AddRange(colorChanged);
                    state["changed"] = changed;
                    return state;
                }
            }
        }

        // ---- Live per-key frames (music mode) ----
        // Feature report 8 (382 bytes, collection COL08), exactly as the Redragon app's music mode sends it:
        // 08 0A 7A 01 00 00 00 00, then R,G,B for LED slots 1..90. It needs no setup and stores nothing.
        const int LiveLength = 382;
        static SafeFileHandle liveHandle;
        static DateTime liveAppCheck = DateTime.MinValue;

        public static void SendLiveFrame(byte[] slotColors)
        {
            lock (gate)
            {
                // Re-check now and then that the official app hasn't started streaming too.
                if (DateTime.UtcNow - liveAppCheck > TimeSpan.FromSeconds(2))
                {
                    liveAppCheck = DateTime.UtcNow;
                    if (Process.GetProcessesByName("KeyboardDrv").Length > 0)
                    {
                        CloseLiveLocked();
                        throw new LightingException("The Redragon app (KeyboardDrv.exe) is running. Close it to light the keyboard with music.");
                    }
                }
                if (liveHandle == null)
                {
                    foreach (KeyValuePair<string, int> collection in FeatureCollections())
                        if (collection.Value == LiveLength)
                        {
                            liveHandle = OpenHid(collection.Key);
                            break;
                        }
                    if (liveHandle == null || liveHandle.IsInvalid)
                    {
                        CloseLiveLocked();
                        throw new LightingException("K631 live lighting channel not found. Is the keyboard plugged in?");
                    }
                }
                byte[] report = new byte[LiveLength];
                report[0] = 0x08; report[1] = 0x0A; report[2] = 0x7A; report[3] = 0x01;
                Array.Copy(slotColors, 0, report, 8, Math.Min(slotColors.Length, LiveLength - 8));
                if (!HidD_SetFeature(liveHandle, report, report.Length))
                {
                    int error = Marshal.GetLastWin32Error();
                    CloseLiveLocked();
                    throw new LightingException("Sending music to the keyboard failed (error " + error + ").");
                }
            }
        }

        // Stops live frames and hands the LEDs back to the keyboard's own effect by re-writing the
        // mode block unchanged, the same write the official app makes when it applies its settings.
        public static void EndLive()
        {
            lock (gate)
            {
                bool wasLive = liveHandle != null;
                CloseLiveLocked();
                if (!wasLive) return;
                using (Handles h = Handles.Open())
                {
                    byte[] block = ReadModeBlock(h);
                    block[1] = ModeWrite;
                    Write(h, block);
                    Thread.Sleep(30);
                }
            }
        }

        static void CloseLiveLocked()
        {
            if (liveHandle != null) liveHandle.Dispose();
            liveHandle = null;
        }

        static List<string> Diff(byte[] before, byte[] after, int limit, string block)
        {
            List<string> changed = new List<string>();
            for (int i = 2; i < BlockLength; i++)
                if (before[i] != after[i])
                    changed.Add(string.Format("{0} 0x{1:X3}: {2:X2}>{3:X2}", block, i, before[i], after[i]));
            if (changed.Count > limit) throw new LightingException("Refusing to write: more bytes would change than expected. Nothing was written.");
            return changed;
        }

        static void Write(Handles h, byte[] block)
        {
            if (!HidD_SetFeature(h.Data, block, block.Length))
                throw new LightingException("Writing to the keyboard failed (error " + Marshal.GetLastWin32Error() + ").");
        }

        static byte[] ParseColor(string color)
        {
            int value;
            if (color.Length != 7 || color[0] != '#' ||
                !int.TryParse(color.Substring(1), System.Globalization.NumberStyles.HexNumber, null, out value))
                throw new LightingException("Colour must look like #rrggbb.");
            return new byte[] { (byte)(value >> 16), (byte)(value >> 8), (byte)value };
        }

        static string ColorStatePath
        {
            get { return Path.Combine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "state"), "C8.bin"); }
        }

        static byte[] LoadColorBlock(bool required)
        {
            byte[] block = File.Exists(ColorStatePath) ? File.ReadAllBytes(ColorStatePath) : null;
            bool valid = block != null && block.Length == BlockLength && block[0] == 0x06 &&
                block[1] == ColorWrite && block[2] == ColorBlock && block[3] == 0x00 && block[4] == 0x40;
            if (!valid && required) throw new LightingException("The saved colour block (state\\C8.bin) is missing or damaged. Nothing was written.");
            return valid ? block : null;
        }

        static byte[] ReadModeBlock(Handles h)
        {
            byte[] select = new byte[CommandLength] { 0x05, ModeRead, ModeBlock, 0x00, 0x00, 0x00 };
            if (!HidD_SetFeature(h.Command, select, select.Length))
                throw new LightingException("Selecting the lighting block failed (error " + Marshal.GetLastWin32Error() + ").");
            Thread.Sleep(20);   // the official app waits ~20 ms between select and read

            byte[] block = new byte[BlockLength];
            block[0] = 0x06;
            if (!HidD_GetFeature(h.Data, block, block.Length))
                throw new LightingException("Reading the lighting block failed (error " + Marshal.GetLastWin32Error() + ").");

            // Only ever modify a block that looks exactly like the ones in the capture.
            if (block[0] != 0x06 || block[1] != ModeRead || block[2] != ModeBlock ||
                block[0x0E] != 0x5A || block[0x0F] != 0xA5 || block[0x4E] != 0x5A || block[0x4F] != 0xA5)
                throw new LightingException(string.Format(
                    "The keyboard returned an unexpected block ({0:X2} {1:X2} {2:X2}). Nothing was written.", block[0], block[1], block[2]));
            return block;
        }

        static Dictionary<string, object> Describe(byte[] block, byte[] colors)
        {
            Dictionary<string, object> state = new Dictionary<string, object>();
            int mode = block[ModeOffset];
            state["ok"] = true;
            state["mode"] = mode;
            if (mode >= 1 && mode <= PairedEffects)
            {
                int pair = block[FirstPairOffset + (mode - 1) * 2 + 1];
                state["speed"] = pair >> 4;
                state["brightness"] = pair & 0x0F;
                state["colourful"] = block[FirstPairOffset + (mode - 1) * 2] == Colourful;
                if (colors != null)
                {
                    int o = FirstColorOffset + (mode - 1) * ColorRecordLength;
                    List<string> palette = new List<string>();
                    for (int slot = 0; slot < ColorRecordLength / 3; slot++, o += 3)
                        palette.Add(string.Format("#{0:x2}{1:x2}{2:x2}", colors[o], colors[o + 1], colors[o + 2]));
                    state["color"] = palette[0];
                    state["palette"] = palette;
                }
            }
            return state;
        }

        // Keeps the first block read in each session, so the original settings can be restored by hand.
        static void Backup(byte[] block)
        {
            if (backedUp) return;
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "backup");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "C6-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bin"), block);
            backedUp = true;
        }

        class Handles : IDisposable
        {
            public SafeFileHandle Command;
            public SafeFileHandle Data;

            public static Handles Open()
            {
                // Two programs doing select/read/write at once could interleave, and our traffic would
                // also pollute a capture in progress, so stay off the bus while the official app runs.
                if (Process.GetProcessesByName("KeyboardDrv").Length > 0)
                    throw new LightingException("The Redragon app (KeyboardDrv.exe) is running. Close it, including from the tray, to control lighting here.");

                string commandPath = null, dataPath = null;
                foreach (KeyValuePair<string, int> collection in FeatureCollections())
                {
                    if (collection.Value == CommandLength) commandPath = collection.Key;
                    else if (collection.Value == BlockLength) dataPath = collection.Key;
                }
                if (commandPath == null || dataPath == null)
                    throw new LightingException("K631 lighting interface not found. Is the keyboard plugged in?");

                Handles h = new Handles();
                h.Command = OpenHid(commandPath);
                h.Data = OpenHid(dataPath);
                if (h.Command.IsInvalid || h.Data.IsInvalid)
                {
                    h.Dispose();
                    throw new LightingException("Could not open the K631 lighting interface.");
                }
                return h;
            }

            public void Dispose()
            {
                if (Command != null) Command.Dispose();
                if (Data != null) Data.Dispose();
            }
        }

        // Path and feature report length of every K631 HID collection.
        static List<KeyValuePair<string, int>> FeatureCollections()
        {
            List<KeyValuePair<string, int>> result = new List<KeyValuePair<string, int>>();
            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);
            IntPtr set = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            try
            {
                SP_DEVICE_INTERFACE_DATA iface = new SP_DEVICE_INTERFACE_DATA();
                iface.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref iface); i++)
                {
                    string path = InterfacePath(set, ref iface);
                    if (path == null || path.ToUpperInvariant().IndexOf(Program.K631Id) < 0) continue;
                    using (SafeFileHandle handle = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
                    {
                        if (handle.IsInvalid) continue;
                        IntPtr preparsed;
                        if (!HidD_GetPreparsedData(handle, out preparsed)) continue;
                        HIDP_CAPS caps;
                        HidP_GetCaps(preparsed, out caps);
                        HidD_FreePreparsedData(preparsed);
                        if (caps.UsagePage == 0xFF00) result.Add(new KeyValuePair<string, int>(path, caps.FeatureReportByteLength));
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
            return result;
        }

        static string InterfacePath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA iface)
        {
            int size;
            SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out size, IntPtr.Zero);
            if (size == 0) return null;
            IntPtr detail = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, detail, size, out size, IntPtr.Zero)) return null;
                return Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4));
            }
            finally
            {
                Marshal.FreeHGlobal(detail);
            }
        }

        static SafeFileHandle OpenHid(string path)
        {
            return CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        }

        const int DIGCF_PRESENT = 0x02;
        const int DIGCF_DEVICEINTERFACE = 0x10;
        const uint GENERIC_READ = 0x80000000;
        const uint GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ = 1;
        const uint FILE_SHARE_WRITE = 2;
        const uint OPEN_EXISTING = 3;

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")]
        static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll")]
        static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsed);

        [DllImport("hid.dll")]
        static extern bool HidD_FreePreparsedData(IntPtr preparsed);

        [DllImport("hid.dll")]
        static extern int HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);

        [DllImport("hid.dll", SetLastError = true)]
        static extern bool HidD_SetFeature(SafeFileHandle device, byte[] report, int length);

        [DllImport("hid.dll", SetLastError = true)]
        static extern bool HidD_GetFeature(SafeFileHandle device, byte[] report, int length);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string enumerator, IntPtr parent, int flags);

        [DllImport("setupapi.dll")]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid classGuid, int index, ref SP_DEVICE_INTERFACE_DATA data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr info);

        [DllImport("setupapi.dll")]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    }
}
