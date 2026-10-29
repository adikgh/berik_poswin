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

        // Чек принтері: программаның өз баптауы (Data\printer.txt). Бос болса — Windows-тың әдепкі принтері
        static string PrinterFile => Path.Combine(Paths.Data, "printer.txt");
        static string ChosenPrinter()
        {
            try { if (File.Exists(PrinterFile)) { var n = File.ReadAllText(PrinterFile).Trim(); if (n != "") return n; } } catch { }
            try { return new PrinterSettings().PrinterName; } catch { return ""; }
        }

        // Чек терезесі (order_print.php): көрінбейтін терезеде ашылады, window.print() ұсталып,
        // таңдалған принтерге диалогсыз шығарылады, сосын терезе өзі жабылады
        async void OnNewWindow(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            var def = e.GetDeferral();
            try
            {
                bool isPrint = e.Uri.Contains("print");
                var f = new Form { Text = "Berik POS", ShowInTaskbar = !isPrint, StartPosition = FormStartPosition.Manual, Size = new Size(600, 700), Location = isPrint ? new Point(-3000, -3000) : new Point(Left + 100, Top + 60) };
                var wv = new WebView2 { Dock = DockStyle.Fill };
                f.Controls.Add(wv);
                f.Show(this);
                await wv.EnsureCoreWebView2Async(env);
                var cw = wv.CoreWebView2;
                cw.WindowCloseRequested += (s, a) => f.Close();
                cw.NewWindowRequested += OnNewWindow;
                if (isPrint)
                {
                    await cw.AddScriptToExecuteOnDocumentCreatedAsync("window.__berikPrint=window.print;window.print=function(){try{window.chrome.webview.postMessage('print')}catch(e){window.__berikPrint()}};");
                    bool done = false;
                    cw.WebMessageReceived += async (s, a) =>
                    {
                        if (done || a.TryGetWebMessageAsString() != "print") return;
                        done = true;
                        await PrintSilent(cw);
                        await Task.Delay(500);
                        if (!f.IsDisposed) f.Close();
                    };
                    var t = new Timer { Interval = 30000 }; t.Tick += (s, a) => { t.Stop(); if (!f.IsDisposed) f.Close(); }; t.Start();
                }
                e.NewWindow = cw;
                e.Handled = true;
            }
            catch (Exception ex) { Log.W("newwindow: " + ex.Message); }
            finally { def.Complete(); }
        }

        async Task PrintSilent(CoreWebView2 cw)
        {
            string printer = ChosenPrinter();
            try
            {
                // Windows 10/11 (жаңа WebView2): нақты принтерге, диалогсыз
                var ps = env.CreatePrintSettings();
                if (!string.IsNullOrEmpty(printer)) ps.PrinterName = printer;
                ps.ShouldPrintHeaderAndFooter = false;
                ps.ShouldPrintBackgrounds = true;
                ps.MarginTop = ps.MarginBottom = ps.MarginLeft = ps.MarginRight = 0;
                var st = await cw.PrintAsync(ps);
                Log.W($"print: {printer} -> {st}");
                if (st == CoreWebView2PrintStatus.Succeeded) return;
                if (st == CoreWebView2PrintStatus.PrinterUnavailable)
                    MessageBox.Show(this, "Принтер табылмады: " + printer + "\n«Принтер» батырмасынан чек принтерін таңдаңыз.", "Berik POS", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else
                    MessageBox.Show(this, "Чек басылмады (" + st + "). Принтер қосулы ма, қағазы бар ма тексеріңіз.", "Berik POS", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                // Ескі WebView2 (Windows 7/8): --kiosk-printing арқылы әдепкі принтерге
                Log.W("print fallback: " + ex.Message);
                try { await cw.ExecuteScriptAsync("window.__berikPrint()"); } catch { }
                await Task.Delay(3000);
            }
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
            string printer = ChosenPrinter();
            lblInfo.Text = $"Жүктелмеген заказ: {st.Int("pending")}   ·   Соңғы жүктеу: {st.Str("last_push") ?? "—"}   ·   Принтер: {(string.IsNullOrEmpty(printer) ? "таңдалмаған" : printer)}   ·   v{Program.Version}";
        }

        void OpenPrinters()
        {
            using (var f = new Form { Text = "Чек принтері", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(420, 170), Font = new Font("Segoe UI", 10f) })
            {
                var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = 20, Top = 45, Width = 380 };
                foreach (string p in PrinterSettings.InstalledPrinters) cb.Items.Add(p);
                cb.SelectedItem = ChosenPrinter();
                var ok = new Button { Text = "Сақтау", Left = 20, Top = 100, Width = 120, Height = 34, DialogResult = DialogResult.OK };
                var test = new Button { Text = "Сынақ чек", Left = 150, Top = 100, Width = 120, Height = 34 };
                var win = new Button { Text = "Windows...", Left = 280, Top = 100, Width = 120, Height = 34 };
                test.Click += async (s, e) => { Save(cb); await TestPrint(); };
                win.Click += (s, e) => { try { System.Diagnostics.Process.Start("control", "printers"); } catch { } };
                f.Controls.Add(new Label { Text = "Чек қай принтерге шықсын:", Left = 20, Top = 15, AutoSize = true });
                f.Controls.AddRange(new Control[] { cb, ok, test, win });
                f.AcceptButton = ok;
                if (f.ShowDialog(this) == DialogResult.OK) Save(cb);
            }
            _ = Refresh2();
        }

        static void Save(ComboBox cb)
        {
            try { Directory.CreateDirectory(Paths.Data); File.WriteAllText(PrinterFile, cb.SelectedItem as string ?? ""); } catch { }
        }

        async Task TestPrint()
        {
            var f = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000), Size = new Size(400, 400) };
            var wv = new WebView2 { Dock = DockStyle.Fill }; f.Controls.Add(wv); f.Show(this);
            try
            {
                await wv.EnsureCoreWebView2Async(env);
                var tcs = new TaskCompletionSource<bool>();
                wv.CoreWebView2.NavigationCompleted += (s, e) => tcs.TrySetResult(true);
                wv.CoreWebView2.NavigateToString("<html><body style='font-family:sans-serif;width:70mm;margin:0'><h3 style='text-align:center'>Berik POS</h3><p style='text-align:center'>Сынақ чек<br>" + DateTime.Now.ToString("dd.MM.yyyy HH:mm") + "</p><p style='text-align:center'>Принтер: " + System.Net.WebUtility.HtmlEncode(ChosenPrinter()) + "</p></body></html>");
                await tcs.Task;
                await PrintSilent(wv.CoreWebView2);
            }
            finally { await Task.Delay(500); f.Close(); }
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
