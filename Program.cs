using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

var cfg = ExporterConfig.Load("appsettings.json");
var cts = new CancellationTokenSource();
var collector = new MetricsCollector(cfg);
_ = Task.Run(() => collector.LoopAsync(cts.Token));

var listenUrl = cfg.ListenUrl.EndsWith("/") ? cfg.ListenUrl : cfg.ListenUrl + "/";
var listener = new HttpListener();
listener.Prefixes.Add(listenUrl);
try { listener.Start(); }
catch (HttpListenerException ex)
{
    Console.WriteLine("HttpListener failed: " + ex.Message);
    Console.WriteLine($@"Run once as admin: netsh http add urlacl url={listenUrl} user=Everyone");
    return;
}

Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); listener.Stop(); };
Console.WriteLine($"Listening on {listenUrl}");

while (listener.IsListening)
{
    HttpListenerContext ctx;
    try { ctx = await listener.GetContextAsync(); } catch when (!listener.IsListening) { break; }
    _ = Task.Run(async () =>
    {
        var res = ctx.Response;
        try
        {
            var req = ctx.Request;
            res.Headers["Cache-Control"] = "no-store";

            if (req.Url is not null && req.Url.AbsolutePath.Equals("/metrics", StringComparison.OrdinalIgnoreCase))
            {
                var payload = ToLf(collector.GetPayload());
                res.ContentType = "text/plain; version=0.0.4; charset=utf-8";
                using var writer = new StreamWriter(res.OutputStream, new UTF8Encoding(false));
                writer.NewLine = "\n";
                writer.AutoFlush = true;
                await writer.WriteAsync(payload);
            }
            else if (req.Url is not null && req.Url.AbsolutePath.Equals("/debug", StringComparison.OrdinalIgnoreCase))
            {
                var payload = ToLf(collector.GetDebug());
                res.ContentType = "text/plain; charset=utf-8";
                using var writer = new StreamWriter(res.OutputStream, new UTF8Encoding(false));
                writer.NewLine = "\n";
                writer.AutoFlush = true;
                await writer.WriteAsync(payload);
            }
            else
            {
                res.StatusCode = 404;
                var payload = "not found\n";
                res.ContentType = "text/plain; charset=utf-8";
                using var writer = new StreamWriter(res.OutputStream, new UTF8Encoding(false));
                writer.NewLine = "\n";
                writer.AutoFlush = true;
                await writer.WriteAsync(payload);
            }
        }
        catch (Exception ex)
        {
            try
            {
                res.StatusCode = 500;
                var msg = ToLf($"error: {ex.GetType().Name}: {ex.Message}\n");
                using var writer = new StreamWriter(res.OutputStream, new UTF8Encoding(false));
                writer.NewLine = "\n";
                writer.AutoFlush = true;
                await writer.WriteAsync(msg);
            }
            catch { }
        }
        finally
        {
            try { res.OutputStream.Close(); } catch { }
        }
    });
}

static string ToLf(string s)
{
    if (string.IsNullOrEmpty(s)) return "\n";
    s = s.Replace("\r\n", "\n").Replace("\r", "\n");
    if (!s.EndsWith("\n")) s += "\n";
    return s;
}

public sealed class ExporterConfig
{
    public string ListenUrl { get; init; } = "http://127.0.0.1:9108/";
    public int PollSeconds { get; init; } = 60;
    public int LookbackMinutes { get; init; } = 15;
    public string SqlConnectionString { get; init; } = "";
    public string MetricPrefix { get; init; } = "veeam_server"; // new

    public static ExporterConfig Load(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Root>(json)?.Exporter ?? new ExporterConfig();
        }
        catch { return new ExporterConfig(); }
    }
    private sealed class Root { public ExporterConfig Exporter { get; set; } = new(); }
}

public record MRow(string Host, string Key, string Inst, DateTime TsUtc, long Value);

public sealed class MetricsCollector
{
    private readonly ExporterConfig _cfg;
    private readonly object _lock = new();
    private readonly string _p;               // sanitized prefix
    private string _last = "# no data yet\n";
    private int _up = 0;
    private string _lastError = "none";
    private DateTimeOffset _lastSuccess = DateTimeOffset.FromUnixTimeSeconds(0);

    public MetricsCollector(ExporterConfig cfg)
    {
        _cfg = cfg;
        _p = SanitizePrefix(cfg.MetricPrefix);
    }

    // Build metric name: <prefix>_<suffix>
    private string M(string suffix) => $"{_p}_{suffix}";

    public string GetPayload()
    {
        lock (_lock)
        {
            var sb = new StringBuilder();

            // Top-level exporter health
            sb.Append($"# HELP {M("up")} Exporter health\n");
            sb.Append($"# TYPE {M("up")} gauge\n");
            sb.Append($"{M("up")} {_up}\n");

            sb.Append($"# HELP {M("last_scrape_success_seconds")} Unix time of last successful scrape\n");
            sb.Append($"# TYPE {M("last_scrape_success_seconds")} gauge\n");
            sb.Append($"{M("last_scrape_success_seconds")} {_lastSuccess.ToUnixTimeSeconds()}\n");

            var err = _lastError ?? "none";
            if (err.Length > 300) err = err[..300];
            sb.Append($"# HELP {M("last_scrape_error_info")} Last scrape error info (0 means none)\n");
            sb.Append($"# TYPE {M("last_scrape_error_info")} gauge\n");
            sb.Append($"{M("last_scrape_error_info")}{{message=\"{Esc(err)}\"}} 1\n");

            // Collected metrics
            sb.Append(_last);
            return sb.ToString();
        }
    }

    public string GetDebug()
    {
        lock (_lock)
        {
            return $"UP: {_up}\nLAST_SUCCESS_UTC: {_lastSuccess:O}\nLAST_ERROR:\n{_lastError}\n";
        }
    }

    public async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await PollOnceAsync(ct);
            try { await Task.Delay(TimeSpan.FromSeconds(_cfg.PollSeconds), ct); } catch { }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        try
        {
            var rows = await QueryAsync(ct);

            var sb = new StringBuilder();
            // HELP/TYPE once per family using the prefix
            sb.Append($"# HELP {M("net_bytes_sent_per_sec")} Network bytes sent per second\n");
            sb.Append($"# TYPE {M("net_bytes_sent_per_sec")} gauge\n");
            sb.Append($"# HELP {M("disk_bytes_per_sec")} Disk bytes per second\n");
            sb.Append($"# TYPE {M("disk_bytes_per_sec")} gauge\n");
            sb.Append($"# HELP {M("cpu_usage_percent")} CPU usage percent\n");
            sb.Append($"# TYPE {M("cpu_usage_percent")} gauge\n");
            sb.Append($"# HELP {M("memory_used_bytes")} Memory used in bytes\n");
            sb.Append($"# TYPE {M("memory_used_bytes")} gauge\n");

            foreach (var r in rows)
            {
                long tsMs = new DateTimeOffset(DateTime.SpecifyKind(r.TsUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
                string host = Esc(r.Host);
                string inst = Esc(r.Inst);

                switch (r.Key)
                {
                    case "net_bytes_sent_per_sec":
                        sb.Append($"{M("net_bytes_sent_per_sec")}{{host=\"{host}\"");
                        if (!string.IsNullOrEmpty(inst)) sb.Append($",iface=\"{inst}\"");
                        sb.Append($"}} {r.Value} {tsMs}\n");
                        break;

                    case "disk_bytes_per_sec":
                        sb.Append($"{M("disk_bytes_per_sec")}{{host=\"{host}\"");
                        if (!string.IsNullOrEmpty(inst)) sb.Append($",disk=\"{inst}\"");
                        sb.Append($"}} {r.Value} {tsMs}\n");
                        break;

                    case "cpu_usage_pct":
                        sb.Append($"{M("cpu_usage_percent")}{{host=\"{host}\"}} {r.Value} {tsMs}\n");
                        break;

                    case "memory_used_bytes":
                        sb.Append($"{M("memory_used_bytes")}{{host=\"{host}\"}} {r.Value} {tsMs}\n");
                        break;
                }
            }

            lock (_lock)
            {
                _last = sb.ToString();
                _up = 1;
                _lastError = "none";
                _lastSuccess = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _up = 0;
                _lastError = ex.GetType().Name + ": " + ex.Message;
            }
            Console.Error.WriteLine($"[scrape-error] {ex}");
        }
    }

    private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

    // Keep prefix valid for Prometheus metric names
    private static string SanitizePrefix(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) raw = "exporter";
        // letters, digits, underscore, colon; must start with letter or underscore
        var sb = new StringBuilder();
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == ':') sb.Append(ch);
            else sb.Append('_');
        }
        var s = sb.ToString();
        if (!(char.IsLetter(s, 0) || s[0] == '_')) s = "_" + s;
        return s;
    }

    private async Task<List<MRow>> QueryAsync(CancellationToken ct)
    {
        const string sql = @"
WITH metric_map AS (
  SELECT 'bp.serv.networkInterface.bytesSentPerSec.average' AS string_id, 'net_bytes_sent_per_sec' AS counter_key
  UNION ALL SELECT 'bp.serv.physicalDisk.BytesPerSec.average',           'disk_bytes_per_sec'
  UNION ALL SELECT 'bp.serv.processorInformation.processorTimePct.average','cpu_usage_pct'
  UNION ALL SELECT 'bp.serv.memory.usedBytes.average',                   'memory_used_bytes'
),
base AS (
  SELECT
      COALESCE(NULLIF(LTRIM(RTRIM(e.host_name)), ''), NULLIF(LTRIM(RTRIM(e.name)), '')) AS host_name,
      m.counter_key,
      NULLIF(LTRIM(RTRIM(i.name)), '') AS instance_name,
      s.[timestamp] AS ts_utc,
      CAST(s.value AS BIGINT) AS value,
      e.id AS entity_id,
      s.instance_id,
      s.counter_id,
      ROW_NUMBER() OVER (
        PARTITION BY e.id, s.instance_id, s.counter_id
        ORDER BY s.[timestamp] DESC
      ) AS rn
  FROM monitor.PerfSample s
  JOIN monitor.PerfInstance     i ON i.id = s.instance_id
  JOIN monitor.PerfCounterInfo  c ON c.id = s.counter_id
  JOIN monitor.Entity           e ON e.id = i.entity_id
  JOIN metric_map               m ON m.string_id = c.string_id
  WHERE s.[timestamp] >= DATEADD(minute, -@lookback, SYSUTCDATETIME())
)
SELECT host_name, counter_key, instance_name, ts_utc, value
FROM base
WHERE rn = 1
  AND host_name IS NOT NULL;";

        var list = new List<MRow>();
        using var conn = new SqlConnection(_cfg.SqlConnectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new SqlParameter("@lookback", System.Data.SqlDbType.Int) { Value = Math.Max(1, _cfg.LookbackMinutes) });

        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            string host = r.GetString(0);
            string key  = r.GetString(1);
            string inst = r.IsDBNull(2) ? "" : r.GetString(2);
            DateTime ts = r.GetDateTime(3);
            long val    = Convert.ToInt64(r.GetValue(4));
            list.Add(new MRow(host, key, inst, ts, val));
        }
        return list;
    }
}
