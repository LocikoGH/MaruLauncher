# MaruLauncher

Windows desktop launcher for Minecraft: Java Edition, built with .NET 8 and WPF.

## Build and run

- Install the .NET 8 SDK with the Windows Desktop workload/runtime.
- Open `MaruLauncher.csproj` in Visual Studio or run `dotnet build .\MaruLauncher.csproj` from PowerShell.
- The launcher finds the application root by locating its `java` runtime folder and resolves the relative per-instance path `java\bin\javaw.exe` from that root. The settings page uses this relative path for the bundled runtime, so moving the whole project/application folder does not leave a stale absolute Java path. Java selection checks the Minecraft metadata requirement and prefers an exact major-version match; the included Java 25 can be used as a newer compatible runtime when Java 21 is not installed.
- Launcher data is stored in `%LOCALAPPDATA%\MaruLauncher`; each instance defaults to its own `instances\<id>\game` directory.

## Offline profiles

MaruLauncher currently supports local Offline profiles only. Microsoft account sign-in, OAuth, and associated saved token files have been removed. Offline profiles are for single-player, local testing, and compatible local servers. They do not prove ownership of Minecraft, do not provide normal online authentication, and may not work on servers requiring a verified account. On startup, profiles and token files from older Microsoft-enabled versions are removed.

## Supported installation and packs

- The version selector reads Mojang's official release manifest and offers Minecraft 26.3, 26.1.2, 1.21.11, and 1.21.4. Each instance has its own game directory, so its client JAR, metadata, libraries, assets, and extracted natives are installed independently. A bundled `versions\<version>\<version>.jar` client is used only after its SHA-1 matches Mojang's version metadata; missing files are downloaded and checked against official hashes when provided.
- Launch arguments, classpath, logging configuration, asset index, and required Java major version come from the selected version's official metadata. Windows native JARs in both legacy classifier metadata and modern `natives-windows` Maven coordinates are extracted into the JVM library directories before launch; architecture-specific natives are selected for the running process. The launcher prefers an installed Java matching the required major version, then accepts a newer compatible version. It checks the configured Java, bundled Java, `JAVA_HOME`, `PATH`, and standard Windows Java installation folders. The game runs without a console window. Java output is captured in `instances\<id>\diagnostics\java-process.log`; an early process exit is reported as a launch error instead of a successful start. Minecraft's `logs\latest.log` remains available in the selected game directory and is included in launcher diagnostics.
- Library and asset downloads can run concurrently, while writes to the same target file and installations sharing a game directory are serialized. Downloads use unique temporary files, verify metadata sizes and SHA-1 hashes before an atomic move, retry transient Windows sharing violations a bounded number of times, and log failures to `data\logs\launcher.log` in portable mode or `%LOCALAPPDATA%\MaruLauncher\logs\launcher.log` otherwise. Abandoned `.download` files older than one day are cleaned up when that target is next downloaded.
- New instances install the latest stable Fabric Loader automatically by default; disable **Автоматически установить Fabric** to create a Vanilla instance. Fabric installation requires a usable Java runtime. Fabric API is a separate mod and is fetched from Modrinth only after the user explicitly requests it.
- Modpack import supports Modrinth `.mrpack` and MaruLauncher `.zip` exports. Unsupported formats are rejected; external mod downloads are displayed for confirmation.
- The mod checker reports likely issues but cannot guarantee runtime compatibility between mods.
- Backups include only the selected instance folders and settings. Credentials are never included. Potential secret values in supported text configuration files are redacted; oversized or unreadable text files are omitted. During restore, Java path, game directory, Fabric profile, and JVM arguments remain local to the target instance.

The launcher does not execute scripts from modpacks, automatically remove mods, or claim that offline mode grants a license. Mods run inside Minecraft and can contain code; only import packs and add mods from sources you trust.

## Portable release

Publish a self-contained Windows x64 build with `dotnet publish .\MaruLauncher.csproj -c Release -r win-x64 --self-contained true -o .\release`. The output contains the launcher, its .NET runtime, the bundled Java runtime, and the four local version JARs. The publish target writes `portable.txt`, which makes launcher settings and instance data live in `release\data` rather than `%LOCALAPPDATA%`. Minecraft libraries and assets are downloaded from Mojang when an instance is installed.

Run the dependency-free concurrency and temporary-file checks with `dotnet run --project .\tests\MaruLauncher.Tests.csproj -c Release`.
