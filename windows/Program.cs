using System.Diagnostics;
using System.Net.Sockets;

namespace PhoneGamepadBridge;

internal sealed record Options(
    int LocalPort,
    int DevicePort,
    bool SpawnIproxy,
    string IproxyPath,
    string? Udid,
    bool Verbose)
{
    public static Options? Parse(string[] args)
    {
        var localPort = 5111;
        var devicePort = 5111;
        var spawn = true;
        var iproxy = "iproxy";
        string? udid = null;
        var verbose = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out localPort)) return null;
                    devicePort = localPort;
                    break;
                case "--device-port" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out devicePort)) return null;
                    break;
                case "--iproxy" when i + 1 < args.Length:
                    iproxy = args[++i];
                    break;
                case "--udid" when i + 1 < args.Length:
                    udid = args[++i];
                    break;
                case "--no-iproxy":
                    spawn = false;
                    break;
                case "-v" or "--verbose":
                    verbose = true;
                    break;
                case "-h" or "--help":
                    return null;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    return null;
            }
        }

        return new Options(localPort, devicePort, spawn, iproxy, udid, verbose);
    }

    public static void PrintUsage() => Console.WriteLine(
        """
        PhoneGamepadBridge — relays a Bluetooth controller paired to an iPhone
        into a virtual Xbox 360 pad on this PC, over the USB cable.

          --port <n>          Local TCP port, also the device port (default 5111)
          --device-port <n>   Override the port the iOS app listens on
          --iproxy <path>     Path to iproxy.exe (default: "iproxy" on PATH)
          --udid <udid>       Target a specific device when several are attached
          --no-iproxy         Don't spawn iproxy; assume a tunnel already exists
          -v, --verbose       Print packet rate once a second
          -h, --help          This text
        """);
}

internal static class Program
{
    /// <summary>Go neutral if the phone stops talking for this long.</summary>
    private static readonly TimeSpan InputTimeout = TimeSpan.FromMilliseconds(400);

    private static async Task<int> Main(string[] args)
    {
        var opts = Options.Parse(args);
        if (opts is null)
        {
            Options.PrintUsage();
            return 1;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Process? iproxy = null;

        try
        {
            using var sink = new ViGEmSink();
            Console.WriteLine("Virtual Xbox 360 pad created.");

            if (opts.SpawnIproxy)
            {
                iproxy = StartIproxy(opts);
                Console.WriteLine($"iproxy tunnelling localhost:{opts.LocalPort} -> device:{opts.DevicePort}");
            }

            await RunAsync(opts, sink, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Normal Ctrl+C path.
        }
        catch (DllNotFoundException)
        {
            Console.Error.WriteLine("ViGEmBus driver not found. Install it, then re-run. See README.");
            return 2;
        }
        catch (Exception ex) when (ex.GetType().Name == "VigemBusNotFoundException")
        {
            Console.Error.WriteLine("ViGEmBus driver not found. Install it, then re-run. See README.");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal: {ex.Message}");
            return 3;
        }
        finally
        {
            if (iproxy is { HasExited: false })
            {
                try { iproxy.Kill(entireProcessTree: true); } catch { /* already gone */ }
            }
            iproxy?.Dispose();
        }

        Console.WriteLine("Stopped.");
        return 0;
    }

    private static Process StartIproxy(Options opts)
    {
        var psi = new ProcessStartInfo(opts.IproxyPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Options must precede the positional ports.
        if (opts.Udid is not null)
        {
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add(opts.Udid);
        }
        psi.ArgumentList.Add(opts.LocalPort.ToString());
        psi.ArgumentList.Add(opts.DevicePort.ToString());

        var proc = Process.Start(psi)
                   ?? throw new InvalidOperationException("Could not start iproxy.");

        // Drain the pipes so a chatty iproxy can never block on a full buffer.
        _ = Task.Run(() => proc.StandardOutput.ReadToEnd());
        _ = Task.Run(() => proc.StandardError.ReadToEnd());

        return proc;
    }

    private static async Task RunAsync(Options opts, ViGEmSink sink, CancellationToken ct)
    {
        var reader = new FrameReader();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient { NoDelay = true };
                await client.ConnectAsync("127.0.0.1", opts.LocalPort, ct);
                Console.WriteLine("Phone connected.");

                reader.Reset();
                await PumpAsync(client, reader, sink, opts.Verbose, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (opts.Verbose) Console.WriteLine($"Link down: {ex.Message}");
            }

            sink.Neutral();

            if (ct.IsCancellationRequested) break;
            Console.WriteLine("Waiting for phone...");
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    private static async Task PumpAsync(
        TcpClient client, FrameReader reader, ViGEmSink sink, bool verbose, CancellationToken ct)
    {
        var stream = client.GetStream();
        var lastFrame = Stopwatch.StartNew();
        var lastReport = Stopwatch.StartNew();
        var framesSinceReport = 0;

        while (!ct.IsCancellationRequested)
        {
            if (reader.Capacity == 0)
            {
                // Only reachable if the peer floods garbage that never aligns.
                reader.Reset();
            }

            // A read timeout doubles as the watchdog tick, so the pad can be
            // released without needing a second timer thread.
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(InputTimeout);

            int bytesRead;
            try
            {
                bytesRead = await stream.ReadAsync(reader.FreeMemory, readCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (lastFrame.Elapsed > InputTimeout)
                {
                    sink.Neutral();
                    if (verbose) Console.WriteLine("No input — pad released.");
                }
                continue;
            }

            if (bytesRead == 0) return; // Clean close from the phone.

            reader.Commit(bytesRead);

            while (reader.TryRead(out var frame))
            {
                sink.Apply(frame);
                lastFrame.Restart();
                framesSinceReport++;
            }

            if (verbose && lastReport.Elapsed >= TimeSpan.FromSeconds(1))
            {
                Console.WriteLine($"{framesSinceReport} frames/s");
                framesSinceReport = 0;
                lastReport.Restart();
            }
        }
    }
}
