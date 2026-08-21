# Build output policy

All Windows and Linux builds must be created by running `tools\Build-All.ps1`.

The canonical output directories are:

- `C:\Users\Tobias\Documents\ConsoleClientBuilds\Windows`
- `C:\Users\Tobias\Documents\ConsoleClientBuilds\Linux`

Do not create user-facing builds in repository `bin` or `builds` folders. The script may use its repository-local `.build-staging` directory temporarily.

Build updates must overwrite program files while preserving runtime data already in the canonical output directories, especially:

- `meshtastic-settings.xml`
- `meshtastic-messages.db`
- `Host\settings.ini`

The Windows host and its dependencies belong in the `Windows\Host` subdirectory to prevent assembly-version collisions with ConsoleClient.
