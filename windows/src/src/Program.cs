using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace FabricaDeApuntes
{
    static class Program
    {
        public const int Puerto = 4780;
        public static readonly string Base = "http://127.0.0.1:" + Puerto + "/";

        public static string CarpetaDeDatos()
        {
            string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fábrica de Apuntes");
            try
            {
                string r = Path.Combine(baseDir, "ruta.txt");
                if (File.Exists(r))
                {
                    string t = File.ReadAllText(r).Trim();
                    if (t.Length > 0 && Path.IsPathRooted(t)) return t;
                }
            }
            catch { }
            return baseDir;
        }

        [STAThread]
        static void Main()
        {
            bool nuevo;
            using var mutex = new Mutex(true, "FabricaDeApuntes.InstanciaUnica", out nuevo);
            if (!nuevo) return;
            ApplicationConfiguration.Initialize();
            Application.Run(new Ventana());
        }
    }

    class Ventana : Form
    {
        WebView2 web;
        ServidorLocal servidor;
        readonly string recursos = Path.Combine(AppContext.BaseDirectory, "recursos");

        public Ventana()
        {
            Text = "Fábrica de Apuntes";
            Width = 1280;
            Height = 860;
            MinimumSize = new Size(800, 560);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var menu = new MenuStrip();
            var m = new ToolStripMenuItem("Fábrica de Apuntes");
            m.DropDownItems.Add("Ver la bienvenida y el tutorial", null, (s, e) => EjecutarJs("window.faTour && window.faTour()"));
            m.DropDownItems.Add("Abrir carpeta de datos", null, (s, e) => AbrirDatos());
            m.DropDownItems.Add(new ToolStripSeparator());
            var rec = new ToolStripMenuItem("Recargar") { ShortcutKeys = Keys.F5 };
            rec.Click += (s, e) => web?.Reload();
            m.DropDownItems.Add(rec);
            m.DropDownItems.Add("Salir", null, (s, e) => Close());
            menu.Items.Add(m);
            MainMenuStrip = menu;

            web = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(web);
            Controls.Add(menu);

            Load += async (s, e) => await Iniciar();
            FormClosed += (s, e) => { try { servidor?.Parar(); } catch { } };
        }

        static string Splash(string t)
        {
            return "<html><body style=\"margin:0;height:100vh;display:flex;align-items:center;justify-content:center;font-family:Segoe UI,sans-serif;background:#f6f5f2;color:#555;font-size:16px\"><div style=\"text-align:center\">" + t + "</div></body></html>";
        }

        async System.Threading.Tasks.Task Iniciar()
        {
            try
            {
                string udf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fábrica de Apuntes", "WebView2");
                var env = await CoreWebView2Environment.CreateAsync(null, udf);
                await web.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex)
            {
                var r = MessageBox.Show(
                    "Fábrica de Apuntes necesita Microsoft Edge WebView2, que no está instalado.\n\n¿Quieres abrir la página para descargarlo? Después vuelve a abrir la app.\n\n(" + ex.Message + ")",
                    "Falta un componente de Windows", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (r == DialogResult.Yes) AbrirExterno("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
                Close();
                return;
            }

            var cw = web.CoreWebView2;
            cw.Settings.AreDefaultContextMenusEnabled = true;
            cw.Settings.IsStatusBarEnabled = false;
            cw.PermissionRequested += (s, e) =>
            {
                bool local = false;
                try { local = new Uri(e.Uri).Host == "127.0.0.1"; } catch { }
                e.State = local ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            };
            cw.NewWindowRequested += (s, e) =>
            {
                e.Handled = true;
                if (EsLocal(e.Uri)) cw.Navigate(e.Uri); else AbrirExterno(e.Uri);
            };
            cw.NavigationStarting += (s, e) =>
            {
                if (!EsLocal(e.Uri) && (e.Uri.StartsWith("http") || e.Uri.StartsWith("mailto:")))
                {
                    e.Cancel = true;
                    AbrirExterno(e.Uri);
                }
            };

            web.NavigateToString(Splash("Abriendo Fábrica de Apuntes…"));

            string dir = Program.CarpetaDeDatos();
            Directory.CreateDirectory(dir);
            var upd = new Actualizador(dir, recursos);
            upd.LimpiarObsoleta();
            servidor = new ServidorLocal(Program.Puerto, dir, recursos);
            servidor.AlFallar = msg => BeginInvoke(new Action(() =>
                web.NavigateToString(Splash("No he podido abrir el servidor local.<br>" + System.Net.WebUtility.HtmlEncode(msg)))));
            servidor.Arrancar();
            upd.Comprobar();

            // Espera a que el servidor responda y carga la app.
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            for (int i = 0; i < 80; i++)
            {
                try
                {
                    var r = await http.GetAsync(Program.Base + "api/rev");
                    if (r.IsSuccessStatusCode) { cw.Navigate(Program.Base); return; }
                }
                catch { }
                await System.Threading.Tasks.Task.Delay(250);
            }
            web.NavigateToString(Splash("El servidor local no ha arrancado.<br>Cierra la app y vuélvela a abrir."));
        }

        static bool EsLocal(string uri)
        {
            try
            {
                var u = new Uri(uri);
                if (u.Scheme == "about" || u.Scheme == "blob" || u.Scheme == "data") return true;
                return u.Host == "127.0.0.1" || u.Host == "localhost";
            }
            catch { return false; }
        }

        static void AbrirExterno(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        void EjecutarJs(string js)
        {
            try { web?.CoreWebView2?.ExecuteScriptAsync(js); } catch { }
        }

        void AbrirDatos()
        {
            try
            {
                string d = Program.CarpetaDeDatos();
                Directory.CreateDirectory(d);
                Process.Start(new ProcessStartInfo(d) { UseShellExecute = true });
            }
            catch { }
        }
    }
}
