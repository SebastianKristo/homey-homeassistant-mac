# Mac Bridge – styr Macen (og Windows-PC-en) fra Home Assistant og Homey

Fire deler:

| Mappe | Hva |
|---|---|
| `mac-app/` | **MacBridge** – menylinje-app (Swift, ingen avhengigheter) som kjører et lokalt HTTP-API, annonseres via Bonjour (`_macbridge._tcp`) og kan pushe status til webhooks. |
| `windows-app/` | **MacBridge for Windows** – app i systemstatusfeltet (C#/.NET 8) med samme API, samme signerte protokoll og samme funksjoner som Mac-appen. |
| `custom_components/mac_bridge/` | Home Assistant-integrasjon (HACS-kompatibel). |
| `homey/` | Homey-app (SDK v3). |

## Funksjoner

- **Mediaspiller**: play/pause, neste/forrige, nå spilles (tittel, artist, album, posisjon, omslag fra Spotify) fra Musikk/Music og Spotify. Andre spillere kan styres via systemets medietaster (valgfri tillatelse).
- **Volum**: sett nivå, opp/ned, demp.
- **Strøm**: vekk (Wake-on-LAN + skjerm på), dvale, skjerm av, lås skjerm, restart, slå av (restart/slå av kan skrus av i menyen).
- **Statistikk**: CPU-bruk, minne (% og GB), disk (% og ledig GB), load, batteri og lading, temperaturstatus, oppetid/sist startet.
- **Ekstra**: `say` (tekst-til-tale), `notify` (varsel, med knapper) og `play_audio` (spiller TTS fra Home Assistant). Kan skrus av i menyen.
- **Sensorer**: se [Sensorer](#sensorer) under.

Alt under gjelder både Mac og Windows-PC, med mindre noe annet står. Se [Windows-appen](#1b-windows-appen) for det som er spesielt for Windows.

## 1. Mac-appen

Krever macOS 13+ og Xcode Command Line Tools.

```bash
cd mac-app
./scripts/build-app.sh
cp -r build/MacBridge.app /Applications/
open /Applications/MacBridge.app
```

**Eller som DMG:** `./scripts/make-dmg.sh` lager `build/MacBridge.dmg`. GitHub Actions bygger også DMG-en automatisk (under *Actions → Build MacBridge (Mac + Windows) → Artifacts*, og på releaser for `v*`-tagger sammen med Windows-exe-en).

Appen er ad-hoc-signert, ikke notarisert. Første gang: høyreklikk appen → **Åpne**, eller kjør `xattr -dr com.apple.quarantine /Applications/MacBridge.app`.

- Et skjerm-ikon dukker opp i menylinjen. Velg **Copy token** – det trengs i HA/Homey.
- Start Musikk og/eller Spotify, velg **Request permissions** og godta at MacBridge kan styre dem (Automatisering). Første restart/slå av ber om lov til å styre «loginwindow».
- Tilgjengelighet trengs **ikke**. Den er bare valgfri hvis du vil styre andre spillere (nettleser o.l.) med medietastene.
- Legg appen til i *Systeminnstillinger → Generelt → Påloggingsobjekter* så den starter automatisk.
- Standard port er `8765` (kan endres i menyen).

### API

Alle kall unntatt `/api/ping` må signeres. Tokenet sendes aldri over nettverket:

```
X-MacBridge-Timestamp: <unix-sekunder>
X-MacBridge-Nonce:     <16–64 tilfeldige hex-tegn, aldri gjenbrukt>
X-MacBridge-Signature: hex(HMAC-SHA256(token, "MacBridge-v1\n<METODE>\n<sti>\n<timestamp>\n<nonce>\n<sha256-hex av body>"))
```

Svaret har `X-MacBridge-Signature: hex(HMAC-SHA256(token, "MacBridge-v1-response\n<nonce>\n<status>\n<sha256-hex av body>"))`, slik at klienten vet at svaret kommer fra riktig Mac.

```
GET  /api/ping      (kun {"ok": true}, ingen enhetsinfo)
GET  /api/status
POST /api/command   {"command": "set_volume", "value": 40}
```

Kommandoer (fast liste, alt annet avvises): `play_pause play pause next previous set_volume volume_up volume_down mute unmute set_mute sleep display_sleep wake wake_display lock restart shutdown say notify play_audio stop_audio`.

`notify` tar `{"title": "...", "message": "..."}` eller en streng.

### Vekke Macen fra dvale

En Mac i dvale svarer ikke på nettverket, så «Vekk» sender en Wake-on-LAN-pakke til Macen (MAC-adressen lærer HA/Homey når Macen er våken). Slå på *Systeminnstillinger → Energi → Vekk for nettverkstilgang*. Det fungerer best med kabel; på Wi-Fi avhenger det av ruteren.

### Push (valgfritt, for umiddelbare oppdateringer)

I menyen **Push URLs** kan du legge inn http/https-URL-er som får status POST-et hvert 10. sekund, signert med tokenet (samme skjema, `MacBridge-v1-push\n<timestamp>\n<nonce>\n<sha256-hex av body>`). For Home Assistant:

```
http://<home-assistant>:8123/api/webhook/mac_bridge_<device-id>
```

`<device-id>` står i MacBridge-menyen (*Device ID*). Webhooken tar kun imot fra lokalt nettverk og bare signerte, ferske meldinger. Uten push poller HA og Homey selv (standard hvert 10. sekund).

## 1b. Windows-appen

Krever Windows 10 (versjon 2004 eller nyere) eller Windows 11, 64-bit. Ingen administratorrettigheter trengs.

1. Last ned `MacBridge-Windows.exe` fra [siste release](https://github.com/SebastianKristo/homey-homeassistant-mac/releases/latest) (eller fra *Actions → Build MacBridge (Mac + Windows) → Artifacts*). Legg den for eksempel i `%LOCALAPPDATA%\Programs\MacBridge\` og start den.
2. Filen er ikke kodesignert, så SmartScreen kan si «Windows beskyttet PC-en». Velg **Mer informasjon → Kjør likevel**.
3. Når Windows-brannmuren spør, tillat MacBridge på **private nettverk** (ikke offentlige).
4. Et ikon dukker opp i systemstatusfeltet (ved klokken, kanskje under ^). Høyreklikk og velg **Copy token**, og velg **Start with Windows** så den starter når du logger på.

Menyen har det samme som på Mac: versjon og port, Device ID, Copy token, Regenerate token, Change port, Push URLs, **Allow restart/shutdown**, **Allow speech and notifications** og **Last rejected** (siste avviste forespørsel og hvorfor). For å oppdatere starter du bare den nye exe-filen; den ber den gamle om å avslutte.

Hvordan funksjonene virker på Windows:

- **Media**: Windows' mediekontroller (samme som volum-panelet bruker), så Spotify, Medieavspilling, nettlesere o.l. fungerer med tittel/artist/album/posisjon. Uten aktiv spiller sendes medietastene.
- **Volum/demp**: standard avspillingsenhet.
- **Dvale, skjerm av/på, lås, restart, slå av**: vanlige Windows-API-er. Restart og slå av gjøres på vanlig måte (programmer får lagre; ingenting tvinges).
- **Vekke fra dvale**: Wake-on-LAN, som på Mac. Slå på *Wake on Magic Packet* for nettverkskortet i Enhetsbehandling (Strømstyring/Avansert), og ev. Wake-on-LAN i BIOS/UEFI. Fungerer best med kabel.
- **Statistikk**: CPU, minne, disk, batteri (bærbare), oppetid. Windows har ikke load average eller «temperaturstatus», så de mangler.
- **`say`/`notify`**: Windows' talesyntese og et varsel fra ikonet.

Bygge selv (krever .NET 8 SDK): `dotnet publish windows-app/MacBridge.Windows.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`.

## 2. Home Assistant

1. Kopier `custom_components/mac_bridge` til `config/custom_components/` (eller legg til repoet som *custom repository* i HACS).
2. Start HA på nytt. Macen/PC-en blir oppdaget automatisk via Zeroconf – ellers *Innstillinger → Enheter og tjenester → Legg til → Mac Bridge*.
3. Lim inn token.

Du får:

- `media_player.<mac>` – spiller med tittel/artist/album/omslag, neste/forrige, volum, demp, av = dvale, på = vekk
- `number.<mac>_volume` – volum-slider
- `button.*` – vekk (fungerer også når Macen sover), dvale, skjerm av, vekk skjerm, lås, restart, slå av, play/pause, neste, forrige, demp
- `sensor.*` – CPU, minne, disk, load, batteri, temperaturstatus, sist startet, spilles nå
- `binary_sensor.*` – online, lader
- Tjeneste `mac_bridge.send_command`:

```yaml
action: mac_bridge.send_command
data:
  device_id: <din mac>
  command: say
  value: "Middagen er klar"
```

## 3. Homey

```bash
npm install -g homey
cd homey
homey app install      # eller: homey app run
```

Legg til enhet: *Mac Bridge → Mac mini*, *MacBook Pro* eller *Windows PC* (hver har sitt eget ikon). Oppdagede maskiner av riktig type vises i listen; lim inn token.

Enheten er en høyttaler med play/pause, neste/forrige, volum, demp, spor/artist/album, «Våken» (av = dvale, på = vekk), knappene **Vekk**, **Dvale**, **Start på nytt** og **Slå av**, samt CPU, minne, disk, ledig disk, load, temperaturstatus og batteri (kun bærbare). Windows PC har de samme, unntatt load og temperaturstatus.

Flows:
- **Handlinger**: vekk (Wake-on-LAN), dvale, skjerm av, vekk skjerm, lås, restart, slå av, play/pause, neste, forrige, si tekst, vis varsel
- **Triggere**: CPU gikk over X %, Macen gikk offline, Macen kom online (+ alle standard kapabilitet-triggere)

## Sensorer

Mac- og Windows-appen rapporterer disse i tillegg til CPU/minne/disk/batteri. HA og Homey lager bare entiteter for det datamaskinen faktisk rapporterer.

| Sensor | Mac | Windows | HA-entitet |
|---|---|---|---|
| Aktiv app (app i forgrunnen, ikke vindustittel) | ✓ | ✓ | `sensor.*_active_app` |
| Sist aktiv / inaktiv tid | ✓ | ✓ | `sensor.*_last_active`, `sensor.*_idle_time` |
| Innlogget bruker / brukere | ✓ | ✓ | `sensor.*_logged_in_user(s)` |
| Låst skjerm | ✓ | ✓ | `binary_sensor.*_locked` |
| Skjerm på/av | ✓ | ✓ | `binary_sensor.*_display_on` |
| Siste systemendring (dvale, vekk, låst, skjerm av …) | ✓ | ✓ | `sensor.*_last_system_state_change` + hendelsen `mac_bridge_state_change` |
| GPU-bruk | ✓ | ✓ | `sensor.*_gpu_usage` |
| CPU-klokkefrekvens | – (finnes ikke på Apple silicon) | ✓ | `sensor.*_cpu_clock_speed` |
| Mikrofon i bruk | ✓ | ✓ (+ hvilke apper) | `binary_sensor.*_microphone_in_use`, `sensor.*_apps_using_microphone` |
| Kamera i bruk | ✓ | ✓ (+ hvilke apper) | `binary_sensor.*_webcam_in_use`, `sensor.*_apps_using_webcam` |
| Lydutgang/-inngang (standardenhet) | ✓ | ✓ | `sensor.*_audio_output`, `sensor.*_audio_input` |
| Skjermer (antall, oppløsning) | ✓ | ✓ | `sensor.*_displays` |
| Aksentfarge | ✓ | ✓ | `sensor.*_accent_color` |
| Skrivere | ✓ | ✓ | `sensor.*_printers` |
| Nettverk (IP og grensesnitt) | ✓ | ✓ | `sensor.*_ip_address` |
| Bluetooth-enheter (tilkoblede) | ✓ valgfri | ✓ valgfri (+ BLE) | `sensor.*_bluetooth_devices` |
| Varslingsstatus (presentasjon, fullskjerm, stille tid …) | – | ✓ | `sensor.*_notification_state` |
| Ventende Windows-oppdateringer (sjekkes hver 6. time) | – | ✓ | `sensor.*_pending_windows_updates` |
| Kjører program X | ✓ valgfri | ✓ valgfri | `binary_sensor.*_<navn>_running` |
| Tilstand for Windows-tjeneste X | – | ✓ valgfri | `sensor.*_service_<navn>` |

«Valgfri» skrus på i menylinjen/systemstatusfeltet (**Report Bluetooth devices**, **Watched apps/programs…**, **Watched services…**). Listen over programmer og tjenester kan bare endres lokalt på datamaskinen, ikke fra nettverket.

**Ikke tatt med, med vilje:** PowerShell, WMI-spørringer og egendefinerte ytelsestellere (lar hvem som helst med tokenet kjøre vilkårlige kommandoer/spørringer), skjermbilde og posisjon (personvern), vindustitler (kan inneholde e-postemner o.l.), aktivt virtuelt skrivebord (ingen offentlig API), GPU-temperatur (ingen offentlig API uten tredjepartsdrivere) og BLE-skanning (kun tilkoblede enheter rapporteres).

## Varsler med knapper

Hver datamaskin får en egen varseltjeneste `notify.<navn>`:

```yaml
action: notify.studio
data:
  title: Støvsugeren er full
  message: Vil du pause den eller sende den hjem?
  data:
    actions:
      - action: pause
        title: Pause
      - action: home
        title: Send hjem
      - action: openuri
        title: Åpne app
        uri: https://example.com
```

Maks 4 knapper. En knapp med `uri` åpner lenken (kun `http`/`https`) når du klikker på den. Et klikk sendes til Home Assistant som hendelsen `mac_bridge_notification_action`:

```yaml
triggers:
  - trigger: event
    event_type: mac_bridge_notification_action
    event_data:
      action: pause
actions:
  - action: vacuum.pause
    target:
      entity_id: vacuum.roborock
```

Hendelsen har også `device_id`, `name` og `title`. Den kommer fram ved neste oppdatering (standard hvert 10. sekund), eller med en gang hvis push er satt opp. I Homey: handlingen **Vis varsel med knapper** og triggeren **En varselknapp ble trykket** (med knappen som token).

På Mac må du godta varsler fra MacBridge første gang. På Windows brukes vanlige Windows-varsler (toast).

## Tekst-til-tale og lyd fra Home Assistant

Mediaspilleren kan spille av lyd fra Home Assistant, f.eks. tekst-til-tale:

```yaml
action: tts.speak
target:
  entity_id: tts.google_translate_no_no
data:
  media_player_entity_id: media_player.studio
  message: Middagen er klar
```

Datamaskinen laster bare ned lyd fra den samme maskinen som sendte den signerte forespørselen (Home Assistant), følger ingen omdirigeringer og godtar maks 20 MB. Home Assistant må ha en intern URL som datamaskinen når (*Innstillinger → System → Nettverk*).

## Ikoner

Alle ikoner genereres med `python3 tools/generate_icons.py` (krever Pillow): app-ikon (`.icns`), DMG-bakgrunn, Homey-bilder og Home Assistant-ikon i `custom_components/mac_bridge/brand/`. Nyere Home Assistant-versjoner viser `brand/`-ikonene for egne integrasjoner; i eldre versjoner krever det at ikonet legges inn i home-assistant/brands.

## Sikkerhet

MacBridge kan bare gjøre det som står i kommandolisten over. Konkret:

- **Ingen vilkårlig kjøring**: ingen kommando tar en sti, URL, script eller kommandolinje fra nettverket. `open_url` er fjernet. Tekst til `say`/`notify` lengdebegrenses og sendes som egne argumenter, aldri limt inn i et script.
- **Signerte forespørsler**: HMAC-SHA256 med tidsstempel (±60 s) og engangs-nonce, så tokenet aldri går over nettverket og opptak ikke kan spilles av på nytt. Svarene er også signert, så en falsk «Mac» på nettet blir avvist av HA og Homey.
- **Kun lokalt nett**: serveren godtar bare tilkoblinger fra private/lokale adresser, begrenser antall samtidige tilkoblinger, størrelse og tid, og stenger ute adresser etter 10 feilede forsøk (5 min).
- **Token i Nøkkelring** (ikke i en innstillingsfil). «Copy token» kopierer bare til denne Macen (ikke Universal Clipboard) og tømmes etter ett minutt.
- **Minst mulig rettigheter**: ingen Tilgjengelighet/System Events. Hardened runtime med én rettighet (Apple Events), som macOS uansett spør om for hver app (Musikk, Spotify, loginwindow).
- **Ingen personlig info ut**: serienummeret deles ikke (enheten identifiseres med en anonym hash). `/api/ping` viser ingen enhetsinfo, og Bonjour bare Mac-navnet og modellen.
- Restart/slå av og tale/varsler kan skrus av i menyen.
- **Windows**: samme regler. Appen kjører som vanlig bruker (aldri administrator), starter ingen andre programmer og bygger ingen kommandolinjer – hver kommando er ett fast Windows-API-kall. Tokenet ligger i Windows Legitimasjonsbehandling (kun denne brukeren på denne PC-en, ikke synkronisert). «Copy token» holdes utenfor utklippstavlehistorikken og skyutklippstavlen og tømmes etter ett minutt. Enhets-ID-en er en hash av Windows' MachineGuid.
- Trafikken er ikke kryptert (bare signert), så status som «spilles nå» kan sees av andre på samme nett. Ikke eksponer porten mot internett.

### Oppgradering fra 1.0

1.1 bruker signerte forespørsler, så Mac-appen, Home Assistant-integrasjonen og Homey-appen må oppdateres samtidig. Eksisterende oppsett fortsetter med samme token. Push-URL til HA må endres til `mac_bridge_<device-id>` for nye oppsett (eldre oppsett beholder serienummer-ID-en).
