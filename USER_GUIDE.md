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

Aplicatia cere drepturi de administrator la pornire prin `app.manifest`. Pastram acest comportament pentru testare usoara si pentru operatii Windows care pot cere privilegii ridicate.

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
- Ultima diagnoza si actiuni rapide catre modulele principale

Technician Home nu este o copie a meniului si nu ruleaza scanari de retea la incarcare. Colecteaza doar informatii locale read-only si afiseaza sumarul ultimei diagnoze cand exista.

---

## Diagnosis

Diagnosis este modulul fuzionat pentru diagnoza PC-ului: Quick Diagnosis, Deep Ping & Path si Manual Port Test sunt in aceeasi pagina.

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

### Deep Ping & Path

Ruleaza ping/jitter pentru un target ales:

- Gateway, internet IP sau host privat autorizat
- 1-50 samples
- Packet loss, min/avg/max latency si jitter
- Praguri diferite pentru gateway, internet, target si DNS

### Manual Port Test

Testeaza un `host:port` manual:

- Porturi rapide: 22, 80, 443, 445, 3389, 9100, 161
- Verdict: open, refused sau timeout
- Latenta pana la connect/refuse

---

## Targeted Tests

Targeted Tests ruleaza doar cele 3 scenarii care au nevoie de un target concret. Ele refolosesc Quick Diagnosis baseline si adauga verificari specifice.

| Test | Input | Ce verifica |
|----------|-------|-------------|
| Internal Server or Share Access | server/UNC list | DNS, ping si TCP 445 |
| DNS/Domain | DC override optional | DC discovery, Kerberos 88, LDAP 389, DNS lookup |
| Service Access | host + porturi | DNS, ping si TCP pentru porturi specifice |

Testele cu input obligatoriu refuza rularea cand lipseste targetul. Targeturile recente sunt pastrate doar in memoria sesiunii curente, nu intr-un modul separat.

---

## Modulul Printers

### Local Printers

Citeste imprimantele instalate local si starea spoolerului.

### Print Server Discovery

Se conecteaza la un print server UNC si listeaza queue-urile shared disponibile.

### Auto Safe Scan

Detecteaza subnetul local si scaneaza controlat un `/24`:

- 9100 RAW
- 515 LPR
- 631 IPP
- 80/443 pentru web UI

Rezultatul include clasificare heuristica pentru posibile imprimante.

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

Network Devices este centrat pe SNMP pentru switch-uri, AP-uri, routere, firewall-uri si alte device-uri.

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
2. Reporneste aplicatia si accepta promptul UAC.
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
- Drepturi administrator la pornire, prin manifest

---

## Resurse

- [QUICKSTART.md](QUICKSTART.md) - onboarding rapid
- [implementation_plan_v2.md](implementation_plan_v2.md) - roadmap tehnica
- [progress.md](progress.md) - istoric implementare
