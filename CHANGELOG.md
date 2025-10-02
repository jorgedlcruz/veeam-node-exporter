# Changelog

## v0.1.0
- Initial public release
- Windows x64 self contained build
- Exposes:
  - `*_net_bytes_sent_per_sec` with labels `host` and `iface` where applicable
  - `*_disk_bytes_per_sec` with labels `host` and `disk`
  - `*_cpu_usage_percent` with label `host`
  - `*_memory_used_bytes` with label `host`
- Health metrics:
  - `<prefix>_up`
  - `<prefix>_last_scrape_success_seconds`
  - `<prefix>_last_scrape_error_info`
