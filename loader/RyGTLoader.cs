// RyGT loader: sets up Gran Turismo Sport from the player's own dump, firmware modules and fonts,
// then starts it. Built with build.cmd (C# for .NET Framework 4.x, which Windows includes).
// SPDX-License-Identifier: GPL-2.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace RyGT
{
    static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);

        // Returns a one-byte BOOLEAN, not a BOOL.
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool CreateSymbolicLink(string link, string target, int flags);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr window, string appName, string idList);

        [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software,
            uint flags, IntPtr featureLevels, uint featureLevelsCount, uint sdkVersion,
            out IntPtr device, out int featureLevel, out IntPtr immediateContext);
    }

    /// Wine (Linux, Steam Proton): its kernel32 has wine_get_unix_file_name. Wine before 11 cannot
    /// make Windows links (CreateSymbolicLink does nothing there and mklink /J fails), so under Wine
    /// the game's files and folders are linked with Unix symbolic links, which Wine follows like the
    /// files themselves (and reports as reparse points when they are folders).
    static class Wine
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern IntPtr wine_get_unix_file_name(string dosPath);

        [DllImport("kernel32.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true,
                   BestFitMapping = false)]
        static extern IntPtr wine_get_dos_file_name(string unixPath);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetProcessHeap();

        [DllImport("kernel32.dll")]
        static extern bool HeapFree(IntPtr heap, uint flags, IntPtr memory);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct StartupInfo
        {
            public int cb;
            public string reserved, desktop, title;
            public int x, y, width, height, columns, rows, fill, flags;
            public short show, reserved2;
            public IntPtr reserved3, input, output, error;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ProcessInformation
        {
            public IntPtr process, thread;
            public int processId, threadId;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory,
            ref StartupInfo startup, out ProcessInformation information);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        static readonly bool detected = DetectWine();

        public static bool Detected { get { return detected; } }

        static bool DetectWine()
        {
            try
            {
                IntPtr kernel = GetModuleHandle("kernel32.dll");
                return kernel != IntPtr.Zero && GetProcAddress(kernel, "wine_get_unix_file_name") != IntPtr.Zero;
            }
            catch { return false; }
        }

        /// The Unix path of a Windows path (whose last part need not exist yet).
        public static string UnixPath(string path)
        {
            IntPtr unix = wine_get_unix_file_name(Path.GetFullPath(path));
            if (unix == IntPtr.Zero) throw new Exception("Wine has no Unix path for " + path + ".");
            try
            {
                int length = 0;
                while (Marshal.ReadByte(unix, length) != 0) length++;
                var bytes = new byte[length];
                Marshal.Copy(unix, bytes, 0, length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally { HeapFree(GetProcessHeap(), 0, unix); }
        }

        static string DosPath(string unixPath)
        {
            IntPtr dos = wine_get_dos_file_name(unixPath);
            if (dos == IntPtr.Zero) throw new Exception("Wine has no Windows path for " + unixPath + ".");
            try { return Marshal.PtrToStringUni(dos); }
            finally { HeapFree(GetProcessHeap(), 0, dos); }
        }

        static string Quote(string unixPath)
        {
            return "'" + unixPath.Replace("'", "'\\''") + "'";
        }

        /// Makes each link (key) a Unix symbolic link to its target (value), replacing an earlier
        /// symbolic link but never a real file or folder, with one /bin/sh script. Wine starts Unix
        /// programs detached, so the script's last line writes a file to wait for.
        public static void Link(IEnumerable<KeyValuePair<string, string>> links)
        {
            string name = "rygt-link-" + Guid.NewGuid().ToString("N");
            string script = Path.Combine(Path.GetTempPath(), name + ".sh");
            string done = Path.Combine(Path.GetTempPath(), name + ".done");
            var text = new StringBuilder();
            text.Append("#!/bin/sh\n# Written by RyGT.exe: links the game's files under Wine.\n");
            text.Append("PATH=/usr/local/bin:/usr/bin:/bin\n");
            text.Append("link() { if [ -L \"$2\" ] || [ ! -e \"$2\" ]; then ln -sfn \"$1\" \"$2\"; fi; }\n");
            foreach (var pair in links)
            {
                // The link's folder exists; its last part may not (Wine still gives the Unix path).
                string folder = UnixPath(Path.GetDirectoryName(Path.GetFullPath(pair.Key)));
                text.Append("link " + Quote(UnixPath(pair.Value)) + " " +
                            Quote(folder.TrimEnd('/') + "/" + Path.GetFileName(pair.Key)) + "\n");
            }
            text.Append("echo done > " + Quote(UnixPath(done)) + "\n");
            File.WriteAllText(script, text.ToString(), new UTF8Encoding(false));
            try
            {
                var startup = new StartupInfo { cb = Marshal.SizeOf(typeof(StartupInfo)) };
                ProcessInformation information;
                var command = new StringBuilder("\"" + DosPath("/bin/sh") + "\" \"" + UnixPath(script) + "\"");
                const uint CreateNoWindow = 0x08000000;
                if (!CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, false, CreateNoWindow, IntPtr.Zero, null,
                                   ref startup, out information))
                    throw new Exception("Could not start /bin/sh to link the game's files (error " +
                                        Marshal.GetLastWin32Error() + ").");
                if (information.thread != IntPtr.Zero) CloseHandle(information.thread);
                if (information.process != IntPtr.Zero) CloseHandle(information.process);
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
                while (!File.Exists(done) && DateTime.UtcNow < deadline) Thread.Sleep(50);
                if (!File.Exists(done)) throw new Exception("Linking the game's files with /bin/sh did not finish.");
            }
            finally
            {
                try { File.Delete(script); } catch { }
                try { File.Delete(done); } catch { }
            }
        }
    }

    /// Dark colors for the loader window.
    static class Dark
    {
        public static readonly Color Back = Color.FromArgb(30, 30, 30);
        public static readonly Color Field = Color.FromArgb(45, 45, 48);
        public static readonly Color Button = Color.FromArgb(58, 58, 61);
        public static readonly Color ButtonHover = Color.FromArgb(75, 75, 80);
        public static readonly Color Accent = Color.FromArgb(178, 24, 36);
        public static readonly Color AccentHover = Color.FromArgb(206, 40, 52);
        public static readonly Color Heading = Color.FromArgb(240, 96, 96);
        public static readonly Color Border = Color.FromArgb(70, 70, 74);
        public static readonly Color Text = Color.FromArgb(230, 230, 230);
        public static readonly Color DimText = Color.FromArgb(150, 150, 150);

        public static void Apply(Control control)
        {
            if (control.BackColor.A == 255) control.BackColor = Back;
            if (control.Tag as string != "heading") control.ForeColor = Text;
            var button = control as Button;
            if (button != null)
            {
                bool accent = button.Tag as string == "accent";
                button.FlatStyle = FlatStyle.Flat;
                button.BackColor = accent ? Accent : Button;
                button.FlatAppearance.BorderColor = accent ? Accent : Border;
                button.FlatAppearance.MouseOverBackColor = accent ? AccentHover : ButtonHover;
                button.FlatAppearance.MouseDownBackColor = accent ? Accent : Border;
                button.Padding = new Padding(10, 2, 10, 2);
            }
            var box = control as TextBox;
            if (box != null)
            {
                box.BackColor = Field;
                box.BorderStyle = BorderStyle.FixedSingle;
                box.HandleCreated += delegate { Native.SetWindowTheme(box.Handle, "DarkMode_Explorer", null); };
            }
            var check = control as CheckBox;
            if (check != null && !(check is DarkCheckBox)) check.FlatStyle = FlatStyle.Flat;
            var combo = control as ComboBox;
            if (combo != null) combo.BackColor = Field;
            foreach (Control child in control.Controls) Apply(child);
        }

        /// A dark title bar (Windows 10 2004 and later; ignored before).
        public static void TitleBar(Form form)
        {
            int on = 1;
            if (Native.DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)) != 0)
                Native.DwmSetWindowAttribute(form.Handle, 19, ref on, sizeof(int));
        }
    }

    /// A check box drawn in the loader's colors: a dark square, red with a white tick when checked.
    class DarkCheckBox : CheckBox
    {
        public DarkCheckBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var text = TextRenderer.MeasureText(Text, Font);
            return new Size(text.Width + 24, Math.Max(text.Height, 16) + 2);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            PaintBehind(this, e.Graphics);
        }

        /// The window's picture under `control`, with the translucent layer of the panel it is on.
        public static void PaintBehind(Control control, Graphics g)
        {
            var form = control.FindForm() as LoaderForm;
            if (form == null || !form.PaintBackdrop(control, g))
            {
                using (var back = new SolidBrush(Dark.Back)) g.FillRectangle(back, control.ClientRectangle);
            }
            for (var parent = control.Parent; parent != null; parent = parent.Parent)
            {
                var glass = parent as GlassPanel;
                if (glass != null)
                {
                    using (var overlay = new SolidBrush(glass.Overlay)) g.FillRectangle(overlay, control.ClientRectangle);
                    break;
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            OnPaintBackground(e);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int size = 14;
            var box = new Rectangle(1, (Height - size) / 2, size, size);
            bool hover = ClientRectangle.Contains(PointToClient(Cursor.Position));
            using (var fill = new SolidBrush(!Enabled ? Color.FromArgb(40, 40, 42) : Checked ? Dark.Accent : Color.FromArgb(32, 32, 34)))
                g.FillRectangle(fill, box);
            using (var border = new Pen(!Enabled ? Color.FromArgb(70, 70, 72) : Checked ? Dark.AccentHover : hover ? Color.FromArgb(150, 150, 155) : Color.FromArgb(105, 105, 110)))
                g.DrawRectangle(border, box);
            if (Checked)
            {
                using (var tick = new Pen(Enabled ? Color.White : Color.FromArgb(150, 150, 150), 2f))
                {
                    g.DrawLines(tick, new[]
                    {
                        new PointF(box.Left + 3f, box.Top + 7.5f),
                        new PointF(box.Left + 6f, box.Top + 10.5f),
                        new PointF(box.Left + 11f, box.Top + 4f)
                    });
                }
            }
            TextRenderer.DrawText(g, Text, Font, new Point(box.Right + 7, (Height - TextRenderer.MeasureText(Text, Font).Height) / 2),
                                  Enabled ? ForeColor : Dark.DimText, TextFormatFlags.NoPrefix);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }
    }

    /// A settings panel over the window's background picture: it paints the picture under it and
    /// a translucent dark layer itself, so the controls on it (transparent) repaint cleanly. A
    /// translucent BackColor left old text behind when a control on it was redrawn.
    class GlassPanel : TableLayoutPanel
    {
        public Color Overlay = Color.FromArgb(150, 12, 12, 14);

        public GlassPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var form = FindForm() as LoaderForm;
            if (form == null || !form.PaintBackdrop(this, e.Graphics))
            {
                using (var back = new SolidBrush(Dark.Back)) e.Graphics.FillRectangle(back, ClientRectangle);
            }
            using (var overlay = new SolidBrush(Overlay)) e.Graphics.FillRectangle(overlay, ClientRectangle);
        }
    }

    /// Unwraps an unencrypted (fake-signed) PS4 SELF into a plain ELF.
    static class Self
    {
        public static bool IsElf(byte[] d)
        {
            return d.Length >= 4 && d[0] == 0x7f && d[1] == (byte)'E' && d[2] == (byte)'L' && d[3] == (byte)'F';
        }

        public static byte[] ToElf(byte[] d, string name)
        {
            if (IsElf(d)) return d;
            if (d.Length < 32 || d[0] != 0x4f || d[1] != 0x15 || d[2] != 0x3d || d[3] != 0x1d)
                throw new Exception(name + " is neither a SELF nor an ELF file.");
            int count = BitConverter.ToUInt16(d, 24);
            var entries = new Dictionary<long, long[]>();
            for (int i = 0; i < count; i++)
            {
                long props = (long)BitConverter.ToUInt64(d, 32 + i * 32);
                long offset = (long)BitConverter.ToUInt64(d, 40 + i * 32);
                long size = (long)BitConverter.ToUInt64(d, 48 + i * 32);
                if ((props & 0xA) != 0)
                    throw new Exception(name + " has encrypted or compressed segments. The dump must be decrypted.");
                if ((props & 0x800) != 0) entries[(props >> 20) & 0xFFF] = new[] { offset, size };
            }
            int elf = 32 + count * 32;
            long phoff = (long)BitConverter.ToUInt64(d, elf + 0x20);
            int phnum = BitConverter.ToUInt16(d, elf + 0x38);
            if (phoff != 0x40) throw new Exception(name + ": unexpected ELF header layout.");
            int headerLength = 0x40 + phnum * 0x38;
            long outSize = headerLength;
            var headers = new List<long[]>();
            for (int i = 0; i < phnum; i++)
            {
                int b = elf + 0x40 + i * 0x38;
                long type = BitConverter.ToUInt32(d, b);
                long offset = (long)BitConverter.ToUInt64(d, b + 8);
                long fileSize = (long)BitConverter.ToUInt64(d, b + 0x20);
                headers.Add(new[] { type, offset, fileSize });
                outSize = Math.Max(outSize, offset + fileSize);
            }
            var output = new byte[outSize];
            Array.Copy(d, elf, output, 0, headerLength);
            long selfSize = (long)BitConverter.ToUInt64(d, 16);
            for (int i = 0; i < headers.Count; i++)
            {
                long type = headers[i][0], offset = headers[i][1], fileSize = headers[i][2];
                if (fileSize == 0) continue;
                long[] entry;
                if (entries.TryGetValue(i, out entry))
                    Array.Copy(d, entry[0], output, offset, Math.Min(entry[1], fileSize));
                else if (type == 0x6fffff01 && selfSize + fileSize <= d.Length)
                    Array.Copy(d, selfSize, output, offset, fileSize); // PT_SCE_VERSION follows the body
            }
            return output;
        }

        /// Rewrites PT_SCE_RELRO (0x61000010) as a read-write PT_LOAD so the converter maps it and
        /// allows relocations into it.
        public static byte[] RelroToLoad(byte[] elf)
        {
            long phoff = (long)BitConverter.ToUInt64(elf, 0x20);
            int phnum = BitConverter.ToUInt16(elf, 0x38);
            for (int i = 0; i < phnum; i++)
            {
                int header = (int)phoff + i * 0x38;
                if (BitConverter.ToUInt32(elf, header) != 0x61000010) continue;
                BitConverter.GetBytes(1u).CopyTo(elf, header);     // PT_LOAD
                BitConverter.GetBytes(6u).CopyTo(elf, header + 4); // read, write
            }
            return elf;
        }
    }

    /// Finishes the converted GTSport.exe: the launcher's icon (RyGT.exe's embedded copy of ryGT.ico),
    /// so Explorer and shortcuts show the game's art instead of a blank program icon, and the Windows
    /// (GUI) subsystem, so a double-click opens only the game window, without a console.
    static class GameExe
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr BeginUpdateResource(string file, bool deleteExisting);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool UpdateResource(IntPtr update, IntPtr type, IntPtr name, ushort language, byte[] data, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool EndUpdateResource(IntPtr update, bool discard);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);

        [DllImport("kernel32.dll")]
        static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);

        [DllImport("kernel32.dll")]
        static extern bool FreeLibrary(IntPtr module);

        const int RtIcon = 3, RtGroupIcon = 14;
        const ushort GuiSubsystem = 2;

        public static bool IsPrepared(string exe)
        {
            return (Wine.Detected || HasIcon(exe)) && ReadSubsystem(exe) == GuiSubsystem;
        }

        public static void Prepare(string exe)
        {
            // (Not the icon under Wine: it is only for Windows' Explorer, and this keeps Set up from
            // depending on Wine rewriting the resources of a 100 MB program.)
            if (!Wine.Detected && !HasIcon(exe)) ApplyIcon(exe);
            if (ReadSubsystem(exe) != GuiSubsystem) WriteSubsystem(exe, GuiSubsystem);
        }

        // The subsystem field of the PE optional header: PE header offset at 0x3c, then the "PE\0\0"
        // signature (4 bytes), the file header (20 bytes) and 68 bytes into the optional header.
        static long SubsystemOffset(BinaryReader reader)
        {
            reader.BaseStream.Position = 0x3c;
            return reader.ReadInt32() + 4 + 20 + 68;
        }

        static ushort ReadSubsystem(string exe)
        {
            using (var reader = new BinaryReader(File.OpenRead(exe)))
            {
                reader.BaseStream.Position = SubsystemOffset(reader);
                return reader.ReadUInt16();
            }
        }

        static void WriteSubsystem(string exe, ushort subsystem)
        {
            using (var stream = new FileStream(exe, FileMode.Open, FileAccess.ReadWrite))
            {
                long offset = SubsystemOffset(new BinaryReader(stream));
                stream.Position = offset;
                stream.Write(BitConverter.GetBytes(subsystem), 0, 2);
            }
        }

        static bool HasIcon(string exe)
        {
            IntPtr module = LoadLibraryEx(exe, IntPtr.Zero, 0x2 | 0x20); // as a data file and image resource
            if (module == IntPtr.Zero) return false;
            try { return FindResource(module, (IntPtr)1, (IntPtr)RtGroupIcon) != IntPtr.Zero; }
            finally { FreeLibrary(module); }
        }

        /// Adds every image of the .ico as RT_ICON 1..n and the icon group as RT_GROUP_ICON 1.
        static void ApplyIcon(string exe)
        {
            byte[] ico;
            using (var stream = typeof(GameExe).Assembly.GetManifestResourceStream("ryGT.ico"))
            {
                if (stream == null) throw new Exception("RyGT.exe has no embedded icon.");
                ico = new byte[stream.Length];
                stream.Read(ico, 0, ico.Length);
            }
            int count = BitConverter.ToUInt16(ico, 4);
            IntPtr update = BeginUpdateResource(exe, false);
            if (update == IntPtr.Zero) throw new Exception("Could not open " + exe + " (error " + Marshal.GetLastWin32Error() + ").");
            bool ok = false;
            try
            {
                var group = new MemoryStream();
                var writer = new BinaryWriter(group);
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)count);
                for (int i = 0; i < count; i++)
                {
                    int entry = 6 + 16 * i;
                    int size = BitConverter.ToInt32(ico, entry + 8), offset = BitConverter.ToInt32(ico, entry + 12);
                    var image = new byte[size];
                    Array.Copy(ico, offset, image, 0, size);
                    if (!UpdateResource(update, (IntPtr)RtIcon, (IntPtr)(i + 1), 0, image, (uint)size))
                        throw new Exception("Could not add the icon (error " + Marshal.GetLastWin32Error() + ").");
                    writer.Write(ico, entry, 12);   // width, height, colors, reserved, planes, bit count, size
                    writer.Write((ushort)(i + 1)); // the RT_ICON id instead of the file offset
                }
                var data = group.ToArray();
                if (!UpdateResource(update, (IntPtr)RtGroupIcon, (IntPtr)1, 0, data, (uint)data.Length))
                    throw new Exception("Could not add the icon group (error " + Marshal.GetLastWin32Error() + ").");
                ok = true;
            }
            finally
            {
                if (!EndUpdateResource(update, !ok) && ok)
                    throw new Exception("Could not write the icon into " + exe + " (error " + Marshal.GetLastWin32Error() + ").");
            }
        }
    }

    class Settings
    {
        public string Game = "", Update = "", Modules = "", Fonts = "";
        public string StdoutLevel = "Warning";
        readonly string path;

        public Settings(string path)
        {
            this.path = path;
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                if (key == "game") Game = value;
                else if (key == "update") Update = value;
                else if (key == "modules") Modules = value;
                else if (key == "fonts") Fonts = value;
                else if (key == "stdout_level") StdoutLevel = LogFiles.Levels[LogFiles.LevelIndex(value)];
            }
        }

        public void Save()
        {
            File.WriteAllLines(path, new[] { "game=" + Game, "update=" + Update, "modules=" + Modules, "fonts=" + Fonts, "stdout_level=" + StdoutLevel });
        }
    }

    class LoaderForm : Form
    {
        static readonly string[] FirmwareModules = { "libSceFont", "libSceFontFt", "libSceFreeTypeOt", "libSceJson2", "libSceNgs2" };
        // Used when present: the JPEG decoder (Scapes thumbnails and backgrounds) and PNG encoder (photos).
        // Both are plain software modules needing only libkernel and libSceLibcInternal.
        // (The other firmware modules GT Sport imports stay RyGT's stubs in libs: converted from a full
        // firmware folder, some of them need modules nothing provides, such as libSceOttvCapture,
        // and GTSport.exe then stopped at start with "Failed to load module".)
        static readonly string[] OptionalFirmwareModules = { "libSceJpegDec", "libScePngEnc" };

        readonly string root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        readonly Settings settings;
        readonly TextBox game, update, modules, fonts, log;
        readonly Button setup, play;
        readonly Label status;
        volatile bool busy;

        public LoaderForm()
        {
            settings = new Settings(Path.Combine(root, "RyGT.ini"));
            Text = "ryGT - Gran Turismo Sport";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(1280, 760);
            MinimumSize = new Size(1260, 640);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            // The background picture (launcher\background.png next to RyGT.exe) fills the window;
            // the settings sit on translucent panels over its dark left side.
            background = LoadBackground();
            if (background != null) ResizeRedraw = true;

            var scroll = new Panel
            {
                Name = "launcherScroll", Dock = DockStyle.Fill, AutoScroll = true,
                BackColor = Color.Transparent, AutoScrollMargin = new Size(0, 12)
            };
            Controls.Add(scroll);
            var column = new TableLayoutPanel
            {
                Name = "launcherContent", Dock = DockStyle.Top, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(1220, 0),
                ColumnCount = 1, Padding = new Padding(24, 18, 10, 14),
                BackColor = Color.Transparent
            };
            column.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            scroll.Controls.Add(column);

            var title = new Label { Text = "ryGT", AutoSize = true, BackColor = Color.Transparent, Font = new Font("Segoe UI Semibold", 22f), Margin = new Padding(0, 0, 0, 0) };
            var subtitle = new Label
            {
                Text = "Gran Turismo Sport on Windows. Pick your own decrypted dump (CUSA03220), its 1.69 update, " +
                       "your PS4 firmware's system modules and fonts; Set up converts the game, Play starts it.",
                AutoSize = true, MaximumSize = new Size(1160, 0), BackColor = Color.Transparent, Margin = new Padding(2, 0, 0, 12)
            };
            column.Controls.Add(title);
            column.Controls.Add(subtitle);

            // Game files.
            var files = Section(column, "Game files");
            var fileTable = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill, BackColor = Color.Transparent };
            fileTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            fileTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            fileTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            files.Controls.Add(fileTable);
            game = AddRow(fileTable, 0, "Game (CUSA03220)", settings.Game);
            update = AddRow(fileTable, 1, "Update 1.69 (if separate)", settings.Update);
            modules = AddRow(fileTable, 2, "System modules", settings.Modules);
            fonts = AddRow(fileTable, 3, "Fonts", settings.Fonts);

            // Display on the left, graphics and controls in the middle, shaders on the right.
            var grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(0) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
            var left = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(0, 0, 5, 0) };
            var right = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(5, 0, 5, 0) };
            var third = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(5, 0, 0, 0) };
            left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            third.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.Controls.Add(left, 0, 0);
            grid.Controls.Add(right, 1, 0);
            grid.Controls.Add(third, 2, 0);

            var display = Section(left, "Display");
            AddScreenSettings(display);
            AddShapeSetting(display);
            AddOption(display, "Show an FPS counter", "General", "show_fps_counter", false);
            AddOption(display, "Show the splash screen while the game starts", "General", "show_splash", false);
            AddOption(display, "Show the game on Discord (Rich Presence)", "General", "discord_rpc_enabled", true);
            // The game's opening movie (when its own option plays it); off: skipped at once.
            AddOption(display, "Play the opening movie", "General", "opening_movie", true);
            AddMemorySetting(display);


            // Graphics.
            var graphics = Section(right, "Graphics");
            AddRendererSetting(graphics);
            // Sparks, tire smoke and other effects are always on; an older launcher could have
            // turned them off in the config.
            if (!ReadSetting("GPU", "particle_effects", true))
            {
                try { WriteSetting("GPU", "particle_effects", true); } catch { }
            }
            // AMD FSR scales the game's 1920x1080 picture to the window or screen size: FSR 3.1 through
            // amd_fidelityfx_vk.dll (next to GTSport.exe, part of the release), or shadGT's FSR 1 pass.
            // AMD: FSR upscaling (FSR 3.1 through amd_fidelityfx_vk.dll, or shadGT's FSR 1) and
            // FSR 3.1 frame generation (any GPU; independent of the upscaling choice).
            var fsr = AddOption(graphics, "Upscale with AMD FSR (above 1080p)", "GPU", "fsr_enabled", false);
            bool fsr3Installed = File.Exists(Path.Combine(root, "amd_fidelityfx_vk.dll"));
            var fsr3 = AddOption(graphics, fsr3Installed
                ? "Use FSR 3.1.4 (off: FSR 1)"
                : "Use FSR 3.1.4 (needs amd_fidelityfx_vk.dll)", "GPU", "fsr3_enabled", true);
            var rcas = AddOption(graphics, "FSR sharpening (RCAS)", "GPU", "rcas_enabled", true);
            var framegen = AddOption(graphics, fsr3Installed
                ? "FSR frame generation: about 2x the frames (any GPU)"
                : "FSR frame generation (needs amd_fidelityfx_vk.dll)", "GPU", "framegen_enabled", false);
            foreach (var sub in new[] { fsr3, rcas, framegen }) sub.Margin = new Padding(22, 2, 0, 2);
            fsr3.Enabled = fsr.Checked && fsr3Installed;
            rcas.Enabled = fsr.Checked;
            framegen.Enabled = fsr3Installed;
            fsr.CheckedChanged += delegate { fsr3.Enabled = fsr.Checked && fsr3Installed; rcas.Enabled = fsr.Checked; };
            // NVIDIA (the dlss folder): DLSS upscaling through ryGT_dlss.dll and nvngx_dlss.dll, and
            // DLSS frame generation through Streamline (sl.*.dll and nvngx_dlssg.dll; RTX 40 and
            // newer, Windows' hardware-accelerated GPU scheduling on). One frame generation at a time.
            // The release does not include NVIDIA's files: the player adds their own copies with the
            // button below (NVIDIA's DLSS SDK and Streamline SDK, or another game's copies).
            string dlssDir = Path.Combine(root, "dlss");
            var dlss = AddOption(graphics, "", "GPU", "dlss_enabled", false);
            var dlssg = AddOption(graphics, "", "GPU", "dlssg_enabled", false);
            dlssg.Margin = new Padding(22, 2, 0, 2);
            Action refreshDlss = delegate
            {
                bool dlssInstalled = File.Exists(Path.Combine(dlssDir, "ryGT_dlss.dll")) &&
                                     File.Exists(Path.Combine(dlssDir, "nvngx_dlss.dll"));
                bool dlssgInstalled = File.Exists(Path.Combine(dlssDir, "sl.interposer.dll")) &&
                                      File.Exists(Path.Combine(dlssDir, "nvngx_dlssg.dll"));
                dlss.Text = dlssInstalled ? "Upscale with NVIDIA DLSS (RTX cards; instead of FSR)"
                                          : "Upscale with NVIDIA DLSS (add the DLSS files first)";
                dlss.Enabled = dlssInstalled;
                dlssg.Text = dlssgInstalled ? "DLSS frame generation: about 2x the frames (RTX 40+)"
                                            : "DLSS frame generation (add the DLSS files first)";
                dlssg.Enabled = dlssgInstalled;
            };
            refreshDlss();
            var dlssFiles = new Button { Text = "Add DLSS files...", AutoSize = true, Margin = new Padding(22, 2, 0, 4) };
            dlssFiles.Click += delegate
            {
                using (var dialog = new FolderBrowserDialog
                {
                    Description = "Pick the folder with your NVIDIA DLSS files: nvngx_dlss.dll for upscaling; " +
                                  "for frame generation also nvngx_dlssg.dll and Streamline's sl.*.dll and " +
                                  "NvLowLatencyVk.dll (from NVIDIA's DLSS and Streamline SDKs)."
                })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    var names = new[] { "nvngx_dlss.dll", "nvngx_dlssg.dll", "sl.interposer.dll", "sl.common.dll",
                                        "sl.dlss_g.dll", "sl.reflex.dll", "sl.pcl.dll", "NvLowLatencyVk.dll" };
                    int copied = 0;
                    try
                    {
                        Directory.CreateDirectory(dlssDir);
                        foreach (var name in names)
                        {
                            // Found anywhere under the picked folder (the SDKs keep them in bin\x64).
                            var found = Directory.GetFiles(dialog.SelectedPath, name, SearchOption.AllDirectories);
                            if (found.Length == 0) continue;
                            File.Copy(found[0], Path.Combine(dlssDir, name), true);
                            ++copied;
                        }
                    }
                    catch (Exception e)
                    {
                        MessageBox.Show(this, "Could not copy the DLSS files: " + e.Message);
                    }
                    refreshDlss();
                    MessageBox.Show(this, copied == 0
                        ? "No DLSS files were found in that folder."
                        : "Copied " + copied + " DLSS file(s) into the dlss folder.");
                }
            };
            graphics.Controls.Add(dlssFiles);
            framegen.CheckedChanged += delegate { if (framegen.Checked && dlssg.Checked) dlssg.Checked = false; };
            dlssg.CheckedChanged += delegate { if (dlssg.Checked && framegen.Checked) framegen.Checked = false; };
            // GTS-007: in the rain the game renders the scene a second time for the wet track's
            // reflection (about a quarter of a rainy frame's draws). Off: about +30% fps in rain.
            var wet = AddOption(graphics, "Wet-road reflections in the rain (off: much faster in rain)", "GPU",
                                "wet_reflections", true);
            // GTS-008: the rear-view mirror is redrawn every second frame (its picture is kept).
            var mirror = AddOption(graphics, "Rear-view mirror at half rate (a little faster)", "GPU",
                                   "mirror_half_rate", false);
            // Controls: which controller is which player (splitscreen) with a live preview, and the
            // controller and keyboard bindings.
            // Under Shaders (moved there below), so the columns end at about the same height.
            var controls = Section(third, "Controls");
            controls.Controls.Add(new Label
            {
                Text = "2 players for Arcade's Split Screen: pick each player's name and controller, " +
                       "and what each button does. In game, F3 or holding Share + Options " +
                       "(View + Menu) changes controllers.",
                AutoSize = true, MaximumSize = new Size(360, 0), BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 6)
            });
            var controlButtons = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0), WrapContents = true };
            var portsButton = new Button { Text = "Controller ports...", AutoSize = true };
            portsButton.Click += delegate
            {
                using (var form = new PortsForm(ReadStringSetting("Input", "controller_ports", ""),
                                                ReadStringSetting("General", "player_names", "").Split(','),
                                                (ports, names) =>
                                                {
                                                    WriteSetting("Input", "controller_ports", ports);
                                                    // Read by the game's user service (libSceUserService).
                                                    WriteSetting("General", "player_names",
                                                                 string.Join(",", names.Select(n => n.Replace(",", ""))));
                                                }))
                {
                    form.ShowDialog(this);
                }
            };
            var bindingsButton = new Button { Text = "Button bindings...", AutoSize = true };
            bindingsButton.Click += delegate
            {
                using (var form = new ControlsForm(root, Math.Max(1, ReadIntSetting("Input", "keyboard_player", 1)),
                                                   player => WriteSetting("Input", "keyboard_player", player)))
                {
                    form.ShowDialog(this);
                }
            };
            // Racing wheels and pedals (ryty-input.ini / ryty-wheel.ini, read by libScePad).
            var wheelButton = new Button { Text = "Steering wheel...", AutoSize = true };
            wheelButton.Click += delegate
            {
                using (var form = new WheelForm(root)) form.ShowDialog(this);
            };
            controlButtons.Controls.Add(portsButton);
            controlButtons.Controls.Add(bindingsButton);
            controlButtons.Controls.Add(wheelButton);
            controls.Controls.Add(controlButtons);
            var triggerLabel = new Label
            {
                AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 8, 0, 0),
                Text = "Trigger strength"
            };
            var triggerStrength = new TrackBar
            {
                Name = "triggerStrength", Minimum = 0, Maximum = 200, TickFrequency = 25,
                SmallChange = 5, LargeChange = 25, AutoSize = false, Height = 36, Dock = DockStyle.Fill,
                Value = Math.Max(0, Math.Min(200, ReadIntSetting("Input", "trigger_strength", 100))),
                AccessibleName = "Trigger feedback strength", Margin = new Padding(0)
            };
            Action updateTriggerLabel = delegate
            {
                triggerLabel.Text = "Trigger strength: " + triggerStrength.Value + "%" +
                                    (triggerStrength.Value == 0 ? " (off)" : "");
            };
            updateTriggerLabel();
            triggerStrength.ValueChanged += delegate
            {
                updateTriggerLabel();
                try { WriteSetting("Input", "trigger_strength", triggerStrength.Value); }
                catch (Exception e) { MessageBox.Show(this, "Could not save trigger strength: " + e.Message); }
            };
            controls.Controls.Add(triggerLabel);
            controls.Controls.Add(triggerStrength);
            controls.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(360, 0), BackColor = Color.Transparent,
                Text = "0% off, 100% standard, 200% boosted. Updates while playing.",
                ForeColor = Dark.DimText, Tag = "heading", Margin = new Padding(0, 0, 0, 2)
            });

            // Shaders: the stored ones are built while the splash screen shows; a new one pauses
            // the game until it is compiled (instead of a stutter and missing objects).
            var shaders = Section(third, "Shaders");
            AddOption(shaders, "Precompile shaders on the splash screen", "GPU", "precompile_shaders", true);
            // What the game does while a new shader compiles during play (GPU.compile_mode).
            var modeRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 2), WrapContents = false };
            var modeLabel = new Label { Text = "When new shaders compile", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) };
            var mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, FlatStyle = FlatStyle.Flat };
            mode.Items.AddRange(new object[] { "Pause the game", "Slow motion", "Keep going (objects pop in)" });
            mode.SelectedIndex = Math.Max(0, Math.Min(2, ReadIntSetting("GPU", "compile_mode", 1)));
            modeRow.Controls.Add(modeLabel);
            modeRow.Controls.Add(mode);
            shaders.Controls.Add(modeRow);
            var batchRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 2), WrapContents = false };
            var batchLabel = new Label { Text = "Compile batching (when pausing)", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) };
            // How long one pause may go on compiling the shaders of what comes next: longer pauses,
            // fewer of them (GPU.compile_batch_ms).
            var batch = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, FlatStyle = FlatStyle.Flat };
            var batchValues = new[] { 0, 100, 300, 1000 };
            batch.Items.AddRange(new object[] { "Off (only what is needed)", "Short (up to 0.1 s)", "Medium (up to 0.3 s)", "Long (up to 1 s)" });
            int current = ReadIntSetting("GPU", "compile_batch_ms", 0);
            int index = Array.IndexOf(batchValues, current);
            batch.SelectedIndex = index >= 0 ? index : 0;
            batch.SelectedIndexChanged += delegate
            {
                try { WriteSetting("GPU", "compile_batch_ms", batchValues[batch.SelectedIndex]); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            batchRow.Controls.Add(batchLabel);
            batchRow.Controls.Add(batch);
            shaders.Controls.Add(batchRow);
            batch.Enabled = mode.SelectedIndex == 0;
            mode.SelectedIndexChanged += delegate
            {
                batch.Enabled = mode.SelectedIndex == 0;
                try { WriteSetting("GPU", "compile_mode", mode.SelectedIndex); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };

            var logging = Section(left, "Logging"); // under Display, where there is room
            logging.Controls.Add(new Label { Text = "stdout.log minimum level", AutoSize = true,
                BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 4) });
            var logLevel = new ComboBox { Width = 170, DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat, BackColor = Dark.Field, ForeColor = Dark.Text };
            logLevel.Items.AddRange(LogFiles.Levels);
            logLevel.SelectedIndex = LogFiles.LevelIndex(settings.StdoutLevel);
            logLevel.SelectedIndexChanged += delegate
            {
                if (logLevel.SelectedIndex < 0) return;
                settings.StdoutLevel = LogFiles.Levels[logLevel.SelectedIndex];
                try { settings.Save(); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the logging level: " + e.Message); }
            };
            logging.Controls.Add(logLevel);
            logging.Controls.Add(new Label { Text = "Selected level and below, on the next launch.\nAll logs are in the logs folder; previous logs are timestamped.",
                AutoSize = true, MaximumSize = new Size(360, 0), BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 0) });
            third.Controls.SetChildIndex(controls, third.Controls.Count - 1);
            column.Controls.Add(grid);

            // Actions on the left; the status line and the log to their right. Pinned under the
            // settings (not scrolled with them), so Play is always in view and focusing it at start
            // does not scroll the window's top out of view.
            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 128, ColumnCount = 2, RowCount = 2, BackColor = Color.Transparent, Padding = new Padding(24, 6, 26, 12) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var buttons = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 0, 12, 4), WrapContents = false };
            setup = new Button { Text = "Set up", AutoSize = true };
            play = new Button { Text = "Play", AutoSize = true, Tag = "accent", Font = new Font("Segoe UI Semibold", 10f) };
            var open = new Button { Text = "Open folder", AutoSize = true };
            setup.Click += delegate { StartSetup(); };
            play.Click += delegate { Play(); };
            open.Click += delegate { Process.Start("explorer.exe", "\"" + root + "\""); };
            buttons.Controls.AddRange(new Control[] { play, setup, open });
            bottom.Controls.Add(buttons, 0, 0);
            bottom.SetRowSpan(buttons, 2);

            status = new Label { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(2, 0, 0, 4) };
            bottom.Controls.Add(status, 1, 0);

            log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 110, Dock = DockStyle.Fill, Font = new Font("Consolas", 8.5f) };
            bottom.Controls.Add(log, 1, 1);
            Controls.Add(bottom);
            scroll.BringToFront(); // fills what the pinned bar leaves
            foreach (Control control in column.Controls) column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            // As tall as the settings need, as far as the screen allows.
            Load += delegate
            {
                column.PerformLayout();
                int want = column.Height + scroll.AutoScrollMargin.Height + bottom.Height + 2;
                int room = Screen.FromControl(this).WorkingArea.Height - (Height - ClientSize.Height) - 16;
                ClientSize = new Size(ClientSize.Width, Math.Max(MinimumSize.Height - (Height - ClientSize.Height), Math.Min(want, room)));
                CenterToScreen();
            };

            Dark.Apply(this);
            subtitle.ForeColor = Dark.DimText;
            title.ForeColor = Color.White;
            HandleCreated += delegate { Dark.TitleBar(this); };
            Shown += delegate
            {
                ActiveControl = play.Enabled ? (Control)play : setup;
                scroll.AutoScrollPosition = new Point(0, 0);
            };
            RefreshState();
        }

        readonly Image background;

        Image LoadBackground()
        {
            foreach (var path in new[] { Path.Combine(root, "launcher", "background.png"), Path.Combine(root, "launcher", "background.jpg") })
            {
                try { if (File.Exists(path)) return Image.FromStream(new MemoryStream(File.ReadAllBytes(path))); }
                catch { }
            }
            return null;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (!PaintBackdrop(this, e.Graphics)) base.OnPaintBackground(e);
        }

        Bitmap backdrop;

        /// The background picture as it lies under `control` (scaled to cover the window, anchored
        /// right: the picture's subject is on its right). False without a picture.
        public bool PaintBackdrop(Control control, Graphics g)
        {
            if (background == null) return false;
            var client = ClientRectangle;
            if (client.Width <= 0 || client.Height <= 0) return false;
            if (backdrop == null || backdrop.Size != client.Size)
            {
                // Scaled once per window size; panels copy their part of it.
                if (backdrop != null) backdrop.Dispose();
                backdrop = new Bitmap(client.Width, client.Height);
                using (var bg = Graphics.FromImage(backdrop))
                {
                    float scale = Math.Max(client.Width / (float)background.Width, client.Height / (float)background.Height);
                    int width = (int)Math.Ceiling(background.Width * scale), height = (int)Math.Ceiling(background.Height * scale);
                    bg.Clear(Dark.Back);
                    bg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    bg.DrawImage(background, client.Right - width, (client.Height - height) / 2, width, height);
                }
            }
            var offset = control == this ? Point.Empty : PointToClient(control.PointToScreen(Point.Empty));
            g.DrawImage(backdrop, new Rectangle(-offset.X, -offset.Y, backdrop.Width, backdrop.Height));
            return true;
        }

        /// A titled group on a translucent dark panel.
        TableLayoutPanel Section(TableLayoutPanel parent, string heading)
        {
            var panel = new GlassPanel
            {
                ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill,
                Padding = new Padding(12, 8, 12, 10), Margin = new Padding(0, 0, 0, 10)
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.Controls.Add(new Label
            {
                Text = heading.ToUpperInvariant(), AutoSize = true, BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 8.5f), ForeColor = Dark.Heading, Margin = new Padding(0, 0, 0, 4), Tag = "heading"
            });
            parent.Controls.Add(panel);
            return panel;
        }

        TextBox AddRow(TableLayoutPanel table, int row, string label, string value)
        {
            table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, BackColor = Color.Transparent, Margin = new Padding(0, 6, 10, 0) }, 0, row);
            var box = new TextBox { Text = value, Dock = DockStyle.Fill };
            table.Controls.Add(box, 1, row);
            var browse = new Button { Text = "Browse...", AutoSize = true };
            browse.Click += delegate
            {
                using (var dialog = new FolderBrowserDialog { Description = label, SelectedPath = box.Text })
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.SelectedPath;
                }
            };
            table.Controls.Add(browse, 2, row);
            return box;
        }

        string Exe { get { return Path.Combine(root, "GTSport.exe"); } }

        string ConfigPath { get { return Path.Combine(root, "user", "custom_configs", "CUSA03220.json"); } }

        /// A shadGT setting in a section of the GT Sport config (General: show_splash,
        /// show_fps_counter, discord_rpc_enabled; GPU: full_screen, particle_effects, fsr_enabled, fsr3_enabled, rcas_enabled, dlss_enabled,
        /// framegen_enabled, precompile_shaders, compile_freeze).
        bool ReadSetting(string sectionName, string key, bool fallback)
        {
            try
            {
                var config = LoadConfig();
                object found, value;
                var section = config.TryGetValue(sectionName, out found) ? found as Dictionary<string, object> : null;
                if (section != null && section.TryGetValue(key, out value) && value is bool) return (bool)value;
            }
            catch { }
            return fallback;
        }

        int ReadIntSetting(string sectionName, string key, int fallback)
        {
            try
            {
                var config = LoadConfig();
                object found, value;
                var section = config.TryGetValue(sectionName, out found) ? found as Dictionary<string, object> : null;
                if (section != null && section.TryGetValue(key, out value) && value is int) return (int)value;
            }
            catch { }
            return fallback;
        }

        void WriteSetting(string sectionName, string key, object on)
        {
            var config = LoadConfig();
            object found;
            var section = config.TryGetValue(sectionName, out found) ? found as Dictionary<string, object> : null;
            if (section == null) { section = new Dictionary<string, object>(); config[sectionName] = section; }
            section[key] = on;
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            File.WriteAllText(ConfigPath, new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(config),
                              new UTF8Encoding(false));
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

        static int InstalledRamGb()
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
            if (!GlobalMemoryStatusEx(ref status)) return 32;
            return (int)Math.Round(status.TotalPhys / (1024.0 * 1024 * 1024));
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public ushort SpecVersion, DriverVersion, Size, DriverExtra;
            public uint Fields;
            public int PositionX, PositionY;
            public uint DisplayOrientation, DisplayFixedOutput;
            public short Color, Duplex, YResolution, TTOption, Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
            public ushort LogPixels;
            public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
            public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplaySettings(string device, int mode, ref DevMode devMode);

        class Descending<T> : IComparer<T> where T : IComparable<T>
        {
            public int Compare(T a, T b) { return b.CompareTo(a); }
        }

        /// The primary display's modes (resolution, largest first -> refresh rates in Hz, highest
        /// first) and its current mode.
        static SortedDictionary<long, SortedSet<int>> DisplayModes(out int currentWidth, out int currentHeight, out int currentHz)
        {
            var modes = new SortedDictionary<long, SortedSet<int>>(new Descending<long>());
            var mode = new DevMode { Size = (ushort)Marshal.SizeOf(typeof(DevMode)) };
            for (int i = 0; EnumDisplaySettings(null, i, ref mode); ++i)
            {
                if (mode.BitsPerPel < 24 || mode.PelsWidth < 1280 || mode.PelsHeight < 720) continue;
                long key = ((long)mode.PelsWidth << 32) | mode.PelsHeight;
                SortedSet<int> rates;
                if (!modes.TryGetValue(key, out rates)) modes[key] = rates = new SortedSet<int>(new Descending<int>());
                if (mode.DisplayFrequency > 1) rates.Add((int)mode.DisplayFrequency);
            }
            currentWidth = currentHeight = currentHz = 0;
            if (EnumDisplaySettings(null, -1, ref mode))
            {
                currentWidth = (int)mode.PelsWidth;
                currentHeight = (int)mode.PelsHeight;
                currentHz = (int)mode.DisplayFrequency;
            }
            return modes;
        }

        string ReadStringSetting(string sectionName, string key, string fallback)
        {
            try
            {
                var config = LoadConfig();
                object found, value;
                var section = config.TryGetValue(sectionName, out found) ? found as Dictionary<string, object> : null;
                if (section != null && section.TryGetValue(key, out value) && value is string) return (string)value;
            }
            catch { }
            return fallback;
        }

        static string ModeText(long key)
        {
            return string.Format("{0} x {1}", key >> 32, key & 0xffffffff);
        }

        // Screen: windowed, borderless fullscreen (the desktop's resolution and refresh rate), or
        // exclusive fullscreen at a chosen resolution and refresh rate (GPU.full_screen,
        // full_screen_mode, window_width/height, fullscreen_refresh_rate). The game draws at
        // 1920x1080 and runs at up to 60 fps; FSR or DLSS scale it to the screen, and a higher
        // refresh rate shows frame generation's extra frames.
        void AddScreenSettings(TableLayoutPanel panel)
        {
            int desktopWidth, desktopHeight, desktopHz;
            var modes = DisplayModes(out desktopWidth, out desktopHeight, out desktopHz);

            var screenRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2), WrapContents = false };
            var screen = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, FlatStyle = FlatStyle.Flat };
            screen.Items.AddRange(new object[] { "Windowed", "Borderless fullscreen", "Exclusive fullscreen" });
            bool full = ReadSetting("GPU", "full_screen", false);
            bool exclusive = ReadStringSetting("GPU", "full_screen_mode", "Windowed") == "Fullscreen";
            screen.SelectedIndex = !full ? 0 : exclusive ? 2 : 1;
            screenRow.Controls.Add(new Label { Text = "Screen", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) });
            screenRow.Controls.Add(screen);
            panel.Controls.Add(screenRow);

            var resolutionRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2), WrapContents = false };
            var resolution = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, FlatStyle = FlatStyle.Flat };
            var keys = modes.Keys.ToList();
            int width = ReadIntSetting("GPU", "window_width", 0), height = ReadIntSetting("GPU", "window_height", 0);
            if (width < 640 || height < 360)
            {
                width = desktopWidth > 0 ? Math.Min(1920, desktopWidth) : 1920;
                height = desktopHeight > 0 ? Math.Min(1080, desktopHeight) : 1080;
            }
            long wanted = ((long)width << 32) | (uint)height;
            if (!keys.Contains(wanted)) keys.Add(wanted);
            foreach (var key in keys) resolution.Items.Add(ModeText(key));
            resolution.SelectedIndex = keys.IndexOf(wanted);
            resolutionRow.Controls.Add(new Label { Text = "Resolution", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) });
            resolutionRow.Controls.Add(resolution);
            panel.Controls.Add(resolutionRow);

            var refreshRow = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2), WrapContents = false };
            var refresh = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, FlatStyle = FlatStyle.Flat };
            refreshRow.Controls.Add(new Label { Text = "Refresh rate", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) });
            refreshRow.Controls.Add(refresh);
            panel.Controls.Add(refreshRow);

            bool loading = true;
            Action<int> fillRates = delegate (int wantedHz)
            {
                refresh.Items.Clear();
                refresh.Items.Add(desktopHz > 0 ? string.Format("Desktop ({0} Hz)", desktopHz) : "Desktop");
                SortedSet<int> rates;
                if (modes.TryGetValue(keys[resolution.SelectedIndex], out rates))
                {
                    foreach (int hz in rates) refresh.Items.Add(hz + " Hz");
                }
                int index = refresh.Items.IndexOf(wantedHz + " Hz");
                refresh.SelectedIndex = wantedHz > 0 && index > 0 ? index : 0;
            };
            Action save = delegate
            {
                if (loading) return;
                try
                {
                    WriteSetting("GPU", "full_screen", screen.SelectedIndex != 0);
                    WriteSetting("GPU", "full_screen_mode", screen.SelectedIndex == 2 ? "Fullscreen" : "Borderless");
                    long key = keys[resolution.SelectedIndex];
                    WriteSetting("GPU", "window_width", (int)(key >> 32));
                    WriteSetting("GPU", "window_height", (int)(key & 0xffffffff));
                    WriteSetting("GPU", "fullscreen_refresh_rate",
                                 refresh.SelectedIndex > 0 ? int.Parse(((string)refresh.SelectedItem).Replace(" Hz", "")) : 0);
                }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            Action enable = delegate
            {
                // Borderless fullscreen always uses the desktop's mode.
                resolution.Enabled = screen.SelectedIndex != 1;
                refresh.Enabled = screen.SelectedIndex == 2;
            };
            fillRates(ReadIntSetting("GPU", "fullscreen_refresh_rate", 0));
            enable();
            screen.SelectedIndexChanged += delegate { enable(); save(); };
            resolution.SelectedIndexChanged += delegate
            {
                if (loading) return;
                loading = true;
                int hz = refresh.SelectedIndex > 0 ? int.Parse(((string)refresh.SelectedItem).Replace(" Hz", "")) : 0;
                fillRates(hz);
                loading = false;
                save();
            };
            refresh.SelectedIndexChanged += delegate { save(); };
            loading = false;
        }

        // System memory (GPU.system_ram_gb): how much the game may use for things it can do
        // without. With 16 GB the driver's pipeline cache file (it grows past 1 GB and is loaded
        // whole, then copied again when saved) is off and fewer freed GPU images are kept for
        // reuse. Tokyo rain race, committed memory: 32 GB setting ~11 GB average / 13.5 GB peak,
        // 16 GB setting ~8.6 / 9.0 GB. Picked from the installed RAM the first time.
        // Screen shape (GPU.aspect_ratio, GTS-009 in renderer patch 0051): GT Sport draws a 16:9
        // picture. "Fill the screen" widens the race camera to the shape of the game's window or
        // screen and stretches the picture over it (ultrawide without black bars; the HUD and menus
        // are stretched too). Read when the game starts.
        void AddShapeSetting(TableLayoutPanel panel)
        {
            var row = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2), WrapContents = false };
            var label = new Label { Text = "Screen shape", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) };
            var shape = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, FlatStyle = FlatStyle.Flat };
            shape.Items.AddRange(new object[] { "16:9 (the game's own)", "Fill the screen (wide)" });
            shape.SelectedIndex = ReadStringSetting("GPU", "aspect_ratio", "16:9") == "auto" ? 1 : 0;
            shape.SelectedIndexChanged += delegate
            {
                try { WriteSetting("GPU", "aspect_ratio", shape.SelectedIndex == 1 ? "auto" : "16:9"); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            row.Controls.Add(label);
            row.Controls.Add(shape);
            panel.Controls.Add(row);
        }

        void AddMemorySetting(TableLayoutPanel panel)
        {
            var row = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 2), WrapContents = false };
            var label = new Label { Text = "System memory (RAM)", AutoSize = false, Width = 196, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) };
            var ram = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, FlatStyle = FlatStyle.Flat };
            ram.Items.AddRange(new object[] { "16 GB (minimum)", "32 GB+ (recommended)" });
            int stored = ReadIntSetting("GPU", "system_ram_gb", 0);
            int gb = stored > 0 ? stored : InstalledRamGb();
            ram.SelectedIndex = gb >= 24 ? 1 : 0;
            Action apply = delegate
            {
                bool large = ram.SelectedIndex == 1;
                WriteSetting("GPU", "system_ram_gb", large ? 32 : 16);
                WriteSetting("Vulkan", "pipeline_cache_enabled", large);
                WriteSetting("GPU", "image_recycle_mb", large ? 512 : 128);
            };
            if (stored == 0)
            {
                try { apply(); } catch { }
            }
            ram.SelectedIndexChanged += delegate
            {
                try { apply(); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            row.Controls.Add(label);
            row.Controls.Add(ram);
            panel.Controls.Add(row);
        }

        // Renderer (GPU.native_renderer): the old renderer (shadGT's, with the direct memory
        // renderer; the default) or the native renderer (experimental: GT Sport's command buffers
        // recorded straight into Vulkan, faster in races; RYGT_NATIVE=1 or =0 overrides the choice).
        void AddRendererSetting(TableLayoutPanel panel)
        {
            var row = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 4), WrapContents = false };
            var label = new Label { Text = "Renderer", AutoSize = false, Width = 110, Height = 20, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) };
            var renderer = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, FlatStyle = FlatStyle.Flat };
            renderer.Items.AddRange(new object[] { "Old renderer (default)", "Native renderer (experimental)" });
            renderer.SelectedIndex = ReadSetting("GPU", "native_renderer", false) ? 1 : 0;
            renderer.SelectedIndexChanged += delegate
            {
                try { WriteSetting("GPU", "native_renderer", renderer.SelectedIndex == 1); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            row.Controls.Add(label);
            row.Controls.Add(renderer);
            panel.Controls.Add(row);
        }

        CheckBox AddOption(TableLayoutPanel panel, string text, string sectionName, string key, bool fallback)
        {
            var box = new DarkCheckBox { Text = text, AutoSize = true, Checked = ReadSetting(sectionName, key, fallback), Margin = new Padding(0, 2, 0, 2) };
            box.CheckedChanged += delegate
            {
                try { WriteSetting(sectionName, key, box.Checked); }
                catch (Exception e) { MessageBox.Show(this, "Could not save the setting: " + e.Message); }
            };
            panel.Controls.Add(box);
            return box;
        }

        Dictionary<string, object> LoadConfig()
        {
            if (!File.Exists(ConfigPath)) return new Dictionary<string, object>();
            var parsed = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(File.ReadAllText(ConfigPath));
            return parsed as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        void RefreshState()
        {
            bool ready = File.Exists(Exe) && Directory.Exists(Path.Combine(root, "app0", "sce_module"));
            play.Enabled = ready && !busy;
            setup.Enabled = !busy;
            if (!busy) status.Text = ready ? "Ready. Press Play." : "Not set up yet. Pick the folders and press Set up.";
        }

        readonly object logLock = new object();
        string setupLogPath;

        void Log(string line)
        {
            lock (logLock)
            {
                try
                {
                    if (setupLogPath == null) setupLogPath = LogFiles.Prepare(root, "setup.log");
                    File.AppendAllText(setupLogPath, line + Environment.NewLine);
                }
                catch { }
            }
            if (!IsHandleCreated || IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action<string>(AppendLogText), line); return; }
            AppendLogText(line);
        }

        void AppendLogText(string line)
        {
            if (!IsDisposed) log.AppendText(line + Environment.NewLine);
        }
        void SetStatus(string text)
        {
            Log("== " + text);
            if (!IsHandleCreated) return;
            if (InvokeRequired) { BeginInvoke(new Action<string>(SetStatusText), text); return; }
            SetStatusText(text);
        }

        void SetStatusText(string text)
        {
            status.Text = text;
        }

        /// RyGT.exe --setup: the same set up without the window, from the folders in RyGT.ini.
        public int SetupWithoutWindow()
        {
            try { setupLogPath = LogFiles.Prepare(root, "setup.log"); RunSetup(); return 0; }
            catch (Exception e) { Log("ERROR: " + e.Message); return 1; }
        }

        void StartSetup()
        {
            settings.Game = game.Text.Trim();
            settings.Update = update.Text.Trim();
            settings.Modules = modules.Text.Trim();
            settings.Fonts = fonts.Text.Trim();
            try { settings.Save(); } catch { }
            try { setupLogPath = LogFiles.Prepare(root, "setup.log"); }
            catch (Exception e) { MessageBox.Show(this, "Could not prepare setup log: " + e.Message); return; }
            busy = true;
            RefreshState();
            log.Clear();
            var thread = new Thread(delegate ()
            {
                bool ok = false;
                try { RunSetup(); ok = true; }
                catch (Exception e) { Log("ERROR: " + e.Message); }
                BeginInvoke(new Action(delegate
                {
                    busy = false;
                    RefreshState();
                    if (ok) status.Text = "Set up finished. Press Play.";
                    else status.Text = "Set up failed; see the log.";
                }));
            });
            thread.IsBackground = true;
            thread.Start();
        }

        void RunSetup()
        {
            string baseDir = settings.Game, updateDir = settings.Update;
            if (!Directory.Exists(baseDir)) throw new Exception("Pick the game folder (the one holding gt00.vol and eboot.bin).");
            if (!File.Exists(Path.Combine(baseDir, "gt00.vol")))
                throw new Exception("The game folder has no gt00.vol. Pick the base game folder of CUSA03220.");
            if (updateDir.Length > 0 && !Directory.Exists(updateDir)) throw new Exception("The update folder does not exist.");
            if (updateDir.Length == 0) updateDir = null;
            string gameDir = updateDir ?? baseDir; // the newest executable, modules and data index

            string version = ReadAppVersion(Path.Combine(gameDir, "sce_sys", "param.sfo"));
            Log("Game version: " + (version ?? "unknown"));
            if (version != null && version != "01.69")
                throw new Exception("This build is for update 1.69, but the game is " + version + ". Pick the 1.69 update folder too.");

            string fontDir, font2Dir;
            FindFonts(settings.Fonts, out fontDir, out font2Dir);
            var firmware = new Dictionary<string, string>();
            foreach (var name in FirmwareModules)
            {
                var found = FindModule(settings.Modules, name);
                if (found == null) throw new Exception("System module " + name + " was not found under " + settings.Modules + ".");
                firmware[name] = found;
            }
            foreach (var name in OptionalFirmwareModules)
            {
                var found = FindModule(settings.Modules, name);
                if (found != null) firmware[name] = found;
                else Log("Note: " + name + " is not in the system modules folder; some related game features may be unavailable.");
            }

            // 1. The executable and modules to convert, unwrapped to plain ELF.
            SetStatus("Preparing the game files...");
            string work = Path.Combine(root, "convert");
            string workModules = Path.Combine(work, "sce_module");
            if (Directory.Exists(workModules)) Directory.Delete(workModules, true);
            Directory.CreateDirectory(workModules);
            Unwrap(Path.Combine(gameDir, "eboot.bin"), Path.Combine(work, "eboot.elf"));
            foreach (var name in new[] { "libc.prx", "libSceFios2.prx" })
            {
                string source = FirstExisting(Path.Combine(gameDir, "sce_module", name), Path.Combine(baseDir, "sce_module", name));
                if (source == null) throw new Exception("The game's sce_module\\" + name + " is missing.");
                Unwrap(source, Path.Combine(workModules, name));
            }
            foreach (var name in new[] { "libSceSecure.prx", "logiWheel.prx" })
            {
                string source = FirstExisting(Path.Combine(gameDir, name), Path.Combine(baseDir, name));
                if (source != null) Unwrap(source, Path.Combine(workModules, name));
            }
            foreach (var pair in firmware) Unwrap(pair.Value, Path.Combine(workModules, pair.Key + ".prx"));

            // 2. Convert.
            SetStatus("Converting the game for Windows (this takes a while)...");
            string converter = Path.Combine(root, "converter", "ryty.exe");
            if (!File.Exists(converter)) throw new Exception("converter\\ryty.exe is missing from the RyGT folder.");
            Directory.CreateDirectory(Path.Combine(root, "app0", "sce_module"));
            int code = Run(converter, "--windows --ps4 --skip-syscall-check --registry --report \"" +
                Path.Combine(work, "port-report.json") + "\" \"" + Path.Combine(work, "eboot.elf") + "\" \"" + Exe + "\"", work);
            if (code != 0 || !File.Exists(Exe)) throw new Exception("The converter failed (exit code " + code + ").");
            GameExe.Prepare(Exe);
            Log(Wine.Detected ? "Made GTSport.exe a Windows program." : "Gave GTSport.exe the ryGT icon and made it a Windows program.");

            // 3. app0: the game's files, the update's over the base game's, linked rather than copied.
            SetStatus("Linking the game files...");
            LinkGameFiles(baseDir, updateDir);

            // 4. Fonts.
            SetStatus("Copying the fonts...");
            CopyDirectory(fontDir, Path.Combine(root, "sys", "common", "font"));
            CopyDirectory(font2Dir, Path.Combine(root, "sys", "common", "font2"));

            // 5. Offline livery and decal scripts, made from this game's own files.
            SetStatus("Making the offline livery scripts from your game files...");
            if (!BuildGameScripts(baseDir, updateDir))
                Log("Note: the offline livery scripts could not be made; liveries will not apply offline.");

            // 6. Shaders other players have met, prepared from this game's own files, into the
            // shader folder every RyGT folder shares.
            UsePortableShaderCache();
            if (File.Exists(ShaderPack))
            {
                SetStatus("Preparing shaders from your game files (a few minutes)...");
                if (!PrepareShaders(baseDir, updateDir))
                    Log("Note: the shaders could not be prepared; they will compile while you play.");
            }

            Log("Set up finished.");
        }

        // The game scripts GTS-005 serves from patches\scripts: offline livery apply (UI_KIT, LIVERY2,
        // THUMBNAIL) and the decals folder (GALLERY, GRIM). They are made from Polyphony's scripts, so
        // they are not shipped: tools\adcpatch\adcpatch.exe reads them from the player's own game
        // volumes (GTToolsSharp) and patches them (GTAdhocToolchain), both by Nenkai, MIT licensed.
        static readonly string[] GameScripts = { "UI_KIT.ADC", "LIVERY2.ADC", "THUMBNAIL.ADC", "GALLERY.ADC", "GRIM.ADC" };

        bool GameScriptsMissing()
        {
            string dir = Path.Combine(root, "patches", "scripts");
            return GameScripts.Any(name => !File.Exists(Path.Combine(dir, name)));
        }

        // Missing, or made by another toolsdcpatch (a newer build ships a newer one): the scripts
        // folder keeps a hash of the adcpatch.exe that made it.
        bool GameScriptsOutdated()
        {
            if (GameScriptsMissing()) return true;
            string tool = Path.Combine(root, "tools", "adcpatch", "adcpatch.exe");
            if (!File.Exists(tool)) return false;
            string stamp = Path.Combine(root, "patches", "scripts", "made-by.txt");
            try { return !File.Exists(stamp) || File.ReadAllText(stamp).Trim() != ToolHash(tool); }
            catch { return true; }
        }

        static string ToolHash(string tool)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var stream = File.OpenRead(tool))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        bool BuildGameScripts(string baseDir, string updateDir)
        {
            string tool = Path.Combine(root, "tools", "adcpatch", "adcpatch.exe");
            if (!File.Exists(tool)) { Log("tools\\adcpatch\\adcpatch.exe is missing."); return false; }
            string output = Path.Combine(root, "patches", "scripts");
            Func<string, string> arg = d => "\"" + (d ?? "").TrimEnd('\\') + "\"";
            string arguments = "all " + arg(baseDir) + " " + arg(updateDir) + " " + arg(output);
            Log("> adcpatch.exe " + arguments);
            var info = new ProcessStartInfo(tool, arguments)
            {
                WorkingDirectory = Path.GetDirectoryName(tool), UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            var lines = new List<string>();
            using (var process = new Process { StartInfo = info })
            {
                DataReceivedEventHandler collect = delegate (object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (lines) lines.Add(e.Data);
                    if (e.Data.StartsWith("[OK]") || e.Data.StartsWith("[X]") || e.Data.Contains(" scripts patched"))
                        Log(e.Data);
                };
                process.OutputDataReceived += collect;
                process.ErrorDataReceived += collect;
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                lock (lines)
                {
                    try { File.WriteAllLines(Path.Combine(Path.GetDirectoryName(tool), "adcpatch.log"), lines); } catch { }
                }
                bool ok = process.ExitCode == 0 && !GameScriptsMissing();
                if (ok)
                {
                    try { File.WriteAllText(Path.Combine(output, "made-by.txt"), ToolHash(tool)); } catch { }
                }
                return ok;
            }
        }

        // Shader pre-build: tools\adcpatch\shaders.pack holds the settings of the shaders and
        // pipelines RyGT's players have met (no shader code). shaderprep writes them into the
        // shader cache and copies each shader's program out of this PC's game files; the renderer
        // translates them on the splash screen and builds the pipelines there, so they don't
        // pause the game the first time a car or track shows up.
        string ShaderPack { get { return Path.Combine(root, "tools", "adcpatch", "shaders.pack"); } }
        string ShaderPackStamp { get { return Path.Combine(ShaderFolder, "shaders-pack.txt"); } }

        bool ShadersOutdated()
        {
            if (!File.Exists(ShaderPack)) return false;
            try { return !File.Exists(ShaderPackStamp) || File.ReadAllText(ShaderPackStamp).Trim() != ToolHash(ShaderPack); }
            catch { return true; }
        }

        bool PrepareShaders(string baseDir, string updateDir)
        {
            string tool = Path.Combine(root, "tools", "adcpatch", "adcpatch.exe");
            if (!File.Exists(tool) || !File.Exists(ShaderPack)) return false;
            string cache = ShaderFolder;
            Directory.CreateDirectory(cache);
            Func<string, string> arg = d => "\"" + (d ?? "").TrimEnd('\\') + "\"";
            string arguments = "shaderprep " + arg(baseDir) + " " + arg(updateDir) + " " + arg(ShaderPack) + " " + arg(cache);
            Log("> adcpatch.exe " + arguments);
            var info = new ProcessStartInfo(tool, arguments)
            {
                WorkingDirectory = Path.GetDirectoryName(tool), UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            var lines = new List<string>();
            using (var process = new Process { StartInfo = info })
            {
                DataReceivedEventHandler collect = delegate (object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (lines) lines.Add(e.Data);
                    if (e.Data.Contains(" programs copied")) Log(e.Data);
                };
                process.OutputDataReceived += collect;
                process.ErrorDataReceived += collect;
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                lock (lines)
                {
                    try { File.WriteAllLines(Path.Combine(Path.GetDirectoryName(tool), "shaderprep.log"), lines); } catch { }
                }
                if (process.ExitCode != 0) return false;
                try { File.WriteAllText(ShaderPackStamp, ToolHash(ShaderPack)); } catch { }
                return true;
            }
        }

        static string ReadAppVersion(string sfo)
        {
            if (!File.Exists(sfo)) return null;
            var d = File.ReadAllBytes(sfo);
            if (d.Length < 20 || BitConverter.ToUInt32(d, 0) != 0x46535000) return null;
            int keys = (int)BitConverter.ToUInt32(d, 8), data = (int)BitConverter.ToUInt32(d, 12), count = (int)BitConverter.ToUInt32(d, 16);
            for (int i = 0; i < count; i++)
            {
                int e = 20 + i * 16;
                int keyOffset = BitConverter.ToUInt16(d, e);
                int length = (int)BitConverter.ToUInt32(d, e + 4);
                int dataOffset = (int)BitConverter.ToUInt32(d, e + 12);
                int end = keys + keyOffset;
                while (end < d.Length && d[end] != 0) end++;
                string key = Encoding.ASCII.GetString(d, keys + keyOffset, end - keys - keyOffset);
                if (key == "APP_VER") return Encoding.ASCII.GetString(d, data + dataOffset, length).TrimEnd('\0');
            }
            return null;
        }

        static void FindFonts(string selected, out string font, out string font2)
        {
            if (!Directory.Exists(selected)) throw new Exception("Pick the fonts folder (the one holding font and font2).");
            if (Directory.Exists(Path.Combine(selected, "font")) && Directory.Exists(Path.Combine(selected, "font2")))
            {
                font = Path.Combine(selected, "font");
                font2 = Path.Combine(selected, "font2");
                return;
            }
            var parent = Directory.GetParent(selected);
            if (parent != null && Path.GetFileName(selected).Equals("font", StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(Path.Combine(parent.FullName, "font2")))
            {
                font = selected;
                font2 = Path.Combine(parent.FullName, "font2");
                return;
            }
            throw new Exception("The fonts folder needs font and font2 folders inside it.");
        }

        static string FindModule(string folder, string name)
        {
            if (!Directory.Exists(folder)) throw new Exception("Pick the system modules folder.");
            foreach (var extension in new[] { ".sprx", ".prx", ".elf" })
            {
                var hit = Directory.EnumerateFiles(folder, name + extension, SearchOption.AllDirectories).FirstOrDefault();
                if (hit != null) return hit;
            }
            return null;
        }

        static string FirstExisting(params string[] paths)
        {
            return paths.FirstOrDefault(File.Exists);
        }

        void Unwrap(string source, string destination)
        {
            var bytes = File.ReadAllBytes(source);
            File.WriteAllBytes(destination, Self.RelroToLoad(Self.ToElf(bytes, Path.GetFileName(source))));
            Log("Prepared " + Path.GetFileName(destination) + " from " + source);
        }

        int Run(string exe, string arguments, string directory)
        {
            Log("> " + Path.GetFileName(exe) + " " + arguments);
            var info = new ProcessStartInfo(exe, arguments)
            {
                WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var process = new Process { StartInfo = info })
            {
                var lines = new List<string>();
                DataReceivedEventHandler collect = delegate (object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (lines) lines.Add(e.Data);
                    if (e.Data.StartsWith("FAIL") || e.Data.StartsWith("error") || e.Data.Contains("Exception")) Log(e.Data);
                };
                process.OutputDataReceived += collect;
                process.ErrorDataReceived += collect;
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                lock (lines) File.WriteAllLines(Path.Combine(directory, "convert.log"), lines);
                Log("Converter finished with exit code " + process.ExitCode + " (full output in convert\\convert.log).");
                return process.ExitCode;
            }
        }

        void LinkGameFiles(string baseDir, string updateDir)
        {
            string app0 = Path.Combine(root, "app0");
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var dirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in new[] { baseDir, updateDir })
            {
                if (source == null) continue;
                foreach (var file in Directory.GetFiles(source))
                {
                    string name = Path.GetFileName(file);
                    if (!name.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase)) files[name] = file;
                }
                foreach (var dir in Directory.GetDirectories(source))
                {
                    string name = Path.GetFileName(dir);
                    if (!name.Equals("sce_module", StringComparison.OrdinalIgnoreCase)) dirs[name] = dir;
                }
            }
            // GT Sport only uses its data archives when gt.idx is there; without it the game closes
            // right after its splash screen.
            if (!files.ContainsKey("gt.idx"))
                throw new Exception("Neither game folder has gt.idx. Pick the GT Sport folder (CUSA03220) and its update, " +
                                    "the folders with gt.idx and the gt*.vol files.");
            int linked = 0, hard = 0, symbolic = 0;
            var unixLinks = new Dictionary<string, string>(); // under Wine: link -> target
            foreach (var pair in files)
            {
                string target = Path.Combine(app0, pair.Key);
                if (File.Exists(target)) File.Delete(target);
                if (Native.CreateHardLink(target, pair.Value, IntPtr.Zero)) { linked++; hard++; continue; }
                if (Wine.Detected) { unixLinks[target] = pair.Value; continue; }
                if (Native.CreateSymbolicLink(target, pair.Value, 2)) { linked++; symbolic++; continue; } // needs Developer Mode
                throw new Exception("Could not link " + pair.Value + ". Put the RyGT folder on the same drive as the game, " +
                                    "or turn on Developer Mode in Windows settings.");
            }
            if (Wine.Detected)
            {
                foreach (var pair in dirs) unixLinks[Path.Combine(app0, pair.Key)] = pair.Value;
                Wine.Link(unixLinks);
                foreach (var pair in unixLinks)
                {
                    if (dirs.ContainsKey(Path.GetFileName(pair.Key)) ? !Directory.Exists(pair.Key) : !File.Exists(pair.Key))
                        throw new Exception("Could not link " + pair.Value + " into app0.");
                    if (!dirs.ContainsKey(Path.GetFileName(pair.Key))) linked++;
                }
                Log("Linked " + linked + " game files and " + dirs.Count + " folders into app0.");
                return;
            }
            foreach (var pair in dirs)
            {
                string target = Path.Combine(app0, pair.Key);
                if (Directory.Exists(target)) Directory.Delete(target, false);
                var info = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + target + "\" \"" + pair.Value + "\"")
                { UseShellExecute = false, CreateNoWindow = true };
                using (var process = Process.Start(info)) process.WaitForExit();
                if (!Directory.Exists(target)) throw new Exception("Could not link the folder " + pair.Value + ".");
            }
            foreach (var pair in files)
            {
                string problem = ReadProblem(Path.Combine(app0, pair.Key), new FileInfo(pair.Value).Length);
                if (problem != null)
                    throw new Exception("app0\\" + pair.Key + " " + problem + " after linking it to " + pair.Value +
                                        ". Put the RyGT folder on the same drive as the game and press Set up again.");
            }
            Log("Linked " + linked + " game files (" + hard + " hard links, " + symbolic + " symbolic links) and " +
                dirs.Count + " folders into app0.");
        }

        /// Why a linked game file can't be used (missing, unreadable or a different size), or null.
        /// Opening it follows a symbolic link, unlike FileInfo.
        static string ReadProblem(string path, long expectedLength)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (expectedLength >= 0 && stream.Length != expectedLength)
                        return "has " + stream.Length + " bytes instead of " + expectedLength;
                }
                return null;
            }
            catch (FileNotFoundException) { return "is missing"; }
            catch (Exception e) { return "can't be read (" + e.Message.TrimEnd('.') + ")"; }
        }

        static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            foreach (var dir in Directory.GetDirectories(source))
                CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }

        // Everything RyGT makes stays in the RyGT folder (portable), and the shaders are in plain sight
        // in <RyGT>\shaders (the renderer's cache folder), never in AppData. Older folders kept them in
        // user\cache: a real folder there is moved to shaders once. Builds from 2026-10-18 to 10-20
        // made user\cache a link to one AppData folder shared by every RyGT folder; its shaders are
        // copied instead (the AppData folder stays for other RyGT folders still linked to it).
        static string OldSharedShaderCache
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "RyGT", "shader-cache");
            }
        }

        string ShaderFolder { get { return Path.Combine(root, "shaders"); } }

        bool OldShaderCacheExists()
        {
            return Directory.Exists(Path.Combine(root, "user", "cache"));
        }

        void UsePortableShaderCache()
        {
            string old = Path.Combine(root, "user", "cache");
            try
            {
                var info = new DirectoryInfo(old);
                if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // File by file, skipping what an earlier run (perhaps closed halfway) already
                    // copied, so a second start picks up where the first stopped. A link to a
                    // shared folder that is gone is just removed.
                    if (Directory.Exists(old))
                    {
                        Log("Copying the shaders from the shared AppData folder into the shaders folder (once)...");
                        int copied = 0;
                        CopyShaders(old, ShaderFolder, ref copied);
                        Log("Copied " + copied + " shader files.");
                    }
                    Directory.Delete(old, false); // the link only, not the shared folder
                    Log("Shaders are kept in the shaders folder now. " + OldSharedShaderCache +
                        " can be deleted once no other RyGT folder uses it.");
                }
                else if (info.Exists)
                {
                    Log("Moving the shaders from user\\cache to the shaders folder (once)...");
                    if (!Directory.Exists(ShaderFolder)) Directory.Move(old, ShaderFolder);
                    else { MergeShaderCache(old, ShaderFolder, true); Directory.Delete(old, true); }
                }
                Directory.CreateDirectory(ShaderFolder);
            }
            catch (Exception e)
            {
                Log("Note: shader folder: " + e.Message);
                try { Directory.CreateDirectory(ShaderFolder); } catch { }
            }
        }

        // Copies a shader folder's files that the target doesn't have yet, with progress in the status
        // line. The driver's pipeline cache file (several GB, unused since FIX-165) stays behind.
        void CopyShaders(string source, string target, ref int copied)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                    name.IndexOf(".vkpipelinecache", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                string destination = Path.Combine(target, name);
                if (File.Exists(destination)) continue;
                File.Copy(file, destination + ".part", true);
                File.Move(destination + ".part", destination);
                if (++copied % 2000 == 0) SetStatus("Copying your shaders into the shaders folder (once): " + copied + " files...");
            }
            foreach (var dir in Directory.GetDirectories(source))
                CopyShaders(dir, Path.Combine(target, Path.GetFileName(dir)), ref copied);
        }

        static void MergeShaderCache(string source, string target, bool move = true)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                string destination = Path.Combine(target, name);
                if (!File.Exists(destination))
                {
                    if (move) File.Move(file, destination); else File.Copy(file, destination);
                }
                else if (name.EndsWith(".vkpipelinecache", StringComparison.OrdinalIgnoreCase) &&
                         new FileInfo(file).Length > new FileInfo(destination).Length)
                    File.Copy(file, destination, true);
            }
            foreach (var dir in Directory.GetDirectories(source))
                MergeShaderCache(dir, Path.Combine(target, Path.GetFileName(dir)), move);
        }

        void Play()
        {
            if (!File.Exists(Exe)) { MessageBox.Show(this, "Press Set up first."); return; }
            // Without its data index GT Sport closes right after the splash screen.
            string dataProblem = ReadProblem(Path.Combine(root, "app0", "gt.idx"), -1);
            if (dataProblem != null)
            {
                MessageBox.Show(this, "GT Sport's data index app0\\gt.idx " + dataProblem + ", so the game can't start.\n\n" +
                                "Press Set up again. If the RyGT folder is on a different drive than the game, move it " +
                                "to the game's drive first.");
                return;
            }
            // The one-time steps below can take minutes (copying shaders, preparing them), so they run
            // beside the window, which keeps showing their progress.
            string gameDir = settings.Game;
            string updateDir = settings.Update.Length > 0 && Directory.Exists(settings.Update) ? settings.Update : null;
            busy = true;
            RefreshState();
            var thread = new Thread(delegate ()
            {
                try
                {
                    if (OldShaderCacheExists()) SetStatus("Moving your shaders to the shaders folder (once)...");
                    UsePortableShaderCache();
                    // Folders set up by an earlier RyGT.exe or adcpatch get the offline livery scripts
                    // (again) now (a few seconds).
                    if (GameScriptsOutdated() && Directory.Exists(gameDir))
                    {
                        SetStatus("Making the offline livery scripts from your game files...");
                        try { BuildGameScripts(gameDir, updateDir); }
                        catch (Exception e) { Log("Offline livery scripts: " + e.Message); }
                    }
                    // A newer shaders.pack (or a folder set up before it existed): prepared once now.
                    if (ShadersOutdated() && Directory.Exists(gameDir))
                    {
                        SetStatus("Preparing shaders from your game files (a few minutes, once)...");
                        try { PrepareShaders(gameDir, updateDir); }
                        catch (Exception e) { Log("Shaders: " + e.Message); }
                    }
                }
                catch (Exception e) { Log("ERROR: " + e.Message); }
                BeginInvoke(new Action(delegate
                {
                    busy = false;
                    RefreshState();
                    StartGame(root);
                    status.Text = "GT Sport is starting. Logs: logs\\stdout.log and logs\\stderr.log.";
                }));
            });
            thread.IsBackground = true;
            thread.Start();
        }

        /// Starts GTSport.exe with session log rotation and waits for GPU recovery if Vulkan loses
        /// its device.
        static void StartGame(string root)
        {
            string exe = Path.Combine(root, "GTSport.exe");
            // Folders set up by an earlier RyGT.exe get the icon and the Windows subsystem now.
            try { if (!GameExe.IsPrepared(exe)) GameExe.Prepare(exe); } catch { }
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool recoveredOnce = false;
                while (true)
                {
                    string logDirectory;
                    try
                    {
                        logDirectory = LogFiles.PrepareRuntimeLogs(root);
                    }
                    catch (Exception e)
                    {
                        AppendSetupLog(root, "Could not prepare game logs: " + e.Message);
                        return;
                    }

                    Process started;
                    try
                    {
                        var start = new ProcessStartInfo(exe)
                        { WorkingDirectory = root, UseShellExecute = false };
                        start.EnvironmentVariables["RYGT_LOG_DIRECTORY"] = logDirectory;
                        started = Process.Start(start);
                    }
                    catch (Exception e)
                    {
                        AppendSetupLog(root, "Could not start GT Sport: " + e.Message);
                        return;
                    }
                    if (started == null) return;
                    using (Process game = started) game.WaitForExit();

                    if (recoveredOnce || !RendererReportedDeviceLoss(logDirectory)) return;
                    recoveredOnce = true;
                    AppendSetupLog(root, "Vulkan lost the GPU device. Waiting for Windows to restore the graphics device before restarting GT Sport.");
                    if (!WaitForGraphicsDevice(TimeSpan.FromMinutes(2)))
                    {
                        AppendSetupLog(root, "The graphics device did not recover within 2 minutes; GT Sport was not restarted.");
                        return;
                    }
                    AppendSetupLog(root, "The graphics device is responding again. Restarting GT Sport once; the interrupted session cannot be resumed.");
                }
            });
        }

        static bool RendererReportedDeviceLoss(string logDirectory)
        {
            string path = Path.Combine(logDirectory, "stdout.log");
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    int count = (int)Math.Min(stream.Length, 1024 * 1024);
                    stream.Seek(-count, SeekOrigin.End);
                    byte[] bytes = new byte[count];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int n = stream.Read(bytes, read, bytes.Length - read);
                        if (n == 0) break;
                        read += n;
                    }
                    string tail = Encoding.UTF8.GetString(bytes, 0, read);
                    return tail.IndexOf("ErrorDeviceLost", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           tail.IndexOf("Device lost", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        static bool WaitForGraphicsDevice(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                IntPtr device = IntPtr.Zero, context = IntPtr.Zero;
                try
                {
                    // A fresh hardware D3D11 device is a small probe that the Windows display
                    // driver has recovered. Driver type 1 is HARDWARE; SDK version is 7.
                    int featureLevel;
                    int result = Native.D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0,
                        IntPtr.Zero, 0, 7, out device, out featureLevel, out context);
                    if (result >= 0) return true;
                }
                catch { }
                finally
                {
                    if (context != IntPtr.Zero) Marshal.Release(context);
                    if (device != IntPtr.Zero) Marshal.Release(device);
                }
                Thread.Sleep(2000);
            }
            return false;
        }

        static void AppendSetupLog(string root, string line)
        {
            try { File.AppendAllText(Path.Combine(root, "setup.log"), line + Environment.NewLine); }
            catch { }
        }

        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // Portable: the tools' work folders (and anything else asking for a temporary folder) go
            // into the RyGT folder instead of AppData\Local\Temp; the tools delete theirs when done.
            try
            {
                string temp = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");
                Directory.CreateDirectory(temp);
                Environment.SetEnvironmentVariable("TMP", temp);
                Environment.SetEnvironmentVariable("TEMP", temp);
            }
            catch { }
            if (args.Contains("--setup")) return new LoaderForm().SetupWithoutWindow();
            if (args.Length == 2 && args[0] == "--prepare")
            {
                // Gives an already converted GTSport.exe the ryGT icon and the Windows subsystem.
                try { GameExe.Prepare(Path.GetFullPath(args[1])); return 0; }
                catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
            }
            if (args.Length == 2 && args[0] == "--snapshot")
            {
                // Renders the window's contents to a PNG (for checking the layout).
                using (var form = new LoaderForm())
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-4000, -4000);
                    form.ShowInTaskbar = false;
                    form.Show();
                    Application.DoEvents();
                    // The scrolled settings and the pinned bar under them.
                    using (var image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
                    {
                        foreach (Control control in form.Controls) control.DrawToBitmap(image, control.Bounds);
                        image.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                    }
                    form.Close();
                }
                return 0;
            }
            if (args.Length == 2 && args[0] == "--snapshot-previews")
            {
                // The controller preview's layouts side by side as a PNG, some controls pressed.
                var kinds = new[] { PadKind.DualShock4, PadKind.DualSense, PadKind.Xbox, PadKind.Generic };
                using (var image = new Bitmap(420 * kinds.Length, 290))
                {
                    for (int i = 0; i < kinds.Length; ++i)
                    {
                        using (var preview = new ControllerPreview
                        {
                            Kind = kinds[i], Connected = i != 3, Caption = kinds[i].ToString(), Size = new Size(420, 290),
                            Cross = true, R1 = true, PadLeft = true, LX = -0.8f, RY = 0.6f, L2 = 0.5f, R2 = 1f
                        })
                        using (var tile = new Bitmap(420, 290))
                        {
                            preview.DrawToBitmap(tile, new Rectangle(0, 0, 420, 290));
                            using (var g = Graphics.FromImage(image)) g.DrawImage(tile, 420 * i, 0);
                        }
                    }
                    image.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                }
                return 0;
            }
            if (args.Length == 2 && (args[0] == "--snapshot-ports" || args[0] == "--snapshot-bindings" || args[0] == "--snapshot-wheel"))
            {
                // The Controller ports, Button bindings or Steering wheel window as a PNG (nothing is saved).
                string root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
                using (Form form = args[0] == "--snapshot-ports"
                           ? (Form)new PortsForm("", null, (ports, names) => { })
                           : args[0] == "--snapshot-wheel"
                               ? (Form)new WheelForm(root)
                               : new ControlsForm(root, 1, player => { }))
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-4000, -4000);
                    form.ShowInTaskbar = false;
                    form.Show();
                    Application.DoEvents();
                    // The wheel window shows live state from its probe; give it a moment to arrive.
                    var settle = DateTime.Now.AddMilliseconds(args[0] == "--snapshot-wheel" ? 2000 : 200);
                    while (DateTime.Now < settle) { Application.DoEvents(); Thread.Sleep(20); }
                    using (var image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
                    {
                        form.DrawToBitmap(image, new Rectangle(Point.Empty, form.ClientSize));
                        image.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                    }
                    form.Close();
                }
                return 0;
            }
            Application.Run(new LoaderForm());
            return 0;
        }
    }
}
