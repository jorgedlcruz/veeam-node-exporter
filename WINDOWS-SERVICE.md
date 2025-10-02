# Run VONE Exporter as a Windows Service (NSSM)

This app is a console program. The simplest service wrapper on Windows is NSSM.

## Steps

1) Create a folder, for example `C:\vone-exporter`, and place these files inside:
   - `VoneExporter.exe`
   - `appsettings.json`

2) Download NSSM and place `nssm.exe` somewhere on your PATH, for example `C:\Windows\System32`.

3) Reserve the URL if you bind to `0.0.0.0` or run under a non admin account:
```powershell
netsh http add urlacl url=http://0.0.0.0:9108/ user="NT AUTHORITY\LocalService"
```
If you will use a custom service account, replace the user accordingly.

4) Create the service with NSSM:
```powershell
nssm install VoneExporter "C:\vone-exporter\VoneExporter.exe"
nssm set VoneExporter AppDirectory "C:\vone-exporter"
nssm set VoneExporter Start SERVICE_AUTO_START
nssm set VoneExporter AppExit Default Restart
nssm set VoneExporter AppStdout "C:\vone-exporter\logs\stdout.log"
nssm set VoneExporter AppStderr "C:\vone-exporter\logs\stderr.log"
```

5) Open the port in the firewall if needed:
```powershell
netsh advfirewall firewall add rule name="VONE Exporter 9108" dir=in action=allow protocol=TCP localport=9108
```

6) Start and check status:
```powershell
sc start VoneExporter
sc query VoneExporter
```

Logs will be written to the configured files by NSSM. To change parameters, stop the service, edit `appsettings.json`, then start again.
