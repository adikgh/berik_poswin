using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Collections.Generic;
using System.Windows.Forms;

namespace BerikPOS
{
    // Жаңа нұсқаны GitHub Releases-тен тексеру: adikgh/berik_poswin, файл BerikPOS-Setup-x.y.z.exe
    static class Updater
    {
        const string Api = "https://api.github.com/repos/adikgh/berik_poswin/releases/latest";

        public static void CheckLater(Form owner, Func<Task> pushFirst)
        {
            var t = new Timer { Interval = 30000 };
            t.Tick += async (s, e) => { t.Stop(); await Check(owner, pushFirst); };
            t.Start();
        }

        static async Task Check(Form owner, Func<Task> pushFirst)
        {
            try
            {
                var rel = await Task.Run(() =>
                {
                    var wc = new WebClient(); wc.Headers["User-Agent"] = "BerikPOS"; wc.Encoding = System.Text.Encoding.UTF8;
                    return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(wc.DownloadString(Api));
                });
                string tag = (rel.Str("tag_name") ?? "").TrimStart('v');
                if (!Version.TryParse(tag, out var nv) || nv <= Version.Parse(Program.Version)) return;
                string url = null;
                if (rel["assets"] is System.Collections.ArrayList assets)
                    foreach (var a in assets) if (a is Dictionary<string, object> d && (d.Str("name") ?? "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) url = d.Str("browser_download_url");
                if (url == null) return;
                if (MessageBox.Show(owner, $"Жаңа нұсқа бар: {tag}\nҚазір жаңартамыз ба? Программа 1–2 минутқа жабылып, өзі қайта ашылады.\nЗаказдар өшпейді.",
                    "Berik POS жаңарту", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                string file = Path.Combine(Path.GetTempPath(), "BerikPOS-Setup-" + tag + ".exe");
                await Task.Run(() => { var wc = new WebClient(); wc.Headers["User-Agent"] = "BerikPOS"; wc.DownloadFile(url, file); });
                await pushFirst();
                Process.Start(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTAPP");
                owner.Close();
            }
            catch (Exception ex) { Log.W("update: " + ex.Message); }
        }
    }
}
