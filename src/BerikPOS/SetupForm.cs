using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BerikPOS
{
    // Бірінші кіру: кассаны серверге байлау және бүкіл деректі тарту
    class SetupForm : UiForm
    {
        readonly Agent agent;
        readonly UiText server = new UiText(), code = new UiText("", 10);
        readonly UiPicker branch = new UiPicker { Visible = false };
        readonly Label branchLbl;
        readonly Label msg;
        readonly UiButton ok = new UiButton("Кіру және деректі жүктеу", UiButton.Kind.Dark);

        public SetupForm(Agent a, string currentServer = null) : base("Кассаны байлау", 480)
        {
            agent = a;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            Label L(string t, bool muted = false) => Ui.Para(t, muted ? Ui.F(10.5f) : Ui.Semi(10.5f), muted ? Ui.Muted : Ui.Text, Inner);
            AddBody(L("Бірінші рет интернетпен кіру керек. Компания кодын енгізіңіз, кейін кассирлер өз PIN-кодымен кіреді, интернетсіз де.", true), 18);
            AddBody(L("Сервер адресі"), 6); AddBody(server, 16);
            AddBody(L("Компания коды (мысалы: n135)"), 6); AddBody(code, 16);
            branchLbl = L("Точка"); branchLbl.Visible = false;
            AddBody(branchLbl, 6); AddBody(branch, 16);
            msg = new Label { AutoSize = false, Height = 44, ForeColor = Ui.Red, Font = Ui.F(10f), BackColor = Color.Transparent };
            AddBody(msg, 0);
            AddFoot(ok);
            AcceptButton = ok;
            Finish();

            server.Text = string.IsNullOrEmpty(currentServer) ? DefaultServer() : currentServer;
            ok.Click += async (s, e) => await Go();
            Shown += (s, e) => code.Box.Focus();
        }

        static string DefaultServer()
        {
            // Орнатқыш server.txt-ке жазады (build кезінде берілген адрес)
            try { string f = Path.Combine(Paths.App, "server.txt"); if (File.Exists(f)) return File.ReadAllText(f).Trim(); } catch { }
            return "https://";
        }

        async Task Go()
        {
            msg.Text = ""; ok.Enabled = false; ok.Text = "Жүктелуде... (бірнеше минут болуы мүмкін)"; ok.Invalidate(); UseWaitCursor = true;
            try
            {
                var body = new Dictionary<string, object> { ["server"] = server.Text.Trim(), ["company_code"] = code.Text.Trim() };
                int bid; if (branch.Visible && int.TryParse(branch.Value, out bid)) body["branch_id"] = bid;
                var r = await Task.Run(() => agent.Call("setup", body));
                if (r.Bool("ok")) { DialogResult = DialogResult.OK; Close(); return; }
                string err = r.Str("error");
                if (err == "choose_branch" && r.TryGetValue("branches", out var bl) && bl is ArrayList list)
                {
                    branch.Items.Clear();
                    foreach (var o in list) if (o is Dictionary<string, object> d) branch.Add(d.Int("id").ToString(), d.Str("name"));
                    branch.Visible = branchLbl.Visible = true;
                    if (branch.Items.Count > 0) branch.SelectedIndex = 0;
                    msg.Text = "Точканы таңдап, қайта басыңыз.";
                }
                else msg.Text = MainForm.Human(err) + (r.Str("detail") != null ? " (" + r.Str("detail") + ")" : "");
            }
            finally { ok.Enabled = true; ok.Text = "Кіру және деректі жүктеу"; ok.Invalidate(); UseWaitCursor = false; }
        }
    }
}
