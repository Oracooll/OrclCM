using System;
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
            using (var mutex = new Mutex(true, MutexName, out bool first))
            using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
            {
                if (!first)
                {
                    showEvent.Set();  // already running: just bring its window up
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                bool startHidden = Array.Exists(args, a => string.Equals(a, Autostart.TrayArgument, StringComparison.OrdinalIgnoreCase));
                string settingsPath = Settings.DefaultPath;
                var form = new MainForm(new Poller(NativeBattery.Read, TimeSpan.FromSeconds(2)),
                                        Settings.Load(settingsPath), settingsPath, startHidden);

                var listener = new Thread(() =>
                {
                    var handles = new WaitHandle[] { showEvent, closing };
                    while (WaitHandle.WaitAny(handles) == 0)
                    {
                        try { form.BeginInvoke((Action)form.ShowFromTray); }
                        catch (InvalidOperationException) { break; }  // form already gone
                    }
                }) { IsBackground = true, Name = "show-listener" };
                listener.Start();

                Application.Run(form);
                closing.Set();
                GC.KeepAlive(mutex);
            }
        }

        static readonly ManualResetEvent closing = new ManualResetEvent(false);
    }
}
