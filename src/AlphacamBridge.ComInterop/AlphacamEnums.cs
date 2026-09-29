namespace AlphacamBridge.ComInterop;

/// <summary>
/// Managed representation of Alphacam COM drill types.
/// </summary>
public enum AcamDrillType
{
    /// <summary>Regular drill operation</summary>
    Drill = 0,
    /// <summary>Peck drill (withdraw to clear chips)</summary>
    Peck = 1,
    /// <summary>Tap operation</summary>
    Tap = 2,
    /// <summary>Bore operation</summary>
    Bore = 3,
    /// <summary>Chamfer operation</summary>
    Chamfer = 4
}

/// <summary>
/// Managed representation of Alphacam COM lead side options.
/// </summary>
public enum AcamLeadSide
{
    /// <summary>Left side lead</summary>
    Left = 0,
    /// <summary>Center lead</summary>
    Center = 1,
    /// <summary>Right side lead</summary>
    Right = 2
}

/// <summary>
/// Managed representation of Alphacam COM lead type options.
/// </summary>
public enum AcamLeadType
{
    /// <summary>Linear lead</summary>
    Line = 0,
    /// <summary>Arc lead</summary>
    Arc = 1,
    /// <summary>Both linear and arc lead</summary>
    Both = 2,
    /// <summary>No lead</summary>
    None = 3,
    /// <summary>No change to lead</summary>
    NoChange = 4,
    /// <summary>Both lead types but not tangential</summary>
    BothNotTangential = 10
}

/// <summary>
/// Managed representation of Alphacam license levels.
/// </summary>
public enum AcamLevel
{
    /// <summary>Basic level</summary>
    Basic = 1,
    /// <summary>Standard level</summary>
    Standard = 2,
    /// <summary>Advanced level</summary>
    Advanced = 3,
    /// <summary>Advanced 3D 3-axis</summary>
    Advanced3D3Axis = 4,
    /// <summary>Advanced 3D 5-axis</summary>
    Advanced3D5Axis = 5,
    /// <summary>ViewPlus level</summary>
    ViewPlus = 6,
    /// <summary>OEM 5-axis</summary>
    OEM5Axis = 8,
    /// <summary>Express level</summary>
    Express = 9
}

/// <summary>
/// Managed representation of Alphacam COM process types (machining operations).
/// </summary>
public enum AcamProcessType
{
    /// <summary>Rough and finish operation</summary>
    RoughFinish = 1,
    /// <summary>Contour pocket operation</summary>
    ContourPocket = 2,
    /// <summary>Manual operation</summary>
    Manual = 3,
    /// <summary>Spiral pocket operation</summary>
    SpiralPocket = 4,
    /// <summary>Linear pocket operation</summary>
    LinearPocket = 5,
    /// <summary>Engrave operation</summary>
    Engrave = 10,
    /// <summary>Machine surface operation</summary>
    MachineSurface = 11,
    /// <summary>Machine polyline operation</summary>
    MachinePolyline = 12,
    /// <summary>Drill operation</summary>
    Drill = 21,
    /// <summary>Peck drill operation</summary>
    Peck = 22,
    /// <summary>Tap operation</summary>
    Tap = 23,
    /// <summary>Bore operation</summary>
    Bore = 24
}

/// <summary>
/// Managed representation of Alphacam COM tool types.
/// </summary>
public enum AcamToolType
{
    /// <summary>Flat end mill</summary>
    Flat = 0,
    /// <summary>Ball end mill</summary>
    Ball = 1,
    /// <summary>Bull nose end mill</summary>
    BullNose = 2,
    /// <summary>Chamfer tool</summary>
    Chamfer = 3,
    /// <summary>Drill bit</summary>
    Drill = 4,
    /// <summary>Tap tool</summary>
    Tap = 5,
    /// <summary>User-defined tool</summary>
    User = 6,
    /// <summary>Boring bar</summary>
    Boring = 7,
    /// <summary>Face mill</summary>
    FaceMill = 8,
    /// <summary>Thread mill</summary>
    ThreadMill = 9
}

/// <summary>
/// Managed representation of Alphacam file types.
/// </summary>
public enum AcamFileType
{
    /// <summary>Drawing file</summary>
    Drawing = 1,
    /// <summary>Font file</summary>
    Font = 2,
    /// <summary>NC code file</summary>
    NC = 3,
    /// <summary>Post processor file</summary>
    Post = 4,
    /// <summary>Tool library file</summary>
    Tool = 5
}

/// <summary>
/// Managed representation of Alphacam file action types.
/// </summary>
public enum AcamFileAction
{
    /// <summary>Open file action</summary>
    Open = 1,
    /// <summary>Save file action</summary>
    Save = 2
}

/// <summary>
/// Managed representation of Alphacam view types.
/// </summary>
public enum AcamView
{
    /// <summary>Top view (XY plane)</summary>
    Top = 1,
    /// <summary>Front view (XZ plane)</summary>
    Front = 2,
    /// <summary>Back view</summary>
    Back = 3,
    /// <summary>Left view (YZ plane)</summary>
    Left = 4,
    /// <summary>Right view</summary>
    Right = 5,
    /// <summary>Bottom view</summary>
    Bottom = 6,
    /// <summary>Isometric 3D view</summary>
    Isometric = 7
}

/// <summary>
/// Managed representation of Alphacam path types.
/// </summary>
public enum AcamPathType
{
    /// <summary>Geometry path (CAD geometry)</summary>
    Geometry = 1,
    /// <summary>Tool path (machine code path)</summary>
    ToolPath = 2
}

/// <summary>
/// Managed representation of Alphacam tool compensation modes.
/// </summary>
public enum AcamCompensation
{
    /// <summary>No tool compensation</summary>
    None = 0,
    /// <summary>Left-hand tool compensation</summary>
    Left = 1,
    /// <summary>Right-hand tool compensation</summary>
    Right = 2,
    /// <summary>Machine center tool compensation</summary>
    MC = 3
}

/// <summary>
/// Managed representation of Alphacam surface machining methods.
/// </summary>
public enum AcamSurfaceMCMethod
{
    /// <summary>Machine in XY plane</summary>
    XY = 1,
    /// <summary>Machine along parameter lines on surface</summary>
    ParameterLines = 2,
    /// <summary>Machine with contour lines</summary>
    Contour = 3,
    /// <summary>Machine with spiral pattern</summary>
    Spiral = 4
}

/// <summary>
/// Managed representation of Alphacam surface machining actions.
/// </summary>
public enum AcamSurfaceMCAction
{
    /// <summary>Machine the surface</summary>
    MCSurfaces = 1,
    /// <summary>Roughing operation</summary>
    Roughing = 2,
    /// <summary>Tool side operation</summary>
    ToolSide = 3,
    /// <summary>Finishing operation</summary>
    Finishing = 4
}
