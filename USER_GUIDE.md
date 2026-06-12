# ISG Desk - User Guide

Ghid complet pentru helpdesk IT. Cititi mai intai [QUICKSTART.md](QUICKSTART.md) pentru onboarding rapid.

---

## Cuprins

1. [Arhitectura aplicatie](#arhitectura-aplicatie)
2. [Technician Home](#technician-home)
3. [Diagnosis](#diagnosis)
4. [Targeted Tests](#targeted-tests)
5. [Modulul Printers](#modulul-printers)
6. [Modulul Network Devices](#modulul-network-devices)
7. [Modulul Wi-Fi Analyzer](#modulul-wi-fi-analyzer)
8. [Modulul Reports](#modulul-reports)
9. [Internal profile and session data](#internal-profile-and-session-data)
10. [Securitate credentiale](#securitate-credentiale)
11. [Troubleshooting](#troubleshooting)
12. [Compatibilitate](#compatibilitate)

---

## Arhitectura aplicatie

ISG Desk este o aplicatie WPF .NET 8 care ruleaza local pe statia tehnicianului. Nu trimite date in cloud si nu deschide porturi de ascultare.

Aplicatia porneste fara prompt UAC (`asInvoker` in `app.manifest`) ca sa poata fi folosita si de un cont standard, in mod read-only. Probele care cer privilegii ridicate (BitLocker, TPM, contoarele S.M.A.R.T. de temperatura/uzura) afiseaza "Requires admin" pana cand rulezi elevat — fie din butonul "Restart as administrator" de pe tile-ul ADMIN MODE din Technician Home, fie cu click dreapta > Run as administrator.

Scanarea este limitata intentionat:

- IP-uri private RFC 1918: `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`
- Maxim 254-256 hosts per scan
- CIDR mai mare decat `/24` este respins pentru scanarile locale
- IP-urile publice sunt respinse de safety policy

Tehnologii folosite:

- Ping ICMP prin `System.Net.NetworkInformation.Ping`
- DNS lookup prin `System.Net.Dns`
- TCP probe prin `System.Net.Sockets.TcpClient`
- HTTPS GET prin `System.Net.Http.HttpClient`
- SNMP v2c/v3 prin `Lextm.SharpSnmpLib`
- WLAN API pentru scanari Wi-Fi reale
- PowerShell pentru anumite informatii Windows locale, imprimante si rapoarte WLAN

---

## Technician Home

Technician Home este pagina de start operationala, read-only:

- Identitate device: producator, model, serial, Windows edition/build, uptime
- User si privilegii: user curent, profil, admin/elevated
- Network snapshot: adapter, IPv4, gateway, DNS, DHCP, MAC, Wi-Fi SSID
- Organization: domain/workgroup, Entra/hybrid join, MDM, logon server
- Security posture: Defender, Firewall, BitLocker, UAC, Secure Boot, TPM, pending reboot, activation
- Storage & Disk Health (S.M.A.R.T.): stare per disc fizic (SSD/HDD/NVMe) din MSFT_PhysicalDisk — Healthy/Warning/Unhealthy plus predictie de defectare; temperatura, uzura SSD si ore de functionare apar cand aplicatia ruleaza ca administrator; bare de spatiu liber pe fiecare volum fix (Critical sub 5% sau 5 GB liber, Warning sub 12% sau 15 GB)
- Ultima diagnoza si actiuni rapide catre modulele principale

Technician Home nu este o copie a meniului si nu ruleaza scanari de retea la incarcare. Colecteaza doar informatii locale read-only si afiseaza sumarul ultimei diagnoze cand exista.

---

## Diagnosis

Diagnosis este modulul fuzionat pentru diagnoza PC-ului: Quick Diagnosis, Deep Ping & Path (cu traceroute manual), Manual Port Test si Repair Actions sunt in aceeasi pagina.

### Quick Diagnosis

Ruleaza un baseline complet al statiei:

1. Adaptoare active, tip conexiune, MAC, link speed si duplex
2. IP, subnet, DHCP, gateway si DNS configurate
3. Gateway ping si reachability IPv4
4. DNS reachability si external lookup
5. Internet probe prin ping, TCP 443 si HTTPS GET
6. Wi-Fi context cand statia este pe wireless
7. PC context: domain, logon server, VPN/proxy, MTU si routes

Rezultatul include:

- Health Score 0-100 calculat de `HealthScoreCalculator`
- Verdict generat de `RuleEngine`
- Diagnostic timeline cu durata fiecarei etape
- Technical Details cu evidence, limitations, recommended actions, warnings si score penalties
- Comparatie cu rularea anterioara din sesiune ("Previous run HH:mm:ss: 92/100 ... score +8 — improved") — confirmi imediat daca un fix a ajutat

### Deep Ping & Path

Ruleaza ping/jitter pentru un target ales:

- Gateway, internet IP sau host privat autorizat
- 1-50 samples
- Packet loss, min/avg/max latency si jitter
- Praguri diferite pentru gateway, internet, target si DNS

Tot aici este si **Trace Route** manual (max 15 hops, bounded): foloseste targetul din campul de ping (sau beacon-ul de internet cand e gol) si eticheteaza fiecare hop ca `local` sau `isp/internet`, ca sa vezi unde se rupe calea fara sa astepti ca Quick Diagnosis sa detecteze pana.

### Manual Port Test

Testeaza un `host:port` manual:

- Porturi rapide: 22, 80, 443, 445, 3389, 9100, 161
- Verdict: open, refused sau timeout
- Latenta pana la connect/refuse

### Repair Actions

Singura sectiune din aplicatie care MODIFICA sistemul — primul ajutor de retea, cu confirmare inainte de actiunile care intrerup conexiunea si cu rezultatul exact afisat dupa:

| Actiune | Comanda | Admin | Observatii |
| --- | --- | --- | --- |
| Flush DNS Cache | `Clear-DnsClientCache` | Nu | Fara intrerupere |
| Renew DHCP Lease | `ipconfig /release` + `/renew` | Nu | Conexiunea cade cateva secunde; afiseaza noile IP-uri DHCP |
| Reset Winsock | `netsh winsock reset` | Da | Cere restart Windows |
| Restart Adapter | `Restart-NetAdapter` pe adaptorul activ din ultima diagnoza | Da | Buton dezactivat pana exista o diagnoza |

Dupa o reparatie, aplicatia iti sugereaza sa rulezi din nou Quick Diagnosis — linia "Previous run" de pe verdict arata imediat daca scorul s-a imbunatatit.

---

## Targeted Tests

Targeted Tests ruleaza doar cele 3 scenarii care au nevoie de un target concret. Ele refolosesc Quick Diagnosis baseline si adauga verificari specifice.

| Test | Input | Ce verifica |
|----------|-------|-------------|
| Internal Server or Share Access | server/UNC list | DNS, ping, TCP 445, acces UNC si lista share-urilor publicate de server (NetShareEnum — "ce share-uri exista pe SRV01", nu doar "portul e deschis") |
| DNS/Domain | DC override optional | DC discovery (override > profil > DNS SRV automat > logon server), Kerberos 88, LDAP 389, SMB 445 si trustul contului de masina (Test-ComputerSecureChannel — clasicul "trust relationship failed"; cere drepturi de administrator, altfel apare ca neverificat) |
| Service Access | host + porturi | DNS, ping, TCP pentru porturi specifice; pe porturile web (80/443/8080/8443) face si proba HTTP reala — "443:Open (HTTP 200)" confirma ca SERVICIUL raspunde, nu doar socketul |

Testele cu input obligatoriu refuza rularea cand lipseste targetul. Targeturile recente sunt pastrate doar in memoria sesiunii curente, nu intr-un modul separat.

---

## Modulul Printers

### Local Printers

Citeste imprimantele instalate local si starea spoolerului.

### Print Server Discovery

Se conecteaza la un print server UNC si listeaza queue-urile shared disponibile.

**Auto-detect Server** gaseste serverul singur, in doua trepte: intai din conexiunile
shared deja instalate pe PC, apoi (pe PC-uri in domeniu) din print serverele publicate
in Active Directory (obiecte `printQueue`); cand exista mai multe, alege serverul cu
cele mai multe cozi publicate si le raporteaza pe toate in activity feed.

La instalarea unei cozi din Install poti bifa optional "Set the installed queue as the
Windows default printer" — coza proaspat instalata devine si imprimanta implicita.

### Auto Safe Scan

Detecteaza subnetul local si scaneaza controlat un `/24` **doar pe porturile de
protocol de imprimare**:

- 9100 RAW
- 515 LPR
- 631 IPP

Porturile web (80/443) nu mai sunt scanate intentionat: faceau ca orice router/NAS/camera
cu interfata web sa apara ca "possible printer". Identitatea se confirma prin SNMP, iar
grila are filtrul "Only printers" (bifat implicit) care ascunde device-urile pe care
SNMP le-a confirmat ca NU sunt imprimante (switch, NAS, UPS).

### Custom Range Scan

Accepta single IP, CIDR `/24` sau range limitat, de exemplu `10.0.0.20-10.0.0.80`.

### SNMP Identify

Ruleaza OID-uri printer-MIB pentru device-ul selectat:

- `sysDescr`, `sysName`, `sysLocation`
- Nivel toner cand este disponibil
- Status imprimanta

SNMPv3 authPriv este suportat. Credentialele pot fi salvate local prin DPAPI.

### Queue Install

Instaleaza o coada de pe print server prin `Add-Printer -ConnectionName \\server\share`. Actiunea cere confirmare in UI.

---

## Modulul Network Devices

Network Devices inventariaza TOATE device-urile vii din retea si identifica echipamentele de infrastructura prin SNMP.

### Discovery scan (Auto Safe Scan / Scan Range)

Scan in doua trepte:

1. **Descoperire generala** — ping sweep paralel + tabela ARP/neighbor (prinde si hosturile care blocheaza ping), reverse DNS, nume NetBIOS (PC-uri Windows), vendor din MAC (baza OUI completa) si porturi-semnatura (9100 imprimanta, 445/3389 PC Windows, 8009 TV/cast, 22+80 echipament de retea).
2. **Imbogatire SNMP** — doar device-urile care raspund pe UDP 161 primesc identitate completa (sysName, sysLocation, sysDescr).

Clasificarea e onesta: SNMP = confidence High; porturi = Medium; doar vendor MAC = Low ("Probably ..."). Telefoanele moderne folosesc MAC-uri randomizate, deci vendorul lor apare "Unknown" — limitare de protocol, nu de aplicatie.

**Etichete persistente**: selecteaza un device si scrie "Label / location" (ex: "Imprimanta etaj 2 — contabilitate"). Se salveaza per MAC si reapare la fiecare scan; baza de nume e comuna cu modulul Wi-Fi Analyzer. Locatia fizica nu se poate detecta din retea: vine din SNMP sysLocation (daca adminul l-a completat) sau din eticheta ta.

### SNMP Identity

Returneaza:

- `sysDescr`, `sysName`, `sysLocation`, `sysContact`, `sysUpTime`
- Clasificare device: Switch, Access Point, Router, Firewall, Printer sau Unknown

### IF-MIB Interfaces

Citeste tabelul interfetelor si calculeaza rate pe baza unui sampling scurt:

- `ifIndex`, `ifName`, `ifDescr`
- `ifAdminStatus`, `ifOperStatus`
- `ifSpeed`, `ifHighSpeed`
- in/out octets
- errors/sec, discards/sec si utilization%

Filtre utile:

- All
- Needs Attention
- Down
- 100 Mbps
- Errors/Discards
- High Traffic
- Unknown Speed

### LAN Scan

Detecteaza subnetul local si scaneaza UDP 161 cu community string-ul ales. Sunt afisate device-urile SNMP gasite si clasificarea lor.

---

## Modulul Wi-Fi Analyzer

Wi-Fi Analyzer foloseste WLAN API pentru scanari reale de access point-uri. Cand serviciul WLAN nu este disponibil, UI-ul afiseaza o eroare explicita.

Afiseaza:

- SSID, BSSID, signal, channel si radio type
- Channel width si rate cand sunt disponibile
- Security si authentication
- Roaming history
- Cautare si filtrare rezultate
- WLAN report generat local

Modulul include si scanare LAN devices pentru context operational in reteaua curenta.

---

## Modulul Reports

Reports centralizeaza exporturile utile pentru ticketing.

### Export Reports

Genereaza in `%LOCALAPPDATA%\ISG Desk\Reports\`:

- HTML pentru browser
- TXT pentru ticket plain text
- JSON pentru analiza sau post-processing

### Copy Summary

Copiaza sumarul curent in clipboard pentru ticket.

### Open HTML / TXT / JSON

Deschide doar fisiere de raport suportate: `.html`, `.htm`, `.txt`, `.json`. Alte tipuri sunt respinse.

### WLAN Report

Genereaza raportul WLAN Windows intr-un folder dedicat pentru investigatii Wi-Fi.

---

## Internal profile and session data

Nu exista modul sau pagina Settings in produs. Aplicatia pastreaza doar date interne necesare pentru diagnoze, rapoarte si credentiale locale.

Locatii principale:

- Profil intern: `%LOCALAPPDATA%\ISG Desk\Data\network-profile.json`
- Credentiale SNMP DPAPI: `%LOCALAPPDATA%\ISG Desk\Data\snmp-credential.bin`
- Rapoarte: `%LOCALAPPDATA%\ISG Desk\Reports\`
- Loguri: `%LOCALAPPDATA%\ISG Desk\Logs\`
- Istoric Wi-Fi CSV: `%LOCALAPPDATA%\ISG Desk\WifiHistory\`

Targeturile recente nu se mai persista in `recent-targets.json`; ele raman doar in memoria sesiunii curente.

Profilul intern poate contine gateway, DNS si servere asteptate pentru reguli de diagnoza. Editarea lui nu este expusa ca modul GUI.

---

## Securitate credentiale

### SNMPv3 authPriv

Daca selectezi "Save credentials", credentialele SNMPv3 sunt criptate prin Windows DPAPI (`ProtectedData.Protect`) si salvate in profilul userului curent.

DPAPI inseamna:

- Doar userul curent poate decripta credentialele
- Fisierul mutat pe alt PC nu poate fi decriptat
- Daca profilul userului este sters, credentialele nu pot fi recuperate

"Forget credentials" sterge fisierul DPAPI si curata parolele din memorie.

### SNMPv2c

Community string-ul SNMPv2c nu este salvat criptat ca secret persistent. Este tratat ca input operational al sesiunii.

### Logging

Logurile Serilog sunt in `%LOCALAPPDATA%\ISG Desk\Logs\`, cu rolling daily, retentie 7 zile si limita de dimensiune per fisier. Parolele nu sunt logate.

---

## Troubleshooting

### App nu porneste

1. Inchide instanta veche `ISG Desk.exe` din Task Manager.
2. Reporneste aplicatia (nu mai apare prompt UAC la pornire; elevarea se face din butonul "Restart as administrator").
3. Verifica logurile din `%LOCALAPPDATA%\ISG Desk\Logs\`.

### Quick Diagnosis dureaza peste 60s

- PowerShell sau WMI pot fi lente pe sisteme incarcate.
- NIC-urile virtuale Hyper-V, VMware, Docker, WSL sau VPN pot lungi detectia.
- Ruleaza din nou dupa ce conexiunea fizica este stabila.

### SNMP timeout

- Verifica firewall-ul catre UDP 161.
- Verifica community string-ul sau credentialele SNMPv3.
- Confirma ca device-ul permite management SNMP de pe statia tehnicianului.

### Reports folder lipseste

Deschide pagina Reports si foloseste "Open Reports folder". Folderul este creat automat cand lipseste.

### Gateway ramane detecting

- Nu exista adapter UP cu gateway IPv4 valid.
- Adapterul detectat poate fi virtual sau VPN si este ignorat intentionat.

### Aplicatia nu mai raspunde dupa scan mare

- Apasa `Esc` sau foloseste butonul Stop cand exista un scan in derulare.
- Scanarile peste limita safety sunt respinse by design.

---

## Compatibilitate

- Windows 10 21H2+ sau Windows 11
- .NET 8 Desktop Runtime pentru build framework-dependent
- PowerShell 5.1+
- Porneste fara drepturi de administrator (asInvoker); elevarea este optionala, la un click, pentru BitLocker/TPM/S.M.A.R.T.

---

## Resurse

- [QUICKSTART.md](QUICKSTART.md) - onboarding rapid
- [implementation_plan_v2.md](implementation_plan_v2.md) - roadmap tehnica
- [progress.md](progress.md) - istoric implementare
