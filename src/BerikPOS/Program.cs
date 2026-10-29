using System;
using System.Threading;
using System.Windows.Forms;

namespace BerikPOS
{
    static class Program
    {
        public const string Version = "1.0.0";

        [STAThread]
        static void Main()
        {
            using (var m = new Mutex(true, "Global\\BerikPOS_single", out bool first))
            {
                if (!first) { MessageBox.Show("Berik POS бұрыннан ашық тұр.", "Berik POS", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072; // TLS 1.2 (Windows 7)
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => { Log.W("ui: " + e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.W("fatal: " + e.ExceptionObject);
                Application.Run(new MainForm());
            }
        }
    }
}
