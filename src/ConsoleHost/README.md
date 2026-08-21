# Meshtastic Console Host for Windows

`MeshtasticConsoleHost.exe` runs the unchanged `ConsoleClient.exe` in its own
WinForms window. It uses Windows ConPTY and the Windows Terminal renderer to
support terminal colours, Unicode symbols, keyboard navigation and mouse input.

## Running

Place the complete host release beside `ConsoleClient.exe` and start
`MeshtasticConsoleHost.exe`. The local client always has priority over a path
stored in `settings.ini`. If no local client exists, the host uses a valid
manually selected fallback or displays a file picker.

An explicit executable path can also be passed as the first command-line
argument. This is mainly useful while developing or testing the host.

## Host functions

- Font sizes 9, 10, 12, 14, 16 and 18 from the Windows system menu
- Persistent font size, window size, dark title bar and fallback client path
- Optional minimized startup and startup with Windows
- Window Close button minimizes; system-menu **Close** and **Alt+F4** exit
- Terminal scrollbar hidden and mouse-wheel input forwarded to the client
- Taskbar flashing when a BEL character is received
- Bundled default alarm sound and selection of a custom audio file
- Hosted ConsoleClient is terminated when the host exits

The settings are written to `settings.ini` beside the host executable. The
selected ConsoleClient runs in its own directory, keeping its configuration and
database next to that executable.

## Building

Build the host and optionally pass the ConsoleClient path for a test run:

```powershell
dotnet build ConsoleHost.csproj --configuration Debug
bin\Debug\net472\MeshtasticConsoleHost.exe `
  ..\ConsoleClient\bin\Release\net472\ConsoleClient.exe
```

ConPTY requires Windows 10 version 1809 or newer. The host targets x64 because
the embedded Windows Terminal renderer contains native x64 components. ConPTY
is part of Windows and does not add a separate redistributable license.
