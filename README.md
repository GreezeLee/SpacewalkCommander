# Urevo Spacewalk Commander

A Windows desktop app to control a **Urevo Spacewalk 3S** treadmill from your PC via Bluetooth and to log every
walk to **Google Health** (the Fitbit / Google Health app – the successor of Google Fit).

## What the app does

- **Finds and connects the treadmill automatically.** On start-up the app searches for 20 seconds and connects to
  the first device whose name contains `URTM`, `Urevo` or `Spacewalk`. You can also pick a device manually
  (“Scan” → select → “Connect”).
- **Controls the belt:** “Start”, “Pause”, “STOP”, target speed and incline (slider or −/+ buttons).
  The selected speed is applied on start; while the belt runs, changes are sent as soon as the slider rests.
  The last selected speed and incline are remembered and preselected at the next start of the app.
- **Shows live values** from the treadmill: speed, incline, distance, time, energy, heart rate (as far as the
  treadmill reports them).
- **Records every walk** and, when it ends (stop, safety key, disconnect or closing the app), saves it on your PC
  with all values. Walks shorter than 1 minute or 20 m are ignored as accidental starts.
- **Uploads the walk to Google Health** as a “treadmill walk” workout: duration without pauses, distance, average
  speed and pace, virtual climb (from the incline), energy, pauses, per-kilometre splits and a note with maximum
  speed and incline.
- **Safety:** “STOP” is always available while connected. “Disconnect” stops a running belt first, and closing the
  app while the belt runs asks before stopping it.

Walks are never lost: if Google is not set up, not signed in or offline, they stay in the local archive and are
uploaded later (at the next start, after signing in, or with “Upload pending”).

## Requirements

- Windows 10 (version 2004) or Windows 11 with Bluetooth LE.
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build and run the app.
- For the upload: a Google account and a (free) Google Cloud project – see below.

## Build and run

```powershell
dotnet run --project UrevoSpacewalkCommander.csproj
```

Before starting, **close the Urevo app on your phone** (or switch off the phone's Bluetooth): the treadmill
accepts only one connection and is invisible while the phone is connected.

## Signing in to Google Health

Google does not offer a shared login for apps like this one. You create your own small “app registration” in
Google Cloud once (about 10 minutes); it stays private to your account.

### 1. Create a Google Cloud project and enable the API

1. Open the [Google Cloud console](https://console.cloud.google.com/) and sign in with the Google account you use
   for Google Health / Fitbit.
2. Create a new project (project selector at the top → **New project**), e.g. “Spacewalk Commander”.
3. Open the [Google Health API page](https://console.developers.google.com/apis/library/health.googleapis.com)
   with this project selected and click **Enable**.

### 2. Configure the consent screen

In the console, open **Google Auth Platform** (APIs & Services → OAuth consent screen):

1. **Branding:** enter an app name (e.g. “Spacewalk Commander”) and your e-mail address; save.
2. **Audience:** user type **External**, publishing status **Testing**. Under **Test users**, add your own Google
   account.
3. **Data Access:** **Add or remove scopes**, search for “Google Health API”, select
   `https://www.googleapis.com/auth/googlehealth.activity_and_fitness.writeonly` and save.

### 3. Create the OAuth client

1. **Clients** → **Create client**, application type **Desktop app**, any name → **Create**.
2. **Download JSON**. You get a file named `client_secret_….json`.

Keep this file private – do not share it or put it into source control.

### 4. Give the client to the app

1. In the app, click **“Open settings”**. Explorer opens `%APPDATA%\UrevoSpacewalkCommander\`.
2. Copy the downloaded `client_secret_….json` into this folder.
   (Alternatively, enter `googleClientId` and `googleClientSecret` in `settings.json` in the same folder.)
3. **Restart the app** – the settings are read at start-up.

### 5. Link your account to Google Health

Install the **Google Health** (Fitbit) app on your phone and sign in once with the same Google account.
Without this, Google rejects uploads with “account not linked”.

### 6. Sign in from the app

1. Click **“Sign in with Google”**. Your browser opens Google's sign-in page.
2. Choose your account. Google warns **“Google hasn't verified this app”** – this is expected for your own app in
   testing mode: click **Continue**.
3. Allow the app to **add activity and fitness data**.
4. The browser shows “Sign-in successful.”; close the tab.

The “Google Health” area of the app now shows the result, e.g. “Everything is synchronized.”, and pending walks are
uploaded. From now on every finished walk is uploaded automatically.

### Re-signing in

While your Cloud project is in **Testing** status, Google ends the sign-in after **7 days**. The app then shows
“Sign-in required”; walks are kept locally. Click **“Sign in with Google”** again and they are uploaded.

## Settings

`%APPDATA%\UrevoSpacewalkCommander\settings.json` (created on first start):

| Setting | Meaning |
|---|---|
| `bodyWeightKg` | Your weight in kg. If the treadmill does not report energy, the app estimates it from speed, incline and weight. `null` = no estimate. |
| `googleClientId`, `googleClientSecret` | OAuth client, only needed if no `client_secret_….json` file is in the folder. |

Restart the app after changing settings.

## Where the data is stored

| What | Location |
|---|---|
| Walks not yet uploaded | `%LOCALAPPDATA%\UrevoSpacewalkCommander\workouts\pending\` |
| Uploaded walks (archive) | `%LOCALAPPDATA%\UrevoSpacewalkCommander\workouts\uploaded\` |
| Google sign-in (encrypted for your Windows user) | `%LOCALAPPDATA%\UrevoSpacewalkCommander\google-refresh-token.bin` |
| Last selected speed and incline | `%APPDATA%\UrevoSpacewalkCommander\last-targets.json` |

To sign out, close the app and delete `google-refresh-token.bin`.

## Troubleshooting

| Problem | Solution |
|---|---|
| Treadmill not found | Switch the treadmill on, close the Urevo app / switch off the phone's Bluetooth, move the PC closer. After a search, the log says how many Bluetooth signals were received – 0 means Bluetooth on the PC is off or blocked. |
| “Not set up” in the Google Health area | No OAuth client found: put the `client_secret_….json` into the settings folder and restart the app. |
| “The Google account is not linked to Google Health” | Sign in to the Google Health / Fitbit app on your phone once with the same account (step 5). |
| Sign-in shows “access blocked” / “not a test user” | Add your account under **Audience → Test users** (step 2). |
| “Sign-in required” | The 7-day sign-in expired – sign in again. |
| Walk does not appear in the daily step / distance totals | Expected: only the workout itself is uploaded, not minute-by-minute data. |

## Limitations

- The app assumes the treadmill speaks the standard Bluetooth fitness protocol (FTMS). It has not yet been
  tested on every firmware version.
- There is no automatic reconnect; a lost connection ends the current walk.

## For developers

How the app works internally is described in [documents/internals.md](documents/internals.md).
Tests: `dotnet test UrevoSpacewalkCommander.Tests`.
