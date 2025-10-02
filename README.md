# VONE Exporter for Prometheus (Windows)

Prometheus exporter that reads recent performance counters from a Veeam ONE database and exposes them at `/metrics`.

- Metrics: `*_net_bytes_sent_per_sec`, `*_disk_bytes_per_sec`, `*_cpu_usage_percent`, `*_memory_used_bytes`
- Labels: `host`, plus `iface` for network and `disk` for disks
- Format: Prometheus text exposition
- Endpoints: `/metrics` and `/debug`
- Default listen URL: `http://127.0.0.1:9108/`

## Requirements

- Windows 10 or later, or Windows Server 2019 or later
- .NET 8 SDK for building
- No runtime required if you use the self contained release binary

## Quick start on Windows

1) Download the latest Windows ZIP from the Releases page once you have published one.
2) Extract to a folder, for example `C:\vone-exporter`.
3) Copy `appsettings.example.json` to `appsettings.json`. Edit the connection string to your Veeam ONE SQL Server. Use a read only login.
4) Run the exporter:
   ```powershell
   cd C:\vone-exporter
   .\VoneExporter.exe
   ```
5) Browse to `http://127.0.0.1:9108/metrics` and confirm you see metrics.

### Allowing remote scrape

If Prometheus scrapes from another host, change `ListenUrl` to `http://0.0.0.0:9108/` in `appsettings.json`, then allow the port in Windows Defender Firewall:

```powershell
netsh advfirewall firewall add rule name="VONE Exporter 9108" dir=in action=allow protocol=TCP localport=9108
```

If you see `HttpListener failed` about URL ACL, reserve the URL once:

```powershell
netsh http add urlacl url=http://0.0.0.0:9108/ user=Everyone
```

## Configuration

`appsettings.json` must live next to the executable.

```json
{
  "Exporter": {
    "ListenUrl": "http://127.0.0.1:9108/",
    "PollSeconds": 60,
    "LookbackMinutes": 15,
    "SqlConnectionString": "Server=YOURVONESERVER;Database=VeeamOne;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True;",
    "MetricPrefix": "vone_exporter"
  }
}
```

Notes:
- On production systems use a proper server certificate and set `TrustServerCertificate=False`.
- Create a least privileged SQL login that can read the `monitor` schema only.
- By default the JSON config uses Windows Auth, considering this .exe is stored on the VONE Server itself.

## Prometheus scrape example

```yaml
scrape_configs:
  - job_name: "voneexporter"
    metrics_path: /metrics
    static_configs:
      - targets: ["your-windows-host:9108"]
```

## Telegraf scrape example

```yaml
[[inputs.prometheus]]
  urls = ["http://127.0.0.1:9108/metrics"]
  metric_version = 2
  name_override = "vone_exporter"
```

## Run as a Windows service

Use NSSM to run the console app as a Windows service. Full steps are in **WINDOWS-SERVICE.md**.

## Build from source on Windows

Assuming your `VoneExporter.csproj` is at the repository root and targets `net8.0-windows`.

```powershell
# restore
dotnet restore .\VoneExporter.csproj

# publish self contained single file for Windows x64
dotnet publish .\VoneExporter.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\out\win-x64
```

Copy `appsettings.example.json` to `.\out\win-x64\appsettings.json`, edit, then run `VoneExporter.exe` from that folder.


## Security notes

- Bind to `127.0.0.1` unless a remote scrape is required.
- Use a low privilege service account when running as a service.
- Limit SQL permissions to read only on the needed tables.

## License

MIT or Apache 2.0 are both good choices for an exporter.
