using System;
using System.Threading;
using System.Windows.Forms;

namespace LiteMonitor
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            using var mutex = new Mutex(true, "LiteMonitor_SingleInstance", out bool first);
            if (!first) return;
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Log.Write(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write(e.ExceptionObject as Exception);
            ApplicationConfiguration.Initialize();
            bool tray = Array.IndexOf(args, "--tray") >= 0;
            try { Application.Run(new MainForm(tray)); }
            catch (Exception ex) { Log.Write(ex); MessageBox.Show("LiteMonitor hit a problem and has to close.\nDetails were saved to %APPDATA%\\LiteMonitor\\error.log\n\n" + ex.Message, "LiteMonitor"); }
        }
    }
}
