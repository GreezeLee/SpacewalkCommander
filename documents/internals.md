# UrevoSpacewalkCommander – Internals

This document describes how the application works internally. It is the reference for anyone (human or AI)
changing the code. **Keep it in sync with the code** – every change to behaviour, protocols, file formats,
settings or structure must be reflected here in the same change, and noted in the [change history](#change-history).

## 1. Purpose

**UrevoSpacewalkCommander** is a Windows WPF desktop application that controls a **Urevo Spacewalk 3S**
treadmill over Bluetooth LE (start, pause, stop, target speed, target incline), shows its live values, records
every workout and uploads it to **Google Health** (the successor of Google Fit).

The name `UrevoSpacewalkCommander` is used for the project file, assembly, root namespace, test project and the
AppData folders (`%APPDATA%\UrevoSpacewalkCommander`, `%LOCALAPPDATA%\UrevoSpacewalkCommander`). The window title
is “Urevo Spacewalk Commander”.

## 2. Build, run, test

| Task | Command (from the repository root) |
|---|---|
| Build | `dotnet build UrevoSpacewalkCommander.csproj` |
| Run | `dotnet run --project UrevoSpacewalkCommander.csproj` |
| Test | `dotnet test UrevoSpacewalkCommander.Tests` |

- Target framework: `net10.0-windows10.0.19041.0` (needs the Windows SDK projection for the WinRT Bluetooth APIs).
- No NuGet packages in the application; the test project uses xUnit.
- The test project lives in `UrevoSpacewalkCommander.Tests/` *below* the application folder. The application
  project excludes it via `DefaultItemExcludes`; do not remove that line, or the tests get compiled into the app.
- Internal types are visible to the tests through `InternalsVisibleTo`.

## 3. Project layout

```
UrevoSpacewalkCommander.csproj  WPF application
App.xaml(.cs)                   Application entry (StartupUri = MainWindow)
MainWindow.xaml(.cs)            The only window; view concerns only (culture, log scrolling, close confirmation)
Bluetooth/                      BLE scanning, FTMS protocol and GATT client
Workouts/                       Recording, analysis and local archive of workouts
GoogleHealth/                   OAuth sign-in, Google Health API client, payload mapping, upload service
Configuration/                  AppSettings (settings.json, OAuth client file), TreadmillTargetsStore (last-targets.json)
Infrastructure/                 MVVM helpers: ObservableObject, RelayCommand, Debouncer
ViewModels/                     MainViewModel (treadmill), WorkoutSyncViewModel (Google Health area)
UrevoSpacewalkCommander.Tests/  xUnit tests (analysis, payload, FTMS codec, archive, targets store)
documents/internals.md          This document
README.md                       User guide: features, setup, Google Health sign-in, troubleshooting
CLAUDE.md                       Instructions for AI assistants (read and maintain this document)
.gitignore                      Ignores build output (bin/, obj/), IDE files, test results and OAuth secrets (client_secret*.json)
```

## 4. Architecture overview

```
MainWindow ──binds──> MainViewModel ──────────────> TreadmillScanner (BLE advertisements)
                         │   │                      FtmsTreadmillClient (GATT, FTMS)
                         │   └── WorkoutRecorder ──> WorkoutSession ──> WorkoutAnalyzer ──> WorkoutSummary
                         └──> WorkoutSyncViewModel ──> WorkoutSyncService ──> WorkoutStore (JSON archive)
                                                                         ├─> GoogleAuthorization (OAuth, TokenStore)
                                                                         └─> GoogleHealthClient (REST)
```

- **MVVM without frameworks.** `ObservableObject.SetProperty` raises `PropertyChanged` and calls
  `CommandManager.InvalidateRequerySuggested()`, so command availability (`CanExecute`) follows every state change.
- **Threading.** Bluetooth events and HTTP continuations arrive on background threads. `MainViewModel.PostToUi`
  marshals them to the dispatcher. View models are only touched on the UI thread. Lower layers use
  `ConfigureAwait(false)`; view models do not.
- **Error handling.** Commands never throw: `MainViewModel.RunGuardedAsync` and `WorkoutSyncViewModel.RunAsync`
  turn exceptions into a log line and a status text.
- **Timeouts.** Every external call has an explicit bound (see the tables below). A timeout surfaces as
  `TimeoutException`; only the caller's own cancellation surfaces as `OperationCanceledException`.

## 5. Bluetooth / FTMS

### 5.1 Assumption

The treadmill is assumed to implement the Bluetooth SIG **Fitness Machine Service (FTMS, 0x1826)**. This has
not been verified on real hardware. If the device does not offer FTMS, `FtmsTreadmillClient.DiscoverFtmsAsync`
throws `NotSupportedException` and the log lists all GATT services found – that list is the starting point for
supporting a proprietary protocol.

### 5.2 UUIDs and op codes (`FtmsProtocol`)

| Item | Value |
|---|---|
| Fitness Machine Service | `0x1826` |
| Treadmill Data (notify) | `0x2ACD` |
| Control Point (write + indicate) | `0x2AD9` |
| Fitness Machine Status (notify) | `0x2ADA` |
| Supported Speed Range (read) | `0x2AD4` – min/max/step, uint16, 0.01 km/h |
| Supported Inclination Range (read) | `0x2AD5` – min/max sint16, step uint16, 0.1 % |

Control point commands (first byte = op code):

| Op code | Meaning | Parameter |
|---|---|---|
| `0x00` | Request control | – (required before any other command; re-sent before every start) |
| `0x02` | Set target speed | uint16 LE, 0.01 km/h |
| `0x03` | Set target inclination | sint16 LE, 0.1 % |
| `0x07` | Start / resume | – |
| `0x08` | Stop / pause | `0x01` stop, `0x02` pause |

The control point answers with an indication `0x80, <request op code>, <result>`; result `0x01` is success,
`2` op code not supported, `3` invalid parameter, `4` operation failed, `5` control not permitted.

### 5.3 Command handling (`FtmsTreadmillClient.SendControlAsync`)

- Commands are serialized by a `SemaphoreSlim` (FTMS allows one outstanding procedure).
- The pending command is registered **before** the write, so an indication arriving before the write completes
  is not lost.
- Write with response; then wait up to **3 s** for the indication. No indication → logged as a hint and treated
  as success (some devices do not indicate). An explicit non-success result → `InvalidOperationException`.

### 5.4 Telemetry decoding

`FtmsProtocol.ParseTreadmillData` follows the FTMS field order and reads fields up to *elapsed time*:
speed (present when flag bit 0 is **clear**), average speed (skipped), total distance (uint24, m),
inclination (sint16, 0.1 %) + ramp angle (skipped), elevation gain (skipped), paces (skipped),
expended energy (total kcal; `0xFFFF` = unavailable), heart rate (`0` = unavailable), MET (skipped),
elapsed time (uint16, s). FTMS devices may split one record over several notifications (“more data” flag);
consumers must therefore treat every field as optional.

### 5.5 Timeouts

| Operation | Bound |
|---|---|
| Opening the device (`FromBluetoothAddressAsync`) | 20 s |
| Any GATT discovery / read / write | 10 s |
| Control point indication | 3 s (then “no confirmation”) |

### 5.6 Scanning and auto-connect

- `TreadmillScanner` uses an active `BluetoothLEAdvertisementWatcher` (names often only arrive in scan responses).
- Before scanning, `TreadmillScanner.CheckRadioAsync` verifies radio access and that Bluetooth is on.
- A scan runs **20 s**. All devices are listed (also unnamed ones); an entry is replaced when a later
  advertisement reveals its name or the FTMS service. At the end, the list is sorted by the best RSSI seen.
- Preselection: devices that advertise FTMS or whose name contains `urtm` / `urevo` / `spacewalk`
  (case-insensitive, `MainViewModel.s_preferredNameParts`). The Spacewalk 3S advertises itself as **`URTM`**.
- **Auto-connect at start-up:** `MainWindow.OnLoaded` → `MainViewModel.AutoConnectAsync` scans and connects to the
  first device whose **name** contains one of the preferred parts. Only once per application start; no automatic
  reconnect after a lost connection.

### 5.7 Control behaviour (`MainViewModel`)

- **Start:** request control + start; then send the incline, wait 1 s, send the speed; if the speed is rejected
  (start countdown) retry once after 3 s.
- **Speed / incline sliders:** values are snapped to the step reported by the treadmill. Changes are debounced
  (400 ms, `Debouncer`) so dragging sends one command. Speed is only sent while the belt runs (otherwise applied
  at the next start); incline is sent whenever connected.
- **Ranges:** from the treadmill; defaults if not reported: speed 1.0–6.0 km/h (step 0.1), incline 0–15 % (step 1).
- **Remembered targets:** the selected speed and incline are saved to `last-targets.json` (see 8.1) 1 s after the
  last change (own `Debouncer`) and again when the app closes, and preselected at the next start (initial values
  without a file: 2.0 km/h, 0 %). Until the first connection, the restored values are kept as saved
  (`_restoredTargets`) and only limited to the treadmill's ranges in `ApplyRanges`, so the default ranges cannot cut
  them off; saves before the first connection write them unchanged. Restoring does not send anything to the
  treadmill: the incline is sent at the next change or start, the speed at the start.
- **Safety:** Stop is always enabled while connected. *Disconnect* stops a running belt first. Closing the window
  while the belt runs asks for confirmation and stops it.
- **Status notifications** (console buttons, safety key) update the running state the same way as own commands.

## 6. Workouts

### 6.1 Recording (`WorkoutRecorder`)

- A workout begins with the first start (own command or `Started` status) and ends with stop, `Reset`,
  safety key, lost connection, *Disconnect* or closing the app.
- Events: `Start`, `Pause`, `Resume`, `Stop`. All transitions are idempotent because each is usually reported
  twice (command confirmation and status notification).
- Samples (`WorkoutSample`) are recorded only while the belt runs, one per telemetry notification, with the
  receive time.

### 6.2 Analysis (`WorkoutAnalyzer`)

| Figure | Rule |
|---|---|
| Active duration | Sum of start/resume → pause/stop intervals (fallback: whole session) |
| Distance | Treadmill distance counter if any sample has one (a decreasing counter = reset, new value counts); otherwise speed integrated over time. Gaps > 5 s are not integrated |
| Energy | Treadmill energy counter if reported; otherwise ACSM walking equation if `bodyWeightKg` is set: VO2 = 0.1·v + 1.8·v·grade (ml/kg/min, v in m/min), 5 kcal per litre O2, resting share excluded; otherwise none |
| Average speed | distance / active duration |
| Incline | distance-weighted mean and maximum |
| Elevation gain | Σ distance · max(incline, 0) / 100 (virtual climb) |
| Splits | One per full kilometre (boundary time interpolated) + the remainder if ≥ 10 m |
| Heart rate | Mean of reported values (Spacewalk normally reports none) |

Workouts with **less than 60 s active time or less than 20 m** are discarded as accidental starts.

### 6.3 Local archive (`WorkoutStore`)

- Folder: `%LOCALAPPDATA%\UrevoSpacewalkCommander\workouts\` with `pending\` and `uploaded\`.
- File name: `yyyyMMdd-HHmmss-<first 8 hex of id>.json`; content: the complete `WorkoutSession`
  (camelCase JSON, enums as names, all samples). Upload payloads are always recomputed from this file.
- A finished workout is written to `pending\` **synchronously before** any upload starts, so it survives
  failures and closing the app. After a successful upload the file is moved to `uploaded\`.

## 7. Google Health integration

### 7.1 Why Google Health and not Google Fit

The Google Fit REST API accepts no new projects since May 2024 and is shut down in 2026. Its successor for
cloud integrations is the **Google Health API** (v4, launched March 2026, also replacing the Fitbit Web API).
Data written there appears in the Fitbit / Google Health app.

### 7.2 What is uploaded

One **exercise** data point per workout: `POST https://health.googleapis.com/v4/users/me/dataTypes/exercise/dataPoints`
(`ExercisePayloadBuilder`, DTOs in `GoogleHealthDtos.cs`):

- `dataSource`: `recordingMethod = ACTIVELY_MEASURED`, `device.manufacturer = Urevo`, `device.displayName` = BLE name.
- `exercise.exerciseType = TREADMILL_WALK`; `displayName` is required but replaced by Google for this type.
- `interval`: start/end as UTC RFC 3339 (`yyyy-MM-ddTHH:mm:ss.fffZ`) plus the local UTC offsets as durations
  (`"3600s"`).
- `activeDuration` (`"<seconds>s"`), `metricsSummary` (distance mm, kcal, average speed mm/s, pace s/m,
  elevation gain mm, average heart rate as string), `exerciseEvents` (START/PAUSE/RESUME/STOP),
  `splitSummaries` (`splitType = DISTANCE`, distance and kcal per split), `notes` (English text with average/max
  speed, average/max incline, climb, energy and whether it was estimated).
- Null values are omitted from the JSON.

**Not uploaded:** per-minute time series (distance, steps, heart rate, active energy). The Google Health data
type overview lists only `list, reconcile, rollup, dailyRollup` for these types (no `create`), while the workouts
guide suggests writing them – the documentation is contradictory, so only the documented exercise write is used.
The full time series stays in the local archive. Consequence: the workout does not feed Google Health's daily
step/distance totals.

### 7.3 Sign-in (`GoogleAuthorization`)

- OAuth 2.0 for installed apps: system browser (never an embedded web view) + **loopback redirect**
  `http://127.0.0.1:<random port>/` served by a `TcpListener` (avoids `HttpListener` URL ACLs) + **PKCE (S256)**
  + `state`. `access_type=offline`, `prompt=consent` to always receive a refresh token.
- Scope: `https://www.googleapis.com/auth/googlehealth.activity_and_fitness.writeonly` only.
- Endpoints: `https://accounts.google.com/o/oauth2/v2/auth`, `https://oauth2.googleapis.com/token`.
- The refresh token is stored DPAPI-encrypted (current Windows user) in
  `%LOCALAPPDATA%\UrevoSpacewalkCommander\google-refresh-token.bin` (`TokenStore`). Access tokens live in memory only and are
  renewed 1 minute before expiry. `invalid_grant` deletes the stored token → “sign-in required”.
- After sign-in, `GET /v4/users/me/identity` verifies the account; `ACCOUNT_NOT_LINKED` means the user has to sign
  in to the Google Health app on the phone once.
- Browser sign-in times out after 5 minutes. HTTP requests to Google time out after 30 s.
- **Testing mode:** while the Cloud project's publishing status is *Testing*, Google expires refresh tokens after
  7 days → the user has to press “Sign in with Google” about once a week. All Google Health scopes are
  *restricted*; up to 100 test users work without the security review.

### 7.4 Upload flow (`WorkoutSyncService`)

- Triggers: end of a workout (`SubmitAsync`), sign-in, button “Upload pending”, application start
  (`MainWindow.OnLoaded`). Closing the app waits up to 30 s for the running upload.
- Uploads and sign-ins are serialized by a semaphore. Uploads **never** open the browser; only the sign-in button does.
- Pending files are uploaded oldest first. A `401` renews the access token once and retries (safe: nothing was
  created). `ACCOUNT_NOT_LINKED` aborts the whole run; other failures leave that file pending and continue.
- An operation with `done: false` is treated as accepted (the polling endpoint is not documented).
- Known gap: if the app is killed while a request is in flight, the workout may exist in Google Health but still
  be pending locally → a duplicate on the next upload. No client-side id is sent (the create ID parameter is not
  documented).

### 7.5 Setup for a user

1. Google Cloud console: create a project, enable **Google Health API**.
2. OAuth consent screen: user type *External*, publishing status *Testing*, add the own Google account as test
   user, add the scope `googlehealth.activity_and_fitness.writeonly` under Data Access.
3. Credentials: create an OAuth client of type **Desktop app**; download the JSON.
4. Put the downloaded `client_secret_*.json` into `%APPDATA%\UrevoSpacewalkCommander\` (button “Open settings”), or
   enter `googleClientId` / `googleClientSecret` in `settings.json`.
5. Sign in once to the Google Health / Fitbit app on the phone with the same account.
6. In the app: “Sign in with Google”.

## 8. Settings (`AppSettings`)

`%APPDATA%\UrevoSpacewalkCommander\settings.json` (created as a template on first start):

| Key | Type | Meaning |
|---|---|---|
| `bodyWeightKg` | number / null | Enables the energy estimate when the treadmill reports no energy |
| `googleClientId` | string | OAuth client id (optional if a `client_secret_*.json` file is present) |
| `googleClientSecret` | string | OAuth client secret |

If `googleClientId`/`googleClientSecret` are empty, the first `client_secret*.json` in the same folder is used
(`installed` or `web` section). Secrets never belong in the repository.

### 8.1 Remembered targets (`TreadmillTargetsStore`)

`%APPDATA%\UrevoSpacewalkCommander\last-targets.json`, written by the app only (kept apart from `settings.json` so
saving never rewrites the user's file):

```json
{
  "speedKmh": 3.5,
  "inclinePercent": 4
}
```

- Both keys are required. Written to `last-targets.json.tmp` and then moved over the file, so an interrupted save
  keeps the previous values.
- A missing file means “nothing to restore”. An unreadable or invalid file is logged and ignored; the next save
  overwrites it. Save failures are only logged.

## 9. Conventions

- Ammann C#/.NET coding guidelines apply (2-space indentation, Allman braces, `_camelCase` fields, file-scoped
  namespaces, XML documentation on every member including private ones and tests, methods ≤ 60 lines).
- Source files are pure ASCII where possible. Should a C# string literal ever need a non-ASCII character, it is
  written as a Unicode escape (backslash, `u`, four hex digits); in XAML as a character reference (`&#x00FC;`).
- Deliberate deviations (private project): no Ammann copyright header; UI texts are hard-coded, not localized
  through the LTE.
- Everything is English: code, comments, this document, the README, UI texts, log lines, the browser page after
  sign-in and the notes uploaded to Google Health.

## 10. Known limitations

- FTMS support of the Spacewalk 3S is assumed, not verified on hardware.
- No automatic reconnect; a lost connection ends the current workout (a short dropout splits it into two).
- The Google Fit API is not used (shut down); data goes to Google Health.
- Time-series data is not uploaded (see 7.2); a killed upload can cause a duplicate (see 7.4).

## Change history

| Date | Change |
|---|---|
| 2026-11-06 | Initial WPF app: BLE scan, FTMS control (start/pause/stop, speed, incline), live values, log. |
| 2026-11-06 | Scan diagnostics: radio check, unnamed devices listed, RSSI sorting, scan summary. |
| 2026-11-06 | Auto-connect at start-up to devices named `urevo` / `spacewalk`. |
| 2026-11-06 | Workout recording, analysis, local archive; upload to Google Health (exercise sessions); unit tests. |
| 2026-11-06 | Added this document and `CLAUDE.md`. |
| 2026-11-06 | Renamed the project to `UrevoSpacewalkCommander` (project files, assembly, namespaces, tests, AppData folders, window title). |
| 2026-11-06 | Treadmill name `URTM` added to the auto-select / auto-connect name parts. |
| 2026-11-06 | Added `README.md` (user guide incl. Google Health sign-in). |
| 2026-11-06 | Translated the whole app to English (UI, status and log texts, dialogs, sign-in page, Google Health notes). |
| 2026-11-06 | Added `.gitignore` (build output, IDE files, test results, OAuth client secrets). |
| 2026-11-06 | Target speed and incline are saved to `last-targets.json` and restored at the next start. |
