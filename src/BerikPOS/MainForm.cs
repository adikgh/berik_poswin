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
        // Серверден жаңа заказдар (QR/сайт заказдары, курьер статустары) 20 секунд сайын тартылады,
        // кассадағы «Сайттан жаңа тапсырыс» хабарламасы (notify.js) жергілікті базадан көреді
        readonly Timer news = new Timer { Interval = 20000 };
        bool syncing, closing, netUp;

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
            bar.Controls.Add(B("Принтер", (s, e) => OpenPrinters()));
            bar.Controls.Add(B("Менюді жаңарту", async (s, e) => await Sync("menu", true)));
            bar.Controls.Add(B("Серверге жүктеу", async (s, e) => await Sync("push", true)));
            Controls.Add(web);
            Controls.Add(bar);
            Controls.Add(splash);

            timer.Tick += async (s, e) => await Sync("auto", false);
            news.Tick += async (s, e) => await News();
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
                await cw.AddScriptToExecuteOnDocumentCreatedAsync(BarJs);
                cw.WebMessageReceived += OnBarCmd;
                cw.NavigationCompleted += (s, a) => PushBar();
                cw.Navigate($"http://127.0.0.1:{engine.SitePort}/");
                splash.Visible = false; web.Visible = true; // жоғарғы жолақ жоқ: күйі мен батырмалары кассаның өз мәзірінде (BarJs)
                await Refresh2();
                timer.Start(); news.Start();
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

        // Кухня чегінің принтері (Data\printer_kc.txt). Бос болса — чек принтерімен бірдей
        static string KitchenFile => Path.Combine(Paths.Data, "printer_kc.txt");
        static string KitchenPrinter()
        {
            try { if (File.Exists(KitchenFile)) { var n = File.ReadAllText(KitchenFile).Trim(); if (n != "") return n; } } catch { }
            return ChosenPrinter();
        }

        // Басып шығару баптаулары (Data\print.txt, key=value): қағаз, шеттері, масштаб, көшірме, бағдар, түс, колонтитул.
        // Браузердің «Печать» терезесіндегі баптаулар сияқты, бірақ бір рет сақталады да, чек диалогсыз шығады.
        static string OptsFile => Path.Combine(Paths.Data, "print.txt");
        static Dictionary<string, string> Opts()
        {
            var d = new Dictionary<string, string> { ["paper"] = "printer", ["margin"] = "0", ["scale"] = "100", ["copies"] = "1", ["orient"] = "portrait", ["color"] = "color", ["header"] = "0", ["rw"] = "0", ["len"] = "0" };
            try
            {
                if (File.Exists(OptsFile))
                    foreach (var l in File.ReadAllLines(OptsFile)) { int i = l.IndexOf('='); if (i > 0) d[l.Substring(0, i).Trim()] = l.Substring(i + 1).Trim(); }
            }
            catch { }
            return d;
        }
        static int OptInt(Dictionary<string, string> o, string k, int def, int min, int max)
        {
            int v; if (!int.TryParse(o.TryGetValue(k, out var s) ? s : "", out v)) v = def;
            return Math.Max(min, Math.Min(max, v));
        }
        static void ApplyOpts(CoreWebView2PrintSettings ps, double contentIn = 0)
        {
            var o = Opts();
            ps.ShouldPrintBackgrounds = true;
            ps.ShouldPrintHeaderAndFooter = o["header"] == "1";
            double m = OptInt(o, "margin", 0, 0, 30) / 25.4; // мм → дюйм
            ps.MarginTop = ps.MarginBottom = ps.MarginLeft = ps.MarginRight = m;
            // Әдепкі мәннен өзгергенін ғана береміз: кейбір чек принтерінің драйвері түс/көшірме баптауын қабылдамай, чек шықпай қалуы мүмкін
            int sc = OptInt(o, "scale", 100, 30, 200);
            if (sc != 100) ps.ScaleFactor = sc / 100.0;
            if (o["orient"] == "landscape") ps.Orientation = CoreWebView2PrintOrientation.Landscape;
            // Чек лентасы: беттің биіктігі = чектің өз биіктігі, сонда чек бір тұтас кесінді болып шығады
            // (драйвердің қысқа қағаз өлшемі, мысалы 80×80, чекті бірнеше бөлікке бөліп, арасына бос орын қалдыратын, user 2026-11-25).
            int rw0 = OptInt(o, "rw", 0, 0, 210);
            double rollW = (o["paper"] == "58" || (o["paper"] == "printer" && rw0 > 0 && rw0 <= 58) ? 58 : 80) / 25.4;
            // Лента ұзындығы: «len» мм берілсе — соны (мысалы 270/300), 0 болса — чектің өз ұзындығы (авто)
            int len = OptInt(o, "len", 0, 0, 3000);
            double h = len >= 30 ? len / 25.4
                : contentIn > 0 ? Math.Max(2.0, Math.Min(120.0, contentIn * sc / 100.0 + 2 * m + 0.4)) : 297 / 25.4;
            switch (o["paper"])
            {
                case "a4": ps.PageWidth = 210 / 25.4; ps.PageHeight = 297 / 25.4; break;
                case "fixed": break; // принтер драйверінің өз қағазы (бұрынғыдай)
                default: ps.PageWidth = rollW; ps.PageHeight = h; break; // printer, 58, 80
            }
            try
            {
                int cp = OptInt(o, "copies", 1, 1, 9);
                if (cp > 1) ps.Copies = cp;
                if (o["color"] == "gray") ps.ColorMode = CoreWebView2PrintColorMode.Grayscale;
            }
            catch { }
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
            // Чек (order_print.php): терезені сайтқа байламай, өзіміз бөлек жасырын ашып басамыз.
            // «Сақтау және чек шығару» чек терезесін ашқан бойда бетті қайта жүктейді (location.reload) —
            // байланған терезе сонда үзіліп, чек шықпай қалатын. Бөлек ашылса, бет жаңарса да чек шығады
            if (e.Uri.Contains("order_print") || (e.Uri.Contains("print") && e.Uri.StartsWith("http")))
            {
                e.Handled = true; // сайттың терезесі ашылмайды
                string url = e.Uri;
                BeginInvoke(new Action(() => { _ = PrintUrl(url); }));
                return;
            }
            var def = e.GetDeferral();
            try
            {
                bool isPrint = e.Uri.Contains("print");
                // Бос терезе (window.open('') — QR/сайт заказын қабылдағанда чек терезесі алдын ала ашылады,
                // сосын order_print.php-ге ауысады): ол да чек болуы мүмкін, сондықтан әзірге жасырын ашамыз.
                // Чек емес бетке ауысса — көрсетеміз
                bool blank = string.IsNullOrEmpty(e.Uri) || e.Uri.StartsWith("about:");
                bool hidden = isPrint || blank;
                var f = new Form { Text = "Berik POS", ShowInTaskbar = !hidden, StartPosition = FormStartPosition.Manual, Size = new Size(600, 700), Location = hidden ? new Point(-3000, -3000) : new Point(Left + 100, Top + 60) };
                if (hidden) { f.FormBorderStyle = FormBorderStyle.None; f.Opacity = 0; } // чек терезесі көрінбейді
                var wv = new WebView2 { Dock = DockStyle.Fill };
                f.Controls.Add(wv);
                f.Show(this);
                await wv.EnsureCoreWebView2Async(env);
                var cw = wv.CoreWebView2;
                cw.WindowCloseRequested += (s, a) => f.Close();
                cw.NewWindowRequested += OnNewWindow;
                if (hidden)
                {
                    if (blank)
                        cw.NavigationStarting += (s, a) =>
                        {
                            if (!hidden || a.Uri.Contains("print") || a.Uri.StartsWith("about:")) return;
                            hidden = false; // чек емес бет — кәдімгі терезе
                            f.FormBorderStyle = FormBorderStyle.Sizable; f.ShowInTaskbar = true;
                            f.Location = new Point(Left + 100, Top + 60); f.Opacity = 1;
                        };
                    await cw.AddScriptToExecuteOnDocumentCreatedAsync("window.__berikPrint=window.print;window.print=function(){try{window.chrome.webview.postMessage('print')}catch(e){window.__berikPrint()}};");
                    bool done = false;
                    cw.WebMessageReceived += async (s, a) =>
                    {
                        if (done || !hidden || a.TryGetWebMessageAsString() != "print") return;
                        done = true;
                        await PrintSilent(cw, (cw.Source ?? e.Uri).Contains("kind=kitchen")); // кухня чегі — кухня принтеріне
                        await Task.Delay(500);
                        if (!f.IsDisposed) f.Close();
                    };
                    var t = new Timer { Interval = 30000 }; t.Tick += (s, a) => { t.Stop(); if (hidden && !f.IsDisposed) f.Close(); }; t.Start();
                }
                e.NewWindow = cw;
                e.Handled = true;
            }
            catch (Exception ex) { Log.W("newwindow: " + ex.Message); }
            finally { def.Complete(); }
        }

        // Чек бетін жасырын терезеде ашып, window.print() шақырылғанда (немесе бет жүктелген соң) диалогсыз басады
        async Task PrintUrl(string url)
        {
            Log.W("print open: " + url);
            var f = new Form { Text = "Berik POS", ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Size = new Size(600, 700), Location = new Point(-3000, -3000), FormBorderStyle = FormBorderStyle.None, Opacity = 0 };
            var wv = new WebView2 { Dock = DockStyle.Fill };
            f.Controls.Add(wv);
            f.Show(this);
            bool done = false;
            try
            {
                await wv.EnsureCoreWebView2Async(env);
                var cw = wv.CoreWebView2;
                cw.NewWindowRequested += (s, a) => a.Handled = true;
                cw.WindowCloseRequested += (s, a) => { }; // бет өзі жабам десе де — басып болғанша күтеміз
                await cw.AddScriptToExecuteOnDocumentCreatedAsync("window.__berikPrint=window.print;window.print=function(){try{window.chrome.webview.postMessage('print')}catch(e){window.__berikPrint()}};window.close=function(){};");
                var got = new TaskCompletionSource<bool>();
                cw.WebMessageReceived += (s, a) => { if (a.TryGetWebMessageAsString() == "print") got.TrySetResult(true); };
                cw.NavigationCompleted += async (s, a) =>
                {
                    if (!a.IsSuccess) { Log.W("print page error: " + a.WebErrorStatus); got.TrySetResult(false); return; }
                    await Task.Delay(1500); // бет өзі print() шақырмаса да — жүктелген соң басамыз
                    got.TrySetResult(true);
                };
                cw.Navigate(url);
                var first = await Task.WhenAny(got.Task, Task.Delay(20000));
                if (first == got.Task && got.Task.Result)
                {
                    done = true;
                    await Task.Delay(300); // суреттер (логотип) жүктелсін
                    await PrintSilent(cw, url.Contains("kind=kitchen"));
                }
                else Log.W("print timeout/fail: " + url);
            }
            catch (Exception ex) { Log.W("print url: " + ex.Message); }
            finally { await Task.Delay(done ? 800 : 0); if (!f.IsDisposed) f.Close(); }
        }

        // Чектің басылғандағы биіктігі (дюйм): print стилімен өлшейміз
        static async Task<double> ContentHeightIn(CoreWebView2 cw)
        {
            try
            {
                try { await cw.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedMedia", "{\"media\":\"print\"}"); } catch { }
                string r = await cw.ExecuteScriptAsync("(function(){var d=document.documentElement,b=document.body;var h=Math.max(d.scrollHeight,d.offsetHeight,b?b.scrollHeight:0,b?b.offsetHeight:0,b?Math.ceil(b.getBoundingClientRect().bottom+window.scrollY):0);return h;})()");
                try { await cw.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedMedia", "{\"media\":\"\"}"); } catch { }
                double px;
                if (double.TryParse((r ?? "").Trim('"'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out px) && px > 0)
                {
                    Log.W($"print height: {px}px");
                    return px / 96.0;
                }
            }
            catch (Exception ex) { Log.W("print height: " + ex.Message); }
            return 0;
        }

        async Task PrintSilent(CoreWebView2 cw, bool kitchen = false)
        {
            // Принтер әлі таңдалмаса — бірінші рет таңдатамыз
            // Чек ешнәрсе сұрамай бірден шығады: принтер таңдалмаса — Windows-тың әдепкі принтері
            // (баптау терезесі тек «Принтер» батырмасынан ашылады, user 2026-11-25)
            string printer = kitchen ? KitchenPrinter() : ChosenPrinter();
            try
            {
                // Windows 10/11 (жаңа WebView2): нақты принтерге, диалогсыз
                var ps = env.CreatePrintSettings();
                if (!string.IsNullOrEmpty(printer)) ps.PrinterName = printer;
                // Чек ені (мм): сайттың print.css-індегі body ені (80 мм чекте 70 мм) орнына — шеттегі бос орын азаяды
                int rw = OptInt(Opts(), "rw", 0, 0, 210);
                if (rw > 0)
                    try { await cw.ExecuteScriptAsync("(function(){var s=document.createElement('style');s.textContent='html,body{margin:0!important}body{width:" + rw + "mm!important;padding:0 1mm!important;box-sizing:border-box}';document.head?document.head.appendChild(s):document.documentElement.appendChild(s);})()"); } catch { }
                ApplyOpts(ps, await ContentHeightIn(cw));
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
            if (manual) { lblInfo.Text = action == "menu" ? "Меню жаңартылуда..." : "Серверге жүктелуде..."; barBusy = lblInfo.Text; PushBar(); UseWaitCursor = true; }
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

        async Task News()
        {
            if (agent == null || syncing || closing || !netUp) return;
            syncing = true;
            try { await Task.Run(() => agent.Call("state", null, 60000)); }
            catch (Exception ex) { Log.W("news: " + ex.Message); }
            finally { syncing = false; }
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
            netUp = online;
            lblNet.Text = online ? "● Онлайн" : "● Офлайн";
            lblNet.ForeColor = online ? Color.FromArgb(22, 150, 70) : Color.FromArgb(200, 60, 40);
            PushBar();
        }

        async Task Refresh2()
        {
            if (agent == null) return;
            var st = await Task.Run(() => agent.Call("status", null, 15000));
            if (string.IsNullOrEmpty(lblNet.Text)) SetNet(false);
            lblInfo.Text = $"Жүктелмеген заказ: {st.Int("pending")}   ·   Соңғы жүктеу: {st.Str("last_push") ?? "—"}";
            barPending = st.Int("pending"); barLast = st.Str("last_push") ?? "—"; barBusy = "";
            PushBar();
        }

        // ===== Кассаның өз мәзіріндегі программа батырмасы (бұрынғы жоғарғы жолақтың орнына, user 2026-11-25) =====
        int barPending; string barLast = "—", barBusy = "";
        static string J(string v) => "\"" + (v ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ") + "\"";
        void PushBar()
        {
            try
            {
                var cw = web.CoreWebView2; if (cw == null) return;
                _ = cw.ExecuteScriptAsync("window.__bpw&&window.__bpw({on:" + (netUp ? "true" : "false") + ",pending:" + barPending + ",last:" + J(barLast) + ",busy:" + J(barBusy) + ",ver:" + J(Program.Version) + "})");
            }
            catch { }
        }
        // Диагностика: басқа компьютерде чек шықпаса — осы терезенің суретін жібереді (user 2026-11-25)
        async Task ShowDiag()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Berik POS нұсқасы: " + Program.Version);
            sb.AppendLine("Windows: " + Environment.OSVersion.VersionString + (Environment.Is64BitOperatingSystem ? " 64-bit" : " 32-bit"));
            try { sb.AppendLine("WebView2: " + CoreWebView2Environment.GetAvailableBrowserVersionString()); } catch { }
            sb.AppendLine("Интернет: " + (netUp ? "бар" : "жоқ"));
            sb.AppendLine();
            string def = ""; try { def = new PrinterSettings().PrinterName; } catch { }
            sb.AppendLine("Клиент чегі принтері: " + (File.Exists(PrinterFile) ? File.ReadAllText(PrinterFile).Trim() : "(таңдалмаған)") + "  → " + ChosenPrinter());
            sb.AppendLine("Кухня чегі принтері: " + (File.Exists(KitchenFile) && File.ReadAllText(KitchenFile).Trim() != "" ? File.ReadAllText(KitchenFile).Trim() : "(клиент чегімен бірдей)"));
            sb.AppendLine("Windows әдепкі принтері: " + def);
            try { if (File.Exists(OptsFile)) sb.AppendLine("Баптаулар: " + string.Join(", ", File.ReadAllLines(OptsFile))); } catch { }
            sb.Append("Компьютердегі принтерлер: ");
            try { var l = new List<string>(); foreach (string p in PrinterSettings.InstalledPrinters) l.Add(p); sb.AppendLine(string.Join("; ", l)); } catch { sb.AppendLine("?"); }
            // Касса бетіндегі «Сақтау және чек шығару» батырмасы: автомат чек қосулы ма (data-print), қай чектер (data-kinds)
            try
            {
                string r = await web.CoreWebView2.ExecuteScriptAsync("(function(){var b=document.querySelector('.cashbox_pay2[data-print]');return b?('print='+b.getAttribute('data-print')+' kinds='+b.getAttribute('data-kinds')):'(касса беті ашық емес)'})()");
                sb.AppendLine("Автомат чек (касса беті): " + r.Trim('"'));
            }
            catch { }
            sb.AppendLine();
            sb.AppendLine("Соңғы басу жазбалары:");
            try
            {
                var lines = new List<string>();
                foreach (var f in Directory.GetFiles(Paths.Logs, "app-*.log")) lines.AddRange(File.ReadAllLines(f));
                var pr = lines.FindAll(x => x.Contains(" print") || x.Contains("newwindow") || x.Contains("update"));
                foreach (var x in pr.GetRange(Math.Max(0, pr.Count - 25), Math.Min(25, pr.Count))) sb.AppendLine(x);
                if (pr.Count == 0) sb.AppendLine("(жоқ: программа әлі чек басуға тырыспаған)");
            }
            catch (Exception ex) { sb.AppendLine("лог оқылмады: " + ex.Message); }

            using (var f = new Form { Text = "Диагностика", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(820, 560), Font = new Font("Segoe UI", 9.5f), MinimizeBox = false })
            {
                var tb = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9.5f), Text = sb.ToString() };
                var pn = new Panel { Dock = DockStyle.Bottom, Height = 48 };
                var cp = new Button { Text = "Көшіру", Left = 12, Top = 8, Width = 140, Height = 32 };
                var lg = new Button { Text = "Лог папкасы", Left = 162, Top = 8, Width = 140, Height = 32 };
                var cl = new Button { Text = "Жабу", Left = 312, Top = 8, Width = 140, Height = 32, DialogResult = DialogResult.Cancel };
                cp.Click += (s, e) => { try { Clipboard.SetText(tb.Text); cp.Text = "Көшірілді ✓"; } catch { } };
                lg.Click += (s, e) => { try { System.Diagnostics.Process.Start("explorer.exe", Paths.Logs); } catch { } };
                pn.Controls.AddRange(new Control[] { cp, lg, cl });
                f.Controls.Add(tb); f.Controls.Add(pn); f.CancelButton = cl;
                f.ShowDialog(this);
            }
        }

        async void OnBarCmd(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string m; try { m = e.TryGetWebMessageAsString(); } catch { return; }
            switch (m)
            {
                case "bpw:menu": await Sync("menu", true); break;
                case "bpw:push": await Sync("push", true); break;
                case "bpw:printer": OpenPrinters(); break;
                case "bpw:server": await ChangeServer(); break;
                case "bpw:diag": await ShowDiag(); break;
            }
        }
        // Онлайн/Офлайн белгісі «Касса-…» блогында (аты мен иконкасының жанында); басқанда толығы ашылады
        const string BarJs = @"(function(){
if(window.top!==window||window.__bpw)return;
var st={on:false,pending:0,last:'—',busy:''},el;
var ON='<svg viewBox=""0 0 24 24"" width=""20"" height=""20"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M5 12.55a11 11 0 0 1 14.08 0""/><path d=""M1.42 9a16 16 0 0 1 21.16 0""/><path d=""M8.53 16.11a6 6 0 0 1 6.95 0""/><line x1=""12"" y1=""20"" x2=""12.01"" y2=""20""/></svg>';
var OFF='<svg viewBox=""0 0 24 24"" width=""20"" height=""20"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><line x1=""1"" y1=""1"" x2=""23"" y2=""23""/><path d=""M16.72 11.06A10.94 10.94 0 0 1 19 12.55""/><path d=""M5 12.55a10.94 10.94 0 0 1 5.17-2.39""/><path d=""M10.71 5.05A16 16 0 0 1 22.58 9""/><path d=""M1.42 9a15.91 15.91 0 0 1 4.7-2.88""/><path d=""M8.53 16.11a6 6 0 0 1 6.95 0""/><line x1=""12"" y1=""20"" x2=""12.01"" y2=""20""/></svg>';
var css='.bpw{position:relative;display:flex;align-items:center;justify-content:center;width:40px;height:40px;margin-right:8px;flex:0 0 auto;border-radius:12px;cursor:pointer;user-select:none;pointer-events:auto;color:#c83c28;background:#fdecea;font-family:Nunito,Segoe UI,sans-serif}'
+'.bpw.on{color:#16964a;background:#e8f6ee}.bpw.busy svg{animation:bpwp 1s infinite}@keyframes bpwp{50%{opacity:.35}}'
+'.bpw_fix{position:fixed;top:8px;right:8px;z-index:99999}'
+'.bpw_n{position:absolute;top:-5px;right:-5px;background:#f59e0b;color:#fff;border-radius:10px;padding:0 6px;font-size:11px;font-weight:700;line-height:17px}'
+'.bpw_m{display:none;position:absolute;right:0;top:calc(100% + 8px);background:#fff;color:#222;border-radius:12px;box-shadow:0 8px 30px rgba(0,0,0,.16);padding:6px;min-width:260px;z-index:99999;cursor:default;text-align:left}'
+'.bpw.open .bpw_m{display:block}'
+'.bpw_s{padding:8px 12px 2px;font-size:14px;font-weight:700}.bpw.on .bpw_s{color:#16964a}.bpw:not(.on) .bpw_s{color:#c83c28}'
+'.bpw_h{padding:2px 12px 10px;color:#666;font-size:12.5px;font-weight:500;line-height:1.6;border-bottom:1px solid #eee;margin-bottom:4px;white-space:normal}'
+'.bpw_i{padding:10px 12px;border-radius:8px;cursor:pointer;font-size:14px;font-weight:600;white-space:nowrap}.bpw_i:hover{background:#f3f4f6}';
function msg(c){try{window.chrome.webview.postMessage(c)}catch(e){}}
function draw(){
 if(!el)return;
 el.className='bpw'+(st.on?' on':'')+(st.busy?' busy':'')+(el.__fix?' bpw_fix':'')+(el.__open?' open':'');
 el.title=st.busy||(st.on?'Онлайн':'Офлайн');
 el.querySelector('.bpw_ic').innerHTML=st.on?ON:OFF;
 var n=el.querySelector('.bpw_n');n.style.display=st.pending>0?'':'none';n.textContent=st.pending;
 el.querySelector('.bpw_s').textContent=st.busy||(st.on?'● Онлайн':'● Офлайн');
 el.querySelector('.bpw_h').innerHTML='Жүктелмеген заказ: <b>'+st.pending+'</b><br>Соңғы жүктеу: '+String(st.last).replace(/</g,'&lt;')+'<br>Нұсқа: '+String(st.ver||'');
}
function mount(){
 if(el&&document.body.contains(el))return;
 if(!document.getElementById('bpw_css')){var s=document.createElement('style');s.id='bpw_css';s.textContent=css;(document.head||document.documentElement).appendChild(s);}
 el=document.createElement('div');
 el.innerHTML='<span class=""bpw_ic""></span><span class=""bpw_n""></span>'
 +'<div class=""bpw_m""><div class=""bpw_s""></div><div class=""bpw_h""></div>'
 +'<div class=""bpw_i"" data-c=""bpw:push"">Серверге жүктеу</div>'
 +'<div class=""bpw_i"" data-c=""bpw:menu"">Менюді жаңарту</div>'
 +'<div class=""bpw_i"" data-c=""bpw:printer"">Принтер</div>'
 +'<div class=""bpw_i"" data-c=""bpw:server"">Сервер</div>'
 +'<div class=""bpw_i"" data-c=""bpw:diag"">Диагностика</div></div>';
 var host=document.querySelector('.ub1_lx');
 if(host){host.insertBefore(el,host.firstChild);}else{el.__fix=true;document.body.appendChild(el);}
 el.addEventListener('click',function(e){
  e.stopPropagation();
  var i=e.target.closest('.bpw_i');
  if(i){el.__open=false;draw();msg(i.getAttribute('data-c'));return;}
  if(!e.target.closest('.bpw_m')){el.__open=!el.__open;draw();}
 });
 document.addEventListener('click',function(e){if(el.__open&&!el.contains(e.target)){el.__open=false;draw();}});
 draw();
}
window.__bpw=function(s){st=s;if(document.body)mount();draw();};
if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',mount);else mount();
})();";

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
            timer.Stop(); news.Stop();
            try
            {
                using (var f = new SetupForm(agent, cur))
                    if (f.ShowDialog(this) == DialogResult.OK) web.CoreWebView2?.Reload();
            }
            finally { timer.Start(); news.Start(); await Refresh2(); }
        }

        void OpenPrinters()
        {
            const string Same = "(клиент чегінің принтері)";
            var o = Opts();
            using (var f = new Form { Text = "Принтер баптаулары", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(480, 560), Font = new Font("Segoe UI", 10f) })
            {
                int y = 12;
                Label L(string t) { var l = new Label { Text = t, Left = 20, Top = y + 4, Width = 170 }; f.Controls.Add(l); return l; }
                ComboBox C(params string[] items) { var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = 200, Top = y, Width = 260 }; c.Items.AddRange(items); f.Controls.Add(c); y += 40; return c; }

                f.Controls.Add(new Label { Text = "Клиент чегі қай принтерге шықсын:", Left = 20, Top = y, AutoSize = true }); y += 26;
                var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = 20, Top = y, Width = 440 };
                foreach (string p in PrinterSettings.InstalledPrinters) cb.Items.Add(p);
                cb.SelectedItem = ChosenPrinter(); f.Controls.Add(cb); y += 44;
                // Кухня чегі (админ ▸ Чек ▸ «Кухня чегі» қосулы болса): бөлек принтер, болмаса клиент чегінің принтері
                f.Controls.Add(new Label { Text = "Кухня чегі қай принтерге шықсын:", Left = 20, Top = y, AutoSize = true }); y += 26;
                var kc = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = 20, Top = y, Width = 440 };
                kc.Items.Add(Same);
                foreach (string p in PrinterSettings.InstalledPrinters) kc.Items.Add(p);
                string kcNow = "";
                try { if (File.Exists(KitchenFile)) kcNow = File.ReadAllText(KitchenFile).Trim(); } catch { }
                if (kcNow != "" && kc.Items.Contains(kcNow)) kc.SelectedItem = kcNow; else kc.SelectedIndex = 0;
                f.Controls.Add(kc); y += 52;

                string[] paperK = { "printer", "58", "80", "a4", "fixed" };
                L("Қағаз өлшемі"); var paper = C("Чек лентасы (авто, бір тұтас)", "58 мм лента", "80 мм лента", "A4", "Windows драйверінікі");
                paper.SelectedIndex = Math.Max(0, Array.IndexOf(paperK, o["paper"]));
                L("Лента ұзындығы, мм"); var len = new NumericUpDown { Left = 200, Top = y, Width = 100, Minimum = 0, Maximum = 3000, Increment = 10, Value = OptInt(o, "len", 0, 0, 3000) }; f.Controls.Add(len);
                f.Controls.Add(new Label { Text = "0 = авто (чектің өз ұзындығы)", Left = 310, Top = y + 4, AutoSize = true, ForeColor = Color.Gray }); y += 40;
                int[] marginK = { 0, 2, 5, 10 };
                L("Чек ені, мм"); var rw = new NumericUpDown { Left = 200, Top = y, Width = 100, Minimum = 0, Maximum = 210, Value = OptInt(o, "rw", 0, 0, 210) }; f.Controls.Add(rw);
                f.Controls.Add(new Label { Text = "0 = әдепкі (80 мм чекке 70)", Left = 310, Top = y + 4, AutoSize = true, ForeColor = Color.Gray }); y += 40;
                L("Шеттері (поля)"); var margin = C("Жоқ", "Аз (2 мм)", "Орташа (5 мм)", "Стандарт (10 мм)");
                margin.SelectedIndex = Math.Max(0, Array.IndexOf(marginK, OptInt(o, "margin", 0, 0, 30)));
                L("Масштаб, %"); var scale = new NumericUpDown { Left = 200, Top = y, Width = 100, Minimum = 30, Maximum = 200, Increment = 5, Value = OptInt(o, "scale", 100, 30, 200) }; f.Controls.Add(scale); y += 40;
                L("Көшірме саны"); var copies = new NumericUpDown { Left = 200, Top = y, Width = 100, Minimum = 1, Maximum = 9, Value = OptInt(o, "copies", 1, 1, 9) }; f.Controls.Add(copies); y += 40;
                L("Бағдар"); var orient = C("Тік", "Жатық"); orient.SelectedIndex = o["orient"] == "landscape" ? 1 : 0;
                L("Түс"); var color = C("Түсті", "Ақ-қара"); color.SelectedIndex = o["color"] == "gray" ? 1 : 0;
                var header = new CheckBox { Text = "Жоғарғы/төменгі колонтитул (сайт сілтемесі, күні)", Left = 20, Top = y, Width = 440, Checked = o["header"] == "1" }; f.Controls.Add(header); y += 44;

                void SaveAll()
                {
                    Save(cb, kc);
                    try
                    {
                        File.WriteAllLines(OptsFile, new[] {
                            "paper=" + paperK[Math.Max(0, paper.SelectedIndex)],
                            "margin=" + marginK[Math.Max(0, margin.SelectedIndex)],
                            "scale=" + (int)scale.Value,
                            "copies=" + (int)copies.Value,
                            "orient=" + (orient.SelectedIndex == 1 ? "landscape" : "portrait"),
                            "color=" + (color.SelectedIndex == 1 ? "gray" : "color"),
                            "header=" + (header.Checked ? "1" : "0"),
                            "rw=" + (int)rw.Value,
                            "len=" + (int)len.Value,
                        });
                    }
                    catch { }
                }

                var ok = new Button { Text = "Сақтау", Left = 20, Top = y, Width = 140, Height = 34, DialogResult = DialogResult.OK };
                var test = new Button { Text = "Сынақ чек", Left = 170, Top = y, Width = 140, Height = 34 };
                var win = new Button { Text = "Windows...", Left = 320, Top = y, Width = 140, Height = 34 };
                test.Click += async (s, e) => { SaveAll(); await TestPrint(false); if (kc.SelectedIndex > 0) await TestPrint(true); };
                win.Click += (s, e) => { try { System.Diagnostics.Process.Start("control", "printers"); } catch { } };
                f.Controls.AddRange(new Control[] { ok, test, win });
                f.ClientSize = new Size(480, y + 50);
                f.AcceptButton = ok;
                if (f.ShowDialog(this) == DialogResult.OK) SaveAll();
            }
            _ = Refresh2();
        }

        static void Save(ComboBox cb, ComboBox kc)
        {
            try
            {
                Directory.CreateDirectory(Paths.Data);
                File.WriteAllText(PrinterFile, cb.SelectedItem as string ?? "");
                File.WriteAllText(KitchenFile, kc.SelectedIndex > 0 ? (kc.SelectedItem as string ?? "") : ""); // бос = клиент чегінің принтері
            }
            catch { }
        }

        async Task TestPrint(bool kitchen)
        {
            var f = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000), Size = new Size(400, 400) };
            var wv = new WebView2 { Dock = DockStyle.Fill }; f.Controls.Add(wv); f.Show(this);
            try
            {
                await wv.EnsureCoreWebView2Async(env);
                var tcs = new TaskCompletionSource<bool>();
                wv.CoreWebView2.NavigationCompleted += (s, e) => tcs.TrySetResult(true);
                wv.CoreWebView2.NavigateToString("<html><body style='font-family:sans-serif;width:70mm;margin:0'><h3 style='text-align:center'>Berik POS</h3><p style='text-align:center'>" + (kitchen ? "Сынақ чек: КУХНЯ" : "Сынақ чек") + "<br>" + DateTime.Now.ToString("dd.MM.yyyy HH:mm") + "</p><p style='text-align:center'>Принтер: " + System.Net.WebUtility.HtmlEncode(kitchen ? KitchenPrinter() : ChosenPrinter()) + "</p></body></html>");
                await tcs.Task;
                await PrintSilent(wv.CoreWebView2, kitchen);
            }
            finally { await Task.Delay(500); f.Close(); }
        }

        async void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (closing) return;
            closing = true;
            e.Cancel = true;
            timer.Stop(); news.Stop();
            Hide();
            // Жабар алдында интернет болса, соңғы жазбаларды жүктеп көреміз (ең көбі 20 секунд)
            if (agent != null) { var t = Task.Run(() => agent.Call("auto", null, 20000)); await Task.WhenAny(t, Task.Delay(20000)); }
            await Task.Run(() => engine.Dispose());
            Application.Exit();
        }
    }
}
