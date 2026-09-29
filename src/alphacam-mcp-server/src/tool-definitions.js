/**
 * MCP tool definitions for all Alphacam bridge tools.
 *
 * Descriptions sourced from ACAMAPI.chm (Hexagon Alphacam API Reference).
 * Each tool maps to a JSON-RPC method on the bridge via toBridgeMethod().
 * The bridge method name converts the first underscore to a dot:
 *   alphacam_get_drawing_info → alphacam.get_drawing_info
 *
 * Tool groupings are designed for milling workflows. Turning/Wire/Laser
 * operations are not exposed as primary tools (use alphacam_api_help to
 * discover them if needed).
 */

/** @type {Array<import('@modelcontextprotocol/sdk/types.js').Tool>} */
export const TOOL_DEFINITIONS = [
  // ════════════════════════════════════════════════════════════════
  //  FILE & DRAWING OPERATIONS
  // ════════════════════════════════════════════════════════════════
  {
    name: 'alphacam_new',
    description:
      'Create a new empty drawing — clears all memory. Equivalent to File > New. The drawing starts with default layers (typically 12).',
    inputSchema: {
      type: 'object',
      properties: {},
    },
  },
  {
    name: 'alphacam_open_drawing',
    description:
      'Open an Alphacam drawing file. Extension depends on the active module: .ard (Router), .amd (Mill), .ald (Lathe), .asd (Stone), .awd (Wire). For CAD imports (DXF/DWG/STEP/IGES/STL), use alphacam_import_cad instead.',
    inputSchema: {
      type: 'object',
      properties: {
        path: {
          type: 'string',
          description: 'Full file path to the Alphacam drawing (.ard for Router, .amd for Mill, etc.)',
        },
      },
      required: ['path'],
    },
  },
  {
    name: 'alphacam_save_drawing',
    description:
      'Save the current drawing to its existing file path. Returns the full path. Fails if the drawing has never been saved (use alphacam_save_as).',
    inputSchema: {
      type: 'object',
      properties: {},
    },
  },
  {
    name: 'alphacam_save_as',
    description:
      'Save the current drawing to a new file path. Use for first save or to create a copy. The file extension determines the drawing type — use .ard for Router, .amd for Mill, .ald for Lathe, .asd for Stone, .awd for Wire. Alphacam writes whichever extension is passed; choose the one matching the active module.',
    inputSchema: {
      type: 'object',
      properties: {
        path: {
          type: 'string',
          description: 'Destination file path. Extension picks the Alphacam document type (.ard Router, .amd Mill, .ald Lathe, .asd Stone, .awd Wire).',
        },
      },
      required: ['path'],
    },
  },
  {
    name: 'alphacam_import_cad',
    description:
      'Import a CAD file into the current drawing. Supported formats: DXF/DWG (OpenDxfFile), STEP (OpenStepFile — imports as surfaces), IGES (OpenIgesFile), STL (OpenStlFile). The format determines which Alphacam import method is called.',
    inputSchema: {
      type: 'object',
      properties: {
        path: {
          type: 'string',
          description: 'Full path to the CAD file',
        },
        format: {
          type: 'string',
          description:
            'File format. If omitted, inferred from extension.',
          enum: ['dxf', 'iges', 'step', 'stl'],
        },
      },
      required: ['path'],
    },
  },
  {
    name: 'alphacam_get_drawing_info',
    description:
      'Get information about the current drawing. Returns: fullName (complete path, empty if unsaved), name (filename without path/extension), path (directory), geometryCount (from GetGeoCount), toolPathCount (from GetToolPathCount), layerCount (from GetLayerCount), modified (true if unsaved changes).',
    inputSchema: {
      type: 'object',
      properties: {},
    },
  },

  // ════════════════════════════════════════════════════════════════
  //  GEOMETRY CREATION
  //  All methods add geometry to the Alphacam geometry database.
  //  Coordinates are in drawing units (typically mm or inches).
  // ════════════════════════════════════════════════════════════════
  {
    name: 'alphacam_create_rectangle',
    description:
      'Create a rectangle geometry (Drawing.CreateRectangle). Adds a closed 4-sided geometry to the database. Returns a Path object reference.',
    inputSchema: {
      type: 'object',
      properties: {
        x1: { type: 'number', description: 'X coordinate of first corner' },
        y1: { type: 'number', description: 'Y coordinate of first corner' },
        x2: { type: 'number', description: 'X coordinate of opposite corner' },
        y2: { type: 'number', description: 'Y coordinate of opposite corner' },
        layer: { type: 'string', description: 'Target layer name (optional)' },
      },
      required: ['x1', 'y1', 'x2', 'y2'],
    },
  },
  {
    name: 'alphacam_create_circle',
    description:
      'Create a circle geometry (Drawing.CreateCircle). Parameters: diameter (not radius), center X/Y. Returns a Path object reference.',
    inputSchema: {
      type: 'object',
      properties: {
        diameter: { type: 'number', description: 'Circle diameter' },
        centerX: { type: 'number', description: 'X coordinate of center' },
        centerY: { type: 'number', description: 'Y coordinate of center' },
        layer: { type: 'string', description: 'Target layer name (optional)' },
      },
      required: ['diameter', 'centerX', 'centerY'],
    },
  },
  {
    name: 'alphacam_create_line',
    description:
      'Create a single 2D line geometry (Drawing.Create2DLine). Adds an open geometry with one line segment.',
    inputSchema: {
      type: 'object',
      properties: {
        x1: { type: 'number', description: 'X coordinate of start point' },
        y1: { type: 'number', description: 'Y coordinate of start point' },
        x2: { type: 'number', description: 'X coordinate of end point' },
        y2: { type: 'number', description: 'Y coordinate of end point' },
        layer: { type: 'string', description: 'Target layer name (optional)' },
      },
      required: ['x1', 'y1', 'x2', 'y2'],
    },
  },
  {
    name: 'alphacam_create_polygon',
    description:
      'Create a regular polygon (Drawing.CreatePolygon). Parameters: diameter (of circumscribed circle), number of sides, whether to inscribe inside the circle, center position.',
    inputSchema: {
      type: 'object',
      properties: {
        diameter: { type: 'number', description: 'Diameter of the circumscribed circle' },
        numSides: { type: 'integer', description: 'Number of sides (3+)' },
        insideCircle: {
          type: 'boolean',
          description: 'True to inscribe polygon inside the diameter circle (default false)',
        },
        centerX: { type: 'number', description: 'X coordinate of center' },
        centerY: { type: 'number', description: 'Y coordinate of center' },
        layer: { type: 'string', description: 'Target layer name (optional)' },
      },
      required: ['diameter', 'numSides', 'centerX', 'centerY'],
    },
  },
  {
    name: 'alphacam_create_text',
    description:
      'Create text as geometry lines and arcs (Drawing.CreateText). Text is converted to cuttable geometry (not annotation). Font must start with "A" for Alphacam font (e.g., "AStencil") or "T" for TrueType (e.g., "TArial"). Returns a Paths collection.',
    inputSchema: {
      type: 'object',
      properties: {
        text: { type: 'string', description: 'The text string to create' },
        x: { type: 'number', description: 'X coordinate of text origin' },
        y: { type: 'number', description: 'Y coordinate of text origin' },
        height: { type: 'number', description: 'Character height in drawing units' },
        font: {
          type: 'string',
          description: 'Font name. Prefix "A" for Alphacam font, "T" for TrueType (e.g., "TArial"). Default: Alphacam default.',
        },
        layer: { type: 'string', description: 'Target layer name (optional)' },
      },
      required: ['text', 'x', 'y', 'height'],
    },
  },
  {
    name: 'alphacam_create_ellipse',
    description:
      'Create an ellipse geometry (Drawing.CreateEllipse). Specified by width, height, number of quadrants, and arcs per quadrant. Returns a Path object.',
    inputSchema: {
      type: 'object',
      properties: {
        width: { type: 'number', description: 'Ellipse width (X axis diameter)' },
        height: { type: 'number', description: 'Ellipse height (Y axis diameter)' },
        centerX: { type: 'number', description: 'X coordinate of center' },
        centerY: { type: 'number', description: 'Y coordinate of center' },
        numQuads: {
          type: 'integer',
          description: 'Number of quadrants to draw (1-4, default 4 for full ellipse)',
        },
        arcsPerQuad: {
          type: 'integer',
          description: 'Number of arcs per quadrant (higher = smoother, default 4)',
        },
        layer: { type: 'string', description: 'Target layer name (optional)' },
      },
      required: ['width', 'height', 'centerX', 'centerY'],
    },
  },
  {
    name: 'alphacam_create_2d_geometry',
    description:
      'Create arbitrary 2D geometry from connected lines and arcs (Drawing.Create2DGeometry + Geo2D). Start with a point, then add segments. Geo2D methods: AddLine(X,Y), AddArcCW(X,Y,Xc,Yc), AddArcCCW(X,Y,Xc,Yc), CloseAndFinishLine/FinishLine.',
    inputSchema: {
      type: 'object',
      properties: {
        startX: { type: 'number', description: 'Starting X coordinate' },
        startY: { type: 'number', description: 'Starting Y coordinate' },
        segments: {
          type: 'array',
          description: 'Array of segment objects defining the geometry path',
          items: {
            type: 'object',
            properties: {
              type: {
                type: 'string',
                enum: ['line', 'arcCW', 'arcCCW'],
                description: 'Segment type: line, clockwise arc, or counter-clockwise arc',
              },
              x: { type: 'number', description: 'End point X' },
              y: { type: 'number', description: 'End point Y' },
              cx: { type: 'number', description: 'Arc center X (arcs only)' },
              cy: { type: 'number', description: 'Arc center Y (arcs only)' },
            },
            required: ['type', 'x', 'y'],
          },
        },
        closed: {
          type: 'boolean',
          description: 'True to close geometry (CloseAndFinishLine). False for open geometry (FinishLine). Default: false.',
        },
        layer: { type: 'string', description: 'Target layer name (optional)' },
      },
      required: ['startX', 'startY', 'segments'],
    },
  },

  // ════════════════════════════════════════════════════════════════
  //  GEOMETRY QUERY & MANIPULATION
  // ════════════════════════════════════════════════════════════════
  {
    name: 'alphacam_list_geometries',
    description:
      "List all geometries in the current drawing with their selection state. Each entry has geometryId, index, and selected (Path.Selected — true if the user has it selected in the Alphacam window). Pass selectedOnly=true to return just the user's current selection — the way to see what a human picked without an interactive prompt. Response includes selectedCount.",
    inputSchema: {
      type: 'object',
      properties: {
        selectedOnly: {
          type: 'boolean',
          description: 'Return only currently selected geometries. Default false.',
        },
      },
    },
  },
  {
    name: 'alphacam_get_geometry_info',
    description:
      'Get detailed information about a specific geometry. Returns element-level data: coordinates, line/arc types, layer, construction flag, and bounding box.',
    inputSchema: {
      type: 'object',
      properties: {
        id: {
          type: 'string',
          description: 'Geometry object ID (from alphacam_list_geometries)',
        },
      },
      required: ['id'],
    },
  },

  // ════════════════════════════════════════════════════════════════
  //  LAYER MANAGEMENT
  // ════════════════════════════════════════════════════════════════
  {
    name: 'alphacam_list_layers',
    description:
      'List all layers in the drawing with their properties: name, visible, active, locked, special (non-zero = built-in Alphacam layer), color, and geometry count. Also returns activeLayer (the current user layer, if any). Walks Drawing.GetFirstLayer/GetNext.',
    inputSchema: {
      type: 'object',
      properties: {},
    },
  },
  {
    name: 'alphacam_manage_layer',
    description:
      'Create, rename, delete, or modify a layer. Actions: create (idempotent — returns the existing layer if the name exists), rename, delete, set_visible, set_active (Layer.Active — selectability), set_current (Drawing.SetLayer — new geometry goes to this layer), set_color. Layers are matched by case-insensitive name.',
    inputSchema: {
      type: 'object',
      properties: {
        action: {
          type: 'string',
          enum: ['create', 'rename', 'delete', 'set_visible', 'set_active', 'set_current', 'set_color'],
          description: 'What to do with the layer',
        },
        name: { type: 'string', description: 'Layer name (max 31 chars for create)' },
        newName: { type: 'string', description: 'New layer name (for rename)' },
        visible: { type: 'boolean', description: 'Visibility state (for set_visible)' },
        active: { type: 'boolean', description: 'Active state (for set_active)' },
        color: { type: 'integer', description: 'Color index 0-15 (for set_color)' },
      },
      required: ['action', 'name'],
    },
  },

  // ════════════════════════════════════════════════════════════════
  //  VIEW OPERATIONS
  // ════════════════════════════════════════════════════════════════
  {
    name: 'alphacam_zoom_all',
    description:
      'Zoom the view to fit all geometry (ViewWindow.ZoomAll). Equivalent to View > Zoom All.',
    inputSchema: {
      type: 'object',
      properties: {},
    },
  },
  {
    name: 'alphacam_redraw',
    description:
      'Force a full redraw of the Alphacam display. Call after programmatic changes to see updates.',
    inputSchema: {
      type: 'object',
      properties: {},
    },
  },

  // ════════════════════════════════════════════════════════════════
  //  TOOL MANAGEMENT
  //  Alphacam tools are MillTool objects with properties:
  //    Diameter, CornerRadius, Length, Type (acamToolSQUARE/BULL/BALL/
  //    DRILL/TAP), Number, Name, FeedPerTooth, NumberOfTeeth,
  //    DepthOfCut, FixedFeed, FixedSpeed, Rotation (CW/CCW), etc.
  // ════════════════════════════════════════════════════════════════
  {
    name: 'alphacam_select_tool',
    description:
      'Select a tool for subsequent machining (App.SelectTool). Requires the FULL PATH to a tool file, not just a name. Returns the selected tool\'s actual fileName — if alphacam_list_tools auto-discovery fails on this install, call select_tool with NO fileName (opens the user\'s tool picker), let them pick any tool, and learn the library folder from the returned fileName. WORKFLOW: if you don\'t already have a full path in hand, call alphacam_list_tools FIRST — it recursively scans the Router/Mill library (including user subfolders like "Canavan Tools") and returns each tool\'s `path` value ready to hand to this method. Router tool files are .art; Mill are .amt. Pass an empty string or omit fileName to show the user\'s tool picker dialog. Returns the selected tool\'s diameter, number, type, length.',
    inputSchema: {
      type: 'object',
      properties: {
        fileName: {
          type: 'string',
          description: 'Full path to tool file (e.g. .art for Router, .amt for Mill). Omit or pass empty string to show the user picker dialog.',
        },
      },
    },
  },
  {
    name: 'alphacam_create_tool',
    description:
      'Create a new mill tool definition (App.CreateTool), set its properties, and by default SELECT it for machining. Tool types (AcamToolType — note SQUARE is 1, not 0): 1=SQUARE (end mill), 2=BULL (bull-nose), 3=BALL, 4=DRILL, 5=TAP, 6=USER, 7=WHEEL — string names also accepted. Pass savePath to also write the tool to a .art/.amt library file, or use alphacam_save_tool afterward.',
    inputSchema: {
      type: 'object',
      properties: {
        diameter: { type: 'number', description: 'Tool diameter' },
        type: {
          description: 'Tool type: 1=SQUARE (end mill), 2=BULL, 3=BALL, 4=DRILL, 5=TAP, 6=USER, 7=WHEEL, or the string square/bull/ball/drill/tap/user/wheel',
        },
        name: { type: 'string', description: 'Tool name' },
        cornerRadius: {
          type: 'number',
          description: 'Corner radius for bull-nose tools (type 2). Default: 0.',
        },
        length: { type: 'number', description: 'Tool length' },
        depthOfCut: { type: 'number', description: 'Depth of one cut' },
        number: { type: 'integer', description: 'Tool number to assign' },
        numberOfTeeth: { type: 'integer', description: 'Number of cutting teeth/flutes' },
        feedPerTooth: { type: 'number', description: 'Feed per tooth for feed calculation' },
        pitch: { type: 'number', description: 'Thread pitch (tap/drill tools)' },
        shankDiameter: { type: 'number', description: 'Shank diameter' },
        fixedFeed: { type: 'number', description: 'Fixed feed rate (overrides calculated). Setting this or fixedSpeed sets FixedFeedAndSpeed=true.' },
        fixedSpeed: { type: 'number', description: 'Fixed spindle speed (overrides calculated)' },
        savePath: { type: 'string', description: 'Optional full path to save the tool file (MillTool.SaveAs)' },
        select: { type: 'boolean', description: 'Select the tool for machining after creating (MillTool.Select). Default true.' },
      },
      required: ['diameter', 'type'],
    },
  },
  {
    name: 'alphacam_get_current_tool',
    description:
      'Get properties of the currently selected tool (App.GetCurrentTool). Returns: Diameter, Type, CornerRadius, Length, Number, Name, FileName, FeedPerTooth, NumberOfTeeth, DepthOfCut, FixedFeed, FixedSpeed, Rotation.',
    inputSchema: {
      type: 'object',
      properties: {},
    },
  },
  {
    name: 'alphacam_save_tool',
    description:
      'Save a tool to a library file (MillTool.SaveAs). Saves the CURRENT tool by default, or pass toolId (from alphacam_create_tool / alphacam_select_tool) to save a cached tool object. Router tool files are .art; Mill are .amt. Saving does not select the tool.',
    inputSchema: {
      type: 'object',
      properties: {
        path: {
          type: 'string',
          description: 'Full file path to save the tool definition (.art for Router, .amt for Mill)',
        },
        toolId: {
          type: 'string',
          description: 'Optional cached tool id. Omit to save the current tool.',
        },
      },
      required: ['path'],
    },
  },
  {
    name: 'alphacam_list_tools',
    description:
      'List every tool available in the Alphacam tool library. Defaults to a RECURSIVE scan of LicomdatPath\\Rtools.alp (Router) or Mtools.alp (Mill), so user subfolders (e.g. "Canavan Tools", "Amana", vendor libraries) are included automatically. Returns each tool\'s name, full path, and subfolder (if any). ALWAYS call this at the start of a machining task to see what tools are available before calling alphacam_select_tool — the returned `path` values are what select_tool needs. Pass `folder` explicitly only when overriding the auto-discovered location, or `recursive: false` to restrict to the top level.',
    inputSchema: {
      type: 'object',
      properties: {
        folder: {
          type: 'string',
          description: 'Full path to the tool library folder. Omit for auto-discovery under LicomdatPath.',
        },
        extension: {
          type: 'string',
          description: 'Tool file extension. Default ".art" for Router; pass ".amt" for Mill.',
        },
        recursive: {
          type: 'boolean',
          description: 'Recurse into subfolders. Default true — needed to find user-organized libraries like "Canavan Tools".',
        },
      },
    },
  },

  // ════════════════════════════════════════════════════════════════
  //  MACHINING OPERATIONS (MILLING)
  //  These use MillData properties to configure the operation, then
  //  call the appropriate machining method. The MillData object has
  //  125+ properties controlling cut depth, stepover, feeds/speeds,
  //  lead-in/out, compensation, etc. Use alphacam_api_help for the
  //  full MillData property reference.
  // ════════════════════════════════════════════════════════════════
  {
    name: 'alphacam_rough_finish',
    description:
      'Apply a Rough/Finish operation to a single geometry (MillData.RoughFinish). The bridge selects only the named geometry, builds a MillData with operator-level defaults, applies the caller\'s overrides, and runs the operation. Defaults: McComp=TOOL_CENTER, SafeRapidLevel=1.0, RapidDownTo=0.25, FinalDepth=-0.25, NumberOfCuts=1, no helical, no stock. A post processor and tool must be selected first (alphacam_select_post, alphacam_select_tool). Tool side is taken from the geometry\'s existing direction.',
    inputSchema: {
      type: 'object',
      properties: {
        selected: { type: 'boolean', description: 'Act on the geometries currently SELECTED in the Alphacam window (reads Path.Selected).' },
        interactive: { type: 'boolean', description: 'Have Alphacam PROMPT the user to pick geometries (blocks until they finish with Escape).' },
        prompt: { type: 'string', description: 'Prompt-bar text for interactive mode.' },
        geometryId: {
          type: 'string',
          description: 'ID of the geometry to machine (from create_* or list_geometries).',
        },
        geometryIds: {
          type: 'array',
          items: { type: 'string' },
          description: 'Machine MULTIPLE geometries in one operation (one MillData run on all of them). Use instead of (or alongside) geometryId.',
        },
        finalDepth: {
          type: 'number',
          description: 'Final Z depth (typically negative — below the material surface). Default -0.25.',
        },
        numberOfCuts: {
          type: 'integer',
          description: 'Number of Z levels (vertical passes). Default 1.',
        },
        helical: {
          type: 'boolean',
          description: 'True for helical interpolation (continuous Z descent on a circular tool path). Default false.',
        },
        helicalPitch: {
          type: 'number',
          description: 'Z pitch per revolution when helical=true (sets MillData.MaxDepthPerCut). Defaults to the current tool\'s Diameter — sensible starting point for a spiral entry. Fallback 0.1 if no tool selected.',
        },
        safeRapidLevel: {
          type: 'number',
          description: 'Z level for rapid moves above the work (clearance plane). Default 1.0.',
        },
        rapidDownTo: {
          type: 'number',
          description: 'Z level to rapid down to before transitioning to feed (typically just above stock). Default 0.25.',
        },
        initialStock: {
          type: 'number',
          description: 'Initial XY stock allowance when using multiple lateral cuts (MillData.InitialStock). Default 0.',
        },
        stockToLeave: {
          type: 'number',
          description: 'Material to leave on the part after this operation (MillData.Stock). Default 0.',
        },
        lateralCuts: {
          type: 'integer',
          description: 'Number of XY cuts for Rough/Finish (MillData.RoughFinishNumberOfCuts). Default 1.',
        },
        feedRate: {
          type: 'number',
          description: 'Cutting feed rate (MillData.CutFeed). Omit to use the tool\'s feedrate.',
        },
        spindleSpeed: {
          type: 'number',
          description: 'Spindle RPM (MillData.SpindleSpeed). Omit to use the tool\'s speed.',
        },
        leadInStyle: { type: 'string', enum: ['line', 'arc', 'both', 'none'], description: 'Lead-in style (LeadData.LeadIn).' },
        leadOutStyle: { type: 'string', enum: ['line', 'arc', 'both', 'none'], description: 'Lead-out style (LeadData.LeadOut).' },
        leadInLength: { type: 'number', description: 'Lead-in length in TOOL RADII (LeadData.LengthIn) — e.g. 2 with a 0.5" tool means a 0.5" XY lead. Not drawing units. Matches the Alphacam UI convention.' },
        leadOutLength: { type: 'number', description: 'Lead-out length in TOOL RADII (LeadData.LengthOut).' },
        leadInAngle: { type: 'number', description: 'Lead-in approach angle in degrees (LeadData.AngleIn). Used by sloping-line and mixed leads.' },
        leadOutAngle: { type: 'number', description: 'Lead-out departure angle in degrees (LeadData.AngleOut).' },
        leadInRadius: { type: 'number', description: 'Lead-in arc radius (LeadData.RadiusIn). Only meaningful when leadInStyle is arc or both.' },
        leadOutRadius: { type: 'number', description: 'Lead-out arc radius (LeadData.RadiusOut).' },
        leadInTangential: { type: 'boolean', description: 'True for tangential lead-in (LeadData.LineArcInTangential).' },
        leadOutTangential: { type: 'boolean', description: 'True for tangential lead-out (LeadData.LineArcOutTangential).' },
        leadInSlope: { type: 'boolean', description: 'Enable a Z-sloped (ramping) lead-in (LeadData.SlopeIn is a bool). The ramp angle is DERIVED by Alphacam from safeRapidLevel down to finalDepth over the XY leadInLength — you don\'t supply an angle.' },
        leadOutSlope: { type: 'boolean', description: 'Enable a Z-sloped lead-out (LeadData.SlopeOut).' },
        leadOverlap: { type: 'number', description: 'Overlap distance between lead-in start and lead-out end (LeadData.Overlap).' },
        leadInFeedModifier: { type: 'number', description: 'Feedrate multiplier during lead-in (LeadData.FeedrateModifierIn). 1.0 = full cut feed.' },
        leadOutFeedModifier: { type: 'number', description: 'Feedrate multiplier during lead-out (LeadData.FeedrateModifierOut).' },
      },
      required: [],
    },
  },
  {
    name: 'alphacam_pocket',
    description:
      'Pocket (area clearance) inside a closed boundary geometry (MillData.Pocket). The bridge sets Path.ToolInOut on the boundary (default acamINSIDE — machine the interior), builds a MillData, selects the boundary, and runs the pocket. WidthOfCut is the stepover; if omitted, defaults to 50% of the active tool diameter. PocketType: contour (offset inward, default), linear (zigzag), spiral. Lead-in/out params (leadInStyle, leadInLength, leadInSlope, etc.) are applied via Path.SetLeadInOutAuto on the returned toolpaths, same as rough_finish. A post processor and tool must be selected first.',
    inputSchema: {
      type: 'object',
      properties: {
        selected: { type: 'boolean', description: 'Act on the geometries currently SELECTED in the Alphacam window (reads Path.Selected).' },
        interactive: { type: 'boolean', description: 'Have Alphacam PROMPT the user to pick geometries (blocks until they finish with Escape).' },
        prompt: { type: 'string', description: 'Prompt-bar text for interactive mode.' },
        geometryId: {
          type: 'string',
          description: 'ID of the closed boundary geometry to pocket',
        },
        geometryIds: {
          type: 'array',
          items: { type: 'string' },
          description: 'Machine MULTIPLE geometries in one operation (one MillData run on all of them). Use instead of (or alongside) geometryId.',
        },
        depth: {
          type: 'number',
          description: 'Final Z depth of the pocket (negative, below surface). Alias: finalDepth. Default -0.25.',
        },
        numberOfCuts: {
          type: 'integer',
          description: 'Number of Z passes. Default 1.',
        },
        pocketType: {
          type: 'string',
          enum: ['contour', 'linear', 'spiral'],
          description: 'Pocket strategy. contour=offset-inward (default), linear=zigzag, spiral=spiral-in.',
        },
        toolSide: {
          type: 'string',
          enum: ['inside', 'outside', 'center'],
          description: 'Which side of the boundary the tool stays on. Default inside (normal pocket).',
        },
        stepover: {
          type: 'number',
          description: 'Step-over between passes (MillData.WidthOfCut). Defaults to 50% of the current tool diameter.',
        },
        finishAllowance: {
          type: 'number',
          description: 'Material to leave on the walls/floor (MillData.Stock). Alias: stockToLeave. Default 0.',
        },
        safeRapidLevel: {
          type: 'number',
          description: 'Clearance Z for rapid moves. Default 1.0.',
        },
        rapidDownTo: {
          type: 'number',
          description: 'Z to rapid down to before transitioning to feed. Default 0.25.',
        },
        cutDirection: {
          type: 'number',
          description: 'Angle (degrees) for linear pocket cuts. Only used when pocketType=linear.',
        },
        pocketOrder: {
          type: 'string',
          enum: ['zone', 'depth'],
          description: 'Order for multi-Z pockets. zone=clear-each-level-then-next, depth=full-depth-each-zone.',
        },
        feedRate: { type: 'number', description: 'Cutting feed rate (MillData.CutFeed). Omit to use tool default.' },
        spindleSpeed: { type: 'number', description: 'Spindle RPM. Omit to use tool default.' },
        leadInStyle: { type: 'string', enum: ['line', 'arc', 'both', 'none'], description: 'Lead-in style (LeadData.LeadIn).' },
        leadOutStyle: { type: 'string', enum: ['line', 'arc', 'both', 'none'], description: 'Lead-out style (LeadData.LeadOut).' },
        leadInLength: { type: 'number', description: 'Lead-in length in TOOL RADII (LeadData.LengthIn) — e.g. 2 with a 0.5" tool means a 0.5" XY lead. Not drawing units. Matches the Alphacam UI convention.' },
        leadOutLength: { type: 'number', description: 'Lead-out length in TOOL RADII (LeadData.LengthOut).' },
        leadInAngle: { type: 'number', description: 'Lead-in approach angle in degrees (LeadData.AngleIn).' },
        leadOutAngle: { type: 'number', description: 'Lead-out departure angle in degrees (LeadData.AngleOut).' },
        leadInRadius: { type: 'number', description: 'Lead-in arc radius (LeadData.RadiusIn).' },
        leadOutRadius: { type: 'number', description: 'Lead-out arc radius (LeadData.RadiusOut).' },
        leadInTangential: { type: 'boolean', description: 'True for tangential lead-in (LeadData.LineArcInTangential).' },
        leadOutTangential: { type: 'boolean', description: 'True for tangential lead-out (LeadData.LineArcOutTangential).' },
        leadInSlope: { type: 'boolean', description: 'Enable a Z-sloped (ramping) lead-in (LeadData.SlopeIn is a bool). Ramp angle is auto-derived from safeRapidLevel → finalDepth over the XY leadInLength.' },
        leadOutSlope: { type: 'boolean', description: 'Enable a Z-sloped lead-out (LeadData.SlopeOut).' },
        leadOverlap: { type: 'number', description: 'Overlap distance between lead-in and lead-out (LeadData.Overlap).' },
        leadInFeedModifier: { type: 'number', description: 'Feedrate multiplier during lead-in (LeadData.FeedrateModifierIn).' },
        leadOutFeedModifier: { type: 'number', description: 'Feedrate multiplier during lead-out (LeadData.FeedrateModifierOut).' },
      },
      required: [],
    },
  },
  {
    name: 'alphacam_set_tool_side',
    description:
      'Set which side of a geometry the cutter stays on. For CLOSED geometries this sets Path.ToolInOut (inside/outside/center). For OPEN geometries (lines, open polylines) it sets Path.ToolSide (left/right/center). The bridge picks the right property based on whether the geometry endpoints meet. Combined with geometry direction (CW/CCW), this controls climb vs conventional milling.',
    inputSchema: {
      type: 'object',
      properties: {
        geometryId: {
          type: 'string',
          description: 'ID of the geometry to update',
        },
        side: {
          type: 'string',
          enum: ['inside', 'outside', 'center', 'left', 'right'],
          description: 'Tool side. inside/outside for closed loops, left/right for open paths, center for both.',
        },
      },
      required: ['geometryId', 'side'],
    },
  },
  {
    name: 'alphacam_reverse_geometry',
    description:
      'Flip the cut direction of a geometry. Pass direction=cw/ccw to set it explicitly (Path.CW), or omit direction to simply call Path.Reverse(). Flipping the geometry direction swaps climb and conventional milling when combined with the existing tool side.',
    inputSchema: {
      type: 'object',
      properties: {
        geometryId: {
          type: 'string',
          description: 'ID of the geometry to reverse',
        },
        direction: {
          type: 'string',
          enum: ['cw', 'ccw'],
          description: 'Target direction. Omit to simply toggle the current direction.',
        },
      },
      required: ['geometryId'],
    },
  },
  {
    name: 'alphacam_set_start_point',
    description:
      'Set where the tool plunges on a closed geometry (Path.SetStartPoint). Instead of computing coordinates by hand, pass a `rule` and the bridge picks a point that lies on the geometry. Rules: `explicit` (needs `x`, `y`), `middleOfLongestEdge`, `middleOfShortestEdge`, `topLeft` / `topRight` / `bottomLeft` / `bottomRight` (nearest vertex to that bounding-box corner), `nearestToPoint` (needs `x`, `y`). Response echoes the computed `x`/`y` and, for edge-midpoint rules, the chosen edge\'s `edgeLength`. Only works on closed geometries.',
    inputSchema: {
      type: 'object',
      properties: {
        geometryId: { type: 'string', description: 'ID of the closed geometry to modify' },
        rule: {
          type: 'string',
          enum: [
            'explicit',
            'middleOfLongestEdge',
            'middleOfShortestEdge',
            'topLeft', 'topRight', 'bottomLeft', 'bottomRight',
            'nearestToPoint',
          ],
          description: 'How to pick the start point. Default explicit (needs x, y).',
        },
        x: { type: 'number', description: 'X coordinate (required for explicit and nearestToPoint).' },
        y: { type: 'number', description: 'Y coordinate (required for explicit and nearestToPoint).' },
      },
      required: ['geometryId'],
    },
  },
  {
    name: 'alphacam_delete_geometry',
    description:
      'Delete a geometry (Path) from the drawing via Path.Delete. Unlike Path.Erase (which only paints in the background colour), Delete actually removes it. Also drops the id from the shim\'s object cache. Use this to iterate on cut parameters without recreating geometry.',
    inputSchema: {
      type: 'object',
      properties: {
        geometryId: { type: 'string', description: 'ID of the geometry to delete' },
      },
      required: ['geometryId'],
    },
  },
  {
    name: 'alphacam_delete_toolpath',
    description:
      'Delete one or more toolpaths. Modes: (a) geometryId — delete a specific cached toolpath by its obj_N id. (b) index — delete Drawing.ToolPaths.Item(index), 1-based. (c) all=true — Drawing.Clear(toolpaths only), wipes every toolpath in the drawing. Toolpaths are Path objects, so Path.Delete works.',
    inputSchema: {
      type: 'object',
      properties: {
        geometryId: { type: 'string', description: 'Cached toolpath id (from a machining op that returned a Paths collection).' },
        index: { type: 'integer', description: '1-based index into Drawing.ToolPaths.' },
        all: { type: 'boolean', description: 'True to delete all toolpaths in one shot (Drawing.Clear).' },
      },
    },
  },
  {
    name: 'alphacam_delete_panel',
    description:
      'Delete the panel geometry that was designated via Path.SetPanel. Alphacam has no explicit CancelPanel COM call — the panel designation lives on the geometry, so removing the geometry removes the panel. Same underlying operation as delete_geometry, but semantically distinct.',
    inputSchema: {
      type: 'object',
      properties: {
        geometryId: { type: 'string', description: 'ID of the panel geometry (from specify_panel).' },
      },
      required: ['geometryId'],
    },
  },
  {
    name: 'alphacam_delete_material',
    description:
      'Delete a material designation. Two targets: (a) target="geo" (default) — delete the geometry designated as material via Path.SetMaterial (requires geometryId). (b) target="type" — clear the active material TYPE (feeds/speeds/simulation source) by setting a blank Material via Drawing.SetMaterial. Matches the specify_material vs set_material split.',
    inputSchema: {
      type: 'object',
      properties: {
        target: { type: 'string', enum: ['geo', 'type'], description: 'geo = delete material geometry; type = clear material type. Default geo.' },
        geometryId: { type: 'string', description: 'Required when target=geo. ID of the material geometry (from specify_material).' },
      },
    },
  },
  {
    name: 'alphacam_drill_tap',
    description:
      'Drill, peck-drill, tap, bore, or chamfer at the selected circle geometry (MillData.DrillTap). Select a drill/tap tool first (alphacam_select_tool). Drill types (AcamDrillType): 0=DRILL, 1=PECK, 2=TAP, 3=BORE, 4=CHAMFER — string names also accepted. depth is BottomOfHole (negative = below material top). For taps, threadPitch defaults to the current tool\'s Pitch.',
    inputSchema: {
      type: 'object',
      properties: {
        selected: { type: 'boolean', description: 'Act on the geometries currently SELECTED in the Alphacam window (reads Path.Selected).' },
        interactive: { type: 'boolean', description: 'Have Alphacam PROMPT the user to pick geometries (blocks until they finish with Escape).' },
        prompt: { type: 'string', description: 'Prompt-bar text for interactive mode.' },
        geometryId: {
          type: 'string',
          description: 'ID of the circle/point geometry to drill at',
        },
        geometryIds: {
          type: 'array',
          items: { type: 'string' },
          description: 'Machine MULTIPLE geometries in one operation (one MillData run on all of them). Use instead of (or alongside) geometryId.',
        },
        drillType: {
          description: 'Drill type: 0=DRILL, 1=PECK, 2=TAP, 3=BORE, 4=CHAMFER, or the string drill/peck/tap/bore/chamfer. Default 0 (drill).',
        },
        depth: { type: 'number', description: 'Bottom of hole Z (MillData.BottomOfHole). Negative = below material top.' },
        peckDistance: {
          type: 'number',
          description: 'Peck increment per plunge (MillData.PeckDistance, for drillType=peck). Alias: peckDepth.',
        },
        dwell: {
          type: 'number',
          description: 'Dwell time at the bottom in seconds (MillData.DwellTime)',
        },
        retractPartial: {
          type: 'boolean',
          description: 'True for partial retract between pecks (MillData.RetractPartial)',
        },
        threadPitch: {
          type: 'number',
          description: 'Thread pitch for tapping (MillData.ThreadPitch). Defaults to the current tool\'s Pitch.',
        },
        whichHoles: {
          type: 'integer',
          description: 'Which holes to drill (AcamHoles): 0=CIRCLES (default), 1=CROSSES, 2=MATCH_DRILL, 3=GIVEN_DIA',
        },
        givenHoleDiameter: {
          type: 'number',
          description: 'Hole diameter filter, used with whichHoles=3 (GIVEN_DIA)',
        },
        safeRapidLevel: { type: 'number', description: 'Z level for rapids. Default 1.0.' },
        rapidDownTo: { type: 'number', description: 'Z level to rapid down to before feeding. Default 0.25.' },
        feedRate: { type: 'number', description: 'Drilling feed rate (MillData.DrillFeed)' },
        spindleSpeed: { type: 'number', description: 'Spindle RPM' },
      },
      required: ['depth'],
    },
  },
  {
    name: 'alphacam_engrave',
    description:
      'Create an engraving toolpath along the selected geometry (MillData.Engrave with EngraveType=GEOMETRIES — follows the geometry centerline at the given depth). Typically used with a V-bit/cone tool. Supports the same lead-in/out params as rough_finish (applied via Path.SetLeadInOutAuto on the returned toolpaths).',
    inputSchema: {
      type: 'object',
      properties: {
        selected: { type: 'boolean', description: 'Act on the geometries currently SELECTED in the Alphacam window (reads Path.Selected).' },
        interactive: { type: 'boolean', description: 'Have Alphacam PROMPT the user to pick geometries (blocks until they finish with Escape).' },
        prompt: { type: 'string', description: 'Prompt-bar text for interactive mode.' },
        geometryId: {
          type: 'string',
          description: 'ID of the geometry or text outline to engrave',
        },
        geometryIds: {
          type: 'array',
          items: { type: 'string' },
          description: 'Machine MULTIPLE geometries in one operation (one MillData run on all of them). Use instead of (or alongside) geometryId.',
        },
        depth: { type: 'number', description: 'Engraving depth (MillData.FinalDepth). Negative = below material top.' },
        numberOfCuts: { type: 'integer', description: 'Number of Z passes. Default 1.' },
        cornerStyle: {
          type: 'string',
          enum: ['round', 'straight', 'loop'],
          description: 'XY corner handling (AcamCorners): round (default), straight, or loop',
        },
        cornerAngleLimit: {
          type: 'number',
          description: 'Ignore corners whose angle exceeds this (MillData.EngraveCornerAngleLimit, degrees)',
        },
        safeRapidLevel: { type: 'number', description: 'Z level for rapids. Default 1.0.' },
        rapidDownTo: { type: 'number', description: 'Z level to rapid down to before feeding. Default 0.25.' },
        feedRate: { type: 'number', description: 'Feed rate' },
        spindleSpeed: { type: 'number', description: 'Spindle RPM' },
      },
      required: ['depth'],
    },
  },
  {
    name: 'alphacam_manual_toolpath',
    description:
      'Create a manual toolpath from explicit XYZ moves (MillData.ManualToolPath → MillManualToolPath). The FIRST move supplies the start point; each later move becomes Add3DRapid (rapid), Add3DLine (feed), or Add3DArcPointCenter (arcCW/arcCCW, center from i/j offsets relative to the PREVIOUS point, G-code style). The path may not have internal rapids — Alphacam splits it into multiple toolpaths if it does. Select a tool first.',
    inputSchema: {
      type: 'object',
      properties: {
        moves: {
          type: 'array',
          description: 'Sequence of tool moves',
          items: {
            type: 'object',
            properties: {
              type: {
                type: 'string',
                enum: ['rapid', 'feed', 'arcCW', 'arcCCW'],
                description: 'Move type',
              },
              x: { type: 'number', description: 'X coordinate' },
              y: { type: 'number', description: 'Y coordinate' },
              z: { type: 'number', description: 'Z coordinate' },
              i: { type: 'number', description: 'Arc center X offset from previous point (arcs only)' },
              j: { type: 'number', description: 'Arc center Y offset from previous point (arcs only)' },
            },
            required: ['type', 'x', 'y', 'z'],
          },
        },
        safeRapidLevel: { type: 'number', description: 'Z level for rapids (MillData.SafeRapidLevel)' },
        feedRate: { type: 'number', description: 'Cut feed rate (MillData.CutFeed)' },
        spindleSpeed: { type: 'number', description: 'Spindle RPM' },
      },
      required: ['moves'],
    },
  },

  // ════════════════════════════════════════════════════════════════
  //  NC OUTPUT
  // ════════════════════════════════════════════════════════════════
  {
    name: 'alphacam_output_nc',
    description:
      'Generate NC code (G-code) for the current drawing using the active post processor (Drawing.OutputNC). Defaults to UNATTENDED mode for the Xilog+ post: the shim patches LicomdirPath\\VBMacros\\Startup\\XilogPlusPost\\XilogPost.ini to set DisplayScreen=0 (skips the operator dialog) plus pre-populates sDX/sDY/sDZ (from the last specify_panel), sBX=0, sBY=0, sBZ=20, sV=10, sField=AD; then runs OutputNC; then restores DisplayScreen=1 so interactive use is unaffected. Set unattended=false to preserve the classic operator-dialog flow. Returns OutputNCStatus (0=success). Select a post processor first with alphacam_select_post if needed.',
    inputSchema: {
      type: 'object',
      properties: {
        path: {
          type: 'string',
          description: 'Output NC file path. Defaults to the drawing path with .nc extension, or %TEMP%\\output.nc if unsaved.',
        },
        outputTo: {
          type: 'integer',
          description: 'Output destination: 0 = disk file only, 1 = machine only, 2 = both, -1 = show dialog. Default -1.',
          enum: [-1, 0, 1, 2],
        },
        visibleOnly: {
          type: 'boolean',
          description: 'True to output visible operations only. Default true.',
        },
        unattended: {
          type: 'boolean',
          description: 'True (default) to patch XilogPost.ini for skip-dialog output, then restore DisplayScreen=1. False leaves the INI alone.',
        },
        machineSection: {
          type: 'string',
          description: 'INI section to patch. Default "Pratix Z2". Names must match the exact section header in XilogPost.ini (e.g. "Pratix-S IN", "Accord 40 FX - Dustin").',
        },
        iniPath: {
          type: 'string',
          description: 'Full path to XilogPost.ini. Default: LicomdirPath\\VBMacros\\Startup\\XilogPlusPost\\XilogPost.ini.',
        },
        sDX: { type: 'number', description: 'Material X dimension. Defaults to last specify_panel sizeX.' },
        sDY: { type: 'number', description: 'Material Y dimension. Defaults to last specify_panel sizeY.' },
        sDZ: { type: 'number', description: 'Material Z (thickness). Defaults to last specify_panel thickness.' },
        sBX: { type: 'number', description: 'Bed X offset. Default 0.' },
        sBY: { type: 'number', description: 'Bed Y offset. Default 0.' },
        sBZ: { type: 'number', description: 'Bed Z offset (spoilboard fudge). Default 20 (out-of-range, overridden at runtime).' },
        sV:  { type: 'number', description: 'Vacuum hold-down setting. Default 10.' },
        sField: { type: 'string', description: 'Machine zone / field. Default "AD".' },
      },
    },
  },
  {
    name: 'alphacam_list_nc',
    description:
      'List/display the generated NC code (Drawing.ListNC). Shows the G-code in the Alphacam NC listing window.',
    inputSchema: {
      type: 'object',
      properties: {},
    },
  },
  {
    name: 'alphacam_select_post',
    description:
      'Select a post processor (App.SelectPost). The post processor determines the G-code dialect for NC output. Omit fileName to default to the user\'s XilogPlus.arb post — the shim probes several candidate locations on disk (active post\'s directory, LicomdirPath, LicomdirPath\\LICOMDIR, and the LICOMDIR sibling of LicomdatPath) and uses the first that exists, dodging the LicomdirPath case/segment mismatch on some installs. Returns the active post filename.',
    inputSchema: {
      type: 'object',
      properties: {
        fileName: {
          type: 'string',
          description: 'Full path to a post processor file (.arb). Omit to use the XilogPlus default.',
        },
      },
    },
  },

  // ════════════════════════════════════════════════════════════════
  //  OPERATIONS MANAGEMENT
  //  Operations group toolpaths. Each operation has sub-operations.
  // ════════════════════════════════════════════════════════════════
  {
    name: 'alphacam_list_operations',
    description:
      'List all machining operations in the drawing (Drawing.Operations collection). Each operation contains sub-operations with individual toolpaths. Returns: name, tool info, process type, sub-operation count.',
    inputSchema: {
      type: 'object',
      properties: {},
    },
  },
  {
    name: 'alphacam_get_operation_info',
    description:
      'Get detailed info about a machining operation: its sub-operations (name, times, feed distances, toolpath/geometry counts) and tool assignment. Sub-operation names reflect the process (AcamProcessType: 1=ROUGH_FINISH, 2=CONTOUR_POCKET, 3=MANUAL, 4=SPIRAL_POCKET, 5=LINEAR_POCKET, 10=ENGRAVE, 21=DRILL, 22=PECK, 23=TAP, 24=BORE).',
    inputSchema: {
      type: 'object',
      properties: {
        id: {
          type: 'string',
          description: 'Operation ID (from alphacam_list_operations)',
        },
      },
      required: ['id'],
    },
  },

  // ════════════════════════════════════════════════════════════════
  //  MATERIAL & WORKPIECE
  // ════════════════════════════════════════════════════════════════
  {
    name: 'alphacam_set_material',
    description:
      'Set the active workpiece material — Alphacam\'s "Set Material" command. Sets feeds/speeds source, simulation stock properties, etc. Two modes: (a) pass `name` alone → Drawing.SelectMaterial(name) looks it up in Alphacam\'s material data file (e.g. MMAT.DAT). (b) pass any of `speed`/`inch`/`kFactor` (with optional `name`) → creates a custom Material and calls Drawing.SetMaterial. If both are supplied, custom wins. Returns the active material\'s name/speed/inch/kFactor after the call. For workpiece dimensions / panel geometry use alphacam_specify_panel instead.',
    inputSchema: {
      type: 'object',
      properties: {
        name: {
          type: 'string',
          description: 'Material name. Library mode: must match an entry in the material data file. Custom mode: sets Material.Name on the newly created object.',
        },
        speed: {
          type: 'number',
          description: 'Surface speed (Material.Speed). Units depend on `inch`: feet/minute when inch=true, metres/minute when false.',
        },
        inch: {
          type: 'boolean',
          description: 'True if speed units are feet/minute; false for metres/minute (Material.Inch).',
        },
        kFactor: {
          type: 'number',
          description: 'K-factor for bending (Material.KFactor). Rarely needed for milling/routing.',
        },
      },
    },
  },
  {
    name: 'alphacam_specify_material',
    description:
      'Creates a rectangle of sizeX × sizeY at optional offset and calls Path.SetMaterial(topZ, bottomZ) on it to designate that geometry as material — used for solid simulation, roughing bounds, etc. Mirrors alphacam_specify_panel but calls SetMaterial instead of SetPanel. Defaults: topZ=0, bottomZ=-thickness. Returns the material geometryId. For material TYPE (Plywood, MDF, etc.) use alphacam_set_material. For panel designation use alphacam_specify_panel.',
    inputSchema: {
      type: 'object',
      properties: {
        sizeX: { type: 'number', description: 'Material width (X dimension)' },
        sizeY: { type: 'number', description: 'Material height (Y dimension)' },
        thickness: { type: 'number', description: 'Material thickness (Z). Default bottomZ = topZ - thickness.' },
        offsetX: { type: 'number', description: 'X position of lower-left corner. Default 0.' },
        offsetY: { type: 'number', description: 'Y position of lower-left corner. Default 0.' },
        topZ: { type: 'number', description: 'Z coordinate of top of material. Default 0.' },
        bottomZ: { type: 'number', description: 'Z coordinate of bottom of material. Default topZ - thickness.' },
      },
      required: ['sizeX', 'sizeY', 'thickness'],
    },
  },
  {
    name: 'alphacam_specify_panel',
    description:
      'Router "Specify Panel" workflow. Creates a rectangle of sizeX × sizeY at optional offset and calls Path.SetPanel(topZ, bottomZ) on it to designate the workpiece geometry. Defaults: topZ=0 (material surface at Z=0), bottomZ=-thickness. Returns the panel geometryId. For material TYPE (Plywood, feeds/speeds) use alphacam_set_material. For material geometry designation use alphacam_specify_material.',
    inputSchema: {
      type: 'object',
      properties: {
        sizeX: { type: 'number', description: 'Panel width (X dimension)' },
        sizeY: { type: 'number', description: 'Panel height (Y dimension)' },
        thickness: { type: 'number', description: 'Panel thickness (Z). Default bottomZ = topZ - thickness.' },
        offsetX: { type: 'number', description: 'X position of lower-left corner. Default 0.' },
        offsetY: { type: 'number', description: 'Y position of lower-left corner. Default 0.' },
        topZ: { type: 'number', description: 'Z coordinate of top of material. Default 0.' },
        bottomZ: { type: 'number', description: 'Z coordinate of bottom of material. Default topZ - thickness.' },
      },
      required: ['sizeX', 'sizeY', 'thickness'],
    },
  },

  // ════════════════════════════════════════════════════════════════
  //  WORK PLANES
  // ════════════════════════════════════════════════════════════════
  {
    name: 'alphacam_manage_workplane',
    description:
      'Manage work planes for multi-sided machining. Actions: list (all work planes + current), create (from three points: origin P1, +X axis point P2, XY-plane point P3 — Drawing.CreateWorkPlane), set (make a plane current, by name or workPlaneId), set_origin (WorkPlane.SetOrigin on the current or named plane), cancel (Drawing.CancelWorkPlane — back to global coordinates).',
    inputSchema: {
      type: 'object',
      properties: {
        action: {
          type: 'string',
          enum: ['list', 'create', 'set', 'set_origin', 'cancel'],
          description: 'Work plane operation',
        },
        name: { type: 'string', description: 'Work plane name (for create naming, or set/set_origin lookup)' },
        workPlaneId: { type: 'string', description: 'Cached work plane id from a previous list/create (alternative to name)' },
        x1: { type: 'number', description: 'Origin X (for create)' },
        y1: { type: 'number', description: 'Origin Y (for create)' },
        z1: { type: 'number', description: 'Origin Z (for create)' },
        x2: { type: 'number', description: 'Point on local +X axis: X (for create)' },
        y2: { type: 'number', description: 'Point on local +X axis: Y (for create)' },
        z2: { type: 'number', description: 'Point on local +X axis: Z (for create)' },
        x3: { type: 'number', description: 'Point in local XY plane, +Y side: X (for create)' },
        y3: { type: 'number', description: 'Point in local XY plane, +Y side: Y (for create)' },
        z3: { type: 'number', description: 'Point in local XY plane, +Y side: Z (for create)' },
        setCurrent: { type: 'boolean', description: 'Make the newly created plane current (for create). Default false.' },
        originX: { type: 'number', description: 'New origin X (for set_origin)' },
        originY: { type: 'number', description: 'New origin Y (for set_origin)' },
        originZ: { type: 'number', description: 'New origin Z (for set_origin)' },
      },
      required: ['action'],
    },
  },

  // ════════════════════════════════════════════════════════════════
  //  GEOMETRY EDITING & TRANSFORMS (added 2026-07-20)
  // ════════════════════════════════════════════════════════════════
  {
    name: 'alphacam_transform_geometry',
    description:
      'Move, rotate, mirror, or scale one or more geometries (Path.MoveL/MoveG, RotateL, MirrorL, ScaleL2). Set copy=true to transform a COPY and keep the original. Set copies=N to build an ARRAY: each successive copy is the previous one transformed again — e.g. move dx=2 copies=5 makes a linear array on 2" centers; rotate angle=60 copies=5 around a base point makes a rotary pattern. Angles are degrees, CW negative. Returns the affected/created geometry ids.',
    inputSchema: {
      type: 'object',
      properties: {
        selected: { type: 'boolean', description: 'Act on the geometries currently SELECTED in the Alphacam window (reads Path.Selected).' },
        interactive: { type: 'boolean', description: 'Have Alphacam PROMPT the user to pick geometries (blocks until they finish with Escape).' },
        prompt: { type: 'string', description: 'Prompt-bar text for interactive mode.' },
        geometryId: { type: 'string', description: 'Single geometry to transform' },
        geometryIds: { type: 'array', items: { type: 'string' }, description: 'Multiple geometries to transform' },
        operation: {
          type: 'string',
          enum: ['move', 'rotate', 'mirror', 'scale'],
          description: 'Which transform to apply',
        },
        dx: { type: 'number', description: 'Move: X distance' },
        dy: { type: 'number', description: 'Move: Y distance' },
        dz: { type: 'number', description: 'Move: Z distance (uses global 3D move when nonzero)' },
        angle: { type: 'number', description: 'Rotate: angle in degrees, CW negative' },
        baseX: { type: 'number', description: 'Rotate/scale: base point X. Default 0.' },
        baseY: { type: 'number', description: 'Rotate/scale: base point Y. Default 0.' },
        x1: { type: 'number', description: 'Mirror: line point 1 X' },
        y1: { type: 'number', description: 'Mirror: line point 1 Y' },
        x2: { type: 'number', description: 'Mirror: line point 2 X' },
        y2: { type: 'number', description: 'Mirror: line point 2 Y' },
        factor: { type: 'number', description: 'Scale: uniform factor' },
        factorX: { type: 'number', description: 'Scale: X factor (overrides factor)' },
        factorY: { type: 'number', description: 'Scale: Y factor (overrides factor)' },
        copy: { type: 'boolean', description: 'Transform a copy, keep the original. Default false.' },
        copies: { type: 'integer', description: 'Number of chained copies (array pattern). Implies copy=true.' },
      },
      required: ['operation'],
    },
  },
  {
    name: 'alphacam_offset_geometry',
    description:
      'Offset geometry by a distance (Path.Offset) — the CAD offset command, used for clearances, inlay gaps, onion skins. side is relative to the PATH DIRECTION (left/right): for a CW closed path, left = outside. Check alphacam_get_geometry_info / reverse_geometry if direction matters. Returns the new offset geometry ids; optionally deletes the originals.',
    inputSchema: {
      type: 'object',
      properties: {
        selected: { type: 'boolean', description: 'Act on the geometries currently SELECTED in the Alphacam window (reads Path.Selected).' },
        interactive: { type: 'boolean', description: 'Have Alphacam PROMPT the user to pick geometries (blocks until they finish with Escape).' },
        prompt: { type: 'string', description: 'Prompt-bar text for interactive mode.' },
        geometryId: { type: 'string', description: 'Single geometry to offset' },
        geometryIds: { type: 'array', items: { type: 'string' }, description: 'Multiple geometries to offset' },
        distance: { type: 'number', description: 'Offset distance in drawing units' },
        side: { type: 'string', enum: ['left', 'right'], description: 'Side relative to path direction. Default left.' },
        deleteOriginal: { type: 'boolean', description: 'Delete the source geometry after offsetting. Default false.' },
      },
      required: ['distance'],
    },
  },
  {
    name: 'alphacam_fillet_geometry',
    description:
      'Fillet (radius) or chamfer all EXTERNAL sharp corners on geometry (Path.Fillet / Path.Chamfer). Internal corners are left as-is — which corners count as external depends on the path direction (CW/CCW). Reverse the geometry first if the wrong corners get rounded.',
    inputSchema: {
      type: 'object',
      properties: {
        selected: { type: 'boolean', description: 'Act on the geometries currently SELECTED in the Alphacam window (reads Path.Selected).' },
        interactive: { type: 'boolean', description: 'Have Alphacam PROMPT the user to pick geometries (blocks until they finish with Escape).' },
        prompt: { type: 'string', description: 'Prompt-bar text for interactive mode.' },
        geometryId: { type: 'string', description: 'Single geometry' },
        geometryIds: { type: 'array', items: { type: 'string' }, description: 'Multiple geometries' },
        mode: { type: 'string', enum: ['fillet', 'chamfer'], description: 'Default fillet.' },
        radius: { type: 'number', description: 'Fillet radius' },
        dist1: { type: 'number', description: 'Chamfer: first distance' },
        dist2: { type: 'number', description: 'Chamfer: second distance. Default = dist1.' },
      },
    },
  },
  {
    name: 'alphacam_join_geometries',
    description:
      'Join geometries end-to-end into continuous paths (Drawing.Join) — ESSENTIAL after DXF imports, which typically arrive as hundreds of disconnected lines/arcs. Pass geometryIds, or all=true to join everything in the drawing. The input geometries are CONSUMED: their ids become invalid; use the returned ids.',
    inputSchema: {
      type: 'object',
      properties: {
        selected: { type: 'boolean', description: 'Act on the geometries currently SELECTED in the Alphacam window (reads Path.Selected).' },
        interactive: { type: 'boolean', description: 'Have Alphacam PROMPT the user to pick geometries (blocks until they finish with Escape).' },
        prompt: { type: 'string', description: 'Prompt-bar text for interactive mode.' },
        geometryIds: { type: 'array', items: { type: 'string' }, description: 'Geometries to join' },
        all: { type: 'boolean', description: 'Join ALL geometries in the drawing. Default false.' },
      },
    },
  },
  {
    name: 'alphacam_explode_geometry',
    description:
      'Explode geometry into separate single-element paths (Path.Explode) — each line/arc becomes its own geometry. The original is deleted; its id becomes invalid. Returns the new ids.',
    inputSchema: {
      type: 'object',
      properties: {
        selected: { type: 'boolean', description: 'Act on the geometries currently SELECTED in the Alphacam window (reads Path.Selected).' },
        interactive: { type: 'boolean', description: 'Have Alphacam PROMPT the user to pick geometries (blocks until they finish with Escape).' },
        prompt: { type: 'string', description: 'Prompt-bar text for interactive mode.' },
        geometryId: { type: 'string', description: 'Single geometry to explode' },
        geometryIds: { type: 'array', items: { type: 'string' }, description: 'Multiple geometries to explode' },
      },
    },
  },
  {
    name: 'alphacam_get_extent',
    description:
      'Get the bounding box of the whole drawing (Drawing.GetExtent): min/max global X, Y, Z plus computed sizes and entity counts. Use to sanity-check part size against the panel, or before placing inserted parts.',
    inputSchema: {
      type: 'object',
      properties: {},
    },
  },
  {
    name: 'alphacam_export_file',
    description:
      'Export the current drawing to an interchange format: dxf (SaveDxfFile), emf/wmf (vector picture of the drawing — useful to SEE the current drawing state), stl, iges. Format inferred from the path extension if omitted.',
    inputSchema: {
      type: 'object',
      properties: {
        path: { type: 'string', description: 'Destination file path' },
        format: { type: 'string', enum: ['dxf', 'emf', 'wmf', 'stl', 'iges'], description: 'Export format. Inferred from extension if omitted.' },
        visibleOnly: { type: 'boolean', description: 'DXF/IGES: export visible entities only. Default true.' },
        decimalPlaces: { type: 'integer', description: 'DXF: decimal places. Default 4.' },
        drawAxes: { type: 'boolean', description: 'EMF/WMF: include axes. Default false.' },
        drawBackground: { type: 'boolean', description: 'EMF/WMF: include background. Default false.' },
        stlType: { type: 'integer', description: 'STL: 1=STL models (default), 2=facet surfaces, 4=facet parasolid' },
        tolerance: { type: 'number', description: 'STL: facet tolerance. Default 0.01.' },
      },
      required: ['path'],
    },
  },
  {
    name: 'alphacam_insert_drawing',
    description:
      'Insert another Alphacam drawing file (part) into the current drawing at an XY(Z) offset (Drawing.InsertDrawing) — the manual sheet-layout / nesting workflow: specify a panel, then insert part files at chosen positions. Returns the ids of every geometry the insert added.',
    inputSchema: {
      type: 'object',
      properties: {
        path: { type: 'string', description: 'Full path to the .ard/.amd drawing to insert' },
        x: { type: 'number', description: 'X offset for the inserted drawing. Default 0.' },
        y: { type: 'number', description: 'Y offset. Default 0.' },
        z: { type: 'number', description: 'Z offset. Default 0.' },
      },
      required: ['path'],
    },
  },
  {
    name: 'alphacam_set_tool_side_auto',
    description:
      "Set tool sides automatically for a batch of geometries (Drawing.SetToolSideAuto) using nesting logic. mode=cut: tool OUTSIDE the outermost profiles, INSIDE inner ones (cutting out parts that have holes). mode=pocket: the reverse (pocketing with islands). Applies to the given geometryIds, or ALL geometries if none given. Much faster than calling set_tool_side per geometry.",
    inputSchema: {
      type: 'object',
      properties: {
        selected: { type: 'boolean', description: 'Act on the geometries currently SELECTED in the Alphacam window (reads Path.Selected).' },
        interactive: { type: 'boolean', description: 'Have Alphacam PROMPT the user to pick geometries (blocks until they finish with Escape).' },
        prompt: { type: 'string', description: 'Prompt-bar text for interactive mode.' },
        mode: { type: 'string', enum: ['cut', 'pocket'], description: 'Auto tool-side rule. Default cut.' },
        geometryId: { type: 'string', description: 'Single geometry' },
        geometryIds: { type: 'array', items: { type: 'string' }, description: 'Geometries to process. Omit for all.' },
      },
    },
  },
  {
    name: 'alphacam_user_select',
    description:
      "INTERACTIVE: ask the human at the Alphacam window to pick geometries (single clicks or a selection window), then finish with Escape/right-click. Returns the picked geometry ids for use in machining/editing calls. BLOCKS until the user finishes (5-minute timeout) — tell the user what to select BEFORE calling this, and only call it when they are at the machine.",
    inputSchema: {
      type: 'object',
      properties: {
        prompt: { type: 'string', description: 'Text shown in the Alphacam prompt bar' },
      },
    },
  },

  // ════════════════════════════════════════════════════════════════
  //  NESTING (added 2026-07-20)
  //  Workflow: save_part (extract parts from the drawing to .ard files)
  //  → create_nest_list (.anl with quantities/rotation rules)
  //  → nest (add sheets, set gap/margin, DoNest into the active drawing).
  // ════════════════════════════════════════════════════════════════
  {
    name: 'alphacam_order_nested_toolpaths',
    description:
      "Run 'Order Toolpaths in Nested Sheets' (Drawing.OrderToolPathsInNestedSheets) — REQUIRED after a multi-sheet nest and BEFORE alphacam_output_nc, so toolpaths are sequenced sheet by sheet. mode=current uses OrderToolPathsInCurrentNestedSheetOrder (keep the current sheet order) instead.",
    inputSchema: {
      type: 'object',
      properties: {
        mode: { type: 'string', enum: ['nested', 'current'], description: 'nested (default) = standard nested-sheet ordering; current = keep current sheet order.' },
      },
    },
  },
  {
    name: 'alphacam_save_part',
    description:
      'Extract geometries to their own .ard part file WITHOUT changing the working drawing (copies them through an invisible temp drawing — App.CreateTempDrawing + Path.MoveToDrawing). Source: explicit ids, selected:true (current Alphacam selection), or interactive:true (Alphacam prompts the user). This is how parts are produced for nesting; also useful to split a part out of any drawing.',
    inputSchema: {
      type: 'object',
      properties: {
        path: { type: 'string', description: 'Destination .ard file path for the part' },
        geometryId: { type: 'string', description: 'Single geometry' },
        geometryIds: { type: 'array', items: { type: 'string' }, description: 'Geometries that make up the part (outline + holes etc.)' },
        selected: { type: 'boolean', description: 'Use the geometries currently selected in Alphacam.' },
        interactive: { type: 'boolean', description: 'Have Alphacam prompt the user to pick the part geometries.' },
        prompt: { type: 'string', description: 'Prompt-bar text for interactive mode.' },
      },
      required: ['path'],
    },
  },
  {
    name: 'alphacam_create_nest_list',
    description:
      "Write an Alphacam nest list (.anl) in the modern $VERSION 3.60 format. Each item is a PART FILE (.ard, e.g. from alphacam_save_part) with quantity, priority, rotation step (e.g. 90 = quarter turns, 0 = fixed), mirror, extraPartGap, maxPerSheet. Setup: gap between parts, sheetEdgeGap ('Gap at Sheet Edge' — keeps parts off the sheet edges), lead gap, pack direction (8-way), true-shape vs rectangular, time limits, toolPaths mode. Sheets are NOT stored in the list — add them at nest time (alphacam_nest) from drawing geometry.",
    inputSchema: {
      type: 'object',
      properties: {
        path: { type: 'string', description: 'Destination .anl file path (convention: LICOMDIR\\NestLists\\)' },
        items: {
          type: 'array',
          description: 'Parts to nest',
          items: {
            type: 'object',
            properties: {
              path: { type: 'string', description: 'Full path to the part .ard file' },
              quantity: { type: 'integer', description: 'How many to nest. Default 1.' },
              priority: { type: 'integer', description: 'Nesting order (1 = first). Default: item order.' },
              rotation: { type: 'number', description: 'Allowed rotation step in degrees (0 = fixed, 90 = quarter turns, etc.). Default 90.' },
              mirror: { type: 'boolean', description: 'Allow mirroring. Default false.' },
              tryRotatedFirst: { type: 'boolean', description: 'Try the rotated orientation first. Default false.' },
              maxPerSheet: { type: 'integer', description: 'Maximum of this part per sheet. Default 0 (no limit).' },
              extraPartGap: { type: 'number', description: 'Extra gap around this specific part. Default 0.' },
              kitNumber: { type: 'integer', description: 'Kit number (0 = none).' },
            },
            required: ['path'],
          },
        },
        gap: { type: 'number', description: 'Gap between parts. Default 0.25.' },
        sheetEdgeGap: { type: 'number', description: "Gap at Sheet Edge — parts keep this far from every sheet edge. Default 0. (This replaced the old nest sheetMargin param.)" },
        leadGap: { type: 'number', description: 'Extra lead-in gap. Default 0.' },
        toolPaths: { description: 'true/1 = nest toolpaths (default), false/0 = bare geometry, 2 = toolpaths with enclosed geometries. MUST match the part files: geometry-only parts (e.g. from save_part, which copies geometry not toolpaths) need false/0, or CreateNestData fails with "Failed to Load Nest List".' },
        nestingMethod: { type: 'string', enum: ['trueshape', 'rect'], description: 'True-shape (VERONEST, default) or rectangular nesting.' },
        subroutines: { type: 'boolean', description: 'Use NC subroutines. Default false.' },
        direction: { description: 'Pack direction: left/bottom/right/top/top-left/bottom-left/bottom-right/top-right or 0-7. Default bottom-left (5, the UI default).' },
        resolution: { type: 'number', description: 'Search resolution. Default 0.1.' },
        timePerSheet: { type: 'number', description: 'Engine time limit per sheet, seconds. Default 2.' },
        totalTime: { type: 'number', description: 'Engine total time limit, seconds. Default 30.' },
        sheetOrder: { type: 'integer', description: '0=best utilisation (default), 1=picked order.' },
        strictPriorities: { type: 'boolean', description: 'Force strict part priorities. Default false.' },
        evenlySpaced: { type: 'boolean', description: 'Evenly spaced parts. Default false.' },
      },
      required: ['path', 'items'],
    },
  },
  {
    name: 'alphacam_nest',
    description:
      "Run a nest into the ACTIVE drawing (Drawing.CreateNestData + NestData.AddSheet + DoNest). Reads parts/quantities/rotation from a .anl nest list (alphacam_create_nest_list). Sheets come from drawing geometry (pass a sheet geometryId) or are created as rectangles (width/height at optional x/y) — each with material name, thickness, and available quantity. Sheet-edge clearance comes from the nest list's sheetEdgeGap ('Gap at Sheet Edge') — set it in alphacam_create_nest_list. gap/leadGap/resolution/direction override the list's setup. TIP: run in a fresh or dedicated drawing — parts nest in wherever DoNest runs.",
    inputSchema: {
      type: 'object',
      properties: {
        nestListPath: { type: 'string', description: 'Path to the .anl nest list' },
        sheets: {
          type: 'array',
          description: 'Sheets to nest onto (in order)',
          items: {
            type: 'object',
            properties: {
              geometryId: { type: 'string', description: 'Existing closed geometry to use as the sheet boundary' },
              width: { type: 'number', description: 'Sheet width (creates a rectangle when no geometryId)' },
              height: { type: 'number', description: 'Sheet height' },
              x: { type: 'number', description: 'Sheet lower-left X. Default 0.' },
              y: { type: 'number', description: 'Sheet lower-left Y. Default 0.' },
              material: { type: 'string', description: 'Material name. Default: the drawing\'s current material.' },
              thickness: { type: 'number', description: 'Sheet thickness. Default 0.75.' },
              quantity: { type: 'integer', description: 'How many of this sheet are available. Default 1.' },
            },
          },
        },
        gap: { type: 'number', description: 'Minimum gap between parts (NestData.Gap).' },
        leadGap: { type: 'number', description: 'Extra gap for leads (NestData.LeadGap).' },
        resolution: { type: 'number', description: 'Nesting resolution (NestData.Resolution).' },
        direction: { description: 'Pack direction: left/bottom/right/top or 0-3.' },
        subroutines: { type: 'boolean', description: 'Use NC subroutines.' },
      },
      required: ['nestListPath', 'sheets'],
    },
  },
];

/**
 * Map from MCP tool name (underscores) to bridge method name (dots).
 * e.g. "alphacam_new" -> "alphacam.new"
 * CRITICAL: Uses string replace (not regex) so only the FIRST underscore
 * becomes a dot. "alphacam_get_drawing_info" -> "alphacam.get_drawing_info"
 */
export function toBridgeMethod(mcpToolName) {
  // Only replace the first underscore: "alphacam_get_drawing_info" -> "alphacam.get_drawing_info"
  return mcpToolName.replace('_', '.');
}

/**
 * Map from bridge method name (dots) to MCP tool name (underscores).
 * e.g. "alphacam.new" -> "alphacam_new"
 */
export function toMcpToolName(bridgeMethod) {
  return bridgeMethod.replace('.', '_');
}

/**
 * Build a lookup map of tool name -> definition.
 */
export function getToolMap() {
  const map = new Map();
  for (const tool of TOOL_DEFINITIONS) {
    map.set(tool.name, tool);
  }
  return map;
}
