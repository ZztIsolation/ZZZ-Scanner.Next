# ZZZ Scanner Next

English | [简体中文](README.zh-CN.md)

ZZZ Scanner Next is a Windows scanner and exporter for the Drive Disc inventory
in *Zenless Zone Zero*. It reads the visible game UI by bringing the game window
forward, clicking cells, scrolling the list, taking screenshots, and running
PP-OCRv5 locally. It does not read game memory, inject code, install a game
plug-in, or hook the game process.

The project contains:

- the WinForms scanner;
- a NativeAOT Helper used by the web calculator;
- a release pipeline that builds framework-dependent and self-contained
  Windows packages from the same scanner source.

## Contents

- [Supported environment](#supported-environment)
- [Choose an installation method](#choose-an-installation-method)
- [Recommended: calculator and Helper](#recommended-calculator-and-helper)
- [Manual installation](#manual-installation)
- [Prepare the game](#prepare-the-game)
- [Use the desktop scanner](#use-the-desktop-scanner)
- [Output files](#output-files)
- [Permissions and UAC](#permissions-and-uac)
- [Troubleshooting](#troubleshooting)
- [Known limitations](#known-limitations)
- [Command-line use](#command-line-use)
- [Build from source](#build-from-source)
- [Security and privacy](#security-and-privacy)

## Supported environment

| Item | Supported |
| --- | --- |
| Operating system | Windows 10 1809 / Build 17763 or later, and Windows 11 |
| Architecture | x64 Windows and x64 packages |
| Windows editions | Normal, N, and LTSC editions are release targets |
| Game client | Local PC client and Cloud Zenless Zone Zero |
| Game language | Simplified Chinese UI |
| Primary layout | Current Simplified Chinese Drive Disc inventory, primarily 16:9 |
| OCR | Bundled PP-OCRv5 ONNX model; recognition runs locally |

Not currently supported:

- Windows 7, Windows 8/8.1, or Windows 10 older than Build 17763;
- 32-bit Windows, x86 processes, and ARM64 Windows;
- non-Chinese game UI languages;
- mobile or console clients, Wine/Proton, and unknown streaming layouts;
- inventory UI replacement mods or layouts changed by a future game update.

The Helper checks OS build and architecture before downloading. A manually
extracted scanner bypasses that preflight, but it does not make an unsupported
system compatible.

### Verified visual profiles

Strict Fast OCR profiles have been validated for:

- Local client: <code>1280x720</code>, <code>1600x900</code>, and
  <code>1920x1080</code>.
- Cloud client: <code>1440x808</code>, <code>1592x896</code>, and
  <code>1920x1080</code>.

Other 16:9 resolutions may work because coordinates are scaled relative to the
game client area. Unknown visual profiles fall back to PP-OCR instead of reusing
a nearby Fast OCR profile. Non-16:9 layouts, custom UI scale, HDR/filter
changes, and future UI changes are not guaranteed.

## Choose an installation method

### Method A: calculator + Helper

Recommended for most users. The Helper:

- checks Windows, architecture, write permission, and free space;
- detects the .NET 8 Windows Desktop Runtime;
- downloads only the package suitable for the computer;
- verifies package size, SHA-256, ZIP paths, entry point, and installed files;
- resumes interrupted downloads and tries configured mirrors;
- repairs corrupt caches and reports structured errors to the calculator.

### Method B: manual package

Use this for the standalone GUI or command-line tools:

| Package | Choose it when | .NET requirement |
| --- | --- | --- |
| <code>ZZZ-Scanner.Next-win-x64-fdd.zip</code> | <code>Microsoft.WindowsDesktop.App 8.x</code> x64 is already installed | Must already exist |
| <code>ZZZ-Scanner.Next-win-x64-self-contained.zip</code> | .NET 8 is absent or you are unsure | None |

Both packages contain the same scanner, model, data, ONNX Runtime, and app-local
Visual C++ runtime files. The self-contained package is larger because it also
contains the .NET desktop runtime.

If the FDD package reports that .NET is missing, use the self-contained package.
Installing .NET is not required just for this scanner.

## Recommended: calculator and Helper

### 1. Download the Helper

Download <code>ZZZ-Scanner-Helper.exe</code> from the official
[GitHub Releases](https://github.com/ZztIsolation/ZZZ-Scanner.Next/releases).
Keep it in a stable, writable location. Do not run it from inside a ZIP, an
attachment preview, or a temporary directory that will be deleted.

The Helper is NativeAOT and does not require .NET 8.

### 2. Verify and start it

The current binaries are unsigned. SmartScreen, antivirus, or enterprise policy
can block the Helper before it starts. In that case it cannot display its own
diagnostic because no Helper process exists yet.

Only allow a file obtained from the official release. Check its hash with:

~~~powershell
Get-FileHash .\ZZZ-Scanner-Helper.exe -Algorithm SHA256
~~~

Compare the result with the release information. Do not disable security
software globally and do not download DLLs from third-party DLL sites.

Run the Helper once. It registers <code>zzz-scanner://</code> for the current
Windows account and listens only on <code>127.0.0.1:22355</code>. If the EXE is
moved, run it again so the protocol registration points to the new location.
Only one Helper instance can own port 22355.

### 3. Start from the calculator

1. Start the game and sign in.
2. Set the game UI to Simplified Chinese.
3. Open Inventory, then the Drive Disc list.
4. Open the scanner page in the supported calculator.
5. Select the local or cloud client.
6. Start scanning and allow the browser to open <code>zzz-scanner://</code>.
7. Keep the game visible. Do not use the mouse, scroll wheel, inventory tabs,
   sort, filters, or window controls until the scan ends.

The browser obtains a one-time token from the Helper and then uses a
token-protected loopback WebSocket. Arbitrary websites are not accepted.

### 4. Automatic package choice

The Helper checks registered .NET locations, standard installation directories,
and <code>dotnet --list-runtimes</code>.

- Confirmed Desktop Runtime 8.x: use <code>win-x64-fdd</code>.
- Missing or uncertain runtime: use <code>win-x64-self-contained</code>.
- Runtime disappears before launch: fall back once to self-contained.

The Helper never starts a .NET installer and does not change the machine-wide
.NET installation.

Current space checks are approximately 160 MiB for FDD and 358 MiB for
self-contained, including ZIP, extraction, and a 100 MiB safety margin. The
calculator receives the exact required byte count.

### 5. Cache and update behavior

~~~text
%LOCALAPPDATA%\ZZZScannerNext\
  helper\
    ZZZ-Scanner-Helper.exe
  packages\
    temporary downloads only
  runtime\
    <version>\
      <packageId>\
  outputs\
    newest successful and failed sessions
  logs\
    helper-YYYYMMDD.log
~~~

Helper 1.2.1 installs itself into the fixed current-user helper directory and
registers the browser protocol to that path. Future Helper releases update this
managed file transactionally. Helper 1.1.x requires one final download: run the
1.2.1 installer and confirm the takeover once, and it safely closes the uniquely
verified old Helper, installs the managed copy, and restarts it automatically.

Manifest schema v3 lists the size and SHA-256 of every runtime file. The Helper
therefore deletes the package ZIP after a verified install and still validates
the installed tree before reuse. A newly downloaded runtime does not become
active until its child WebSocket handshake succeeds. The activation receipt is
then replaced and every inactive runtime is removed. During an update, the old
and new runtimes coexist briefly so a failed launch cannot destroy the working
version.

Managed scan outputs are version-independent. Cleanup migrates legacy
<code>runtime/**/Scans</code> sessions and retains only the newest successful
session and newest failed session. The calculator Settings page reports exact
usage and can repeat this cleanup without uninstalling the active Scanner.

## Manual installation

### 1. Extract the complete package

Extract the whole ZIP into a normal writable directory. Do not:

- run the EXE from inside the archive;
- copy only the EXE and omit <code>Data</code>, <code>Resources</code>, or DLLs;
- mix files from different versions;
- replace bundled DLLs with downloads from unrelated sites.

The EXE, <code>Data</code>, <code>Resources\models</code>,
<code>onnxruntime.dll</code>, and the bundled VC runtime files must remain
together.

### 2. Start the GUI

Run <code>ZZZ-Scanner.Next.exe</code>. It uses the current user's permissions
and does not request administrator access by default.

Standalone defaults:

- Process: <code>ZenlessZoneZero</code>
- Read limit: <code>0</code>, meaning no explicit item limit
- Rarity: S
- Only level-15 Drive Discs: enabled
- Bring game forward: enabled
- High-speed OCR: enabled
- Capture backend: GDI
- Traversal: profile default, currently overlap-signature scanning
- Verified Fast Mode: disabled until selected

For Cloud Zenless Zone Zero, use this process name:

~~~text
Zenless Zone Zero Cloud
~~~

## Use the desktop scanner

### 1. Detect the window

Click **检测窗口 / Detect Window**.

- Success means the configured process and a usable main window were found.
- Failure usually means the process name is wrong, the game is not running, the
  local/cloud selection is wrong, or the game window is not ready.
- A higher-integrity game cannot be controlled by a normal scanner. See
  [Permissions and UAC](#permissions-and-uac).

### 2. Start safely

Before clicking **开始扫描 / Start Scan**:

- open the Drive Disc inventory;
- keep the game visible and not minimized;
- close overlays and windows covering the grid or detail panel;
- keep display scale, HDR, resolution, UI scale, sort, and filters unchanged;
- stop using the mouse and keyboard for the duration of the scan.

The scanner deliberately controls the pointer and foreground window. Using the
computer during a scan can select the wrong item or trigger safety stops.

Use **停止 / Stop** when needed. A canceled scan may still leave diagnostics in
its output directory.

### 3. Basic settings

| Setting | Meaning | Guidance |
| --- | --- | --- |
| Process name | Windows process to locate | <code>ZenlessZoneZero</code> locally; <code>Zenless Zone Zero Cloud</code> for cloud |
| Read limit | Maximum captured items; 0 has no explicit limit | Use 30 for a smoke test, 120 for validation, 0 for normal import |
| S / A | Rarity filters shown by the GUI | Calculator workflows normally use S |
| Only level-15 | Stops at the first lower-level Drive Disc | Keep enabled for normal calculator import |
| Bring to foreground | Activates the game before input and capture | Normally keep enabled |

Sort the inventory so desired level-15 items appear before lower-level items.
Stopping at the first non-15 item is expected behavior.

Disabling **Only level-15** is experimental. Lower-level panels may contain fewer
substat rows, so full non-15 scanning can fail ROI completeness checks. It is
not the normal calculator import path.

### 4. Advanced settings

Leave these at defaults for the first scan.

- **Verified Fast Mode** enables the validated fast profile, strict visual
  routing, Fast OCR assist, early one-row acceptance, adaptive full-ROI panel
  acceptance, and recoverable overlap conflicts. Invalid templates cause a
  safe fallback.
- **GDI** is conservative. **DXGI** can be faster and falls back to GDI if
  initialization, frame acquisition, or monitor matching fails.
- Debug screenshots and OCR shadow datasets can create many local files and are
  intended for diagnosis or model work.
- Worker, batch, queue, and IntraOp values affect OCR throughput. Aggressive
  values can raise CPU/memory usage and disrupt UI timing.
- Experimental panel and scroll timing controls exist for repeatable benchmarks,
  not as universal speed recommendations.

## Prepare the game

Before every scan:

1. Use the Simplified Chinese UI.
2. Open the Drive Disc inventory.
3. Use a supported local/cloud window layout.
4. Keep Windows scaling and game resolution stable.
5. Remove overlays from the inventory and detail panel.
6. Confirm sort and filters.
7. Run a 30-item smoke scan after a game update, driver change, resolution
   change, or switching between local and cloud.

If the smoke scan reports failed items, duplicates, incomplete ROIs, unexpected
profile fallback, or an incorrect count, inspect <code>scan.log</code> before a
full import.

## Output files

Each scan creates a unique directory next to the scanner executable:

~~~text
Scans\YYYY-MM-DD-HH-mm-ss-fff-p<process>-<random>\
~~~

| File | Purpose |
| --- | --- |
| <code>export.json</code> | Cleaned Drive Disc records used for import |
| <code>scan.log</code> | Version, options, progress, safety events, fallbacks, and errors |
| <code>ocr_diagnostics.csv</code> | Per-ROI timing and OCR diagnostics |
| <code>*.error.txt</code> | Failure details for an item |
| <code>*.non15.txt</code> | First lower-level item that caused a normal stop |

Optional modes can add shadow ROI images, debug screenshots, Fast OCR CSV files,
resource metrics, and visual profile metadata.

The GUI's **打开产物文件夹 / Open Output Folder** button opens the latest result.
In Helper mode, results are under
<code>%LOCALAPPDATA%\ZZZScannerNext\outputs</code>.

Review diagnostics before sharing. Screenshots contain visible game UI, and
logs can contain local paths and machine-specific information.

## Permissions and UAC

The scanner uses <code>asInvoker</code>. Normal operation should not require
administrator rights.

Windows prevents a lower-integrity process from controlling a higher-integrity
game. If the game was started as administrator, the scanner reports
<code>elevation_required</code>. The calculator can ask the Helper to restart
only the scanner through UAC.

- Approve UAC only after initiating the scan yourself.
- Canceling UAC is reported as <code>uac_cancelled</code>, not a timeout.
- Prefer running both the game and scanner normally instead of permanently
  running everything as administrator.

## Troubleshooting

### Helper does not start

Check SmartScreen, Windows Security protection history, antivirus quarantine,
enterprise policy, the x64/OS requirement, and whether another Helper owns port
22355. A pre-start security block cannot be diagnosed inside the application.

### Calculator says Helper is missing or outdated

1. In the calculator, select **Download and update Helper**.
2. Run Helper 1.2.1 and confirm the one-time takeover.
3. Leave the scan drawer open; the installer closes the verified old Helper,
   installs the managed copy, and the page reconnects automatically.

Do not terminate an unknown process that owns port 22355. The installer refuses
to take over when the service identity, version, or candidate process is
ambiguous and reports the recovery action instead.

A healthy Helper reports its versions at
<code>http://127.0.0.1:22355/</code>.

### Download fails

Use Retry. The Helper resumes partial downloads and tries all mirrors.
Persistent failures usually involve offline/filtered network access, TLS proxy
problems, an incomplete release upload, insufficient space, no write access
under LocalAppData, or security software removing the ZIP.

Remote manifests, packages, and redirects require HTTPS. Loopback HTTP is only
for local development.

### Package is corrupt or files are missing

Choose **Repair scanner**. The Helper clears the selected cache, downloads
again, verifies SHA-256, extracts to a temporary directory, checks every file,
and atomically replaces the runtime.

### Native DLL missing or process exits immediately

Repair first. The release already includes ONNX Runtime and required app-local
VC files. If Windows still reports <code>0xC0000135</code>:

1. check whether antivirus quarantined a bundled DLL;
2. confirm the complete archive was extracted;
3. confirm the x64 package is being used;
4. open Helper logs and include the diagnostic ID in a report.

Do not download an individual DLL from a third-party site.

### Game not found

- Start the game before scanning.
- Use <code>ZenlessZoneZero</code> for local.
- Use <code>Zenless Zone Zero Cloud</code> for cloud.
- Open the Drive Disc inventory.
- Confirm the process is the game, not the launcher/updater.

### Data is wrong or scanning stops

Confirm the Chinese UI, supported layout, unobstructed visible window, no user
input during scanning, stable sort/filter/display settings, and correct
local/cloud selection. Inspect <code>scan.log</code> for incomplete ROIs,
profile routing, fallbacks, duplicates, overlap conflicts, and slot-safety
events. After a game UI update, treat profiles as potentially stale until a
smoke scan passes.

### Logs and diagnostics

Use **Open log folder** or **Copy diagnostics** in the calculator:

~~~text
%LOCALAPPDATA%\ZZZScannerNext\logs
~~~

Startup failures before browser connection use a native Windows dialog with an
error code, diagnostic ID, and log location.

## Known limitations

- The scanner depends on visible UI coordinates, colors, panel timing, and OCR
  text. A game patch can break it without changing the process name.
- Only the current Simplified Chinese dictionary and inventory layout are
  maintained.
- Fast OCR profiles cover a finite list of local/cloud resolutions. PP-OCR
  fallback does not prove an unknown layout is safe.
- Full lower-level Drive Disc scanning is not a supported calculator workflow.
- GDI requires a visible desktop. Minimized, covered, locked, disconnected, or
  remote-session windows can produce stale or blank captures.
- DXGI depends on GPU, driver, display, and session and may fall back to GDI.
- HDR, color filters, overlays, UI mods, and color-management changes can alter
  rarity and stability checks.
- Unsigned binaries can be blocked before self-diagnostics are possible.
- No program can guarantee every Windows 10/11 installation. Damaged system
  files, restrictive policy, security products, unsupported hardware, and
  future game changes remain outside its control.
- A non-invasive implementation is not game-publisher approval. Users are
  responsible for applicable rules and account risk.
- This community project is not affiliated with or endorsed by HoYoverse.

## Command-line use

Command-line modes are intended for repeatable validation and development. A
live scan controls the mouse and game window just like the GUI.

### One scan

~~~powershell
.\ZZZ-Scanner.Next.exe --scan-once --max-items 30
.\ZZZ-Scanner.Next.exe --scan-once --max-items 120 --fast-mode --capture-mode dxgi
~~~

| Option | Meaning |
| --- | --- |
| <code>--process &lt;name&gt;</code> | Local or cloud process |
| <code>--profile &lt;name&gt;</code> | Exact scan profile |
| <code>--max-items &lt;n&gt;</code> | 0 has no explicit limit |
| <code>--rarities S,A</code> | Comma-separated rarity filters |
| <code>--include-non15</code> | Experimental lower-level scanning |
| <code>--no-bring-to-front</code> | Do not activate the game |
| <code>--capture-mode gdi|dxgi</code> | Capture backend |
| <code>--fast-mode</code> | Validated fast profile and Fast OCR |
| <code>--adaptive-timing</code> | Force per-run adaptive timing |
| <code>--no-adaptive-timing</code> | Disable adaptive timing |
| <code>--ocr-workers 0..4</code> | 0 selects automatically |
| <code>--ocr-batch 1..16</code> | OCR batch size |
| <code>--ocr-queue 1..256</code> | Queue capacity |
| <code>--ocr-intra-op 1..8</code> | ONNX intra-op threads |
| <code>--config &lt;json&gt;</code> | Load a ScanRunCommand JSON file |

The command prints output paths and counts. Exit code 0 means no failed items,
1 means failure, 73 means another scan owns the mutex, and 130 means canceled.

### Offline benchmark

~~~powershell
.\ZZZ-Scanner.Next.exe --scan-benchmark <scan-directory> [baseline-directory]
~~~

This reads existing output only and does not control the game. Release checks
normally require zero duplicate exports, zero error files,
<code>slot_safety_pass=true</code>, zero hard overlap stops, and the expected
profile route/item count.

### Maintainer tools

~~~powershell
.\ZZZ-Scanner.Next.exe --capture-stability-suite both --max-items 120 --rounds 5
.\ZZZ-Scanner.Next.exe --scan-stability-suite <suite-directory>
.\ZZZ-Scanner.Next.exe --ocr-shadow-analyze <scan-or-parent> --build-fast-index <index.json>
.\ZZZ-Scanner.Next.exe --ocr-fast-eval <index.json> <shadow-parent>
.\ZZZ-Scanner.Next.exe --ocr-fast-cross-validate <shadow-parent>
.\ZZZ-Scanner.Next.exe --ocr-fast-calibrate <shadow-parent> --output <index.json> --feature v6
.\ZZZ-Scanner.Next.exe --ocr-fast-calibrate-visual-profiles <shadow-parent> --output <index.json> --feature v6
.\ZZZ-Scanner.Next.exe --ocr-fast-merge-indexes <output.json> <index1.json> <index2.json> [...]
~~~

Calibration requires multiple clean runs. Faster output alone is not enough to
enable a release policy. See [architecture](docs/ARCHITECTURE.md) and
[testing evidence](docs/TESTING.md).

## Build from source

Requirements:

- Windows x64;
- .NET 8 SDK;
- <code>Resources\models\PP-OCRv5_mobile_rec_infer.onnx</code>.

The large model is not tracked in Git. Obtain it from an official project
release and verify it.

~~~powershell
dotnet restore
dotnet build ZZZ-Scanner.Next.csproj -c Release -p:NuGetAudit=false
dotnet build Launcher\ZZZ-Scanner.Helper.csproj -c Release -p:NuGetAudit=false
dotnet run --project Tests\ZZZ-Scanner.Next.RegressionTests.csproj -c Release -p:NuGetAudit=false
.\scripts\publish-slim.ps1 -Version 1.0.38
~~~

Outputs:

~~~text
dist\ZZZ-Scanner.Next-win-x64-fdd.zip
dist\ZZZ-Scanner.Next-win-x64-self-contained.zip
dist\publish-helper\ZZZ-Scanner-Helper.exe
dist\scanner-manifest-<version>.json
dist\publish-report-<version>.json
~~~

Release gates enforce 25 MiB FDD, 90 MiB self-contained, and 10 MiB Helper
limits; no OpenCvSharp/PDB files; matching models; required VC/ONNX files;
complete PE dependencies; and deterministic ZIP paths/timestamps.

Official CI uses <code>-RequireVCRedistLayout</code>. Local builds may record a
System32 fallback, but those are not official release artifacts.

## Security and privacy

- OCR and screenshots are processed locally.
- Normal scans do not upload screenshots to this repository.
- Web integration uses loopback HTTP/WebSocket, an origin allowlist, and a
  one-time token.
- Remote manifests, packages, and redirects require HTTPS.
- ZIP extraction rejects absolute paths, traversal, and entries outside the
  controlled runtime root.
- Cache reuse requires package and per-file integrity verification.
- Scanner WebSocket messages are size-limited and only one scan runs at a time.

Debug screenshots and OCR shadow datasets are written locally when explicitly
enabled. Review them before sharing.

## More documentation

- [Architecture](docs/ARCHITECTURE.md)
- [Testing and release evidence](docs/TESTING.md)
- [Data sources](docs/DATA_SOURCES.md)
- [Changelog](docs/CHANGELOG.md)
- [YAS study notes](docs/yas-study.md)

## License

[MIT](LICENSE)
