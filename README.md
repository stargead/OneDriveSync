# OneDriveSynch2

Version 2.0.0 — bidirectional, real-time synchronization between a local folder and a OneDrive folder using the Microsoft Graph API.

## How it works

Two watchers run concurrently. A shared lock ensures they never process changes at the same time, preventing race conditions.

### Local Watcher

Uses `FileSystemWatcher` to detect changes inside the local folder (including all subdirectories) in real time.

| Event | Behavior |
|---|---|
| **Created / Changed** | Fetch the OneDrive item's `lastModifiedDateTime`. Upload if local is newer. Log a `CONFLICT` warning if OneDrive is newer. Skip if timestamps are equal (within 2 s). |
| **Renamed / Moved** | PATCH the OneDrive item to its new name/location. If the destination is outside the watched folder, delete the OneDrive item instead. |
| **Deleted** | Delete the corresponding OneDrive item immediately. No timestamp check. |
| **Locked file** | Retry up to 5 times with a 2-second interval. Skip with a warning if the file is still locked after all retries. |

Changed/Created events are debounced by 500 ms so a file being written incrementally is only uploaded once.

### OneDrive Poller

Periodically enumerates the remote OneDrive folder and reconciles it with the local folder. Change detection is driven by a persisted snapshot (stored in `~/.local/share/OneDriveSync/snapshot-{hash}.json`) that tracks each item by its OneDrive item ID.

| Change detected | Behavior |
|---|---|
| **Deleted** | Item ID no longer present in the remote scan → delete the local file. |
| **Moved** | Same item ID, different path → move the local file. If the destination already exists, keep the newer copy and log a conflict. |
| **Updated** | Same ID and path, `lastModifiedDateTime` changed → download if OneDrive copy is newer than the local file. |
| **Created** | Item ID not seen in the previous snapshot → download if OneDrive copy is newer than the local file (or if the local file does not exist). |

---

## Prerequisites

1. **.NET 10 SDK** — [download](https://dotnet.microsoft.com/download)
2. **Azure AD app registration** with the following delegated permissions:
   - `Files.ReadWrite`
   - `User.Read`
   - `offline_access`

   Register the app at [portal.azure.com](https://portal.azure.com) → Azure Active Directory → App registrations. Add a **Mobile and desktop application** redirect URI of `https://login.microsoftonline.com/common/oauth2/nativeclient`. Copy the **Application (client) ID**.

---

## Configuration

Configuration is merged from three sources in order (later sources override earlier ones):

1. `appsettings.json` (shipped alongside the binary)
2. `appsettings.Local.json` (optional, git-ignored, for local overrides)
3. Environment variables prefixed with `ONEDRIVESYNCH2_`
4. Command-line arguments (highest priority)

### appsettings.json

```json
{
  "AzureAd": {
    "ClientId": "<your-app-client-id>",
    "TenantId": "consumers"
  },
  "Sync": {
    "LocalPath": "/path/to/local/folder",
    "OneDrivePath": "/RemoteFolder",
    "OneDrivePollIntervalSeconds": 60
  }
}
```

| Key | Description |
|---|---|
| `AzureAd:ClientId` | Azure AD application (client) ID. |
| `AzureAd:TenantId` | `consumers` for personal accounts, `common` for personal + work, or a specific tenant GUID/domain. |
| `Sync:LocalPath` | Absolute path to the local folder to watch. |
| `Sync:OneDrivePath` | Destination folder in OneDrive, relative to the drive root (e.g. `/Documents/Backup`). |
| `Sync:OneDrivePollIntervalSeconds` | How often (in seconds) to poll OneDrive for remote changes. Default: `60`. |

### Command-line options

```
Usage: onedrivesynch2 [options]

Options:
  --local <path>       Local folder to watch.
  --remote <path>      OneDrive folder (drive-root relative, e.g. /Documents/Backup).
  --clientid <guid>    Azure AD application (client) ID.
  --tenant <id>        Azure AD tenant (default: consumers).
  --interval <sec>     OneDrive poll interval in seconds (default: 60).
  -h, --help           Show this help.
```

CLI arguments override `appsettings.json`. Example:

```bash
onedrivesynch2 --local ~/Documents --remote /Backup --interval 30
```

---

## Authentication

On first run the tool prints a device code and a URL. Open the URL in any browser, enter the code, and sign in with your Microsoft account. The authentication record and token are cached in:

```
~/.local/share/OneDriveSync/auth-record.json   (shared with OneDriveSync v1)
```

Subsequent runs reuse the cached token silently.

---

## Running

```bash
cd OneDriveSynch2
dotnet run
```

Or build and run the binary directly:

```bash
dotnet publish -c Release -o out
./out/onedrivesynch2
```

Press **Ctrl+C** to stop both watchers gracefully.

---

## Console output

| Color | Meaning |
|---|---|
| Green | File uploaded / watcher started |
| Cyan | File downloaded / moved locally |
| Red | File deleted (local or remote) / error |
| Yellow | Conflict detected / locked file skipped |
| Grey | Status / informational |

---

## Conflict handling

A **conflict** occurs when a local file change is detected but the OneDrive copy has a newer `lastModifiedDateTime`. In this case the tool logs a warning and does **not** overwrite the remote copy:

```
CONFLICT subdir/report.docx — OneDrive copy is newer; not uploading.
```

Resolve the conflict manually (e.g. rename one copy, then save again to trigger a fresh upload).

---

## Persisted state

| File | Purpose |
|---|---|
| `~/.local/share/OneDriveSync/auth-record.json` | Cached OAuth2 authentication record (shared with v1). |
| `~/.local/share/OneDriveSync/snapshot-{hash}.json` | OneDrive folder snapshot used by the poller to detect changes between polls. One file per local+remote path pair. |

---

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Clean exit. |
| `1` | Configuration error (missing or invalid options). |
| `2` | Authentication failed. |
| `3` | Permission denied on local filesystem. |
| `4` | Local folder not found. |
| `5` | Unexpected error. |
| `130` | Cancelled by Ctrl+C (SIGINT). |
