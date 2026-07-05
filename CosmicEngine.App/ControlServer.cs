using System;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace CosmicEngine.App
{
    public static class ControlServer
    {
        private static Thread? _thread;
        private static HttpListener? _listener;

        public static void Start()
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add("http://localhost:8080/");
            _listener.Start();

            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "ControlServer"
            };
            _thread.Start();

            Console.WriteLine("Control panel: http://localhost:8080");
        }

        public static void Stop()
        {
            _listener?.Stop();
        }

        private static void Loop()
        {
            while (_listener!.IsListening)
            {
                try
                {
                    var ctx = _listener.GetContext();
                    Handle(ctx);
                }
                catch { }
            }
        }

        private static void Handle(HttpListenerContext ctx)
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";

            if (ctx.Request.HttpMethod == "POST" && path == "/set")
            {
                using var reader = new System.IO.StreamReader(ctx.Request.InputStream);
                string body = reader.ReadToEnd();
                var doc = JsonDocument.Parse(body);

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    float val = prop.Value.GetSingle();
                    switch (prop.Name)
                    {
                        case "Smoothing":      Tuning.Smoothing      = val; break;
                        case "BassFloor":      Tuning.BassFloor      = val; break;
                        case "MidFloor":       Tuning.MidFloor       = val; break;
                        case "TrebleFloor":    Tuning.TrebleFloor    = val; break;
                        case "BassMax":        Tuning.BassMax        = val; break;
                        case "MidMax":         Tuning.MidMax         = val; break;
                        case "TrebleMax":      Tuning.TrebleMax      = val; break;
                        case "BassBrightness": Tuning.BassBrightness = val; break;
                        case "DimLevel":       Tuning.DimLevel       = val; break;
                    }
                }

                Respond(ctx, 200, "{}");
                return;
            }

            if (path == "/values")
            {
                string json = JsonSerializer.Serialize(new
                {
                    Tuning.Smoothing,
                    Tuning.BassFloor,
                    Tuning.MidFloor,
                    Tuning.TrebleFloor,
                    Tuning.BassMax,
                    Tuning.MidMax,
                    Tuning.TrebleMax,
                    Tuning.BassBrightness,
                    Tuning.DimLevel
                });
                Respond(ctx, 200, json, "application/json");
                return;
            }

            Respond(ctx, 200, Html(), "text/html");
        }

        private static void Respond(HttpListenerContext ctx, int code, string body, string mime = "application/json")
        {
            byte[] buf = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = mime;
            ctx.Response.ContentLength64 = buf.Length;
            ctx.Response.OutputStream.Write(buf, 0, buf.Length);
            ctx.Response.OutputStream.Close();
        }

        private static string Html() => @"<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'>
<title>The Deepest Space</title>
<style>
  body { background: #050308; color: #ccc; font-family: monospace; padding: 30px; max-width: 600px; margin: 0 auto; }
  h1 { color: #ff2266; letter-spacing: 2px; }
  h3 { color: #22ddff; margin-top: 28px; margin-bottom: 8px; }
  .row { display: flex; align-items: center; margin: 10px 0; gap: 12px; }
  label { width: 170px; font-size: 13px; color: #aaa; }
  input[type=range] { flex: 1; accent-color: #ff2266; }
  .val { width: 48px; text-align: right; font-size: 13px; color: #ff2266; }
  .reset { margin-top: 28px; background: #222; color: #ff2266; border: 1px solid #ff2266;
           padding: 8px 20px; cursor: pointer; font-family: monospace; letter-spacing: 1px; }
  .reset:hover { background: #ff2266; color: #000; }
</style>
</head>
<body>
<h1>THE DEEPEST SPACE</h1>
<p style='color:#666;font-size:12px'>A new pattern generates every time you launch the app. These controls tune how the audio drives it.</p>

<h3>SMOOTHING</h3>
<div class='row'><label>Smoothing</label><input type='range' id='Smoothing' min='0' max='0.95' step='0.01'><span class='val' id='Smoothing_v'></span></div>

<h3>AUDIO FLOORS (silence threshold)</h3>
<div class='row'><label>Bass Floor</label><input type='range' id='BassFloor' min='0' max='0.3' step='0.001'><span class='val' id='BassFloor_v'></span></div>
<div class='row'><label>Mid Floor</label><input type='range' id='MidFloor' min='0' max='0.05' step='0.0001'><span class='val' id='MidFloor_v'></span></div>
<div class='row'><label>Treble Floor</label><input type='range' id='TrebleFloor' min='0' max='0.01' step='0.00001'><span class='val' id='TrebleFloor_v'></span></div>

<h3>AUDIO MAXIMUMS (full scale point)</h3>
<div class='row'><label>Bass Max</label><input type='range' id='BassMax' min='1' max='100' step='0.5'><span class='val' id='BassMax_v'></span></div>
<div class='row'><label>Mid Max</label><input type='range' id='MidMax' min='1' max='50' step='0.5'><span class='val' id='MidMax_v'></span></div>
<div class='row'><label>Treble Max</label><input type='range' id='TrebleMax' min='0.05' max='2' step='0.01'><span class='val' id='TrebleMax_v'></span></div>

<h3>OVERALL BRIGHTNESS</h3>
<div class='row'><label>Dim Level</label><input type='range' id='DimLevel' min='0' max='1' step='0.01'><span class='val' id='DimLevel_v'></span></div>
<div class='row'><label>Bass Brightness</label><input type='range' id='BassBrightness' min='0' max='3' step='0.01'><span class='val' id='BassBrightness_v'></span></div>

<button class='reset' onclick='resetDefaults()'>RESET DEFAULTS</button>

<script>
const defaults = {
  Smoothing: 0.40, BassFloor: 0.08, MidFloor: 0.004, TrebleFloor: 0.001,
  BassMax: 35, MidMax: 12, TrebleMax: 0.4,
  BassBrightness: 1.20, DimLevel: 0.20
};

const sliders = document.querySelectorAll('input[type=range]');

function send(key, val) {
  fetch('/set', { method: 'POST', body: JSON.stringify({ [key]: parseFloat(val) }) });
}

sliders.forEach(s => {
  s.addEventListener('input', () => {
    document.getElementById(s.id + '_v').textContent = parseFloat(s.value).toFixed(3);
    send(s.id, s.value);
  });
});

function resetDefaults() {
  sliders.forEach(s => {
    if (defaults[s.id] !== undefined) {
      s.value = defaults[s.id];
      document.getElementById(s.id + '_v').textContent = parseFloat(s.value).toFixed(3);
      send(s.id, s.value);
    }
  });
}

fetch('/values').then(r => r.json()).then(vals => {
  sliders.forEach(s => {
    if (vals[s.id] !== undefined) {
      s.value = vals[s.id];
      document.getElementById(s.id + '_v').textContent = parseFloat(s.value).toFixed(3);
    }
  });
});
</script>
</body>
</html>";
    }
}
