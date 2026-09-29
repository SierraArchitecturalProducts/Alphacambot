# AlphacamBridge — Developer Guide

This document contains everything needed to understand, build, deploy, and extend the AlphacamBridge MCP plugin. It is written for an LLM agent that may be picking up this project cold.

---

## What This Is

AlphacamBridge is an MCP server that lets any MCP-capable LLM agent (Claude Desktop, or any other MCP client) control Hexagon Alphacam (a Windows CAM application) via its COM automation API. The user issues natural-language commands; the agent calls MCP tools; those tools traverse a multi-layer bridge to invoke COM methods on the running Alphacam instance.

## Architecture (Data Flow)

```
MCP client (Claude Desktop, or any MCP-capable agent)
  ↕  stdio (MCP protocol)
Node.js MCP Server  (alphacam-mcp-server/)
  ↕  Named pipe "AlphacamBridge" — JSON-RPC 2.0, 4-byte big-endian length prefix
.NET 8 Launcher  (AlphacamBridge.Launcher/)
  ↕  COM interop via Running Object Table (ROT)
.NET 4.8 Shim  (AlphacamBridge.Shim/)  — loaded in-process by Alphacam
  ↕  COM interface (IAlphaCamApp)
Alphacam Application
```

Two runtime boundaries matter:

1. The shim runs inside Alphacam's .NET 4.8 process. It cannot be .NET 8.
2. The Launcher runs as a separate .NET 8 process. It communicates with the shim exclusively through the COM Running Object Table (ROT).

## Source Tree (`AlphacamBridge/src/`)

### `AlphacamBridge.Shim/`
**.NET Framework 4.8, x64.** Loaded by Alphacam's add-in mechanism.

- **AlphacamAddinShim.cs** — Entry point. Class `AlphacamEvents` in namespace `CambotAlphacam.Models`. Constructor receives `IAlphaCamApp` from Alphacam.
  - Registers `IAlphaCamApp` in the COM ROT under CLSID `F47AC10B-58CC-4372-A567-0E02B2C3D479` using P/Invoke (`RegisterActiveObject`/`RevokeActiveObject`).
  - Subscribes to `InitAlphacamAddIn` event and sets `Data.ReturnCode = 0` — **without this, Alphacam disposes the add-in within ~9 seconds**.
  - On `InitAlphacamAddIn`, calls `EnsureLauncherRunning()` which starts the Launcher process.
  - Logs to `%LOCALAPPDATA%\AlphacamBridge\shim.log`.
- **AlphacamBridge.Shim.csproj** — `AssemblyName=CambotAlphacam`, `RootNamespace=CambotAlphacam`. Output is `CambotAlphacam.dll`.

### `AlphacamBridge.Launcher/`
**.NET 8, Windows.** Out-of-process host.

- Reads `bridge-config.json` (see Configuration below).
- Connects to Alphacam's `IAlphaCamApp` via ROT using the shared CLSID.
- Hosts a named-pipe JSON-RPC 2.0 server that the Node.js MCP server connects to.
- Dispatches JSON-RPC methods to handler classes.

### `AlphacamBridge.ComInterop/`
**.NET 8.** COM interaction layer.

- **AlphacamConnection.cs** — Manages the COM connection. Uses `ComHelper.GetActiveObject(Guid)` to retrieve the ROT-registered object. The custom CLSID `F47AC10B-58CC-4372-A567-0E02B2C3D479` must match the shim.
  - **Do NOT use `GetActiveObject("Alphacam.Application")`** — Alphacam does not register a COM automation server by ProgID. This will fail with `CO_E_CLASSSTRING (0x800401F3)`.
- **StaThreadDispatcher.cs** — Marshals COM calls from the pipe server's async I/O threads onto a dedicated STA thread. Used by `alphacam.diag` (cross-process COM smoke tests) and by `alphacam.status` to read `App.ProgramLetter` for the `Module` field.
- **ShimPipeClient.cs** — JSON-RPC 2.0 client for the shim's `AlphacamBridgeCmd` pipe. The Launcher forwards every `alphacam.*` method to the shim through this.

The IToolHandler implementations that previously lived under `Handlers/` were removed 2026-05-29 — `ComCommandServer` in the shim owns all dispatch now.

### `AlphacamBridge.Core/`
**.NET 8.** Shared types, protocol, security, configuration.

- **Configuration/BridgeConfiguration.cs** — Strongly-typed config. Loaded from JSON.
- **Protocol/** — JSON-RPC 2.0 message types, `PipeServer`, `JsonRpcResponse` helper.
- **Security/** — API key auth, pipe ACL, path validation (PathValidator), auth scopes.

The `Tools/` and `ObjectCache/` subfolders were removed 2026-05-29 (only existed to support the deleted IToolHandler implementations).

### `AlphacamBridge/`
**.NET 8.** Shared project or additional utilities (check contents as needed).

### `alphacam-mcp-server/`
**Node.js.** The MCP server that the MCP client talks to. Built on the official `@modelcontextprotocol/sdk`; nothing in it is client-specific (see `src/__tests__/generic-client.test.js`, which drives it with the reference SDK client).

- **src/index.js** — Entry point. Parses `--pipe` and `--key` CLI args. Creates MCP server, connects to the named pipe, registers tools from `tool-definitions.js`.
- **src/tool-definitions.js** — All MCP tool definitions (name, description, inputSchema). Also exports `toBridgeMethod()`.
- **src/pipe-client.js** — Named pipe client. Implements the 4-byte big-endian length-prefixed JSON-RPC 2.0 protocol. Handles the initial auth handshake (sends `{"clientId":"mcp-server","apiKey":"..."}` as raw JSON before entering JSON-RPC mode).

## Critical Implementation Details

### Tool Name Mapping

MCP tool names use underscores. The bridge uses a dot after the category prefix.

```
MCP:    alphacam_get_drawing_info
Bridge: alphacam.get_drawing_info
```

`toBridgeMethod()` in `tool-definitions.js` does: `mcpToolName.replace('_', '.')` — this is a **string** replace (not regex), so only the **first** underscore becomes a dot. Using `/_/g` (regex global) would incorrectly produce `alphacam.get.drawing.info` and the bridge handler won't match.

### Named Pipe Protocol

1. **Auth handshake** (if enabled): Client sends raw JSON `{"clientId":"...","apiKey":"..."}`, server responds with raw JSON `{"success":true,...}`.
2. **JSON-RPC messages**: Each message is preceded by a 4-byte big-endian uint32 containing the byte length of the JSON payload, followed by the raw UTF-8 JSON.

### COM API (IAlphaCamApp / IDrawing)

**Do not guess property or method names.** The Alphacam COM API uses specific naming that doesn't follow typical .NET conventions. Known correct members (verified via reflection on `Interop.AlphaCAMMill.dll`):

**IAlphaCamApp:**
- `.ActiveDrawing` — returns IDrawing
- `.New()` — create new drawing
- `.OpenDrawing(path)` — open a file
- `.Frame` — returns IFrame

**IDrawing:**
- `.FullName` (NOT `.FileName`) — full path
- `.Name` — filename only
- `.Path` — directory only
- `.GetGeoCount()` (NOT `.GeometryCount` or `.Geometries.Count`) — method, not property
- `.GetToolPathCount()` — method
- `.GetLayerCount()` — method
- `.Modified` — bool, whether drawing has unsaved changes
- `.Save()` / `.SaveAs(path)`
- `.OutputNC()` / `.ListNC()`

**API reference files** ship with ALPHACAM as help files: `ACAMAPI.chm`, `AEDITAPI.chm`, `ACAM4.chm`, etc. (search your ALPHACAM installation). These contain the full COM API documentation. **Always consult these before writing new handlers.**

To inspect the COM API via PowerShell on the target machine:
```powershell
$asm = [Reflection.Assembly]::LoadFile("path\to\Interop.AlphaCAMMill.dll")
$asm.GetTypes() | Where-Object { $_.Name -eq 'IDrawing' } | ForEach-Object { $_.GetMembers() | Select Name, MemberType }
```

### Handler Pattern (shim, .NET 4.8)

All `alphacam.*` methods land in `ComCommandServer.DispatchMethod(method, prms)` in the shim. It's a single switch keyed on the method name; each case calls into `IAlphaCamApp` / `IDrawing` / `MillData` via `dynamic` (the Mill interop's late binding handles overloads at runtime).

```csharp
switch (method)
{
    case "alphacam.create_circle":
    {
        dynamic drw = _app.ActiveDrawing;
        dynamic path = drw.CreateCircle(
            GetDouble(prms, "diameter"),
            GetDouble(prms, "centerX"),
            GetDouble(prms, "centerY"));
        return new Dictionary<string, object> { ["geometryId"] = StoreObject(path) };
    }
    // ...
}
```

UI-thread marshalling is handled by `Control.Invoke` at the level of `ProcessRequest`, so every case runs on Alphacam's UI thread without per-case ceremony. COM objects returned to callers are cached by `StoreObject` and addressed by `obj_N` ids; the cache dedupes by `Marshal.GetIUnknownForObject` identity so multiple calls returning the same underlying COM object share an id.

When adding a handler:
1. Look up the COM method in `ACAMAPI.chm` (use `alphacam_api_help` for fast lookup). Verify exact arity and parameter types.
2. Add a `case "alphacam.<name>":` block in `DispatchMethod`.
3. If the method needs a custom MCP-facing parameter shape, also update `tool-definitions.js` in the Node.js MCP server.
4. Rebuild shim (Debug, .NET 4.8), run `Deploy-Shim.ps1` (auto-stages the new DLL from `bin\Debug\net48\`), restart Alphacam.
5. Restart your MCP client (e.g. Claude Desktop) if you changed `tool-definitions.js` (the Node MCP server pre-validates against the schemas at startup).

## Deployment

### Alphacam Add-In Installation

Alphacam looks in its `DotNetAdd-Ins\` subdirectory (under the Alphacam install path, typically pointed to by `%LICOMDIR%`) for folders containing `.acamaddin` manifest files. The folder name does **not** need to match the assembly name (folder is `AlphacamBridge\`, assembly is `CambotAlphacam`).

**Layout:**
```
%LICOMDIR%\DotNetAdd-Ins\AlphacamBridge\
    CambotAlphacam.acamaddin     ← manifest
    CambotAlphacam.dll           ← the shim assembly
    Interop.AlphaCAMMill.dll     ← COM interop assembly (from Alphacam SDK)
```

**Folder is in Program Files: writes require elevated PowerShell.** Read access works from a normal user.

#### `.acamaddin` manifest — gotchas confirmed in 2026-05-28 session

The manifest format is finicky. All of these will silently break it (Alphacam ignores the addin with no error message in any visible log):

- **LF-only line endings.** The file must use Windows CRLF (`0x0D 0x0A`). A bash heredoc with `sed 's/$/\r/'` over a Cowork mount can produce mixed line endings (most lines CRLF, last line just CR) — also silently rejected. The only reliable way is to write the file natively in PowerShell.
- **UTF-8 with BOM, UTF-16, or any non-ASCII char.** Encoding must be plain ASCII (or strict Windows-1252 with no high bytes). The canonical checked-in manifest lives at `deploy\CambotAlphacam.acamaddin` and was produced (originally) by `CreateAcamAddin.exe` with `File.WriteAllText(..., Encoding.ASCII)` and `StringBuilder.AppendLine()` (CRLF). Treat that file as the source of truth and don't regenerate it.
- **`$2 = 1` (always-load) is NOT a reliable mode.** In testing, with `$2=1` Alphacam silently failed to load the addin and it did not appear in Utils > Add-Ins at all. With `$2=0` (the sample default) the addin appears in Utils > Add-Ins and loads after the user checks the box. Use `$2=0`.
- **Don't hand-author the manifest.** `Deploy-Shim.ps1` byte-copies `deploy\CambotAlphacam.acamaddin` straight into the install folder. No string substitution, no template-and-replace. Earlier versions of the script did string-replace from a build output of `AlphacamAddinSample`; that dependency was removed 2026-05-29 because the sample's bin/Debug folder isn't always present and the swap-then-write step was brittle.

`Deploy-Shim.ps1` (run from elevated PowerShell with Alphacam closed) handles all of this — it auto-stages the freshly-built DLL from `bin\Debug\net48\` into `deploy\` and then copies DLL + PDB + manifest to Program Files. The condensed essentials of what it does:

```powershell
$dest       = 'C:\Program Files\Hexagon\ALPHACAM 2025\DotNetAdd-Ins\AlphacamBridge'
$stageDll   = Join-Path $PSScriptRoot 'deploy\CambotAlphacam.dll'
$stagePdb   = Join-Path $PSScriptRoot 'deploy\CambotAlphacam.pdb'
$stageAddin = Join-Path $PSScriptRoot 'deploy\CambotAlphacam.acamaddin'

# Manifest: byte-for-byte copy of the canonical deploy\ manifest.
$bytes = [System.IO.File]::ReadAllBytes($stageAddin)
[System.IO.File]::WriteAllBytes((Join-Path $dest 'CambotAlphacam.acamaddin'), $bytes)

# DLL and PDB
Copy-Item $stageDll (Join-Path $dest 'CambotAlphacam.dll') -Force
Copy-Item $stagePdb (Join-Path $dest 'CambotAlphacam.pdb') -Force
```

#### First-run user step

After deploying the manifest and DLL with `$2=0`, the user must:

1. Start Alphacam.
2. Open Utils > Add-Ins.
3. Check the box next to `CambotAlphacam`.
4. Click OK and restart Alphacam if prompted.

Once enabled, Alphacam persists the per-user enable state in the registry — subsequent restarts auto-load it. If the addin is replaced with a new version that has a different filename, the registry entry resets and the user must enable it again.

### Launcher Installation

The Launcher (`AlphacamBridge.Launcher.exe` and dependencies) should be placed in one of:
1. Same directory as the shim DLL (in `DotNetAdd-Ins\AlphacamBridge\`)
2. `%LOCALAPPDATA%\Programs\AlphacamBridge\`
3. Dev build output: `AlphacamBridge.Launcher\bin\{Debug|Release}\net8.0-windows\`

The shim's `FindLauncher()` checks these paths in order.

**CRITICAL: After rebuilding, you must deploy to the location the shim actually uses.** Check `%LOCALAPPDATA%\AlphacamBridge\shim.log` for the "Starting Launcher:" line to see which path is active. The installed copy at `%LOCALAPPDATA%\Programs\AlphacamBridge\` takes precedence over the dev build output. To deploy after building:

```powershell
Stop-Process -Name "AlphacamBridge.Launcher" -Force
Copy-Item "src\AlphacamBridge.Launcher\bin\Debug\net8.0-windows\*" "$env:LOCALAPPDATA\Programs\AlphacamBridge\" -Recurse -Force
```

Then restart Alphacam (or let the shim relaunch it).

### Configuration (`bridge-config.json`)

Located next to `AlphacamBridge.Launcher.exe`. **The file next to the exe takes precedence** over `%LOCALAPPDATA%\AlphacamBridge\bridge-config.json`.

**CRITICAL:** The config uses **flat** property names, not nested objects:
```json
{
  "pipeName": "AlphacamBridge",
  "authenticationEnabled": false,
  "maxMessageSizeBytes": 10485760,
  "authTimeoutSeconds": 30,
  "logLevel": "Debug",
  "allowedPaths": {
    "read": ["C:\\Alphacam"],
    "write": ["C:\\Temp"],
    "denied": []
  }
}
```

**Wrong** (will silently default to auth enabled):
```json
{ "Auth": { "Enabled": false } }
```

### MCP Client Config

Any MCP client that can spawn a stdio server works. For Claude Desktop, in `%APPDATA%\Claude\claude_desktop_config.json`, add under `mcpServers` (other clients take the same command/args in their own config format):
```json
{
  "alphacam": {
    "command": "node",
    "args": [
      "C:\\path\\to\\alphacam-mcp-server\\src\\index.js",
      "--pipe", "AlphacamBridge",
      "--key", "anonymous"
    ]
  }
}
```

## Known Gotchas

1. **`.acamaddin` encoding** — Must be plain ASCII with strict CRLF line endings, no BOM. Use `deploy\CambotAlphacam.acamaddin` byte-for-byte as the source of truth (Deploy-Shim.ps1 does this automatically). Do not write from bash/sed even with CRLF conversion. See the manifest-gotchas section above.
2. **`.acamaddin` `$2` value** — Must be `0`. The "always load" mode (`$2=1`) caused the addin to silently fail discovery in our environment. With `$2=0` it appears in Utils > Add-Ins for the user to enable.
3. **`InitAlphacamAddIn` event** — Must subscribe and set `Data.ReturnCode = 0`. Without this, Alphacam garbage-collects the add-in almost immediately.
4. **ROT registration** — Uses a custom CLSID, not a ProgID. Both shim and Launcher must agree on `F47AC10B-58CC-4372-A567-0E02B2C3D479`.
5. **`toBridgeMethod` must use string replace** — `'_'` not `/_/g`. Only the first underscore separates category from method name.
6. **Config format is flat** — `"authenticationEnabled"` not `"Auth.Enabled"`.
7. **Config file precedence** — File next to Launcher.exe wins over AppData location.
8. **COM property names** — `.FullName` not `.FileName`, `.GetGeoCount()` not `.GeometryCount`. Always verify against the interop assembly or CHM docs.
9. **All COM calls must be STA-marshalled** — Always use `_dispatcher.InvokeAsync(...)` in the Launcher, or `Control.Invoke` in the shim's ComCommandServer.
10. **PowerShell `ConvertTo-Json` can corrupt the MCP client's config (e.g. Claude Desktop's)** — Edit the JSON file directly or use a different tool.
11. **Alphacam process name is `Acam.exe`** — Not `Mill`, `Lathe`, `Router`, etc. A deploy script that checks for blocking processes by name should look for `Acam`, or better, enumerate `Get-Process | Where { $_.Modules.FileName -like 'C:\Program Files\Hexagon\ALPHACAM*' }` and reject if any are found.
12. **The shim DLL is locked while loaded.** Even after closing Alphacam, the launching PowerShell session can hold it via `Assembly.LoadFrom`. If a deploy reports "copy silently skipped (size mismatch)", start a fresh PowerShell session and retry.
13. **`alphacam_status.Connected` is lazy.** It reports the shim-pipe state, which is `false` until the first request triggers a connection. As of 2026-05-29 `status` also reports `Module` (read cross-process via the ROT proxy, doesn't depend on the shim pipe), so the *module letter* is reliable from a fresh start but `Connected` still isn't. For pipe-readiness, call an actual method (e.g. `alphacam_new`).
14. **PowerShell scripts must be plain ASCII.** Em-dashes, smart quotes, or any UTF-8 multi-byte character causes the PS parser to fail with `TerminatorExpectedAtEndOfString` because `.ps1` files are read as Windows-1252 by default.
15. **PowerShell `@(@(a,b), @(c,d))` flattens.** Don't try to build "an array of pairs" this way — it becomes `@(a,b,c,d)`. Use hashtables, custom objects, or inline calls instead.
16. **Cowork mount writes don't always propagate immediately.** A file just written through the mount may still show old content when read directly from Windows seconds later. Verify with `Get-FileHash` or paste a one-liner directly into PowerShell instead of saving to a `.ps1` file when iterating quickly.
17. **Cowork mount reads can be stale.** `tail` and `ls` over the mount may return cached data. Re-mounting the folder usually helps but isn't always sufficient — `Grep` and `Read` with explicit `offset` can bypass the cache better than `tail`.
18. **The shim's ComCommandServer uses `JavaScriptSerializer`,** which lives in `System.Web.Extensions.dll`. The reference is added in `AlphacamBridge.Shim.csproj` and resolves correctly under Alphacam's .NET 4.8 AppDomain — verified by `Assembly.LoadFrom` and successful end-to-end circle draw.
19. **The Launcher pipe is single-client.** `PipeServer` opens with `maxNumberOfServerInstances=1`. Two MCP client sessions targeting the same bridge will fight — the second sees "Connection timeout" or "Socket closed before connection established." Restart the client to release the stale connection. To support multiple sessions, bump max instances in `PipeServer.cs` and add per-client session tracking.
20. **MCP tool-definition edits require an MCP client restart.** The Node MCP server in `alphacam-mcp-server/src/index.js` pre-validates against the JSON schemas at startup. In-flight edits to `tool-definitions.js` don't apply until the server respawns — which means restarting the client (e.g. Claude Desktop; it owns the process).
21. **`Deploy-Shim.ps1` sources the manifest from `deploy\CambotAlphacam.acamaddin`.** It used to do a string-replace from `AlphacamAddinSample\AlphacamAddinSample\bin\Debug\AlphacamAddinSample.acamaddin`. That dependency was removed 2026-05-29 (the sample's build output isn't always present and the swap-then-write step was brittle). The DLL is auto-staged from `bin\Debug\net48\` when newer than `deploy\CambotAlphacam.dll`.
22. **`App.LicomdirPath` casing on this install does not match the on-disk path.** `App.LicomdirPath` returns `C:\ALPHACAM\` (uppercase), but the actual folder hosting the post processor is `C:\Alphacam\LICOMDAT\...` (mixed case). `Path.Combine` preserves the wrong root and the default `select_post` open fails with `Error Opening Post`. Workaround: pass `fileName` explicitly when calling `alphacam_select_post`. Real fix: case-canonicalize via `Path.GetFullPath` + filesystem probe, or probe `LicomdatPath` as an alternate base. Tracked in HANDOFF.md TODO.
23. **Cowork mount can present a desynced file view between Edit/Write and bash.** Symptom: the Edit tool reports successful writes and `Read` shows full content, but `wc -l` / `cat` over the bash mount shows an older truncated copy (sometimes with stale mtime). Build against the bash-visible file fails on a mid-block syntax error. Fix that works: `mcp__cowork__allow_cowork_file_delete` to grant delete, then `rm` and re-create the file via a single `cat > file << 'EOF' ... EOF` bash heredoc. After that all three views (bash, Edit-tool, Windows) agree. This came up writing `ComCommandServer.cs` on 2026-05-31.
24. **The Linux sandbox can build .NET 4.8 targets.** `dotnet 8.0.421` with `<TargetFramework>net48</TargetFramework>` builds `CambotAlphacam.dll` cleanly on Linux; the IL is portable to Windows .NET Framework 4.8. Reference assemblies for net48 come from the `Microsoft.NETFramework.ReferenceAssemblies.net48` package picked up via NuGet. Useful for fast iteration in a sandboxed setup; final verification should still happen on Windows.
25. **Enum values are discoverable from `Interop.AlphaCAMMill.dll` directly.** When the CHM comment for a property says "X = 0, Y = 1, Z = 2" but you need to confirm — or when an enum like `AcamToolInOut` has a negative value (`acamOUTSIDE = -1`) that VBA syntax hides — decode the metadata. Quickest path: `pip install dnfile`, then walk `pe.net.mdtables.TypeDef` → `FieldList_Index` range → `pe.net.mdtables.Constant` per field. Used 2026-05-31 to verify the AcamToolInOut/AcamToolSide/AcamPocketType/AcamComp values that the shim now bakes in as `(short)` literals.
26. **Closing the Alphacam UI does NOT terminate `Acam.exe`.** The shim's `ComCommandServer` is still listening, the Launcher's `ShimPipeClient` is still connected, and the ROT entry still holds an IUnknown ref to `IAlphaCamApp`. The process lingers as an invisible orphan. If the user then starts a new Alphacam window, that window's shim copy can't claim the ROT entry, so bridge calls keep going to the invisible orphan — the visible window appears frozen/blank no matter what tools are called. Recovery: kill both processes (`Stop-Process -Name Acam,AlphacamBridge.Launcher -Force`) and restart Alphacam. Surfaced 2026-06-30 during the post-pocket smoke test. **Fix implemented 2026-07-15 evening (pending live verification):** the shim subscribes `AddInInterface.BeforeExit`/`BeforeUnload` (both parameterless — there is no `QuitApplication` event; names confirmed by decoding the interop) and disposes the ROT registration + command server; the Launcher runs a 15 s `Acam.exe` process watchdog and exits once Alphacam disappears (after having been seen running).
27. **Leads are added by `Path.SetLeadInOutAuto`, NOT by `MillData.SetLeadData` before the operation.** Setting `LeadData.LeadIn`/`LengthIn`/`AngleIn`/`SlopeIn` on the MillData and then calling `md.RoughFinish()` produces no visible lead-in in the G-code — the tool rapids straight to depth. The correct pattern is to capture the `Paths` collection returned by `md.RoughFinish()` (or `md.Pocket()`) and call `Path.SetLeadInOutAuto(LeadIn, LeadOut, LengthMultiplier, RadiusMultiplier, Angle, SlopeIn, SlopeOut, Overlap)` on each toolpath. `LengthMultiplier` and `RadiusMultiplier` are **in tool radii** (matches the Alphacam UI convention, e.g. `LengthMultiplier=4` with a 0.5″ tool = 1″ XY lead). `SlopeIn` is a **boolean** — the CHM says "True if lead-in is to be sloping"; the actual ramp angle is auto-derived from `SafeRapidLevel` → `FinalDepth` over the XY LengthMul. Signature decoded from `dnfile` on `Interop.AlphaCAMMill.dll` 2026-07-06.
28. **`LicomdirPath` case-mismatch on some installs — use `App.PostFileName` directory as the reliable base.** On this user's install, `(string)App.LicomdirPath` returns `C:\ALPHACAM\` (all caps, no `LICOMDIR` subfolder), but the real files live under `C:\Alphacam\LICOMDIR\...`. Anything auto-computing paths from LicomdirPath (like `select_post`'s default post file, or `output_nc`'s default INI path) fails with a file-not-found. Workaround adopted 2026-07-06 in `output_nc`: probe `Path.GetDirectoryName((string)App.PostFileName) + "XilogPost.ini"` first — since `select_post` has already validated that path, its directory is the most reliable place to look for peer files. `select_post`'s own default still needs the same treatment (TODO).
29. **INI files can accumulate hundreds of duplicate keys.** The user's XilogPost.ini `[Pratix Z2]` section had ~200 duplicate `sV=10` lines, presumably from repeated automated writes. `PatchIniSection` in the shim reads all lines, updates every occurrence of a target key within the section (not just the first), and appends missing keys at the section boundary — preserving everything else. Callers can also opportunistically dedupe an INI by rewriting it: read line-by-line, track current section, keep the LAST occurrence per key per section (standard INI parser semantics).
30. **Mount corruption can also DUPLICATE a file's tail, not just truncate it.** Found 2026-07-15: `ComCommandServer.cs` on disk had 16 junk lines appended after the final closing brace (fragments of the last two methods repeated) — the file did not compile. Presumably a partial re-write through the Cowork mount from an earlier session. The deployed DLL predated the corruption so the running bridge was unaffected. Recovery: truncate at the real end-of-namespace and rebuild. Sanity check after any big mount write: `dotnet build`, or at minimum `tail -20` looking for code after the final `}`.
31. **AcamToolType is NOT zero-based at SQUARE.** Decoded from the interop 2026-07-15: 0=TURNING, 1=SQUARE, 2=BULL, 3=BALL, 4=DRILL, 5=TAP, 6=USER, 7=WHEEL. An earlier tool-definitions schema claimed 0=SQUARE…4=TAP (off by one) — corrected. Related enums (also decoded): AcamDrillType 0=DRILL/1=PECK/2=TAP/3=BORE/4=CHAMFER (no HELICAL/REAM); AcamEngraveType 0=GEOMETRIES/1=GUIDE_LINES_APPROX/2=GUIDE_LINES_EXACT/3=SIMPLE_EXACT; AcamCorners 0=ROUND/1=STRAIGHT/2=LOOP; AcamHoles 0=CIRCLES/1=CROSSES/2=MATCH_DRILL/3=GIVEN_DIA; AcamProcessType DRILL=21/PECK=22/TAP=23/BORE=24/ENGRAVE=10.
32. **Helical descent params on `rough_finish`.** `helical: true` sets `MillData.HelicalInterpolation = true`. `helicalPitch` sets `MillData.MaxDepthPerCut` = **Z drop per revolution** (verified 2026-07-15 on a 0.5″ tool: pitch=0.1 → 5 revolutions to descend 0.5″; pitch=0.5 → 1 revolution to descend 0.5″). Same-arc Z-descent in the G-code (G2/G3 with declining Z per arc, not flat groups) is the confirming signature. `helicalPitch` defaults to the current tool's Diameter via `App.GetCurrentTool().Diameter`, with a hard fallback of 0.1 if no tool is selected. Explicit `helicalPitch` always wins. Helical is a rough/finish concept — not exposed on `pocket` (and shouldn't be; pockets have their own entry strategy).

## API Reference System

The ACAMAPI.chm has been decompiled and parsed into structured JSON at `alphacam-mcp-server/data/acamapi-reference.json`. This covers 57 COM objects, ~648 methods, ~722 properties, 5 enums, and 44 events. It is generated locally from the user's own ACAMAPI.chm by `tools/generate-api-reference.js` (not distributed — it derives from Hexagon's documentation).

The MCP server exposes an `alphacam_api_help` tool that queries this reference locally (no bridge call). The agent can use it to look up:
- Object overviews: `"Drawing"`, `"MillData"`, `"MillTool"`
- Specific members: `"Drawing.CreateCircle"`, `"MillData.AutoZ"`
- Keyword search: `"pocket"`, `"stepover"`, `"lead-in"`

This is the tiered approach: tool descriptions contain enough context for normal use, and `alphacam_api_help` provides deep documentation on demand.

Additional CHM files from the ALPHACAM installation (AEDITAPI.chm, ConstraintsAPI.chm, Feature.chm, etc.) cover supplementary APIs not yet parsed.

## Generic Full-API Layer

Beyond the curated tools, three tools give 100% coverage of the automation API via late-bound COM (`Type.InvokeMember` → IDispatch) in the shim:

- **`alphacam_api_invoke`** (`alphacam.api_invoke`) — call any method / get or set any property on `"app"`, `"drawing"`, or any cached `obj_N` id. Args pass through as VARIANTs; object-valued args are written `{"$id": "obj_N"}`. `byRef: [indices]` marks VB-style output parameters (via `ParameterModifier`); their post-call values return in `byRefValues`. `kind: "auto"` (default) sends `DISPATCH_METHOD|DISPATCH_PROPERTYGET`, which is the standard late-binding trick for COM servers that mix the two.
- **`alphacam_api_inspect`** (`alphacam.api_inspect`) — runtime COM type name (via `Microsoft.VisualBasic.Information.TypeName`, i.e. ITypeInfo) plus method/property name lists reflected from the interop assembly. With no target: lists every live handle.
- **`alphacam_api_release`** (`alphacam.api_release`) — drops cached handles (`RemoveFromCache`), releasing the `GetIUnknownForObject` ref the dedupe map holds.

Returned COM objects are stored through the same `StoreObject` cache the curated tools use, so ids flow freely between the two layers (a `geometryId` from `alphacam_create_circle` is a valid `api_invoke` target and vice versa). Error mapping: `DISP_E_UNKNOWNNAME` → "not a member" hint, `DISP_E_BADPARAMCOUNT`/`TYPEMISMATCH` → signature hint pointing at `alphacam_api_help`.

Discovery loop for the agent: `alphacam_api_help` (docs, signatures, enums) → `alphacam_api_invoke` (execute) → `alphacam_api_inspect` (orient among returned handles).

## Current Status (as of 2026-07-15 evening)

**Working end-to-end:**
- Shim loads in Alphacam (after user enables the addin once in Utils > Add-Ins), registers IAlphaCamApp in ROT, launches Launcher.
- Shim's `ComCommandServer` listens on pipe `AlphacamBridgeCmd` and marshals COM calls to the UI thread via `Control.Invoke`. All `alphacam.*` mutations go through here.
- Launcher (.NET 8) connects to Alphacam via ROT for read-only ops (`alphacam.status` reads `App.ProgramLetter` for `Module`; `alphacam.diag` exercises read/mutate cross-process). All other dispatch forwards to the shim pipe.
- Node.js MCP server connects to Launcher pipe `AlphacamBridge`, exposes the tool catalogue to the MCP client.
- Tool descriptions enriched with ACAMAPI.chm documentation; API reference JSON generated and served by MCP server. `alphacam_api_help` works locally without bridge.

**Verified end-to-end through Claude Desktop (the protocol layer is additionally validated with the reference MCP SDK client in `src/__tests__/generic-client.test.js`, so any MCP client can drive it):**
- File / view: `status`, `new`, `get_drawing_info`, `zoom_all`, `redraw`, `save_as` (.amd/.ard), `save_drawing`, `open_drawing` (round-trip), `import_cad` (DXF), `output_nc` (3-arg COM call; `OutputNCStatus` surfaced), `list_nc`.
- Geometry: `create_rectangle`, `create_circle`, `create_line`, `create_polygon`, `create_text`, `create_ellipse`, `create_2d_geometry`, `list_geometries` (IUnknown-deduped), `get_geometry_info` (real metadata: layer, elementCount, extents, length, closed).
- Post / Tool / Machining: `select_post` (default XilogPlus.arb — see caveat under Known Gotchas #22), `list_tools` (**recursive** folder scan as of 2026-07-06 — auto-discovers user-created tool subfolders), `select_tool` (`$USER` for picker), `get_current_tool`, `rough_finish` (operator-level params + full lead-in surface via `Path.SetLeadInOutAuto`; verified with ramped and asymmetric leads; **helical descent verified 2026-07-15** — `helicalPitch` defaults to the current tool's Diameter), `pocket` (closed-boundary area clearance; PocketType contour/linear/spiral; WidthOfCut stepover defaults to 50% of tool diameter — pocket lead-in still uses MillData.LeadData and doesn't visibly produce leads; deferred).
- Material / Direction / Iteration: `specify_panel` (Path.SetPanel + tracks helper geometries), `specify_material` (Path.SetMaterial mirror), `set_material` (Drawing.SelectMaterial for material type), `set_tool_side` (Path.ToolInOut / Path.ToolSide; inside/outside/center/left/right), `reverse_geometry` (Path.Reverse or explicit Path.CW), `set_start_point` (Path.SetStartPoint with rule-based positioning: `middleOfLongestEdge`, corner names, `nearestToPoint`, explicit).
- Deletion (all landed 2026-07-06): `delete_geometry`, `delete_toolpath` (by id/index/all), `delete_panel`, `delete_material` (target=geo|type). All cascade-delete tracked panel/material auto-helpers via `_designationHelpers`.
- NC Output: `output_nc` defaults to **unattended mode** — patches XilogPost.ini `[Pratix Z2]` with `DisplayScreen=0` + material dims from last `specify_panel` + fixed zone/vac defaults, runs OutputNC (no operator dialog), then restores `DisplayScreen=1`. INI path derived from `App.PostFileName` directory (dodges the LicomdirPath case-mismatch bug). Set `unattended: false` to keep the classic dialog flow.

**Architecture note:** Mutating COM calls (`New`, `CreateCircle`, etc.) are dispatched through the shim's pipe because they fail cross-process with `RPC_E_SERVERFAULT (0x80010105)`. Read-only calls (`FullName`, `GetGeoCount`, `ProgramLetter`, ...) work cross-process through the Launcher's ROT proxy connection.

**Implemented 2026-07-15 evening but NOT yet deployed or live-verified** (compile-verified on Linux only — see HANDOFF.md's 2026-07-15 evening session log for the deploy/verify checklist):
- Tool management: `create_tool` (with correct AcamToolType mapping — see gotcha #31), `save_tool`
- Machining: `drill_tap` (AcamDrillType 0-4, peck/tap params), `engrave` (EngraveType=GEOMETRIES, corner control, leads), `manual_toolpath` (first move = start point; Add3DRapid/Add3DLine/Add3DArcPointCenter; i/j arc centers relative to previous point)
- Layer/workplane/operations: `list_layers`, `manage_layer` (create/rename/delete/set_visible/set_active/set_current/set_color), `list_operations`, `get_operation_info`, `manage_workplane` (list/create/set/set_origin/cancel)
- Pocket lead-in: fixed — pocket now captures the Paths returned by `md.Pocket()` and calls `Path.SetLeadInOutAuto` on each (rough_finish pattern). The old `ApplyLeadDataFromParams`/`MillData.SetLeadData` path was deleted.
- `select_post` default path: fixed via `ProbeDefaultPostPath()` (probes active-post dir, LicomdirPath, LicomdirPath\LICOMDIR, parent(LicomdatPath)\LICOMDIR; first existing file wins).

`ComCommandServer.DispatchMethod` now has 62 dispatch cases (59 curated + the 3 generic API commands). New helpers: `FindLayerByName`, `ResolveWorkPlane`, `ProbeDefaultPostPath`.

**Removed 2026-05-29 (dead code cleanup):**
- `src\AlphacamBridge.ComInterop\Handlers\` (4 IToolHandler classes) — all dispatch routes through the shim pipe now.
- `src\AlphacamBridge.Core\Tools\` (`ToolDispatcher`, `ToolRegistry`, `ToolDefinition`) — only existed to wire the dead handlers.
- `src\AlphacamBridge.Core\ObjectCache\ObjectStore.cs` — Launcher-side cache that nothing populated.
- ~80 LOC of dead wiring + the fallback dispatch block + the `alphacam.list_tools` special case in `Program.cs`.
- `PathValidator.cs` was kept (small surface, future security work).

**Known constraints:**
- `PipeServer` is single-client (`NamedPipeServerStream(..., 1, ...)`). Two MCP client sessions against the same bridge will conflict — restart the client to release. Fix: bump max instances and add per-client session tracking.
- MCP tool-definition edits require an MCP client restart to take effect (the Node MCP server pre-validates against schemas at startup).

**TODO:**
- Live-verify the generic full-API layer (`api_invoke`/`api_inspect`/`api_release`) against a running ALPHACAM after the next deploy (rebuild shim, `Deploy-Shim.ps1`, restart the MCP client).
- Audit existing shim handlers against ACAMAPI.chm for correct COM property/method names (use `alphacam_api_help` queries).
- Consider parsing additional CHM files (AEDITAPI, ConstraintsAPI) if those APIs are needed.
- If multi-session use is wanted, expand the Launcher pipe to multiple instances.
