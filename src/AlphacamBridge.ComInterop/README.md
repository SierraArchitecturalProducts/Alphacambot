# AlphacamBridge.ComInterop

Managed COM interop layer used by the .NET 8 Launcher. Wraps `IAlphaCamApp` with proper STA marshalling for the small set of operations the Launcher still performs in-process — primarily `alphacam.diag` (cross-process COM smoke tests) and ROT-based connection management. **All `alphacam.*` tool dispatch routes through the in-process shim's `ComCommandServer` named pipe**, not through this project.

## Components

**StaThreadDispatcher.cs** — Dedicated STA thread for COM calls. Provides `InvokeAsync<T>` / `InvokeAsync` with timeout management. Required because the Launcher's pipe server callbacks arrive on async I/O threads, but COM objects are apartment-bound.

**AlphacamConnection.cs** — Manages connection to Alphacam's `IAlphaCamApp` via the COM Running Object Table (ROT) using the custom CLSID `F47AC10B-58CC-4372-A567-0E02B2C3D479`. Exposes `Application` and raises `ConnectionStateChanged`. **Read-only COM calls work cross-process; mutations do not.** The shim is the canonical mutation path.

**AlphacamEnums.cs** — Managed mirrors of the AcamFile/AcamLead/AcamProcess/etc. COM enums. Currently kept as reference even though enum values are also accessible from the shim side via `Interop.AlphaCAMMill.dll`.

**ShimPipeClient.cs** — JSON-RPC 2.0 client for the shim's command pipe (`AlphacamBridgeCmd`). Same 4-byte big-endian length-prefix framing as the main pipe. Owned by the Launcher's request handler.

## Dependencies

- AlphacamBridge.Core (JsonRpc message types, configuration)
- Microsoft.Extensions.Logging
- .NET 8.0 Windows runtime

## History

Previously this project hosted four `IToolHandler` implementations (`FileOperationHandler`, `GeometryHandler`, `ToolManagementHandler`, `MachiningHandler`) that ran COM mutations cross-process from the Launcher. Those failed with `RPC_E_SERVERFAULT (0x80010105)` for any mutation, which is why the in-process shim's `ComCommandServer` was introduced. The handler classes have been deleted (2026-05-28) because they were dead code after that pivot. See `HANDOFF.md` for the rationale.
