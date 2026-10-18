// Read Aloud - highlight text in any app and press a shortcut to hear it.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Read Aloud")]
[assembly: System.Reflection.AssemblyProduct("Read Aloud")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

static class Program {
    [STAThread]
    static void Main() {
        bool created;
        using (var mutex = new Mutex(true, "Local\\ReadAloudHotkey", out created)) {
            if (!created) {
                MessageBox.Show("Read Aloud is already running.\nLook for the speaker icon in the system tray.", "Read Aloud");
                return;
            }
            Application.EnableVisualStyles();
            Application.Run(new ReadAloudApp());
        }
    }
}

// Wraps the Windows speech engine (SAPI). Includes the modern "OneCore" voices
// (George, Susan, Mark...) that System.Speech can't see.
public class Speaker {
    const int ASYNC = 1, PURGE = 2, NOT_XML = 16;
    readonly dynamic voice = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice"));
    bool paused;

    public class VoiceChoice { public string Name, Label; public object Token; }
    public readonly List<VoiceChoice> Voices = new List<VoiceChoice>();
    public int Current;

    public Speaker() {
        // Modern voices first; older "Desktop" versions of the same voice are skipped
        foreach (var category in new[] { @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech_OneCore\Voices",
                                         @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech\Voices" }) {
            try {
                dynamic cat = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpObjectTokenCategory"));
                cat.SetId(category, false);
                foreach (dynamic tok in cat.EnumerateTokens()) {
                    string name = ((string)tok.GetAttribute("Name")).Replace("Microsoft ", "").Replace(" Desktop", "");
                    if (Voices.Exists(v => v.Name == name)) continue;
                    string desc = tok.GetDescription();
                    string region = desc.Contains("United Kingdom") ? "UK" : desc.Contains("United States") ? "US"
                                  : desc.Contains("Australia") ? "AU" : desc.Contains("India") ? "IN" : "";
                    string gender = ((string)tok.GetAttribute("Gender")).ToLower();
                    string label = name + " (" + (region != "" ? region + ", " : "") + gender + ")";
                    Voices.Add(new VoiceChoice { Name = name, Label = label, Token = tok });
                }
            } catch { }
        }
    }

    public bool Busy { get { return paused || (int)voice.Status.RunningState == 2; } }

    public int Rate {
        get { return (int)voice.Rate; }
        set { voice.Rate = Math.Max(-10, Math.Min(10, value)); }
    }

    public void SelectVoice(int i) {
        if (i < 0 || i >= Voices.Count) return;
        Stop();
        voice.Voice = Voices[i].Token;
        Current = i;
    }

    public void Speak(string text) { paused = false; voice.Speak(text, ASYNC | PURGE | NOT_XML); }

    public void Stop() {
        if (paused) { voice.Resume(); paused = false; }
        voice.Speak("", ASYNC | PURGE);
    }

    public void PauseOrResume() {
        if (paused) { voice.Resume(); paused = false; }
        else if (Busy) { voice.Pause(); paused = true; }
    }
}

public class ReadAloudApp : Form {
    // Set by the launcher: the command Windows should run at sign-in, and where the log lives
    public static string StartupCommand;
    public static string LogFile;
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000, KEYUP = 2;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "ReadAloud";

    class Shortcut {
        public int Id; public Keys Key; public string Label; public Action Run;
    }

    const string SettingsKey = @"Software\ReadAloud";
    readonly Speaker synth = new Speaker();
    readonly NotifyIcon tray = new NotifyIcon();
    ToolStripMenuItem voiceMenu;
    Shortcut[] shortcuts;

    public ReadAloudApp() {
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        Opacity = 0;
        LoadSettings();

        shortcuts = new[] {
            new Shortcut { Id = 1, Key = Keys.R,    Label = "Read highlighted text / Stop", Run = ReadOrStop },
            new Shortcut { Id = 2, Key = Keys.P,    Label = "Pause / Resume",               Run = synth.PauseOrResume },
            new Shortcut { Id = 3, Key = Keys.Oemplus,  Label = "Speak faster",             Run = () => ChangeRate(+1) },
            new Shortcut { Id = 4, Key = Keys.OemMinus, Label = "Speak slower",             Run = () => ChangeRate(-1) },
            new Shortcut { Id = 5, Key = Keys.V,    Label = "Next voice",                   Run = NextVoice },
        };

        CreateHandle();
        string failed = "";
        foreach (var s in shortcuts)
            if (!RegisterHotKey(Handle, s.Id, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)s.Key))
                failed += "\n  " + KeyText(s.Key) + "  (" + s.Label + ")";

        tray.Icon = MakeIcon();
        tray.Text = "Read Aloud - Ctrl+Alt+R";
        tray.ContextMenuStrip = BuildMenu();
        tray.MouseClick += (o, e) => { if (e.Button == MouseButtons.Left) ShowBalloon(); };
        tray.Visible = true;

        if (failed != "")
            MessageBox.Show("These shortcuts are already used by another program and won't work:" + failed, "Read Aloud");
        else
            ShowBalloon();
    }

    static string KeyText(Keys k) {
        string name = k == Keys.Oemplus ? "=" : k == Keys.OemMinus ? "-" : k.ToString();
        return "Ctrl+Alt+" + name;
    }

    void ShowBalloon() {
        tray.ShowBalloonTip(4000, "Read Aloud is running",
            "Highlight text and press Ctrl+Alt+R.\nRight-click this icon to see all shortcuts.", ToolTipIcon.Info);
    }

    ContextMenuStrip BuildMenu() {
        var menu = new ContextMenuStrip();
        var header = new ToolStripMenuItem("Shortcut keys") { Enabled = false };
        header.Font = new Font(header.Font, FontStyle.Bold);
        menu.Items.Add(header);
        foreach (var s in shortcuts) {
            var sc = s;
            var item = new ToolStripMenuItem(sc.Label);
            item.ShortcutKeyDisplayString = KeyText(sc.Key);
            item.Click += (o, e) => { if (sc.Id != 1) sc.Run(); else if (synth.Busy) synth.Stop(); };
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());

        voiceMenu = new ToolStripMenuItem("Voice");
        for (int i = 0; i < synth.Voices.Count; i++) {
            int idx = i;
            voiceMenu.DropDownItems.Add(synth.Voices[i].Label, null, (o, e) => ChooseVoice(idx));
        }
        menu.Items.Add(voiceMenu);

        var startup = new ToolStripMenuItem("Run at startup") { CheckOnClick = true, Checked = IsStartupEnabled() };
        startup.Click += (o, e) => SetStartup(startup.Checked);
        menu.Items.Add(startup);

        var speed = new ToolStripMenuItem("Speed: normal");
        menu.Opening += (o, e) => {
            speed.Text = "Speed: " + (synth.Rate == 0 ? "normal" : (synth.Rate > 0 ? "+" : "") + synth.Rate);
            startup.Checked = IsStartupEnabled();
            voiceMenu.Text = "Voice: " + synth.Voices[synth.Current].Name;
            for (int i = 0; i < voiceMenu.DropDownItems.Count; i++)
                ((ToolStripMenuItem)voiceMenu.DropDownItems[i]).Checked = i == synth.Current;
        };
        speed.Enabled = false;
        menu.Items.Add(speed);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Read Aloud", null, (o, e) => Close());
        return menu;
    }

    static bool IsStartupEnabled() {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
            return key != null && key.GetValue(AppName) != null;
    }

    static void SetStartup(bool on) {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey)) {
            if (on) key.SetValue(AppName, StartupCommand ?? ("\"" + Application.ExecutablePath + "\""));
            else key.DeleteValue(AppName, false);
        }
    }

    protected override void WndProc(ref Message m) {
        if (m.Msg == WM_HOTKEY) {
            int id = m.WParam.ToInt32();
            foreach (var s in shortcuts)
                if (s.Id == id) { try { s.Run(); } catch (Exception ex) { Log(s.Label + " failed: " + ex); } }
        }
        base.WndProc(ref m);
    }

    void ChangeRate(int delta) {
        synth.Rate += delta;
        SaveSettings();
    }

    void ChooseVoice(int i) {
        synth.SelectVoice(i);
        SaveSettings();
        synth.Speak("Hello, I'm " + synth.Voices[i].Name + ".");
    }

    void NextVoice() {
        if (synth.Voices.Count > 0) ChooseVoice((synth.Current + 1) % synth.Voices.Count);
    }

    // Remembers the chosen voice and speed between sessions
    void LoadSettings() {
        using (var key = Registry.CurrentUser.OpenSubKey(SettingsKey)) {
            string name = key == null ? null : key.GetValue("Voice") as string;
            int idx = synth.Voices.FindIndex(v => v.Name == name);
            synth.SelectVoice(idx >= 0 ? idx : 0);
            if (key != null) synth.Rate = (int)key.GetValue("Rate", 0);
        }
    }

    void SaveSettings() {
        using (var key = Registry.CurrentUser.CreateSubKey(SettingsKey)) {
            key.SetValue("Voice", synth.Voices[synth.Current].Name);
            key.SetValue("Rate", synth.Rate);
        }
    }

    // Diagnostics: create an empty ReadAloud.log next to the exe to turn on logging
    static string LogPath { get { return LogFile ?? System.IO.Path.ChangeExtension(Application.ExecutablePath, ".log"); } }
    static void Log(string s) {
        try { if (System.IO.File.Exists(LogPath)) System.IO.File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff ") + s + Environment.NewLine); } catch { }
    }

    static bool IsHeld(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

    static void SendCtrlC() {
        keybd_event(0x11, 0, 0, UIntPtr.Zero);      // Ctrl down
        Thread.Sleep(30);
        keybd_event(0x43, 0, 0, UIntPtr.Zero);      // C down
        Thread.Sleep(30);
        keybd_event(0x43, 0, KEYUP, UIntPtr.Zero);  // C up
        Thread.Sleep(30);
        keybd_event(0x11, 0, KEYUP, UIntPtr.Zero);  // Ctrl up
    }

    void ReadOrStop() {
        if (synth.Busy) { synth.Stop(); return; }

        // Wait for Ctrl/Alt/Shift to be released so the copy isn't Ctrl+Alt+C
        for (int waited = 0; waited < 1500 && (IsHeld(0x11) || IsHeld(0x12) || IsHeld(0x10)); waited += 20)
            Thread.Sleep(20);

        string savedText = null;
        try { if (Clipboard.ContainsText()) savedText = Clipboard.GetText(); } catch { }

        uint seq = GetClipboardSequenceNumber();
        // Office sometimes ignores a copy that arrives too fast, so try up to 3 times, slowly
        for (int attempt = 0; attempt < 3 && GetClipboardSequenceNumber() == seq; attempt++) {
            SendCtrlC();
            for (int t = 0; t < 700 && GetClipboardSequenceNumber() == seq; t += 20)
                Thread.Sleep(20);
        }
        Log("hotkey: foreground copy " + (GetClipboardSequenceNumber() == seq ? "produced nothing" : "ok"));
        if (GetClipboardSequenceNumber() == seq) return; // nothing was selected

        string text = null;
        for (int i = 0; i < 10 && text == null; i++) {
            try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); } catch { }
            if (text == null) Thread.Sleep(50);
        }
        if (savedText != null)
            for (int i = 0; i < 5; i++) { try { Clipboard.SetText(savedText); break; } catch { Thread.Sleep(50); } }

        Log("speaking " + (text == null ? 0 : text.Length) + " chars");
        if (!string.IsNullOrWhiteSpace(text)) synth.Speak(text);
    }

    // Speaker icon drawn at runtime so the exe is fully self-contained
    public static Bitmap DrawSpeaker(int size) {
        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float u = size / 32f;
            using (var bg = new SolidBrush(Color.FromArgb(0, 120, 212)))
                g.FillEllipse(bg, 0, 0, size - 1, size - 1);
            var body = new PointF[] {
                new PointF(7*u, 12*u), new PointF(12*u, 12*u), new PointF(18*u, 7*u),
                new PointF(18*u, 25*u), new PointF(12*u, 20*u), new PointF(7*u, 20*u) };
            g.FillPolygon(Brushes.White, body);
            using (var pen = new Pen(Color.White, 2.2f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
                g.DrawArc(pen, 15*u, 11*u, 8*u, 10*u, -50, 100);
                g.DrawArc(pen, 14*u, 7*u, 13*u, 18*u, -50, 100);
            }
        }
        return bmp;
    }

    static Icon MakeIcon() {
        using (var bmp = DrawSpeaker(32)) return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void OnFormClosed(FormClosedEventArgs e) {
        foreach (var s in shortcuts) UnregisterHotKey(Handle, s.Id);
        tray.Visible = false;
        tray.Dispose();
        synth.Stop();
        base.OnFormClosed(e);
    }
}



