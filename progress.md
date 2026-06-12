# Progress log

Running log of meaningful changes: what changed, why, and the verification numbers.

## 2026-06-12 — Unobserved task exceptions: observed at the source, error log capped

**Audit**: %LOCALAPPDATA%\ISG Desk\Logs\startup-errors.log had grown to 6,700 lines
(649 KB). Histogram: 114 SocketException/AggregateException pairs — "No such host
is known" from reverse-DNS lookups and disposed UDP receives surfacing via
TaskScheduler.UnobservedTaskException on the finalizer thread; plus 53 stale
XamlParseException entries from 2026-06-04 (CongestionMeterValue TwoWay binding —
already fixed, now Mode=OneWay).

**Root cause**: tasks abandoned by timeout races. WifiLanScanner.ResolveHostAsync
raced Dns.GetHostEntryAsync against Task.Delay via Task.WhenAny and never awaited
the loser — every LAN IP without a PTR record produced one unobserved fault per
scan. Same family: mDNS/SSDP UdpClient.ReceiveAsync abandoned at deadline faults
when the client is disposed; the four Dns...WaitAsync(timeout) call sites leak the
inner task on the timeout-then-fault path.

**What changed**
- New Core/TaskFaultObserver: attaches a fault-observing continuation (reads
  t.Exception OnlyOnFaulted) and returns the same task for fluent use.
- Applied at all 7 leak sites: WifiLanScanner (reverse DNS + mDNS receive + SSDP
  receive), NetworkDeviceCollector, PrinterDiscoveryCollector,
  TargetShareDiscoveryCollector, TargetServiceDiscoveryCollector.
- App.LogStartupException now trims startup-errors.log to its newest 64 KB once it
  exceeds 512 KB (entry-boundary aware), so a repeating fault can never grow it
  unbounded again.

**Verification**
- 822/822 tests (5 new TaskFaultObserver pins: identity pass-through, result
  pass-through, fault propagation to awaiters, WhenAny-race behavior).
- Live A/B was attempted but is inconclusive by nature: the unobserved-exception
  event only fires on a gen2 GC, which short sessions never trigger (the pre-fix
  control also logged zero). Non-regression verified live on the fixed build:
  Wi-Fi Analyzer -> Start Analyzer -> Auto Detect found 11 LAN devices (the exact
  rDNS path that leaked), no new log entries, clean close.

## 2026-06-12 — Release v1.1.0 published to GitHub

- Version bumped 1.0.0 -> 1.1.0 (csproj + build-release.ps1 default).
- Docs refreshed for the elevation model: the app starts asInvoker (no UAC prompt),
  elevation via the in-app "Restart as administrator" button — README install step,
  Requirements section and QUICKSTART launch paragraph corrected; test badge and
  counts 718 -> 817; Features table updated for the production pass (S.M.A.R.T.,
  repair actions + traceroute + run comparison, published shares + trust check,
  AD print-server discovery, two-tier device discovery with labels).
- build-release.ps1: self-contained build now uses EnableCompressionInSingleFile.
- Pushed to github.com/Silviu3369/ISG-Desk as a single release commit on top of
  the initial import; granular local history kept on a local backup branch.
- GitHub Release v1.1.0 with two assets: ISG-Desk-1.1.0-portable.zip (framework-
  dependent, needs .NET 8 Desktop Runtime) and ISG-Desk-1.1.0-selfcontained.zip
  (no dependencies).

## 2026-06-12 — Live Monitor sidebar: honest state + gateway auto-retarget

**Audit**: the always-on gateway monitor worked, but the panel overstated it — the
LIVE chip was hardcoded green (even with no network), the loss line's binding
fallback claimed "loss 0%" before any ping, and the monitor never followed network
changes (dock/Wi-Fi/VPN switch left it pinging a dead gateway; opening the app
offline left it stopped forever).

**What changed**
- LIVE chip bound to `IsRunning`: green LIVE while sampling, gray OFF when idle,
  each with an explanatory tooltip (`StatusMonitorOffline` computed + forwarding).
- `NetworkChange.NetworkAddressChanged` subscription in MainViewModel with a 2 s
  debounce timer: re-detects the default gateway and retargets/starts the monitor
  only when it actually changed; publishes an activity entry; cleaned up in Dispose.
  StartAsync is dispatched to the UI thread and `ClearSamples` now freezes its empty
  PointCollection, so the retarget path is thread-safe.
- Honest empty state: `MonitoringSummary.LossDisplay` says "waiting for samples"
  until data exists; `TooltipText` (on the latency line and sparkline) shows the
  full window stats — samples, duration, avg/min/max, jitter, loss, failures —
  invariant-culture formatted.

**Verification**
- 817/817 tests (5 new MonitoringSummary display pins).
- Live via UIA on the published build: green LIVE chip present, OFF chip absent,
  gateway 192.168.0.1, "4 ms - loss 0%", tooltip read back with real stats
  (53 samples / 105 s, avg 4.9 ms, jitter 1.5 ms, 0 failed); sparkline rendering.
- startup-errors.log checked: no new entries from this build (pre-existing
  unobserved UdpClient exceptions from the morning run flagged as separate work).
- Published single-file refreshed (8.4 MB).

## 2026-06-12 — Reports: builders catch up with the new session data

**Audit**: ReportsViewModel/storage clean (HTML+TXT+JSON set with collision-safe names,
gated commands, WLAN report, open-folder). The gap was content: the builders predated
this session's additions.

**What changed (both HtmlReportBuilder and TextSummaryBuilder)**
- Traceroute section (target/status/summary + hop list with local/ISP scope) — the
  auto-traceroute result existed on the diagnosis but was never rendered.
- Targeted-test probe rows now include the share path status and the published-shares
  list (NetShareEnum results).
- LAN-scan device table rebuilt for the discovery era: "Devices Found" (was "SNMP
  Devices Found") with Name precedence (label > sysName > NetBIOS > rDNS > vendor),
  MAC + vendor, technician label, SNMP location, open-port signature, confirmation.
  Port columns inherit the HTTP-status text automatically via PortSummary.

**Verification**
- 812/812 tests (4 new builder-content pins in Reports/ReportBuildersTests).
- Live end-to-end: Quick Diagnosis run → Report Center → Export Reports via UIA →
  HTML (9.2 KB) + TXT (4.9 KB) + JSON (18.5 KB) written to %LOCALAPPDATA%\ISG Desk\
  Reports; TXT and HTML inspected — clean structure, all sections, no mojibake.
- Published single-file refreshed (8.4 MB) and smoke-tested.

This completes the production pass over all seven modules.

## 2026-06-12 — Wi-Fi Analyzer: careful pass (plan-first, per user request)

**Process**: git checkpoint first (e1078d5), then audit + plan approved by the user,
then one stage at a time with live verification after each. Explicit constraint:
don't break what works — the ON-DEMAND model (nothing touches the radio until
"Start Analyzer") is a prior user request and was left untouched.

**The one real defect found & fixed**
- Page wheel-scroll froze whenever the cursor crossed a DataGrid (grids mark the
  wheel handled even with nothing to scroll) — proven by 4 identical scroll captures.
  New `UI/Behaviors/ScrollWheelBubbling.cs` attached behavior wired into the implicit
  DataGrid style: bubbles the wheel to the page scroll chain ONLY when the grid can't
  scroll further itself. Benefits every page; long grids keep inner scrolling.

**Live verification sweep (all 13 sections, real Wi-Fi, real traffic)**
- Connection card: SSID/BSSID/channel/PHY/security/IP live; RSSI + latency sparklines.
- Start Analyzer → Receive Rate showed a real 17 Mbps download burst (Now/Avg/Peak),
  Transfer Rate 178 Kbps; Stop preserves data. Threshold slider + sound alert present.
- Channel Graph: 2.4/5/6 GHz humps, own AP bold (ch 106), hidden networks rendered.
- Signal Over Time: top-8 trend lines with per-network legend checkboxes.
- Channel Congestion: per-channel bars + recommendation card + DFS warnings +
  before/after baseline. Security Audit: WPA2→WPA3 advisory. Health: 89/100 with
  Signal/Congestion/Security/Wi-Fi-gen breakdown.
- Device inventory Auto Detect: honest model confirmed live (own PC = only
  "Confirmed wireless"; gateway + others in discovery evidence).
- Visible Networks: 9 networks with per-BSSID session stats (Avg/Seen%); Adapter
  Capabilities (Intel AX201). Roaming + Saved Profiles expanders open correctly.

**Verification**: 808/808 tests; published single-file refreshed (8.39 MB) and
smoke-tested. Stages 2-5 of the plan found zero functional defects — verified
rather than changed, exactly as agreed.

## 2026-06-12 — Follow-up: "placeholder?" report investigated — data real, self-row clarified

User challenged two scan rows ("I have no NAS, no Vivobook"). Investigation with
Windows tools, independent of the app (Get-NetNeighbor, nbtstat, Resolve-DnsName):

- "SilviuVivobook" never existed — the row reads "Silviu.telenet.be" (the user's own PC
  via the ISP's reverse DNS); the assistant misread the low-res screenshot when
  summarizing. App data was correct.
- "PC / NAS (SMB)" is the SMB-open category label on that same own-PC row, not a NAS claim.
- All 12 devices cross-check against the OS ARP table exactly (11 neighbors + self).

Fix shipped from the feedback: `NetworkDeviceCollector.ApplySelfIdentity` (public, pure,
test-pinned) — the scanning machine's own row now shows DeviceType "This PC (running the
scan)", the real machine name instead of the ISP rDNS name, confidence High. 808/808
tests; republished (8.39 MB).

## 2026-06-12 — Network Devices: real device discovery (two-tier scan)

**Problem (live-proven before the change)**: the LAN scan was SNMP-only — on the user's
real /24 it scanned 254 hosts and found **0 devices** (253 SNMP timeouts), because home
and office endpoints don't speak SNMP. The page headline also kept saying "No network
device check has been run yet" after a scan (it only tracked the single-device check).

**What changed**

- `NetworkDeviceCollector` — scan reworked into two tiers: (1) parallel ping sweep that
  also primes the ARP cache + ONE bulk neighbor-table readout (replaces 254 per-host
  PowerShell spawns) + reverse DNS + NetBIOS name + signature ports 22/80/443/445/3389/
  8009/9100; (2) SNMP identity attempt per live host — a bonus, not a requirement.
  `ClassifyDiscoveredDevice` (public, pure, test-pinned): SNMP identity → High; printer
  port/gateway → High; RDP/SMB+NetBIOS → "Windows PC / server" Medium; cast port → TV;
  vendor hints → honest "Probably …" Low. MAC vendor now uses the Wi-Fi module's full
  OUI table (old 20-entry dict kept as fallback).
- `Collectors/Shared/NetBiosNameResolver.cs` — nbtstat-based Windows name lookup
  (extracted pattern from WifiLanScanner; bounded 900 ms, never throws).
- `NetworkDeviceResult` — NetBiosName, IsGateway, FriendlyLabel + DisplayName precedence
  (label > sysName > NetBIOS > rDNS > vendor) and LocationDisplay (SNMP sysLocation).
- `NetworkDevicesViewModel` — persistent per-MAC labels via the SHARED
  WifiDeviceFriendlyNameStore (label once, visible in Wi-Fi module too): Save/Clear
  commands + editor synced to the selected row; labels re-applied on every scan.
  Headline verdict now follows the latest action (scan vs single check).
- XAML — grid rebuilt for the discovery answer: IP, Name, Type, Vendor (MAC), MAC,
  Label/location, SNMP location, Confidence, Ping, Open ports, Confirmation + label
  editor row under the grid.
- DI note: MainViewModel takes the label store as a REQUIRED ctor param (Microsoft DI
  fills optional params with their defaults instead of resolving them).

**Verification**

- Tests: 807/807 (new: 8 classification/DisplayName pins, 3 VM label/headline tests;
  one stale wording assertion updated with the new empty-state text).
- Live on the real LAN: same /24 now reports "**Found 12 device(s) in 192.168.0.0/24;
  0 answered SNMP**" — router classified "Router / gateway" (High), the user's PC
  "PC / NAS (SMB)", a second Windows laptop resolved by NetBIOS name ("SilviuVivobook"),
  phones honest-labelled (randomized MACs ⇒ vendor Unknown — protocol limitation,
  documented in USER_GUIDE). Headline band updates correctly after the scan.
- Published single-file refreshed (8.39 MB) and smoke-tested; USER_GUIDE Network
  Devices section rewritten around the two-tier discovery + labels.

## 2026-06-11 — Printers module: repaired, clarified, compacted

**Audit (code + live UIA run of every tab)** — the features the user asked for mostly
existed but were buried or half-right: an 8-button action bar duplicated every tab's
actions with zero hierarchy; the scan listed routers/NAS as "possible printer" because it
probed 80/443; auto-detect only looked at already-installed connections; the SNMP
"Active alerts" band rendered even with no device (null binding path leaves Visible).

**Fixes**

- `DiagnosticConstants.PrinterPorts` → [9100, 515, 631] (printer protocols only). Scan
  classification simplified; live /24 scan now correctly reports "No printer-related
  ports found" instead of listing the router as a possible printer.
- SNMP marking: rows that answer SNMP with no Printer-MIB evidence and no print port are
  now "Not a printer (SNMP)"; print-port hosts with a weak MIB stay "Likely printer
  (SNMP identity unconfirmed)". New "Only printers" filter (default on) hides confirmed
  non-printers; hidden-count hint shows how many and how to reveal.
- `Infrastructure/AdPrintServerLocator.cs` (new, + System.DirectoryServices 8.0.0):
  Auto-detect Server now falls back to AD-published printQueue objects — finds the print
  server with zero config on domain PCs, picks the busiest, reports all candidates.
  Workgroup/AD-failure → clean manual-entry message (live-verified on this machine).
- Install tab: optional "Set the installed queue as the Windows default printer" —
  install + default in one step, at the tech's choice.
- UI compacting: the duplicated top action row is gone; the status strip keeps verdict +
  chips + SNMP protocol + Copy for ticket, with Stop buttons appearing only while a scan
  or SNMP run is active. Every action now lives next to its own results in its tab.
  "Active alerts" band fixed with FallbackValue=Collapsed.

**Verification** — 793/793 tests (new: AD fallback ×2, printers-only filter, SNMP
non-printer marking ×2, set-default-after-install ×2, AD locator integration guard; one
existing classification assertion updated to the new wording). Live: compact strip, AD
fallback message, real /24 scan with printer-only ports, Install checkbox — all
screenshot-verified; single-file publish refreshed (8.34 MB) and smoke-tested.

## 2026-06-11 — Targeted Tests: one production capability per sub-module

**Audit** — code (ScenarioEngine 631 lines, TargetedTestsViewModel 1 444 lines, 3 discovery
collectors) + live runs of all 3 scenarios via UIA. Everything already worked (validation,
bounded discovery, port categories, verdict cascades); each sub-module lacked exactly one
capability a sysadmin expects.

**Sub-module 1 — Internal Server / Share Access: published-share enumeration**
- `Infrastructure/SmbShareEnumerator.cs` — NetShareEnum level 1 P/Invoke (docs-verified:
  no special group membership needed at level 0/1; locale-independent, unlike `net view`).
  IPC$ filtered, hidden/print shares labelled, bounded 8 s, never throws.
- `TargetConnectivityCollector` — enriches file-server probes (TCP 445 open) with the
  share list; virtual seam for tests. `TargetProbeResult` gains ShareEnum fields +
  display computeds; engine evidence + "Published shares" grid column.
- Live-verified: localhost shows "3 share(s) published: ADMIN$ (hidden), C$ (hidden)…".

**Sub-module 2 — DNS / Domain: machine-account trust + zero-config DC targets**
- `DomainCollector.TestSecureChannelAsync` — Test-ComputerSecureChannel (docs: needs
  admin → degrades to Unknown with a relaunch-elevated hint; domain members only).
- ScenarioEngine: "Machine account trust" step when domain-joined; Critical → dedicated
  verdict "Machine account trust with the domain is broken" (the classic helpdesk case)
  with Repair/Reset-ComputerMachinePassword next-check; healthy-but-unverified gets an
  explicit "(trust not verified)" verdict. DC target fallback chain extended:
  override > profile > **DNS SRV auto-discovery** (new 3-param ctor; DI picks it,
  tests keep the 2-param one) > logon server.
- Workgroup machines skip everything (live-verified earlier; unit-covered).

**Sub-module 3 — Service Access: real HTTP probe on web ports**
- Collector script: for TCP-open 80/443/8080/8443, an HttpWebRequest GET (5 s, no
  redirects, self-signed certs accepted) captures the status code even from WebException.
  `PortProbeResult` gains HttpStatusCode/HttpDetails; StatusText → "Open (HTTP 200)";
  engine adds "HTTP check <port>" evidence lines.
- Live-verified against www.google.com: grid shows "80:Open (HTTP 200), 443:Open (HTTP
  200)"; 401/403 are reported as "service is answering" (alive but auth-gated).

**Verification**
- Tests: 785/785 (new: 3 collector enrichment, 4 engine trust/SRV, 6 port-model, plus
  the earlier Diagnosis-round suites).
- All 3 scenarios exercised live in the running app via UIA; published single-file build
  refreshed (8.32 MB) and smoke-tested; USER_GUIDE Targeted Tests table updated.

## 2026-06-11 — Diagnosis module: repair actions, manual traceroute, run comparison

**Audit verdict (code + live click-through):** the module was already strong — clean
host-pattern VMs, full cancellation discipline, bounded operations, verdict with
evidence/recommendations, cancellable loading overlay, presets fed by the live
diagnosis. What a helpdesk/sysadmin tool was missing: remediation, on-demand path
visibility, and before/after proof.

**What changed**

- `Collectors/NetworkRepairService.cs` (new) — the only state-CHANGING collector:
  Flush DNS (`Clear-DnsClientCache`), Renew DHCP (`ipconfig /release`+`/renew`, shows the
  fresh DHCP IPs), Reset Winsock (`netsh winsock reset`, restart-required), Restart
  Adapter (`Restart-NetAdapter`, name via the PsSingleQuote choke-point). Time-boxed,
  JSON results, virtual methods for test fakes.
- `UI/ViewModels/RepairActionsViewModel.cs` (new) + `MainViewModel.RepairActions.cs`
  (new partial) — confirmation-gated commands (injectable confirm → no MessageBox in
  tests), elevation hint, adapter-gated restart button, last-result panel with severity.
- `UI/ViewModels/LinkQualityViewModel.cs` — manual **Trace Route** (max 15 hops, bounded,
  validated target, defaults to the ping target or the internet beacon), hop list with
  local vs isp/internet scope. `TraceRouteCollector.TraceAsync` made virtual + unsealed
  for fakes; user targets documented as PsSingleQuote-protected.
- `Core/DiagnosticHelpers.BuildDiagnosisComparison` + `MainViewModel.LastDiagnosis`
  setter — session-scoped "Previous run HH:mm:ss: 92/100 (…) · score +8 — improved"
  line on the verdict card; in-place mutations don't rotate the baseline.
- `MainWindow.xaml` — Repair Actions expander (amber, with admin-hint banner and
  outcome panel), Trace Route card in Deep Ping & Path, comparison line under the
  verdict chips, hero subtitle updated.
- `USER_GUIDE.md` — Diagnosis section documents traceroute, repair actions, comparison.

**Bug caught by live verification (not by unit tests):** the first Flush DNS run showed
"produced no result" — `try {…} catch {…} | ConvertTo-Json` is a PS parse error (you
can't pipe from a try/catch). Fixed by assigning to `$result` first; same fix applied to
the restart-adapter script. Regression-proofed with an integration test that runs the
real flush script through real powershell.exe (harmless on any machine).

**Verification**

- Tests: 772/772 (new: 6 repair VM + 4 severity matrix + 4 traceroute VM + 3 comparison
  + 2 repair service incl. the real-PowerShell flush).
- Live click-through via UIA InvokePattern (mouse-free — synthetic cursor clicks raced
  the user's physical mouse): two Quick Diagnosis runs → comparison line rendered
  ("Previous run 15:38:31: 100/100 (OK — …) · score unchanged"); manual traceroute OK
  (1 hop to gateway); Repair Actions expander renders with amber admin hint,
  Restart Adapter correctly disabled before a diagnosis, Flush DNS green panel
  "DNS client cache cleared" at 15:45:14.
- Republished `publish\ISG Desk.exe` (8.28 MB).

## 2026-06-11 — Task Manager quick-tool + Release publish

- Windows Tools row gains a "Task Manager" button (indigo, EC4A SpeedHigh glyph —
  verified in MDL2 so it renders on Windows 10 too; F120 TaskManagerApp is Fluent-only).
  `OpenTaskManagerCommand` follows the same `OpenWindowsTool` pattern as the other six.
- Module declared feature-complete; deliberately NOT added: Windows Update status (slow
  scan), top-process list (overlaps Task Manager), NTP status (too niche for the page).
- Release publish: `dotnet publish -c Release -p:PublishProfile=Portable -o publish` →
  single-file framework-dependent `publish\ISG Desk.exe` (8.2 MB, needs .NET 8 Desktop
  Runtime on target PCs). Self-contained build deferred until the app is finished.
- Verification: 753/753 tests; published exe launched, tools row screenshot confirms the
  new button wraps cleanly to a second row. Note for future verification: launching via a
  harness background task can get the GUI child process killed when the task completes —
  start verification instances with `[System.Diagnostics.Process]::Start` instead.

## 2026-06-11 — Technician Home polish: tooltips, elevate-on-demand, adapter dedup

**What changed**

- `Core/Models/SystemOverview.cs` — `SystemOverviewNetwork.AdapterDisplay` collapses
  "Wi-Fi · Wi-Fi" to "Wi-Fi" when the adapter is literally named after its type; used by
  the Network Snapshot card and Copy Summary.
- `UI/ViewModels/TechnicianHomeViewModel.cs` — `ShowRestartAsAdmin` (true only after the
  snapshot shows a non-elevated process) + `RestartAsAdminCommand`: relaunches via
  `runas`, treats a declined UAC prompt (Win32 1223) as a Warning activity, never a crash.
- `MainWindow.xaml` — amber "Restart as administrator" button (shield icon) inside the
  ADMIN MODE tile, visible only when not elevated; helpdesk tooltips (what it is + what to
  do when red) on all 8 Security Snapshot rows, `Cursor=Help`, 30 s show duration.
- `app.manifest` — **requireAdministrator → asInvoker.** The old manifest forced a UAC
  prompt at every launch (discovered when a background launch died with "operation was
  canceled by the user"), made the in-app elevate flow impossible and contradicted the
  collectors' own "Requires admin" graceful-degradation design. The app now opens without
  UAC for any user; elevation is one click away. Revert = set `requireAdministrator` back.
- `USER_GUIDE.md` — the three sections describing launch-time admin updated to match.

**Verification**

- Tests: 753/753 (new: AdapterDisplay theory ×4, ShowRestartAsAdmin visible/hidden,
  RestartAsAdminCommand availability).
- Visual (screenshots of the running app, non-elevated): top-bar badge "Standard";
  ADMIN MODE tile red with the amber elevate button; Battery "53% · Plugged in (AC)" and
  Graphics "… Design · Intel(R) UHD Graphics" — UTF-8 fix confirmed on screen;
  "Adapter / type: Wi-Fi" single value; BitLocker/TPM/Secure Boot show "Requires admin /
  Unknown" exactly as designed for non-elevated; Defender tooltip renders wrapped and
  readable on hover; STORAGE HEALTH tile still green (disk HealthStatus needs no admin).
- App launches with NO UAC prompt and was left running.

**Known gaps / deferred**

- Stale log entries from 18:42/18:45 (error 206, command line too long) predate the
  temp-file fallback — no recurrence with the current binary.

## 2026-06-10 — Visual audit of Technician Home + UTF-8 output fix

**What changed**

- `Infrastructure/PowerShellRunner.cs` — second runner bug found by on-screen inspection:
  Windows PowerShell 5.1 writes redirected stdout in the OEM codepage, so '·' in
  PS-built strings rendered as "ú" on the page (Hardware → Graphics "GTX 1650 ú Intel UHD",
  Battery "54% ú Plugged in"); '°', '…' and Romanian diacritics were equally at risk in
  every collector. The runner now prepends `[Console]::OutputEncoding = UTF8` to every
  script and sets `StandardOutput/ErrorEncoding = UTF8`, fixing the whole app.
- `PowerShellRunnerTests` — regression test round-trips "GPU·list 34 °C București șț"
  through real powershell.exe.

**Verification**

- Tests: 746/746 passing (suite run against an isolated `BaseOutputPath` because the
  user's elevated app instance locked `bin\Debug`).
- Visual audit (screenshots of the live app, elevated): hero/tiles/tools/cards render
  cleanly; STORAGE HEALTH tile shows real data ("1 disk(s) healthy · C: 75% free");
  Storage & Disk Health card shows the NVMe SSD with serial, 20 °C, wear 0% (admin
  counters live); volume bar fill matches usage; Disk Management button works (user had
  diskmgmt.msc open). The only visible defect was the "ú" mojibake — fixed above.
- The running elevated instance still has the old binary — restart the app to pick up
  the UTF-8 fix.

**Known gaps / deferred**

- Optional polish ideas recorded in the session summary (tooltips on security rows,
  EN-only UI, "Adapter / type" duplication when the adapter is literally named "Wi-Fi").

## 2026-06-10 — Technician Home: Storage & Disk Health (S.M.A.R.T.)

**What changed**

- `Core/Models/SystemOverview.cs` — new `SystemOverviewDisk` (per-physical-disk S.M.A.R.T. health,
  failure prediction, wear/temperature/power-on hours, computed `Severity`/`TypeLine`/`SmartDetail`)
  and `SystemOverviewVolume` (raw byte counts, computed usage %, free-space severity bands:
  Critical < 5 % or < 5 GiB free, Warning < 12 % or < 15 GiB free). Severity logic lives in the
  model so it is unit-testable, following the existing `SystemOverviewEvent.Severity` precedent.
- `Collectors/SystemOverviewCollector.cs` — fast tier gained `FillVolumes` (DriveInfo, no admin,
  renders even when PowerShell fails); heavy tier gained a physical-disk block:
  `Get-PhysicalDisk` (MediaType/BusType/HealthStatus enums mapped per MSFT_PhysicalDisk docs),
  `Get-StorageReliabilityCounter` per disk (guarded — needs admin → `SmartAvailable=false`),
  predictive-failure via OperationalStatus + `Win32_DiskDrive.Status` fallback. Heavy timeout
  12 s → 15 s.
- `Infrastructure/PowerShellRunner.cs` — **bug found by the verification ritual:** the grown
  heavy script blew the ~32 K CreateProcess command-line ceiling via `-EncodedCommand`
  (Win32 error 206). Scripts whose encoded form exceeds 24 K now run from a temp `.ps1` via
  `-File` (UTF-8 BOM), deleted in `finally`; orphans from killed processes are swept when
  older than 1 h.
- `UI/ViewModels/TechnicianHomeViewModel.cs` — `Disks`/`Volumes`/`StorageSeverity`/`StorageSummary`
  projections, `OpenDiskManagementCommand` (diskmgmt.msc), storage section in Copy Summary.
- `MainWindow.xaml` — full-width "Storage & Disk Health" card (per-disk health pills + S.M.A.R.T.
  detail, per-volume usage bars via new `VolumeUsageBar` ProgressBar style), 5th STORAGE HEALTH
  status tile, Disk Management tool button, hero subtitle updated.
- Tests — new `SystemOverviewStorageTests` (severity bands, display projections), storage cases in
  `TechnicianHomeViewModelTests`, temp-file + stale-sweep regression in `PowerShellRunnerTests`.
- `USER_GUIDE.md` — Technician Home section documents the storage feature.

**Why**

Technician Home showed storage only as a model/size string and system-drive free space. Helpdesk
needs disk *health* at a glance: failing HDD prediction, SSD wear, and which volume is full —
without running scans or external tools.

**Verification**

- Build: 0 errors / 0 warnings.
- Tests: 745/745 passing (was 744; +13 storage tests, +1 runner regression; all green).
- Launch: app started, Technician Home rendered; stable and `Responding=True` past 48 s idle;
  working set ~250–290 MB (baseline band, no creep).
- App log: zero errors in the post-fix session (the 206 error reproduced with the old binary,
  gone with the new one).
- Event Viewer: no Application Error / .NET Runtime / WER events; no crash dumps.
- The S.M.A.R.T. PowerShell block was also validated standalone on this machine: detects the
  NVMe SSD correctly, `SmartAvailable=false` without admin (expected guarded degradation).

**Known gaps / deferred**

- Reliability counters (temperature, wear, hours) require running the app as administrator;
  without it the card states this per disk. By design.
- `AppCompositionTests` constructs the real `MainViewModel`, whose constructor fire-and-forgets
  `TechnicianHome.EnsureLoadedAsync()` → real `powershell.exe` spawns during unit tests and can
  orphan temp scripts when the test process exits first. The 1 h sweep self-heals this; a cleaner
  fix (injectable/no-op collector in composition tests) is deferred.
