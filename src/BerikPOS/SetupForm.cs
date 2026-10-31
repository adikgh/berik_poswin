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
    class SetupForm : Form
    {
        readonly Agent agent;
        readonly TextBox server = new TextBox(), code = new TextBox { MaxLength = 10 };
        readonly ComboBox branch = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Visible = false };
        readonly Label branchLbl = new Label { Text = "Точка:", Visible = false, AutoSize = true };
        readonly Label msg = new Label { AutoSize = false, Height = 40, ForeColor = Color.FromArgb(190, 50, 40) };
        readonly Button ok = new Button { Text = "Кіру және деректі жүктеу", Height = 38 };

        public SetupForm(Agent a, string currentServer = null)
        {
            agent = a;
            Text = "Berik POS — кассаны байлау";
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(420, 400);
            Font = new Font("Segoe UI", 10f);

            var p = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1 };
            Label L(string t) => new Label { Text = t, AutoSize = true, Margin = new Padding(0, 8, 0, 2) };
            p.Controls.Add(new Label { Text = "Бірінші рет интернетпен кіру керек. Компания кодын енгізіңіз, кейін кассирлер өз PIN-кодымен кіреді, интернетсіз де.", AutoSize = false, Height = 44, Width = 370 });
            p.Controls.Add(L("Сервер адресі:")); p.Controls.Add(server);
            p.Controls.Add(L("Компания коды (мысалы: n135):")); p.Controls.Add(code);
            p.Controls.Add(branchLbl); p.Controls.Add(branch);
            p.Controls.Add(msg); p.Controls.Add(ok);
            foreach (Control c in p.Controls) if (!(c is Label)) c.Width = 370;
            msg.Width = 370;
            Controls.Add(p);
            AcceptButton = ok;

            server.Text = string.IsNullOrEmpty(currentServer) ? DefaultServer() : currentServer;
            ok.Click += async (s, e) => await Go();
        }

        static string DefaultServer()
        {
            // Орнатқыш server.txt-ке жазады (build кезінде берілген адрес)
            try { string f = Path.Combine(Paths.App, "server.txt"); if (File.Exists(f)) return File.ReadAllText(f).Trim(); } catch { }
            return "https://";
        }

        async Task Go()
        {
            msg.Text = ""; ok.Enabled = false; ok.Text = "Жүктелуде... (бірнеше минут болуы мүмкін)"; UseWaitCursor = true;
            try
            {
                var body = new Dictionary<string, object> { ["server"] = server.Text.Trim(), ["company_code"] = code.Text.Trim() };
                if (branch.Visible && branch.SelectedItem is KeyValuePair<int, string> kv) body["branch_id"] = kv.Key;
                var r = await Task.Run(() => agent.Call("setup", body));
                if (r.Bool("ok")) { DialogResult = DialogResult.OK; Close(); return; }
                string err = r.Str("error");
                if (err == "choose_branch" && r.TryGetValue("branches", out var bl) && bl is ArrayList list)
                {
                    branch.Items.Clear();
                    foreach (var o in list) if (o is Dictionary<string, object> d) branch.Items.Add(new KeyValuePair<int, string>(d.Int("id"), d.Str("name")));
                    branch.DisplayMember = "Value"; branch.Visible = branchLbl.Visible = true;
                    if (branch.Items.Count > 0) branch.SelectedIndex = 0;
                    msg.Text = "Точканы таңдап, қайта басыңыз.";
                }
                else msg.Text = MainForm.Human(err) + (r.Str("detail") != null ? " (" + r.Str("detail") + ")" : "");
            }
            finally { ok.Enabled = true; ok.Text = "Кіру және деректі жүктеу"; UseWaitCursor = false; }
        }
    }
}
