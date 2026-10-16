# Alphacambot — Tool Reference

A plain-English guide to everything Alphacambot lets an AI agent do inside
ALPHACAM. You don't call these yourself — you ask the agent in normal language
("draw a 300×200 panel, drill six 8 mm holes down the middle, profile the
outside and post it") and it picks the right tools. This page is so you can see
the whole toolbox, understand what each tool does, and know what's possible.

The bridge exposes **73 tools**: 69 for CAD/CAM work, plus four utilities
(connection status, API help, and the two preference tools). They're grouped
below the way you'd actually use them, roughly in the order of a job.

## A few ideas that run through everything

- **Geometry handles.** When the agent draws or finds something, it gets back a
  short id like `obj_3` (often called a *geometryId*). It reuses that id to
  machine, move, or delete exactly that shape — no guessing which circle it
  meant.
- **Your current selection counts.** Many tools can act on whatever *you* have
  selected in the ALPHACAM window, or ask ALPHACAM to prompt you to pick — so
  you and the agent can work on the same drawing together.
- **The agent learns your shop once.** Tool-catalog folder, post processor,
  units and material defaults are saved as preferences (see the last section),
  so you don't re-explain your setup every session.
- **Always check the program before you cut.** These tools generate real
  toolpaths and NC code for real machines. Review every program.

---

## Getting started & status

| Tool | What it does |
|------|--------------|
| `alphacam_status` | Is the bridge connected to ALPHACAM? Also reports your saved preferences. Good first thing to ask: *"give me alphacam status."* |
| `alphacam_api_help` | Looks up ALPHACAM's own API documentation (objects, methods, properties, enums) so the agent can find capabilities beyond the curated tools. |
| `alphacam_get_preferences` | Shows your saved shop setup. |
| `alphacam_set_preference` | Saves a shop setting or a note so the agent remembers it next time. |

## Files & drawings

| Tool | What it does |
|------|--------------|
| `alphacam_new` | Start a fresh, empty drawing. |
| `alphacam_open_drawing` | Open an existing ALPHACAM drawing (.ard / .amd / .ald / …). |
| `alphacam_save_drawing` | Save to the current file. |
| `alphacam_save_as` | Save to a new file (the extension picks Router/Mill/Lathe/etc.). |
| `alphacam_import_cad` | Bring in a DXF/DWG, IGES, STEP, STL, Parasolid or SolidWorks file. 3D formats arrive as a solid model ready for `alphacam_machine_3d`. (Importing clears the drawing and the tool selection unless told to merge.) |
| `alphacam_export_file` | Write out a DXF, EMF/WMF picture, STL, or IGES. |
| `alphacam_insert_drawing` | Drop another drawing/part into this one at a chosen position. |
| `alphacam_get_drawing_info` | Filename, counts of geometries/toolpaths/layers, unsaved-changes flag. |
| `alphacam_get_extent` | The overall bounding box (size) of the drawing. |
| `alphacam_zoom_all` | Zoom to fit everything. |
| `alphacam_redraw` | Force the display to refresh. |

## Drawing shapes (CAD)

| Tool | What it does |
|------|--------------|
| `alphacam_create_rectangle` | A rectangle. |
| `alphacam_create_circle` | A circle (by diameter and centre). |
| `alphacam_create_line` | A single straight line. |
| `alphacam_create_polygon` | A regular polygon (hexagon, octagon, …). |
| `alphacam_create_ellipse` | An ellipse. |
| `alphacam_create_text` | Text turned into cuttable line/arc geometry (for engraving or cutting letters). |
| `alphacam_text` | Real, editable **text objects** — create, list, edit, delete, or convert to geometry. Use this for labels, part numbers, notes and part maps: a person can still edit them in ALPHACAM afterwards. |
| `alphacam_create_2d_geometry` | Any shape built from connected lines and arcs — the workhorse for arbitrary profiles (this is how the demo involute gear was drawn). |
| `alphacam_list_geometries` | List every shape, with which ones are selected. |
| `alphacam_get_geometry_info` | Details on one shape: layer, size, length, open/closed. |

## Editing shapes

| Tool | What it does |
|------|--------------|
| `alphacam_transform_geometry` | Move, rotate, mirror, or scale shapes (with array/copy patterns). |
| `alphacam_offset_geometry` | Offset a shape by a distance — clearances, inlay gaps, onion-skins. |
| `alphacam_fillet_geometry` | Round or chamfer sharp corners. |
| `alphacam_join_geometries` | Join loose lines/arcs into continuous paths — essential after a DXF import. |
| `alphacam_explode_geometry` | Break a path back into individual lines and arcs. |
| `alphacam_reverse_geometry` | Flip a path's cut direction. |
| `alphacam_set_start_point` | Choose where the tool plunges on a closed shape. |
| `alphacam_delete_geometry` | Delete a shape. |
| `alphacam_check_geometry` | **Lint** shapes for things that break machining styles or halt a CNC: enormous-radius arcs (I/J overflow, arc-radius alarms), micro elements, joint gaps, self-intersecting hooks/spikes, nearly-closed paths, non-zero Z, stacked duplicates. One call scans a layer, a selection, a sheet rectangle or the whole drawing and returns counts plus the worst offenders with coordinates; `fix:true` rebuilds only the flagged shapes in place (same ids, layers, tool sides). Run it after a DXF/font import and before saving parts or nesting. |

## Layers & work planes

| Tool | What it does |
|------|--------------|
| `alphacam_list_layers` | List layers with colour, visibility, lock state, counts. |
| `alphacam_manage_layer` | Create/rename/delete a layer, set active/colour/visibility. |
| `alphacam_manage_workplane` | Work planes for multi-sided / multi-face machining. |

## Material & panel

| Tool | What it does |
|------|--------------|
| `alphacam_specify_panel` | Set up the workpiece (Router "Specify Panel") — size and thickness. |
| `alphacam_set_material` | Choose the material type (drives feeds/speeds, simulation stock). |
| `alphacam_specify_material` | Designate a specific geometry as the material blank. |
| `alphacam_delete_panel` | Remove the panel designation. |
| `alphacam_delete_material` | Remove a material designation. |

## Cutting tools

| Tool | What it does |
|------|--------------|
| `alphacam_list_tools` | List every tool in your library (scans your catalog folder, subfolders included). |
| `alphacam_select_tool` | Pick a tool for the next operations. |
| `alphacam_create_tool` | Define a new tool (end mill, ball, bull-nose, drill, tap, …). |
| `alphacam_get_current_tool` | Report the currently selected tool. |
| `alphacam_save_tool` | Save a tool definition to a library file. |

## Machining

| Tool | What it does |
|------|--------------|
| `alphacam_rough_finish` | Profile a shape (rough/finish) with lead-in/out, optional helical descent, sloping (tapered) or profiled walls, and auto-Z depth stepping — the main contour-cutting operation. |
| `alphacam_pocket` | Clear out the inside of a closed shape (contour, zigzag, or spiral), optionally with sloping/profiled walls and auto-Z levels. |
| `alphacam_drill_tap` | Drill, peck-drill, tap, bore, or chamfer at circle centres. |
| `alphacam_engrave` | Follow a shape's centreline at a set depth — engraving / V-carving lines. |
| `alphacam_manual_toolpath` | Build a toolpath from explicit XYZ moves when you want full control. |
| `alphacam_set_tool_side` | Cut on the inside, outside, or centre of a shape. |
| `alphacam_set_tool_side_auto` | Let ALPHACAM decide tool sides for a batch (nesting logic). |
| `alphacam_delete_toolpath` | Remove a toolpath. |

## 3D machining (STL / STEP / surfaces)

| Tool | What it does |
|------|--------------|
| `alphacam_list_3d_models` | List the solid models and surfaces in the drawing (name, size, selected/visible) and hand back the ids the other 3D tools take. |
| `alphacam_machine_3d` | Cut a 3D model: **rough** (Z-contour roughing, level by level, with stock to leave), then finish with **parallel** (raster), **zContours** (waterline), steep/shallow pairs, projected contours, radial, spiral, rest machining (corner clean-up after a bigger tool), drive curve, and more. Boundaries contain the cut; a stock model can define the starting material. Works on models you select, all models, or ones you pick in ALPHACAM. |
| `alphacam_delete_3d_model` | Remove a solid model or surface from the drawing. |
| `alphacam_simulate` | Run ALPHACAM's solid simulation of every toolpath, export the simulated stock, and compare it against the model: a verdict (OK / INCOMPLETE / GOUGE), how much material is left and where, and whether anything was cut below the model (deviations are measured normal to the surface, so steep walls are judged fairly; the comparison is top-down, so vertical walls and undercuts are not measured). Run it before posting. |
| `alphacam_job_status` | Poll a long job started with `background: true` (3D machining, simulation, posting, imports, nesting) — the agent gets a job id back immediately and checks on it instead of waiting on one blocking call. |

> **Typical 3D job:** import the STL → specify the panel (top of material at the
> model's top) → select a flat tool → `machine_3d` rough → select a ball nose →
> `machine_3d` parallel or zContours → `simulate` → post. Every `machine_3d` call
> returns plan-check warnings (rapid levels vs. the model and stock top, roughing
> floor vs. the model's low points, a finishing boundary smaller than the model,
> uncovered areas, edge roll-over) — read them before going on. Define the
> material before machining: roughing takes its stock from it, simulation needs it,
> and the post reads it. Parallel passes run along the work plane's X axis — rotate
> a work plane to raster at an angle.

## Operations (the job list)

| Tool | What it does |
|------|--------------|
| `alphacam_list_operations` | List the machining operations in the drawing. |
| `alphacam_get_operation_info` | Details on one operation: sub-operations, times, tool used. |

## Nesting

| Tool | What it does |
|------|--------------|
| `alphacam_save_part` | Save selected shapes to their own part file for nesting, without disturbing the working drawing. |
| `alphacam_create_nest_list` | Build a nest list (.anl): parts, quantities, rotation rules, gaps, sheet-edge gap. |
| `alphacam_nest` | Run the nest onto one or more sheets (true-shape by default, rectangular available). |
| `alphacam_order_nested_toolpaths` | Sequence toolpaths sheet-by-sheet — required after a multi-sheet nest, before posting NC. |

## NC output (G-code)

| Tool | What it does |
|------|--------------|
| `alphacam_select_post` | Choose the post processor (your machine's G-code dialect). |
| `alphacam_output_nc` | Post the program to an NC file. Set the material/panel first — the Xilog post reads its dimensions from it, and simulation needs it. |
| `alphacam_list_nc` | Show the generated NC code. |

> **Post processors differ.** Alphacambot doesn't assume any particular
> machine. If your post isn't saved as a preference, the agent will ask for it
> rather than guess. Some posts also reserve certain tool *numbers* for drills —
> point the agent at your real tool catalog once (a preference) and it uses
> tools your post already expects.

## Working alongside you

| Tool | What it does |
|------|--------------|
| `alphacam_user_select` | Hands control to you: ALPHACAM prompts you to pick geometry, then the agent works on exactly what you chose. |

## The whole API (power tools)

Beyond the curated tools above, three tools reach **everything** ALPHACAM's
automation API exposes — every method and property, including modules the
curated tools don't cover. This is how new capabilities get used without waiting
for a dedicated tool.

| Tool | What it does |
|------|--------------|
| `alphacam_api_invoke` | Call any API method, or read/set any property, on the application, the drawing, or any geometry handle. Accepts a list of calls to run as one batch (one permission prompt), feeding earlier results into later calls. |
| `alphacam_api_inspect` | See what an object is and what methods/properties it has. |
| `alphacam_api_release` | Free object handles after heavy scripting. |

> These are a raw line into ALPHACAM with no safety rails — powerful, and best
> left to the agent to drive with `alphacam_api_help` as its guide.

---

## Remembering your shop's setup

So the agent stops needing reminders, it can save a small preferences file
(`%LOCALAPPDATA%\AlphacamBridge\preferences.json`) with:

- **toolCatalogFolder** — where your `.art`/`.amt` tools live
- **defaultRoutingTool** — a go-to tool for profiling
- **postFile** — your post processor (.arb)
- **ncOutputDir** — where NC programs should be written
- **units**, **defaultMaterial**, **defaultPanelThickness** — sensible defaults
- **notes** — free-form: *"always leave 0.2 mm finish stock on outside
  profiles,"* *"tabs on parts wider than 300 mm,"* whatever you'd otherwise
  repeat. The agent reads these on its first call each session.

Just tell the agent your setup once ("my tools are in …", "we run millimetres",
"here's how I like profiles done") and it saves it for next time.

---

*See [README](README.md) for install and architecture, and
[docs/DEVELOPER-GUIDE.md](docs/DEVELOPER-GUIDE.md) for how the bridge is built
and how to add tools.*
