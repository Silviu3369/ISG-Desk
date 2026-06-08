# ISG Desk

**Local network diagnostics for IT helpdesk technicians — a Windows desktop tool that runs entirely on the technician's machine. No cloud, no telemetry, no listening ports.**

![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6?logo=windows)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)
![Tests](https://img.shields.io/badge/tests-718%20passing-success)
![License](https://img.shields.io/badge/license-MIT-blue)

ISG Desk consolidates the everyday network-triage tasks of a Tier 1/2 helpdesk — ping, DNS, gateway and internet checks, Wi-Fi analysis, SNMP device inspection, printer discovery, and report export — into a single application. Every diagnosis produces evidence, a confidence level, and recommended next steps so the result can be pasted straight into a support ticket.

---

## Features

| Module | Description |
|--------|-------------|
| **Technician Home** | Read-only local snapshot: device identity, OS build, user and privileges, network, organization (domain/Entra/MDM), and security posture (Defender, Firewall, BitLocker, TPM, Secure Boot, UAC). |
| **Diagnosis** | Quick Diagnosis (adapter, IP/DHCP, gateway, DNS, internet, PC context) with a 0–100 Health Score, a rule-based verdict, deep ping/jitter, and a manual port test. |
| **Targeted Tests** | Guided checks for internal servers and shares, DNS/domain (DC discovery, Kerberos, LDAP), and specific services and ports. |
| **Printers** | Local printers and spooler state, print-server queue discovery, safe subnet scan (9100/515/631), SNMP identify (toner and status), and queue install. |
| **Network Devices** | SNMP v2c/v3 inspection of switches, access points, routers, and firewalls; IF-MIB interface statistics (utilization, errors, discards); and LAN SNMP discovery. |
| **Wi-Fi Analyzer** | Real WLAN-API scanning: nearby access points, signal, channels, congestion map, channel recommendation, roaming history, and a live signal and throughput monitor. |
| **Reports** | Export HTML, TXT, and JSON reports for tickets, copy a summary to the clipboard, and generate a Windows WLAN report. |

**Safety by design:** scanning is restricted to private IPv4 ranges (RFC 1918) and capped per scan; public IP scanning is rejected. SNMPv3 credentials are encrypted with Windows DPAPI and never leave the machine.

---

## Download and install

### Option A — Pre-built release (recommended for users)
1. Open the [Releases](../../releases) page.
2. Download the latest `ISG-Desk-x.y.z-selfcontained.zip`.
3. Unzip anywhere and run `ISG Desk.exe`.
   - The self-contained build bundles the .NET runtime, so nothing else needs to be installed.
   - A smaller portable build requires the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).
4. Accept the UAC prompt (the application requests administrator rights; see [Requirements](#requirements)).

### Option B — Build from source (for developers)
See [Building from source](#building-from-source) below.

---

## Usage

Launch `ISG Desk.exe`. The application opens on Technician Home with a sidebar on the left and a live gateway-ping status panel.

Typical helpdesk flow:
1. Press `Ctrl+R` (or click **Diagnose This PC**).
2. Read the Health Score, verdict, and recommendations on the Diagnosis page.
3. Drill in with Deep Ping & Path or Manual Port Test as needed.
4. Press `Ctrl+E` to export an HTML/TXT/JSON report for the ticket.

### Keyboard shortcuts
| Shortcut | Action |
|----------|--------|
| `Ctrl+R` / `F5` | Run Quick Diagnosis |
| `Ctrl+E` | Export reports |
| `Ctrl+Shift+C` | Copy summary to clipboard |
| `Esc` | Stop or cancel the current operation |
| `Ctrl+1` … `Ctrl+7` | Jump to a module (Home, Diagnosis, Targeted Tests, Printers, Network Devices, Wi-Fi, Reports) |

For full documentation see [QUICKSTART.md](QUICKSTART.md) and [USER_GUIDE.md](USER_GUIDE.md).

---

## Privacy and data

ISG Desk runs entirely locally. It does not send any data to the cloud and opens no listening ports. Internal data is stored under `%LOCALAPPDATA%\ISG Desk\`:

- `Data\` — internal network profile and DPAPI-encrypted SNMP credentials
- `Reports\` — exported reports
- `Logs\` — rolling daily logs (7-day retention; passwords are never logged)
- `WifiHistory\` — Wi-Fi sample history (CSV)

---

## Requirements

- Windows 10 21H2 or later, or Windows 11
- .NET 8 Desktop Runtime (only for the portable build; the self-contained build bundles it)
- PowerShell 5.1 or later (ships with Windows)
- Administrator rights at launch, requested via the application manifest and used for elevated Windows queries (security posture, printer install, some adapter operations)

---

## Building from source

```powershell
# Prerequisites: .NET 8 SDK
git clone https://github.com/Silviu3369/ISG-Desk.git
cd ISG-Desk

# Build
dotnet build -c Release

# Run
dotnet run --project NetScopeDiagnosticCenter.csproj

# Run the test suite (718 tests)
dotnet test
```

### Produce a distributable build
```powershell
# Framework-dependent single file (small; needs the .NET 8 runtime on the target)
.\build-release.ps1

# Fully self-contained single .exe (no runtime needed on the target)
.\build-release.ps1 -SelfContained
```

---

## Tech stack

- WPF on .NET 8 (`net8.0-windows`), C# with nullable reference types
- MVVM architecture with dependency injection (`Microsoft.Extensions.DependencyInjection`)
- Serilog for logging, Lextm.SharpSnmpLib for SNMP
- Native WLAN API (P/Invoke) for real Wi-Fi scanning
- Hybrid data collection: .NET BCL for network probes, PowerShell for Windows management data
- 718 unit and integration tests (xUnit and FluentAssertions)

The code is organized into `Collectors/` (data gathering), `Core/` (engines, models, pure logic), `Infrastructure/` (logging, storage, SNMP, WLAN), `Reports/`, and `UI/` (views and view-models).

---

## Contributing

Issues and pull requests are welcome. If you open a pull request, please ensure `dotnet build` is clean (no warnings) and `dotnet test` passes.

---

## License

Released under the [MIT License](LICENSE).

---

## Disclaimer

ISG Desk is an IT diagnostics tool intended for use on networks you own or are authorized to administer. Network scanning is deliberately limited to private (RFC 1918) IPv4 ranges. You are responsible for complying with the policies of any network on which you run it.
