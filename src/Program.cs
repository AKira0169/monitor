// MonitorSwitch - tiny tray app to view monitors and switch their input over DDC/CI.
// Build with build.cmd (uses the C# 5 compiler that ships with .NET Framework 4.x).
// Settings: %APPDATA%\MonitorSwitch\settings.ini   Log: %APPDATA%\MonitorSwitch\log.txt
using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool created;
        using (var mutex = new Mutex(true, "MonitorSwitch.SingleInstance", out created))
        {
            if (!created)
            {
                // Already running: ask it to show its window.
                Native.PostMessage((IntPtr)Native.HWND_BROADCAST, Native.WM_SHOWME, IntPtr.Zero, IntPtr.Zero);
                return;
            }

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Log.Error("UI thread", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("Fatal", e.ExceptionObject as Exception);

            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (new TrayApp(args.Contains("/hidden", StringComparer.OrdinalIgnoreCase)))
                Application.Run();
        }
    }
}
