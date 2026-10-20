# mqtt2PC

A Windows tray app that connects your PC to an existing MQTT broker on your local network.

Stage 2 includes Home Assistant discovery, volume and mute controls with actual Windows state reporting, media playback buttons, broker settings, automatic reconnect, online/offline availability, and a test notification command. Power controls and app automation come in stage 3. Startup registration and an installer are deferred; launch the app manually for now.

## Run

Requirements: Windows, the .NET 9 SDK to build, and an MQTT 5 broker (for example, a current Mosquitto installation). The generated executable requires the .NET 9 Desktop Runtime.

```powershell
dotnet build mqtt2PC.sln --configuration Release
dotnet run --project src/Mqtt2Pc --configuration Release --no-build
```

Alternatively, run `src/Mqtt2Pc/bin/Release/net9.0-windows/mqtt2PC.exe` after building. Only one instance runs per Windows user session.

On the first launch, the settings window opens. Enter your broker's hostname/IP, port, and credentials, then select **Save and connect**. Use a bare hostname such as `mqtt.home.lan`, without `mqtt://` or a port. Closing the settings window keeps the tray app running. Double-click its tray icon to reopen settings, or right-click for **Reconnect** and **Exit**. On subsequent launches, saved settings connect automatically.

Enable TLS when your broker supports it. The broker certificate must be valid for the configured hostname and trusted by Windows; certificate validation is never bypassed. Changing the TLS checkbox switches between the default ports 1883 and 8883, unless you chose a custom port.

Settings are saved in `%LOCALAPPDATA%\mqtt2PC\settings.json`. The password is stored separately in Windows Credential Manager under `mqtt2PC/<device-id>`. The password field is prefilled when settings open; clearing it and saving removes the stored password. Each saved installation has a stable generated device ID, independent of the editable PC name.

## Home Assistant controls

Exit the stage 1 app from its tray menu before launching `artifacts/stage2/mqtt2PC.exe`. Saved broker settings and credentials are reused without migration. Stage 1 and stage 2 share the single-instance check, so the older app must exit before the new app can start.

With MQTT discovery enabled in Home Assistant and the default discovery prefix `homeassistant`, connecting the app creates one device named after the configured **PC name**, containing:

| Entity | Behavior |
| --- | --- |
| Volume | Slider from 0–100% for the default Windows playback output |
| Mute | Switch that sets mute explicitly on or off |
| Play / pause | Sends the standard Windows play/pause media key |
| Next track | Sends the next-track media key |
| Previous track | Sends the previous-track media key |

Find the device under **Settings → Devices & services → MQTT → Devices**. Set the volume to a comfortable value, then change it from the Windows taskbar: Home Assistant should reflect the local change within about one second. Mute also reports the actual Windows state. Discovery and state are retained and republished after broker reconnects and Home Assistant's `homeassistant/status = online` birth message. Changing the PC name updates the existing device's discovery configuration while keeping stable entity IDs.

Audio controls always use the current default multimedia playback device, including after changing speakers/headsets. If there is no default output or the audio service is unavailable, volume and mute become unavailable; the PC's MQTT connection and media buttons continue to work. Audio availability recovers automatically when an output becomes available. The settings window shows the current volume/mute state.

Media handling depends on the signed-in Windows session and the player that handles system media keys. Buttons confirm that Windows accepted the key input; they do not report track metadata or playback state. Locked/secure desktops or elevated applications may prevent media input. Playback is not started or changed just by launching the app or reading its state.

If the device does not appear, check discovery is enabled and the broker account can publish `homeassistant/number/mqtt2pc_<device-id>/volume/config`, `homeassistant/switch/mqtt2pc_<device-id>/mute/config`, and `homeassistant/button/mqtt2pc_<device-id>/+/config`. It also needs to subscribe to `homeassistant/status` and its own `command/#` topics, and publish its `state/audio`, `availability`, `availability/audio`, and `result` topics. An ACL rejection is shown as a connection error in settings. Home Assistant needs the reverse topic permissions. Custom discovery prefixes and custom Home Assistant birth topics are not yet configurable.

Discovery uses Home Assistant's [MQTT discovery](https://www.home-assistant.io/integrations/mqtt/#mqtt-discovery), [number](https://www.home-assistant.io/integrations/number.mqtt/), [switch](https://www.home-assistant.io/integrations/switch.mqtt/), and [button](https://www.home-assistant.io/integrations/button.mqtt/) schemas.

## Test from Home Assistant

Use the **Test command** and **Results** topic fields in the app's settings. **Copy a fresh test payload** copies JSON with a new command ID.

In Home Assistant's Developer Tools, open **Actions**, choose `mqtt.publish`, and publish the copied payload to the app's test command topic. Keep **Retain disabled** and use QoS 1. For example, in YAML mode, replace `desktop-demo` with the actual device ID:

```yaml
action: mqtt.publish
data:
  topic: mqtt2pc/desktop-demo/command/test
  payload: '{"id":"first-test-1","message":"Hello from Home Assistant!"}'
  qos: 1
  retain: false
```

The PC requests a tray notification, and the settings window shows when the test arrived. Windows notification settings and Do Not Disturb can suppress the visible notification. A successful result confirms the request was made; it does not confirm that a person saw it.

Listen to `mqtt2pc/<device-id>/result` through Home Assistant's MQTT integration to see a JSON result:

```json
{
  "id": "first-test-1",
  "status": "success",
  "message": "Notification requested on the PC.",
  "timestamp": "2026-10-20T18:00:00+00:00"
}
```

Use a fresh `id` for each test. Duplicate IDs produce `status: duplicate` without another notification. The latest 1,024 IDs are remembered through automatic broker reconnects. Exiting the app or choosing **Reconnect** / **Save and connect** resets this in-memory history.

The original notification command remains compatible with stage 1; the new volume, mute, and media entities require no manual YAML configuration.

## MQTT contract

| Topic | Direction relative to the PC | Payload | Retained |
| --- | --- | --- | --- |
| `mqtt2pc/<device-id>/availability` | Publish | `online` / `offline` | Yes |
| `mqtt2pc/<device-id>/availability/audio` | Publish | Audio endpoint `online` / `offline` | Yes |
| `mqtt2pc/<device-id>/state/audio` | Publish | `{"volume":35,"muted":false}` | Yes |
| `mqtt2pc/<device-id>/command/test` | Subscribe | JSON with required `id`, optional `message` | Never |
| `mqtt2pc/<device-id>/command/volume` | Subscribe | Whole percent 0–100, e.g. `35` or `35.0` | Never |
| `mqtt2pc/<device-id>/command/mute` | Subscribe | `ON` / `OFF` | Never |
| `mqtt2pc/<device-id>/command/media/play_pause` | Subscribe | `PRESS`, QoS 0 | Never |
| `mqtt2pc/<device-id>/command/media/next` | Subscribe | `PRESS`, QoS 0 | Never |
| `mqtt2pc/<device-id>/command/media/previous` | Subscribe | `PRESS`, QoS 0 | Never |
| `mqtt2pc/<device-id>/result` | Publish | JSON result with `id`, `status`, `message`, `timestamp` | No |
| `homeassistant/<component>/mqtt2pc_<device-id>/<entity>/config` | Publish | Entity discovery JSON | Yes |
| `homeassistant/status` | Subscribe | Home Assistant birth message `online` | N/A |

The app uses MQTT 5 and clean sessions with zero session expiry. Discovery, state, availability, and results use QoS 1. Home Assistant publishes media buttons with QoS 0 to avoid retrying a non-idempotent action; retransmissions marked DUP are ignored for media. Volume and mute commands are explicit and idempotent. The app subscribes and publishes initial discovery/audio state before announcing `online`. Graceful exit publishes `offline`; an unexpected connection loss uses a retained offline Last Will. A broken network can take a keepalive interval to be detected; the configured keepalive is 15 seconds. Reconnect retries back off from about one second to a maximum of about 30 seconds.

Results also include `command_topic` to identify which command produced the result. Volume, mute, and media commands use `id: null`; notification commands preserve their supplied ID. Audio state is read back after commands, polled every second, and published when changed. Volume and mute use both the PC and audio availability topics; media uses PC availability only.

Test commands must be JSON objects of at most 1,024 UTF-8 bytes. IDs contain 1–64 ASCII letters, digits, hyphens or underscores. Messages contain 1–200 characters without control characters; omitting the message uses a default notification. Unknown JSON fields and invalid commands are rejected. Result statuses are `success`, `error`, `rejected`, and `duplicate`.

Stored retained commands are not delivered on subscription, and live commands with the retain flag are rejected. Commands published while the PC is offline are not queued for later execution. The receive queue holds at most 32 commands; excess commands and oversized payloads are dropped. Results are not stored or retried across a network loss.

The PC needs outbound access to the broker; it does not listen on a network port. Only the documented audio, media, and notification actions are accepted.

## Development and verification

```powershell
dotnet restore mqtt2PC.sln --locked-mode
dotnet build mqtt2PC.sln --configuration Release --no-restore
dotnet test mqtt2PC.sln --configuration Release --no-build --no-restore
```

`src/Mqtt2Pc.Core` owns settings validation, command parsing, and the MQTT lifetime. `src/Mqtt2Pc` owns the WinForms tray/settings UI and Windows Credential Manager access. MQTT communication uses [MQTTnet](https://github.com/dotnet/MQTTnet).

Integration tests start temporary MQTT brokers bound only to loopback. They cover authentication, retained state, discovery, Home Assistant birth messages, command validation, audio feedback, media routing, audio-device loss/recovery, and reconnects. A TCP relay drops the network without an MQTT disconnect to check the offline Last Will. Windows tests read the actual audio endpoint without changing volume or playback, check native media input layouts, create and remove a uniquely named test credential, and render the settings form without displaying a window. The render is saved to `artifacts/ui/settings-preview.png`. Automated command tests use a fake desktop backend so they cannot adjust your volume or start playback.

To create a portable, framework-dependent build folder:

```powershell
dotnet publish src/Mqtt2Pc --configuration Release --no-restore --output artifacts/stage2
```

Keep the whole output folder together. No broker address or password is included in the build.
