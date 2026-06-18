# OneDriveSync

A small .NET 8 console application that performs a one-way sync of a local folder to
Microsoft OneDrive using the Microsoft Graph API. It authenticates with the OAuth2
**device code flow**, so it works on a headless terminal (no local browser required),
and caches the refresh token so you only sign in once.

## Features

- Uploads new and modified files (recursively, including subfolders).
- Skips unchanged files (compares size, then SHA-1 content hash, falling back to
  last-modified timestamp).
- Optional pruning of remote files that no longer exist locally (`--delete`).
- Dry-run mode to preview actions (`--dry-run`).
- Persistent token cache — re-authentication is not needed on every run.
- Nice terminal output: progress bar and a summary table (Spectre.Console).

## Prerequisites

- .NET 8 SDK
- A Microsoft account (personal or work/school) with OneDrive.
- Your own Azure AD app registration (see below).

## 1. Register an Azure AD application

You must supply your own app registration's **Client ID**. This is free and takes a
couple of minutes.

1. Go to the [Azure Portal](https://portal.azure.com) and open
   **Azure Active Directory** -> **App registrations** -> **New registration**.
   (Personal-account-only users can use <https://entra.microsoft.com>.)
2. **Name**: anything, e.g. `OneDriveSync CLI`.
3. **Supported account types**: choose one that fits your account:
   - *Accounts in any organizational directory and personal Microsoft accounts*
     (maps to tenant `common`), or
   - *Personal Microsoft accounts only* (maps to tenant `consumers`).
4. **Redirect URI**: leave blank (the device code flow does not need one).
5. Click **Register**.
6. On the app's **Overview** page, copy the **Application (client) ID** — this is your
   `ClientId`.
7. Open **Authentication** -> under **Advanced settings** set
   **Allow public client flows** to **Yes**, then **Save**. (Required for the device
   code flow.)
8. Open **API permissions** -> **Add a permission** -> **Microsoft Graph** ->
   **Delegated permissions**, and add:
   - `Files.ReadWrite`
   - `User.Read`
   - `offline_access`

   Click **Add permissions**. (Admin consent is generally not required for these
   delegated, user-scoped permissions.)

## 2. Configure

Edit `appsettings.json` and fill in your `ClientId` (and optionally default paths):

```json
{
  "AzureAd": {
    "ClientId": "YOUR-CLIENT-ID-GUID",
    "TenantId": "common"
  },
  "Sync": {
    "LocalPath": "/home/you/Documents/ToBackup",
    "OneDrivePath": "/Documents/Backup"
  }
}
```

- `TenantId`: use `common` (personal + work/school), `consumers` (personal only),
  or a specific tenant GUID/domain.
- Any value can be overridden on the command line.

You may instead create an `appsettings.Local.json` (git-ignored) for your secrets.

## 3. Build

```bash
dotnet build -c Release
```

## 4. Run

```bash
# Using appsettings.json defaults:
dotnet run -- --delete

# Or specify everything on the CLI:
dotnet run -- --local /path/to/folder --remote /Documents/Backup --delete

# Preview without making changes:
dotnet run -- --local /path/to/folder --remote /Documents/Backup --delete --dry-run
```

After publishing, the produced executable is named `onedrivesync`:

```bash
dotnet publish -c Release -o ./out
./out/onedrivesync --local /path/to/folder --remote /Documents/Backup
```

### CLI options

```
onedrivesync --local /path/to/folder --remote /RemoteFolder [--delete] [--dry-run]

  --local <path>     Local source folder (overrides Sync:LocalPath).
  --remote <path>    OneDrive destination, e.g. /Documents/Backup.
  --clientid <guid>  Azure AD app client id (overrides AzureAd:ClientId).
  --tenant <id>      Azure AD tenant (default: common).
  --delete           Delete remote files missing locally.
  --dry-run          Report actions without making changes.
  -h, --help         Show this help.
```

### First run / authentication

On the first run (or when the cached token expires beyond refresh), the tool prints a
device-code message like:

```
To sign in, use a web browser to open the page https://microsoft.com/devicelogin and
enter the code XXXXXXXXX to authenticate.
```

Open that URL on any device, enter the code, and sign in. The authentication record is
cached under your OS local application data directory (`OneDriveSync/auth-record.json`)
plus an OS-managed encrypted token cache, so subsequent runs are silent.

## How change detection works

For each local file, the tool fetches the matching remote item:

1. If sizes differ -> upload.
2. Else if the remote item exposes a SHA-1 hash, compare it against the local file's
   SHA-1 -> upload only if they differ.
3. Else compare last-modified timestamps (1s tolerance) -> upload if the local file is
   newer.

Files larger than 4 MiB use a resumable upload session; smaller files use a single PUT.

## Notes & limitations

- This is a **one-way** sync (local -> OneDrive). It never downloads or modifies local
  files.
- `--delete` removes remote *files* that have no local counterpart; it does not remove
  empty remote folders.
- Deleted items go to the OneDrive recycle bin, per Graph behavior.
- Run with `--dry-run` first when using `--delete` to review what would be removed.

## Project layout

```
OneDriveSync.csproj      Project + NuGet dependencies
Program.cs               Entry point, config + CLI parsing, error handling
appsettings.json         Configuration template
Models/SyncOptions.cs    Resolved run configuration + validation
Services/AuthService.cs  Device-code auth + persistent token cache
Services/SyncService.cs  Core sync engine (upload/skip/delete)
```

## Security

- Never commit `appsettings.Local.json` or any token cache (already in `.gitignore`).
- The Client ID is not a secret, but treat your token cache as sensitive — it grants
  access to your OneDrive.
