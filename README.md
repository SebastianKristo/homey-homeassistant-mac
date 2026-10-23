# Mac Bridge – styr Macen fra Home Assistant og Homey

Tre deler:

| Mappe | Hva |
|---|---|
| `mac-app/` | **MacBridge** – menylinje-app (Swift, ingen avhengigheter) som kjører et lokalt HTTP-API, annonseres via Bonjour (`_macbridge._tcp`) og kan pushe status til webhooks. |
| `custom_components/mac_bridge/` | Home Assistant-integrasjon (HACS-kompatibel). |
| `homey/` | Homey-app (SDK v3). |

## Funksjoner

- **Mediaspiller**: play/pause, neste/forrige, nå spilles (tittel, artist, album, posisjon) fra Music og Spotify. Andre spillere styres via systemets medietaster.
- **Volum**: sett nivå, opp/ned, demp.
- **Strøm**: dvale, skjerm av/på, lås skjerm, restart, slå av (restart/slå av kan skrus av i menyen).
- **Statistikk**: CPU-bruk, minne (% og GB), disk (% og ledig GB), load, batteri og lading, temperaturstatus, oppetid/sist startet.
- **Ekstra**: `say` (tekst-til-tale), `notify` (macOS-varsel), `open_url`.

## 1. Mac-appen

Krever macOS 13+ og Xcode Command Line Tools.

```bash
cd mac-app
./scripts/build-app.sh
cp -r build/MacBridge.app /Applications/
open /Applications/MacBridge.app
```

- Et skjerm-ikon dukker opp i menylinjen. Velg **Copy token** – det trengs i HA/Homey.
- Velg **Request permissions** og godta Automation (System Events, Music, Spotify) og Tilgjengelighet (for medietaster og lås skjerm).
- Legg appen til i *Systeminnstillinger → Generelt → Påloggingsobjekter* så den starter automatisk.
- Standard port er `8765` (kan endres i menyen).

### API

Alle kall unntatt `/api/ping` krever `Authorization: Bearer <token>`.

```
GET  /api/ping
GET  /api/status
POST /api/command   {"command": "set_volume", "value": 40}
```

Kommandoer: `play_pause play pause next previous set_volume volume_up volume_down mute unmute set_mute sleep display_sleep wake_display lock restart shutdown say notify open_url`.

`notify` tar `{"title": "...", "message": "..."}` eller en streng.

### Push (valgfritt, for umiddelbare oppdateringer)

I menyen **Push URLs** kan du legge inn URL-er som får status POST-et hvert 10. sekund (med token i `Authorization`). For Home Assistant:

```
http://<home-assistant>:8123/api/webhook/mac_bridge_<serienummer>
```

Serienummeret er det samme som enhetens serienummer i HA (Enhetsinfo). Uten push poller HA og Homey selv (standard hvert 10. sekund).

## 2. Home Assistant

1. Kopier `custom_components/mac_bridge` til `config/custom_components/` (eller legg til repoet som *custom repository* i HACS).
2. Start HA på nytt. Macen blir oppdaget automatisk via Zeroconf – ellers *Innstillinger → Enheter og tjenester → Legg til → Mac Bridge*.
3. Lim inn token.

Du får:

- `media_player.<mac>` – spiller, volum, demp, av = dvale, på = vekk skjerm
- `number.<mac>_volume` – volum-slider
- `button.*` – dvale, skjerm av, vekk skjerm, lås, restart, slå av, play/pause, neste, forrige, demp
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

Legg til enhet: *Mac Bridge → Mac*. Oppdagede Macer vises i listen; lim inn token.

Enheten er en høyttaler med play/pause, neste/forrige, volum, demp, spor/artist/album, «Våken» (av = dvale), samt CPU, minne, disk, ledig disk, load, temperaturstatus og batteri (kun bærbare).

Flows:
- **Handlinger**: dvale, skjerm av, vekk skjerm, lås, restart, slå av, play/pause, si tekst, vis varsel, åpne URL
- **Triggere**: CPU gikk over X %, Macen gikk offline, Macen kom online (+ alle standard kapabilitet-triggere)

## Sikkerhet

API-et er kun ment for lokalt nettverk og bruker et tilfeldig token. Ikke eksponer porten mot internett. Restart/slå av kan skrus av i menylinje-appen.
