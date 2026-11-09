using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using AiUsageDock.Core.Abstractions;

namespace AiUsageDock.Core.Providers.Antigravity;

/// <summary>
/// Locates the Antigravity language server process, its CSRF token and the loopback HTTPS port it listens on.
/// </summary>
public sealed class AntigravityProcessDiscovery : IAntigravityDiscovery
{
    private static readonly Regex CsrfTokenRegex =
        new(@"--csrf_token\s+(\S+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AppDataDirRegex =
        new(@"--app_data_dir\s+antigravity", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex MainLogPortRegex =
        new(@"Local:\s+https://127\.0\.0\.1:(\d+)/", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const string StatusPath = "/exa.language_server_pb.LanguageServerService/GetUserStatus";
    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;
    private const int ErrorInsufficientBuffer = 122;
    private const uint LoopbackAddr = 0x0100007F; // 127.0.0.1 in network byte order

    private const int ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    private const string StatusRequestBody =
        "{\"metadata\":{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\",\"ideVersion\":\"unknown\",\"locale\":\"en\"}}";

    private readonly Func<IReadOnlyList<(int Pid, string CommandLine)>> _processEnumerator;
    private readonly Func<int, string, CancellationToken, Task<bool>> _portProber;

    public AntigravityProcessDiscovery(
        Func<IReadOnlyList<(int Pid, string CommandLine)>>? processEnumerator = null,
        Func<int, string, CancellationToken, Task<bool>>? portProber = null)
    {
        _processEnumerator = processEnumerator ?? EnumerateLanguageServerProcesses;
        _portProber = portProber ?? DefaultProbeAsync;
    }

    public static string? ExtractCsrfToken(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        if (!AppDataDirRegex.IsMatch(commandLine)) return null;
        var match = CsrfTokenRegex.Match(commandLine);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static int? ExtractPortFromMainLog(string? logContent)
    {
        if (string.IsNullOrEmpty(logContent)) return null;
        int? port = null;
        foreach (Match match in MainLogPortRegex.Matches(logContent))
        {
            if (int.TryParse(match.Groups[1].Value, out var parsed) && parsed > 0 && parsed <= 65535)
            {
                port = parsed; // last match wins
            }
        }
        return port;
    }

    public static IReadOnlyList<int> GetLoopbackPortsForProcess(int processId)
    {
        var ports = new List<int>();
        if (!OperatingSystem.IsWindows() || processId <= 0) return ports;

        int size = 0;
        int status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
        if (status != ErrorInsufficientBuffer && status != 0) return ports;

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            status = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
            if (status != 0) return ports;

            int entryCount = Marshal.ReadInt32(buffer);
            int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            IntPtr rows = IntPtr.Add(buffer, sizeof(int));

            for (int i = 0; i < entryCount; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(IntPtr.Add(rows, i * rowSize));
                if (row.OwningPid != (uint)processId) continue;
                if (row.LocalAddr != LoopbackAddr) continue;

                var portBytes = BitConverter.GetBytes(row.LocalPort);
                int port = (portBytes[0] << 8) | portBytes[1];
                if (port > 0 && port <= 65535) ports.Add(port);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return ports.Distinct().OrderBy(p => p).ToList();
    }

    public async Task<AntigravityDiscoveryResult> DiscoverAsync(CancellationToken ct = default)
    {
        var discovery = await Task.Run(() =>
        {
            IReadOnlyList<(int Pid, string CommandLine)> processes;
            try
            {
                processes = _processEnumerator() ?? Array.Empty<(int, string)>();
            }
            catch
            {
                processes = Array.Empty<(int, string)>();
            }

            var candidates = new List<int>();
            string? csrfToken = null;
            int processId = 0;

            foreach (var (pid, commandLine) in processes)
            {
                var token = ExtractCsrfToken(commandLine);
                if (token is null) continue;

                csrfToken ??= token;
                if (processId == 0) processId = pid;

                var logPort = TryReadMainLogPort();
                if (logPort is int fromLog) candidates.Add(fromLog);

                candidates.AddRange(GetLoopbackPortsForProcess(pid));
            }

            return (CsrfToken: csrfToken, ProcessId: processId, Candidates: candidates);
        }, ct).ConfigureAwait(false);

        var csrfToken = discovery.CsrfToken;
        var processId = discovery.ProcessId;
        var candidates = discovery.Candidates;

        if (csrfToken is null)
        {
            return new AntigravityDiscoveryResult(
                null,
                "ls_not_found",
                "Antigravity language_server.exe not found.");
        }

        if (candidates.Count == 0)
        {
            candidates.AddRange(Enumerable.Range(1908, 9)); // 1908..1916 inclusive
        }

        foreach (var port in candidates.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            if (port <= 0 || port > 65535) continue;

            try
            {
                if (await _portProber(port, csrfToken, ct).ConfigureAwait(false))
                {
                    return new AntigravityDiscoveryResult(
                        new AntigravityEndpoint(processId, port, csrfToken));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Port not usable; keep probing the remaining candidates.
            }
        }

        return new AntigravityDiscoveryResult(
            null,
            "port_closed",
            "No responding Antigravity port.");
    }

    private static int? TryReadMainLogPort()
    {
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Antigravity", "logs", "main.log");

            if (!File.Exists(logPath)) return null;

            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return ExtractPortFromMainLog(reader.ReadToEnd());
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> DefaultProbeAsync(int port, string csrfToken, CancellationToken ct)
    {
        var target = IPAddress.Parse("127.0.0.1");
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(2.5),
            UseProxy = false,
        };

        handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, sslErrors) =>
            sslErrors == SslPolicyErrors.None || IPAddress.IsLoopback(target);

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2.5) };
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(2.5));

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://127.0.0.1:{port}{StatusPath}")
        {
            Content = new StringContent(StatusRequestBody, Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrEmpty(csrfToken))
        {
            request.Headers.TryAddWithoutValidation("x-codeium-csrf-token", csrfToken);
        }

        request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");

        using var response = await client.SendAsync(request, linked.Token).ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.OK;
    }

    private static IReadOnlyList<(int Pid, string CommandLine)> EnumerateLanguageServerProcesses()
    {
        var results = new List<(int, string)>();
        if (!OperatingSystem.IsWindows()) return results;

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("language_server");
        }
        catch
        {
            return results;
        }

        foreach (var process in processes)
        {
            try
            {
                var commandLine = TryReadCommandLine(process.Id);
                if (!string.IsNullOrEmpty(commandLine))
                {
                    results.Add((process.Id, commandLine));
                }
            }
            catch
            {
                // Skip processes that cannot be inspected.
            }
            finally
            {
                process.Dispose();
            }
        }

        return results;
    }

    private static string? TryReadCommandLine(int processId)
    {
        IntPtr hProcess = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (hProcess == IntPtr.Zero) return null;

        IntPtr buffer = IntPtr.Zero;
        try
        {
            int size = 0x1000;
            buffer = Marshal.AllocHGlobal(size);

            int status = NtQueryInformationProcess(
                hProcess,
                ProcessCommandLineInformation,
                buffer,
                size,
                out int returnLength);

            if (status == StatusInfoLengthMismatch && returnLength > size)
            {
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;

                size = returnLength;
                buffer = Marshal.AllocHGlobal(size);
                status = NtQueryInformationProcess(
                    hProcess,
                    ProcessCommandLineInformation,
                    buffer,
                    size,
                    out returnLength);
            }

            if (status != 0) return null;

            var commandLine = Marshal.PtrToStructure<UnicodeString>(buffer);
            if (commandLine.Buffer == IntPtr.Zero || commandLine.Length == 0) return null;

            return Marshal.PtrToStringUni(commandLine.Buffer, commandLine.Length / 2);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (hProcess != IntPtr.Zero)
            {
                CloseHandle(hProcess);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        bool bOrder,
        int ulAf,
        int tableClass,
        uint reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        IntPtr processInformation,
        int processInformationLength,
        out int returnLength);
}
