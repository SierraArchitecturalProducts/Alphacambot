# AlphaCambot

**Drive Hexagon ALPHACAM® with plain English — from any AI agent that speaks MCP.**

> ## WARNING — this software drives real machinery
>
> AlphaCambot generates toolpaths and NC programs that run on CNC machines. An error —
> in this software, in the AI agent driving it, in the post processor, or in what you
> asked for — can cause **fire, injury, death, and destruction of machines, tooling,
> fixtures, and material**. Spindles, rapids, and plunges do not care that the program
> came from a conversation.
>
> **Nothing produced by this software is guaranteed correct. Before any program reaches a
> machine:**
>
> 1. **Inspect the toolpaths in ALPHACAM** — depths, rapid heights, lead-ins, tool side,
>    order of operations.
> 2. **Run ALPHACAM's solid simulation** and look at the result (the `alphacam_simulate`
>    tool compares it against the model, but it is a 3-axis top-down check, not proof).
> 3. **Verify the G-code itself** — post processor, tool numbers and offsets, work
>    offsets, material size and position, spindle and feed values, safe Z.
> 4. **Prove it out on the machine** with its own safeguards: dry run / air cut above the
>    part, single block, feed and rapid override, hand on the stop.
>
> You, the operator, are solely responsible for everything that runs on your machine.
> This software is provided "as is", without warranty of any kind (see [LICENSE](LICENSE)).

AlphaCambot is an open-source [Model Context Protocol](https://modelcontextprotocol.io) (MCP)
server for ALPHACAM CAD/CAM software. Any MCP-capable LLM agent — the
[Claude Desktop app](https://claude.ai/download), or any other MCP client — can use it to
draw geometry, apply machining, run true-shape nesting, and post NC programs from a
plain-English conversation:

> "Draw a 24x12 panel, drill ten 8 mm holes along the bottom, profile the outside
> with a ramped lead-in in two passes, and post the G-code."

It exposes **69 tools** covering:

- **Drawing & CAD editing** — rectangles, circles, polygons, editable text objects and
  text-as-geometry, arbitrary 2D paths; move/rotate/mirror/scale (with array patterns),
  offset, fillet/chamfer, join, explode
- **Geometry lint** — one call finds and fixes NC-hostile geometry (huge-radius arcs,
  micro elements, gaps, self-intersecting hooks, stacked duplicates) before it reaches
  a part file, a nest or the machine
- **Machining** — profile (rough/finish with helical descent, automatic lead-in/out,
  sloping/profiled sides and auto-Z levels), pocketing, drilling/tapping (batch),
  engraving, manual toolpaths
- **3D machining** — import an STL/STEP/Parasolid model as a solid, then Z-contour
  roughing, parallel/raster, waterline (Z-contour) finishing, steep/shallow, projected
  contours, radial, spiral, rest machining and drive-curve strategies with boundaries,
  stock models and lead/link control — with plan checks on every operation and a
  **simulate-and-compare** step that measures material left / gouges against the
  model (normal to the surface) before anything is posted
- **Long jobs** — dense 3D machining, simulation, posting, imports and nests can run
  in the background and be polled, so no client timeout is hit
- **Setup** — tools, posts, panels/materials, layers, work planes, tool sides, start points
- **Nesting** — parts from your drawing saved to part files, generated nest lists
  (quantities, rotation rules, gaps, sheet-edge gap), sheets taken from drawing geometry,
  multi-sheet nests, nested toolpath ordering, per-sheet NC output
- **Interop** — DXF/STEP/IGES/STL/Parasolid/SolidWorks import, DXF/EMF/STL/IGES export, drawing queries
- **Human-in-the-loop** — act on your current ALPHACAM selection, or have ALPHACAM
  prompt you to pick geometry mid-conversation
- **The entire automation API** — beyond the curated tools above, a generic invoke
  layer reaches every method and property the ALPHACAM API exposes (see below)

**[See TOOLS.md](TOOLS.md) for a plain-English reference to every tool**

## How it works

```
MCP client (Claude Desktop, or any MCP-capable agent)
   |  stdio (Model Context Protocol)
Node.js MCP server            src/alphacam-mcp-server
   |  named pipe "AlphacamBridge" (JSON-RPC 2.0)
Launcher (.NET 8)             src/AlphacamBridge + AlphacamBridge.Launcher
   |  named pipe "AlphacamBridgeCmd" (JSON-RPC 2.0)
In-process add-in (.NET 4.8)  src/AlphacamBridge.Shim  ("CambotAlphacam.dll")
   |  COM automation, marshalled to the UI thread
ALPHACAM
```

ALPHACAM's COM server rejects mutating calls from outside its process, so the shim
runs inside ALPHACAM as a .NET add-in and executes commands on the UI thread; the
out-of-process Launcher handles transport, and the Node server speaks standard MCP
to whatever client is attached. The bridge survives ALPHACAM restarts, cleans up
after itself on exit, and heals its own processes if one goes away.

## Full API coverage

The curated tools are the tested fast path for common workflows. Three generic
tools cover the rest of the automation API — 57 COM objects, ~650 methods,
~720 properties — via late-bound COM:

- `alphacam_api_invoke` — call any method, or get/set any property, on the
  Application, the active drawing, or any object handle returned by earlier calls
  (including ByRef output parameters)
- `alphacam_api_inspect` — see what an object handle is and what members it has
- `alphacam_api_release` — free handles after long sessions

`alphacam_api_invoke` also takes a `calls` array to run a sequence of API calls in
one round-trip (one permission prompt in Claude Desktop), with `{"$ref": i}` to feed
one call's returned object into a later one.

Paired with `alphacam_api_help` (searchable documentation generated from your own
`ACAMAPI.chm`), the agent can discover a member, read its signature, and call it —
anything the API has, it can do.

## Remembering your shop's setup

Every shop keeps its tool catalog in a different place, uses a different post,
and works in different units. So the agent doesn't need reminding each session,
the server keeps a small per-user preferences file at
`%LOCALAPPDATA%\AlphacamBridge\preferences.json` that any connecting MCP agent
can read and update:

- `alphacam_get_preferences` — read the saved setup (tool-catalog folder,
  preferred routing tool, post `.arb`, NC output directory, units, material
  defaults). The same summary is folded into `alphacam_status`, so an agent
  sees your setup on its first call.
- `alphacam_set_preference` — persist what you tell it: *"my tools are in
  C:\…\Canavan Tools"*, *"default to millimetres"*, and so on.

This also sidesteps a subtle NC gotcha: some posts (e.g. Xilog) reserve certain
tool **numbers** for drills, so routing with an ad-hoc tool in that range makes
the post refuse the job. Point the agent at your real catalog once, and it uses
tools whose numbering your post already expects.

## Works with any MCP client

The server is built on the official `@modelcontextprotocol/sdk` and speaks plain
MCP over stdio — nothing in it is specific to one client. The test suite drives it
with the reference MCP SDK client (`npm test` in `src/alphacam-mcp-server`) to keep
it that way. Any client that can spawn a stdio server uses the same command:

```json
{
  "command": "node",
  "args": ["<repo>/src/alphacam-mcp-server/src/index.js", "--pipe", "AlphacamBridge", "--key", "anonymous"]
}
```

`Setup.ps1`/`Install.ps1` register it in Claude Desktop's config automatically when
present; for other clients (or agent frameworks with MCP support), add the command
above in that client's own config format.

## Requirements

- Windows with **ALPHACAM** (developed and tested against ALPHACAM 2025, Router and Mill)
- **An MCP client** — e.g. the Claude Desktop app (any plan; local MCP servers are
  configured via `claude_desktop_config.json`), or any other MCP-capable agent
- **Node.js** LTS
- **.NET SDK 8+** (to build) and the **.NET 8 Desktop Runtime** (to run)
- .NET Framework 4.8 (present on modern Windows)

## Quick start

From the repository root, in a PowerShell opened **as Administrator**
(with ALPHACAM closed):

```powershell
.\Setup.ps1
```

That one script checks prerequisites, copies the COM interop assembly out of your
ALPHACAM installation, builds the add-in and the Launcher, installs the MCP server
(registering it in Claude Desktop's config if you use Claude Desktop), builds the
optional API-help index from your own `ACAMAPI.chm`, and deploys everything. From a
non-elevated shell it does all of the build/config work and tells you the one
remaining elevated step (`Deploy-Shim.ps1`).

Then start ALPHACAM (the add-in loads automatically; it does not appear in
Utils > Add-Ins), restart your MCP client, and ask the agent:
*"give me alphacam status"*.

Logs live in `%LOCALAPPDATA%\AlphacamBridge\` (`shim.log`, `launcher.log`).

## Two files that come from your own ALPHACAM installation

To respect Hexagon's intellectual property, this repository does **not** include two
files. `Setup.ps1` produces both from your own licensed ALPHACAM installation:

1. **`Interop.AlphaCAMMill.dll`** — the COM interop assembly for ALPHACAM's automation
   API, required to build. `tools\Get-Interop.ps1` finds it in your ALPHACAM install
   and copies it to `src/AlphacamBridge.Shim/PrivateReferences/`. (Despite the "Mill"
   name it is the canonical interop for Mill, Router, and the other modules.)

2. **`acamapi-reference.json`** *(optional)* — an index of ALPHACAM's API help that
   powers the `alphacam_api_help` tool; everything else works without it.
   `tools\generate-api-reference.js` builds it by decompiling your own `ACAMAPI.chm`
   (via Windows' built-in `hh.exe`) and parsing the pages — for your local use only,
   since the content is Hexagon's documentation.

## Manual build and install

If you prefer to run the steps yourself:

```powershell
.\tools\Get-Interop.ps1
dotnet build src\AlphacamBridge.Shim\AlphacamBridge.Shim.csproj -c Debug       # .NET 4.8 add-in
dotnet build src\AlphacamBridge.Launcher\AlphacamBridge.Launcher.csproj -c Debug   # .NET 8 launcher
cd src\alphacam-mcp-server
.\Install.ps1                       # npm install + Claude Desktop config entry
cd ..\..
node tools\generate-api-reference.js   # optional API-help index
.\Deploy-Shim.ps1                   # elevated, ALPHACAM closed
```

`Deploy-Shim.ps1` stages the built add-in into ALPHACAM's `DotNetAdd-Ins\` folder
(the `.acamaddin` manifest must be byte-exact — the script handles this) and copies
the Launcher to `%LOCALAPPDATA%\Programs\AlphacamBridge`. It auto-detects your
ALPHACAM installation; pass `-AlphacamDir` if you have several.

See `docs/DEVELOPER-GUIDE.md` for architecture details, the handler pattern for
adding tools, and a long list of hard-won gotchas.

## Good to know

- The pipe connection drops whenever you interact with ALPHACAM directly — this is
  normal; Claude reconnects and all state survives.
- One agent session at a time can use the bridge.
- Unattended (no-dialog) NC output is tuned for SCM machines with the Xilog Plus post,
  where the post's `.ini` is adjusted around output. With other post processors NC
  output works but the post's normal dialog may appear.
- True-shape nesting is the default engine; rectangular nesting is available as a
  fallback. Nesting runs typically take 30–60 seconds.
- **Always review, simulate, and verify NC programs before running them on a
  machine** — see the warning at the top of this file. This software generates real
  toolpaths for real machinery; you are responsible for every program that runs.

## Trademarks and attribution

- **ALPHACAM®** is a registered trademark of **Hexagon AB** and/or its subsidiaries
  and affiliates. This project is an independent, community-built integration and is
  **not affiliated with, endorsed by, or supported by Hexagon**. You need your own
  licensed copy of ALPHACAM to use it.
- **Claude** and **Anthropic** are trademarks of **Anthropic, PBC**. **Model Context
  Protocol** is an open standard originated by Anthropic. This project is not
  affiliated with or endorsed by Anthropic.
- **Xilog** and related machine/post names are trademarks of **SCM Group S.p.A.**
- **Windows**, **.NET**, and **PowerShell** are trademarks of the **Microsoft** group
  of companies. **Node.js** is a trademark of the **OpenJS Foundation**.
- All other trademarks are the property of their respective owners.

AlphaCambot was built by Sierra Hardware Products, Inc., in collaboration
with Claude.

## License

[MIT](LICENSE) — Copyright (c) 2026 Sierra Hardware Products, Inc.

Provided "as is", without warranty of any kind. CNC machinery can cause fire, injury,
death, and property damage; inspect, simulate, and verify every generated toolpath
and program before use, and prove it out on the machine.
