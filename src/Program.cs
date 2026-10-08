using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace OrclCM
{
    static class Program
    {
        const string MutexName = @"Local\OrclCM.SingleInstance";
        const string ShowEventName = @"Local\OrclCM.ShowWindow";

        [STAThread]
        static void Main(string[] args)
        {
            bool restarting = Array.Exists(args, a => string.Equals(a, Updater.RestartArgument, StringComparison.OrdinalIgnoreCase));
            using (var mutex = new Mutex(true, MutexName, out bool first))
            using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
            {
                if (restarting) Updater.SignalStarted();  // the new exe runs: the old version may now exit
                if (!first && restarting)  // started by an update: wait for the old version to exit
                {
                    int i = Array.FindIndex(args, a => string.Equals(a, Updater.RestartArgument, StringComparison.OrdinalIgnoreCase));
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int oldPid))
                        try { using (var old = System.Diagnostics.Process.GetProcessById(oldPid)) old.WaitForExit(60000); }
                        catch (Exception) { }  // already gone, or not ours to wait on
                    try { first = mutex.WaitOne(30000); }
                    catch (AbandonedMutexException) { first = true; }
                }
                if (!first)
                {
                    AllowSetForegroundWindow(-1);  // let the running copy bring its window to the front
                    showEvent.Set();               // already running: just bring its window up
                    return;
                }

                // An unexpected UI error must not kill the meter or pop up a dialog every tick: log it.
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => LogError(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => LogError(e.ExceptionObject as Exception);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                bool startHidden = Array.Exists(args, a => string.Equals(a, Autostart.TrayArgument, StringComparison.OrdinalIgnoreCase));
                string settingsPath = Settings.DefaultPath;
                if (!restarting)
                    try { Updater.CleanUp(Application.ExecutablePath); } catch { }
                var form = new MainForm(new Poller(NativeBattery.Read, TimeSpan.FromSeconds(2)),
                                        Settings.Load(settingsPath), settingsPath, startHidden);

                var listener = new Thread(() =>
                {
                    var handles = new WaitHandle[] { showEvent, closing };
                    while (WaitHandle.WaitAny(handles) == 0)
                    {
                        // during startup the window may not exist yet: wait for it instead of giving up
                        while (!form.IsHandleCreated && !form.IsDisposed && !closing.WaitOne(100)) { }
                        try { form.BeginInvoke((Action)form.ShowFromTray); }
                        catch (ObjectDisposedException) { break; }     // form already gone
                        catch (InvalidOperationException) { if (form.IsDisposed) break; }
                    }
                }) { IsBackground = true, Name = "show-listener" };
                listener.Start();

                Application.Run(form);
                closing.Set();
                GC.KeepAlive(mutex);
            }
        }

        static readonly ManualResetEvent closing = new ManualResetEvent(false);

        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);

        static void LogError(Exception e)
        {
            if (e == null) return;
            try
            {
                string path = Path.Combine(BatteryLog.Folder, "error.log");
                Directory.CreateDirectory(BatteryLog.Folder);
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Delete(path);  // keep it small
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + AppInfo.Version + "  " + e + "\r\n\r\n");
            }
            catch (Exception) { }
        }
    }
}
