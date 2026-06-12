# ISG Desk - Quickstart (5 minute)

> IT Support & Network Diagnostics - diagnoza locala pentru helpdesk, cu evidenta, confidence si urmatorii pasi.

## Lansare

Dublu-click pe `ISG Desk.exe` sau shortcut-ul **ISG Desk** din meniul Start. Aplicatia porneste fara prompt UAC (merge si pe cont standard) si deschide fereastra principala cu sidebar in stanga si Technician Home in centru. Probele care cer privilegii (BitLocker, TPM, S.M.A.R.T. detaliat) se activeaza cu butonul **Restart as administrator** din Technician Home.

In stanga jos ai panel-ul Status: ping live la gateway, indicator Running si ultima activitate.

## Flux tipic helpdesk

1. Apasa `Ctrl+R` sau click **Diagnose This PC**.
2. Verifica Health Score, verdictul si recomandarile din pagina Diagnosis.
3. Pentru detaliu foloseste sectiunile Quick Diagnosis, Deep Ping & Path si Manual Port Test.
4. Apasa `Ctrl+E` ca sa exporti raport HTML/TXT/JSON pentru ticket.

## Comenzi keyboard

| Shortcut | Actiune |
|----------|---------|
| `Ctrl+R` / `F5` | Ruleaza Quick Diagnosis |
| `Ctrl+E` | Export rapoarte |
| `Ctrl+Shift+C` | Copy summary in clipboard |
| `Esc` | Stop / Cancel operatia curenta |
| `Ctrl+1` | Technician Home |
| `Ctrl+2` | Diagnosis |
| `Ctrl+3` | Targeted Tests |
| `Ctrl+4` | Printers |
| `Ctrl+5` | Network Devices |
| `Ctrl+6` | Wi-Fi Analyzer |
| `Ctrl+7` | Reports |

## Module

| Modul | Pentru ce |
|-------|-----------|
| Technician Home | Snapshot local read-only: device, user, network, security si ultima diagnoza |
| Diagnosis | Quick Diagnosis + Deep Ping + Port Test |
| Targeted Tests | Teste ghidate pentru share-uri, domeniu si servicii |
| Printers | Imprimante locale, print server, scan IP, SNMP, install queue |
| Network Devices | Switch/AP/router prin SNMP v2c/v3 si IF-MIB |
| Wi-Fi Analyzer | SSID, signal, channel, rates, roaming, WLAN report |
| Reports | Export HTML/TXT/JSON si folder de rapoarte |

Status dots: verde = OK, galben = warning, rosu = critical, fara dot = nu a rulat inca.

## Unde se salveaza datele

- Profil intern: `%LOCALAPPDATA%\ISG Desk\Data\network-profile.json`
- Recent targets: doar in sesiunea curenta; nu se mai salveaza pe disk
- Rapoarte: `%LOCALAPPDATA%\ISG Desk\Reports\` (pagina Reports -> Open Reports folder)
- Loguri: `%LOCALAPPDATA%\ISG Desk\Logs\`
- Istoric Wi-Fi CSV: `%LOCALAPPDATA%\ISG Desk\WifiHistory\`
- Credentiale SNMP: criptate prin DPAPI pentru userul curent

## About

Click pe logo-ul ISG Desk din sidebar pentru versiune si capabilitatile principale.

## Probleme comune

| Simptom | Solutie |
|---------|---------|
| Status dot lipseste | Modulul nu a rulat masuratori inca |
| Sparkline gol | Gateway-ul nu raspunde la ping sau nu este detectat |
| SNMP timeout | Verifica credentialele SNMP in Printers sau Network Devices |
| Reports nu apar | Deschide pagina Reports -> Open Reports folder |

Pentru detalii complete, vezi [USER_GUIDE.md](USER_GUIDE.md).
