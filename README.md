# mqtt2PC

A Windows tray app that connects your PC to an existing MQTT broker on your local network.

Stage 1 includes broker settings, automatic reconnect, online/offline availability, and a test command that requests a Windows tray notification. Home Assistant discovery, media/volume controls, power controls, and app automation come in later stages. Startup registration and an installer are also deferred; launch the app manually for now.

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

No Home Assistant entities are discovered in stage 1; commands are published manually until stage 2.

## MQTT contract

| Topic | Direction relative to the PC | Payload | Retained |
| --- | --- | --- | --- |
| `mqtt2pc/<device-id>/availability` | Publish | `online` / `offline` | Yes |
| `mqtt2pc/<device-id>/command/test` | Subscribe | JSON with required `id`, optional `message` | Never |
| `mqtt2pc/<device-id>/result` | Publish | JSON result with `id`, `status`, `message`, `timestamp` | No |

The app uses MQTT 5, clean sessions with zero session expiry, and QoS 1. It subscribes before announcing `online`. Graceful exit publishes `offline`; an unexpected connection loss uses a retained offline Last Will. A broken network can take a keepalive interval to be detected; the configured keepalive is 15 seconds. Reconnect retries back off from about one second to a maximum of about 30 seconds.

Test commands must be JSON objects of at most 1,024 UTF-8 bytes. IDs contain 1–64 ASCII letters, digits, hyphens or underscores. Messages contain 1–200 characters without control characters; omitting the message uses a default notification. Unknown JSON fields and invalid commands are rejected. Result statuses are `success`, `error`, `rejected`, and `duplicate`.

Stored retained commands are not delivered on subscription, and live commands with the retain flag are rejected. Commands published while the PC is offline are not queued for later execution. The receive queue holds at most 32 commands; excess commands and oversized payloads are dropped. Results are not stored or retried across a network loss.

The PC needs outbound access to the broker; it does not listen on a network port. Broker permissions for its account should allow reading its command topic and writing its availability and result topics. Home Assistant needs the reverse permissions. The test command only requests a notification.

## Development and verification

```powershell
dotnet restore mqtt2PC.sln --locked-mode
dotnet build mqtt2PC.sln --configuration Release --no-restore
dotnet test mqtt2PC.sln --configuration Release --no-build --no-restore
```

`src/Mqtt2Pc.Core` owns settings validation, command parsing, and the MQTT lifetime. `src/Mqtt2Pc` owns the WinForms tray/settings UI and Windows Credential Manager access. MQTT communication uses [MQTTnet](https://github.com/dotnet/MQTTnet).

Integration tests start temporary MQTT brokers bound only to loopback, exercise authentication and retained state, and verify command handling and reconnects. A TCP relay test drops the network without an MQTT disconnect to check the offline Last Will. Windows tests create and remove a uniquely named test credential and render the settings form without displaying a window. The render is saved to `artifacts/ui/settings-preview.png`.

To create a portable, framework-dependent build folder:

```powershell
dotnet publish src/Mqtt2Pc --configuration Release --no-restore --output artifacts/stage1
```

Keep the whole output folder together. No broker address or password is included in the build.
