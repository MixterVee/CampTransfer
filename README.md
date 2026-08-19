# CampTransfer

CampTransfer is a simple Windows file-transfer queue for slow or bandwidth-limited links such as a router-to-router WireGuard tunnel.

It does **not** configure or modify WireGuard. If Windows can already reach a remote Windows/Samba share through your ASUS routers, CampTransfer can copy to it.

## Features

- Queue files or complete folders.
- Each queued item can have a different destination.
- One transfer at a time for predictable bandwidth use.
- Live global upload limit. Change it while a transfer is running.
- Accepts speed limits such as `2 Mbps`, `500 KB/s`, `1 MB/s`, or `Unlimited`.
- Pause/resume the active transfer.
- Cancel the active transfer without losing the partial copy.
- Resumes from a `.camptransfer.part` file on the destination.
- Drag and drop files/folders onto the window.
- Remembers queue, last destination, recent destinations, and speed setting.
- Copies to normal local, mapped-drive, and UNC paths such as `\\192.168.50.10\Media\Incoming`.
- Uses a temporary file and only replaces the destination file after a successful transfer.

## How to use

1. Make sure the destination share is reachable in Windows Explorer over the router-to-router WireGuard tunnel.
2. Enter or browse to the destination folder.
3. Click **Add Files**, **Add Folder**, or drag files onto CampTransfer.
4. To give some queued files another destination, select those rows, choose the other destination, and click **Set on Selected**.
5. Choose an upload limit.
6. Click **Start**.

The upload limit only applies to copies made by CampTransfer. It does not throttle the WireGuard tunnel itself or other programs.

## Build on Windows

Requirements: Visual Studio 2022 with the .NET desktop workload, or the .NET 8 SDK.

```powershell
dotnet build -c Release
```

Run:

```powershell
dotnet run
```

## Make a single self-contained Windows EXE

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

The executable will be `publish\CampTransfer.exe`.

## GitHub Actions

The included workflow builds a self-contained `CampTransfer.exe` on GitHub's Windows runner. Open the repository's **Actions** tab, run **Build CampTransfer**, and download the `CampTransfer-win-x64` artifact.
