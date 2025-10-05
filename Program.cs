using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
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
    public int LookbackMinutes { get; init; } = 65;
    public string SqlConnectionString { get; init; } = "";
    public string MetricPrefix { get; init; } = "vone_exporter";

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

public record MRow(string Host, string Role, string Key, string InstLabel, string Inst, DateTime TsUtc, double Value);

public sealed class MetricsCollector
{
    private readonly ExporterConfig _cfg;
    private readonly object _lock = new();
    private readonly string _p;
    private string _last = "# no data yet\n";
    private int _up = 0;
    private string _lastError = "none";
    private DateTimeOffset _lastSuccess = DateTimeOffset.FromUnixTimeSeconds(0);

    private static readonly (string suffix, string help, string instLbl)[] MetricDefsArray = new[]
    {
        ("cpu_usage_percent", "CPU usage percent", ""),
        ("memory_available_bytes", "Available memory in bytes", ""),
        ("memory_used_bytes", "Used memory in bytes", ""),
        ("memory_usage_percent", "Memory usage percent", ""),
        ("disk_bytes_per_sec", "Disk bytes per second", "disk"),
        ("disk_read_bytes_per_sec", "Disk read bytes per second", "disk"),
        ("disk_write_bytes_per_sec", "Disk write bytes per second", "disk"),
        ("net_bytes_sent_per_sec", "Network bytes sent per second", "iface"),
        ("net_bytes_received_per_sec", "Network bytes received per second", "iface"),
        ("net_bytes_total_per_sec", "Network total bytes per second", "iface"),
        ("repository_used_bytes", "Repository used bytes", ""),
        ("repository_capacity_bytes", "Repository capacity bytes", ""),
        ("repository_free_bytes", "Repository free bytes", ""),
        ("repository_file_backups_bytes", "Repository file backups bytes", ""),
        ("repository_image_backups_bytes", "Repository image backups bytes", ""),
        ("repository_app_backups_bytes", "Repository application backups bytes", ""),
        ("repository_slots_used", "Repository used task slots", ""),
        ("repository_slots_max", "Repository maximum task slots", ""),
        ("proxy_slots_used", "Proxy used task slots", ""),
        ("proxy_slots_max", "Proxy maximum task slots", ""),
        ("cdp_sla_percent", "CDP SLA percent", ""),
        ("cdp_max_delay_ms", "CDP maximum delay in milliseconds", ""),
        ("cdp_proxy_cache_used_bytes", "CDP proxy cache used bytes", ""),
        ("cdp_proxy_cache_capacity_bytes", "CDP proxy cache capacity bytes", ""),
        ("cdp_proxy_cache_used_percent", "CDP proxy cache used percent", ""),
        ("object_storage_used_bytes", "Object storage used bytes", ""),
        ("object_storage_free_bytes", "Object storage free bytes", ""),
        ("archiverep_used_bytes", "Archive repository used bytes", ""),
        ("archiverep_capacity_bytes", "Archive repository capacity bytes", ""),
        ("externalrep_used_bytes", "External repository used bytes", ""),
        ("veeam_one_server_cpu_usage_percent", "Veeam ONE server CPU usage percent", ""),
        ("veeam_one_server_memory_usage_percent", "Veeam ONE server memory usage percent", ""),
        ("veeam_one_server_memory_pressure_percent", "Veeam ONE server memory pressure percent", "")
    };

    private static readonly Dictionary<string, (string suffix, string help, string instLbl)> MetricDefs =
        MetricDefsArray.ToDictionary(x => x.suffix, x => x);

    public MetricsCollector(ExporterConfig cfg)
    {
        _cfg = cfg;
        _p = SanitizePrefix(cfg.MetricPrefix);
    }

    private string M(string suffix) => $"{_p}_{suffix}";

    public string GetPayload()
    {
        lock (_lock)
        {
            var sb = new StringBuilder();

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

            var presentKeys = rows.Select(r => r.Key).Distinct().ToArray();
            foreach (var key in presentKeys)
            {
                if (!MetricDefs.TryGetValue(key, out var def)) continue;
                sb.Append($"# HELP {M(def.suffix)} {def.help}\n");
                sb.Append($"# TYPE {M(def.suffix)} gauge\n");
            }

            foreach (var r in rows)
            {
                long tsMs = new DateTimeOffset(DateTime.SpecifyKind(r.TsUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
                string host = Esc(r.Host);
                string role = Esc(r.Role);
                string inst = Esc(r.Inst);

                var labels = new StringBuilder();
                labels.Append($"host=\"{host}\",role=\"{role}\"");
                if (!string.IsNullOrEmpty(r.InstLabel) && !string.IsNullOrEmpty(inst))
                    labels.Append($",{r.InstLabel}=\"{inst}\"");

                sb.Append($"{M(r.Key)}{{{labels}}} {r.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)} {tsMs}\n");
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

    private static string SanitizePrefix(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) raw = "exporter";
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
    WITH metric_map (string_id, counter_key, inst_lbl) AS (
    -- map string_id -> exporter key + optional instance label
    SELECT 'bp.em.memory.availableBytes.average','memory_available_bytes',NULL UNION ALL
    SELECT 'bp.em.memory.usedBytes.average','memory_used_bytes',NULL UNION ALL
    SELECT 'bp.em.networkInterface.bytesSentPerSec.average','net_bytes_sent_per_sec','iface' UNION ALL
    SELECT 'bp.em.physicalDisk.BytesPerSec.average','disk_bytes_per_sec','disk' UNION ALL
    SELECT 'bp.em.processorInformation.processorTimePct.average','cpu_usage_percent',NULL UNION ALL

    SELECT 'bp.serv.memory.availableBytes.average','memory_available_bytes',NULL UNION ALL
    SELECT 'bp.serv.memory.usedBytes.average','memory_used_bytes',NULL UNION ALL
    SELECT 'bp.serv.networkInterface.bytesSentPerSec.average','net_bytes_sent_per_sec','iface' UNION ALL
    SELECT 'bp.serv.physicalDisk.BytesPerSec.average','disk_bytes_per_sec','disk' UNION ALL
    SELECT 'bp.serv.processorInformation.processorTimePct.average','cpu_usage_percent',NULL UNION ALL
    SELECT 'bp.serv.cdp.slaCompliance.latest','cdp_sla_percent',NULL UNION ALL
    SELECT 'bp.serv.cdp.MaxDelay.latest','cdp_max_delay_ms',NULL UNION ALL

    SELECT 'bp.rep.memory.availableBytes.average','memory_available_bytes',NULL UNION ALL
    SELECT 'bp.rep.memory.usedBytes.average','memory_used_bytes',NULL UNION ALL
    SELECT 'bp.rep.networkInterface.bytesSentPerSec.average','net_bytes_sent_per_sec','iface' UNION ALL
    SELECT 'bp.rep.physicalDisk.BytesPerSec.average','disk_bytes_per_sec','disk' UNION ALL
    SELECT 'bp.rep.processorInformation.processorTimePct.average','cpu_usage_percent',NULL UNION ALL
    SELECT 'bp.rep.concurrentJobNum.latest','repository_slots_used',NULL UNION ALL
    SELECT 'bp.rep.concurrentJobMax.latest','repository_slots_max',NULL UNION ALL

    SELECT 'bp.repository.usedSpace.latest','repository_used_bytes',NULL UNION ALL
    SELECT 'bp.repository.capacity.latest','repository_capacity_bytes',NULL UNION ALL
    SELECT 'bp.repository.fileBackupsSize.latest','repository_file_backups_bytes',NULL UNION ALL
    SELECT 'bp.repository.imageBackupsSize.latest','repository_image_backups_bytes',NULL UNION ALL
    SELECT 'bp.repository.appBackupsSize.latest','repository_app_backups_bytes',NULL UNION ALL

    SELECT 'bp.prx.memory.availableBytes.average','memory_available_bytes',NULL UNION ALL
    SELECT 'bp.prx.memory.usedBytes.average','memory_used_bytes',NULL UNION ALL
    SELECT 'bp.prx.networkInterface.bytesSentPerSec.average','net_bytes_sent_per_sec','iface' UNION ALL
    SELECT 'bp.prx.physicalDisk.BytesPerSec.average','disk_bytes_per_sec','disk' UNION ALL
    SELECT 'bp.prx.processorInformation.processorTimePct.average','cpu_usage_percent',NULL UNION ALL
    SELECT 'bp.prx.concurrentJobNum.latest','proxy_slots_used',NULL UNION ALL
    SELECT 'bp.prx.concurrentJobMax.latest','proxy_slots_max',NULL UNION ALL
    SELECT 'bp.prx.cdpcacheusage.latest','cdp_proxy_cache_used_bytes',NULL UNION ALL
    SELECT 'bp.prx.cdpcachecapacity.latest','cdp_proxy_cache_capacity_bytes',NULL UNION ALL
    SELECT 'bp.prx.cdpcacheusagepercent.latest','cdp_proxy_cache_used_percent',NULL UNION ALL

    SELECT 'bp.wac.memory.availableBytes.average','memory_available_bytes',NULL UNION ALL
    SELECT 'bp.wac.memory.usedBytes.average','memory_used_bytes',NULL UNION ALL
    SELECT 'bp.wac.networkInterface.bytesSentPerSec.average','net_bytes_sent_per_sec','iface' UNION ALL
    SELECT 'bp.wac.physicalDisk.BytesPerSec.average','disk_bytes_per_sec','disk' UNION ALL
    SELECT 'bp.wac.processorInformation.processorTimePct.average','cpu_usage_percent',NULL UNION ALL

    SELECT 'bp.tapeprx.memory.availableBytes.average','memory_available_bytes',NULL UNION ALL
    SELECT 'bp.tapeprx.memory.usedBytes.average','memory_used_bytes',NULL UNION ALL
    SELECT 'bp.tapeprx.networkInterface.bytesSentPerSec.average','net_bytes_sent_per_sec','iface' UNION ALL
    SELECT 'bp.tapeprx.physicalDisk.BytesPerSec.average','disk_bytes_per_sec','disk' UNION ALL
    SELECT 'bp.tapeprx.processorInformation.processorTimePct.average','cpu_usage_percent',NULL UNION ALL

    SELECT 'vbm.srv.cpu.usage','cpu_usage_percent',NULL UNION ALL
    SELECT 'vbm.srv.mem.available','memory_available_bytes',NULL UNION ALL
    SELECT 'vbm.srv.mem.usage','memory_used_bytes',NULL UNION ALL
    SELECT 'vbm.srv.disk.readBytesPerSec','disk_read_bytes_per_sec','disk' UNION ALL
    SELECT 'vbm.srv.disk.writeBytesPerSec','disk_write_bytes_per_sec','disk' UNION ALL
    SELECT 'vbm.srv.disk.bytesPerSec','disk_bytes_per_sec','disk' UNION ALL
    SELECT 'vbm.srv.net.bytesPerSec','net_bytes_total_per_sec','iface' UNION ALL
    SELECT 'vbm.srv.net.bytesReceivedPersec','net_bytes_received_per_sec','iface' UNION ALL
    SELECT 'vbm.srv.net.bytesSentPersec','net_bytes_sent_per_sec','iface' UNION ALL

    SELECT 'vbm.prx.cpu.usage','cpu_usage_percent',NULL UNION ALL
    SELECT 'vbm.prx.mem.available','memory_available_bytes',NULL UNION ALL
    SELECT 'vbm.prx.mem.usage','memory_used_bytes',NULL UNION ALL
    SELECT 'vbm.prx.mem.usage.percent','memory_usage_percent',NULL UNION ALL
    SELECT 'vbm.prx.disk.readBytesPerSec','disk_read_bytes_per_sec','disk' UNION ALL
    SELECT 'vbm.prx.disk.writeBytesPerSec','disk_write_bytes_per_sec','disk' UNION ALL
    SELECT 'vbm.prx.disk.bytesPerSec','disk_bytes_per_sec','disk' UNION ALL
    SELECT 'vbm.prx.net.bytesPerSec','net_bytes_total_per_sec','iface' UNION ALL
    SELECT 'vbm.prx.net.bytesReceivedPersec','net_bytes_received_per_sec','iface' UNION ALL
    SELECT 'vbm.prx.net.bytesSentPersec','net_bytes_sent_per_sec','iface' UNION ALL

    SELECT 'vbm.rep.usedSpace.latest','repository_used_bytes',NULL UNION ALL
    SELECT 'vbm.rep.freeSpace.latest','repository_free_bytes',NULL UNION ALL
    SELECT 'vbm.objrep.usedSpace.latest','object_storage_used_bytes',NULL UNION ALL
    SELECT 'vbm.objrep.freeSpace.latest','object_storage_free_bytes',NULL UNION ALL

    SELECT 'bp.archiverep.usedSpace.latest','archiverep_used_bytes',NULL UNION ALL
    SELECT 'bp.archiverep.capacity.latest','archiverep_capacity_bytes',NULL UNION ALL
    SELECT 'bp.externalrep.usedSpace.latest','externalrep_used_bytes',NULL UNION ALL

    SELECT 'veeam.srv.cpu.usage.average','veeam_one_server_cpu_usage_percent',NULL UNION ALL
    SELECT 'veeam.srv.mem.usage.average','veeam_one_server_memory_usage_percent',NULL UNION ALL
    SELECT 'veeam.srv.mem.pressure.average','veeam_one_server_memory_pressure_percent',NULL
    ),
    base AS (
    SELECT
        COALESCE(NULLIF(LTRIM(RTRIM(e.host_name)), ''), NULLIF(LTRIM(RTRIM(e.name)), '')) AS host_name,
        CASE
            WHEN c.string_id LIKE 'bp.serv.%'        THEN 'serv'
            WHEN c.string_id LIKE 'bp.em.%'          THEN 'em'
            WHEN c.string_id LIKE 'bp.rep.%'         THEN 'rep'
            WHEN c.string_id LIKE 'bp.repository.%'  THEN 'repository'
            WHEN c.string_id LIKE 'bp.prx.%'         THEN 'prx'
            WHEN c.string_id LIKE 'bp.wac.%'         THEN 'wac'
            WHEN c.string_id LIKE 'bp.tapeprx.%'     THEN 'tapeprx'
            WHEN c.string_id LIKE 'bp.archiverep.%'  THEN 'archiverep'
            WHEN c.string_id LIKE 'bp.externalrep.%' THEN 'externalrep'
            WHEN c.string_id LIKE 'vbm.srv.%'        THEN 'vbm_srv'
            WHEN c.string_id LIKE 'vbm.prx.%'        THEN 'vbm_prx'
            WHEN c.string_id LIKE 'vbm.rep.%'        THEN 'vbm_rep'
            WHEN c.string_id LIKE 'vbm.objrep.%'     THEN 'vbm_objrep'
            WHEN c.string_id LIKE 'veeam.srv.%'      THEN 'veeam_srv'
            ELSE 'other'
        END AS role,
        m.counter_key,
        m.inst_lbl,
        NULLIF(LTRIM(RTRIM(i.name)), '') AS instance_name,
        s.[timestamp] AS ts_utc,
        CONVERT(float, s.value) / NULLIF(CONVERT(float, c.divider), 0) AS value_scaled,
        ROW_NUMBER() OVER (
            PARTITION BY e.id, s.instance_id, s.counter_id
            ORDER BY
            CASE
                WHEN c.rt_interval     > 0 AND s.interval = c.rt_interval     THEN 0
                WHEN c.level2_interval > 0 AND s.interval = c.level2_interval THEN 1
                WHEN c.level3_interval > 0 AND s.interval = c.level3_interval THEN 2
                WHEN c.level4_interval > 0 AND s.interval = c.level4_interval THEN 3
                ELSE 9
            END,
            s.[timestamp] DESC
        ) AS rn
    FROM monitor.PerfSample s
    JOIN monitor.PerfInstance     i ON i.id = s.instance_id
    JOIN monitor.PerfCounterInfo  c ON c.id = s.counter_id
    JOIN monitor.Entity           e ON e.id = i.entity_id
    JOIN metric_map               m ON m.string_id = c.string_id
    WHERE s.[timestamp] >= DATEADD(minute, -@lookback, SYSUTCDATETIME())
    )
    SELECT host_name, role, counter_key, inst_lbl, instance_name, ts_utc, value_scaled
    FROM base
    WHERE rn = 1
    AND host_name IS NOT NULL;";

        var list = new List<MRow>();
        using var conn = new SqlConnection(_cfg.SqlConnectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new SqlParameter("@lookback", System.Data.SqlDbType.Int)
        { Value = Math.Max(1, _cfg.LookbackMinutes) });

        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            string host = r.GetString(0);
            string role = r.GetString(1);
            string key  = r.GetString(2);
            string instLbl = r.IsDBNull(3) ? "" : r.GetString(3);
            string inst = r.IsDBNull(4) ? "" : r.GetString(4);
            DateTime ts = r.GetDateTime(5);
            double val  = Convert.ToDouble(r.GetValue(6));
            if (!MetricDefs.ContainsKey(key)) continue;
            list.Add(new MRow(host, role, key, instLbl, inst, ts, val));
        }
        return list;
    }

}
