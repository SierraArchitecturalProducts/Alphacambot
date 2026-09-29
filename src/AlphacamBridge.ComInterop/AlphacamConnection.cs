using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Extensions.Logging;

namespace AlphacamBridge.ComInterop;

/// <summary>
/// Provides Marshal.GetActiveObject equivalent for .NET 5+ where it was removed.
/// </summary>
internal static class ComHelper
{
    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(
        ref Guid rclsid,
        IntPtr pvReserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

    [DllImport("ole32.dll")]
    private static extern int CLSIDFromProgID(
        [MarshalAs(UnmanagedType.LPWStr)] string lpszProgID,
        out Guid pclsid);

    public static object GetActiveObject(string progId)
    {
        var hr = CLSIDFromProgID(progId, out var clsid);
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);

        GetActiveObject(ref clsid, IntPtr.Zero, out var obj);
        return obj;
    }

    public static object GetActiveObject(Guid clsid)
    {
        GetActiveObject(ref clsid, IntPtr.Zero, out var obj);
        return obj;
    }
}

/// <summary>
/// Manages the connection to a running Alphacam instance via COM.
/// All access to COM objects is marshalled through StaThreadDispatcher to ensure STA thread safety.
/// </summary>
public class AlphacamConnection : IDisposable
{
    private readonly StaThreadDispatcher _dispatcher;
    private readonly ILogger<AlphacamConnection> _logger;
    private dynamic? _applicationObject;
    private volatile bool _isConnected;
    private volatile bool _isDisposed;

    /// <summary>
    /// Raised when the connection state changes.
    /// </summary>
    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    /// <summary>
    /// Gets the current connection state.
    /// </summary>
    public bool IsConnected => _isConnected;

    public AlphacamConnection(StaThreadDispatcher dispatcher, ILogger<AlphacamConnection> logger)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _isConnected = false;
        _isDisposed = false;
    }

    /// <summary>
    /// Attempts to connect to a running Alphacam instance.
    /// First tries to get the active object; if that fails, creates a new instance.
    /// </summary>
    /// <summary>
    /// Well-known CLSID shared with the CambotAlphacam shim.
    /// The shim registers IAlphaCamApp in the ROT under this CLSID.
    /// </summary>
    private static readonly Guid AlphacamBridgeClsid =
        new Guid("F47AC10B-58CC-4372-A567-0E02B2C3D479");

    public async Task ConnectAsync()
    {
        ThrowIfDisposed();

        _logger.LogInformation("Attempting to connect to Alphacam via ROT");

        try
        {
            _applicationObject = await _dispatcher.InvokeAsync(() =>
            {
                // Get the IAlphaCamApp object that the shim registered in the ROT
                _logger.LogDebug("Looking up IAlphaCamApp in ROT (CLSID {Clsid})", AlphacamBridgeClsid);
                return ComHelper.GetActiveObject(AlphacamBridgeClsid);
            });

            _isConnected = true;
            _logger.LogInformation("Successfully connected to Alphacam");
            RaiseConnectionStateChanged(true);
        }
        catch (Exception ex)
        {
            _isConnected = false;
            _logger.LogError(ex, "Failed to connect to Alphacam");
            RaiseConnectionStateChanged(false);
            throw;
        }
    }

    /// <summary>
    /// Gets the Alphacam Application COM object.
    /// </summary>
    public dynamic Application
    {
        get
        {
            ThrowIfDisposed();
            if (_applicationObject == null || !_isConnected)
                throw new InvalidOperationException("Not connected to Alphacam. Call ConnectAsync first.");
            return _applicationObject;
        }
    }

    /// <summary>
    /// Gets the active drawing from the Alphacam application.
    /// </summary>
    public async Task<dynamic> GetActiveDrawingAsync()
    {
        ThrowIfDisposed();

        try
        {
            return await _dispatcher.InvokeAsync(() => Application.ActiveDrawing);
        }
        catch (COMException ex) when (ex.ErrorCode == unchecked((int)0x800706BA)) // RPC_E_SERVER_DIED
        {
            _logger.LogWarning("Alphacam connection lost (RPC_E_SERVER_DIED), marking disconnected");
            _isConnected = false;
            RaiseConnectionStateChanged(false);
            throw;
        }
        catch (COMException ex) when (ex.ErrorCode == unchecked((int)0x800706BE)) // RPC_E_DISCONNECTED
        {
            _logger.LogWarning("Alphacam connection disconnected (RPC_E_DISCONNECTED), marking disconnected");
            _isConnected = false;
            RaiseConnectionStateChanged(false);
            throw;
        }
    }

    /// <summary>
    /// Disconnects from Alphacam and releases COM references.
    /// </summary>
    public void Disconnect()
    {
        ThrowIfDisposed();

        if (_applicationObject != null)
        {
            try
            {
                _dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        if (_applicationObject != null)
                        {
                            Marshal.ReleaseComObject(_applicationObject);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error releasing COM object");
                    }
                    finally
                    {
                        _applicationObject = null;
                    }
                }).Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during COM object release");
            }
        }

        _isConnected = false;
        _logger.LogInformation("Disconnected from Alphacam");
        RaiseConnectionStateChanged(false);
    }

    private void RaiseConnectionStateChanged(bool isConnected)
    {
        ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(isConnected));
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(AlphacamConnection));
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        Disconnect();
    }
}

/// <summary>
/// Event arguments for connection state changes.
/// </summary>
public class ConnectionStateChangedEventArgs : EventArgs
{
    public bool IsConnected { get; }

    public ConnectionStateChangedEventArgs(bool isConnected)
    {
        IsConnected = isConnected;
    }
}
