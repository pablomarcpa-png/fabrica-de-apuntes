using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FabricaDeApuntes
{
    public class ProcResult
    {
        public int Status = -1;
        public string Out = "";
        public string Err = "";
        public bool TimedOut = false;
    }

    public static class Proc
    {
        public static string RutaExtendida()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string actual = Environment.GetEnvironmentVariable("PATH") ?? "";
            return Path.Combine(home, ".local", "bin") + ";" + actual;
        }

        public static ProcResult Run(string exe, IEnumerable<string> args, string stdin = null, string cwd = null,
            int timeoutSec = 60, CancellationToken cancel = default, Action<string> onOut = null)
        {
            var res = new ProcResult();
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                StandardInputEncoding = new UTF8Encoding(false)
            };
            if (cwd != null) psi.WorkingDirectory = cwd;
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["PATH"] = RutaExtendida();
            var so = new StringBuilder();
            var se = new StringBuilder();
            Process p;
            try
            {
                p = new Process { StartInfo = psi };
                p.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    lock (so) so.AppendLine(e.Data);
                    if (onOut != null) { try { onOut(e.Data); } catch { } }
                };
                p.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    lock (se) se.AppendLine(e.Data);
                    if (onOut != null) { try { onOut(e.Data); } catch { } }
                };
                p.Start();
            }
            catch (Exception ex)
            {
                res.Err = ex.Message;
                return res;
            }
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            try
            {
                if (stdin != null) p.StandardInput.Write(stdin);
                p.StandardInput.Close();
            }
            catch { }
            var limite = DateTime.UtcNow.AddSeconds(timeoutSec);
            while (!p.HasExited)
            {
                if (DateTime.UtcNow > limite) { res.TimedOut = true; Matar(p); break; }
                if (cancel.IsCancellationRequested) { Matar(p); break; }
                Thread.Sleep(50);
            }
            try { p.WaitForExit(3000); } catch { }
            Thread.Sleep(100);
            try { res.Status = p.HasExited ? p.ExitCode : -1; } catch { }
            lock (so) res.Out = so.ToString();
            lock (se) res.Err = se.ToString();
            return res;
        }

        static void Matar(Process p)
        {
            try { p.Kill(true); } catch { }
        }
    }

    public class ClaudeCode
    {
        public static readonly ClaudeCode Shared = new ClaudeCode();
        readonly object lk = new object();
        string cached;
        public bool Instalando;
        public string InstalacionError = "";
        public bool Entrando;
        public string LoginUrl = "";
        Dictionary<string, object> ultimoEstado = new Dictionary<string, object>();
        DateTime ultimaHora = DateTime.MinValue;

        public string Buscar(bool force = false)
        {
            lock (lk)
            {
                if (!force && cached != null && File.Exists(cached)) return cached;
            }
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] cands =
            {
                Path.Combine(home, ".local", "bin", "claude.exe"),
                Path.Combine(home, ".claude", "local", "claude.exe"),
                Path.Combine(local, "Programs", "claude", "claude.exe"),
                Path.Combine(local, "Microsoft", "WinGet", "Links", "claude.exe")
            };
            foreach (var c in cands)
            {
                if (File.Exists(c)) { lock (lk) cached = c; return c; }
            }
            var r = Proc.Run("where.exe", new[] { "claude.exe" }, timeoutSec: 8);
            foreach (var linea in r.Out.Split('\n'))
            {
                var s = linea.Trim();
                if (s.Length > 3 && File.Exists(s)) { lock (lk) cached = s; return s; }
            }
            return null;
        }

        public Dictionary<string, object> Estado()
        {
            lock (lk)
            {
                if ((DateTime.UtcNow - ultimaHora).TotalSeconds < 2.0 && ultimoEstado.Count > 0 && !Instalando && !Entrando)
                    return ultimoEstado;
            }
            var r = Calcular();
            lock (lk) { ultimoEstado = r; ultimaHora = DateTime.UtcNow; }
            return r;
        }

        Dictionary<string, object> Calcular()
        {
            var d = new Dictionary<string, object>();
            lock (lk)
            {
                d["instalando"] = Instalando;
                d["errorInstalacion"] = InstalacionError;
                d["entrando"] = Entrando;
                d["loginUrl"] = LoginUrl;
            }
            var cl = Buscar();
            if (cl == null)
            {
                d["instalado"] = false;
                d["sesion"] = false;
                d["detalle"] = "Claude Code no está instalado.";
                return d;
            }
            var v = Proc.Run(cl, new[] { "--version" }, timeoutSec: 15);
            var a = Proc.Run(cl, new[] { "auth", "status" }, timeoutSec: 15);
            string txt = (a.Out + " " + a.Err).ToLowerInvariant();
            string[] malo = { "not logged", "no está conectado", "no está autenticado", "not authenticated", "logged out", "\"loggedin\": false", "\"loggedin\":false" };
            bool sesion = a.Status == 0;
            foreach (var m in malo) if (txt.Contains(m)) sesion = false;
            d["instalado"] = true;
            d["version"] = v.Out.Trim();
            d["sesion"] = sesion;
            d["detalle"] = sesion ? "Conectado." : "Falta iniciar sesión con tu cuenta de Claude.";
            if (sesion) lock (lk) { LoginUrl = ""; Entrando = false; }
            return d;
        }

        public void Instalar()
        {
            lock (lk)
            {
                if (Instalando) return;
                Instalando = true;
                InstalacionError = "";
            }
            Task.Run(() =>
            {
                var r = Proc.Run("powershell.exe",
                    new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", "irm https://claude.ai/install.ps1 | iex" },
                    timeoutSec: 900);
                bool ok = Buscar(true) != null;
                lock (lk)
                {
                    Instalando = false;
                    if (!ok)
                    {
                        string t = (r.Err + "\n" + r.Out).Trim();
                        if (r.TimedOut) InstalacionError = "La instalación ha tardado demasiado. Comprueba tu conexión a internet y vuelve a intentarlo.";
                        else InstalacionError = t.Length > 300 ? t.Substring(t.Length - 300) : t;
                        if (string.IsNullOrEmpty(InstalacionError)) InstalacionError = "No se pudo instalar. Comprueba tu conexión a internet.";
                    }
                }
            });
        }

        public void IniciarSesion()
        {
            var cl = Buscar();
            if (cl == null) return;
            lock (lk)
            {
                if (Entrando) return;
                Entrando = true;
                LoginUrl = "";
            }
            Task.Run(() =>
            {
                bool avisado = false;
                Proc.Run(cl, new[] { "auth", "login" }, timeoutSec: 600, onOut: s =>
                {
                    if (avisado) return;
                    int i = s.IndexOf("https://", StringComparison.Ordinal);
                    if (i < 0) return;
                    var sb = new StringBuilder();
                    for (int k = i; k < s.Length && !char.IsWhiteSpace(s[k]) && s[k] != '\u001b'; k++) sb.Append(s[k]);
                    var url = sb.ToString();
                    if (url.Length > 20)
                    {
                        avisado = true;
                        lock (lk) LoginUrl = url;
                    }
                });
                lock (lk) Entrando = false;
            });
        }

        public void AbrirEnTerminal(string cmd)
        {
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", "/c start \"Claude\" cmd /k \"" + cmd + "\"") { UseShellExecute = false, CreateNoWindow = true });
            }
            catch { }
        }
    }
}
