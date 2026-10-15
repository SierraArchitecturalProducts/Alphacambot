using AlphaCAMMill;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Path = System.IO.Path;

namespace CambotAlphacam
{
    internal sealed class ComCommandServer : IDisposable
    {
        private const string PipeName = "AlphacamBridgeCmd";

        private readonly IAlphaCamApp _app;
        private readonly Control _marshalControl;
        private readonly JavaScriptSerializer _json;
        private readonly Dictionary<string, object> _objectCache;
        private readonly Dictionary<IntPtr, string> _idByUnknown;
        private readonly Dictionary<string, List<object>> _designationHelpers;
        // Cached from the most recent specify_panel call, so output_nc can
        // patch the XilogPost.ini material dimensions (sDX/sDY/sDZ) without
        // the caller having to re-supply them.
        private double? _lastPanelSizeX;
        private double? _lastPanelSizeY;
        private double? _lastPanelThickness;
        // True once specify_panel / specify_material ran in this session (plan checks).
        private bool _materialDefined;
        private readonly string _logPath;

        private Thread _listenThread;
        private volatile bool _stopping;
        private volatile NamedPipeServerStream _currentPipe;
        private int _nextObjectId = 1;

        public ComCommandServer(IAlphaCamApp app, string logPath)
        {
            _app = app;
            _logPath = logPath;
            _json = new JavaScriptSerializer { MaxJsonLength = 10 * 1024 * 1024 };
            _objectCache = new Dictionary<string, object>();
            _idByUnknown = new Dictionary<IntPtr, string>();
            _designationHelpers = new Dictionary<string, List<object>>();
            _marshalControl = new Control();
            _marshalControl.CreateControl();
        }

        public void Start()
        {
            _listenThread = new Thread(ListenLoop) { Name = "AlphacamBridgeCmd-Listener", IsBackground = true };
            _listenThread.Start();
            Log("ComCommandServer started on pipe: " + PipeName);
        }

        public void Dispose()
        {
            _stopping = true;
            // Dispose the active pipe stream to unblock WaitForConnection /
            // a pending read, so the listener thread actually stops instead
            // of keeping the launcher connection open (orphan-Acam fix).
            try { _currentPipe?.Dispose(); } catch { }
            _currentPipe = null;
            try { _marshalControl?.Dispose(); } catch { }
            foreach (var unk in _idByUnknown.Keys) { try { Marshal.Release(unk); } catch { } }
            _idByUnknown.Clear();
            _objectCache.Clear();
            _designationHelpers.Clear();
        }

        private void ListenLoop()
        {
            while (!_stopping)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                    _currentPipe = pipe;
                    Log("Waiting for Launcher connection...");
                    pipe.WaitForConnection();
                    Log("Launcher connected");
                    HandleClient(pipe);
                }
                catch (Exception ex) { if (!_stopping) Log("ListenLoop error: " + ex.Message); }
                finally { _currentPipe = null; try { pipe?.Dispose(); } catch { } }
                if (!_stopping) Thread.Sleep(500);
            }
        }

        private void HandleClient(NamedPipeServerStream pipe)
        {
            try
            {
                while (pipe.IsConnected && !_stopping)
                {
                    var lenBuf = new byte[4];
                    var bytesRead = ReadExact(pipe, lenBuf, 4);
                    if (bytesRead < 4) break;
                    var len = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
                    if (len <= 0 || len > 10 * 1024 * 1024) break;
                    var bodyBuf = new byte[len];
                    bytesRead = ReadExact(pipe, bodyBuf, len);
                    if (bytesRead < len) break;
                    var requestJson = Encoding.UTF8.GetString(bodyBuf);
                    Log("Request: " + (requestJson.Length > 200 ? requestJson.Substring(0, 200) + "..." : requestJson));
                    string responseJson;
                    try { responseJson = ProcessRequest(requestJson); }
                    catch (Exception ex) { responseJson = BuildErrorResponse(0, -32603, "Internal error: " + ex.Message); }
                    Log("Response: " + (responseJson.Length > 200 ? responseJson.Substring(0, 200) + "..." : responseJson));
                    var responseBytes = Encoding.UTF8.GetBytes(responseJson);
                    var respLenBuf = new byte[4];
                    respLenBuf[0] = (byte)(responseBytes.Length >> 24);
                    respLenBuf[1] = (byte)(responseBytes.Length >> 16);
                    respLenBuf[2] = (byte)(responseBytes.Length >> 8);
                    respLenBuf[3] = (byte)(responseBytes.Length);
                    pipe.Write(respLenBuf, 0, 4);
                    pipe.Write(responseBytes, 0, responseBytes.Length);
                    pipe.Flush();
                }
            }
            catch (IOException) { Log("Client disconnected (broken pipe)"); }
            catch (Exception ex) { Log("HandleClient error: " + ex.ToString()); }
        }

        private string ProcessRequest(string requestJson)
        {
            var dict = _json.Deserialize<Dictionary<string, object>>(requestJson);
            var id = dict.ContainsKey("id") ? dict["id"] : null;
            var method = dict.ContainsKey("method") ? (string)dict["method"] : "";
            var prms = dict.ContainsKey("params") ? dict["params"] as Dictionary<string, object> ?? new Dictionary<string, object>() : new Dictionary<string, object>();
            try
            {
                // Job queries are answered on the pipe thread so they get
                // through while a long operation holds the UI thread.
                if (method == "alphacam.job_status") return BuildSuccessResponse(id, JobStatus(prms));

                // background:true on a long-running method → queue it on the
                // UI thread (BeginInvoke), return a job id at once, and let the
                // caller poll alphacam.job_status. Added 2026-09-29 because
                // posting/simulating dense 3D toolpaths outlives the pipe and
                // MCP-client timeouts.
                if (GetBool(prms, "background", false) && BackgroundMethods.Contains(method))
                {
                    var job = StartJob(method, prms);
                    return BuildSuccessResponse(id, job);
                }

                object result = null;
                Exception comError = null;
                _marshalControl.Invoke(new Action(() =>
                {
                    try { result = DispatchMethod(method, prms); }
                    catch (Exception ex) { comError = ex; }
                }));
                if (comError != null) return BuildErrorResponse(id, -32603, comError.GetType().Name + ": " + comError.Message);
                return BuildSuccessResponse(id, result);
            }
            catch (Exception ex) { return BuildErrorResponse(id, -32603, "Marshal error: " + ex.Message); }
        }

        // ── Background jobs ────────────────────────────────────────
        private sealed class JobInfo
        {
            public string Id, Method, State; // running | done | error
            public object Result; public string Error;
            public DateTime Started, Finished;
        }
        private static readonly HashSet<string> BackgroundMethods = new HashSet<string>
        {
            "alphacam.output_nc", "alphacam.machine_3d", "alphacam.simulate", "alphacam.import_cad", "alphacam.nest",
        };
        private readonly Dictionary<string, JobInfo> _jobs = new Dictionary<string, JobInfo>();
        private int _nextJobId = 1;

        private Dictionary<string, object> StartJob(string method, Dictionary<string, object> prms)
        {
            var job = new JobInfo { Id = "job_" + _nextJobId++, Method = method, State = "running", Started = DateTime.Now };
            lock (_jobs) _jobs[job.Id] = job;
            var innerPrms = new Dictionary<string, object>(prms);
            innerPrms.Remove("background");
            _marshalControl.BeginInvoke(new Action(() =>
            {
                try { var r = DispatchMethod(method, innerPrms); lock (_jobs) { job.Result = r; job.State = "done"; job.Finished = DateTime.Now; } }
                catch (Exception ex) { lock (_jobs) { job.Error = ex.GetType().Name + ": " + ex.Message; job.State = "error"; job.Finished = DateTime.Now; } }
                Log("Job " + job.Id + " (" + method + ") " + job.State);
            }));
            return new Dictionary<string, object>
            {
                ["jobId"] = job.Id, ["method"] = method, ["state"] = "running",
                ["message"] = "Started in the background — poll alphacam_job_status with this jobId (every 10-20 s) until state is done or error."
            };
        }

        private object JobStatus(Dictionary<string, object> prms)
        {
            var idStr = GetString(prms, "jobId", "");
            lock (_jobs)
            {
                if (string.IsNullOrEmpty(idStr))
                {
                    var all = new List<object>();
                    foreach (var j in _jobs.Values) all.Add(new Dictionary<string, object> { ["jobId"] = j.Id, ["method"] = j.Method, ["state"] = j.State, ["started"] = j.Started.ToString("s") });
                    return new Dictionary<string, object> { ["jobs"] = all };
                }
                JobInfo job;
                if (!_jobs.TryGetValue(idStr, out job)) throw new ArgumentException("Unknown jobId " + idStr);
                var d = new Dictionary<string, object>
                {
                    ["jobId"] = job.Id, ["method"] = job.Method, ["state"] = job.State,
                    ["elapsedSeconds"] = Math.Round(((job.State == "running" ? DateTime.Now : job.Finished) - job.Started).TotalSeconds, 1)
                };
                if (job.State == "done") d["result"] = job.Result;
                if (job.State == "error") d["error"] = job.Error;
                return d;
            }
        }

        private object DispatchMethod(string method, Dictionary<string, object> prms)
        {
            switch (method)
            {
                case "alphacam.new":
                    _app.New();
                    _designationHelpers.Clear();
                    return new Dictionary<string, object> { ["message"] = "New drawing created" };

                case "alphacam.open_drawing":
                    _app.OpenDrawing(GetString(prms, "path"));
                    _designationHelpers.Clear();
                    return new Dictionary<string, object> { ["path"] = GetString(prms, "path"), ["message"] = "Drawing opened" };

                case "alphacam.save_drawing":
                {
                    dynamic drw = _app.ActiveDrawing;
                    drw.Save();
                    return new Dictionary<string, object> { ["fullName"] = (string)drw.FullName };
                }

                case "alphacam.save_as":
                {
                    dynamic drw = _app.ActiveDrawing;
                    var path = GetString(prms, "path");
                    drw.SaveAs(path);
                    return new Dictionary<string, object> { ["path"] = path };
                }

                case "alphacam.get_drawing_info":
                {
                    dynamic drw = _app.ActiveDrawing;
                    return new Dictionary<string, object>
                    {
                        ["fullName"] = (string)drw.FullName,
                        ["name"] = (string)drw.Name,
                        ["path"] = (string)drw.Path,
                        ["geometryCount"] = (int)drw.GetGeoCount(),
                        ["toolPathCount"] = (int)drw.GetToolPathCount(),
                        ["layerCount"] = (int)drw.GetLayerCount(),
                        ["modified"] = (bool)drw.Modified
                    };
                }

                case "alphacam.output_nc":
                {
                    dynamic drw = _app.ActiveDrawing;
                    string defaultPath = null;
                    try
                    {
                        var fullName = (string)drw.FullName;
                        if (!string.IsNullOrEmpty(fullName)) defaultPath = Path.ChangeExtension(fullName, ".nc");
                    }
                    catch { }
                    if (defaultPath == null) defaultPath = Path.Combine(Path.GetTempPath(), "output.nc");
                    var ncPath = GetString(prms, "path", defaultPath);
                    // Default 0 = file. -1 shows Alphacam's own "Output NC"
                    // dialog (post + File/Machine/Both), which needs a click —
                    // that was the "intervention" on every post of 2026-09-29.
                    var outputTo = (short)GetInt(prms, "outputTo", 0);
                    var visibleOnly = GetBool(prms, "visibleOnly", true);

                    // ── Unattended mode: patch XilogPost.ini before OutputNC.
                    // On the Xilog+ post, DisplayScreen=1 pops a modal dialog
                    // in the operator's workflow. Setting DisplayScreen=0 skips
                    // it, along with pre-populating material dims + zone + vac.
                    // We restore DisplayScreen=1 after so interactive use is
                    // unaffected.
                    bool unattended = GetBool(prms, "unattended", true);
                    string machineSection = GetString(prms, "machineSection", "Pratix Z2");
                    string iniPath = GetString(prms, "iniPath", "");
                    if (unattended && string.IsNullOrEmpty(iniPath))
                    {
                        // Preferred source: XilogPost.ini lives next to the
                        // selected XilogPlus.arb. Since select_post already
                        // validated that path, its directory is the most
                        // reliable place to look (avoids the LicomdirPath
                        // case-mismatch that breaks on some installs).
                        try
                        {
                            var postFile = (string)((dynamic)_app).PostFileName;
                            if (!string.IsNullOrEmpty(postFile))
                            {
                                var dir = Path.GetDirectoryName(postFile);
                                if (!string.IsNullOrEmpty(dir))
                                {
                                    var candidate = Path.Combine(dir, "XilogPost.ini");
                                    if (File.Exists(candidate)) iniPath = candidate;
                                }
                            }
                        }
                        catch { }
                        // Fallback: LicomdirPath (known to case-mismatch on
                        // some installs, but worth a shot if PostFileName
                        // is unset).
                        if (string.IsNullOrEmpty(iniPath))
                        {
                            try
                            {
                                var licomdir = (string)((dynamic)_app).LicomdirPath;
                                if (!string.IsNullOrEmpty(licomdir))
                                    iniPath = Path.Combine(licomdir, "VBMacros", "Startup", "XilogPlusPost", "XilogPost.ini");
                            }
                            catch { }
                        }
                    }
                    bool iniPatched = false;
                    string iniPatchError = null;
                    if (unattended && !string.IsNullOrEmpty(iniPath) && File.Exists(iniPath))
                    {
                        try
                        {
                            var patch = new Dictionary<string, string>();
                            patch["DisplayScreen"] = "0";
                            // Material dims: caller overrides > last panel > skip.
                            double? sdxN = prms.ContainsKey("sDX") ? (double?)GetDouble(prms, "sDX") : _lastPanelSizeX;
                            double? sdyN = prms.ContainsKey("sDY") ? (double?)GetDouble(prms, "sDY") : _lastPanelSizeY;
                            double? sdzN = prms.ContainsKey("sDZ") ? (double?)GetDouble(prms, "sDZ") : _lastPanelThickness;
                            if (sdxN.HasValue && sdxN.Value > 0) patch["sDX"] = sdxN.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            if (sdyN.HasValue && sdyN.Value > 0) patch["sDY"] = sdyN.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            if (sdzN.HasValue && sdzN.Value > 0) patch["sDZ"] = sdzN.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            // Fixed defaults for zone / bed / vacuum, overridable.
                            patch["sBX"] = GetDouble(prms, "sBX", 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
                            patch["sBY"] = GetDouble(prms, "sBY", 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
                            patch["sBZ"] = GetDouble(prms, "sBZ", 20).ToString(System.Globalization.CultureInfo.InvariantCulture);
                            patch["sV"]  = GetDouble(prms, "sV", 10).ToString(System.Globalization.CultureInfo.InvariantCulture);
                            patch["sField"] = GetString(prms, "sField", "AD");
                            PatchIniSection(iniPath, machineSection, patch);
                            iniPatched = true;
                        }
                        catch (Exception ex) { iniPatchError = ex.Message; Log("INI patch error: " + ex.Message); }
                    }

                    drw.OutputNC(ncPath, outputTo, visibleOnly);
                    int status = 0;
                    try { status = Convert.ToInt32(drw.OutputNCStatus); } catch { }

                    // Always restore DisplayScreen=1 for the human operator.
                    if (iniPatched)
                    {
                        try
                        {
                            var restore = new Dictionary<string, string> { { "DisplayScreen", "1" } };
                            PatchIniSection(iniPath, machineSection, restore);
                        }
                        catch (Exception ex) { Log("INI restore error: " + ex.Message); }
                    }

                    var resp = new Dictionary<string, object>
                    {
                        ["path"] = ncPath, ["outputTo"] = (int)outputTo, ["visibleOnly"] = visibleOnly,
                        ["status"] = status,
                        ["unattended"] = unattended,
                        ["iniPatched"] = iniPatched,
                        ["iniPathTried"] = iniPath ?? "",
                        ["iniPathExists"] = !string.IsNullOrEmpty(iniPath) && File.Exists(iniPath),
                        ["message"] = status == 0 ? "NC code output generated" : "NC output failed; OutputNCStatus=" + status
                    };
                    if (iniPatched) resp["machineSection"] = machineSection;
                    if (iniPatchError != null) resp["iniPatchError"] = iniPatchError;
                    return resp;
                }

                case "alphacam.list_nc":
                {
                    dynamic drw = _app.ActiveDrawing;
                    drw.ListNC();
                    return new Dictionary<string, object> { ["message"] = "NC code list displayed" };
                }

                case "alphacam.create_rectangle":
                {
                    dynamic drw = _app.ActiveDrawing;
                    dynamic path = drw.CreateRectangle(GetDouble(prms, "x1"), GetDouble(prms, "y1"), GetDouble(prms, "x2"), GetDouble(prms, "y2"));
                    return new Dictionary<string, object> { ["geometryId"] = StoreObject(path) };
                }

                case "alphacam.create_circle":
                {
                    dynamic drw = _app.ActiveDrawing;
                    dynamic path = drw.CreateCircle(GetDouble(prms, "diameter"), GetDouble(prms, "centerX"), GetDouble(prms, "centerY"));
                    return new Dictionary<string, object> { ["geometryId"] = StoreObject(path) };
                }

                case "alphacam.create_line":
                {
                    dynamic drw = _app.ActiveDrawing;
                    dynamic path = drw.Create2DLine(GetDouble(prms, "x1"), GetDouble(prms, "y1"), GetDouble(prms, "x2"), GetDouble(prms, "y2"));
                    return new Dictionary<string, object> { ["geometryId"] = StoreObject(path) };
                }

                case "alphacam.create_polygon":
                {
                    dynamic drw = _app.ActiveDrawing;
                    dynamic path = drw.CreatePolygon(GetDouble(prms, "diameter"), GetInt(prms, "numSides"), GetBool(prms, "insideCircle", false), GetDouble(prms, "centerX"), GetDouble(prms, "centerY"));
                    return new Dictionary<string, object> { ["geometryId"] = StoreObject(path) };
                }

                case "alphacam.create_text":
                {
                    dynamic drw = _app.ActiveDrawing;
                    if (prms.ContainsKey("font"))
                    {
                        var font = GetString(prms, "font", "");
                        if (!string.IsNullOrEmpty(font)) drw.Font = font;
                    }
                    dynamic paths = drw.CreateText(GetString(prms, "text"), GetDouble(prms, "x"), GetDouble(prms, "y"), GetDouble(prms, "height"));
                    var ids = new List<string>();
                    int count = (int)paths.Count;
                    for (int i = 1; i <= count; i++) ids.Add(StoreObject(paths.Item(i)));
                    return new Dictionary<string, object> { ["geometryIds"] = ids };
                }

                case "alphacam.create_ellipse":
                {
                    dynamic drw = _app.ActiveDrawing;
                    dynamic path = drw.CreateEllipse(GetDouble(prms, "width"), GetDouble(prms, "height"), (short)GetInt(prms, "numQuads", 4), (short)GetInt(prms, "arcsPerQuad", 4), GetDouble(prms, "centerX"), GetDouble(prms, "centerY"));
                    return new Dictionary<string, object> { ["geometryId"] = StoreObject(path) };
                }

                case "alphacam.create_2d_geometry":
                {
                    dynamic drw = _app.ActiveDrawing;
                    dynamic geo = drw.Create2DGeometry(GetDouble(prms, "startX"), GetDouble(prms, "startY"));
                    var segments = prms.ContainsKey("segments") ? prms["segments"] as System.Collections.ArrayList : new System.Collections.ArrayList();
                    foreach (Dictionary<string, object> seg in segments)
                    {
                        var type = (string)seg["type"];
                        var x = Convert.ToDouble(seg["x"]);
                        var y = Convert.ToDouble(seg["y"]);
                        switch (type)
                        {
                            case "line": geo.AddLine(x, y); break;
                            case "arcCW": geo.AddArcPointCenter(x, y, Convert.ToDouble(seg["cx"]), Convert.ToDouble(seg["cy"]), true); break;
                            case "arcCCW": geo.AddArcPointCenter(x, y, Convert.ToDouble(seg["cx"]), Convert.ToDouble(seg["cy"]), false); break;
                        }
                    }
                    dynamic result = GetBool(prms, "closed", false) ? geo.CloseAndFinishLine : geo.Finish;
                    return new Dictionary<string, object> { ["geometryId"] = StoreObject(result) };
                }

                case "alphacam.list_geometries":
                {
                    dynamic drw = _app.ActiveDrawing;
                    dynamic geometries = drw.Geometries;
                    int count = (int)geometries.Count;
                    bool selectedOnly = GetBool(prms, "selectedOnly", false);
                    int selectedCount = 0;
                    var items = new List<Dictionary<string, object>>();
                    for (int i = 1; i <= count; i++)
                    {
                        dynamic g = geometries.Item(i);
                        bool isSelected = false;
                        try { isSelected = (bool)g.Selected; } catch { }
                        if (isSelected) selectedCount++;
                        if (selectedOnly && !isSelected) continue;
                        items.Add(new Dictionary<string, object>
                        {
                            ["geometryId"] = StoreObject(g),
                            ["index"] = i,
                            ["selected"] = isSelected
                        });
                    }
                    return new Dictionary<string, object>
                    {
                        ["totalCount"] = count,
                        ["selectedCount"] = selectedCount,
                        ["geometries"] = items
                    };
                }

                case "alphacam.get_geometry_info":
                {
                    var geoId = GetString(prms, "id");
                    if (!_objectCache.TryGetValue(geoId, out var rawObj)) throw new InvalidOperationException("Geometry " + geoId + " not found in object cache");
                    dynamic geo = rawObj;
                    var info = new Dictionary<string, object> { ["geometryId"] = geoId };
                    try { dynamic layer = geo.GetLayer(); if (layer != null) info["layer"] = (string)layer.Name; } catch { }
                    int elemCount = 0;
                    try { elemCount = (int)geo.GetElemCount(); } catch { }
                    info["elementCount"] = elemCount;
                    if (elemCount > 0)
                    {
                        try
                        {
                            dynamic first = geo.GetFirstElem();
                            dynamic last = geo.GetLastElem();
                            info["startX"] = (double)first.StartXG;
                            info["startY"] = (double)first.StartYG;
                            info["endX"] = (double)last.EndXG;
                            info["endY"] = (double)last.EndYG;
                            const double tol = 1e-6;
                            info["closed"] = Math.Abs((double)first.StartXG - (double)last.EndXG) < tol && Math.Abs((double)first.StartYG - (double)last.EndYG) < tol;
                            double total = 0;
                            try
                            {
                                dynamic el = first;
                                for (int i = 0; i < elemCount && el != null; i++) { total += (double)el.Length; el = el.GetNext(); }
                            }
                            catch { }
                            info["length"] = total;
                        }
                        catch (Exception ex) { info["elementInfoError"] = ex.GetType().Name + ": " + ex.Message; }
                    }
                    return info;
                }

                case "alphacam.zoom_all":
                {
                    dynamic drw = _app.ActiveDrawing;
                    drw.ZoomAll();
                    return new Dictionary<string, object> { ["message"] = "Zoomed to all geometry" };
                }

                case "alphacam.redraw":
                {
                    dynamic drw = _app.ActiveDrawing;
                    drw.Redraw();
                    return new Dictionary<string, object> { ["message"] = "View redrawn" };
                }

                case "alphacam.import_cad":
                {
                    var path = GetString(prms, "path");
                    if (string.IsNullOrEmpty(path)) throw new ArgumentException("path is required");
                    var format = GetString(prms, "format", "").ToLowerInvariant();
                    if (string.IsNullOrEmpty(format))
                    {
                        var ext = (Path.GetExtension(path) ?? "").TrimStart('.').ToLowerInvariant();
                        if (ext == "dwg") format = "dxf";
                        else if (ext == "igs") format = "iges";
                        else if (ext == "stp") format = "step";
                        else format = ext;
                    }
                    // clear=true (default) is File>New semantics: it also drops
                    // the current tool selection, so callers re-select a tool
                    // afterwards. clear=false merges into the open drawing.
                    bool clear = GetBool(prms, "clear", true);
                    // asSolid: route STL/STEP/other solid formats through
                    // App.OpenExternalFile, which produces a SolidPart that the
                    // 3D machining commands (machine_3d) can act on. The legacy
                    // App.OpenStlFile explodes an STL into one Path per facet
                    // (Path.STL=true) — visible, but NOT machinable as a 3D
                    // model ("No Surfaces/Solid Selected"). Verified 2026-09-29.
                    bool asSolid = GetBool(prms, "asSolid", format != "dxf" && format != "iges" && format != "step");
                    dynamic drwBefore = _app.ActiveDrawing;
                    int solidsBefore = 0, surfacesBefore = 0, geosBefore = 0;
                    if (!clear)
                    {
                        try { solidsBefore = (int)drwBefore.SolidParts.Count; } catch { }
                        try { surfacesBefore = (int)drwBefore.Surfaces.Count; } catch { }
                        try { geosBefore = (int)drwBefore.GetGeoCount(); } catch { }
                    }
                    string importMethod;
                    switch (format)
                    {
                        case "dxf": _app.OpenDxfFile(path, clear); importMethod = "OpenDxfFile"; break;
                        case "iges": _app.OpenIgesFile(path, clear, 0); importMethod = "OpenIgesFile"; break;
                        case "step":
                            if (asSolid) { _app.OpenExternalFile(path, "", clear); importMethod = "OpenExternalFile"; }
                            else { _app.OpenStepFile(path, clear); importMethod = "OpenStepFile"; }
                            break;
                        case "stl":
                            if (asSolid) { _app.OpenExternalFile(path, "", clear); importMethod = "OpenExternalFile"; }
                            else { _app.OpenStlFile(path, clear); importMethod = "OpenStlFile"; }
                            break;
                        default:
                            // Any other extension (x_t, x_b, sldprt, sat, ipt, ...)
                            // goes to the add-in import registry; the extension
                            // picks the importer.
                            _app.OpenExternalFile(path, GetString(prms, "externalType", ""), clear);
                            importMethod = "OpenExternalFile";
                            break;
                    }
                    dynamic drw = _app.ActiveDrawing;
                    int solidsAfter = 0, surfacesAfter = 0, geosAfter = 0;
                    try { solidsAfter = (int)drw.SolidParts.Count; } catch { }
                    try { surfacesAfter = (int)drw.Surfaces.Count; } catch { }
                    try { geosAfter = (int)drw.GetGeoCount(); } catch { }
                    var newSolidIds = new List<string>();
                    try
                    {
                        dynamic parts = drw.SolidParts;
                        for (int i = solidsBefore + 1; i <= solidsAfter; i++) newSolidIds.Add(StoreObject(parts.Item(i)));
                    }
                    catch { }
                    var newSurfaceIds = new List<string>();
                    try
                    {
                        dynamic surfs = drw.Surfaces;
                        for (int i = surfacesBefore + 1; i <= surfacesAfter; i++) newSurfaceIds.Add(StoreObject(surfs.Item(i)));
                    }
                    catch { }
                    var importRes = new Dictionary<string, object>
                    {
                        ["format"] = format,
                        ["path"] = path,
                        ["method"] = importMethod,
                        ["cleared"] = clear,
                        ["solidPartCount"] = solidsAfter,
                        ["surfaceCount"] = surfacesAfter,
                        ["geometryCount"] = geosAfter,
                        ["newSolidIds"] = newSolidIds,
                        ["newSurfaceIds"] = newSurfaceIds,
                    };
                    if (importMethod == "OpenStlFile")
                        importRes["note"] = "Legacy facet import: the STL is now " + (geosAfter - geosBefore) + " facet geometries (Path.STL=true), not a 3D model. For 3D machining re-import with asSolid:true.";
                    else if (clear)
                        importRes["note"] = "Drawing was cleared before import (File>New semantics) — re-select the tool and post before machining.";
                    return importRes;
                }

                case "alphacam.select_post":
                {
                    // Default path probing: LicomdirPath alone is unreliable on
                    // some installs (returns C:\ALPHACAM\ with no LICOMDIR
                    // segment). Probe several candidate locations and take the
                    // first that exists on disk. See DevGuide gotcha #22/#28.
                    string defaultPath = prms.ContainsKey("fileName") ? null : ProbeDefaultPostPath();
                    var postPath = GetString(prms, "fileName", defaultPath);
                    if (string.IsNullOrEmpty(postPath)) throw new ArgumentException("No fileName given and LicomdirPath unavailable");
                    _app.SelectPost(postPath);
                    string activePost = null;
                    try { activePost = (string)((dynamic)_app).PostFileName; } catch { }
                    return new Dictionary<string, object> { ["postFileName"] = postPath, ["activePost"] = activePost };
                }

                case "alphacam.list_tools":
                {
                    var folder = GetString(prms, "folder", "");
                    var extension = GetString(prms, "extension", ".art");
                    var recursive = GetBool(prms, "recursive", true);
                    if (!extension.StartsWith(".")) extension = "." + extension;
                    if (string.IsNullOrEmpty(folder))
                    {
                        // LicomdatPath (like LicomdirPath — gotcha #22/#28) can
                        // return the Alphacam ROOT with no LICOMDAT segment, so
                        // probe several candidate layouts and take the first
                        // that exists on disk.
                        var candidates = new List<string>();
                        try
                        {
                            var licomdat = (string)((dynamic)_app).LicomdatPath;
                            if (!string.IsNullOrEmpty(licomdat))
                            {
                                candidates.Add(Path.Combine(licomdat, "Rtools.alp"));
                                candidates.Add(Path.Combine(licomdat, "Mtools.alp"));
                                candidates.Add(Path.Combine(licomdat, "LICOMDAT", "Rtools.alp"));
                                candidates.Add(Path.Combine(licomdat, "LICOMDAT", "Mtools.alp"));
                            }
                        }
                        catch { }
                        try
                        {
                            var licomdir = (string)((dynamic)_app).LicomdirPath;
                            if (!string.IsNullOrEmpty(licomdir))
                            {
                                candidates.Add(Path.Combine(licomdir, "LICOMDAT", "Rtools.alp"));
                                candidates.Add(Path.Combine(licomdir, "LICOMDAT", "Mtools.alp"));
                            }
                        }
                        catch { }
                        foreach (var c in candidates)
                        {
                            try { if (Directory.Exists(c)) { folder = c; break; } } catch { }
                        }
                    }
                    if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) throw new ArgumentException("Tool folder not found. Pass `folder` explicitly.");
                    var searchOpt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                    var files = Directory.GetFiles(folder, "*" + extension, searchOpt);
                    var items = new List<Dictionary<string, object>>();
                    foreach (var f in files)
                    {
                        var relDir = Path.GetDirectoryName(f);
                        if (relDir != null && relDir.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                            relDir = relDir.Substring(folder.Length).TrimStart('\\', '/');
                        items.Add(new Dictionary<string, object>
                        {
                            ["name"] = Path.GetFileNameWithoutExtension(f),
                            ["path"] = f,
                            ["subfolder"] = string.IsNullOrEmpty(relDir) ? null : relDir
                        });
                    }
                    return new Dictionary<string, object>
                    {
                        ["folder"] = folder, ["extension"] = extension, ["recursive"] = recursive,
                        ["count"] = items.Count, ["tools"] = items
                    };
                }

                case "alphacam.select_tool":
                {
                    var fileName = GetString(prms, "fileName", "");
                    if (string.IsNullOrEmpty(fileName)) fileName = "$USER";
                    dynamic tool = _app.SelectTool(fileName);
                    var info = new Dictionary<string, object> { ["toolId"] = StoreObject(tool) };
                    // Report the tool's ACTUAL file path (works for $USER picker
                    // picks too) — callers can learn the tool-library folder
                    // from it when auto-discovery fails on a new install.
                    try { info["fileName"] = (string)tool.FileName; } catch { info["fileName"] = fileName == "$USER" ? null : fileName; }
                    try { info["diameter"] = (double)tool.Diameter; } catch { }
                    try { info["toolNumber"] = (int)tool.Number; } catch { }
                    try { info["type"] = (int)tool.Type; } catch { }
                    try { info["length"] = (double)tool.Length; } catch { }
                    try { info["cornerRadius"] = (double)tool.CornerRadius; } catch { }
                    return info;
                }

                case "alphacam.get_current_tool":
                {
                    dynamic tool = _app.GetCurrentTool();
                    if (tool == null) return new Dictionary<string, object> { ["toolId"] = null };
                    var info = new Dictionary<string, object> { ["toolId"] = StoreObject(tool) };
                    try { info["fileName"] = (string)tool.FileName; } catch { }
                    try { info["diameter"] = (double)tool.Diameter; } catch { }
                    try { info["toolNumber"] = (int)tool.Number; } catch { }
                    try { info["type"] = (int)tool.Type; } catch { }
                    try { info["length"] = (double)tool.Length; } catch { }
                    return info;
                }

                case "alphacam.specify_panel":
                {
                    var sizeX = GetDouble(prms, "sizeX");
                    var sizeY = GetDouble(prms, "sizeY");
                    var thickness = GetDouble(prms, "thickness");
                    var offsetX = GetDouble(prms, "offsetX", 0.0);
                    var offsetY = GetDouble(prms, "offsetY", 0.0);
                    var topZ = GetDouble(prms, "topZ", 0.0);
                    var bottomZ = GetDouble(prms, "bottomZ", topZ - Math.Abs(thickness));
                    dynamic drw = _app.ActiveDrawing;
                    var beforeUnks = SnapshotGeometryUnknowns(drw);
                    dynamic panel = drw.CreateRectangle(offsetX, offsetY, offsetX + sizeX, offsetY + sizeY);
                    bool ok = true;
                    try { panel.SetPanel(topZ, bottomZ); } catch (Exception ex) { ok = false; Log("SetPanel error: " + ex.Message); }
                    var panelId = StoreObject(panel);
                    var helpers = DiffNewGeometries(drw, beforeUnks, panel);
                    _designationHelpers[panelId] = helpers;
                    // Remember the dims so output_nc can auto-fill sDX/sDY/sDZ
                    // in the XilogPost.ini without the caller re-supplying them.
                    _lastPanelSizeX = sizeX;
                    _materialDefined = true;
                    _lastPanelSizeY = sizeY;
                    _lastPanelThickness = Math.Abs(thickness);
                    return new Dictionary<string, object>
                    {
                        ["geometryId"] = panelId,
                        ["sizeX"] = sizeX, ["sizeY"] = sizeY, ["thickness"] = thickness,
                        ["topZ"] = topZ, ["bottomZ"] = bottomZ,
                        ["offsetX"] = offsetX, ["offsetY"] = offsetY,
                        ["setPanelOk"] = ok, ["helperCount"] = helpers.Count,
                        ["message"] = "Panel set (Router workpiece)"
                    };
                }

                case "alphacam.specify_material":
                {
                    _materialDefined = true;
                    var sizeX = GetDouble(prms, "sizeX");
                    var sizeY = GetDouble(prms, "sizeY");
                    var thickness = GetDouble(prms, "thickness");
                    var offsetX = GetDouble(prms, "offsetX", 0.0);
                    var offsetY = GetDouble(prms, "offsetY", 0.0);
                    var topZ = GetDouble(prms, "topZ", 0.0);
                    var bottomZ = GetDouble(prms, "bottomZ", topZ - Math.Abs(thickness));
                    dynamic drw = _app.ActiveDrawing;
                    var beforeUnks = SnapshotGeometryUnknowns(drw);
                    dynamic matGeo = drw.CreateRectangle(offsetX, offsetY, offsetX + sizeX, offsetY + sizeY);
                    bool ok = true;
                    try { var ret = matGeo.SetMaterial(topZ, bottomZ); try { ok = (bool)ret; } catch { } }
                    catch (Exception ex) { ok = false; Log("SetMaterial error: " + ex.Message); }
                    var matId = StoreObject(matGeo);
                    var helpers = DiffNewGeometries(drw, beforeUnks, matGeo);
                    _designationHelpers[matId] = helpers;
                    return new Dictionary<string, object>
                    {
                        ["geometryId"] = matId,
                        ["sizeX"] = sizeX, ["sizeY"] = sizeY, ["thickness"] = thickness,
                        ["topZ"] = topZ, ["bottomZ"] = bottomZ,
                        ["offsetX"] = offsetX, ["offsetY"] = offsetY,
                        ["setMaterialOk"] = ok, ["helperCount"] = helpers.Count,
                        ["message"] = "Rectangle designated as material geometry"
                    };
                }

                case "alphacam.set_material":
                {
                    dynamic drw = _app.ActiveDrawing;
                    string name = prms.ContainsKey("name") ? GetString(prms, "name", "") : "";
                    bool haveCustomProps = prms.ContainsKey("speed") || prms.ContainsKey("inch") || prms.ContainsKey("kFactor");
                    string mode;
                    if (haveCustomProps)
                    {
                        dynamic mat = drw.CreateMaterial();
                        if (!string.IsNullOrEmpty(name)) mat.Name = name;
                        if (prms.ContainsKey("inch"))    mat.Inch    = GetBool(prms, "inch", false);
                        if (prms.ContainsKey("speed"))   mat.Speed   = (float)GetDouble(prms, "speed", 0);
                        if (prms.ContainsKey("kFactor")) mat.KFactor = (float)GetDouble(prms, "kFactor", 0);
                        drw.SetMaterial(mat);
                        mode = "custom";
                    }
                    else if (!string.IsNullOrEmpty(name)) { drw.SelectMaterial(name); mode = "library"; }
                    else { throw new ArgumentException("set_material needs either `name` or custom props."); }
                    var info = new Dictionary<string, object> { ["mode"] = mode };
                    try
                    {
                        dynamic active = drw.GetMaterial();
                        if (active != null)
                        {
                            try { info["name"] = (string)active.Name; } catch { }
                            try { info["inch"] = (bool)active.Inch; } catch { }
                            try { info["speed"] = (double)active.Speed; } catch { }
                            try { info["kFactor"] = (double)active.KFactor; } catch { }
                        }
                    }
                    catch { }
                    info["message"] = "Material set";
                    return info;
                }

                case "alphacam.set_tool_side":
                {
                    var geoId = GetString(prms, "geometryId");
                    if (!_objectCache.TryGetValue(geoId, out var rawGeo)) throw new InvalidOperationException("Geometry " + geoId + " not found in object cache");
                    var sideStr = GetString(prms, "side").ToLowerInvariant();
                    dynamic geo = rawGeo;
                    bool isClosed = false;
                    try
                    {
                        dynamic first = geo.GetFirstElem();
                        dynamic last = geo.GetLastElem();
                        const double tol = 1e-6;
                        isClosed = Math.Abs((double)first.StartXG - (double)last.EndXG) < tol && Math.Abs((double)first.StartYG - (double)last.EndYG) < tol;
                    }
                    catch { }
                    string usedProp;
                    int sideValue;
                    if (isClosed)
                    {
                        switch (sideStr)
                        {
                            case "inside": sideValue = 1; break;
                            case "outside": sideValue = -1; break;
                            case "center":
                            case "on_center":
                            case "on-center": sideValue = 0; break;
                            case "left": sideValue = 1; break;
                            case "right": sideValue = -1; break;
                            default: throw new ArgumentException("Unknown side '" + sideStr + "'.");
                        }
                        geo.ToolInOut = (short)sideValue;
                        usedProp = "ToolInOut";
                    }
                    else
                    {
                        switch (sideStr)
                        {
                            case "left": sideValue = 1; break;
                            case "right": sideValue = -1; break;
                            case "center": sideValue = 0; break;
                            case "inside": sideValue = 1; break;
                            case "outside": sideValue = -1; break;
                            default: throw new ArgumentException("Unknown side '" + sideStr + "'.");
                        }
                        geo.ToolSide = (short)sideValue;
                        usedProp = "ToolSide";
                    }
                    return new Dictionary<string, object>
                    {
                        ["geometryId"] = geoId, ["side"] = sideStr, ["value"] = sideValue,
                        ["property"] = usedProp, ["closed"] = isClosed,
                        ["message"] = "Tool side set to " + sideStr + " via " + usedProp
                    };
                }

                case "alphacam.reverse_geometry":
                {
                    var geoId = GetString(prms, "geometryId");
                    if (!_objectCache.TryGetValue(geoId, out var rawGeo)) throw new InvalidOperationException("Geometry " + geoId + " not found in object cache");
                    dynamic geo = rawGeo;
                    string mode;
                    bool? finalCW = null;
                    if (prms.ContainsKey("direction"))
                    {
                        var dir = GetString(prms, "direction").ToLowerInvariant();
                        bool wantCW;
                        switch (dir)
                        {
                            case "cw":
                            case "clockwise": wantCW = true; break;
                            case "ccw":
                            case "counterclockwise":
                            case "counter-clockwise": wantCW = false; break;
                            default: throw new ArgumentException("direction must be 'cw' or 'ccw'");
                        }
                        try { geo.CW = wantCW; finalCW = wantCW; mode = "set_cw"; }
                        catch
                        {
                            bool current = (bool)geo.CW;
                            if (current != wantCW) geo.Reverse();
                            finalCW = wantCW;
                            mode = "reverse_to_match";
                        }
                    }
                    else
                    {
                        geo.Reverse();
                        try { finalCW = (bool)geo.CW; } catch { }
                        mode = "reverse";
                    }
                    return new Dictionary<string, object>
                    {
                        ["geometryId"] = geoId, ["mode"] = mode, ["cw"] = finalCW,
                        ["message"] = "Geometry direction updated"
                    };
                }

                case "alphacam.set_start_point":
                {
                    var geoId = GetString(prms, "geometryId");
                    if (!_objectCache.TryGetValue(geoId, out var rawGeo)) throw new InvalidOperationException("Geometry " + geoId + " not found in object cache");
                    dynamic geo = rawGeo;
                    var ruleRaw = GetString(prms, "rule", "explicit").ToLowerInvariant();
                    var rule = ruleRaw.Replace("_", "").Replace("-", "");
                    double targetX = 0, targetY = 0;
                    string ruleName = ruleRaw;
                    double? sourceLen = null;
                    switch (rule)
                    {
                        case "explicit":
                        case "point":
                        case "xy":
                            targetX = GetDouble(prms, "x");
                            targetY = GetDouble(prms, "y");
                            ruleName = "explicit";
                            break;
                        case "middleoflongestedge":
                        case "longestedge":
                        case "longestedgemidpoint":
                        {
                            double len;
                            EdgeMidpoint(geo, true, out targetX, out targetY, out len);
                            ruleName = "middleOfLongestEdge";
                            sourceLen = len;
                            break;
                        }
                        case "middleofshortestedge":
                        case "shortestedge":
                        case "shortestedgemidpoint":
                        {
                            double len;
                            EdgeMidpoint(geo, false, out targetX, out targetY, out len);
                            ruleName = "middleOfShortestEdge";
                            sourceLen = len;
                            break;
                        }
                        case "topleft":
                        case "topright":
                        case "bottomleft":
                        case "bottomright":
                        {
                            double minX, minY, maxX, maxY;
                            GeometryBounds(geo, out minX, out minY, out maxX, out maxY);
                            double cx = 0, cy = 0;
                            switch (rule)
                            {
                                case "topleft":     cx = minX; cy = maxY; break;
                                case "topright":    cx = maxX; cy = maxY; break;
                                case "bottomleft":  cx = minX; cy = minY; break;
                                case "bottomright": cx = maxX; cy = minY; break;
                            }
                            NearestVertex(geo, cx, cy, out targetX, out targetY);
                            ruleName = rule;
                            break;
                        }
                        case "nearesttopoint":
                        case "closesttopoint":
                        {
                            var rx = GetDouble(prms, "x");
                            var ry = GetDouble(prms, "y");
                            NearestVertex(geo, rx, ry, out targetX, out targetY);
                            ruleName = "nearestToPoint";
                            break;
                        }
                        default:
                            throw new ArgumentException(
                                "Unknown rule '" + ruleRaw + "'. Try: explicit, middleOfLongestEdge, " +
                                "middleOfShortestEdge, topLeft, topRight, bottomLeft, bottomRight, nearestToPoint.");
                    }
                    geo.SetStartPoint(targetX, targetY);
                    var result = new Dictionary<string, object>
                    {
                        ["geometryId"] = geoId, ["rule"] = ruleName,
                        ["x"] = targetX, ["y"] = targetY,
                        ["message"] = "Start point set"
                    };
                    if (sourceLen.HasValue) result["edgeLength"] = sourceLen.Value;
                    return result;
                }

                case "alphacam.delete_geometry":
                {
                    var geoId = GetString(prms, "geometryId");
                    if (!_objectCache.TryGetValue(geoId, out var rawGeo)) throw new InvalidOperationException("Geometry " + geoId + " not found in object cache");
                    int helpersDeleted = CascadeDeleteHelpers(geoId);
                    dynamic geo = rawGeo;
                    geo.Delete();
                    RemoveFromCache(geoId);
                    dynamic drwGD = _app.ActiveDrawing;
                    return new Dictionary<string, object>
                    {
                        ["geometryId"] = geoId, ["helpersDeleted"] = helpersDeleted,
                        ["geometryCount"] = (int)drwGD.GetGeoCount(),
                        ["toolPathCount"] = (int)drwGD.GetToolPathCount(),
                        ["message"] = "Geometry deleted"
                    };
                }

                case "alphacam.delete_toolpath":
                {
                    dynamic drw = _app.ActiveDrawing;
                    int deletedCount = 0;
                    string mode;
                    if (GetBool(prms, "all", false))
                    {
                        int before = (int)drw.GetToolPathCount();
                        drw.Clear(false, false, true, false, false, false, false, false);
                        deletedCount = before - (int)drw.GetToolPathCount();
                        mode = "all";
                    }
                    else if (prms.ContainsKey("geometryId"))
                    {
                        var tpId = GetString(prms, "geometryId");
                        if (!_objectCache.TryGetValue(tpId, out var rawTp)) throw new InvalidOperationException("Toolpath " + tpId + " not found in object cache");
                        dynamic tp = rawTp;
                        tp.Delete();
                        RemoveFromCache(tpId);
                        deletedCount = 1;
                        mode = "by-id";
                    }
                    else if (prms.ContainsKey("index"))
                    {
                        var idx = GetInt(prms, "index");
                        dynamic tps = drw.ToolPaths;
                        int count = (int)tps.Count;
                        if (idx < 1 || idx > count) throw new ArgumentException("index " + idx + " out of range 1.." + count);
                        dynamic tp = tps.Item(idx);
                        tp.Delete();
                        deletedCount = 1;
                        mode = "by-index";
                    }
                    else
                    {
                        throw new ArgumentException("delete_toolpath needs one of: `geometryId`, `index`, or `all: true`.");
                    }
                    return new Dictionary<string, object>
                    {
                        ["mode"] = mode, ["deletedCount"] = deletedCount,
                        ["toolPathCount"] = (int)drw.GetToolPathCount(),
                        ["message"] = deletedCount == 1 ? "Toolpath deleted" : deletedCount + " toolpaths deleted"
                    };
                }

                case "alphacam.delete_panel":
                {
                    var geoId = GetString(prms, "geometryId");
                    if (!_objectCache.TryGetValue(geoId, out var rawGeo)) throw new InvalidOperationException("Geometry " + geoId + " not found in object cache");
                    int helpersDeleted = CascadeDeleteHelpers(geoId);
                    dynamic panel = rawGeo;
                    panel.Delete();
                    RemoveFromCache(geoId);
                    dynamic drwDP = _app.ActiveDrawing;
                    return new Dictionary<string, object>
                    {
                        ["geometryId"] = geoId, ["helpersDeleted"] = helpersDeleted,
                        ["geometryCount"] = (int)drwDP.GetGeoCount(),
                        ["message"] = "Panel deleted"
                    };
                }

                case "alphacam.delete_material":
                {
                    var target = GetString(prms, "target", "geo").ToLowerInvariant();
                    dynamic drwDM = _app.ActiveDrawing;
                    if (target == "geo")
                    {
                        var geoId = GetString(prms, "geometryId");
                        if (!_objectCache.TryGetValue(geoId, out var rawGeo)) throw new InvalidOperationException("Geometry " + geoId + " not found in object cache");
                        int helpersDeleted = CascadeDeleteHelpers(geoId);
                        dynamic mat = rawGeo;
                        mat.Delete();
                        RemoveFromCache(geoId);
                        return new Dictionary<string, object>
                        {
                            ["target"] = "geo", ["geometryId"] = geoId, ["helpersDeleted"] = helpersDeleted,
                            ["geometryCount"] = (int)drwDM.GetGeoCount(),
                            ["message"] = "Material geometry deleted"
                        };
                    }
                    else if (target == "type")
                    {
                        dynamic blank = drwDM.CreateMaterial();
                        blank.Name = "";
                        drwDM.SetMaterial(blank);
                        return new Dictionary<string, object> { ["target"] = "type", ["message"] = "Material type cleared" };
                    }
                    else
                    {
                        throw new ArgumentException("target must be 'geo' or 'type' (got '" + target + "')");
                    }
                }

                case "alphacam.rough_finish":
                {
                    var geoList = ResolveGeometryList(prms);
                    var geoId = geoList[0].Key;
                    dynamic drw = _app.ActiveDrawing;
                    bool selectedDirect = SelectGeometries(drw, geoList);
                    dynamic md = _app.CreateMillData();
                    md.McComp = (short)0;
                    md.SafeRapidLevel = GetDouble(prms, "safeRapidLevel", 1.0);
                    md.RapidDownTo = GetDouble(prms, "rapidDownTo", 0.25);
                    md.FinalDepth = GetDouble(prms, "finalDepth", -0.25);
                    md.NumberOfCuts = (short)GetInt(prms, "numberOfCuts", 1);
                    md.HelicalInterpolation = GetBool(prms, "helical", false);
                    if (md.HelicalInterpolation)
                    {
                        // helicalPitch = Z drop per revolution. Default to the
                        // current tool's diameter — a common starting point for
                        // helical entries. Explicit override always wins.
                        double pitch;
                        if (prms.ContainsKey("helicalPitch"))
                        {
                            pitch = GetDouble(prms, "helicalPitch");
                        }
                        else
                        {
                            pitch = 0.1;
                            try
                            {
                                dynamic curTool = _app.GetCurrentTool();
                                if (curTool != null)
                                {
                                    var dia = (double)curTool.Diameter;
                                    if (dia > 0) pitch = dia;
                                }
                            }
                            catch { }
                        }
                        md.MaxDepthPerCut = (float)pitch;
                    }
                    md.Stock = (float)GetDouble(prms, "stockToLeave", 0.0);
                    md.InitialStock = (float)GetDouble(prms, "initialStock", 0.0);
                    md.RoughFinishNumberOfCuts = GetInt(prms, "lateralCuts", 1);
                    if (prms.ContainsKey("feedRate")) md.CutFeed = (float)GetDouble(prms, "feedRate", 0);
                    if (prms.ContainsKey("spindleSpeed")) md.SpindleSpeed = (float)GetDouble(prms, "spindleSpeed", 0);
                    var rfSides = ApplySidesAndAutoZ(md, prms, (bool)md.HelicalInterpolation);
                    // Per ACAMAPI: leads for rough_finish are added by calling
                    // Path.SetLeadInOutAuto on each returned toolpath, NOT by
                    // setting LeadData on MillData beforehand. Capture the
                    // returned Paths so we can post-process them.
                    dynamic rfPaths = md.RoughFinish();
                    int rfLeadsApplied = ApplyLeadInOutToPaths(rfPaths, prms);
                    return new Dictionary<string, object>
                    {
                        ["geometryId"] = geoId,
                        ["geometryCount"] = geoList.Count,
                        ["selection"] = selectedDirect ? "direct" : "collection-fallback",
                        ["finalDepth"] = (double)md.FinalDepth,
                        ["numberOfCuts"] = (int)md.NumberOfCuts,
                        ["safeRapidLevel"] = (double)md.SafeRapidLevel,
                        ["rapidDownTo"] = (double)md.RapidDownTo,
                        ["toolPathCount"] = (int)drw.GetToolPathCount(),
                        ["leadsApplied"] = rfLeadsApplied,
                        ["sides"] = rfSides,
                        ["message"] = "RoughFinish operation completed"
                    };
                }

                case "alphacam.pocket":
                {
                    var geoList = ResolveGeometryList(prms);
                    var geoId = geoList[0].Key;
                    dynamic drw = _app.ActiveDrawing;
                    var sideStr = GetString(prms, "toolSide", "inside").ToLowerInvariant();
                    short toolInOut;
                    switch (sideStr)
                    {
                        case "inside": toolInOut = 1; break;
                        case "outside": toolInOut = -1; break;
                        case "center":
                        case "on_center": toolInOut = 0; break;
                        default: throw new ArgumentException("toolSide must be inside/outside/center");
                    }
                    foreach (var kv in geoList)
                    {
                        try { dynamic g = kv.Value; g.ToolInOut = toolInOut; } catch { }
                    }
                    bool selectedDirect = SelectGeometries(drw, geoList);
                    dynamic md = _app.CreateMillData();
                    md.McComp = (short)0;
                    md.SafeRapidLevel = GetDouble(prms, "safeRapidLevel", 1.0);
                    md.RapidDownTo = GetDouble(prms, "rapidDownTo", 0.25);
                    md.FinalDepth = GetDouble(prms, "depth", GetDouble(prms, "finalDepth", -0.25));
                    md.NumberOfCuts = (short)GetInt(prms, "numberOfCuts", 1);
                    var pocketTypeStr = GetString(prms, "pocketType", "contour").ToLowerInvariant();
                    short pocketType;
                    switch (pocketTypeStr)
                    {
                        case "contour": pocketType = 0; break;
                        case "linear": pocketType = 1; break;
                        case "spiral": pocketType = 2; break;
                        default: throw new ArgumentException("pocketType must be contour/linear/spiral");
                    }
                    md.PocketType = pocketType;
                    double stepover;
                    if (prms.ContainsKey("stepover")) stepover = GetDouble(prms, "stepover");
                    else
                    {
                        stepover = 0.25;
                        try
                        {
                            dynamic tool = _app.GetCurrentTool();
                            if (tool != null) { var dia = (double)tool.Diameter; if (dia > 0) stepover = dia * 0.5; }
                        }
                        catch { }
                    }
                    md.WidthOfCut = stepover;
                    md.Stock = (float)GetDouble(prms, "finishAllowance", GetDouble(prms, "stockToLeave", 0.0));
                    if (pocketType == 1 && prms.ContainsKey("cutDirection")) md.CutDirection = GetDouble(prms, "cutDirection", 0.0);
                    if (prms.ContainsKey("pocketOrder"))
                    {
                        var orderStr = GetString(prms, "pocketOrder", "zone").ToLowerInvariant();
                        md.PocketOrder = (short)(orderStr == "depth" ? 1 : 0);
                    }
                    if (prms.ContainsKey("feedRate")) md.CutFeed = (float)GetDouble(prms, "feedRate", 0);
                    if (prms.ContainsKey("spindleSpeed")) md.SpindleSpeed = (float)GetDouble(prms, "spindleSpeed", 0);
                    var pkSides = ApplySidesAndAutoZ(md, prms, false);
                    // Leads: same pattern as rough_finish — capture the Paths
                    // returned by md.Pocket() and call SetLeadInOutAuto on each.
                    // (The old MillData.LeadData approach produced no visible
                    // leads in the G-code.)
                    dynamic pkPaths = md.Pocket();
                    int pkLeadsApplied = ApplyLeadInOutToPaths(pkPaths, prms);
                    return new Dictionary<string, object>
                    {
                        ["geometryId"] = geoId,
                        ["geometryCount"] = geoList.Count,
                        ["selection"] = selectedDirect ? "direct" : "collection-fallback",
                        ["toolSide"] = sideStr, ["pocketType"] = pocketTypeStr,
                        ["finalDepth"] = (double)md.FinalDepth,
                        ["numberOfCuts"] = (int)md.NumberOfCuts,
                        ["widthOfCut"] = (double)md.WidthOfCut,
                        ["safeRapidLevel"] = (double)md.SafeRapidLevel,
                        ["rapidDownTo"] = (double)md.RapidDownTo,
                        ["toolPathCount"] = (int)drw.GetToolPathCount(),
                        ["leadsApplied"] = pkLeadsApplied,
                        ["sides"] = pkSides,
                        ["message"] = "Pocket operation completed"
                    };
                }

                case "alphacam.drill_tap":
                {
                    var geoList = ResolveGeometryList(prms);
                    var geoId = geoList[0].Key;
                    dynamic drw = _app.ActiveDrawing;
                    bool selectedDirect = SelectGeometries(drw, geoList);
                    dynamic md = _app.CreateMillData();
                    // AcamDrillType: 0=DRILL, 1=PECK, 2=TAP, 3=BORE, 4=CHAMFER
                    // (values decoded from Interop.AlphaCAMMill.dll metadata).
                    short drillType = 0;
                    if (prms.ContainsKey("drillType"))
                    {
                        if (prms["drillType"] is string)
                        {
                            switch (GetString(prms, "drillType").ToLowerInvariant())
                            {
                                case "drill": drillType = 0; break;
                                case "peck": drillType = 1; break;
                                case "tap": drillType = 2; break;
                                case "bore": drillType = 3; break;
                                case "chamfer": drillType = 4; break;
                                default: throw new ArgumentException("drillType must be drill/peck/tap/bore/chamfer or 0-4");
                            }
                        }
                        else drillType = (short)GetInt(prms, "drillType", 0);
                    }
                    md.DrillType = drillType;
                    md.SafeRapidLevel = GetDouble(prms, "safeRapidLevel", 1.0);
                    md.RapidDownTo = GetDouble(prms, "rapidDownTo", 0.25);
                    md.BottomOfHole = (float)GetDouble(prms, "depth");
                    // Peck-specific
                    if (prms.ContainsKey("peckDistance") || prms.ContainsKey("peckDepth"))
                        md.PeckDistance = (float)GetDouble(prms, "peckDistance", GetDouble(prms, "peckDepth", 0.1));
                    if (prms.ContainsKey("dwell")) md.DwellTime = (float)GetDouble(prms, "dwell", 0);
                    if (prms.ContainsKey("retractPartial")) md.RetractPartial = GetBool(prms, "retractPartial", false);
                    // Tap-specific: pitch defaults to the current tool's Pitch.
                    if (drillType == 2)
                    {
                        double pitch = GetDouble(prms, "threadPitch", 0);
                        if (pitch <= 0)
                        {
                            try
                            {
                                dynamic curTool = _app.GetCurrentTool();
                                if (curTool != null) pitch = (double)curTool.Pitch;
                            }
                            catch { }
                        }
                        if (pitch > 0) md.ThreadPitch = (float)pitch;
                    }
                    // AcamHoles: 0=CIRCLES, 1=CROSSES, 2=MATCH_DRILL, 3=GIVEN_DIA
                    if (prms.ContainsKey("whichHoles")) md.WhichHoles = (short)GetInt(prms, "whichHoles", 0);
                    if (prms.ContainsKey("givenHoleDiameter")) md.GivenHoleDiameter = GetDouble(prms, "givenHoleDiameter", 0);
                    if (prms.ContainsKey("feedRate")) md.DrillFeed = (float)GetDouble(prms, "feedRate", 0);
                    if (prms.ContainsKey("spindleSpeed")) md.SpindleSpeed = (float)GetDouble(prms, "spindleSpeed", 0);
                    dynamic dtPaths = md.DrillTap();
                    int dtCreated = 0;
                    try { dtCreated = (int)dtPaths.Count; } catch { }
                    return new Dictionary<string, object>
                    {
                        ["geometryId"] = geoId,
                        ["geometryCount"] = geoList.Count,
                        ["selection"] = selectedDirect ? "direct" : "collection-fallback",
                        ["drillType"] = (int)drillType,
                        ["bottomOfHole"] = (double)md.BottomOfHole,
                        ["safeRapidLevel"] = (double)md.SafeRapidLevel,
                        ["rapidDownTo"] = (double)md.RapidDownTo,
                        ["pathsCreated"] = dtCreated,
                        ["toolPathCount"] = (int)drw.GetToolPathCount(),
                        ["message"] = "DrillTap operation completed"
                    };
                }

                case "alphacam.engrave":
                {
                    var geoList = ResolveGeometryList(prms);
                    var geoId = geoList[0].Key;
                    dynamic drw = _app.ActiveDrawing;
                    bool selectedDirect = SelectGeometries(drw, geoList);
                    dynamic md = _app.CreateMillData();
                    // acamEngraveGEOMETRIES = 0 — engrave the selected geometries.
                    md.EngraveType = (short)0;
                    md.SafeRapidLevel = GetDouble(prms, "safeRapidLevel", 1.0);
                    md.RapidDownTo = GetDouble(prms, "rapidDownTo", 0.25);
                    md.FinalDepth = GetDouble(prms, "depth", GetDouble(prms, "finalDepth", -0.02));
                    if (prms.ContainsKey("numberOfCuts")) md.NumberOfCuts = (short)GetInt(prms, "numberOfCuts", 1);
                    // AcamCorners: 0=ROUND, 1=STRAIGHT, 2=LOOP
                    if (prms.ContainsKey("cornerStyle"))
                    {
                        short corners;
                        switch (GetString(prms, "cornerStyle", "round").ToLowerInvariant())
                        {
                            case "round": corners = 0; break;
                            case "straight": corners = 1; break;
                            case "loop": corners = 2; break;
                            default: throw new ArgumentException("cornerStyle must be round/straight/loop");
                        }
                        md.XYCorners = corners;
                    }
                    if (prms.ContainsKey("cornerAngleLimit")) md.EngraveCornerAngleLimit = (float)GetDouble(prms, "cornerAngleLimit", 120);
                    if (prms.ContainsKey("feedRate")) md.CutFeed = (float)GetDouble(prms, "feedRate", 0);
                    if (prms.ContainsKey("spindleSpeed")) md.SpindleSpeed = (float)GetDouble(prms, "spindleSpeed", 0);
                    dynamic enPaths = md.Engrave();
                    int enCreated = 0;
                    try { enCreated = (int)enPaths.Count; } catch { }
                    int enLeadsApplied = ApplyLeadInOutToPaths(enPaths, prms);
                    return new Dictionary<string, object>
                    {
                        ["geometryId"] = geoId,
                        ["geometryCount"] = geoList.Count,
                        ["selection"] = selectedDirect ? "direct" : "collection-fallback",
                        ["finalDepth"] = (double)md.FinalDepth,
                        ["safeRapidLevel"] = (double)md.SafeRapidLevel,
                        ["rapidDownTo"] = (double)md.RapidDownTo,
                        ["pathsCreated"] = enCreated,
                        ["leadsApplied"] = enLeadsApplied,
                        ["toolPathCount"] = (int)drw.GetToolPathCount(),
                        ["message"] = "Engrave operation completed"
                    };
                }

                case "alphacam.manual_toolpath":
                {
                    var movesRaw = prms.ContainsKey("moves") ? prms["moves"] as System.Collections.ArrayList : null;
                    if (movesRaw == null || movesRaw.Count == 0) throw new ArgumentException("moves array is required and must not be empty");
                    dynamic drw = _app.ActiveDrawing;
                    dynamic md = _app.CreateMillData();
                    if (prms.ContainsKey("safeRapidLevel")) md.SafeRapidLevel = GetDouble(prms, "safeRapidLevel", 1.0);
                    if (prms.ContainsKey("feedRate")) md.CutFeed = (float)GetDouble(prms, "feedRate", 0);
                    if (prms.ContainsKey("spindleSpeed")) md.SpindleSpeed = (float)GetDouble(prms, "spindleSpeed", 0);
                    // The FIRST move supplies the start point passed to
                    // MillData.ManualToolPath(StartX, StartY, StartZ). Subsequent
                    // moves become Add3DRapid / Add3DLine / Add3DArcPointCenter
                    // calls. Arc centers come from i/j offsets relative to the
                    // PREVIOUS point (G-code convention).
                    var first = movesRaw[0] as Dictionary<string, object>;
                    if (first == null) throw new ArgumentException("moves[0] is not an object");
                    double px = Convert.ToDouble(first["x"]);
                    double py = Convert.ToDouble(first["y"]);
                    double pz = Convert.ToDouble(first["z"]);
                    dynamic mtp = md.ManualToolPath(px, py, pz);
                    int movesAdded = 0;
                    for (int i = 1; i < movesRaw.Count; i++)
                    {
                        var mv = movesRaw[i] as Dictionary<string, object>;
                        if (mv == null) throw new ArgumentException("moves[" + i + "] is not an object");
                        var moveType = ((string)mv["type"]).ToLowerInvariant();
                        double x = Convert.ToDouble(mv["x"]);
                        double y = Convert.ToDouble(mv["y"]);
                        double z = Convert.ToDouble(mv["z"]);
                        switch (moveType)
                        {
                            case "rapid": mtp.Add3DRapid(x, y, z); break;
                            case "feed": mtp.Add3DLine(x, y, z); break;
                            case "arccw":
                            case "arcccw":
                            {
                                double cx = px + (mv.ContainsKey("i") ? Convert.ToDouble(mv["i"]) : 0.0);
                                double cy = py + (mv.ContainsKey("j") ? Convert.ToDouble(mv["j"]) : 0.0);
                                mtp.Add3DArcPointCenter(x, y, z, cx, cy, moveType == "arccw");
                                break;
                            }
                            default: throw new ArgumentException("Unknown move type: " + moveType + " (use rapid/feed/arcCW/arcCCW)");
                        }
                        px = x; py = y; pz = z;
                        movesAdded++;
                    }
                    dynamic mtPaths = mtp.Finish();
                    int mtCreated = 0;
                    try { mtCreated = (int)mtPaths.Count; } catch { }
                    return new Dictionary<string, object>
                    {
                        ["moves"] = movesRaw.Count,
                        ["movesAdded"] = movesAdded,
                        ["pathsCreated"] = mtCreated,
                        ["toolPathCount"] = (int)drw.GetToolPathCount(),
                        ["message"] = "Manual toolpath completed"
                    };
                }

                case "alphacam.create_tool":
                {
                    dynamic tool = _app.CreateTool();
                    // AcamToolType (decoded from interop metadata): 0=TURNING,
                    // 1=SQUARE, 2=BULL, 3=BALL, 4=DRILL, 5=TAP, 6=USER, 7=WHEEL.
                    short toolType = 1;
                    if (prms.ContainsKey("type"))
                    {
                        if (prms["type"] is string)
                        {
                            switch (GetString(prms, "type").ToLowerInvariant())
                            {
                                case "square": case "endmill": toolType = 1; break;
                                case "bull": case "bullnose": toolType = 2; break;
                                case "ball": case "ballnose": toolType = 3; break;
                                case "drill": toolType = 4; break;
                                case "tap": toolType = 5; break;
                                case "user": toolType = 6; break;
                                case "wheel": toolType = 7; break;
                                default: throw new ArgumentException("type must be square/bull/ball/drill/tap/user/wheel or the raw AcamToolType integer");
                            }
                        }
                        else toolType = (short)GetInt(prms, "type", 1);
                    }
                    tool.Type = toolType;
                    tool.Diameter = (float)GetDouble(prms, "diameter");
                    if (prms.ContainsKey("cornerRadius")) tool.CornerRadius = (float)GetDouble(prms, "cornerRadius", 0);
                    if (prms.ContainsKey("length")) tool.Length = (float)GetDouble(prms, "length", 0);
                    if (prms.ContainsKey("depthOfCut")) tool.DepthOfCut = (float)GetDouble(prms, "depthOfCut", 0);
                    if (prms.ContainsKey("number")) tool.Number = GetInt(prms, "number", 0);
                    if (prms.ContainsKey("numberOfTeeth")) tool.NumberOfTeeth = GetInt(prms, "numberOfTeeth", 0);
                    if (prms.ContainsKey("feedPerTooth")) tool.FeedPerTooth = (float)GetDouble(prms, "feedPerTooth", 0);
                    if (prms.ContainsKey("pitch")) tool.Pitch = (float)GetDouble(prms, "pitch", 0);
                    if (prms.ContainsKey("shankDiameter")) tool.ShankDiameter = (float)GetDouble(prms, "shankDiameter", 0);
                    if (prms.ContainsKey("name")) tool.Name = GetString(prms, "name");
                    bool fixedAny = false;
                    if (prms.ContainsKey("fixedFeed")) { tool.FixedFeed = (float)GetDouble(prms, "fixedFeed", 0); fixedAny = true; }
                    if (prms.ContainsKey("fixedSpeed")) { tool.FixedSpeed = (float)GetDouble(prms, "fixedSpeed", 0); fixedAny = true; }
                    if (fixedAny) { try { tool.FixedFeedAndSpeed = true; } catch { } }
                    string savedTo = null;
                    var savePath = GetString(prms, "savePath", "");
                    if (!string.IsNullOrEmpty(savePath)) { tool.SaveAs(savePath); savedTo = savePath; }
                    bool select = GetBool(prms, "select", true);
                    if (select) tool.Select();
                    var ctResult = new Dictionary<string, object>
                    {
                        ["toolId"] = StoreObject(tool),
                        ["type"] = (int)toolType,
                        ["selected"] = select
                    };
                    try { ctResult["diameter"] = (double)tool.Diameter; } catch { }
                    try { ctResult["name"] = (string)tool.Name; } catch { }
                    if (savedTo != null) ctResult["savedTo"] = savedTo;
                    return ctResult;
                }

                case "alphacam.save_tool":
                {
                    var savePath = GetString(prms, "path");
                    var toolId = GetString(prms, "toolId", "");
                    dynamic tool;
                    if (!string.IsNullOrEmpty(toolId))
                    {
                        if (!_objectCache.TryGetValue(toolId, out var rawTool)) throw new InvalidOperationException("Tool " + toolId + " not found in object cache");
                        tool = rawTool;
                    }
                    else
                    {
                        tool = _app.GetCurrentTool();
                        if (tool == null) throw new InvalidOperationException("No current tool. Select one first (alphacam_select_tool) or pass toolId.");
                    }
                    tool.SaveAs(savePath);
                    var stResult = new Dictionary<string, object> { ["path"] = savePath };
                    try { stResult["name"] = (string)tool.Name; } catch { }
                    try { stResult["diameter"] = (double)tool.Diameter; } catch { }
                    return stResult;
                }

                case "alphacam.list_layers":
                {
                    dynamic drw = _app.ActiveDrawing;
                    string activeName = null;
                    try { dynamic act = drw.GetLayer(); if (act != null) activeName = (string)act.Name; } catch { }
                    var layerItems = new List<Dictionary<string, object>>();
                    dynamic layer = null;
                    try { layer = drw.GetFirstLayer(); } catch { }
                    int guard = 0;
                    while (layer != null && guard++ < 1000)
                    {
                        var d = new Dictionary<string, object>();
                        try { d["name"] = (string)layer.Name; } catch { }
                        try { d["visible"] = (bool)layer.Visible; } catch { }
                        try { d["active"] = (bool)layer.Active; } catch { }
                        try { d["locked"] = (bool)layer.Lock; } catch { }
                        try { d["special"] = (int)(short)layer.Special; } catch { }
                        try { d["color"] = (int)layer.Color; } catch { }
                        try { d["geometryCount"] = (int)layer.Geometries.Count; } catch { }
                        layerItems.Add(d);
                        dynamic next = null;
                        try { next = layer.GetNext(); } catch { }
                        layer = next;
                    }
                    return new Dictionary<string, object>
                    {
                        ["count"] = layerItems.Count,
                        ["activeLayer"] = activeName,
                        ["layers"] = layerItems
                    };
                }

                case "alphacam.manage_layer":
                {
                    dynamic drw = _app.ActiveDrawing;
                    var action = GetString(prms, "action").ToLowerInvariant();
                    var layerName = GetString(prms, "name");
                    dynamic layer;
                    if (action == "create")
                    {
                        // CreateLayer returns the existing layer if one already
                        // has this name, so create is idempotent.
                        layer = drw.CreateLayer(layerName);
                    }
                    else
                    {
                        layer = FindLayerByName(drw, layerName);
                        if (layer == null) throw new InvalidOperationException("Layer not found: " + layerName);
                    }
                    switch (action)
                    {
                        case "create":
                            break;
                        case "rename":
                            layer.Name = GetString(prms, "newName");
                            break;
                        case "delete":
                            layer.Delete();
                            return new Dictionary<string, object> { ["action"] = action, ["name"] = layerName, ["deleted"] = true };
                        case "set_visible":
                            layer.Visible = GetBool(prms, "visible", true);
                            break;
                        case "set_active":
                            layer.Active = GetBool(prms, "active", true);
                            break;
                        case "set_current":
                            drw.SetLayer(layer);
                            break;
                        case "set_color":
                            layer.Color = (short)GetInt(prms, "color", 0);
                            break;
                        default:
                            throw new ArgumentException("action must be create/rename/delete/set_visible/set_active/set_current/set_color");
                    }
                    var mlResult = new Dictionary<string, object> { ["action"] = action };
                    try { mlResult["name"] = (string)layer.Name; } catch { }
                    try { mlResult["visible"] = (bool)layer.Visible; } catch { }
                    try { mlResult["active"] = (bool)layer.Active; } catch { }
                    try { mlResult["color"] = (int)layer.Color; } catch { }
                    return mlResult;
                }

                case "alphacam.list_operations":
                {
                    dynamic drw = _app.ActiveDrawing;
                    dynamic ops = drw.Operations;
                    int opCount = 0;
                    try { opCount = (int)ops.Count; } catch { }
                    var opItems = new List<Dictionary<string, object>>();
                    for (int i = 1; i <= opCount; i++)
                    {
                        dynamic op = ops.Item(i);
                        var d = new Dictionary<string, object> { ["operationId"] = StoreObject(op), ["index"] = i };
                        try { d["number"] = (int)op.Number; } catch { }
                        try { d["visible"] = (bool)op.Visible; } catch { }
                        try
                        {
                            dynamic opTool = op.Tool;
                            if (opTool != null)
                            {
                                try { d["toolName"] = (string)opTool.Name; } catch { }
                                try { d["toolDiameter"] = (double)opTool.Diameter; } catch { }
                                try { d["toolNumber"] = (int)opTool.Number; } catch { }
                            }
                        }
                        catch { }
                        try { d["subOperationCount"] = (int)op.SubOperations.Count; } catch { }
                        opItems.Add(d);
                    }
                    return new Dictionary<string, object> { ["count"] = opCount, ["operations"] = opItems };
                }

                case "alphacam.get_operation_info":
                {
                    var opId = GetString(prms, "id");
                    if (!_objectCache.TryGetValue(opId, out var rawOp)) throw new InvalidOperationException("Operation " + opId + " not found in object cache. Call alphacam_list_operations first.");
                    dynamic op = rawOp;
                    var info = new Dictionary<string, object> { ["operationId"] = opId };
                    try { info["number"] = (int)op.Number; } catch { }
                    try { info["originalNumber"] = (int)op.OriginalNumber; } catch { }
                    try { info["visible"] = (bool)op.Visible; } catch { }
                    try
                    {
                        dynamic opTool = op.Tool;
                        if (opTool != null)
                        {
                            var toolInfo = new Dictionary<string, object>();
                            try { toolInfo["name"] = (string)opTool.Name; } catch { }
                            try { toolInfo["diameter"] = (double)opTool.Diameter; } catch { }
                            try { toolInfo["number"] = (int)opTool.Number; } catch { }
                            try { toolInfo["type"] = (int)(short)opTool.Type; } catch { }
                            info["tool"] = toolInfo;
                        }
                    }
                    catch { }
                    var subItems = new List<Dictionary<string, object>>();
                    try
                    {
                        dynamic subs = op.SubOperations;
                        int subCount = (int)subs.Count;
                        for (int i = 1; i <= subCount; i++)
                        {
                            dynamic sub = subs.Item(i);
                            var d = new Dictionary<string, object> { ["index"] = i };
                            try { d["name"] = (string)sub.Name; } catch { }
                            try { d["number"] = (int)sub.Number; } catch { }
                            try { d["opGroup"] = (int)sub.OpGroup; } catch { }
                            try { d["time"] = (double)sub.Time; } catch { }
                            try { d["feedDistance"] = (double)sub.FeedDistance; } catch { }
                            try { d["visible"] = (bool)sub.Visible; } catch { }
                            try { d["toolPathCount"] = (int)sub.ToolPaths.Count; } catch { }
                            try { d["geometryCount"] = (int)sub.Geometries.Count; } catch { }
                            subItems.Add(d);
                        }
                    }
                    catch (Exception ex) { info["subOperationError"] = ex.Message; }
                    info["subOperations"] = subItems;
                    return info;
                }

                case "alphacam.manage_workplane":
                {
                    dynamic drw = _app.ActiveDrawing;
                    var action = GetString(prms, "action").ToLowerInvariant();
                    switch (action)
                    {
                        case "list":
                        {
                            string currentName = null;
                            try { dynamic cur = drw.GetWorkPlane(); if (cur != null) currentName = (string)cur.Name; } catch { }
                            var wpItems = new List<Dictionary<string, object>>();
                            try
                            {
                                dynamic wps = drw.WorkPlanes;
                                int wpCount = (int)wps.Count;
                                for (int i = 1; i <= wpCount; i++)
                                {
                                    dynamic wp = wps.Item(i);
                                    var d = new Dictionary<string, object> { ["workPlaneId"] = StoreObject(wp), ["index"] = i };
                                    try { d["name"] = (string)wp.Name; } catch { }
                                    try { d["id"] = (int)wp.Id; } catch { }
                                    try { d["flat"] = (bool)wp.Flat; } catch { }
                                    try { d["isIdentity"] = (bool)wp.IsIdentity; } catch { }
                                    try { d["isGlobal"] = (bool)wp.IsGlobal; } catch { }
                                    wpItems.Add(d);
                                }
                            }
                            catch { }
                            return new Dictionary<string, object> { ["count"] = wpItems.Count, ["currentWorkPlane"] = currentName, ["workPlanes"] = wpItems };
                        }
                        case "create":
                        {
                            // Three points define the plane: origin (P1), a point
                            // on the local +X axis (P2), a point in the local XY
                            // plane on the +Y side (P3).
                            dynamic wp = drw.CreateWorkPlane(
                                GetDouble(prms, "x1"), GetDouble(prms, "y1"), GetDouble(prms, "z1"),
                                GetDouble(prms, "x2"), GetDouble(prms, "y2"), GetDouble(prms, "z2"),
                                GetDouble(prms, "x3"), GetDouble(prms, "y3"), GetDouble(prms, "z3"));
                            var wpName = GetString(prms, "name", "");
                            if (!string.IsNullOrEmpty(wpName)) { try { wp.Name = wpName; } catch { } }
                            if (GetBool(prms, "setCurrent", false)) drw.SetWorkPlane(wp);
                            var d2 = new Dictionary<string, object> { ["workPlaneId"] = StoreObject(wp), ["action"] = action };
                            try { d2["name"] = (string)wp.Name; } catch { }
                            return d2;
                        }
                        case "set":
                        {
                            dynamic wp = ResolveWorkPlane(drw, prms);
                            drw.SetWorkPlane(wp);
                            var d3 = new Dictionary<string, object> { ["action"] = action };
                            try { d3["name"] = (string)wp.Name; } catch { }
                            return d3;
                        }
                        case "set_origin":
                        {
                            dynamic wp = null;
                            if (prms.ContainsKey("workPlaneId") || prms.ContainsKey("name")) wp = ResolveWorkPlane(drw, prms);
                            else { try { wp = drw.GetWorkPlane(); } catch { } }
                            if (wp == null) throw new InvalidOperationException("No current work plane. Pass name or workPlaneId, or set a work plane first.");
                            wp.SetOrigin(GetDouble(prms, "originX", 0), GetDouble(prms, "originY", 0), GetDouble(prms, "originZ", 0));
                            var d4 = new Dictionary<string, object> { ["action"] = action };
                            try { d4["name"] = (string)wp.Name; } catch { }
                            return d4;
                        }
                        case "cancel":
                        {
                            drw.CancelWorkPlane();
                            return new Dictionary<string, object> { ["action"] = action, ["message"] = "Work plane cancelled (back to global coordinates)" };
                        }
                        default:
                            throw new ArgumentException("action must be list/create/set/set_origin/cancel");
                    }
                }

                case "alphacam.transform_geometry":
                {
                    // Move / rotate / mirror / scale one or more geometries.
                    // copy=true transforms a copy instead of the original;
                    // copies=N makes an ARRAY: each successive copy is the
                    // previous copy transformed again (linear/rotary pattern).
                    var geoList2 = ResolveGeometryList(prms);
                    var op = GetString(prms, "operation").ToLowerInvariant();
                    bool copy = GetBool(prms, "copy", false);
                    int copies = GetInt(prms, "copies", 0);
                    if (copies > 0) copy = true;
                    if (copy && copies <= 0) copies = 1;
                    dynamic drw = _app.ActiveDrawing;
                    var resultIds = new List<string>();
                    foreach (var kv in geoList2)
                    {
                        if (!copy)
                        {
                            dynamic geo = kv.Value;
                            ApplyTransform(geo, op, prms);
                            resultIds.Add(kv.Key);
                        }
                        else
                        {
                            dynamic source = kv.Value;
                            for (int c = 0; c < copies; c++)
                            {
                                dynamic dup = source.Copy();
                                ApplyTransform(dup, op, prms);
                                resultIds.Add(StoreObject(dup));
                                source = dup; // chain: copy N is transform applied N times
                            }
                        }
                    }
                    try { drw.Redraw(); } catch { }
                    return new Dictionary<string, object>
                    {
                        ["operation"] = op,
                        ["copy"] = copy,
                        ["copies"] = copy ? copies : 0,
                        ["geometryIds"] = resultIds,
                        ["message"] = "Transform applied"
                    };
                }

                case "alphacam.offset_geometry":
                {
                    // Path.Offset(Distance, Side) — side is AcamToolSide
                    // (acamLEFT=1, acamRIGHT=-1) relative to path DIRECTION.
                    // For a CW closed path, left = outside; reverse for CCW.
                    var geoList2 = ResolveGeometryList(prms);
                    var dist = GetDouble(prms, "distance");
                    var sideStr2 = GetString(prms, "side", "left").ToLowerInvariant();
                    int side;
                    switch (sideStr2)
                    {
                        case "left": side = 1; break;
                        case "right": side = -1; break;
                        default: throw new ArgumentException("side must be left/right (relative to path direction; check Path.CW)");
                    }
                    bool deleteOriginal = GetBool(prms, "deleteOriginal", false);
                    var newIds = new List<string>();
                    foreach (var kv in geoList2)
                    {
                        dynamic geo = kv.Value;
                        dynamic offPaths = geo.Offset(dist, (short)side);
                        newIds.AddRange(StorePathsCollection(offPaths));
                        if (deleteOriginal)
                        {
                            try { geo.Delete(); } catch { }
                            RemoveFromCache(kv.Key);
                        }
                    }
                    return new Dictionary<string, object>
                    {
                        ["distance"] = dist, ["side"] = sideStr2,
                        ["deletedOriginals"] = deleteOriginal,
                        ["geometryIds"] = newIds,
                        ["message"] = "Offset created"
                    };
                }

                case "alphacam.fillet_geometry":
                {
                    // Fillet or chamfer all external sharp corners on the
                    // path(s). Internal corners are left alone (Alphacam rule).
                    var geoList2 = ResolveGeometryList(prms);
                    var mode = GetString(prms, "mode", "fillet").ToLowerInvariant();
                    int touched = 0;
                    foreach (var kv in geoList2)
                    {
                        dynamic geo = kv.Value;
                        if (mode == "fillet")
                            geo.Fillet(GetDouble(prms, "radius"));
                        else if (mode == "chamfer")
                        {
                            var d1 = GetDouble(prms, "dist1", GetDouble(prms, "radius", 0));
                            var d2 = GetDouble(prms, "dist2", d1);
                            geo.Chamfer(d1, d2);
                        }
                        else throw new ArgumentException("mode must be fillet/chamfer");
                        touched++;
                    }
                    return new Dictionary<string, object>
                    {
                        ["mode"] = mode, ["geometryCount"] = touched,
                        ["message"] = "Corners " + (mode == "fillet" ? "filleted" : "chamfered") + " (external sharp corners only)"
                    };
                }

                case "alphacam.join_geometries":
                {
                    // Join selected geometries end-to-end into continuous
                    // paths (Drawing.Join). The inputs are CONSUMED — their
                    // ids become invalid; use the returned ids instead.
                    dynamic drw = _app.ActiveDrawing;
                    List<KeyValuePair<string, object>> geoList2 = null;
                    if (GetBool(prms, "all", false))
                    {
                        drw.SetGeosSelected(true);
                    }
                    else
                    {
                        geoList2 = ResolveGeometryList(prms);
                        SelectGeometries(drw, geoList2);
                    }
                    // Remove input ids from the cache BEFORE storing results:
                    // Drawing.Join can return a path whose IUnknown matches an
                    // input path, and StoreObject would dedupe to the stale
                    // input id (which used to then get removed — orphaning the
                    // result). Found live 2026-07-20.
                    if (geoList2 != null)
                        foreach (var kv in geoList2) RemoveFromCache(kv.Key);
                    dynamic joined = drw.Join();
                    var newIds = StorePathsCollection(joined);
                    return new Dictionary<string, object>
                    {
                        ["resultCount"] = newIds.Count,
                        ["geometryIds"] = newIds,
                        ["message"] = "Join complete — input geometry ids are no longer valid"
                    };
                }

                case "alphacam.explode_geometry":
                {
                    // Path.Explode — every element becomes its own path. The
                    // original is deleted by Alphacam.
                    var geoList2 = ResolveGeometryList(prms);
                    var newIds = new List<string>();
                    foreach (var kv in geoList2)
                    {
                        dynamic geo = kv.Value;
                        // Remove from cache BEFORE storing pieces (same
                        // id-recycling hazard as join — see that handler).
                        RemoveFromCache(kv.Key);
                        dynamic pieces = geo.Explode();
                        newIds.AddRange(StorePathsCollection(pieces));
                    }
                    return new Dictionary<string, object>
                    {
                        ["resultCount"] = newIds.Count,
                        ["geometryIds"] = newIds,
                        ["message"] = "Explode complete — input geometry ids are no longer valid"
                    };
                }

                case "alphacam.get_extent":
                {
                    dynamic drw = _app.ActiveDrawing;
                    double x1 = 0, y1 = 0, z1 = 0, x2 = 0, y2 = 0, z2 = 0;
                    // COM byref params: the dynamic binder copies updated
                    // values back into the locals.
                    drw.GetExtent(ref x1, ref y1, ref z1, ref x2, ref y2, ref z2);
                    return new Dictionary<string, object>
                    {
                        ["minX"] = x1, ["minY"] = y1, ["minZ"] = z1,
                        ["maxX"] = x2, ["maxY"] = y2, ["maxZ"] = z2,
                        ["sizeX"] = x2 - x1, ["sizeY"] = y2 - y1, ["sizeZ"] = z2 - z1,
                        ["geometryCount"] = (int)drw.GetGeoCount(),
                        ["toolPathCount"] = (int)drw.GetToolPathCount()
                    };
                }

                case "alphacam.export_file":
                {
                    dynamic drw = _app.ActiveDrawing;
                    var exPath = GetString(prms, "path");
                    var format = GetString(prms, "format", "").ToLowerInvariant();
                    if (string.IsNullOrEmpty(format))
                    {
                        var ext = (Path.GetExtension(exPath) ?? "").TrimStart('.').ToLowerInvariant();
                        format = ext;
                    }
                    switch (format)
                    {
                        case "dxf":
                            drw.SaveDxfFile(exPath, GetBool(prms, "visibleOnly", true), (short)GetInt(prms, "decimalPlaces", 4));
                            break;
                        case "emf":
                            drw.SaveEmfFile(exPath, GetBool(prms, "drawAxes", false), GetBool(prms, "drawBackground", false));
                            break;
                        case "wmf":
                            drw.SaveWmfFile(exPath, GetBool(prms, "drawAxes", false), GetBool(prms, "drawBackground", false));
                            break;
                        case "stl":
                            // AcamSTLType: 1=STL models, 2=facet surfaces, 4=facet parasolid
                            drw.SaveStlFile(exPath, (short)GetInt(prms, "stlType", 1), GetDouble(prms, "tolerance", 0.01));
                            break;
                        case "iges":
                            drw.SaveIgesFile(exPath, GetBool(prms, "visibleOnly", true), GetInt(prms, "units", 0));
                            break;
                        default:
                            throw new ArgumentException("format must be dxf/emf/wmf/stl/iges (or use a path with that extension)");
                    }
                    return new Dictionary<string, object>
                    {
                        ["path"] = exPath, ["format"] = format,
                        ["exists"] = File.Exists(exPath),
                        ["message"] = "Export complete"
                    };
                }

                case "alphacam.insert_drawing":
                {
                    // Insert another Alphacam drawing (part) into the current
                    // drawing at an XY(Z) offset — the manual-nesting workflow.
                    dynamic drw = _app.ActiveDrawing;
                    var insPath = GetString(prms, "path");
                    if (!File.Exists(insPath)) throw new ArgumentException("File not found: " + insPath);
                    var before = SnapshotGeometryUnknowns(drw);
                    drw.InsertDrawing(insPath, GetDouble(prms, "x", 0), GetDouble(prms, "y", 0), GetDouble(prms, "z", 0));
                    // `drw` as primary: matches no geometry, so the diff
                    // returns every newly added path.
                    var added = DiffNewGeometries(drw, before, (object)drw);
                    var newIds = new List<string>();
                    foreach (var g in added) newIds.Add(StoreObject(g));
                    return new Dictionary<string, object>
                    {
                        ["path"] = insPath,
                        ["geometriesAdded"] = newIds.Count,
                        ["geometryIds"] = newIds,
                        ["message"] = "Drawing inserted"
                    };
                }

                case "alphacam.set_tool_side_auto":
                {
                    // Drawing.SetToolSideAuto on selected geometries.
                    // acamToolSideCUT=0: outside of outermost, inside of inner
                    // (cut-out parts with holes). acamToolSidePOCKET=1: inside
                    // of outermost, outside of inner (pocketing with islands).
                    dynamic drw = _app.ActiveDrawing;
                    var modeStr = GetString(prms, "mode", "cut").ToLowerInvariant();
                    short autoType;
                    switch (modeStr)
                    {
                        case "cut": autoType = 0; break;
                        case "pocket": autoType = 1; break;
                        default: throw new ArgumentException("mode must be cut/pocket");
                    }
                    int selCount;
                    if (prms.ContainsKey("geometryId") || prms.ContainsKey("geometryIds") ||
                        GetBool(prms, "selected", false) || GetBool(prms, "interactive", false))
                    {
                        var geoList2 = ResolveGeometryList(prms);
                        SelectGeometries(drw, geoList2);
                        selCount = geoList2.Count;
                    }
                    else
                    {
                        drw.SetGeosSelected(true);
                        selCount = (int)drw.GetGeoCount();
                    }
                    drw.SetToolSideAuto(autoType);
                    drw.Geometries.Selected = false;
                    return new Dictionary<string, object>
                    {
                        ["mode"] = modeStr, ["geometryCount"] = selCount,
                        ["message"] = "Tool sides set automatically"
                    };
                }

                // alphacam.undo was REMOVED 2026-07-20: Drawing.Undo treats the
                // whole un-checkpointed COM session as one transaction and wiped
                // an entire drawing during the live demo. Re-add only with
                // Path.SaveForUndo checkpointing on every mutation handler.

                case "alphacam.user_select":
                {
                    // Interactive: the USER picks geometries in the Alphacam
                    // window (single picks or a selection window), then
                    // right-clicks/Escape to finish. Blocks until they do.
                    dynamic drw = _app.ActiveDrawing;
                    var promptText = GetString(prms, "prompt", "Select geometries for the agent, then press Escape");
                    dynamic picked = drw.UserSelectMultiGeosCollection(promptText, 0);
                    var ids = StorePathsCollection(picked);
                    return new Dictionary<string, object>
                    {
                        ["count"] = ids.Count,
                        ["geometryIds"] = ids,
                        ["message"] = ids.Count == 0 ? "User selected nothing (or cancelled)" : "User selection captured"
                    };
                }

                case "alphacam.order_nested_toolpaths":
                {
                    // The "Order Toolpaths in Nested Sheets" command — run
                    // after a multi-sheet nest, BEFORE NC output, so toolpaths
                    // are sequenced sheet by sheet. mode=current uses
                    // OrderToolPathsInCurrentNestedSheetOrder instead.
                    dynamic drw = _app.ActiveDrawing;
                    var ordMode = GetString(prms, "mode", "nested").ToLowerInvariant();
                    if (ordMode == "current") drw.OrderToolPathsInCurrentNestedSheetOrder();
                    else drw.OrderToolPathsInNestedSheets();
                    return new Dictionary<string, object>
                    {
                        ["mode"] = ordMode,
                        ["toolPathCount"] = (int)drw.GetToolPathCount(),
                        ["message"] = "Nested toolpath ordering applied"
                    };
                }

                case "alphacam.save_part":
                {
                    // Extract geometries to their own .ard part file WITHOUT
                    // touching the working drawing: copy each geometry, move
                    // the copy into an invisible temp drawing
                    // (App.CreateTempDrawing + Path.MoveToDrawing), save it.
                    // This is the part-file producer for nesting.
                    var partPath = GetString(prms, "path");
                    var geoListP = ResolveGeometryList(prms);
                    dynamic temp = _app.CreateTempDrawing();
                    int movedCount = 0;
                    foreach (var kv in geoListP)
                    {
                        dynamic src = kv.Value;
                        dynamic dup = src.Copy();
                        dup.MoveToDrawing(temp);
                        movedCount++;
                    }
                    temp.SaveAs(partPath);
                    try { Marshal.ReleaseComObject(temp); } catch { }
                    return new Dictionary<string, object>
                    {
                        ["path"] = partPath,
                        ["geometryCount"] = movedCount,
                        ["exists"] = File.Exists(partPath),
                        ["message"] = "Part file saved"
                    };
                }

                case "alphacam.create_nest_list":
                {
                    // Write an Alphacam nest list (.anl) in the MODERN
                    // $VERSION 3.60 format (reverse-engineered 2026-07-20 by
                    // having Alphacam 2025 re-save one of our legacy lists).
                    // Sheets are NOT part of the file — they are added at
                    // nest time (NestData.AddSheet), matching the UI.
                    var listPath = GetString(prms, "path");
                    var itemsRaw = prms.ContainsKey("items") ? prms["items"] as System.Collections.ArrayList : null;
                    if (itemsRaw == null || itemsRaw.Count == 0) throw new ArgumentException("items array is required (each: {path, quantity, priority, rotation, mirror, extraPartGap, maxPerSheet})");
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    short direction = ParseNestDirection(prms, 5); // UI default: pack to bottom-left
                    var nestMethod = GetString(prms, "nestingMethod", "trueshape").ToLowerInvariant() == "rect" ? "RECT" : "VERONEST";
                    // toolPaths may arrive as bool, number, or STRING ("false")
                    // — typeless MCP schema fields get stringified by some
                    // clients, and Convert.ToInt32("false") throws.
                    int tp = 1;
                    if (prms.ContainsKey("toolPaths"))
                    {
                        var tpRaw = prms["toolPaths"];
                        if (tpRaw is bool tpB) tp = tpB ? 1 : 0;
                        else
                        {
                            var tpS = Convert.ToString(tpRaw).Trim().ToLowerInvariant();
                            if (tpS == "true") tp = 1;
                            else if (tpS == "false") tp = 0;
                            else tp = Convert.ToInt32(tpRaw);
                        }
                    }
                    var lines = new List<string>
                    {
                        "$VERSION 3.60",
                        "",
                        "$SETUP",
                        nestMethod + "\t'Nesting method (TRUE = True Shape, RECT = Rectangular)",
                        tp + "\t'Tool Paths (0=Geometry, 1=Tool Paths, 2=Tool Paths with Enclosed Geometries)",
                        GetDouble(prms, "gap", 0.25).ToString("F15", inv) + "\t'Gap Between Parts",
                        GetDouble(prms, "leadGap", 0).ToString("F15", inv) + "\t'Extra Lead-in Gap",
                        (GetBool(prms, "subroutines", false) ? "1" : "0") + "\t'Subroutines (0/1)",
                        direction + "\t'Nest Direction (0=Pack to left, 1=Pack to bottom, 2=Pack to right, 3=Pack to top, 4=pack to top-left, 5=pack to bottom-left, 6=pack to bottom-right, 7=pack to top-right)",
                        GetDouble(prms, "resolution", 0.1).ToString("F6", inv) + "\t'Search resolution (smaller is slower but more accurate, higher is faster but less accurate)",
                        GetDouble(prms, "timePerSheet", 2).ToString("F6", inv) + "\t'Time per sheet, in seconds (0-10). Provides an upper limit to the amount of time RadNest will spend trying nests for each sheet",
                        GetInt(prms, "sheetOrder", 0) + "\t'Sheet order (0=Best utilisation, 1=Picked order)",
                        GetDouble(prms, "totalTime", 30).ToString("F6", inv) + "\t'Total time, in seconds.",
                        GetInt(prms, "offcutPreference", 0) + "\t'Offcut Preference (0 = Vertical Offcut, 1 = Horizontal Offcut)",
                        (GetBool(prms, "evenlySpaced", false) ? "1" : "0") + "\t'Evenly Spaced Parts (0 = Disabled, 1 = Enabled)",
                        "1\t'Optimized ToolPath Overlap Mode (0 = Disabled, 1 = Enabled)",
                        "0\t'Allow Parts to include Solid Parts (0 = No, 1 = Yes)",
                        (GetBool(prms, "strictPriorities", false) ? "1" : "0") + "\t'Force strict Part Priorities (0 = No, 1 = Yes)",
                        GetDouble(prms, "sheetEdgeGap", 0).ToString("F15", inv) + "\t'Gap at Sheet Edge",
                        "0\t'Sheet Alignment (0 = Top, 1 = Bottom)",
                        "0.000000\t'Sheet Alignment Z Level",
                        "0\t'Single Instance of Identical Sheets",
                        "0.000000\t'Inactivity timeout, in seconds.",
                        "",
                        "$EXTENSIONS",
                        "RepeatRowColumn     : 0",
                        "ToolPathSort        : 0,0,0,0,0",
                        "PreserveSheetEdge   : 0",
                        "PartAsGroup         : 0",
                        "RotatedFirst        : 0",
                        "AssistedNest        : 0",
                        "SuppressRedraw      : 0",
                        "PostProcessGroups   : 0",
                        "PreventApertureNest : 0",
                        "UseNameIdentifiers  : 1",
                        "RollSplit           : 0,1000.000,900.000",
                        "BridgedNesting      : 0,1,1.000,1,4.000,1",
                        "CutSmallFirst       : 0",
                        "QuantityMerge       : 0",
                        "QuantityMultiplier  : 0,1,0",
                        "OnionSkin           : 0,0,0,0,0,0",
                        "NestSmallToLarge    : 0",
                        "CommonLineCut       : 0",
                        "JoinSawCuts         : 0,1,1,0",
                        "ProcessWaste        : 0,400,300,1,2,,0,0,0,20",
                        "MinimiseSheetPatterns: 0"
                    };
                    int itemCount = 0;
                    var missing = new List<string>();
                    foreach (var o in itemsRaw)
                    {
                        var item = o as Dictionary<string, object>;
                        if (item == null) throw new ArgumentException("items[" + itemCount + "] is not an object");
                        var pPath = GetString(item, "path");
                        if (!File.Exists(pPath)) missing.Add(pPath);
                        itemCount++;
                        lines.Add("");
                        lines.Add("$ITEM " + itemCount);
                        lines.Add(pPath);
                        lines.Add(GetInt(item, "quantity", 1) + "\t' Number Required");
                        lines.Add(GetInt(item, "priority", itemCount) + "\t' Priority");
                        lines.Add("0 " + GetDouble(item, "rotation", 90).ToString("F2", inv) + "\t' Rotation Angle");
                        lines.Add((GetBool(item, "mirror", false) ? "1" : "0") + "\t' Allow Mirror (0/1)");
                        lines.Add((GetBool(item, "tryRotatedFirst", false) ? "1" : "0") + "\t' Try Rotated Part First (0/1)");
                        lines.Add(GetInt(item, "maxPerSheet", 0) + "\t' Maximum number of parts on each sheet");
                        lines.Add("0\t' Quality Zone (0 for none else 1 to 4)");
                        lines.Add(GetInt(item, "kitNumber", 0) + "\t' Kit Number (or 0 for none)");
                        lines.Add("0\t' Ignore 3D Paths when determining this part's nest shape (0=No, 1=Yes)");
                        lines.Add("0\t' Include Solid Parts when nesting Part (0 = No, 1 = Yes)");
                        lines.Add(GetDouble(item, "extraPartGap", 0).ToString("F6", inv) + "\t' Extra Part Gap");
                        lines.Add("0\t' Ignore Paths on Work Planes (except Sawing) when determining this part's nest shape (0=No, 1=Yes)");
                    }
                    File.WriteAllText(listPath, string.Join("\r\n", lines) + "\r\n", Encoding.ASCII);
                    var cnResult = new Dictionary<string, object>
                    {
                        ["path"] = listPath,
                        ["format"] = "3.60",
                        ["itemCount"] = itemCount,
                        ["sheetEdgeGap"] = GetDouble(prms, "sheetEdgeGap", 0),
                        ["message"] = "Nest list written (sheets are added at nest time, not stored in the list)"
                    };
                    if (missing.Count > 0) cnResult["missingPartFiles"] = missing;
                    return cnResult;
                }

                case "alphacam.nest":
                {
                    // Run a nest: read the .anl (parts + quantities + rotation
                    // rules), add sheets, DoNest into the ACTIVE drawing.
                    // Sheets come from drawing geometry (sheet geometryId) or
                    // are created as rectangles (width/height/x/y).
                    // sheetMargin > 0 shrinks each sheet boundary inward by
                    // that amount (parts keep off the sheet edges).
                    var listPath = GetString(prms, "nestListPath");
                    if (!File.Exists(listPath)) throw new ArgumentException("Nest list not found: " + listPath);
                    dynamic drw = _app.ActiveDrawing;
                    dynamic nd = drw.CreateNestData(listPath);
                    if (prms.ContainsKey("gap")) nd.Gap = GetDouble(prms, "gap", 0.25);
                    if (prms.ContainsKey("leadGap")) nd.LeadGap = GetDouble(prms, "leadGap", 0);
                    if (prms.ContainsKey("resolution")) nd.Resolution = GetDouble(prms, "resolution", 0.1);
                    if (prms.ContainsKey("subroutines")) nd.Subroutines = GetBool(prms, "subroutines", false);
                    if (prms.ContainsKey("direction")) nd.Direction = ParseNestDirection(prms, 0);
                    var sheetsRaw = prms.ContainsKey("sheets") ? prms["sheets"] as System.Collections.ArrayList : null;
                    if (sheetsRaw == null || sheetsRaw.Count == 0) throw new ArgumentException("sheets array is required (each: {geometryId | width+height(+x,y), thickness, quantity, material})");
                    // Sheet-edge gap is a NEST LIST setting ("Gap at Sheet
                    // Edge" in the .anl $SETUP) — set it in create_nest_list,
                    // NOT here. The old offset-geometry hack is gone.
                    if (prms.ContainsKey("sheetMargin")) throw new ArgumentException("sheetMargin was removed — pass sheetEdgeGap to alphacam_create_nest_list instead (it is a nest-list option, not a sheet property)");
                    // Default material: the drawing's current material if set,
                    // else "MDF" — the name is cosmetic in this shop's
                    // workflow (no offcut management) but AddSheet crashes on
                    // an empty string, so it must be SOMETHING.
                    string defaultMaterial = "";
                    try { dynamic curMat = drw.GetMaterial(); if (curMat != null) defaultMaterial = (string)curMat.Name; } catch { }
                    if (string.IsNullOrEmpty(defaultMaterial)) defaultMaterial = "MDF";
                    var sheetIds = new List<string>();
                    int sheetNo = 0;
                    foreach (var o in sheetsRaw)
                    {
                        var sheet = o as Dictionary<string, object>;
                        if (sheet == null) throw new ArgumentException("sheets[" + sheetNo + "] is not an object");
                        sheetNo++;
                        dynamic sheetGeo;
                        var sheetGeoId = GetString(sheet, "geometryId", "");
                        if (!string.IsNullOrEmpty(sheetGeoId))
                        {
                            if (!_objectCache.TryGetValue(sheetGeoId, out var rawSheet)) throw new InvalidOperationException("Sheet geometry " + sheetGeoId + " not found in object cache");
                            sheetGeo = rawSheet;
                        }
                        else
                        {
                            var sx = GetDouble(sheet, "x", 0);
                            var sy = GetDouble(sheet, "y", 0);
                            sheetGeo = drw.CreateRectangle(sx, sy, sx + GetDouble(sheet, "width"), sy + GetDouble(sheet, "height"));
                        }
                        var mat = GetString(sheet, "material", defaultMaterial);
                        // AddSheet SEH-crashes on an empty material name —
                        // refuse with a real error instead.
                        if (string.IsNullOrEmpty(mat))
                            throw new ArgumentException("No material name. Pass sheets[].material, or set the drawing material first (alphacam_set_material) — AddSheet crashes Alphacam on an empty name.");
                        Log("nest: AddSheet mat=" + mat);
                        nd.AddSheet(sheetGeo, mat, GetDouble(sheet, "thickness", 0.75), GetInt(sheet, "quantity", 1));
                        sheetIds.Add(StoreObject(sheetGeo));
                    }
                    int geoBefore = (int)drw.GetGeoCount();
                    int tpBefore = (int)drw.GetToolPathCount();
                    Log("nest: DoNest starting (" + sheetIds.Count + " sheets)");
                    nd.DoNest();
                    Log("nest: DoNest finished");
                    int geoAfter = (int)drw.GetGeoCount();
                    int tpAfter = (int)drw.GetToolPathCount();
                    try { drw.ZoomAll(); } catch { }
                    return new Dictionary<string, object>
                    {
                        ["nestList"] = listPath,
                        ["sheetCount"] = sheetIds.Count,
                        ["sheetGeometryIds"] = sheetIds,
                        ["geometriesAdded"] = geoAfter - geoBefore,
                        ["toolPathsAdded"] = tpAfter - tpBefore,
                        ["geometryCount"] = geoAfter,
                        ["toolPathCount"] = tpAfter,
                        ["message"] = "Nest complete"
                    };
                }

                // ════════════════════════════════════════════════════════
                //  GENERIC FULL-API ACCESS (late-bound COM reflection)
                //  Anything ALPHACAM's automation API exposes is callable
                //  through these three commands; the curated handlers above
                //  remain the safe fast path for common workflows.
                // ════════════════════════════════════════════════════════

                // ── 3D models: solids (STL/Parasolid/STEP-as-solid) and surfaces ──
                case "alphacam.list_3d_models":
                {
                    dynamic drw = _app.ActiveDrawing;
                    var solids = new List<object>();
                    foreach (var kv in EnumerateSolidParts(drw)) solids.Add(DescribeSolidPart(kv.Key, kv.Value));
                    var surfaces = new List<object>();
                    foreach (var kv in EnumerateSurfaces(drw)) surfaces.Add(DescribeSurface(kv.Key, kv.Value));
                    // Legacy facet STLs (App.OpenStlFile) show up as ordinary
                    // geometries flagged Path.STL — count them so the agent can
                    // tell "there is an STL on screen" from "there is a model
                    // I can machine".
                    int facetGeos = 0;
                    try
                    {
                        dynamic geos = drw.Geometries;
                        int gcount = (int)geos.Count;
                        if (gcount <= 50000)
                            for (int i = 1; i <= gcount; i++) { try { if ((bool)geos.Item(i).STL) facetGeos++; } catch { } }
                    }
                    catch { }
                    return new Dictionary<string, object>
                    {
                        ["solidCount"] = solids.Count,
                        ["surfaceCount"] = surfaces.Count,
                        ["solids"] = solids,
                        ["surfaces"] = surfaces,
                        ["legacyStlFacetGeometries"] = facetGeos,
                        ["hint"] = solids.Count + surfaces.Count == 0
                            ? (facetGeos > 0
                                ? "Only legacy STL facet geometries are present (not machinable as a model). Re-import with alphacam_import_cad asSolid:true."
                                : "No 3D models. Import one with alphacam_import_cad (STL/STEP/Parasolid) or build surfaces via the API.")
                            : "Pass modelIds to alphacam_machine_3d, or all:true to machine every model."
                    };
                }

                case "alphacam.delete_3d_model":
                {
                    dynamic drw = _app.ActiveDrawing;
                    var targets = Resolve3DModels(prms, drw);
                    int deleted = 0;
                    var failed = new List<string>();
                    foreach (var kv in targets)
                    {
                        try
                        {
                            var sp = AsSolidPart(kv.Value);
                            if (sp != null)
                            {
                                // Delete lives on the IUnknown-only ISolidPartEx.
                                var ex = kv.Value as ISolidPartEx;
                                if (ex == null) throw new InvalidOperationException("SolidPart does not expose ISolidPartEx.Delete");
                                ex.Delete();
                            }
                            else
                            {
                                var su = AsSurface(kv.Value);
                                if (su != null) su.Delete();
                                else { dynamic d = kv.Value; d.Delete(); }
                            }
                            RemoveFromCache(kv.Key);
                            deleted++;
                        }
                        catch (Exception ex) { failed.Add(kv.Key + ": " + ex.Message); }
                    }
                    try { drw.Redraw(); } catch { }
                    var delRes = new Dictionary<string, object> { ["deleted"] = deleted };
                    if (failed.Count > 0) delRes["failed"] = failed;
                    return delRes;
                }

                case "alphacam.machine_3d":
                {
                    dynamic drw = _app.ActiveDrawing;
                    var strategy = GetString(prms, "strategy", "parallel");
                    var key = strategy.ToLowerInvariant().Replace("_", "").Replace("-", "").Replace(" ", "");
                    bool roughing = key == "rough" || key == "roughing" || key == "zcontourroughing" || key == "zcontourrough";
                    short methodCode = roughing ? (short)1 : SurfaceMethodCode(key);

                    var targets = Resolve3DModels(prms, drw);

                    // Tool diameter drives the stepover defaults.
                    double toolDia = 0;
                    try { dynamic curTool = _app.GetCurrentTool(); if (curTool != null) toolDia = (double)curTool.Diameter; } catch { }
                    if (toolDia <= 0) throw new InvalidOperationException("No tool selected — call alphacam_select_tool first (importing with clear:true drops the tool selection).");

                    dynamic md = _app.CreateMillData();

                    // Optional stock model (roughing): Alphacam reads "the
                    // currently selected STL or Solid" at the moment of the
                    // call, so select the stock alone, register it, then
                    // switch the selection to the machining targets.
                    string stockId = prms.ContainsKey("stockModelId") ? GetString(prms, "stockModelId", "") : null;
                    object stockObj = null;
                    if (!string.IsNullOrEmpty(stockId))
                    {
                        if (!_objectCache.TryGetValue(stockId, out stockObj)) throw new InvalidOperationException("stockModelId " + stockId + " not found — use ids from alphacam_list_3d_models");
                        if (AsSolidPart(stockObj) == null && AsSurface(stockObj) == null) throw new ArgumentException("stockModelId " + stockId + " is not a solid or surface");
                        // Remembered so simulate excludes it from the "model".
                        if (!_stockModels.Exists(o => SameModel(o, stockObj))) _stockModels.Add(stockObj);
                    }

                    int solidsSelected, surfacesSelected;
                    Set3DSelection(drw, targets, out solidsSelected, out surfacesSelected);

                    // Plan checks (added 2026-09-29 after a demo cut a ring
                    // instead of a dome): know the model's box, refuse a
                    // clearance plane inside it, and collect warnings that
                    // the agent will see next to the result.
                    var warnings = new List<string>();
                    double res3d_topMin = double.NaN;
                    double mMinX, mMinY, mMinZ, mMaxX, mMaxY, mMaxZ;
                    ModelBounds(targets, out mMinX, out mMinY, out mMinZ, out mMaxX, out mMaxY, out mMaxZ);
                    bool haveModelBounds = !double.IsInfinity(mMinZ);
                    double safeZ = GetDouble(prms, "safeRapidLevel", 1.0);
                    double rapidZ = GetDouble(prms, "rapidDownTo", 0.25);
                    if (haveModelBounds && safeZ <= mMaxZ)
                        throw new ArgumentException("safeRapidLevel " + safeZ + " is not above the model top (maxZ " + Math.Round(mMaxZ, 4) + ") — rapids would cross the part. Use a value above " + Math.Round(mMaxZ, 4) + " (and above the stock top).");
                    if (haveModelBounds && rapidZ < mMaxZ)
                        warnings.Add("rapidDownTo " + rapidZ + " is below the model top (" + Math.Round(mMaxZ, 4) + "): the tool rapids into the part's Z range before feeding. Usually rapidDownTo should sit just above the stock top.");
                    // Material: the drawing's Workpieces collection is the
                    // truth (survives reopening the file / shim restarts);
                    // the session flag is only a fallback.
                    bool materialDefined = _materialDefined;
                    double stockTopZ = double.NaN;
                    try
                    {
                        dynamic wps = drw.Workpieces;
                        int wpc = (int)wps.Count;
                        if (wpc > 0)
                        {
                            materialDefined = true;
                            for (int wi = 1; wi <= wpc; wi++) { try { double wz = (double)wps.Item(wi).MaxZ; if (double.IsNaN(stockTopZ) || wz > stockTopZ) stockTopZ = wz; } catch { } }
                        }
                    }
                    catch { }
                    if (!materialDefined)
                        warnings.Add("No panel/material is defined in the drawing (alphacam_specify_panel / alphacam_specify_material). Roughing takes its stock from it, simulation needs it and the post reads it.");
                    else if (!double.IsNaN(stockTopZ) && safeZ <= stockTopZ)
                        warnings.Add("safeRapidLevel " + safeZ + " is not above the stock top (" + Math.Round(stockTopZ, 4) + ") — rapids would cross the material.");

                    md.SurfaceMCAction = roughing ? (short)2 : (short)0;
                    if (!roughing) md.SurfaceMCMethod = methodCode;
                    md.SurfaceMCAxisType = AxisTypeCode(GetString(prms, "axisType", "3axis"));
                    if (prms.ContainsKey("fiveAxisAngle")) md.SurfaceMC5AxisAngle = GetDouble(prms, "fiveAxisAngle");

                    md.SafeRapidLevel = GetDouble(prms, "safeRapidLevel", 1.0);
                    md.RapidDownTo = GetDouble(prms, "rapidDownTo", 0.25);
                    md.ChordError = (float)GetDouble(prms, "chordError", 0.001);
                    if (prms.ContainsKey("facetToleranceMultiplier")) md.FacetToleranceMultiplier = GetDouble(prms, "facetToleranceMultiplier");

                    // Stepover: WidthOfCut, or CuspHeight (scallop) when given.
                    double stepover = GetDouble(prms, "stepover", toolDia * (roughing ? 0.5 : 0.1));
                    md.WidthOfCut = (float)stepover;
                    if (prms.ContainsKey("cuspHeight")) md.CuspHeight = (float)GetDouble(prms, "cuspHeight");

                    if (roughing)
                    {
                        md.MaxDepthPerCut = (float)GetDouble(prms, "maxDepthPerCut", toolDia * 0.5);
                        md.StockXY = (float)GetDouble(prms, "stockXY", GetDouble(prms, "stock", 0.0));
                        md.StockZ = (float)GetDouble(prms, "stockZ", GetDouble(prms, "stock", 0.0));
                        var pt = GetString(prms, "pocketType", "contour").ToLowerInvariant();
                        md.PocketType = (short)(pt == "linear" ? 1 : pt == "spiral" ? 2 : 0);
                        md.SurfaceMCZOrder = (short)(GetString(prms, "zOrder", "zone").ToLowerInvariant() == "depth" ? 1 : 0);
                        md.SurfaceMCUseFlatAreas = GetBool(prms, "useFlatAreas", false);
                        md.SurfaceMCRoughingUsePreviousMachining = GetBool(prms, "usePreviousMachining", false);
                    }
                    else
                    {
                        md.Stock = (float)GetDouble(prms, "stock", 0.0);
                    }

                    if (prms.ContainsKey("cutDirection"))
                    {
                        var cd = GetString(prms, "cutDirection", "bidirectional").ToLowerInvariant();
                        md.CutDirection = (short)(cd == "conventional" ? 1 : cd == "climb" ? 2 : 0);
                    }
                    if (prms.ContainsKey("millingType"))
                        md.SurfaceMillingType = (short)(GetString(prms, "millingType", "climb").ToLowerInvariant() == "conventional" ? 1 : 0);
                    if (prms.ContainsKey("clockwise")) md.SurfaceMCCW = GetBool(prms, "clockwise", false);
                    if (prms.ContainsKey("upperZ")) md.SurfaceMCUpperZ = GetDouble(prms, "upperZ");
                    if (prms.ContainsKey("lowerZ")) md.SurfaceMCLowerZ = GetDouble(prms, "lowerZ");
                    if (prms.ContainsKey("cutInnerSurfaces")) md.SurfaceMCCutInnerSurfaces = GetBool(prms, "cutInnerSurfaces", false);
                    if (prms.ContainsKey("toolCanUndercut")) md.SurfaceMCToolCanUndercut = GetBool(prms, "toolCanUndercut", false);
                    if (prms.ContainsKey("checkFoulNonMachined")) md.CheckFoulNonMachinedSurfacesOrSolids = GetBool(prms, "checkFoulNonMachined", false);
                    if (prms.ContainsKey("checkFoulCurrentSurface")) md.CheckFoulCurrentSurface = GetBool(prms, "checkFoulCurrentSurface", false);
                    if (prms.ContainsKey("checkFoulOtherMachined")) md.CheckFoulOtherMachinedSurfaces = GetBool(prms, "checkFoulOtherMachined", false);

                    // Rest machining (strategy=restMachining).
                    if (prms.ContainsKey("restType"))
                    {
                        var rt = GetString(prms, "restType", "auto").ToLowerInvariant();
                        md.SurfaceRestMCType = (short)(rt.StartsWith("paralleltocorner") ? 0 : rt.StartsWith("acrosscorner") ? 1 : rt.StartsWith("pencil") ? 2 : 3);
                    }
                    if (prms.ContainsKey("previousToolDiameter")) md.SurfaceRestMCPrevToolDia = GetDouble(prms, "previousToolDiameter");

                    // Radial / spiral extents.
                    if (prms.ContainsKey("startAngle")) md.StartAngle = GetDouble(prms, "startAngle");
                    if (prms.ContainsKey("endAngle")) md.EndAngle = GetDouble(prms, "endAngle");
                    if (prms.ContainsKey("stepAngle")) md.StepAngle = GetDouble(prms, "stepAngle");
                    if (prms.ContainsKey("innerRadius")) md.InnerRadius = GetDouble(prms, "innerRadius");
                    if (prms.ContainsKey("outerRadius")) md.OuterRadius = GetDouble(prms, "outerRadius");

                    // Leads and links.
                    if (prms.ContainsKey("leadsAndLinks")) md.SurfaceMCLeadAndLinks = GetBool(prms, "leadsAndLinks", false);
                    if (prms.ContainsKey("leadArcRadius")) md.SurfaceMCLeadArcRadius = GetDouble(prms, "leadArcRadius");
                    if (prms.ContainsKey("leadExtensionLength")) md.SurfaceMCLeadExtensionLength = GetDouble(prms, "leadExtensionLength");
                    if (prms.ContainsKey("leadRampAngle")) md.SurfaceMCLeadRampAngle = GetDouble(prms, "leadRampAngle");
                    if (prms.ContainsKey("leadRampLength")) md.SurfaceMCLeadRampLength = GetDouble(prms, "leadRampLength");
                    if (prms.ContainsKey("smoothLinks")) md.SurfaceMCLeadSmoothLinks = GetBool(prms, "smoothLinks", false);

                    // Feeds.
                    if (prms.ContainsKey("feedRate")) md.CutFeed = (float)GetDouble(prms, "feedRate", 0);
                    if (prms.ContainsKey("spindleSpeed")) md.SpindleSpeed = (float)GetDouble(prms, "spindleSpeed", 0);
                    if (prms.ContainsKey("downFeed")) md.DownFeed = (float)GetDouble(prms, "downFeed", 0);
                    if (prms.ContainsKey("feedDownDistance")) md.FeedDownDistance = (float)GetDouble(prms, "feedDownDistance", 0);

                    // Containment boundary (closed geometries), drive curve, start points.
                    int boundaryCount = 0;
                    if (prms.ContainsKey("boundaryGeometryIds") || prms.ContainsKey("boundaryGeometryId"))
                    {
                        var bprm = new Dictionary<string, object>();
                        if (prms.ContainsKey("boundaryGeometryIds")) bprm["geometryIds"] = prms["boundaryGeometryIds"];
                        if (prms.ContainsKey("boundaryGeometryId")) bprm["geometryId"] = prms["boundaryGeometryId"];
                        var blist = ResolveGeometryList(bprm);
                        dynamic bcoll = drw.CreatePathCollection();
                        foreach (var kv in blist) { bcoll.Add(kv.Value); boundaryCount++; }
                        md.SetSurfaceMCBoundary(bcoll);
                        if (haveModelBounds)
                        {
                            double bMinX = double.PositiveInfinity, bMinY = double.PositiveInfinity, bMaxX = double.NegativeInfinity, bMaxY = double.NegativeInfinity;
                            foreach (var kv in blist)
                            {
                                double gx0, gy0, gx1, gy1;
                                try { GeometryBounds(kv.Value, out gx0, out gy0, out gx1, out gy1); } catch { continue; }
                                bMinX = Math.Min(bMinX, gx0); bMinY = Math.Min(bMinY, gy0); bMaxX = Math.Max(bMaxX, gx1); bMaxY = Math.Max(bMaxY, gy1);
                            }
                            double slack = toolDia; // a boundary this far inside the footprint leaves visible material
                            if (bMinX > mMinX + slack || bMinY > mMinY + slack || bMaxX < mMaxX - slack || bMaxY < mMaxY - slack)
                                warnings.Add("The boundary (" + Math.Round(bMinX, 3) + "," + Math.Round(bMinY, 3) + ")-(" + Math.Round(bMaxX, 3) + "," + Math.Round(bMaxY, 3) + ") is smaller than the model footprint (" + Math.Round(mMinX, 3) + "," + Math.Round(mMinY, 3) + ")-(" + Math.Round(mMaxX, 3) + "," + Math.Round(mMaxY, 3) + "): material outside it is NOT touched by this operation" + (roughing ? "." : " — roughing terraces there will remain unless another operation covers them."));
                        }
                    }
                    if (prms.ContainsKey("driveCurveGeometryId"))
                    {
                        object dc;
                        var dcId = GetString(prms, "driveCurveGeometryId", "");
                        if (!_objectCache.TryGetValue(dcId, out dc)) throw new InvalidOperationException("driveCurveGeometryId " + dcId + " not found");
                        md.SetSurfaceDriveCurve(dc);
                    }
                    if (prms.ContainsKey("startPoint") && prms.ContainsKey("directionPoint"))
                    {
                        var sp = prms["startPoint"] as Dictionary<string, object>;
                        var dp = prms["directionPoint"] as Dictionary<string, object>;
                        if (sp == null || dp == null) throw new ArgumentException("startPoint/directionPoint must be {x,y,z} objects");
                        md.SetSurfaceMCPoints(GetDouble(sp, "x", 0), GetDouble(sp, "y", 0), GetDouble(sp, "z", 0),
                                              GetDouble(dp, "x", 0), GetDouble(dp, "y", 0), GetDouble(dp, "z", 0));
                    }

                    // Escape hatch: any MillData property by name.
                    var applied = new List<string>();
                    if (prms.ContainsKey("millDataProperties"))
                    {
                        var extra = prms["millDataProperties"] as Dictionary<string, object>;
                        if (extra != null) foreach (var kv in extra) { SetInterfaceProperty(typeof(IMillData), (object)md, kv.Key, kv.Value); applied.Add(kv.Key); }
                    }

                    // Which engine: the classic surface machining code, or the
                    // "Machining Engine" (V9/Planit) used by the modern 3D
                    // roughing dialog. Roughing defaults to the engine.
                    var engine = GetString(prms, "engine", roughing ? "machiningEngine" : "classic").ToLowerInvariant();
                    bool useEngine = engine.StartsWith("machining") || engine == "engine" || engine == "v9";
                    dynamic runner = md;
                    var engineApplied = new List<string>();
                    string stockMode = null;
                    if (stockObj != null && !useEngine)
                    {
                        // Classic engine: Alphacam reads "the currently selected
                        // STL or Solid" at the moment of the call — select the
                        // stock alone, register it, then re-select the targets.
                        int s1, s2;
                        Set3DSelection(drw, new List<KeyValuePair<string, object>> { new KeyValuePair<string, object>(stockId, stockObj) }, out s1, out s2);
                        md.SetStlOrSolidAsMaterial();
                        Set3DSelection(drw, targets, out solidsSelected, out surfacesSelected);
                        stockMode = "MillData.SetStlOrSolidAsMaterial";
                    }
                    if (useEngine)
                    {
                        dynamic me = md.MillDataMachiningEngine;
                        if (stockObj != null)
                        {
                            // Machining Engine: SetStlOrSolidAsMaterial is
                            // refused ("should not be used with
                            // MillDataMachiningEngine"); pass the stock through
                            // SetMaterials(AlphacamObjects) instead. Found 2026-09-29.
                            dynamic mats = drw.CreateAlphacamObjectsCollection();
                            mats.Add(stockObj);
                            me.SetMaterials(mats);
                            stockMode = "MillDataMachiningEngine.SetMaterials";
                        }
                        if (prms.ContainsKey("millType"))
                        {
                            var mt = GetString(prms, "millType", "climb").ToLowerInvariant();
                            me.MillType = (short)(mt == "conventional" ? 1 : mt.StartsWith("optimi") ? 2 : 0);
                        }
                        if (prms.ContainsKey("approachType"))
                        {
                            var at = GetString(prms, "approachType", "automatic").ToLowerInvariant();
                            me.ApproachType = (short)(at.StartsWith("predrill") ? 1 : at == "helix" ? 2 : at == "ramp" ? 3 : 0);
                        }
                        if (prms.ContainsKey("rampAngle")) me.RampAngle = GetDouble(prms, "rampAngle");
                        if (prms.ContainsKey("feedWhenPlunging")) me.FeedWhenPlunging = GetBool(prms, "feedWhenPlunging", false);
                        if (prms.ContainsKey("widthOfCutCleanUp")) me.WidthOfCutCleanUp = GetDouble(prms, "widthOfCutCleanUp");
                        if (prms.ContainsKey("intermediateMaxDepthPerCut")) me.IntermediateMaxDepthPerCut = GetDouble(prms, "intermediateMaxDepthPerCut");
                        if (prms.ContainsKey("intermediateWidthOfCut")) me.IntermediateWidthOfCut = GetDouble(prms, "intermediateWidthOfCut");
                        if (prms.ContainsKey("highSpeedCornering")) me.HighSpeedCornering = GetBool(prms, "highSpeedCornering", false);
                        if (prms.ContainsKey("trochoidal")) me.Trochoidal = GetBool(prms, "trochoidal", false);
                        if (prms.ContainsKey("closeOpenPockets")) me.CloseOpenPockets = GetBool(prms, "closeOpenPockets", false);
                        if (prms.ContainsKey("avoidRollover")) me.AvoidRollover = GetBool(prms, "avoidRollover", false);
                        if (prms.ContainsKey("xyStandOff")) me.XYStandOff = GetDouble(prms, "xyStandOff");
                        if (prms.ContainsKey("minimumRadius")) me.MinimumRadius = GetDouble(prms, "minimumRadius");
                        if (prms.ContainsKey("maxPlungeDepth")) me.MaxPlungeDepth = GetDouble(prms, "maxPlungeDepth");
                        if (prms.ContainsKey("engineProperties"))
                        {
                            var extra = prms["engineProperties"] as Dictionary<string, object>;
                            if (extra != null) foreach (var kv in extra) { SetInterfaceProperty(typeof(IMillDataMachiningEngine), (object)me, kv.Key, kv.Value); engineApplied.Add(kv.Key); }
                        }
                        runner = me;
                    }

                    // Pick the call by what is selected: solids only →
                    // MachineSolid, surfaces only → MachineSurfaces, mixed →
                    // MachineSolidsAndSurfaces.
                    string call;
                    dynamic paths;
                    if (solidsSelected > 0 && surfacesSelected == 0) { call = "MachineSolid"; paths = runner.MachineSolid(); }
                    else if (surfacesSelected > 0 && solidsSelected == 0) { call = "MachineSurfaces"; paths = runner.MachineSurfaces(); }
                    else { call = "MachineSolidsAndSurfaces"; paths = runner.MachineSolidsAndSurfaces(); }

                    var pathIds = StorePathsCollection(paths);
                    int elemTotal = 0; double lenTotal = 0, timeTotal = 0;
                    double tpMinZ = double.PositiveInfinity, tpMaxZ = double.NegativeInfinity;
                    double tpMinX = double.PositiveInfinity, tpMinY = double.PositiveInfinity, tpMaxX = double.NegativeInfinity, tpMaxY = double.NegativeInfinity;
                    int elemsScanned = 0; const int ElemScanCap = 40000;
                    try
                    {
                        int pc = (int)paths.Count;
                        for (int i = 1; i <= pc; i++)
                        {
                            dynamic p = paths.Item(i);
                            try { elemTotal += (int)p.GetElemCount(); } catch { }
                            try { lenTotal += (double)p.Length; } catch { }
                            try { timeTotal += (double)p.TimeForToolPath; } catch { }
                            try
                            {
                                double fx0, fy0, fx1, fy1;
                                fx0 = fy0 = fx1 = fy1 = 0;
                                p.GetFeedExtentXYG(ref fx0, ref fy0, ref fx1, ref fy1); // global XY: local extents lie under a rotated work plane
                                tpMinX = Math.Min(tpMinX, fx0); tpMinY = Math.Min(tpMinY, fy0); tpMaxX = Math.Max(tpMaxX, fx1); tpMaxY = Math.Max(tpMaxY, fy1);
                            }
                            catch { }
                            // Feed-move Z range (rapids excluded), capped so a
                            // huge finish doesn't stall the response.
                            if (elemsScanned < ElemScanCap)
                            {
                                try
                                {
                                    // Bound by the path's own element count:
                                    // GetNext wraps around on closed paths.
                                    int pathElems = 0; try { pathElems = (int)p.GetElemCount(); } catch { }
                                    dynamic e = p.GetFirstElem();
                                    int walked = 0;
                                    while (e != null && elemsScanned < ElemScanCap && walked < pathElems)
                                    {
                                        elemsScanned++; walked++;
                                        bool rapid = false; try { rapid = (bool)e.IsRapid; } catch { }
                                        if (!rapid)
                                        {
                                            double z0 = (double)e.StartZG, z1 = (double)e.EndZG;
                                            tpMinZ = Math.Min(tpMinZ, Math.Min(z0, z1)); tpMaxZ = Math.Max(tpMaxZ, Math.Max(z0, z1));
                                        }
                                        e = e.GetNext();
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                    TopSurfaceInfo topInfo = null;
                    if (haveModelBounds && !double.IsInfinity(tpMinZ))
                    {
                        topInfo = AnalyseTopSurface(drw, solidsSelected > 0, targets, stockTopZ);
                        if (roughing)
                        {
                            double stockZv = GetDouble(prms, "stockZ", GetDouble(prms, "stock", 0.0));
                            // The relevant floor is the lowest point of the
                            // model's TOP surface (what a 3-axis tool can
                            // reach), not the solid's minZ (its underside).
                            double topMin = topInfo.TopMinZ;
                            if (double.IsNaN(topMin)) topMin = mMinZ;
                            double floorGap = tpMinZ - (topMin + stockZv);
                            if (floorGap > 0.02 * Math.Max(1.0, mMaxZ - mMinZ) + 1e-6)
                                warnings.Add("Roughing floor is Z " + Math.Round(tpMinZ, 4) + " but the lowest point of the model's top surface + stockZ is " + Math.Round(topMin + stockZv, 4) + ": up to " + Math.Round(floorGap, 4) + " of material stays above the model in its low areas. Use a smaller maxDepthPerCut, intermediateMaxDepthPerCut, or make sure a finishing pass covers those areas.");
                            res3d_topMin = topMin;
                        }
                        else
                        {
                            double below = mMinZ - tpMinZ;
                            if (below > 0.25 * toolDia)
                                warnings.Add("The tool tip reaches Z " + Math.Round(tpMinZ, 4) + ", " + Math.Round(below, 4) + " below the model's lowest point (" + Math.Round(mMinZ, 4) + "): it is rolling over the model's outer edge. Leave margin stock around the part, contain the operation with a boundary, or accept the roll-off.");
                        }
                        if (!double.IsInfinity(tpMinX))
                        {
                            // Compare against the footprint of material that
                            // actually needs removing (model below the stock
                            // top), not the whole model: a flat top face at
                            // stock level needs no pass. Falls back to the
                            // model bounds when the analysis failed.
                            double cMinX = mMinX, cMinY = mMinY, cMaxX = mMaxX, cMaxY = mMaxY; string what = "model footprint";
                            if (topInfo != null && !double.IsNaN(topInfo.CutMinX)) { cMinX = topInfo.CutMinX; cMinY = topInfo.CutMinY; cMaxX = topInfo.CutMaxX; cMaxY = topInfo.CutMaxY; what = "material-to-remove footprint"; }
                            double m = toolDia; // uncovered margin larger than a tool diameter is a real gap
                            if (tpMinX > cMinX + m || tpMinY > cMinY + m || tpMaxX < cMaxX - m || tpMaxY < cMaxY - m)
                                warnings.Add("Toolpaths span (" + Math.Round(tpMinX, 3) + "," + Math.Round(tpMinY, 3) + ")-(" + Math.Round(tpMaxX, 3) + "," + Math.Round(tpMaxY, 3) + ") but the " + what + " is (" + Math.Round(cMinX, 3) + "," + Math.Round(cMinY, 3) + ")-(" + Math.Round(cMaxX, 3) + "," + Math.Round(cMaxY, 3) + "): part of it is not covered by this operation" + (roughing || key == "restmachining" || key == "rest" || key == "pencil" ? " (expected for strategies that only cut where material remains)" : "") + ".");
                        }
                    }
                    try { drw.Redraw(); } catch { }

                    var res3d = new Dictionary<string, object>
                    {
                        ["strategy"] = strategy,
                        ["action"] = roughing ? "roughing" : "finishing",
                        ["surfaceMCMethod"] = (int)methodCode,
                        ["engine"] = useEngine ? "machiningEngine" : "classic",
                        ["call"] = call,
                        ["solidsSelected"] = solidsSelected,
                        ["surfacesSelected"] = surfacesSelected,
                        ["boundaryCount"] = boundaryCount,
                        ["toolDiameter"] = toolDia,
                        ["stepover"] = stepover,
                        ["chordError"] = (double)md.ChordError,
                        ["safeRapidLevel"] = (double)md.SafeRapidLevel,
                        ["rapidDownTo"] = (double)md.RapidDownTo,
                        ["pathIds"] = pathIds.Count > 40 ? pathIds.GetRange(0, 40) : pathIds,
                        ["pathIdsTruncated"] = pathIds.Count > 40,
                        ["pathCount"] = pathIds.Count,
                        ["elementCount"] = elemTotal,
                        ["totalLength"] = lenTotal,
                        ["timeForToolPath"] = timeTotal,
                        ["toolPathCount"] = (int)drw.GetToolPathCount(),
                        ["message"] = "3D " + (roughing ? "roughing" : strategy) + " completed via " + call
                    };
                    if (stockMode != null) res3d["stockModel"] = new Dictionary<string, object> { ["modelId"] = stockId, ["via"] = stockMode };
                    if (applied.Count > 0) res3d["millDataPropertiesApplied"] = applied;
                    if (engineApplied.Count > 0) res3d["enginePropertiesApplied"] = engineApplied;
                    if (pathIds.Count == 0) warnings.Add("Alphacam returned no toolpaths — check that the models sit within Z range of the tool, the boundary (if any) overlaps them, and the strategy applies to this model type.");
                    if (haveModelBounds) res3d["modelBounds"] = new Dictionary<string, object> { ["minX"] = mMinX, ["minY"] = mMinY, ["minZ"] = mMinZ, ["maxX"] = mMaxX, ["maxY"] = mMaxY, ["maxZ"] = mMaxZ };
                    if (!double.IsNaN(res3d_topMin)) res3d["modelTopSurfaceMinZ"] = Math.Round(res3d_topMin, 4);
                    if (topInfo != null && !double.IsNaN(topInfo.CutMinX))
                        res3d["materialToRemove"] = new Dictionary<string, object> { ["minX"] = Math.Round(topInfo.CutMinX, 3), ["minY"] = Math.Round(topInfo.CutMinY, 3), ["maxX"] = Math.Round(topInfo.CutMaxX, 3), ["maxY"] = Math.Round(topInfo.CutMaxY, 3), ["area"] = Math.Round(topInfo.CutArea, 3), ["stockTop"] = double.IsNaN(stockTopZ) ? (object)"model top" : Math.Round(stockTopZ, 4) };
                    if (!double.IsInfinity(tpMinZ)) res3d["toolpathZRange"] = new Dictionary<string, object> { ["min"] = Math.Round(tpMinZ, 4), ["max"] = Math.Round(tpMaxZ, 4), ["elementsScanned"] = elemsScanned, ["capped"] = elemsScanned >= ElemScanCap };
                    if (!double.IsInfinity(tpMinX)) res3d["toolpathXYExtent"] = new Dictionary<string, object> { ["minX"] = Math.Round(tpMinX, 4), ["minY"] = Math.Round(tpMinY, 4), ["maxX"] = Math.Round(tpMaxX, 4), ["maxY"] = Math.Round(tpMaxY, 4) };
                    if (warnings.Count > 0) res3d["warnings"] = warnings;
                    res3d["nextStep"] = "Run alphacam_simulate to compare the simulated stock against the model before posting.";
                    return res3d;
                }

                case "alphacam.simulate":
                {
                    // Closed loop for 3D work: run Alphacam's solid simulation,
                    // export the resulting stock as STL, export the model as
                    // STL, and compare them top-down (StlCompare). Verified
                    // 2026-09-29: SolidSimulation(false) returns 0 (OK) and
                    // GetUpdatedStockSTL writes <name>.1.stl (binary).
                    dynamic drw = _app.ActiveDrawing;
                    int tpCount = 0; try { tpCount = (int)drw.GetToolPathCount(); } catch { }
                    if (tpCount == 0) throw new InvalidOperationException("No toolpaths to simulate");
                    string outDir = GetString(prms, "outputDir", "");
                    if (string.IsNullOrEmpty(outDir))
                    {
                        try { outDir = (string)drw.Path; } catch { }
                        if (string.IsNullOrEmpty(outDir)) outDir = Path.Combine(Path.GetTempPath(), "AlphacamBridge");
                    }
                    Directory.CreateDirectory(outDir);
                    string baseName = GetString(prms, "name", "");
                    if (string.IsNullOrEmpty(baseName))
                    {
                        try { baseName = Path.GetFileNameWithoutExtension((string)drw.Name); } catch { }
                        if (string.IsNullOrEmpty(baseName)) baseName = "simulation";
                    }
                    double tol = GetDouble(prms, "tolerance", 0.002);
                    double cmpTol = GetDouble(prms, "deviationTolerance", 0.01);
                    int grid = Math.Max(50, Math.Min(600, GetInt(prms, "gridSize", 200)));
                    bool showTools = GetBool(prms, "showTools", false);
                    bool keepOpen = GetBool(prms, "keepSimulationOpen", false);
                    bool compare = GetBool(prms, "compare", true);

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    int simResult = (int)drw.SolidSimulation(showTools);
                    string[] simNames = { "OK", "NoMaterial", "NoDefaultMaterial", "DLLError", "InitError", "Clash", "InvalidTool", "GeneralError", "InvalidTurretGroup" };
                    string simName = simResult >= 0 && simResult < simNames.Length ? simNames[simResult] : simResult.ToString();
                    var simRes = new Dictionary<string, object>
                    {
                        ["simulationResult"] = simResult, ["simulationResultName"] = simName,
                        ["toolPathCount"] = tpCount, ["simulationSeconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1)
                    };
                    if (simResult != 0)
                    {
                        try { if (!keepOpen) drw.CloseSolidSimulation(); } catch { }
                        if (simResult == 1 || simResult == 2) simRes["hint"] = "Define the material first (alphacam_specify_panel or alphacam_specify_material) — the simulation needs a stock definition.";
                        simRes["message"] = "Solid simulation did not run: " + simName;
                        return simRes;
                    }

                    // Export the simulated stock. Alphacam appends ".<index>" to
                    // the name; pick the newest matching file.
                    string stockName = baseName + "-stock";
                    foreach (var old in Directory.GetFiles(outDir, stockName + "*.stl")) { try { File.Delete(old); } catch { } }
                    int stockCount = (int)drw.GetUpdatedStockSTL(outDir, stockName, false, 1.0, tol);
                    string stockPath = null; DateTime newest = DateTime.MinValue;
                    foreach (var f in Directory.GetFiles(outDir, stockName + "*.stl"))
                    {
                        var t = File.GetLastWriteTime(f);
                        if (t > newest) { newest = t; stockPath = f; }
                    }
                    simRes["stockCount"] = stockCount;
                    simRes["stockStlPath"] = stockPath;
                    try { if (!keepOpen) drw.CloseSolidSimulation(); } catch (Exception ex) { Log("CloseSolidSimulation: " + ex.Message); }
                    if (stockPath == null) { simRes["message"] = "Simulation ran but no stock STL was written"; return simRes; }
                    if (!compare) { simRes["message"] = "Simulation complete; stock exported"; return simRes; }

                    // Export the model: STL solids (type 1) or surfaces (type 2).
                    // Scope: modelIds if given, else every solid/surface except
                    // ones that were used as a stock model in machine_3d
                    // (SaveStlFile honours Visible, so the others are hidden
                    // during the export).
                    List<KeyValuePair<string, object>> cmpModels;
                    if (prms.ContainsKey("modelIds") || prms.ContainsKey("modelId")) cmpModels = Resolve3DModels(prms, drw);
                    else
                    {
                        cmpModels = new List<KeyValuePair<string, object>>();
                        cmpModels.AddRange(EnumerateSolidParts(drw)); cmpModels.AddRange(EnumerateSurfaces(drw));
                        cmpModels.RemoveAll(kv => _stockModels.Exists(o => SameModel(o, kv.Value)));
                    }
                    if (cmpModels.Count == 0) { simRes["message"] = "Simulation complete; no 3D model in the drawing to compare against (pass modelIds if only stock models are loaded)"; return simRes; }
                    int solids = cmpModels.FindAll(kv => AsSolidPart(kv.Value) != null).Count;
                    string modelPath = Path.Combine(outDir, baseName + "-model.stl");
                    ExportModelsStl(drw, modelPath, solids > 0 ? 1 : 2, tol, cmpModels);
                    simRes["modelStlPath"] = modelPath;
                    simRes["comparedModelIds"] = cmpModels.ConvertAll(kv => kv.Key);

                    var model = StlCompare.ReadStl(modelPath);
                    var stock = StlCompare.ReadStl(stockPath);
                    var rep = StlCompare.Compare(model, stock, grid, cmpTol, GetInt(prms, "worstSpots", 6));
                    double mx0, my0, mz0, mx1, my1, mz1; StlCompare.Bounds(model, out mx0, out my0, out mz0, out mx1, out my1, out mz1);
                    var spotsR = new List<object>(); foreach (var s in rep.WorstRemaining) spotsR.Add(new Dictionary<string, object> { ["x"] = s.X, ["y"] = s.Y, ["remaining"] = s.Value, ["modelZ"] = s.ModelZ, ["stockZ"] = s.StockZ });
                    var spotsG = new List<object>(); foreach (var s in rep.WorstGouges) spotsG.Add(new Dictionary<string, object> { ["x"] = s.X, ["y"] = s.Y, ["gouge"] = s.Value, ["modelZ"] = s.ModelZ, ["stockZ"] = s.StockZ });
                    simRes["comparison"] = new Dictionary<string, object>
                    {
                        ["method"] = "top-down height map (3-axis); deviations are normal to the model surface (dz x cos slope)",
                        ["steepCells"] = rep.SteepCells,
                        ["gridSize"] = grid,
                        ["deviationTolerance"] = cmpTol,
                        ["modelTriangles"] = model.Count, ["stockTriangles"] = stock.Count,
                        ["modelBounds"] = new Dictionary<string, object> { ["minX"] = mx0, ["minY"] = my0, ["minZ"] = mz0, ["maxX"] = mx1, ["maxY"] = my1, ["maxZ"] = mz1 },
                        ["coveredCells"] = rep.CoveredCells,
                        ["maxRemaining"] = Math.Round(rep.MaxRemaining, 4),
                        ["meanRemaining"] = Math.Round(rep.MeanRemaining, 4),
                        ["maxGouge"] = Math.Round(rep.MaxGouge, 4),
                        ["meanGouge"] = Math.Round(rep.MeanGouge, 4),
                        ["pctWithinTolerance"] = Math.Round(rep.PctWithinTolerance, 1),
                        ["pctRemainingAboveTolerance"] = Math.Round(rep.PctRemainingAboveTolerance, 1),
                        ["pctGougeBeyondTolerance"] = Math.Round(rep.PctGougeBeyondTolerance, 1),
                        ["areaRemainingAboveTolerance"] = Math.Round(rep.AreaRemainingAboveTolerance, 3),
                        ["worstRemaining"] = spotsR,
                        ["worstGouges"] = spotsG,
                    };
                    string verdict;
                    if (rep.MaxGouge > cmpTol) verdict = "GOUGE: the stock is cut below the model by up to " + Math.Round(rep.MaxGouge, 4) + " — check tool, stock/safe levels and rollover at edges.";
                    else if (rep.PctRemainingAboveTolerance > 1.0) verdict = "INCOMPLETE: " + Math.Round(rep.PctRemainingAboveTolerance, 1) + "% of the footprint still carries more than " + cmpTol + " of material (max " + Math.Round(rep.MaxRemaining, 4) + ") — see worstRemaining for where.";
                    else verdict = "OK: within " + cmpTol + " over " + Math.Round(rep.PctWithinTolerance, 1) + "% of the footprint (max remaining " + Math.Round(rep.MaxRemaining, 4) + ").";
                    simRes["verdict"] = verdict;
                    simRes["totalSeconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1);
                    simRes["message"] = "Simulation complete";
                    return simRes;
                }

                case "alphacam.api_invoke":
                {
                    // Batch mode: calls:[{target,member,kind,args,byRef}, ...]
                    // runs sequentially in ONE round-trip (one MCP permission
                    // prompt). Later calls can reference earlier results with
                    // {"$ref": i} (i = zero-based index of the call whose
                    // result handle to use) as target or inside args.
                    if (prms.ContainsKey("calls"))
                    {
                        var calls = prms["calls"] as System.Collections.ArrayList;
                        if (calls == null || calls.Count == 0) throw new ArgumentException("calls must be a non-empty array");
                        bool continueOnError = GetBool(prms, "continueOnError", false);
                        var results = new List<object>();
                        var handles = new List<string>();
                        for (int ci = 0; ci < calls.Count; ci++)
                        {
                            var call = calls[ci] as Dictionary<string, object>;
                            if (call == null) throw new ArgumentException("calls[" + ci + "] is not an object");
                            var resolved = new Dictionary<string, object>(call);
                            resolved["target"] = ResolveBatchRef(call.ContainsKey("target") ? call["target"] : "app", handles, ci);
                            if (call.ContainsKey("args"))
                            {
                                var rawArgs = call["args"] as System.Collections.ArrayList;
                                if (rawArgs != null)
                                {
                                    var newArgs = new System.Collections.ArrayList();
                                    foreach (var a in rawArgs) newArgs.Add(ResolveBatchRefArg(a, handles, ci));
                                    resolved["args"] = newArgs;
                                }
                            }
                            try
                            {
                                var one = InvokeApi(resolved);
                                results.Add(one);
                                var r = one.ContainsKey("result") ? one["result"] as Dictionary<string, object> : null;
                                handles.Add(r != null && r.ContainsKey("objectId") ? Convert.ToString(r["objectId"]) : null);
                            }
                            catch (Exception ex)
                            {
                                results.Add(new Dictionary<string, object> { ["error"] = ex.Message, ["call"] = ci });
                                handles.Add(null);
                                if (!continueOnError)
                                    return new Dictionary<string, object> { ["results"] = results, ["completed"] = ci, ["failedAt"] = ci, ["error"] = ex.Message };
                            }
                        }
                        return new Dictionary<string, object> { ["results"] = results, ["completed"] = calls.Count };
                    }
                    return InvokeApi(prms);
                }

                case "alphacam.api_inspect":
                {
                    var targetStr = GetString(prms, "target", "");
                    if (string.IsNullOrEmpty(targetStr))
                    {
                        // No target: list every live object handle.
                        var handles = new List<object>();
                        foreach (var kv in _objectCache)
                            handles.Add(new Dictionary<string, object> { ["objectId"] = kv.Key, ["comType"] = GetComTypeName(kv.Value) });
                        return new Dictionary<string, object>
                        {
                            ["handleCount"] = handles.Count,
                            ["handles"] = handles,
                            ["roots"] = new List<object> { "app", "drawing" }
                        };
                    }
                    var target = ResolveApiTarget(targetStr);
                    var typeName = GetComTypeName(target);
                    var d = new Dictionary<string, object> { ["target"] = targetStr, ["comType"] = typeName };
                    if (typeName == "_ComObject")
                    {
                        var probed = ProbeInteropInterface(target);
                        if (probed != null)
                        {
                            typeName = probed.Name;
                            d["comType"] = typeName;
                            d["note"] = "Add-in object without a registered type library; members resolved by QueryInterface to the interop interface " + typeName + " (calls go through the vtable).";
                        }
                    }
                    var members = DescribeInteropType(typeName);
                    if (members != null)
                    {
                        d["methods"] = members.Item1;
                        d["properties"] = members.Item2;
                    }
                    d["hint"] = "Use alphacam_api_help for signatures and docs, alphacam_api_invoke to call.";
                    return d;
                }

                case "alphacam.api_release":
                {
                    if (GetBool(prms, "all", false))
                    {
                        var count = _objectCache.Count;
                        foreach (var id in new List<string>(_objectCache.Keys)) RemoveFromCache(id);
                        return new Dictionary<string, object> { ["released"] = count };
                    }
                    var idsToFree = new List<string>();
                    if (prms.ContainsKey("objectIds"))
                    {
                        var arr = prms["objectIds"] as System.Collections.ArrayList;
                        if (arr != null) foreach (var o in arr) { var s = Convert.ToString(o); if (!string.IsNullOrEmpty(s)) idsToFree.Add(s); }
                    }
                    if (prms.ContainsKey("objectId"))
                    {
                        var s = GetString(prms, "objectId", "");
                        if (!string.IsNullOrEmpty(s)) idsToFree.Add(s);
                    }
                    if (idsToFree.Count == 0) throw new ArgumentException("Pass objectId, objectIds, or all:true");
                    int released = 0;
                    foreach (var id in idsToFree) { if (_objectCache.ContainsKey(id)) { RemoveFromCache(id); released++; } }
                    return new Dictionary<string, object> { ["released"] = released };
                }

                default:
                    throw new InvalidOperationException("Unknown method: " + method);
            }
        }

        // ── 3D model helpers ────────────────────────────────────────

        // Solid parts (STL / Parasolid / STEP-as-solid) in the drawing, as
        // (cachedId, comObject). SolidPart objects come from an add-in and
        // must be driven through the interop interface, not IDispatch.
        private List<KeyValuePair<string, object>> EnumerateSolidParts(dynamic drw, bool cache = true)
        {
            var list = new List<KeyValuePair<string, object>>();
            try
            {
                dynamic parts = drw.SolidParts;
                int n = (int)parts.Count;
                for (int i = 1; i <= n; i++)
                {
                    object p = parts.Item(i);
                    list.Add(new KeyValuePair<string, object>(cache ? StoreObject(p) : ("solid_" + i), p));
                }
            }
            catch (Exception ex) { Log("EnumerateSolidParts: " + ex.Message); }
            return list;
        }

        private List<KeyValuePair<string, object>> EnumerateSurfaces(dynamic drw, bool cache = true)
        {
            var list = new List<KeyValuePair<string, object>>();
            try
            {
                dynamic surfs = drw.Surfaces;
                int n = (int)surfs.Count;
                for (int i = 1; i <= n; i++)
                {
                    object s = surfs.Item(i);
                    list.Add(new KeyValuePair<string, object>(cache ? StoreObject(s) : ("surface_" + i), s));
                }
            }
            catch (Exception ex) { Log("EnumerateSurfaces: " + ex.Message); }
            return list;
        }

        private Dictionary<string, object> DescribeSolidPart(string id, object obj)
        {
            var d = new Dictionary<string, object> { ["modelId"] = id, ["kind"] = "solid" };
            var sp = AsSolidPart(obj);
            if (sp == null) { d["error"] = "ISolidPart QueryInterface failed"; return d; }
            try { d["name"] = sp.Name; } catch { }
            try { d["visible"] = sp.Visible; } catch { }
            try { d["selected"] = sp.Selected; } catch { }
            try
            {
                d["minX"] = sp.MinX; d["minY"] = sp.MinY; d["minZ"] = sp.MinZ;
                d["maxX"] = sp.MaxX; d["maxY"] = sp.MaxY; d["maxZ"] = sp.MaxZ;
                d["sizeX"] = sp.MaxX - sp.MinX; d["sizeY"] = sp.MaxY - sp.MinY; d["sizeZ"] = sp.MaxZ - sp.MinZ;
            }
            catch { }
            try { var layer = sp.GetLayer(); if (layer != null) d["layer"] = ((dynamic)layer).Name; } catch { }
            return d;
        }

        private Dictionary<string, object> DescribeSurface(string id, object obj)
        {
            var d = new Dictionary<string, object> { ["modelId"] = id, ["kind"] = "surface" };
            var su = AsSurface(obj);
            if (su == null) { d["error"] = "ISurface QueryInterface failed"; return d; }
            try { d["visible"] = su.Visible; } catch { }
            try { d["selected"] = su.Selected; } catch { }
            try { d["trimmed"] = su.Trimmed; } catch { }
            try { d["group"] = su.Group; } catch { }
            try
            {
                d["minX"] = su.MinX; d["minY"] = su.MinY; d["minZ"] = su.MinZ;
                d["maxX"] = su.MaxX; d["maxY"] = su.MaxY; d["maxZ"] = su.MaxZ;
            }
            catch { }
            try { var layer = su.GetLayer(); if (layer != null) d["layer"] = ((dynamic)layer).Name; } catch { }
            return d;
        }

        private static bool GetModelSelected(object obj)
        {
            var sp = AsSolidPart(obj);
            if (sp != null) { try { return sp.Selected; } catch { return false; } }
            var su = AsSurface(obj);
            if (su != null) { try { return su.Selected; } catch { return false; } }
            try { dynamic d = obj; return (bool)d.Selected; } catch { return false; }
        }

        private static void SetModelSelected(object obj, bool value)
        {
            var sp = AsSolidPart(obj);
            if (sp != null) { sp.Selected = value; return; }
            var su = AsSurface(obj);
            if (su != null) { su.Selected = value; return; }
            dynamic d = obj; d.Selected = value;
        }

        // Union bounding box of solids/surfaces (via the interop interfaces).
        private static void ModelBounds(List<KeyValuePair<string, object>> models, out double minX, out double minY, out double minZ, out double maxX, out double maxY, out double maxZ)
        {
            minX = minY = minZ = double.PositiveInfinity; maxX = maxY = maxZ = double.NegativeInfinity;
            foreach (var kv in models)
            {
                try
                {
                    var sp = AsSolidPart(kv.Value);
                    if (sp != null)
                    {
                        minX = Math.Min(minX, sp.MinX); minY = Math.Min(minY, sp.MinY); minZ = Math.Min(minZ, sp.MinZ);
                        maxX = Math.Max(maxX, sp.MaxX); maxY = Math.Max(maxY, sp.MaxY); maxZ = Math.Max(maxZ, sp.MaxZ);
                        continue;
                    }
                    var su = AsSurface(kv.Value);
                    if (su != null)
                    {
                        minX = Math.Min(minX, su.MinX); minY = Math.Min(minY, su.MinY); minZ = Math.Min(minZ, su.MinZ);
                        maxX = Math.Max(maxX, su.MaxX); maxY = Math.Max(maxY, su.MaxY); maxZ = Math.Max(maxZ, su.MaxZ);
                    }
                }
                catch { }
            }
        }

        // Resolve the 3D models a call targets: modelId/modelIds (from
        // list_3d_models or import_cad), all:true, selected:true (whatever the
        // user has highlighted), or interactive:true (Alphacam prompts the user
        // to pick solids/surfaces).
        private List<KeyValuePair<string, object>> Resolve3DModels(Dictionary<string, object> prms, dynamic drw)
        {
            var all = new List<KeyValuePair<string, object>>();
            all.AddRange(EnumerateSolidParts(drw));
            all.AddRange(EnumerateSurfaces(drw));

            if (GetBool(prms, "interactive", false))
            {
                var promptText = GetString(prms, "prompt", "Select the 3D models (solids/surfaces) for the agent, then press Enter");
                // Flags: acamSelectSURFACES (0x200000); ExtraFlags: acamSelectSOLIDS (8).
                bool ok = false;
                try { ok = (bool)drw.UserSelectMultiObjects2(promptText, 2097152, 8); }
                catch (Exception ex) { throw new InvalidOperationException("UserSelectMultiObjects2 failed: " + ex.Message); }
                if (!ok) throw new InvalidOperationException("User cancelled the selection");
                var picked = all.FindAll(kv => GetModelSelected(kv.Value));
                if (picked.Count == 0) throw new InvalidOperationException("User selected no solids or surfaces");
                return picked;
            }
            if (GetBool(prms, "selected", false))
            {
                var sel = all.FindAll(kv => GetModelSelected(kv.Value));
                if (sel.Count == 0) throw new InvalidOperationException("No solids or surfaces are currently selected in Alphacam");
                return sel;
            }
            if (GetBool(prms, "all", false))
            {
                if (all.Count == 0) throw new InvalidOperationException("The drawing has no solids or surfaces. Import a model with alphacam_import_cad (asSolid) first.");
                return all;
            }
            var ids = new List<string>();
            if (prms.ContainsKey("modelIds"))
            {
                var arr = prms["modelIds"] as System.Collections.ArrayList;
                if (arr != null) foreach (var o in arr) { var s = Convert.ToString(o); if (!string.IsNullOrEmpty(s) && !ids.Contains(s)) ids.Add(s); }
            }
            if (prms.ContainsKey("modelId"))
            {
                var s = GetString(prms, "modelId", "");
                if (!string.IsNullOrEmpty(s) && !ids.Contains(s)) ids.Add(s);
            }
            if (ids.Count == 0)
            {
                // Convenience: exactly one model in the drawing → use it.
                if (all.Count == 1) return all;
                throw new ArgumentException("Pass modelId/modelIds (from alphacam_list_3d_models), all:true, selected:true, or interactive:true");
            }
            var result = new List<KeyValuePair<string, object>>();
            foreach (var id in ids)
            {
                object raw;
                if (!_objectCache.TryGetValue(id, out raw)) throw new InvalidOperationException("Model " + id + " not found in object cache — call alphacam_list_3d_models");
                if (AsSolidPart(raw) == null && AsSurface(raw) == null) throw new ArgumentException(id + " is not a solid or surface (it is " + GetComTypeName(raw) + ")");
                result.Add(new KeyValuePair<string, object>(id, raw));
            }
            return result;
        }

        // Deselect every geometry, solid and surface, then select the given
        // models. Reports how many of each kind ended up selected.
        private void Set3DSelection(dynamic drw, List<KeyValuePair<string, object>> targets, out int solids, out int surfaces)
        {
            solids = 0; surfaces = 0;
            try { drw.Geometries.Selected = false; } catch { }
            foreach (var kv in EnumerateSolidParts(drw, false)) { try { SetModelSelected(kv.Value, false); } catch { } }
            foreach (var kv in EnumerateSurfaces(drw, false)) { try { SetModelSelected(kv.Value, false); } catch { } }
            foreach (var kv in targets)
            {
                SetModelSelected(kv.Value, true);
                if (AsSolidPart(kv.Value) != null) solids++; else surfaces++;
            }
        }

        // AcamSurfaceMCMethod codes, decoded from the interop 2026-09-29.
        private static short SurfaceMethodCode(string key)
        {
            switch (key)
            {
                case "parameterlines": case "parameter": return 0;
                case "zcontours": case "zcontour": case "waterline": case "zfinish": case "zfinishing": case "horizontal": return 1;
                case "parallel": case "xy": case "raster": case "lace": case "alonglinexy": return 2;
                case "projectedcontours": case "projected": case "projectcontours": return 3;
                case "radial": return 4;
                case "spiral": return 5;
                case "restmachining": case "rest": case "pencil": return 6;
                case "drivecurve": case "drivecurves": case "singledrivecurve": return 7;
                case "parallelshallow": case "parallelshallowslopes": case "shallow": return 8;
                case "zcontourssteep": case "zcontoursteep": case "zcontourssteepslopes": case "steep": return 9;
                case "flatareaoffset": case "flatareas": case "flat": return 10;
                case "parallelsteep": case "parallelsteepslopes": return 11;
                case "helicalz": case "helical": return 16;
                case "zcontoursandflat": case "zcontoursandflatareaoffset": case "zcontoursflat": return 17;
                case "cylindricalparallel": case "cylindrical": return 18;
                default:
                    throw new ArgumentException("Unknown strategy '" + key + "'. Use rough, parallel, zContours, zContoursSteep, parallelShallow, parallelSteep, projectedContours, radial, spiral, restMachining, driveCurve, flatAreaOffset, zContoursAndFlat, helicalZ, cylindricalParallel, or parameterLines.");
            }
        }

        // AcamAxis codes: 3=0, 4XZ=1, 4YZ=2, 5=3, 3HELICAL=4, 4XY=5, 3LOCAL=6, 4XY90=7.
        private static short AxisTypeCode(string s)
        {
            var k = (s ?? "3axis").ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", "");
            switch (k)
            {
                case "3": case "3axis": return 0;
                case "4xz": case "4axisxz": return 1;
                case "4yz": case "4axisyz": return 2;
                case "5": case "5axis": return 3;
                case "3helical": case "3axishelical": return 4;
                case "4xy": case "4axisxy": return 5;
                case "3local": case "3axislocal": return 6;
                case "4xy90": case "4axisxy90": return 7;
                default: throw new ArgumentException("axisType must be 3axis, 4axisXZ, 4axisYZ, 4axisXY, 4axisXY90, 5axis, 3axisHelical or 3axisLocal");
            }
        }

        // Set a named property on a COM object through its interop interface
        // (exact CLR typing; enums accept ints). Used by the millDataProperties
        // / engineProperties escape hatches.
        private void SetInterfaceProperty(Type iface, object target, string name, object value)
        {
            var prop = iface.GetProperty(name);
            if (prop == null || !prop.CanWrite)
                throw new ArgumentException("'" + name + "' is not a writable property of " + iface.Name + " — check alphacam_api_help");
            var pt = prop.PropertyType;
            object v = value;
            try
            {
                if (pt.IsEnum) v = Enum.ToObject(pt, Convert.ToInt32(value));
                else if (value != null && (pt.IsPrimitive || pt == typeof(decimal) || pt == typeof(string))) v = Convert.ChangeType(value, pt);
            }
            catch (Exception ex) { throw new ArgumentException("Cannot convert value for " + name + " to " + pt.Name + ": " + ex.Message); }
            try { prop.SetValue(target, v, null); }
            catch (Exception ex)
            {
                var inner = (ex is System.Reflection.TargetInvocationException && ex.InnerException != null) ? ex.InnerException : ex;
                throw new InvalidOperationException("Setting " + iface.Name + "." + name + " failed: " + inner.Message);
            }
        }

        // Sloping / profiled sides and Auto-Z multi-level cuts for RoughFinish
        // and Pocket. Returns a summary for the response, or null when no
        // side/auto-Z params were given. `helical` guards MaxDepthPerCut,
        // which rough_finish already uses as the helical pitch.
        private Dictionary<string, object> ApplySidesAndAutoZ(dynamic md, Dictionary<string, object> prms, bool helical)
        {
            bool any = prms.ContainsKey("sides") || prms.ContainsKey("sideAngle") || prms.ContainsKey("sideProfileGeometryId") ||
                       prms.ContainsKey("autoZ") || prms.ContainsKey("maxDepthPerCut");
            if (!any) return null;
            var summary = new Dictionary<string, object>();
            var sides = GetString(prms, "sides", prms.ContainsKey("sideProfileGeometryId") ? "profiled" : prms.ContainsKey("sideAngle") ? "sloping" : "vertical").ToLowerInvariant();
            short sidesCode;
            switch (sides)
            {
                case "vertical": sidesCode = 0; break;
                case "sloping": case "slope": case "taper": case "tapered": sidesCode = 1; break;
                case "profiled": case "profile": sidesCode = 2; break;
                case "previoustoolprofile": case "previous": sidesCode = 4; break;
                case "solidfaces": sidesCode = 5; break;
                default: throw new ArgumentException("sides must be vertical, sloping or profiled");
            }
            md.Sides = sidesCode;
            summary["sides"] = sides;
            if (sidesCode == 1)
            {
                if (!prms.ContainsKey("sideAngle")) throw new ArgumentException("sides:sloping needs sideAngle (degrees from vertical)");
                md.SideAngle = (float)GetDouble(prms, "sideAngle");
                summary["sideAngle"] = (double)md.SideAngle;
            }
            if (sidesCode == 2)
            {
                var pid = GetString(prms, "sideProfileGeometryId", "");
                object prof;
                if (string.IsNullOrEmpty(pid) || !_objectCache.TryGetValue(pid, out prof)) throw new ArgumentException("sides:profiled needs sideProfileGeometryId (an open profile geometry drawn in the XZ sense: X = distance from the wall, Z = depth)");
                md.SetSideProfile(prof, GetDouble(prms, "sideProfileRefX", 0.0), GetDouble(prms, "sideProfileRefY", 0.0));
                summary["sideProfileGeometryId"] = pid;
            }
            if (prms.ContainsKey("maxDepthPerCut"))
            {
                if (helical) summary["maxDepthPerCutIgnored"] = "helical=true already uses MaxDepthPerCut as the pitch (helicalPitch)";
                else { md.MaxDepthPerCut = (float)GetDouble(prms, "maxDepthPerCut"); summary["maxDepthPerCut"] = (double)md.MaxDepthPerCut; }
            }
            if (prms.ContainsKey("autoZ"))
            {
                bool autoZ = GetBool(prms, "autoZ", false);
                md.AutoZ = autoZ;
                summary["autoZ"] = autoZ;
                if (autoZ)
                {
                    if (prms.ContainsKey("autoZMaxDepthOfCut")) md.AutoZMaxDepthOfCut = (float)GetDouble(prms, "autoZMaxDepthOfCut");
                    if (prms.ContainsKey("autoZMinDepthOfCut")) md.AutoZMinDepthOfCut = (float)GetDouble(prms, "autoZMinDepthOfCut");
                    if (prms.ContainsKey("autoZStockZ")) md.AutoZStockZ = (float)GetDouble(prms, "autoZStockZ");
                    if (prms.ContainsKey("autoZRampAngle")) md.AutoZRampAngle = (float)GetDouble(prms, "autoZRampAngle");
                    if (prms.ContainsKey("autoZRapidLevelIsAbsolute")) md.AutoZRapidLevelIsAbsolute = GetBool(prms, "autoZRapidLevelIsAbsolute", true);
                    try { summary["autoZMaxDepthOfCut"] = (double)md.AutoZMaxDepthOfCut; } catch { }
                    try { summary["autoZMinDepthOfCut"] = (double)md.AutoZMinDepthOfCut; } catch { }
                }
            }
            return summary;
        }

        // Single api_invoke call (also used per item by batch mode).
        private Dictionary<string, object> InvokeApi(Dictionary<string, object> prms)
        {
            var target = ResolveApiTarget(GetString(prms, "target", "app"));
            var member = GetString(prms, "member", "");
            if (string.IsNullOrEmpty(member)) throw new ArgumentException("member is required");
            var kind = GetString(prms, "kind", "auto");
            var argArr = ConvertApiArgs(prms.ContainsKey("args") ? prms["args"] as System.Collections.ArrayList : null);

            System.Reflection.BindingFlags flags;
            switch (kind)
            {
                case "get": flags = System.Reflection.BindingFlags.GetProperty; break;
                case "set": flags = System.Reflection.BindingFlags.SetProperty; break;
                case "method": flags = System.Reflection.BindingFlags.InvokeMethod; break;
                case "auto": flags = System.Reflection.BindingFlags.InvokeMethod | System.Reflection.BindingFlags.GetProperty; break;
                default: throw new ArgumentException("kind must be auto, method, get or set");
            }
            if (kind == "set" && argArr.Length == 0)
                throw new ArgumentException("kind:\"set\" needs args (the new value; for indexed properties the index args first, value last)");

            System.Reflection.ParameterModifier[] mods = null;
            var byRefList = prms.ContainsKey("byRef") ? prms["byRef"] as System.Collections.ArrayList : null;
            if (byRefList != null && byRefList.Count > 0)
            {
                if (argArr.Length == 0) throw new ArgumentException("byRef given but args is empty");
                var pm = new System.Reflection.ParameterModifier(argArr.Length);
                foreach (var o in byRefList)
                {
                    var idx = Convert.ToInt32(o);
                    if (idx < 0 || idx >= argArr.Length) throw new ArgumentException("byRef index " + idx + " out of range");
                    pm[idx] = true;
                }
                mods = new[] { pm };
            }

            // Coerce numeric args to the parameter types the interop
            // method actually declares. Late-bound IDispatch matches a
            // ByRef double slot to a VT_R8|BYREF variant, so a JSON
            // integer (VT_I4) throws DISP_E_TYPEMISMATCH — most visible
            // on ByRef output params seeded with 0. Reflecting the real
            // signature lets a plain "0" stand in for "0.0" everywhere.
            CoerceArgsToSignature(target, member, argArr);

            object ret;
            // Objects owned by an Alphacam add-in (SolidPart from the
            // STL/Parasolid importer, for example) surface as a bare
            // "_ComObject": their type library isn't registered, so
            // IDispatch name lookup fails with TYPE_E_CANTLOADLIBRARY.
            // The interop assembly still declares their interfaces
            // (ISolidPart etc.), so QI to one of those and invoke via
            // the vtable instead. Found 2026-09-29.
            Type viaIface = null;
            if (GetComTypeName(target) == "_ComObject") viaIface = ProbeInteropInterface(target);
            try
            {
                if (viaIface != null)
                {
                    CoerceArgsToInterfaceSignature(viaIface, member, argArr);
                    ret = InvokeViaInterface(viaIface, target, member, flags, argArr);
                }
                else
                {
                    try
                    {
                        ret = target.GetType().InvokeMember(member, flags, null, target, argArr, mods, null, null);
                    }
                    catch (Exception ex0)
                    {
                        var inner0 = (ex0 is System.Reflection.TargetInvocationException && ex0.InnerException != null) ? ex0.InnerException : ex0;
                        var com0 = inner0 as COMException;
                        if (com0 == null || (uint)com0.HResult != 0x80029C4A) throw;
                        viaIface = ProbeInteropInterface(target);
                        if (viaIface == null) throw;
                        CoerceArgsToInterfaceSignature(viaIface, member, argArr);
                        ret = InvokeViaInterface(viaIface, target, member, flags, argArr);
                    }
                }
            }
            catch (Exception ex)
            {
                var inner = (ex is System.Reflection.TargetInvocationException && ex.InnerException != null) ? ex.InnerException : ex;
                var com = inner as COMException;
                if (viaIface != null && inner is MissingMethodException)
                    throw new ArgumentException("'" + member + "' is not a member of " + viaIface.Name + " (vtable path). Check spelling with alphacam_api_inspect.");
                if (com != null && (uint)com.HResult == 0x80020006)
                    throw new ArgumentException("'" + member + "' is not a member of " + GetComTypeName(target) + ". Check spelling with alphacam_api_help.");
                if (com != null && (uint)com.HResult == 0x8002000E)
                    throw new ArgumentException("'" + member + "' was called with the wrong number of arguments (" + argArr.Length + "). Check the signature with alphacam_api_help.");
                if (com != null && ((uint)com.HResult == 0x80020005 || (uint)com.HResult == 0x80020008))
                    throw new ArgumentException("'" + member + "' argument type mismatch: " + inner.Message + ". Check the signature with alphacam_api_help.");
                throw new InvalidOperationException(GetComTypeName(target) + "." + member + " failed: " + inner.Message);
            }

            var res = new Dictionary<string, object> { ["result"] = ConvertApiResult(ret) };
            if (mods != null)
            {
                var outVals = new Dictionary<string, object>();
                foreach (var o in byRefList)
                {
                    var idx = Convert.ToInt32(o);
                    outVals[idx.ToString()] = ConvertApiResult(argArr[idx]);
                }
                res["byRefValues"] = outVals;
            }
            return res;
                }

        private static string RefIndex(object v)
        {
            var d = v as Dictionary<string, object>;
            if (d != null && d.ContainsKey("$ref")) return Convert.ToString(d["$ref"]);
            var str = v as string;
            if (str != null && str.StartsWith("$ref:")) return str.Substring(5);
            return null;
        }

        private object ResolveBatchRef(object v, List<string> handles, int current)
        {
            var idx = RefIndex(v);
            if (idx == null) return v;
            int i;
            if (!int.TryParse(idx, out i) || i < 0 || i >= current) throw new ArgumentException("calls[" + current + "]: $ref " + idx + " must point at an earlier call");
            if (handles[i] == null) throw new ArgumentException("calls[" + current + "]: call " + i + " returned no object handle to reference");
            return handles[i];
        }

        private object ResolveBatchRefArg(object a, List<string> handles, int current)
        {
            var idx = RefIndex(a);
            if (idx == null) return a;
            var id = (string)ResolveBatchRef(a, handles, current);
            return new Dictionary<string, object> { ["$id"] = id };
        }

        // Models registered as stock via machine_3d.stockModelId — excluded
        // from simulate's default comparison set.
        private readonly List<object> _stockModels = new List<object>();

        // Same underlying solid/surface? Item(i) hands out fresh wrappers, so
        // use the objects' own IsSame before falling back to IUnknown identity.
        private static bool SameModel(object a, object b)
        {
            if (a == null || b == null) return false;
            try { var sa = AsSolidPart(a); var sb = AsSolidPart(b); if (sa != null && sb != null) return sa.IsSame((SolidPart)b); } catch { }
            try { var ua = AsSurface(a); var ub = AsSurface(b); if (ua != null && ub != null) return ua.IsSame((Surface)b); } catch { }
            IntPtr pa = IntPtr.Zero, pb = IntPtr.Zero;
            try { pa = Marshal.GetIUnknownForObject(a); pb = Marshal.GetIUnknownForObject(b); return pa == pb; }
            catch { return false; }
            finally { if (pa != IntPtr.Zero) Marshal.Release(pa); if (pb != IntPtr.Zero) Marshal.Release(pb); }
        }

        // SaveStlFile exports every visible solid (type 1) or surface (type 2).
        // Hide everything not in `only` for the duration, then restore.
        private void ExportModelsStl(dynamic drw, string path, int type, double tol, List<KeyValuePair<string, object>> only)
        {
            var hidden = new List<object>();
            try
            {
                if (only != null)
                {
                    var all = new List<KeyValuePair<string, object>>();
                    all.AddRange(EnumerateSolidParts(drw, false)); all.AddRange(EnumerateSurfaces(drw, false));
                    foreach (var kv in all)
                    {
                        if (only.Exists(o => SameModel(o.Value, kv.Value))) continue;
                        bool vis = true;
                        try { var sp = AsSolidPart(kv.Value); if (sp != null) { vis = sp.Visible; if (vis) { sp.Visible = false; hidden.Add(kv.Value); } continue; } } catch { }
                        try { var su = AsSurface(kv.Value); if (su != null) { vis = su.Visible; if (vis) { su.Visible = false; hidden.Add(kv.Value); } } } catch { }
                    }
                }
                drw.SaveStlFile(path, (short)type, tol);
            }
            finally
            {
                foreach (var o in hidden)
                {
                    try { var sp = AsSolidPart(o); if (sp != null) { sp.Visible = true; continue; } } catch { }
                    try { var su = AsSurface(o); if (su != null) su.Visible = true; } catch { }
                }
            }
        }

        // Lowest Z of the model's top surface (top-down height map of a quick
        // STL export). NaN when it can't be computed. Solids export as STL
        // type 1, surfaces as type 2.
        private sealed class TopSurfaceInfo
        {
            public double TopMinZ = double.NaN;
            // Bounding box of the cells that sit BELOW stockTop - eps: the
            // area a 3-axis job actually has to machine. NaN when nothing does.
            public double CutMinX = double.NaN, CutMinY = double.NaN, CutMaxX = double.NaN, CutMaxY = double.NaN;
            public double CutArea;
        }

        // Quick STL export of the target models → top-down height map →
        // lowest point of the TOP surface and the footprint of material to
        // remove (cells below the stock top). NaN fields when unavailable.
        private TopSurfaceInfo AnalyseTopSurface(dynamic drw, bool solids, List<KeyValuePair<string, object>> onlyThese, double stockTop)
        {
            var info = new TopSurfaceInfo();
            string tmp = null;
            try
            {
                tmp = Path.Combine(Path.GetTempPath(), "AlphacamBridge-topmin-" + Guid.NewGuid().ToString("N") + ".stl");
                ExportModelsStl(drw, tmp, solids ? 1 : 2, 0.01, onlyThese);
                var tris = StlCompare.ReadStl(tmp);
                if (tris.Count == 0) return info;
                double x0, y0, z0, x1, y1, z1; StlCompare.Bounds(tris, out x0, out y0, out z0, out x1, out y1, out z1);
                const int n = 120;
                var hm = StlCompare.HeightMap(tris, x0, y0, x1, y1, n);
                double dx = (x1 - x0) / n, dy = (y1 - y0) / n;
                double top = double.IsNaN(stockTop) ? z1 : stockTop;
                double eps = 0.002 * Math.Max(1.0, z1 - z0);
                double min = double.PositiveInfinity; int cut = 0;
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++)
                    {
                        double z = hm[i, j];
                        if (double.IsNaN(z)) continue;
                        if (z < min) min = z;
                        if (z < top - eps)
                        {
                            cut++;
                            double cx0 = x0 + i * dx, cy0 = y0 + j * dy;
                            if (double.IsNaN(info.CutMinX) || cx0 < info.CutMinX) info.CutMinX = cx0;
                            if (double.IsNaN(info.CutMinY) || cy0 < info.CutMinY) info.CutMinY = cy0;
                            if (double.IsNaN(info.CutMaxX) || cx0 + dx > info.CutMaxX) info.CutMaxX = cx0 + dx;
                            if (double.IsNaN(info.CutMaxY) || cy0 + dy > info.CutMaxY) info.CutMaxY = cy0 + dy;
                        }
                    }
                info.TopMinZ = double.IsInfinity(min) ? double.NaN : min;
                info.CutArea = cut * dx * dy;
                return info;
            }
            catch (Exception ex) { Log("AnalyseTopSurface: " + ex.Message); return info; }
            finally { try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }

        // ── Generic API helpers ─────────────────────────────────────

        // Resolve an api_invoke/api_inspect target: a root name or a cached id.
        private object ResolveApiTarget(string target)
        {
            if (string.IsNullOrEmpty(target)
                || string.Equals(target, "app", StringComparison.OrdinalIgnoreCase)
                || string.Equals(target, "application", StringComparison.OrdinalIgnoreCase))
                return _app;
            if (string.Equals(target, "drawing", StringComparison.OrdinalIgnoreCase)
                || string.Equals(target, "activedrawing", StringComparison.OrdinalIgnoreCase))
            {
                var drw = _app.ActiveDrawing;
                if (drw == null) throw new InvalidOperationException("No active drawing");
                return drw;
            }
            if (_objectCache.TryGetValue(target, out var cached)) return cached;
            throw new ArgumentException("Unknown target '" + target + "'. Use \"app\", \"drawing\", or an objectId/geometryId returned by an earlier call.");
        }

        // JSON args -> COM-callable object[]. {"$id":"obj_N"} resolves to a
        // cached COM object; decimals become doubles (ALPHACAM's API wants
        // VT_R8, and JavaScriptSerializer parses non-integer numbers as decimal).
        private object[] ConvertApiArgs(System.Collections.ArrayList raw)
        {
            if (raw == null) return new object[0];
            var result = new object[raw.Count];
            for (int i = 0; i < raw.Count; i++) result[i] = ConvertApiArg(raw[i]);
            return result;
        }

        private object ConvertApiArg(object v)
        {
            if (v == null) return null;
            if (v is decimal) return Convert.ToDouble(v);
            var dict = v as Dictionary<string, object>;
            if (dict != null)
            {
                object idVal;
                if (dict.TryGetValue("$id", out idVal) || dict.TryGetValue("objectId", out idVal))
                {
                    var id = Convert.ToString(idVal);
                    if (!_objectCache.TryGetValue(id, out var cached))
                        throw new ArgumentException("Object " + id + " not found in cache");
                    return cached;
                }
                throw new ArgumentException("Object-valued args must be {\"$id\": \"obj_N\"} references");
            }
            var list = v as System.Collections.ArrayList;
            if (list != null)
            {
                var arr = new object[list.Count];
                for (int i = 0; i < list.Count; i++) arr[i] = ConvertApiArg(list[i]);
                return arr;
            }
            return v; // string, bool, int
        }

        // COM/primitive result -> JSON-safe value. COM objects become
        // {"objectId","comType"} handles (usable as target / {"$id"} args).
        private object ConvertApiResult(object v)
        {
            if (v == null) return null;
            if (v is string || v is bool || v is sbyte || v is byte || v is short || v is ushort
                || v is int || v is uint || v is long || v is ulong
                || v is float || v is double || v is decimal) return v;
            if (v is DateTime) return ((DateTime)v).ToString("o");
            if (v is Enum) return Convert.ToInt32(v);
            var arr = v as Array;
            if (arr != null)
            {
                var list = new List<object>();
                foreach (var e in arr) list.Add(ConvertApiResult(e));
                return list;
            }
            if (Marshal.IsComObject(v))
            {
                return new Dictionary<string, object>
                {
                    ["objectId"] = StoreObject(v),
                    ["comType"] = GetComTypeName(v)
                };
            }
            return v.ToString();
        }

        // Runtime type name of a COM object via its ITypeInfo.
        private string GetComTypeName(object comObj)
        {
            if (comObj == null) return "null";
            try
            {
                if (Marshal.IsComObject(comObj))
                    return Microsoft.VisualBasic.Information.TypeName(comObj);
            }
            catch { }
            try { return comObj.GetType().Name; } catch { return "unknown"; }
        }

        // Resolve the interop interface Type for a live COM object, or null.
        private Type ResolveInteropInterface(object comObj)
        {
            try
            {
                var typeName = GetComTypeName(comObj);
                var asm = typeof(IAlphaCamApp).Assembly;
                return asm.GetType("AlphaCAMMill." + typeName, false)
                    ?? asm.GetType("AlphaCAMMill.I" + typeName, false);
            }
            catch { return null; }
        }

        // Interop interfaces we QI for when a COM object has no loadable type
        // library (add-in objects). Ordered most-specific first; the first
        // successful QI wins. Dual interfaces only — IUnknown-only ones
        // (ISolidPartEx) can't be driven by InvokeMember's IDispatch fallback,
        // so they're excluded here and cast explicitly where needed.
        private static readonly string[] ProbeInterfaceNames =
        {
            "ISolidPart", "ISurface", "IPath", "ISpline", "IText", "IClamp", "IWorkpiece",
            "ILayer", "IWorkPlane", "IOperation", "ISubOperation", "IMillTool", "IMillData",
            "IPaths", "ISurfaces", "ISolidParts", "IAlphacamObjects", "IElement", "IPolyline",
        };
        private readonly Dictionary<IntPtr, Type> _probedIfaceByUnknown = new Dictionary<IntPtr, Type>();

        // Find an interop interface the object actually implements, by
        // QueryInterface. Returns null when nothing matches.
        private Type ProbeInteropInterface(object comObj)
        {
            if (comObj == null || !Marshal.IsComObject(comObj)) return null;
            IntPtr unk = IntPtr.Zero;
            try
            {
                unk = Marshal.GetIUnknownForObject(comObj);
                Type cached;
                if (_probedIfaceByUnknown.TryGetValue(unk, out cached)) return cached;
                var asm = typeof(IAlphaCamApp).Assembly;
                foreach (var name in ProbeInterfaceNames)
                {
                    var t = asm.GetType("AlphaCAMMill." + name, false);
                    if (t == null || !t.IsInterface) continue;
                    try
                    {
                        if (t.IsInstanceOfType(comObj)) { _probedIfaceByUnknown[unk] = t; return t; }
                    }
                    catch { }
                }
                return null;
            }
            catch { return null; }
            finally { if (unk != IntPtr.Zero) Marshal.Release(unk); }
        }

        // Invoke a member through an interop interface's own MethodInfo /
        // PropertyInfo. Type.InvokeMember on a ComImport interface still
        // routes through IDispatch (RuntimeType.IsCOMObject is true for it),
        // which is exactly what fails for add-in objects — so bind the
        // reflection member by hand and call the interface stub (vtable).
        private object InvokeViaInterface(Type iface, object target, string member, System.Reflection.BindingFlags flags, object[] argArr)
        {
            bool wantSet = (flags & System.Reflection.BindingFlags.SetProperty) != 0;
            bool wantGet = (flags & System.Reflection.BindingFlags.GetProperty) != 0;
            bool wantMethod = (flags & System.Reflection.BindingFlags.InvokeMethod) != 0;
            var prop = iface.GetProperty(member);
            if (prop != null && (wantSet || wantGet))
            {
                if (wantSet)
                {
                    if (argArr.Length == 0) throw new ArgumentException("set needs a value");
                    var idx = new object[argArr.Length - 1];
                    Array.Copy(argArr, idx, idx.Length);
                    prop.SetValue(target, argArr[argArr.Length - 1], idx.Length > 0 ? idx : null);
                    return null;
                }
                return prop.GetValue(target, argArr.Length > 0 ? argArr : null);
            }
            if (wantMethod)
            {
                System.Reflection.MethodInfo best = null;
                foreach (var m in iface.GetMethods())
                {
                    if (m.Name != member) continue;
                    if (m.GetParameters().Length == argArr.Length) { best = m; break; }
                    if (best == null) best = m;
                }
                if (best == null)
                {
                    // Property getters are also reachable as get_X on the interface.
                    var getter = iface.GetMethod("get_" + member);
                    if (getter != null) best = getter;
                }
                if (best != null)
                {
                    try { return best.Invoke(target, argArr); }
                    catch (System.Reflection.TargetInvocationException tie) { throw tie.InnerException ?? tie; }
                }
            }
            throw new MissingMethodException(iface.Name, member);
        }

        // Cast helpers for add-in objects (see ProbeInteropInterface). `as`
        // on an RCW performs QueryInterface, no type library needed.
        private static ISolidPart AsSolidPart(object o) { try { return o as ISolidPart; } catch { return null; } }
        private static ISurface AsSurface(object o) { try { return o as ISurface; } catch { return null; } }

        // Coerce each numeric arg to the element type the interop method's
        // matching parameter declares (ByRef stripped). Best-effort: unknown
        // members or unconvertible values are left as-is for InvokeMember.
        private void CoerceArgsToSignature(object target, string member, object[] argArr)
        {
            if (argArr == null || argArr.Length == 0) return;
            try
            {
                var iface = ResolveInteropInterface(target);
                CoerceArgsToInterfaceSignature(iface, member, argArr);
            }
            catch { }
        }

        private void CoerceArgsToInterfaceSignature(Type iface, string member, object[] argArr)
        {
            if (argArr == null || argArr.Length == 0) return;
            try
            {
                if (iface == null) return;
                System.Reflection.ParameterInfo[] pars = null;
                foreach (var m in iface.GetMethods())
                {
                    if (m.Name != member) continue;
                    var candidate = m.GetParameters();
                    if (candidate.Length == argArr.Length) { pars = candidate; break; }
                    if (pars == null) pars = candidate; // fallback: first by name
                }
                if (pars == null)
                {
                    // Property write: coerce the value (last arg) to the
                    // declared property type. Matters on the vtable path,
                    // where a JSON true/1 must arrive as the exact CLR type.
                    var prop = iface.GetProperty(member);
                    if (prop != null && argArr.Length >= 1)
                    {
                        var last = argArr[argArr.Length - 1];
                        var ptp = prop.PropertyType;
                        if (last != null && !Marshal.IsComObject(last) && (last is IConvertible) && (ptp.IsPrimitive || ptp == typeof(decimal) || ptp == typeof(string) || ptp.IsEnum))
                        {
                            try { argArr[argArr.Length - 1] = ptp.IsEnum ? Enum.ToObject(ptp, Convert.ToInt32(last)) : Convert.ChangeType(last, ptp); } catch { }
                        }
                    }
                    return;
                }
                for (int i = 0; i < argArr.Length && i < pars.Length; i++)
                {
                    var a = argArr[i];
                    if (a == null) continue;
                    var pt = pars[i].ParameterType;
                    if (pt.IsByRef) pt = pt.GetElementType();
                    if (pt == null || pt == typeof(object) || Marshal.IsComObject(a)) continue;
                    // Only massage primitive numerics/bools/strings; leave COM
                    // handles and arrays untouched.
                    if ((a is IConvertible) && (pt.IsPrimitive || pt == typeof(decimal) || pt == typeof(string)))
                    {
                        try { argArr[i] = Convert.ChangeType(a, pt); } catch { }
                    }
                    else if ((a is IConvertible) && pt.IsEnum)
                    {
                        try { argArr[i] = Enum.ToObject(pt, Convert.ToInt32(a)); } catch { }
                    }
                }
            }
            catch { }
        }

        // Member lists for a COM type, reflected from the interop assembly.
        // Returns (methods, properties) or null when the type isn't found.
        private Tuple<List<string>, List<string>> DescribeInteropType(string typeName)
        {
            try
            {
                var asm = typeof(IAlphaCamApp).Assembly;
                var t = asm.GetType("AlphaCAMMill." + typeName, false)
                     ?? asm.GetType("AlphaCAMMill.I" + typeName, false);
                if (t == null) return null;
                var seen = new HashSet<string>();
                var methods = new List<string>();
                var props = new List<string>();
                var types = new List<Type> { t };
                types.AddRange(t.GetInterfaces());
                foreach (var it in types)
                {
                    foreach (var p in it.GetProperties())
                        if (seen.Add("p:" + p.Name)) props.Add(p.Name);
                    foreach (var m in it.GetMethods())
                        if (!m.IsSpecialName && seen.Add("m:" + m.Name)) methods.Add(m.Name);
                }
                methods.Sort();
                props.Sort();
                return Tuple.Create(methods, props);
            }
            catch { return null; }
        }

        // Total length of a path by walking its elements.
        private static double GetPathLength(dynamic path)
        {
            double total = 0;
            try
            {
                int n = (int)path.GetElemCount();
                dynamic el = path.GetFirstElem();
                for (int i = 0; i < n && el != null; i++)
                {
                    try { total += (double)el.Length; } catch { }
                    dynamic next = null;
                    try { next = el.GetNext(); } catch { }
                    el = next;
                }
            }
            catch { }
            return total;
        }

        // Nest direction: accepts 0-3 or left/bottom/right/top.
        private short ParseNestDirection(Dictionary<string, object> prms, short defaultValue)
        {
            if (!prms.ContainsKey("direction")) return defaultValue;
            if (prms["direction"] is string)
            {
                switch (GetString(prms, "direction").ToLowerInvariant().Replace("-", "").Replace("_", ""))
                {
                    case "left": return 0;
                    case "bottom": return 1;
                    case "right": return 2;
                    case "top": return 3;
                    case "topleft": return 4;
                    case "bottomleft": return 5;
                    case "bottomright": return 6;
                    case "topright": return 7;
                    default: throw new ArgumentException("direction must be left/bottom/right/top/top-left/bottom-left/bottom-right/top-right or 0-7");
                }
            }
            return (short)GetInt(prms, "direction", defaultValue);
        }

        // Resolve the geometry source for an operation. Three sources, in
        // priority order:
        //   interactive: true  -> Alphacam prompts the user to pick (blocks)
        //   selected: true     -> whatever is currently selected in Alphacam
        //   geometryId(s)      -> explicit cached ids
        // Throws if the chosen source yields nothing.
        private List<KeyValuePair<string, object>> ResolveGeometryList(Dictionary<string, object> prms)
        {
            if (GetBool(prms, "interactive", false))
            {
                dynamic drw = _app.ActiveDrawing;
                var promptText = GetString(prms, "prompt", "Select geometries for the agent, then press Escape");
                dynamic picked = drw.UserSelectMultiGeosCollection(promptText, 0);
                var resultI = new List<KeyValuePair<string, object>>();
                int pcount = 0;
                try { pcount = (int)picked.Count; } catch { }
                for (int i = 1; i <= pcount; i++)
                {
                    dynamic g = picked.Item(i);
                    resultI.Add(new KeyValuePair<string, object>(StoreObject(g), (object)g));
                }
                if (resultI.Count == 0) throw new InvalidOperationException("User selected nothing (or cancelled)");
                return resultI;
            }
            if (GetBool(prms, "selected", false))
            {
                dynamic drw = _app.ActiveDrawing;
                dynamic geos = drw.Geometries;
                var resultS = new List<KeyValuePair<string, object>>();
                int gcount = 0;
                try { gcount = (int)geos.Count; } catch { }
                for (int i = 1; i <= gcount; i++)
                {
                    dynamic g = geos.Item(i);
                    bool sel = false;
                    try { sel = (bool)g.Selected; } catch { }
                    if (sel) resultS.Add(new KeyValuePair<string, object>(StoreObject(g), (object)g));
                }
                if (resultS.Count == 0) throw new InvalidOperationException("No geometries are currently selected in Alphacam");
                return resultS;
            }
            var ids = new List<string>();
            if (prms.ContainsKey("geometryIds"))
            {
                var arr = prms["geometryIds"] as System.Collections.ArrayList;
                if (arr != null) foreach (var o in arr) { var s = Convert.ToString(o); if (!string.IsNullOrEmpty(s) && !ids.Contains(s)) ids.Add(s); }
            }
            if (prms.ContainsKey("geometryId"))
            {
                var s = GetString(prms, "geometryId", "");
                if (!string.IsNullOrEmpty(s) && !ids.Contains(s)) ids.Add(s);
            }
            if (ids.Count == 0) throw new ArgumentException("Pass geometryId/geometryIds, or selected:true (current Alphacam selection), or interactive:true (prompt the user)");
            var result = new List<KeyValuePair<string, object>>();
            foreach (var id in ids)
            {
                if (!_objectCache.TryGetValue(id, out var raw)) throw new InvalidOperationException("Geometry " + id + " not found in object cache");
                result.Add(new KeyValuePair<string, object>(id, raw));
            }
            return result;
        }

        // Deselect everything, then select the given geometries. Returns true
        // when every geometry accepted Selected=true directly; falls back to
        // a PathCollection for any that didn't.
        private bool SelectGeometries(dynamic drw, List<KeyValuePair<string, object>> geos)
        {
            drw.Geometries.Selected = false;
            bool direct = true;
            foreach (var kv in geos)
            {
                dynamic geo = kv.Value;
                try { geo.Selected = true; }
                catch
                {
                    direct = false;
                    dynamic coll = drw.CreatePathCollection();
                    coll.Add(geo);
                    coll.Selected = true;
                }
            }
            return direct;
        }

        // Store every path in a COM Paths collection; returns their ids.
        private List<string> StorePathsCollection(dynamic paths)
        {
            var ids = new List<string>();
            try
            {
                int count = (int)paths.Count;
                for (int i = 1; i <= count; i++) ids.Add(StoreObject(paths.Item(i)));
            }
            catch { }
            return ids;
        }

        // Apply one transform operation to a path, reading params.
        private void ApplyTransform(dynamic geo, string op, Dictionary<string, object> prms)
        {
            switch (op)
            {
                case "move":
                {
                    var dx = GetDouble(prms, "dx", 0);
                    var dy = GetDouble(prms, "dy", 0);
                    var dz = GetDouble(prms, "dz", 0);
                    if (dz != 0) geo.MoveG(dx, dy, dz);
                    else geo.MoveL(dx, dy);
                    break;
                }
                case "rotate":
                    // Angle in degrees, CW negative (Alphacam convention).
                    geo.RotateL(GetDouble(prms, "angle"), GetDouble(prms, "baseX", 0), GetDouble(prms, "baseY", 0));
                    break;
                case "mirror":
                    geo.MirrorL(GetDouble(prms, "x1"), GetDouble(prms, "y1"), GetDouble(prms, "x2"), GetDouble(prms, "y2"));
                    break;
                case "scale":
                {
                    var fx = GetDouble(prms, "factorX", GetDouble(prms, "factor", 1.0));
                    var fy = GetDouble(prms, "factorY", GetDouble(prms, "factor", 1.0));
                    geo.ScaleL2(fx, fy, GetDouble(prms, "baseX", 0), GetDouble(prms, "baseY", 0));
                    break;
                }
                default:
                    throw new ArgumentException("operation must be move/rotate/mirror/scale");
            }
        }

        // Walk the layer list looking for a case-insensitive name match.
        private static dynamic FindLayerByName(dynamic drw, string name)
        {
            dynamic layer = null;
            try { layer = drw.GetFirstLayer(); } catch { }
            int guard = 0;
            while (layer != null && guard++ < 1000)
            {
                try { if (string.Equals((string)layer.Name, name, StringComparison.OrdinalIgnoreCase)) return layer; } catch { }
                dynamic next = null;
                try { next = layer.GetNext(); } catch { }
                layer = next;
            }
            return null;
        }

        // Resolve a work plane from params: by cached workPlaneId, or by
        // case-insensitive name lookup in Drawing.WorkPlanes.
        private dynamic ResolveWorkPlane(dynamic drw, Dictionary<string, object> prms)
        {
            var wpId = GetString(prms, "workPlaneId", "");
            if (!string.IsNullOrEmpty(wpId))
            {
                if (!_objectCache.TryGetValue(wpId, out var rawWp)) throw new InvalidOperationException("Work plane " + wpId + " not found in object cache");
                return rawWp;
            }
            var name = GetString(prms, "name", "");
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Pass name or workPlaneId");
            try
            {
                dynamic wps = drw.WorkPlanes;
                int count = (int)wps.Count;
                for (int i = 1; i <= count; i++)
                {
                    dynamic wp = wps.Item(i);
                    try { if (string.Equals((string)wp.Name, name, StringComparison.OrdinalIgnoreCase)) return wp; } catch { }
                }
            }
            catch { }
            throw new InvalidOperationException("Work plane not found: " + name);
        }

        // Probe candidate locations for the default XilogPlus post processor.
        // LicomdirPath alone is unreliable (on some installs it returns the
        // Alphacam ROOT, e.g. C:\ALPHACAM\, with no LICOMDIR segment). Order:
        //  1. Directory of the currently active post (App.PostFileName)
        //  2. LicomdirPath + rel  (the documented layout)
        //  3. LicomdirPath + LICOMDIR + rel  (LicomdirPath is actually the root)
        //  4. parent(LicomdatPath) + LICOMDIR + rel  (LICOMDAT and LICOMDIR are siblings)
        // First candidate that exists on disk wins; if none exist, fall back to
        // candidate 2 so the error message shows a sensible path.
        private string ProbeDefaultPostPath()
        {
            var rel = Path.Combine("VBMacros", "Startup", "XilogPlusPost", "XilogPlus.arb");
            var candidates = new List<string>();
            try
            {
                var postFile = (string)((dynamic)_app).PostFileName;
                if (!string.IsNullOrEmpty(postFile))
                {
                    var dir = Path.GetDirectoryName(postFile);
                    if (!string.IsNullOrEmpty(dir)) candidates.Add(Path.Combine(dir, "XilogPlus.arb"));
                }
            }
            catch { }
            string documented = null;
            try
            {
                var licomdir = (string)((dynamic)_app).LicomdirPath;
                if (!string.IsNullOrEmpty(licomdir))
                {
                    documented = Path.Combine(licomdir, rel);
                    candidates.Add(documented);
                    candidates.Add(Path.Combine(licomdir, "LICOMDIR", rel));
                }
            }
            catch { }
            try
            {
                var licomdat = (string)((dynamic)_app).LicomdatPath;
                if (!string.IsNullOrEmpty(licomdat))
                {
                    var root = Path.GetDirectoryName(licomdat.TrimEnd('\\', '/'));
                    if (!string.IsNullOrEmpty(root)) candidates.Add(Path.Combine(root, "LICOMDIR", rel));
                }
            }
            catch { }
            foreach (var c in candidates)
            {
                try { if (File.Exists(c)) { Log("select_post default probe hit: " + c); return c; } } catch { }
            }
            Log("select_post default probe: no candidate exists on disk (tried " + candidates.Count + ")");
            return documented ?? (candidates.Count > 0 ? candidates[0] : null);
        }

        // Path.SetLeadInOutAuto(LeadIn, LeadOut, LengthMultiplier, RadiusMultiplier,
        // Angle, SlopeIn, SlopeOut, Overlap). LengthMultiplier and RadiusMultiplier
        // are in TOOL RADII (per the Alphacam UI convention). Returns how many
        // toolpaths we successfully applied leads to.
        private int ApplyLeadInOutToPaths(dynamic paths, Dictionary<string, object> prms)
        {
            bool anyLead =
                prms.ContainsKey("leadInStyle")   || prms.ContainsKey("leadOutStyle") ||
                prms.ContainsKey("leadInLength")  || prms.ContainsKey("leadInRadius") ||
                prms.ContainsKey("leadInAngle")   ||
                prms.ContainsKey("leadInSlope")   || prms.ContainsKey("leadOutSlope") ||
                prms.ContainsKey("leadOverlap");
            if (!anyLead) return 0;

            short leadIn   = LeadStyleCode(GetString(prms, "leadInStyle", "line"));
            short leadOut  = LeadStyleCode(GetString(prms, "leadOutStyle", "line"));
            double lengthMul = GetDouble(prms, "leadInLength", 2.0);
            double radiusMul = GetDouble(prms, "leadInRadius", 2.0);
            double angle     = GetDouble(prms, "leadInAngle", 45.0);
            bool slopeIn     = GetBool(prms, "leadInSlope", false);
            bool slopeOut    = GetBool(prms, "leadOutSlope", false);
            double overlap   = GetDouble(prms, "leadOverlap", 0.0);

            int applied = 0;
            try
            {
                int count = 0;
                try { count = (int)paths.Count; } catch { }
                for (int i = 1; i <= count; i++)
                {
                    try
                    {
                        dynamic p = paths.Item(i);
                        p.SetLeadInOutAuto(leadIn, leadOut, lengthMul, radiusMul, angle, slopeIn, slopeOut, overlap);
                        applied++;
                    }
                    catch (Exception ex) { Log("SetLeadInOutAuto path " + i + " error: " + ex.Message); }
                }
            }
            catch (Exception ex) { Log("ApplyLeadInOutToPaths iterate error: " + ex.Message); }
            return applied;
        }

        private static short LeadStyleCode(string style)
        {
            switch (style.ToLowerInvariant())
            {
                case "arc":  return 1;
                case "both": return 2;
                case "none": return 3;
                default:     return 0; // "line"
            }
        }

        private void EdgeMidpoint(dynamic geo, bool longest, out double mx, out double my, out double bestLen)
        {
            mx = 0; my = 0;
            bestLen = longest ? -1.0 : double.MaxValue;
            try
            {
                dynamic el = geo.GetFirstElem();
                int guard = 0;
                while (el != null && guard++ < 10000)
                {
                    double len = 0;
                    try { len = (double)el.Length; } catch { }
                    bool take = longest ? (len > bestLen) : (len < bestLen);
                    if (take)
                    {
                        bestLen = len;
                        double sx = (double)el.StartXG, sy = (double)el.StartYG;
                        double ex = (double)el.EndXG,   ey = (double)el.EndYG;
                        bool isArc = false;
                        try { isArc = (bool)el.IsArc; } catch { }
                        if (isArc)
                        {
                            try
                            {
                                double cx = (double)el.CenterXG, cy = (double)el.CenterYG;
                                double r  = (double)el.Radius;
                                double startAngle = Math.Atan2(sy - cy, sx - cx);
                                double included = 0;
                                try { included = (double)el.IncludedAngle; } catch { }
                                double midAngle = startAngle + included / 2.0;
                                mx = cx + r * Math.Cos(midAngle);
                                my = cy + r * Math.Sin(midAngle);
                            }
                            catch { mx = (sx + ex) / 2.0; my = (sy + ey) / 2.0; }
                        }
                        else { mx = (sx + ex) / 2.0; my = (sy + ey) / 2.0; }
                    }
                    dynamic next = null;
                    try { next = el.GetNext(); } catch { }
                    el = next;
                }
            }
            catch { }
        }

        private void GeometryBounds(dynamic geo, out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = double.MaxValue; minY = double.MaxValue;
            maxX = double.MinValue; maxY = double.MinValue;
            try
            {
                minX = (double)geo.MinXL;
                maxX = (double)geo.MaxXL;
                minY = (double)geo.MinYL;
                maxY = (double)geo.MaxYL;
                return;
            }
            catch { }
            try
            {
                dynamic el = geo.GetFirstElem();
                int guard = 0;
                while (el != null && guard++ < 10000)
                {
                    double sx = (double)el.StartXG, sy = (double)el.StartYG;
                    double ex = (double)el.EndXG,   ey = (double)el.EndYG;
                    if (sx < minX) minX = sx; if (sx > maxX) maxX = sx;
                    if (sy < minY) minY = sy; if (sy > maxY) maxY = sy;
                    if (ex < minX) minX = ex; if (ex > maxX) maxX = ex;
                    if (ey < minY) minY = ey; if (ey > maxY) maxY = ey;
                    dynamic next = null;
                    try { next = el.GetNext(); } catch { }
                    el = next;
                }
            }
            catch { }
        }

        private void NearestVertex(dynamic geo, double refX, double refY, out double bx, out double by)
        {
            bx = 0; by = 0;
            double bestDist = double.MaxValue;
            try
            {
                dynamic el = geo.GetFirstElem();
                int guard = 0;
                while (el != null && guard++ < 10000)
                {
                    double sx = (double)el.StartXG, sy = (double)el.StartYG;
                    double dx = sx - refX, dy = sy - refY;
                    double d = dx * dx + dy * dy;
                    if (d < bestDist) { bestDist = d; bx = sx; by = sy; }
                    dynamic next = null;
                    try { next = el.GetNext(); } catch { }
                    el = next;
                }
            }
            catch { }
        }

        private HashSet<IntPtr> SnapshotGeometryUnknowns(dynamic drw)
        {
            var set = new HashSet<IntPtr>();
            try
            {
                dynamic geos = drw.Geometries;
                int count = (int)geos.Count;
                for (int i = 1; i <= count; i++)
                {
                    IntPtr unk = IntPtr.Zero;
                    try { unk = Marshal.GetIUnknownForObject(geos.Item(i)); set.Add(unk); }
                    finally { if (unk != IntPtr.Zero) Marshal.Release(unk); }
                }
            }
            catch { }
            return set;
        }

        private List<object> DiffNewGeometries(dynamic drw, HashSet<IntPtr> before, object primary)
        {
            var newOnes = new List<object>();
            IntPtr primaryUnk = IntPtr.Zero;
            try
            {
                primaryUnk = Marshal.GetIUnknownForObject(primary);
                dynamic geos = drw.Geometries;
                int count = (int)geos.Count;
                for (int i = 1; i <= count; i++)
                {
                    dynamic g = geos.Item(i);
                    IntPtr unk = IntPtr.Zero;
                    try
                    {
                        unk = Marshal.GetIUnknownForObject(g);
                        if (unk == primaryUnk) continue;
                        if (!before.Contains(unk)) newOnes.Add(g);
                    }
                    finally { if (unk != IntPtr.Zero) Marshal.Release(unk); }
                }
            }
            catch { }
            finally { if (primaryUnk != IntPtr.Zero) Marshal.Release(primaryUnk); }
            return newOnes;
        }

        private int CascadeDeleteHelpers(string designationId)
        {
            if (!_designationHelpers.TryGetValue(designationId, out var helpers)) return 0;
            int n = 0;
            foreach (var h in helpers)
            {
                try { dynamic path = h; path.Delete(); n++; }
                catch (Exception ex) { Log("Cascade delete error: " + ex.Message); }
            }
            _designationHelpers.Remove(designationId);
            return n;
        }

        private static void PatchIniSection(string iniPath, string section, Dictionary<string, string> updates)
        {
            var lines = File.ReadAllLines(iniPath);
            string sectionHeader = "[" + section + "]";
            int sectionStart = -1, sectionEnd = lines.Length;
            for (int i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed == sectionHeader) { sectionStart = i; continue; }
                if (sectionStart >= 0 && trimmed.StartsWith("[") && trimmed.EndsWith("]") && trimmed.Length > 2)
                {
                    sectionEnd = i;
                    break;
                }
            }
            if (sectionStart == -1)
                throw new InvalidOperationException("INI section [" + section + "] not found in " + iniPath);

            var applied = new HashSet<string>();
            for (int i = sectionStart + 1; i < sectionEnd; i++)
            {
                var line = lines[i];
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).Trim();
                if (updates.ContainsKey(key))
                {
                    lines[i] = key + "=" + updates[key];
                    applied.Add(key);
                }
            }

            var result = new List<string>(lines.Length + updates.Count);
            for (int i = 0; i < lines.Length; i++)
            {
                if (i == sectionEnd)
                {
                    foreach (var kv in updates)
                        if (!applied.Contains(kv.Key))
                            result.Add(kv.Key + "=" + kv.Value);
                }
                result.Add(lines[i]);
            }
            if (sectionEnd == lines.Length)
            {
                foreach (var kv in updates)
                    if (!applied.Contains(kv.Key))
                        result.Add(kv.Key + "=" + kv.Value);
            }
            File.WriteAllLines(iniPath, result);
        }

        private void RemoveFromCache(string id)
        {
            if (!_objectCache.TryGetValue(id, out var comObj)) return;
            _objectCache.Remove(id);
            IntPtr unk = IntPtr.Zero;
            try
            {
                unk = Marshal.GetIUnknownForObject(comObj);
                if (_idByUnknown.TryGetValue(unk, out var _))
                {
                    _idByUnknown.Remove(unk);
                    Marshal.Release(unk);
                }
            }
            catch { }
            finally { if (unk != IntPtr.Zero) Marshal.Release(unk); }
        }

        private string StoreObject(object comObj)
        {
            if (comObj == null) return null;
            IntPtr unk = IntPtr.Zero;
            try
            {
                unk = Marshal.GetIUnknownForObject(comObj);
                if (_idByUnknown.TryGetValue(unk, out var existingId)) return existingId;
                var id = "obj_" + _nextObjectId++;
                _objectCache[id] = comObj;
                _idByUnknown[unk] = id;
                unk = IntPtr.Zero;
                return id;
            }
            finally { if (unk != IntPtr.Zero) Marshal.Release(unk); }
        }

        private static double GetDouble(Dictionary<string, object> p, string key)
        {
            if (!p.ContainsKey(key)) throw new ArgumentException("Missing required parameter: " + key);
            return Convert.ToDouble(p[key]);
        }

        private static double GetDouble(Dictionary<string, object> p, string key, double defaultValue)
        {
            if (!p.ContainsKey(key)) return defaultValue;
            return Convert.ToDouble(p[key]);
        }

        private static int GetInt(Dictionary<string, object> p, string key, int defaultValue = 0)
        {
            if (!p.ContainsKey(key)) return defaultValue;
            return Convert.ToInt32(p[key]);
        }

        private static string GetString(Dictionary<string, object> p, string key, string defaultValue = null)
        {
            if (!p.ContainsKey(key))
            {
                if (defaultValue != null) return defaultValue;
                throw new ArgumentException("Missing required parameter: " + key);
            }
            return Convert.ToString(p[key]);
        }

        private static bool GetBool(Dictionary<string, object> p, string key, bool defaultValue = false)
        {
            if (!p.ContainsKey(key)) return defaultValue;
            return Convert.ToBoolean(p[key]);
        }

        private string BuildSuccessResponse(object id, object result)
        {
            var resp = new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
            return _json.Serialize(resp);
        }

        private string BuildErrorResponse(object id, int code, string message)
        {
            var resp = new Dictionary<string, object>
            {
                ["jsonrpc"] = "2.0", ["id"] = id,
                ["error"] = new Dictionary<string, object> { ["code"] = code, ["message"] = message }
            };
            return _json.Serialize(resp);
        }

        private static int ReadExact(Stream stream, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = stream.Read(buffer, total, count - total);
                if (n == 0) return total;
                total += n;
            }
            return total;
        }

        private void Log(string message)
        {
            if (_logPath == null) return;
            try { System.IO.File.AppendAllText(_logPath, string.Format("[{0:O}] [CMD] {1}\r\n", DateTime.Now, message)); }
            catch { }
        }
    }
}
