// Standalone launcher for testing the Alphacam Bridge add-in
// outside of Alphacam. Instantiates the COM add-in class directly
// and calls InitAlphacamAddIn, then waits for Ctrl+C.

using AlphacamBridge;

Console.WriteLine("═══════════════════════════════════════════");
Console.WriteLine("  Alphacam Bridge — Standalone Launcher");
Console.WriteLine("═══════════════════════════════════════════");
Console.WriteLine();
Console.WriteLine("This launcher starts the bridge pipe server");
Console.WriteLine("without Alphacam. Tool calls that need the");
Console.WriteLine("Alphacam COM interface will fail, but you");
Console.WriteLine("can test connectivity, auth, and list_tools.");
Console.WriteLine();

var addin = new AlphacamBridgeAddin();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("Shutting down...");
    addin.BeforeUnload();
    Environment.Exit(0);
};

var result = addin.InitAlphacamAddIn(0); // 0 = no Alphacam version (standalone mode)

if (result != 0)
{
    Console.Error.WriteLine($"InitAlphacamAddIn returned {result} — initialization failed.");
    return 1;
}

Console.WriteLine();
Console.WriteLine("Bridge is running. Press Ctrl+C to stop.");
Console.WriteLine();

// Keep the process alive
await Task.Delay(Timeout.Infinite);
return 0;
