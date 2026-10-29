using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace BerikPOS
{
    class MainForm : Form
    {
        readonly Engine engine = new Engine();
        Agent agent;
        CoreWebView2Environment env;
        readonly WebView2 web = new WebView2 { Dock = DockStyle.Fill, Visible = false };
        readonly Label splash = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 14f), Text = "Berik POS іске қосылуда..." };
        readonly Panel bar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Color.FromArgb(245, 246, 248), Visible = false };
        readonly Label lblNet = new Label { AutoSize = false, Width = 120, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0), Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
        readonly Label lblInfo = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 9.5f), ForeColor = Color.FromArgb(70, 70, 70) };
        readonly Timer timer = new Timer { Interval = 60000 };
        bool syncing, closing;

        public MainForm()
        {
            Text = "Berik POS";
            WindowState = FormWindowState.Maximized;
            MinimumSize = new Size(800, 600);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Button B(string t, EventHandler h) { var b = new Button { Text = t, Dock = DockStyle.Right, Width = 150, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9.5f) }; b.FlatAppearance.BorderSize = 0; b.Click += h; return b; }
            bar.Controls.Add(lblInfo);
            bar.Controls.Add(lblNet);
            bar.Controls.Add(B("Принтер", (s, e) => OpenPrinters()));
            bar.Controls.Add(B("Менюді жаңарту", async (s, e) => await Sync("menu", true)));
            bar.Controls.Add(B("Серверге жүктеу", async (s, e) => await Sync("push", true)));
            Controls.Add(web);
            Controls.Add(bar);
            Controls.Add(splash);

            timer.Tick += async (s, e) => await Sync("auto", false);
            NetworkChange.NetworkAvailabilityChanged += (s, e) => { if (e.IsAvailable) BeginInvoke(new Action(async () => { await Task.Delay(5000); await Sync("auto", false); })); };
            Shown += async (s, e) => await Boot();
            FormClosing += OnClosing;
        }

        async Task Boot()
        {
            try
            {
                await Task.Run(() => engine.Start(t => BeginInvoke(new Action(() => splash.Text = t))));
                agent = new Agent(engine.AgentPort);

                var st = await Task.Run(() => agent.Call("status"));
                if (!st.Bool("linked"))
                {
                    using (var f = new SetupForm(agent)) if (f.ShowDialog(this) != DialogResult.OK) { Close(); return; }
                }

                splash.Text = "Терезе ашылуда...";
                string fixedRt = Path.Combine(Paths.App, "webview2"); // Windows 7/8 үшін ішіне салынған runtime
                var opt = new CoreWebView2EnvironmentOptions("--kiosk-printing --disable-features=msSmartScreenProtection");
                env = await CoreWebView2Environment.CreateAsync(Directory.Exists(fixedRt) && !HasEvergreen() ? fixedRt : null, Paths.WebView, opt);
                await web.EnsureCoreWebView2Async(env);
                var cw = web.CoreWebView2;
                cw.Settings.IsStatusBarEnabled = false;
                cw.Settings.AreDevToolsEnabled = false;
                cw.Settings.IsZoomControlEnabled = true;
                cw.NewWindowRequested += OnNewWindow;
                cw.Navigate($"http://127.0.0.1:{engine.SitePort}/");
                splash.Visible = false; web.Visible = true; bar.Visible = true;
                await Refresh2();
                timer.Start();
                await Sync("auto", false);
                Updater.CheckLater(this, () => Sync("push", false));
            }
            catch (Exception ex)
            {
                Log.W("boot: " + ex);
                string hint = ex is WebView2RuntimeNotFoundException ? "\n\nWebView2 runtime табылмады. Программаны қайта орнатыңыз." : "";
                MessageBox.Show(this, "Программа іске қосылмады:\n" + ex.Message + hint + "\n\nЛог: " + Paths.Logs, "Berik POS", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        static bool HasEvergreen()
        {
            try { return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()); } catch { return false; }
        }

        // Чек терезесі (order_print.php): көрінбейтін терезеде ашылып, принтерге тікелей шығып, өзі жабылады
        async void OnNewWindow(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            var def = e.GetDeferral();
            try
            {
                bool isPrint = e.Uri.Contains("print");
                var f = new Form { Text = "Berik POS", ShowInTaskbar = !isPrint, StartPosition = FormStartPosition.Manual, Size = new Size(600, 700), Location = isPrint ? new Point(-3000, -3000) : new Point(Left + 100, Top + 60) };
                if (isPrint) f.Opacity = 0;
                var wv = new WebView2 { Dock = DockStyle.Fill };
                f.Controls.Add(wv);
                f.Show(this);
                await wv.EnsureCoreWebView2Async(env);
                wv.CoreWebView2.WindowCloseRequested += (s, a) => f.Close();
                wv.CoreWebView2.NewWindowRequested += OnNewWindow;
                if (isPrint)
                {
                    await wv.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("window.addEventListener('afterprint',function(){setTimeout(function(){window.close()},300)});");
                    var t = new Timer { Interval = 20000 }; t.Tick += (s, a) => { t.Stop(); if (!f.IsDisposed) f.Close(); }; t.Start();
                }
                e.NewWindow = wv.CoreWebView2;
                e.Handled = true;
            }
            catch (Exception ex) { Log.W("newwindow: " + ex.Message); }
            finally { def.Complete(); }
        }

        async Task Sync(string action, bool manual)
        {
            if (agent == null || syncing) { if (manual && syncing) MessageBox.Show(this, "Синхрон жүріп жатыр, біраз күтіңіз.", "Berik POS"); return; }
            syncing = true;
            if (manual) { lblInfo.Text = action == "menu" ? "Меню жаңартылуда..." : "Серверге жүктелуде..."; UseWaitCursor = true; }
            try
            {
                var r = await Task.Run(() => agent.Call(action));
                string err = r.Str("error");
                if (action == "auto") SetNet(err != "weak_net" && err != "no_connection" && r.Bool("ok"));
                if (manual)
                {
                    if (r.Bool("ok"))
                    {
                        SetNet(true);
                        string msg = action == "menu" ? "Меню жаңартылды." : $"Жүктелді: {r.Int("sent")} заказ, {r.Int("shifts")} смена.";
                        MessageBox.Show(this, msg, "Berik POS", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        if (action == "menu") web.CoreWebView2?.Reload();
                    }
                    else
                    {
                        if (err == "no_connection" || err == "agent") SetNet(false);
                        MessageBox.Show(this, "Орындалмады: " + Human(err) + (r.Str("detail") != null ? "\n" + r.Str("detail") : ""), "Berik POS", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
            catch (Exception ex) { Log.W("sync: " + ex.Message); }
            finally { syncing = false; UseWaitCursor = false; await Refresh2(); }
        }

        public static string Human(string e)
        {
            switch (e)
            {
                case "no_connection": return "интернет жоқ немесе сервер жауап бермейді";
                case "weak_net": return "интернет әлсіз";
                case "bad_code": return "компания коды қате";
                case "bad_input": return "компания кодын енгізіңіз";
                case "no_branch": case "branch_not_found": return "компанияда точка табылмады";
                case "bad_token": case "no_token": return "касса серверде өшірілген, қайта байлау керек";
                case "too_many": return "тым көп қате әрекет, 10 минуттан кейін қайталаңыз";
                case "busy": return "синхрон жүріп жатыр";
                default: return e ?? "белгісіз қате";
            }
        }

        void SetNet(bool online)
        {
            lblNet.Text = online ? "● Онлайн" : "● Офлайн";
            lblNet.ForeColor = online ? Color.FromArgb(22, 150, 70) : Color.FromArgb(200, 60, 40);
        }

        async Task Refresh2()
        {
            if (agent == null) return;
            var st = await Task.Run(() => agent.Call("status", null, 15000));
            if (string.IsNullOrEmpty(lblNet.Text)) SetNet(false);
            string printer = "";
            try { printer = new PrinterSettings().PrinterName; } catch { }
            lblInfo.Text = $"Жүктелмеген заказ: {st.Int("pending")}   ·   Соңғы жүктеу: {st.Str("last_push") ?? "—"}   ·   Принтер: {(string.IsNullOrEmpty(printer) ? "таңдалмаған" : printer)}   ·   v{Program.Version}";
        }

        void OpenPrinters()
        {
            MessageBox.Show(this, "Чек Windows-тағы «әдепкі» (по умолчанию) принтерге шығады.\nАшылған терезеде чек принтерін тінтуірдің оң жағымен басып, «Использовать по умолчанию» таңдаңыз.", "Принтер", MessageBoxButtons.OK, MessageBoxIcon.Information);
            try { System.Diagnostics.Process.Start("control", "printers"); } catch { }
        }

        async void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (closing) return;
            closing = true;
            e.Cancel = true;
            timer.Stop();
            Hide();
            // Жабар алдында интернет болса, соңғы жазбаларды жүктеп көреміз (ең көбі 20 секунд)
            if (agent != null) { var t = Task.Run(() => agent.Call("auto", null, 20000)); await Task.WhenAny(t, Task.Delay(20000)); }
            await Task.Run(() => engine.Dispose());
            Application.Exit();
        }
    }
}
