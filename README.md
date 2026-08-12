# MeshtasticNet472

MeshtasticNet472 is a C# 7.3 / .NET Framework 4.7.2 client library and terminal
application for the native Meshtastic Protobuf API. It connects by USB/serial
or TCP, stores messages, nodes and telemetry in SQLite, and supports optional
Telegram gateways and local or HTTP chat bots.

Source code and releases: https://github.com/kr-saibot/MeshtasticConsoleClient

![Screenshot of the terminal client](Screenshot.png)

## Features

- Native serial and TCP transports with automatic reconnect and connection statistics
- Node, channel, text-message, position and telemetry handling
- SQLite message, node and telemetry storage
- Terminal chat interface with direct chats, channels, node details and telemetry
- Optional Telegram gateways, local chat bots and HTTP bots
- Configurable appearance, logo rotation and emoji replacement/picker support

## Build

Prerequisites:

- .NET SDK with .NET Framework 4.7.2 targeting support
- NuGet package restore access
- A Meshtastic device, a TCP-connected Meshtastic service, or both for live use

```powershell
dotnet restore MeshtasticNet472.sln
dotnet build MeshtasticNet472.sln --configuration Release
```

For the Linux output configuration:

```powershell
dotnet build MeshtasticNet472.sln --configuration Linux
```

The terminal client is built to `src/ConsoleClient/bin/<configuration>/net472/`.
On Linux it is intended to run with Mono. The matching `libe_sqlite3.so` native
library must be available in the release directory or in the runtime-specific
native directory.

## Running

Start `ConsoleClient.exe`. On its first start, enter the serial or TCP settings
through the Settings menu. The application writes local settings and its SQLite
database next to the executable; these files can contain private information
and are deliberately excluded from version control.

## Appearance and logos

The terminal colours, frames and message colours can be changed in the
Appearance settings. ASCII logos are loaded from `src/ConsoleClient/logo/` and
can be replaced or extended with your own text files. The Logo settings control
whether the logo frame is shown, its size and the automatic rotation interval.

The emoji replacement list can be regenerated from official Unicode data:

```powershell
cd src\ConsoleClient
powershell -ExecutionPolicy Bypass -File .\tools\GenerateEmojiReplacements.ps1
```

## Security and privacy

Never commit `meshtastic-settings.xml`, SQLite databases, logs, Telegram bot
tokens, chat IDs or other credentials. If a token is exposed, revoke it at
BotFather immediately and create a replacement token before publishing.

## Credits

Created by Tobias Krista with assistance from artificial intelligence
(OpenAI Codex).

## License

Copyright (c) 2026 Tobias Krista.

This project is licensed under the **GNU General Public License, version 3.0
only (GPL-3.0-only)**. See [LICENSE](LICENSE). The Meshtastic Protobuf
definitions included in this repository are also GPL-3.0; their original
license copy is retained in `LICENSE-Meshtastic-Protobufs.txt`.

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for third-party component
attribution and release-packaging notes.
