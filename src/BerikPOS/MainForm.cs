using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
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
        readonly Label lblNet = new Label { AutoSize = false, Width = 250, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0), Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
        readonly Label lblInfo = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 9.5f), ForeColor = Color.FromArgb(70, 70, 70) };
        readonly Timer timer = new Timer { Interval = 60000 };
        readonly Timer pinger = new Timer { Interval = 15000 };
        Button btnMode;
        bool syncing, closing;

        // Режим: интернет бар — серверлік кассаның өзі (барлық функция: жеткізу, клиенттер, кэшбэк, QR заказдар,
        // Telegram, Kaspi Pay); интернет жоқ — жергілікті касса (меню, заказдар, қызметкерлер, баптаулар көшірілген).
        bool online, forceOffline, netOk;
        int pingFail, pingOk;
        string server, companyId;

        public MainForm()
        {
            Text = "Berik POS";
            WindowState = FormWindowState.Maximized;
            MinimumSize = new Size(800, 600);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Button B(string t, EventHandler h) { var b = new Button { Text = t, Dock = DockStyle.Right, Width = 150, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9.5f) }; b.FlatAppearance.BorderSize = 0; b.Click += h; return b; }
            bar.Controls.Add(lblInfo);
            bar.Controls.Add(lblNet);
            bar.Controls.Add(B("Сервер", async (s, e) => await ChangeServer()));
            btnMode = B("Офлайнға өту", async (s, e) => await ToggleMode());
            bar.Controls.Add(btnMode);
            bar.Controls.Add(B("Принтер", (s, e) => OpenPrinters()));
            bar.Controls.Add(B("Менюді жаңарту", async (s, e) => await Sync("menu", true)));
            bar.Controls.Add(B("Серверге жүктеу", async (s, e) => await Sync("push", true)));
            Controls.Add(web);
            Controls.Add(bar);
            Controls.Add(splash);

            timer.Tick += async (s, e) => await Sync("auto", false);
            pinger.Tick += async (s, e) => await Watch();
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
                cw.NavigationCompleted += OnNavDone;
                st = await Task.Run(() => agent.Call("status", null, 15000));
                server = (st.Str("server") ?? "").TrimEnd('/');
                companyId = st.Str("company_id");
                splash.Text = "Интернет тексерілуде...";
                if (await Ping())
                {
                    // Алдымен офлайн жазбалар серверге жетеді, сосын серверлік касса ашылады
                    await Sync("auto", false);
                    GoOnline();
                }
                else { netOk = false; GoOffline(null); }
                splash.Visible = false; web.Visible = true; bar.Visible = true;
                await Refresh2();
                timer.Start(); pinger.Start();
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
            // «Жаңа касса ашу» / «+» (/cashbox/?cart=new): сайтта жаңа вкладкада ашылады; программада жаңа терезе
            // ашпаймыз (терезелер үсті-үстіне жиналмасын) — сол терезеде жаңа кассаға ауысады (user 2026-11-18)
            if (!e.Uri.Contains("print") && e.Uri.Contains("/cashbox/") && sender is CoreWebView2 own)
            {
                e.Handled = true;
                own.Navigate(e.Uri);
                return;
            }
            var def = e.GetDeferral();
            try
            {
                bool isPrint = e.Uri.Contains("print");
                var f = new Form { Text = "Berik POS", ShowInTaskbar = !isPrint, StartPosition = FormStartPosition.Manual, Size = new Size(600, 700), Location = isPrint ? new Point(-3000, -3000) : new Point(Left + 100, Top + 60) };
                if (isPrint) { f.FormBorderStyle = FormBorderStyle.None; f.Opacity = 0; } // чек терезесі көрінбейді
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
            // Принтер әлі таңдалмаса — бірінші рет таңдатамыз
            if (!File.Exists(PrinterFile)) OpenPrinters();
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
                        if (action == "menu" && !online) web.CoreWebView2?.Reload();
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

        string LocalUrl => $"http://127.0.0.1:{engine.SitePort}/";

        void GoOnline()
        {
            online = true; pingFail = 0; netOk = true;
            web.CoreWebView2?.Navigate(server + "/" + (string.IsNullOrEmpty(companyId) ? "" : "?c=" + companyId));
            Log.W("mode: online");
            ShowMode();
        }

        void GoOffline(string why)
        {
            online = false; pingOk = 0;
            web.CoreWebView2?.Navigate(LocalUrl);
            Log.W("mode: offline " + why);
            ShowMode();
            if (why != null) lblInfo.Text = why;
        }

        void ShowMode()
        {
            if (online) { lblNet.Text = "● Онлайн · толық касса"; lblNet.ForeColor = Color.FromArgb(22, 150, 70); }
            else if (netOk) { lblNet.Text = "● Офлайн касса · интернет бар"; lblNet.ForeColor = Color.FromArgb(200, 130, 20); }
            else { lblNet.Text = "● Офлайн касса · интернет жоқ"; lblNet.ForeColor = Color.FromArgb(200, 60, 40); }
            if (btnMode != null) btnMode.Text = online ? "Офлайнға өту" : "Онлайнға өту";
        }

        // Сервер жауап бере ме (5 секунд)
        async Task<bool> Ping()
        {
            if (string.IsNullOrEmpty(server)) return false;
            return await Task.Run(() =>
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(server + "/api/sync/ping.php?t=" + DateTime.Now.Ticks);
                    req.Timeout = 5000; req.ReadWriteTimeout = 5000;
                    using (var r = (HttpWebResponse)req.GetResponse()) return r.StatusCode == HttpStatusCode.OK;
                }
                catch { return false; }
            });
        }

        // 15 секунд сайын: онлайнда интернет үзілсе — жергілікті кассаға; офлайнда интернет қайта келсе —
        // жазбаларды жүктеп, кассир бос тұрғанда (себет бос) онлайнға қайтады
        async Task Watch()
        {
            if (agent == null || closing || forceOffline) return;
            bool ok = await Ping();
            netOk = ok;
            if (online)
            {
                if (ok) { pingFail = 0; return; }
                if (++pingFail >= 2) GoOffline("Интернет үзілді: жергілікті кассаға ауыстық. Заказдар сақталады, интернет келгенде серверге өзі жүктеледі.");
                return;
            }
            ShowMode();
            if (!ok) { pingOk = 0; return; }
            if (++pingOk < 2 || IdleSeconds() < 20 || syncing) return;
            var st = await Task.Run(() => agent.Call("status", null, 15000));
            if (st.Int("drafts") > 0) return; // кассир себет жинап жатыр — бөлмейміз
            await Sync("auto", false);
            st = await Task.Run(() => agent.Call("status", null, 15000));
            if (st.Int("pending") == 0 && !online && !forceOffline) GoOnline();
        }

        async Task ToggleMode()
        {
            if (online) { forceOffline = true; GoOffline("Офлайн режим қолмен қосылды. Онлайнға қайту үшін «Онлайнға өту» басыңыз."); return; }
            forceOffline = false;
            if (!await Ping()) { netOk = false; ShowMode(); MessageBox.Show(this, "Сервер жауап бермейді, интернетті тексеріңіз. Интернет келгенде касса өзі онлайнға ауысады.", "Berik POS", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            var st = await Task.Run(() => agent.Call("status", null, 15000));
            if (st.Int("drafts") > 0 && MessageBox.Show(this, "Жергілікті кассада рәсімделмеген себет бар. Онлайнға өтсеңіз, ол жергілікті кассада қалады. Өтесіз бе?", "Berik POS", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            await Sync("auto", false);
            GoOnline();
        }

        // Беттің өзі ашылмаса (интернет жоқ) — бірден жергілікті кассаға
        void OnNavDone(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!online || e.IsSuccess) return;
            switch (e.WebErrorStatus)
            {
                case CoreWebView2WebErrorStatus.CannotConnect:
                case CoreWebView2WebErrorStatus.ConnectionAborted:
                case CoreWebView2WebErrorStatus.ConnectionReset:
                case CoreWebView2WebErrorStatus.Disconnected:
                case CoreWebView2WebErrorStatus.HostNameNotResolved:
                case CoreWebView2WebErrorStatus.ServerUnreachable:
                case CoreWebView2WebErrorStatus.Timeout:
                    netOk = false;
                    GoOffline("Сервер ашылмады: жергілікті кассаға ауыстық.");
                    break;
            }
        }

        [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
        static int IdleSeconds()
        {
            var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            if (!GetLastInputInfo(ref li)) return 0;
            return (int)((uint)Environment.TickCount - li.dwTime) / 1000;
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

        void SetNet(bool ok)
        {
            netOk = ok;
            ShowMode();
        }

        async Task Refresh2()
        {
            if (agent == null) return;
            var st = await Task.Run(() => agent.Call("status", null, 15000));
            if (string.IsNullOrEmpty(lblNet.Text)) ShowMode();
            string printer = ChosenPrinter();
            lblInfo.Text = $"Сервер: {st.Str("server")}   ·   Жүктелмеген заказ: {st.Int("pending")}   ·   Соңғы жүктеу: {st.Str("last_push") ?? "—"}   ·   Заказдар көшірілді: {st.Str("last_state") ?? "—"}   ·   Принтер: {(string.IsNullOrEmpty(printer) ? "таңдалмаған" : printer)}   ·   v{Program.Version}";
        }

        // Орнатқаннан кейін де серверді ауыстыруға болады (мысалы, локал сервер → интернеттегі сервер)
        async Task ChangeServer()
        {
            if (agent == null) return;
            if (syncing) { MessageBox.Show(this, "Синхрон жүріп жатыр, біраз күтіңіз.", "Berik POS"); return; }
            var st = await Task.Run(() => agent.Call("status", null, 15000));
            string cur = st.Str("server") ?? "";
            int pending = st.Int("pending");
            string q = "Қазіргі сервер: " + cur + "\n\n";
            if (pending > 0) q += "Жүктелмеген заказ: " + pending + ". Олар жаңа серверге жүктеледі. Ескі серверге керек болса, алдымен «Серверге жүктеу» басыңыз.\n\n";
            q += "Серверді ауыстырасыз ба? Меню жаңа серверден қайта жүктеледі, жергілікті заказдар өшпейді.";
            if (MessageBox.Show(this, q, "Серверді ауыстыру", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            timer.Stop(); pinger.Stop();
            try
            {
                using (var f = new SetupForm(agent, cur))
                    if (f.ShowDialog(this) == DialogResult.OK)
                    {
                        var ns = await Task.Run(() => agent.Call("status", null, 15000));
                        server = (ns.Str("server") ?? "").TrimEnd('/'); companyId = ns.Str("company_id");
                        if (online) GoOnline(); else web.CoreWebView2?.Reload();
                    }
            }
            finally { timer.Start(); pinger.Start(); await Refresh2(); }
        }

        void OpenPrinters()
        {
            using (var f = new Form { Text = "Чек принтері", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(460, 170), Font = new Font("Segoe UI", 10f) })
            {
                var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = 20, Top = 45, Width = 380 };
                foreach (string p in PrinterSettings.InstalledPrinters) cb.Items.Add(p);
                cb.SelectedItem = ChosenPrinter();
                var ok = new Button { Text = "Сақтау", Left = 20, Top = 100, Width = 120, Height = 34, DialogResult = DialogResult.OK };
                var test = new Button { Text = "Сынақ чек", Left = 150, Top = 100, Width = 120, Height = 34 };
                var win = new Button { Text = "Windows...", Left = 280, Top = 100, Width = 120, Height = 34 };
                test.Click += async (s, e) => { Save(cb); await TestPrint(); };
                win.Click += (s, e) => { try { System.Diagnostics.Process.Start("control", "printers"); } catch { } };
                f.Controls.Add(new Label { Text = "Чек қай принтерге шықсын (печать басқанда бірден шығады):", Left = 20, Top = 15, AutoSize = true });
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
            timer.Stop(); pinger.Stop();
            Hide();
            // Жабар алдында интернет болса, соңғы жазбаларды жүктеп көреміз (ең көбі 20 секунд)
            if (agent != null) { var t = Task.Run(() => agent.Call("auto", null, 20000)); await Task.WhenAny(t, Task.Delay(20000)); }
            await Task.Run(() => engine.Dispose());
            Application.Exit();
        }
    }
}
