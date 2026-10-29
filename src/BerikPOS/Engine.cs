using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace BerikPOS
{
    // Программа папкалары
    static class Paths
    {
        public static readonly string App = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        public static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BerikPOS");
        public static string Php => Path.Combine(App, "php");
        public static string MariaDb => Path.Combine(App, "mariadb");
        public static string Www => Path.Combine(App, "www");          // www\site = pos.berik.c, www\local = синхрон агенті
        public static string DbData => Path.Combine(Data, "db");
        public static string Logs => Path.Combine(Data, "logs");
        public static string WebView => Path.Combine(Data, "webview");
        public static string Runtime => Path.Combine(Data, "runtime.json");
        public static string Settings => Path.Combine(Data, "settings.json");
    }

    static class Json
    {
        static readonly JavaScriptSerializer S = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        public static Dictionary<string, object> Parse(string s)
        {
            try { return S.Deserialize<Dictionary<string, object>>(s) ?? new Dictionary<string, object>(); }
            catch { return new Dictionary<string, object>(); }
        }
        public static string Write(object o) => S.Serialize(o);
        public static string Str(this Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToString(v) : null;
        public static bool Bool(this Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) && v is bool b && b;
        public static int Int(this Dictionary<string, object> d, string k) { int.TryParse(d.Str(k), out var i); return i; }
    }

    static class Log
    {
        static readonly object L = new object();
        public static void W(string msg)
        {
            try
            {
                lock (L)
                {
                    Directory.CreateDirectory(Paths.Logs);
                    File.AppendAllText(Path.Combine(Paths.Logs, "app-" + DateTime.Now.ToString("yyyy-MM") + ".log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + msg + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    // MariaDB + PHP процестерін іске қосу/тоқтату
    class Engine : IDisposable
    {
        public int DbPort, SitePort, AgentPort;
        string dbPass;
        readonly List<Process> procs = new List<Process>();
        public const string DbName = "berik_local";

        static int FreePort(int start)
        {
            for (int p = start; p < start + 200; p++)
            {
                try { var l = new TcpListener(IPAddress.Loopback, p); l.Start(); l.Stop(); return p; } catch { }
            }
            throw new Exception("Бос порт табылмады");
        }

        static bool PortOpen(int port)
        {
            try { using (var c = new TcpClient()) { var r = c.BeginConnect(IPAddress.Loopback, port, null, null); bool ok = r.AsyncWaitHandle.WaitOne(300); if (ok) c.EndConnect(r); return ok && c.Connected; } }
            catch { return false; }
        }

        public void Start(Action<string> status)
        {
            Directory.CreateDirectory(Paths.Data);
            Directory.CreateDirectory(Paths.Logs);
            var rt = File.Exists(Paths.Runtime) ? Json.Parse(File.ReadAllText(Paths.Runtime)) : new Dictionary<string, object>();
            dbPass = rt.Str("db_pass");
            if (string.IsNullOrEmpty(dbPass)) { dbPass = Guid.NewGuid().ToString("N").Substring(0, 20); }

            // 1) База
            string mysqld = Path.Combine(Paths.MariaDb, "bin", File.Exists(Path.Combine(Paths.MariaDb, "bin", "mariadbd.exe")) ? "mariadbd.exe" : "mysqld.exe");
            if (!Directory.Exists(Path.Combine(Paths.DbData, "mysql")))
            {
                status("Базаны бірінші рет дайындау...");
                string inst = Path.Combine(Paths.MariaDb, "bin", File.Exists(Path.Combine(Paths.MariaDb, "bin", "mariadb-install-db.exe")) ? "mariadb-install-db.exe" : "mysql_install_db.exe");
                Directory.CreateDirectory(Paths.DbData);
                var ip = Run(inst, $"--datadir=\"{Paths.DbData}\" --password={dbPass}", true);
                ip.WaitForExit(180000);
                if (!Directory.Exists(Path.Combine(Paths.DbData, "mysql"))) throw new Exception("Базаны дайындау сәтсіз аяқталды (logs папкасын қараңыз)");
                rt["db_pass"] = dbPass;
                File.WriteAllText(Paths.Runtime, Json.Write(rt));
            }
            DbPort = FreePort(3317);
            status("База іске қосылуда...");
            procs.Add(Run(mysqld, $"--defaults-file=\"{WriteMyIni()}\" --console", false));
            for (int i = 0; i < 120 && !PortOpen(DbPort); i++) Thread.Sleep(250);
            if (!PortOpen(DbPort)) throw new Exception("База іске қосылмады");

            // 2) PHP: касса сайты және синхрон агенті (бөлек, бір-бірін күттірмеу үшін)
            WriteDbConfig();
            string ini = WritePhpIni();
            string php = Path.Combine(Paths.Php, "php.exe");
            SitePort = FreePort(8317);
            procs.Add(Run(php, $"-c \"{ini}\" -S 127.0.0.1:{SitePort} -t \"{Path.Combine(Paths.Www, "site")}\"", false));
            AgentPort = FreePort(SitePort + 1);
            procs.Add(Run(php, $"-c \"{ini}\" -S 127.0.0.1:{AgentPort} -t \"{Paths.Www}\"", false));
            for (int i = 0; i < 60 && !(PortOpen(SitePort) && PortOpen(AgentPort)); i++) Thread.Sleep(200);
            Log.W($"started db:{DbPort} site:{SitePort} agent:{AgentPort}");
        }

        string WriteMyIni()
        {
            string p = Path.Combine(Paths.Data, "my.ini");
            File.WriteAllText(p, string.Join("\r\n", new[] {
                "[mysqld]",
                "datadir=" + Paths.DbData.Replace('\\', '/'),
                "port=" + DbPort,
                "bind-address=127.0.0.1",
                "character-set-server=utf8mb4",
                "collation-server=utf8mb4_unicode_ci",
                "sql_mode=NO_ENGINE_SUBSTITUTION",
                "innodb_buffer_pool_size=128M",
                "innodb_flush_log_at_trx_commit=1",
                "max_allowed_packet=64M",
                "log-error=" + Path.Combine(Paths.Logs, "mariadb.err").Replace('\\', '/'),
                ""
            }));
            return p;
        }

        void WriteDbConfig()
        {
            // pos.berik.c config/db.php — жергілікті базаға
            string p = Path.Combine(Paths.Www, "site", "config", "db.php");
            string php = "<?php\r\n// Офлайн программа өзі жазады, қолмен өзгертпеңіз\r\nclass db {\r\n" +
                "  public static $host = '127.0.0.1';\r\n  public static $port = " + DbPort + ";\r\n" +
                "  public static $user = 'root';\r\n  public static $pass = '" + dbPass + "';\r\n" +
                "  public static $name = '" + DbName + "';\r\n  public static $connect = null;\r\n" +
                "  public function __construct() {\r\n" +
                "    if (self::$connect) return;\r\n" +
                "    self::$connect = mysqli_connect(self::$host, self::$user, self::$pass, '', self::$port);\r\n" +
                "    if (!self::$connect) exit('Жергілікті база қосылмады');\r\n" +
                "    mysqli_query(self::$connect, 'CREATE DATABASE IF NOT EXISTS `" + DbName + "` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci');\r\n" +
                "    mysqli_select_db(self::$connect, self::$name);\r\n    mysqli_set_charset(self::$connect, 'utf8mb4');\r\n" +
                "    mysqli_report(MYSQLI_REPORT_OFF);\r\n  }\r\n" +
                "  public static function query($sql) { return mysqli_query(self::$connect, $sql); }\r\n}\r\n";
            File.WriteAllText(p, php, new UTF8Encoding(false));
        }

        string WritePhpIni()
        {
            string p = Path.Combine(Paths.Data, "php.ini");
            string ext = Path.Combine(Paths.Php, "ext").Replace('\\', '/');
            string sess = Path.Combine(Paths.Data, "sessions");
            Directory.CreateDirectory(sess);
            File.WriteAllText(p, string.Join("\r\n", new[] {
                "[PHP]",
                "extension_dir=\"" + ext + "\"",
                "extension=mysqli", "extension=curl", "extension=mbstring", "extension=openssl", "extension=fileinfo", "extension=gd",
                "short_open_tag=On",
                "display_errors=Off",
                "log_errors=On",
                "error_log=\"" + Path.Combine(Paths.Logs, "php.log").Replace('\\', '/') + "\"",
                "error_reporting=E_ALL & ~E_NOTICE & ~E_WARNING & ~E_DEPRECATED",
                "memory_limit=512M",
                "max_execution_time=300",
                "post_max_size=64M", "upload_max_filesize=32M",
                "date.timezone=Asia/Aqtau",
                "session.save_path=\"" + sess.Replace('\\', '/') + "\"",
                "curl.cainfo=\"" + Path.Combine(Paths.Php, "cacert.pem").Replace('\\', '/') + "\"",
                "openssl.cafile=\"" + Path.Combine(Paths.Php, "cacert.pem").Replace('\\', '/') + "\"",
                ""
            }));
            return p;
        }

        Process Run(string exe, string args, bool wait)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe),
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            psi.EnvironmentVariables["BERIK_OFFLINE"] = "1";
            psi.EnvironmentVariables["BERIK_DATA"] = Paths.Data;
            var pr = new Process { StartInfo = psi, EnableRaisingEvents = true };
            string name = Path.GetFileNameWithoutExtension(exe);
            pr.OutputDataReceived += (s, e) => { };
            pr.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data) && name != "php") Log.W(name + ": " + e.Data); };
            pr.Start();
            pr.BeginOutputReadLine(); pr.BeginErrorReadLine();
            JobObject.Add(pr); // программа құласа да, процестер бірге жабылады
            return pr;
        }

        public void Dispose()
        {
            // Базаны дұрыс жабу (мәлімет жоғалмасын)
            try
            {
                string adm = Path.Combine(Paths.MariaDb, "bin", File.Exists(Path.Combine(Paths.MariaDb, "bin", "mariadb-admin.exe")) ? "mariadb-admin.exe" : "mysqladmin.exe");
                if (DbPort > 0 && File.Exists(adm)) Run(adm, $"-h127.0.0.1 -P{DbPort} -uroot -p{dbPass} shutdown", true).WaitForExit(20000);
            }
            catch (Exception ex) { Log.W("db shutdown: " + ex.Message); }
            foreach (var p in procs) { try { if (!p.HasExited) { if (Path.GetFileName(p.StartInfo.FileName).StartsWith("php")) p.Kill(); else if (!p.WaitForExit(15000)) p.Kill(); } } catch { } }
        }
    }

    // Синхрон агентіне (www\local\sync.php) сұраныс
    class Agent
    {
        readonly int port;
        public Agent(int port) { this.port = port; }
        public Dictionary<string, object> Call(string action, object body = null, int timeoutMs = 330000)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create($"http://127.0.0.1:{port}/local/sync.php?a={action}");
                req.Timeout = timeoutMs; req.ReadWriteTimeout = timeoutMs; req.Proxy = null;
                if (body != null)
                {
                    req.Method = "POST"; req.ContentType = "application/json";
                    var b = Encoding.UTF8.GetBytes(Json.Write(body));
                    using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length);
                }
                HttpWebResponse resp;
                try { resp = (HttpWebResponse)req.GetResponse(); }
                catch (WebException we) when (we.Response != null) { resp = (HttpWebResponse)we.Response; }
                using (resp) using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) return Json.Parse(r.ReadToEnd());
            }
            catch (Exception ex) { return new Dictionary<string, object> { ["ok"] = false, ["error"] = "agent", ["detail"] = ex.Message }; }
        }
    }
}
