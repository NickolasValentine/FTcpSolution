# FTcpSolution
<img alt="Client show" src="https://github.com/NickolasValentine/FTcpSolution/blob/master/FTcp/Resources/demo.jpg">

# FTcp: Client-Server FTP System

This project implements a server and client in C# for transferring files between participants via TCP connections, including P2P transfer between clients.

## Repository structure

- `Program.cs` — server part in pure console C#.
- `MainWindow.xaml` / `MainWindow.xaml.cs` — WPF client.
- `ServerFiles/` — directory for storing files on the server.
- `ClientFiles/` — directory for local client files.
- `*.log` — logging files (server.log, client.log).

## Contents

1. [Overview](#overview)
2. [Requirements](#requirements)
3. [Build and Run](#build-and-run)
- [Build](#build)
- [Start the Server](#start-server)
- [Start the Client](#start-client)
4. [Usage](#usage)
- [Upload to Server](#upload-to-server)
- [Download from Server](#download-from-server)
- [P2P Transfer](#p2p-between-clients-transfer)
5. [Architecture](#architecture)
6. [Configuration](#configuration)
7. [Logging](#logging)
8. [Debugging and Troubleshooting [troubleshooting](#debugging-and-troubleshooting)

## Overview

The system provides the following capabilities:

- Storing files on the server and listing them.
- Uploading
- Downloading
- Tracking changes (REFRESH) via `FileSystemWatcher`.
- Notifications about connecting/disconnecting clients.
- P2P transfer between clients with acknowledgement (ACK).

## Requirements

- .NET 6.0 or higher
- Windows (for WPF client)

## Build and run

### Build

1. Open the solution (or create a new project) in Visual Studio 2022.
2. Make sure that the target is .NET 6.0.
3. Add `Program.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs` files.

### Starting the server

```bash
cd path/to/project
dotnet run --project Server
```

### Starting the client

```bash
cd path/to/project
dotnet run --project Client
```

## Usage

### Uploading to the server

1. Select a local file in the client UI.
2. Click **Upload to Server**.
3. When prompted for **FILE_EXISTS**, confirm "Force Upload".

### Downloading from the server

- Double-click on the file in the list of server files.

### Transferring between clients (P2P)

1. Select a file and recipient from the list of clients.
2. Click **Send to Client**.
3. The invitation to receive will appear on the other client.

## Architecture

- **Program.cs**: main connection receiving loop, `HandleClientAsync`, `ProcessTemporaryCommand`, `HandleP2PTransfer`.
- **MainWindow.xaml.cs**: UI, background tasks for `NOTIFICATION_CHANNEL`, button handlers.

## Configuration

- If you need to change the server port, change `Port` in `Program.cs` and in the client UI.

## Logging

All operations are logged in `server.log` and `client.log`.

## Debugging and troubleshooting

- Make sure the `ServerFiles` and `ClientFiles` directories exist and are accessible.
- Check the logs for errors.
