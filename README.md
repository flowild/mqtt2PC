# mqtt2PC

A Windows tray app that connects your PC to an existing MQTT broker on your local network.

Stage 4 adds a per-user Windows installer, optional start at sign-in, and rolling diagnostic logs. It includes configurable power controls and named program/batch/script actions, Home Assistant discovery, volume/mute state reporting, media buttons, automatic reconnect, and online/offline availability.

## Install and run

Requirements: Windows 10/11 x64, the .NET 9 Desktop Runtime (x64), and an MQTT 5 broker. The installer and portable build are framework-dependent: they do not bundle or install the .NET runtime. Building from source also requires the .NET 9 SDK.

Exit the running mqtt2PC app from its tray menu, then open `artifacts/stage4/mqtt2PC-0.4.0-x64.msi`. It installs for your Windows account under `%LOCALAPPDATA%\Programs\mqtt2PC` and adds a Start menu shortcut. Start mqtt2PC from that shortcut after installation. Installation does not enable startup, launch the app, or change existing broker settings, credentials, device IDs, or configured actions.

Alternatively, extract `artifacts/stage4/mqtt2PC-0.4.0-portable.zip` and run `mqtt2PC.exe` from the extracted folder. The unpacked build is also available at `artifacts/stage4/portable/mqtt2PC.exe`. Keep its files together and use a stable folder if you enable startup. Release checksums are in `artifacts/stage4/SHA256SUMS.txt`. The locally built MSI is unsigned.

For development:

```powershell
dotnet build mqtt2PC.sln --configuration Release
dotnet run --project src/Mqtt2Pc --configuration Release --no-build
```

Alternatively, run `src/Mqtt2Pc/bin/Release/net9.0-windows/mqtt2PC.exe` after building. Only one instance runs per Windows user session.

On the first launch, the settings window opens. Enter your broker's hostname/IP, port, and credentials, then select **Save and connect**. Use a bare hostname such as `mqtt.home.lan`, without `mqtt://` or a port. Closing the settings window keeps the tray app running. Double-click its tray icon to reopen settings, or right-click for **Reconnect** and **Exit**. On subsequent launches, saved settings connect automatically.

Enable TLS when your broker supports it. The broker certificate must be valid for the configured hostname and trusted by Windows; certificate validation is never bypassed. Changing the TLS checkbox switches between the default ports 1883 and 8883, unless you chose a custom port.

Settings are saved in `%LOCALAPPDATA%\mqtt2PC\settings.json`. The password is stored separately in Windows Credential Manager under `mqtt2PC/<device-id>`. The password field is prefilled when settings open; clearing it and saving removes the stored password. Each saved installation has a stable generated device ID, independent of the editable PC name.

## Startup, diagnostics and uninstall

In **Settings → Startup and diagnostics**, select **Start mqtt2PC when I sign in to Windows**, then **Save and connect**. Uncheck it and save to disable startup. This creates/removes the current user's `mqtt2PC.lnk` in the Windows Startup folder, targeting this copy of the app with `--startup`. Windows may delay startup, and Windows Startup settings or Task Manager can independently disable an entry. No Windows service or administrator registration is used; controls run in your signed-in session.

Startup launches use saved settings without opening the settings window. A duplicate startup instance exits silently. Missing or invalid settings produce a tray notice rather than blocking sign-in with a configuration dialog; open Settings from the tray to resolve them. Moving a portable build requires re-enabling startup from its new location.

Select **Open log folder** from the tray menu or Startup and diagnostics tab. Logs live in `%LOCALAPPDATA%\mqtt2PC\logs\mqtt2pc.log`, with four rolling archives (`.1` through `.4`), each limited to approximately 1 MiB. Entries are JSON lines with a UTC timestamp, severity, event code, and optional safe diagnostic code, exception type, and HRESULT. They record connection/retry events, audio availability, and action outcomes. Passwords, usernames, MQTT payloads/request IDs, executable paths, program arguments, and exception messages/stack traces are not recorded. A full/unwritable log directory does not stop MQTT control.

For troubleshooting, look for `mqtt.connection_failed`, `mqtt.retry_scheduled`, `action.failed`, or `command.result`. Connection failures include broker enum codes such as `BadUserNameOrPassword` and `Subscribe: NotAuthorized` when supplied by the broker. Action success confirms a request or process launch, not program completion. Unexpected app/UI exceptions are also recorded using their type and HRESULT.

Uninstall through **Windows Settings → Apps → Installed apps → mqtt2PC** after exiting the tray app. Uninstall removes installed binaries, the Start menu shortcut, and mqtt2PC's startup shortcut. Broker settings, discovery history, credentials, and logs are kept so reinstalling reuses your configuration. Upgrades, including rebuilt installers with the same version, preserve the startup shortcut and the stable install path. Home Assistant's retained device configuration remains available for reuse; an uninstalled PC is shown offline.

## Home Assistant controls

Saved broker settings, credentials, device ID, and existing media/audio entities are reused when upgrading. Settings from before stage 3 load with power controls disabled and no configured actions. All builds share the single-instance check, so exit an older copy before starting the installed or updated copy.

With MQTT discovery enabled in Home Assistant and the default discovery prefix `homeassistant`, connecting the app creates one device named after the configured **PC name**, containing:

| Entity | Behavior |
| --- | --- |
| Volume | Slider from 0–100% for the default Windows playback output |
| Mute | Switch that sets mute explicitly on or off |
| Play / pause | Sends the standard Windows play/pause media key |
| Next track | Sends the next-track media key |
| Previous track | Sends the previous-track media key |
| Lock / Sleep / Shutdown / Restart | Buttons appear for the power controls enabled locally |
| Configured actions | A button for each enabled program/script action |

Find the device under **Settings → Devices & services → MQTT → Devices**. Set the volume to a comfortable value, then change it from the Windows taskbar: Home Assistant should reflect the local change within about one second. Mute also reports the actual Windows state. Discovery and state are retained and republished after broker reconnects and Home Assistant's `homeassistant/status = online` birth message. Changing the PC name updates the existing device's discovery configuration while keeping stable entity IDs.

Audio controls always use the current default multimedia playback device, including after changing speakers/headsets. If there is no default output or the audio service is unavailable, volume and mute become unavailable; the PC's MQTT connection and media buttons continue to work. Audio availability recovers automatically when an output becomes available. The settings window shows the current volume/mute state.

Media handling depends on the signed-in Windows session and the player that handles system media keys. Buttons confirm that Windows accepted the key input; they do not report track metadata or playback state. Locked/secure desktops or elevated applications may prevent media input. Playback is not started or changed just by launching the app or reading its state.

If the device does not appear, check discovery is enabled and the broker account can publish `homeassistant/number/mqtt2pc_<device-id>/volume/config`, `homeassistant/switch/mqtt2pc_<device-id>/mute/config`, and `homeassistant/button/mqtt2pc_<device-id>/+/config`. It also needs to subscribe to `homeassistant/status` and its own `command/#` topics, and publish its `state/audio`, `availability`, `availability/audio`, and `result` topics. An ACL rejection is shown as a connection error in settings. Home Assistant needs the reverse topic permissions. Custom discovery prefixes and custom Home Assistant birth topics are not yet configurable.

Discovery uses Home Assistant's [MQTT discovery](https://www.home-assistant.io/integrations/mqtt/#mqtt-discovery), [number](https://www.home-assistant.io/integrations/number.mqtt/), [switch](https://www.home-assistant.io/integrations/switch.mqtt/), and [button](https://www.home-assistant.io/integrations/button.mqtt/) schemas.

## Configure power and app automation

Open tray **Settings → Power and actions**. Select the power controls you want to expose, then select **Save and connect**. All four are disabled until you enable them. Saving settings only configures buttons; it does not perform a power action or launch a program.

Lock requests a Windows workstation lock. Sleep requests suspend with wake events enabled. Shutdown and restart request an immediate transition without force-closing applications, so Windows may keep the PC running for unsaved-work prompts. Windows permissions, power policies, and hardware determine whether a request succeeds. Sleep sends an `accepted` result before the transition; a final result may not arrive until resume, or may be lost when the network disconnects. `accepted` confirms receipt, and `success` means Windows accepted the request, rather than proving the transition finished. The app cannot receive MQTT while the PC is off or asleep; waking it requires a separate sender such as Home Assistant's [Wake-on-LAN integration](https://www.home-assistant.io/integrations/wake_on_lan/).

To add a program or script action:

1. Select **Add…**, enter a display name and an action ID such as `start_notepad`.
2. Browse to an absolute `.exe`, `.com`, or `.bat` path, for example `C:\Windows\System32\notepad.exe` or `C:\Scripts\MyAutomation.bat`.
3. Enter fixed arguments, one argument per line. A path containing spaces stays on one line, without surrounding quotes. Empty argument lines are preserved as empty arguments; leaving the entire field empty supplies no arguments.
4. Optionally choose a working folder; otherwise the executable's folder is used.
5. Keep **Expose this action in Home Assistant** checked, save the action, then select **Save and connect** in Settings.

Batch (`.bat`) files can be selected directly in the Executable field. They run through Windows `cmd.exe` automatically, with the batch file's folder as the default working folder. Fixed arguments still use one line per argument. Batch arguments follow `cmd.exe` quoting rules; the launcher quotes each argument, disables AutoRun commands, and disables delayed expansion. As with all actions, a successful launch confirms the process started, rather than confirming the script completed successfully.

Batch support is included in the stage 4 installer and portable build.

Other script types use a locally configured interpreter. For example, choose `C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe` and enter these three arguments on separate lines:

```text
-NoProfile
-File
C:\Scripts\MyAutomation.ps1
```

The interpreter's usual execution policy and Windows permissions still apply. Executables and arguments are stored in the local settings file. Executable arguments are passed as individual values; batch files use `cmd.exe`. Console programs run without creating a console window. GUI programs run in the signed-in user's session, with the same permissions as mqtt2PC. There is no automatic elevation. A launch result includes `process_id` and confirms the process started; it does not wait for program completion or track whether an application is still running. Started programs continue if the tray app exits.

You can configure up to 32 actions. IDs are unique and remain fixed when editing an existing action, while display names and launch settings can change. Uncheck an action to disable it, or select it and choose **Remove**, then **Save and connect**. Disabled/removed actions reject MQTT invocation and their retained Home Assistant discovery entries are deleted. A local `discovery-<device-id>.json` ledger beside settings remembers advertised topics so cleanup also works across app restarts. Keep that file with the settings.

The Home Assistant buttons generate a fresh request ID automatically. For manual publishing, supply only a unique `id`; the topic selects the enabled, locally configured action. MQTT cannot supply an executable, arguments, or a working directory. For example, after configuring `start_notepad`:

```yaml
action: mqtt.publish
data:
  topic: mqtt2pc/desktop-demo/command/action/start_notepad
  payload: '{"id":"launch-test-1"}'
  qos: 1
  retain: false
```

Replace `desktop-demo` with the actual device ID and use a new request ID for each invocation. Power topics similarly accept `{"id":"unique-request-id"}` and must be enabled locally. Unknown controls, disabled actions, extra JSON fields, and retained commands are rejected. Requests are not executed merely by reconnecting or receiving discovery messages.

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

The original notification command remains compatible with stage 1. Discovered controls and enabled configured actions require no manual YAML configuration.

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
| `mqtt2pc/<device-id>/command/power/lock` | Subscribe | JSON with a unique `id`, if enabled | Never |
| `mqtt2pc/<device-id>/command/power/sleep` | Subscribe | JSON with a unique `id`, if enabled | Never |
| `mqtt2pc/<device-id>/command/power/shutdown` | Subscribe | JSON with a unique `id`, if enabled | Never |
| `mqtt2pc/<device-id>/command/power/restart` | Subscribe | JSON with a unique `id`, if enabled | Never |
| `mqtt2pc/<device-id>/command/action/<action-id>` | Subscribe | JSON with a unique `id`, if enabled | Never |
| `mqtt2pc/<device-id>/result` | Publish | JSON result with `id`, `status`, `message`, `timestamp` | No |
| `homeassistant/<component>/mqtt2pc_<device-id>/<entity>/config` | Publish | Entity discovery JSON | Yes |
| `homeassistant/status` | Subscribe | Home Assistant birth message `online` | N/A |

The app uses MQTT 5 and clean sessions with zero session expiry. Discovery, state, availability, and results use QoS 1. Home Assistant publishes media buttons with QoS 0 to avoid retrying a non-idempotent action; retransmissions marked DUP are ignored for media. Volume and mute commands are explicit and idempotent. The app subscribes and publishes initial discovery/audio state before announcing `online`. Graceful exit publishes `offline`; an unexpected connection loss uses a retained offline Last Will. A broken network can take a keepalive interval to be detected; the configured keepalive is 15 seconds. Reconnect retries back off from about one second to a maximum of about 30 seconds.

Results include `command_topic` to identify which command produced the result, plus `process_id` for a successful configured launch (null for other results). Volume, mute, and media commands use `id: null`; notification, power, and configured actions preserve their supplied ID. Audio state is read back after commands, polled every second, and published when changed. Volume and mute use both the PC and audio availability topics; other controls use PC availability only.

Notification, power, and configured-action commands must be JSON objects of at most 1,024 UTF-8 bytes. Request IDs contain 1–64 ASCII letters, digits, hyphens or underscores. Notification messages contain 1–200 characters without control characters; omitting the message uses a default notification. Power/action JSON contains only `id`. Unknown JSON fields and invalid commands are rejected. Result statuses are `accepted` (power requests), `success`, `error`, `rejected`, and `duplicate`.

The last 1,024 handled (command topic, request ID) pairs are remembered across automatic reconnects, including failed invocations. Reusing an ID on that topic does not retry an operation. This history resets when the app exits or settings/reconnect creates a new agent; it is not a persistent exactly-once guarantee. Use a fresh ID to retry a failed operation intentionally.

Stored retained commands are not delivered on subscription, and live commands with the retain flag are rejected. Commands published while the PC is offline are not queued for later execution. The receive queue holds at most 32 commands; excess commands and oversized payloads are dropped. Results are not stored or retried across a network loss.

The PC needs outbound access to the broker; it does not listen on a network port. Only the documented audio, media, notification, enabled power controls, and enabled configured actions are accepted. Broker read permissions must cover `mqtt2pc/<device-id>/command/#`, including the power/action subtopics; discovery permissions must allow the additional button configuration topics and their empty removal payloads.

## Development and verification

```powershell
dotnet restore mqtt2PC.sln --locked-mode
dotnet build mqtt2PC.sln --configuration Release --no-restore
dotnet test mqtt2PC.sln --configuration Release --no-build --no-restore
```

`src/Mqtt2Pc.Core` owns settings validation, command parsing, and the MQTT lifetime. `src/Mqtt2Pc` owns the WinForms tray/settings UI and Windows Credential Manager access. MQTT communication uses [MQTTnet](https://github.com/dotnet/MQTTnet).

Integration tests start temporary MQTT brokers bound only to loopback. They cover authentication, retained state, discovery and cleanup, Home Assistant birth messages, command validation, disabled controls, repeat requests, fixed action arguments, settings migration, audio feedback, media routing, audio-device loss/recovery, and reconnects. A TCP relay drops the network without an MQTT disconnect to check the offline Last Will. Power transitions, media keys, and volume changes use fake backends, so tests cannot lock/suspend/shut down/restart your PC or change playback.

Windows tests read the audio endpoint, check native input and shutdown-process specifications without executing power actions, create/remove their own test credential, and run harmless test programs/batch files. Startup shortcut tests use an isolated test directory, not the real Startup folder. UI checks render invisible windows and save previews under `artifacts/ui/`. These tests need an interactive Windows user session for Credential Manager. Diagnostic tests cover broker authentication failures, reconnect logging, redaction of command content, log rotation, and unavailable log storage.

To create the MSI and portable ZIP, restore the WiX SDK from NuGet and build with:

```powershell
.\scripts\Build-Release.ps1
.\scripts\Test-Release.ps1
```

WiX Toolset 5.0.2 is pinned in `installer/mqtt2PC.Installer.wixproj`; no global WiX installation is required. The installer is built separately from the app/test solution. `Build-Release.ps1 -NoRestore` reuses restored packages. Release builds run Windows Installer ICE validation, which requires access to the Windows Installer service. `Test-Release.ps1` opens the MSI database read-only and checks scope, payload, shortcuts, uninstall/upgrade rules, portable contents, and SHA256 hashes without installing or changing startup settings.

Two ICE checks are explicitly suppressed: [ICE91](https://learn.microsoft.com/en-us/windows/win32/msi/ice91), because the package installs strictly per user under LocalAppData, and [ICE61](https://learn.microsoft.com/en-us/windows/win32/msi/ice61), because same-version development builds intentionally replace the existing installation. The remaining ICE checks stay enabled.

Outputs are under `artifacts/stage4/`. Automated tests and package validation do not perform a live install/uninstall against the current Windows profile. Keep the portable folder together. No broker address, password, or user configuration is included in the build; the bundled MQTTnet license is in `THIRD-PARTY-NOTICES.txt`.
