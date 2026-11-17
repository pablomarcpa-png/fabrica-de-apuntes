using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace FabricaDeApuntes
{
    // Servidor local (127.0.0.1): datos, archivos y puente con Claude Code. Equivale al de la versión de Mac.
    public class ServidorLocal
    {
        public readonly int Puerto;
        public readonly string CarpetaDatos;
        public readonly string Recursos;
        readonly string carpetaArchivos;
        readonly string archivoDB;
        readonly object lkDb = new object();
        JsonObject db = new JsonObject();
        Timer temporizador;
        DateTime ultimaCopia = DateTime.MinValue;
        int revExterna = 0;
        readonly SemaphoreSlim semaforoIA = new SemaphoreSlim(3);
        WebApplication app;
        public Action<string> AlFallar;

        static readonly string Sistema = "Eres Claude, asistente de estudio de un estudiante universitario. Responde exactamente a lo que pide el mensaje del usuario, en espanol, con el formato que pida. No hables de ti, de herramientas ni de programacion. No uses herramientas salvo que se indique.";

        public ServidorLocal(int puerto, string carpetaDatos, string recursos)
        {
            Puerto = puerto;
            CarpetaDatos = carpetaDatos;
            Recursos = recursos;
            carpetaArchivos = Path.Combine(carpetaDatos, "archivos");
            archivoDB = Path.Combine(carpetaDatos, "datos.json");
            Directory.CreateDirectory(carpetaArchivos);
            CargarDB();
        }

        // ---------- Base de datos ----------

        void CargarDB()
        {
            if (!File.Exists(archivoDB)) { db = new JsonObject(); return; }
            try
            {
                var n = JsonNode.Parse(File.ReadAllText(archivoDB, Encoding.UTF8));
                db = n as JsonObject ?? new JsonObject();
            }
            catch
            {
                try { File.Copy(archivoDB, archivoDB + ".corrupto-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds(), true); } catch { }
                db = new JsonObject();
            }
        }

        void EscribirDB()
        {
            string txt;
            lock (lkDb) txt = db.ToJsonString();
            try
            {
                if (File.Exists(archivoDB) && (DateTime.UtcNow - ultimaCopia).TotalSeconds > 3600)
                {
                    var destino = Path.Combine(CarpetaDatos, "copia-" + DateTime.Now.ToString("yyyy-MM-dd") + ".json");
                    File.Copy(archivoDB, destino, true);
                    ultimaCopia = DateTime.UtcNow;
                }
                var tmp = archivoDB + ".tmp";
                File.WriteAllText(tmp, txt, new UTF8Encoding(false));
                File.Move(tmp, archivoDB, true);
            }
            catch { }
        }

        void ProgramarGuardado()
        {
            lock (lkDb)
            {
                temporizador?.Dispose();
                temporizador = new Timer(_ => EscribirDB(), null, 400, Timeout.Infinite);
            }
        }

        public void GuardarYa()
        {
            lock (lkDb) { temporizador?.Dispose(); temporizador = null; }
            EscribirDB();
        }

        // ---------- Arranque ----------

        public void Arrancar()
        {
            try
            {
                var b = WebApplication.CreateSlimBuilder();
                b.Logging.ClearProviders();
                b.WebHost.ConfigureKestrel(o =>
                {
                    o.Listen(IPAddress.Loopback, Puerto);
                    o.Limits.MaxRequestBodySize = 250_000_000;
                    o.AddServerHeader = false;
                });
                app = b.Build();
                app.Run(Atender);
                app.StartAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                AlFallar?.Invoke(ex.Message);
            }
        }

        public void Parar()
        {
            try { app?.StopAsync().Wait(2000); } catch { }
            GuardarYa();
        }

        // ---------- HTTP ----------

        static async Task Texto(HttpContext c, int estado, string txt, string tipo = "text/plain; charset=utf-8")
        {
            c.Response.StatusCode = estado;
            c.Response.ContentType = tipo;
            await c.Response.WriteAsync(txt);
        }

        static async Task Json(HttpContext c, int estado, JsonNode obj)
        {
            c.Response.StatusCode = estado;
            c.Response.ContentType = "application/json; charset=utf-8";
            c.Response.Headers["Cache-Control"] = "no-store";
            var bytes = Encoding.UTF8.GetBytes(obj.ToJsonString());
            c.Response.ContentLength = bytes.Length;
            await c.Response.Body.WriteAsync(bytes, 0, bytes.Length);
        }

        static JsonObject Obj(params (string, object)[] campos)
        {
            var o = new JsonObject();
            foreach (var (k, v) in campos) o[k] = ValorJson(v);
            return o;
        }

        static JsonNode ValorJson(object v)
        {
            if (v == null) return null;
            if (v is JsonNode jn) return jn;
            if (v is string s) return JsonValue.Create(s);
            if (v is bool b) return JsonValue.Create(b);
            if (v is int i) return JsonValue.Create(i);
            if (v is long l) return JsonValue.Create(l);
            return JsonValue.Create(v.ToString());
        }

        static JsonObject DesdeDic(Dictionary<string, object> d)
        {
            var o = new JsonObject();
            foreach (var kv in d) o[kv.Key] = ValorJson(kv.Value);
            return o;
        }

        static string TipoMime(string ext)
        {
            switch (ext.ToLowerInvariant())
            {
                case ".html": return "text/html; charset=utf-8";
                case ".js": return "text/javascript";
                case ".png": return "image/png";
                case ".svg": return "image/svg+xml";
                case ".ico": return "image/x-icon";
                case ".json": return "application/json";
                default: return "application/octet-stream";
            }
        }

        async Task Atender(HttpContext c)
        {
            try
            {
                string host = (c.Request.Host.Value ?? "").ToLowerInvariant();
                if (host != "127.0.0.1:" + Puerto && host != "localhost:" + Puerto)
                {
                    await Texto(c, 403, "No permitido");
                    return;
                }
                string p = c.Request.Path.Value ?? "/";
                string metodo = c.Request.Method.ToUpperInvariant();
                if (p.StartsWith("/api/"))
                {
                    if (metodo != "GET" && c.Request.Headers["x-fa"].ToString() != "1")
                    {
                        await Json(c, 403, Obj(("error", "No permitido")));
                        return;
                    }
                    await RutaAPI(c, p, metodo);
                    return;
                }
                if (p.StartsWith("/_blob/")) { await ServirArchivo(c, p); return; }
                if (p == "/" || p == "/index.html")
                {
                    string ov = Path.Combine(CarpetaDatos, "app", "index.html");
                    string f = File.Exists(ov) ? ov : Path.Combine(Recursos, "index.html");
                    if (File.Exists(f))
                    {
                        var bytes = await File.ReadAllBytesAsync(f);
                        c.Response.StatusCode = 200;
                        c.Response.ContentType = "text/html; charset=utf-8";
                        c.Response.Headers["Cache-Control"] = "no-cache";
                        c.Response.ContentLength = bytes.Length;
                        await c.Response.Body.WriteAsync(bytes, 0, bytes.Length);
                    }
                    else await Texto(c, 404, "No encontrado");
                    return;
                }
                await Texto(c, 404, "No encontrado");
            }
            catch (Exception)
            {
                try { if (!c.Response.HasStarted) await Texto(c, 500, "Error"); } catch { }
            }
        }

        static async Task<string> CuerpoTexto(HttpContext c)
        {
            using var ms = new MemoryStream();
            await c.Request.Body.CopyToAsync(ms);
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        static string SoloHex(string s)
        {
            return new string(s.Where(ch => "0123456789abcdef".IndexOf(ch) >= 0).ToArray());
        }

        async Task RutaAPI(HttpContext c, string p, string metodo)
        {
            if (p == "/api/rev") { await Json(c, 200, Obj(("ext", revExterna))); return; }
            if (p == "/api/estado")
            {
                var est = await Task.Run(() => ClaudeCode.Shared.Estado());
                await Json(c, 200, DesdeDic(est));
                return;
            }
            if (p == "/api/db" && metodo == "GET")
            {
                string txt;
                lock (lkDb) txt = db.ToJsonString();
                var bytes = Encoding.UTF8.GetBytes(txt);
                c.Response.StatusCode = 200;
                c.Response.ContentType = "application/json; charset=utf-8";
                c.Response.Headers["Cache-Control"] = "no-store";
                c.Response.ContentLength = bytes.Length;
                await c.Response.Body.WriteAsync(bytes, 0, bytes.Length);
                return;
            }
            if (p == "/api/db" && metodo == "POST")
            {
                JsonObject o = null;
                try { o = JsonNode.Parse(await CuerpoTexto(c)) as JsonObject; } catch { }
                var ops = o?["ops"] as JsonArray;
                if (ops == null) { await Json(c, 500, Obj(("code", "error"), ("message", "Datos no válidos"))); return; }
                lock (lkDb)
                {
                    foreach (var opn in ops)
                    {
                        var op = opn as JsonObject;
                        if (op == null) continue;
                        string ruta = op["path"]?.GetValue<string>();
                        if (ruta == null) continue;
                        string tipo = op["op"]?.GetValue<string>() ?? "";
                        if (tipo == "del") db.Remove(ruta);
                        else if (tipo == "put") db[ruta] = op["data"]?.DeepClone();
                    }
                    if (c.Request.Headers["x-fa-src"].ToString() == "ext") revExterna++;
                }
                ProgramarGuardado();
                await Json(c, 200, Obj(("ok", true)));
                return;
            }
            if (p == "/api/ai" && metodo == "POST") { await AtenderIA(c); return; }
            if (p == "/api/instalar") { ClaudeCode.Shared.Instalar(); await Json(c, 200, Obj(("ok", true))); return; }
            if (p == "/api/conectar") { ClaudeCode.Shared.IniciarSesion(); await Json(c, 200, Obj(("ok", true))); return; }
            if (p == "/api/conectar-terminal")
            {
                var cl = ClaudeCode.Shared.Buscar() ?? "claude";
                ClaudeCode.Shared.AbrirEnTerminal("\"" + cl + "\" auth login");
                await Json(c, 200, Obj(("ok", true)));
                return;
            }
            if (p == "/api/abrir" && metodo == "POST")
            {
                try
                {
                    var o = JsonNode.Parse(await CuerpoTexto(c)) as JsonObject;
                    string u = o?["url"]?.GetValue<string>();
                    if (u != null && u.StartsWith("https://"))
                    {
                        Process.Start(new ProcessStartInfo(u) { UseShellExecute = true });
                        await Json(c, 200, Obj(("ok", true)));
                        return;
                    }
                }
                catch { }
                await Json(c, 500, Obj(("error", "URL no válida")));
                return;
            }
            if (p == "/api/asset" && metodo == "POST")
            {
                var b = new byte[12];
                Random.Shared.NextBytes(b);
                string id = string.Concat(b.Select(x => x.ToString("x2")));
                string f = Path.Combine(carpetaArchivos, id);
                try
                {
                    using (var fs = new FileStream(f, FileMode.Create, FileAccess.Write))
                        await c.Request.Body.CopyToAsync(fs);
                    string t = c.Request.ContentType ?? "application/octet-stream";
                    await File.WriteAllTextAsync(f + ".tipo", t, new UTF8Encoding(false));
                    await Json(c, 200, Obj(("id", id), ("url", "/_blob/" + id)));
                }
                catch
                {
                    await Json(c, 500, Obj(("code", "error"), ("message", "No se pudo guardar el archivo.")));
                }
                return;
            }
            if (p.StartsWith("/api/asset/") && metodo == "DELETE")
            {
                string id = SoloHex(p.Substring(11));
                string f = Path.Combine(carpetaArchivos, id);
                try { if (id.Length > 0) { File.Delete(f); File.Delete(f + ".tipo"); } } catch { }
                await Json(c, 200, Obj(("ok", true)));
                return;
            }
            await Json(c, 404, Obj(("error", "No existe")));
        }

        // ---------- Archivos subidos (con Range para audio y vídeo) ----------

        async Task ServirArchivo(HttpContext c, string p)
        {
            string id = SoloHex(p.Substring(7));
            string f = Path.Combine(carpetaArchivos, id);
            if (id.Length == 0 || !File.Exists(f)) { c.Response.StatusCode = 404; return; }
            string tipo = "application/octet-stream";
            try
            {
                var t = File.ReadAllText(f + ".tipo").Trim();
                if (t.Length > 0) tipo = t;
            }
            catch { }
            long tam = new FileInfo(f).Length;
            c.Response.ContentType = tipo;
            c.Response.Headers["Accept-Ranges"] = "bytes";
            string rg = c.Request.Headers["Range"].ToString();
            long a = 0, b = tam - 1;
            bool parcial = false;
            if (rg.StartsWith("bytes="))
            {
                var partes = rg.Substring(6).Split('-', 2);
                if (partes.Length == 2 && (partes[0].Length > 0 || partes[1].Length > 0))
                {
                    if (partes[0].Length > 0)
                    {
                        long.TryParse(partes[0], out a);
                        if (partes[1].Length > 0 && long.TryParse(partes[1], out var bb)) b = Math.Min(bb, tam - 1);
                    }
                    else
                    {
                        long.TryParse(partes[1], out var suf);
                        a = Math.Max(0, tam - suf);
                    }
                    if (a > b || a >= tam)
                    {
                        c.Response.StatusCode = 416;
                        c.Response.Headers["Content-Range"] = "bytes */" + tam;
                        return;
                    }
                    parcial = true;
                }
            }
            if (parcial)
            {
                c.Response.StatusCode = 206;
                c.Response.Headers["Content-Range"] = "bytes " + a + "-" + b + "/" + tam;
            }
            else
            {
                c.Response.StatusCode = 200;
                c.Response.Headers["Cache-Control"] = "private, max-age=31536000";
            }
            long largo = b - a + 1;
            c.Response.ContentLength = largo;
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.Seek(a, SeekOrigin.Begin);
            var buf = new byte[81920];
            long resta = largo;
            while (resta > 0)
            {
                int n = await fs.ReadAsync(buf, 0, (int)Math.Min(buf.Length, resta), c.RequestAborted);
                if (n <= 0) break;
                await c.Response.Body.WriteAsync(buf, 0, n, c.RequestAborted);
                resta -= n;
            }
        }

        // ---------- IA (Claude Code en segundo plano) ----------

        async Task AtenderIA(HttpContext c)
        {
            JsonObject o = null;
            try { o = JsonNode.Parse(await CuerpoTexto(c)) as JsonObject; } catch { }
            if (o == null) { await Json(c, 500, Obj(("code", "error"), ("message", "Petición no válida."))); return; }
            var ct = c.RequestAborted;
            try { await semaforoIA.WaitAsync(ct); } catch { return; }
            JsonObject res;
            try
            {
                if (ct.IsCancellationRequested) return;
                res = await Task.Run(() => PreguntarClaude(o, ct));
            }
            finally { semaforoIA.Release(); }
            if (ct.IsCancellationRequested) return;
            string txt = res["text"]?.GetValue<string>();
            if (txt != null && (txt.Length > 0 || res["code"] == null))
            {
                await Json(c, 200, Obj(("text", txt)));
                return;
            }
            string codigo = res["code"]?.GetValue<string>() ?? "error";
            await Json(c, codigo == "rate_limited" ? 429 : 500, res);
        }

        JsonObject PreguntarClaude(JsonObject b, CancellationToken cancelar)
        {
            var cl = ClaudeCode.Shared.Buscar();
            if (cl == null) return Obj(("code", "not_installed"), ("message", "Claude Code no está instalado."));
            string dir = Path.Combine(Path.GetTempPath(), "fabrica-apuntes-ia", "q-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string prompt = b["prompt"]?.GetValue<string>() ?? "";
                var ficheros = new List<string>();
                if (b["images"] is JsonArray imgs)
                {
                    int i = 0;
                    foreach (var imn in imgs.Take(12))
                    {
                        string s = null;
                        try { s = imn?.GetValue<string>(); } catch { }
                        if (s == null || !s.StartsWith("data:")) continue;
                        int rr = s.IndexOf(";base64,", StringComparison.Ordinal);
                        if (rr < 0) continue;
                        string mime = s.Substring(5, rr - 5);
                        byte[] dat;
                        try { dat = Convert.FromBase64String(s.Substring(rr + 8)); } catch { continue; }
                        string ext = mime.Contains("png") ? "png" : mime.Contains("webp") ? "webp" : mime.Contains("gif") ? "gif" : "jpg";
                        i++;
                        string f = Path.Combine(dir, "imagen-" + i + "." + ext);
                        File.WriteAllBytes(f, dat);
                        ficheros.Add(f);
                    }
                }
                if (ficheros.Count > 0)
                    prompt = "IMAGENES ADJUNTAS: antes de responder, abre cada una con la herramienta Read y trabaja con lo que veas:\n" + string.Join("\n", ficheros.Select(x => "- " + x)) + "\n\n" + prompt;
                bool web = false;
                try { web = b["web"]?.GetValue<bool>() ?? false; } catch { }
                var herr = new List<string>();
                if (ficheros.Count > 0) herr.Add("Read");
                if (web) { herr.Add("WebSearch"); herr.Add("WebFetch"); }
                string m = "default";
                try { m = b["model"]?.GetValue<string>() ?? "default"; } catch { }
                string modelo = m == "quick" ? "haiku" : m == "complex" ? "opus" : "sonnet";
                var args = new List<string> { "-p", "--output-format", "json", "--model", modelo, "--no-session-persistence",
                    "--setting-sources", "user", "--system-prompt", Sistema, "--tools", string.Join(",", herr) };
                if (herr.Count > 0) { args.Add("--allowedTools"); args.Add(string.Join(",", herr)); }
                if (ficheros.Count > 0) { args.Add("--add-dir"); args.Add(dir); }

                var res = Proc.Run(cl, args, stdin: prompt, cwd: dir, timeoutSec: 15 * 60, cancel: cancelar);
                if (res.TimedOut) return Obj(("code", "timeout"), ("message", "Claude tardó demasiado."));
                JsonObject j = null;
                var lineas = res.Out.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                if (lineas.Count > 0)
                {
                    try { j = JsonNode.Parse(lineas[lineas.Count - 1]) as JsonObject; } catch { }
                }
                string todo = (res.Out + " " + res.Err).ToLowerInvariant();
                bool esError = false;
                try { esError = j?["is_error"]?.GetValue<bool>() ?? false; } catch { }
                string resultado = null;
                try { resultado = j?["result"]?.GetValue<string>(); } catch { }
                if (j != null && !esError && resultado != null) return Obj(("text", resultado));
                bool limite = new[] { "limit", "usage", "quota", "overloaded", "rate" }.Any(x => todo.Contains(x));
                bool aviso = new[] { "reach", "exceed", "hit", "resets", "try again" }.Any(x => todo.Contains(x));
                if (limite && aviso)
                    return Obj(("code", "rate_limited"), ("message", resultado ?? "Has alcanzado el límite de uso de tu plan."));
                bool sesion = new[] { "login", "log in", "not logged", "authenticat", "credential", "401" }.Any(x => todo.Contains(x));
                if (sesion) return Obj(("code", "not_granted"), ("message", "Claude Code no tiene la sesión iniciada."));
                string msg = resultado ?? (string.IsNullOrEmpty(res.Err) ? res.Out : res.Err);
                if (string.IsNullOrEmpty(msg)) msg = "Error " + res.Status;
                if (msg.Length > 400) msg = msg.Substring(0, 400);
                return Obj(("code", "error"), ("message", msg));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
