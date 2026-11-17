using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace FabricaDeApuntes
{
    // Actualización automática de la pantalla (index.html) desde internet. Igual que en Mac.
    public class Actualizador
    {
        public const string UrlActualizacion = "https://pablomarcpa-png.github.io/fabrica-de-apuntes/actualizacion/";
        readonly string datos;
        readonly string recursos;
        string DirApp { get { return Path.Combine(datos, "app"); } }

        public Actualizador(string datos, string recursos)
        {
            this.datos = datos;
            this.recursos = recursos;
        }

        static long LeerNum(string p)
        {
            try { return long.Parse(File.ReadAllText(p).Trim()); } catch { return 0; }
        }

        // Si el instalador trae una pantalla más nueva que la descargada, se descarta la descargada.
        public void LimpiarObsoleta()
        {
            try
            {
                long empaquetada = LeerNum(Path.Combine(recursos, "version.txt"));
                long descargada = LeerNum(Path.Combine(DirApp, "version.txt"));
                if (descargada < empaquetada)
                {
                    File.Delete(Path.Combine(DirApp, "index.html"));
                    File.Delete(Path.Combine(DirApp, "version.txt"));
                }
            }
            catch { }
        }

        public void Comprobar()
        {
            Task.Run(async () =>
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                    var txt = await http.GetStringAsync(UrlActualizacion + "version.json?t=" + DateTime.UtcNow.Ticks);
                    var j = JsonNode.Parse(txt) as JsonObject;
                    long v = j?["version"]?.GetValue<long>() ?? 0;
                    string sha = j?["sha256"]?.GetValue<string>();
                    long actual = Math.Max(LeerNum(Path.Combine(DirApp, "version.txt")), LeerNum(Path.Combine(recursos, "version.txt")));
                    if (v <= actual || string.IsNullOrEmpty(sha)) return;
                    var d = await http.GetByteArrayAsync(UrlActualizacion + "index.html?t=" + DateTime.UtcNow.Ticks);
                    if (d.Length < 100000) return;
                    string t = Encoding.UTF8.GetString(d);
                    if (!(t.Contains("<html") || t.Contains("<!doctype") || t.Contains("<!DOCTYPE"))) return;
                    string h = Convert.ToHexString(SHA256.HashData(d)).ToLowerInvariant();
                    if (h != sha.ToLowerInvariant()) return;
                    Directory.CreateDirectory(DirApp);
                    string tmp = Path.Combine(DirApp, "index.html.nuevo");
                    File.WriteAllBytes(tmp, d);
                    string dest = Path.Combine(DirApp, "index.html");
                    string ant = Path.Combine(DirApp, "index.html.anterior");
                    if (File.Exists(ant)) File.Delete(ant);
                    if (File.Exists(dest)) File.Move(dest, ant);
                    File.Move(tmp, dest);
                    File.WriteAllText(Path.Combine(DirApp, "version.txt"), v.ToString());
                }
                catch { }
            });
        }
    }
}
