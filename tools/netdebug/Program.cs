using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DnsClient;

class Program
{
    // Sets to track ports by protocol
    private static HashSet<int> tcpOutgoing = new HashSet<int>();
    private static HashSet<int> tcpIncoming = new HashSet<int>();
    private static HashSet<int> udpOutgoing = new HashSet<int>();
    private static HashSet<int> udpIncoming = new HashSet<int>();
    private static bool sortOutput = false;
    private static string action = "ports";

    // Cache for DNS lookups
    private static ConcurrentDictionary<string, string> dnsCache = new ConcurrentDictionary<string, string>();

    // DNS client using Cloudflare/Google DNS
    private static LookupClient dnsClient = new LookupClient(
        new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53),
        new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53)
    );

    // Regex to parse lines like:
    // UDP: IP 192.168.1.1.52249 > 10.0.0.1.52064: UDP, length 10
    // TCP: IP 172.18.0.3.36594 > 91.222.185.239.443: tcp 35
    private static Regex udpRegex = new Regex(@"^IP (\d+\.\d+\.\d+\.\d+)\.(\d+) > (\d+\.\d+\.\d+\.\d+)\.(\d+): UDP, length (\d+)$");
    private static Regex tcpRegex = new Regex(@"^IP (\d+\.\d+\.\d+\.\d+)\.(\d+) > (\d+\.\d+\.\d+\.\d+)\.(\d+): tcp (\d+)$");

    static async Task Main(string[] args)
    {
        if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
        {
            PrintHelp();
            return;
        }

        action = args[0].ToLower();
        sortOutput = args.Contains("--sort");

        switch (action)
        {
            case "nat":
                await DiscoverNatType();
                break;
            case "ping":
                var host = args.Length >= 2 ? args[1] : "auth.gog.com";
                await PingHost(host);
                break;
            case "speed":
                await SpeedTest();
                break;
            case "gog-ports":
            case "gog-requests":
                var udpTask = RunTcpDump("udp", udpRegex);
                var tcpTask = RunTcpDump("tcp", tcpRegex);
                await Task.WhenAll(udpTask, tcpTask);
                break;
            default:
                Console.WriteLine($"{Red}Error: Unknown action '{action}'{Reset}");
                Console.WriteLine();
                PrintHelp();
                break;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: netdebug <action> [options]");
        Console.WriteLine();
        Console.WriteLine("Actions:");
        Console.WriteLine("  gog-ports      Track unique ports used for GOG Galaxy traffic");
        Console.WriteLine("  gog-requests   Show live GOG Galaxy requests with resolved domains");
        Console.WriteLine("  nat            Discover NAT type (mapping, filtering, hairpinning)");
        Console.WriteLine("  ping [host]    Test latency (default: auth.gog.com)");
        Console.WriteLine("  speed          Test download speed via Cloudflare");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --sort         Sort ports numerically (gog-ports only)");
    }

    private static async Task PingHost(string host)
    {
        Console.WriteLine($"{Bold}Ping Test{Reset} {Dim}({host}){Reset}\n");

        using var ping = new Ping();
        var results = new List<long>();
        var failed = 0;

        // Resolve host first
        Console.Write($"  {Dim}Resolving...{Reset} ");
        IPAddress? ip;
        try
        {
            ip = (await Dns.GetHostAddressesAsync(host)).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ip == null)
            {
                Console.WriteLine($"{Red}failed (no IPv4 address){Reset}");
                return;
            }
            Console.WriteLine($"{ip}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{Red}failed ({ex.Message}){Reset}");
            return;
        }

        Console.WriteLine();

        // Send 10 pings
        for (int i = 0; i < 10; i++)
        {
            Console.Write($"  {Dim}Ping {i + 1}/10:{Reset} ");
            try
            {
                var reply = await ping.SendPingAsync(ip, 3000);
                if (reply.Status == IPStatus.Success)
                {
                    results.Add(reply.RoundtripTime);
                    Console.WriteLine($"{Green}{reply.RoundtripTime}ms{Reset}");
                }
                else
                {
                    failed++;
                    Console.WriteLine($"{Yellow}{reply.Status}{Reset}");
                }
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"{Red}error: {ex.Message}{Reset}");
            }

            if (i < 9) await Task.Delay(500);
        }

        // Summary
        Console.WriteLine($"\n{Cyan}● Results{Reset}\n");

        if (results.Count > 0)
        {
            Console.WriteLine($"  {Dim}Min:{Reset} {results.Min()}ms");
            Console.WriteLine($"  {Dim}Max:{Reset} {results.Max()}ms");
            Console.WriteLine($"  {Dim}Avg:{Reset} {results.Average():F1}ms");
        }

        Console.WriteLine($"  {Dim}Loss:{Reset} {failed}/10 ({failed * 10}%)");
        Console.WriteLine();
    }

    private static async Task SpeedTest()
    {
        Console.WriteLine($"{Bold}Speed Test{Reset} {Dim}(Cloudflare){Reset}\n");

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        var testSizes = new[] { (10, "10MB"), (50, "50MB"), (100, "100MB") };

        foreach (var (sizeMb, label) in testSizes)
        {
            var bytes = sizeMb * 1_000_000;
            var url = $"https://speed.cloudflare.com/__down?bytes={bytes}";

            Console.Write($"  {Dim}Downloading {label}...{Reset} ");

            try
            {
                var sw = Stopwatch.StartNew();
                var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                var buffer = new byte[81920];
                long totalRead = 0;
                using var stream = await response.Content.ReadAsStreamAsync();

                int read;
                while ((read = await stream.ReadAsync(buffer)) > 0)
                {
                    totalRead += read;
                }

                sw.Stop();

                var seconds = sw.Elapsed.TotalSeconds;
                var mbps = (totalRead * 8 / 1_000_000) / seconds;

                Console.WriteLine($"{Green}{mbps:F1} Mbps{Reset} {Dim}({seconds:F1}s){Reset}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{Red}failed: {ex.Message}{Reset}");
            }
        }

        Console.WriteLine();
    }

    private static async Task RunTcpDump(string protocol, Regex regex)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "tcpdump",
            Arguments = $"{protocol} -n -t -q -l -i eth0 -Z root",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (sender, e) => ProcessOutput(e, protocol, regex);
        process.ErrorDataReceived += (sender, e) => { };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync();
    }

    private static string ResolveHost(string ip)
    {
        return dnsCache.GetOrAdd(ip, key =>
        {
            try
            {
                var result = dnsClient.QueryReverse(IPAddress.Parse(key));
                var host = result.Answers.PtrRecords().FirstOrDefault()?.PtrDomainName.Value ?? key;
                return host.TrimEnd('.');
            }
            catch
            {
                return key;
            }
        });
    }

    private static bool IsGogHost(string host)
    {
        return host.Contains("galaxy") || host.Contains("gog.com");
    }

    private static void ProcessOutput(DataReceivedEventArgs e, string protocol, Regex regex)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
            return;

        var match = regex.Match(e.Data);
        if (!match.Success)
            return;

        string srcIp = match.Groups[1].Value;
        int srcPort = int.Parse(match.Groups[2].Value);
        string dstIp = match.Groups[3].Value;
        int dstPort = int.Parse(match.Groups[4].Value);
        int length = int.Parse(match.Groups[5].Value);

        string srcHost = ResolveHost(srcIp);
        string dstHost = ResolveHost(dstIp);

        bool isGogTraffic = IsGogHost(srcHost) || IsGogHost(dstHost);
        if (!isGogTraffic)
            return;

        if (action == "gog-requests")
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            var proto = protocol.ToUpper();

            // Always show GOG on the right side
            string localSide, gogSide, arrow;
            if (IsGogHost(dstHost))
            {
                // Outgoing: server -> GOG
                localSide = $"{srcHost}:{srcPort}";
                gogSide = $"{dstHost}:{dstPort}";
                arrow = "->";
            }
            else
            {
                // Incoming: GOG -> server
                localSide = $"{dstHost}:{dstPort}";
                gogSide = $"{srcHost}:{srcPort}";
                arrow = "<-";
            }

            Console.WriteLine($"[{timestamp}] {proto}\t{localSide}\t{arrow}\t{gogSide}\t({length} bytes)");
        }
        else // gog-ports
        {
            var outgoing = protocol == "tcp" ? tcpOutgoing : udpOutgoing;
            var incoming = protocol == "tcp" ? tcpIncoming : udpIncoming;

            bool changed = false;

            // Outgoing traffic: our server -> GOG
            if (IsGogHost(dstHost))
                changed = outgoing.Add(dstPort);

            // Incoming traffic: GOG -> our server
            if (IsGogHost(srcHost))
                changed = incoming.Add(srcPort) || changed;

            if (changed)
                PrintPorts();
        }
    }

    private static void PrintPorts()
    {
        var tcpOut = sortOutput ? tcpOutgoing.OrderBy(p => p) : tcpOutgoing.AsEnumerable();
        var tcpIn = sortOutput ? tcpIncoming.OrderBy(p => p) : tcpIncoming.AsEnumerable();
        var udpOut = sortOutput ? udpOutgoing.OrderBy(p => p) : udpOutgoing.AsEnumerable();
        var udpIn = sortOutput ? udpIncoming.OrderBy(p => p) : udpIncoming.AsEnumerable();

        Console.Clear();
        Console.WriteLine("TCP Outgoing (server -> GOG): " + string.Join(", ", tcpOut));
        Console.WriteLine("TCP Incoming (GOG -> server): " + string.Join(", ", tcpIn));
        Console.WriteLine();
        Console.WriteLine("UDP Outgoing (server -> GOG): " + string.Join(", ", udpOut));
        Console.WriteLine("UDP Incoming (GOG -> server): " + string.Join(", ", udpIn));
    }

    // STUN servers for basic mapping tests
    private static readonly (string host, int port)[] stunServers = new[]
    {
        ("stun.l.google.com", 19302),
        ("stun1.l.google.com", 19302),
        ("stun.cloudflare.com", 3478),
    };

    // STUN servers that support CHANGE-REQUEST for filtering tests (RFC 5780)
    private static readonly (string host, int port)[] natTestServers = new[]
    {
        ("stun.nextcloud.com", 3478),
        ("stun.freeswitch.org", 3478),
        ("stun.siptrunk.com", 3478),
        ("stun.telnyx.com", 3478),
        ("stun.signalwire.com", 3478),
    };

    // ANSI color codes
    private const string Reset = "\x1b[0m";
    private const string Bold = "\x1b[1m";
    private const string Dim = "\x1b[2m";
    private const string Green = "\x1b[32m";
    private const string Yellow = "\x1b[33m";
    private const string Cyan = "\x1b[36m";
    private const string Red = "\x1b[31m";

    private static async Task DiscoverNatType()
    {
        Console.WriteLine($"{Bold}NAT Behavior Discovery{Reset} {Dim}(RFC 4787){Reset}\n");

        // Create a single UDP socket to use for all tests
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        socket.ReceiveTimeout = 3000;

        var localEndpoint = (IPEndPoint)socket.LocalEndPoint!;
        Console.WriteLine($"  {Dim}Local endpoint:{Reset} {localEndpoint}\n");

        // Track results for final summary
        bool? isEndpointIndependentMapping = null;
        string? filteringResult = null;
        string? finalNatType = null;

        // Test 1: Mapping Behavior
        Console.WriteLine($"{Cyan}● Mapping Behavior{Reset}\n");

        var mappings = new List<(string server, IPEndPoint mapped)>();

        foreach (var (host, port) in stunServers)
        {
            Console.Write($"  {Dim}{host}:{port} →{Reset} ");
            try
            {
                var serverAddr = await ResolveHostAsync(host);
                var serverEndpoint = new IPEndPoint(serverAddr, port);
                var mapped = await StunBindingRequest(socket, serverEndpoint);

                if (mapped != null)
                {
                    Console.WriteLine($"{mapped}");
                    mappings.Add((host, mapped));
                }
                else
                {
                    Console.WriteLine($"{Yellow}no response{Reset}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{Red}error: {ex.Message}{Reset}");
            }
        }

        Console.WriteLine();

        if (mappings.Count < 2)
        {
            Console.WriteLine($"  {Yellow}[!] Unable to determine (not enough responses){Reset}\n");
        }
        else
        {
            var uniquePorts = mappings.Select(m => m.mapped.Port).Distinct().ToList();
            var uniqueIps = mappings.Select(m => m.mapped.Address.ToString()).Distinct().ToList();

            Console.WriteLine($"  {Dim}Public IP:{Reset} {string.Join(", ", uniqueIps)}");
            Console.WriteLine($"  {Dim}Mapped ports:{Reset} {string.Join(", ", mappings.Select(m => m.mapped.Port))}");

            isEndpointIndependentMapping = uniquePorts.Count == 1;

            if (isEndpointIndependentMapping == true)
            {
                Console.WriteLine($"  {Green}[✓] Endpoint-Independent Mapping{Reset}");
                Console.WriteLine($"    {Dim}Full Cone / Restricted Cone / Port Restricted Cone (NAT 1/2/3){Reset}");
            }
            else
            {
                Console.WriteLine($"  {Yellow}[!] Address- and Port-Dependent Mapping{Reset}");
                Console.WriteLine($"    {Dim}Symmetric NAT (NAT 4){Reset}");
                finalNatType = "Strict";
            }
        }

        // Test 2: Hairpinning
        Console.WriteLine($"\n{Cyan}● Hairpinning{Reset}\n");

        if (mappings.Count > 0)
        {
            var externalEndpoint = mappings[0].mapped;
            Console.Write($"  {Dim}Loopback via {externalEndpoint} →{Reset} ");

            var hairpinResult = await TestHairpinning(socket, externalEndpoint);

            if (hairpinResult)
            {
                Console.WriteLine($"{Green}[✓] Supported{Reset}");
                Console.WriteLine($"    {Dim}Internal hosts can communicate via external address{Reset}");
            }
            else
            {
                Console.WriteLine($"{Yellow}[✗] Not supported{Reset}");
                Console.WriteLine($"    {Dim}Internal hosts must use internal addresses{Reset}");
            }
        }
        else
        {
            Console.WriteLine($"  {Dim}Skipped (no external mapping available){Reset}");
        }

        // Test 3: Port Preservation
        Console.WriteLine($"\n{Cyan}● Port Preservation{Reset}\n");

        if (mappings.Count > 0)
        {
            var localPort = localEndpoint.Port;
            var externalPort = mappings[0].mapped.Port;
            var preserved = localPort == externalPort;

            Console.WriteLine($"  {Dim}Local port:{Reset} {localPort}");
            Console.WriteLine($"  {Dim}External port:{Reset} {externalPort}");

            if (preserved)
                Console.WriteLine($"  {Green}[✓] Port preserved{Reset}");
            else
                Console.WriteLine($"  {Yellow}[✗] Port not preserved{Reset}");
        }

        // Test 4: Filtering Behavior (only if mapping is Endpoint-Independent)
        Console.WriteLine($"\n{Cyan}● Filtering Behavior{Reset}\n");

        if (finalNatType == "Strict")
        {
            Console.WriteLine($"  {Dim}Skipped (Symmetric NAT - filtering irrelevant for NAT type){Reset}");
        }
        else if (isEndpointIndependentMapping == true)
        {
            foreach (var (host, port) in natTestServers)
            {
                Console.Write($"  {Dim}Testing {host}:{port}...{Reset} ");
                try
                {
                    var serverAddr = await ResolveHostAsync(host);
                    var serverEndpoint = new IPEndPoint(serverAddr, port);

                    // Test 1: Change IP + Change Port (Full Cone test)
                    var fullConeResult = await StunChangeRequest(socket, serverEndpoint, changeIp: true, changePort: true);
                    if (fullConeResult)
                    {
                        Console.WriteLine($"{Green}✓{Reset}");
                        filteringResult = "full_cone";
                        break;
                    }

                    // Test 2: Change Port only (Restricted Cone test)
                    var restrictedResult = await StunChangeRequest(socket, serverEndpoint, changeIp: false, changePort: true);
                    if (restrictedResult)
                    {
                        Console.WriteLine($"{Green}✓{Reset}");
                        filteringResult = "restricted_cone";
                        break;
                    }

                    // If we got mapping but no change responses, it's Port Restricted
                    var basicTest = await StunBindingRequest(socket, serverEndpoint);
                    if (basicTest != null)
                    {
                        Console.WriteLine($"{Green}✓{Reset}");
                        filteringResult = "port_restricted";
                        break;
                    }

                    Console.WriteLine($"{Dim}no RFC 5780 support{Reset}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{Dim}error: {ex.Message}{Reset}");
                }
            }

            if (filteringResult != null)
            {
                switch (filteringResult)
                {
                    case "full_cone":
                        Console.WriteLine($"\n  {Green}[✓] Endpoint-Independent Filtering{Reset}");
                        Console.WriteLine($"    {Dim}Full Cone NAT (NAT 1){Reset}");
                        finalNatType = "Open";
                        break;
                    case "restricted_cone":
                        Console.WriteLine($"\n  {Green}[✓] Address-Dependent Filtering{Reset}");
                        Console.WriteLine($"    {Dim}Restricted Cone NAT (NAT 2){Reset}");
                        finalNatType = "Moderate";
                        break;
                    case "port_restricted":
                        Console.WriteLine($"\n  {Yellow}[!] Address- and Port-Dependent Filtering{Reset}");
                        Console.WriteLine($"    {Dim}Port Restricted Cone NAT (NAT 3){Reset}");
                        finalNatType = "Moderate";
                        break;
                }
            }
            else
            {
                Console.WriteLine($"\n  {Dim}Could not determine (no RFC 5780 servers responded){Reset}");
            }
        }
        else
        {
            Console.WriteLine($"  {Dim}Skipped (mapping test incomplete){Reset}");
        }

        // Final Summary
        Console.WriteLine($"\n{Cyan}● NAT Type{Reset}\n");

        if (finalNatType != null)
        {
            var color = finalNatType == "Open" ? Green : (finalNatType == "Moderate" ? Yellow : Red);
            var icon = finalNatType == "Open" ? "[✓]" : "[!]";
            Console.WriteLine($"  {color}{icon} {finalNatType}{Reset}");

            switch (finalNatType)
            {
                case "Open":
                    Console.WriteLine($"    {Dim}Full Cone NAT - best for P2P, all connections work{Reset}");
                    break;
                case "Moderate":
                    Console.WriteLine($"    {Dim}Restricted/Port Restricted - most connections work{Reset}");
                    break;
                case "Strict":
                    Console.WriteLine($"    {Dim}Symmetric NAT - may require relay for some connections{Reset}");
                    break;
            }
        }
        else if (isEndpointIndependentMapping == true)
        {
            Console.WriteLine($"  {Yellow}[!] Open or Moderate{Reset}");
            Console.WriteLine($"    {Dim}Endpoint-Independent mapping, but filtering unknown{Reset}");
        }
        else
        {
            Console.WriteLine($"  {Dim}Unable to determine{Reset}");
        }

        Console.WriteLine();
    }

    private static async Task<bool> StunChangeRequest(Socket socket, IPEndPoint server, bool changeIp, bool changePort)
    {
        // Build STUN Binding Request with CHANGE-REQUEST attribute
        var transactionId = RandomNumberGenerator.GetBytes(12);
        var request = new byte[28]; // 20 header + 8 attribute

        // Message Type: Binding Request (0x0001)
        request[0] = 0x00;
        request[1] = 0x01;

        // Message Length: 8 (CHANGE-REQUEST attribute)
        request[2] = 0x00;
        request[3] = 0x08;

        // Magic Cookie: 0x2112A442
        request[4] = 0x21;
        request[5] = 0x12;
        request[6] = 0xA4;
        request[7] = 0x42;

        // Transaction ID
        Array.Copy(transactionId, 0, request, 8, 12);

        // CHANGE-REQUEST attribute (RFC 5780)
        // Type: 0x0003
        request[20] = 0x00;
        request[21] = 0x03;
        // Length: 4
        request[22] = 0x00;
        request[23] = 0x04;
        // Flags: 0x04 = change IP, 0x02 = change port
        byte flags = 0;
        if (changeIp) flags |= 0x04;
        if (changePort) flags |= 0x02;
        request[24] = 0x00;
        request[25] = 0x00;
        request[26] = 0x00;
        request[27] = flags;

        // Send request
        await socket.SendToAsync(request, SocketFlags.None, server);

        // Wait for response with short timeout
        var buffer = new byte[512];
        var cts = new CancellationTokenSource(2000);

        try
        {
            var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cts.Token);
            // Verify it's a Binding Response (0x0101)
            return result.ReceivedBytes >= 20 && buffer[0] == 0x01 && buffer[1] == 0x01;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TestHairpinning(Socket socket, IPEndPoint externalEndpoint)
    {
        try
        {
            // Try to send a packet to our own external address
            var testData = new byte[] { 0x00 };
            await socket.SendToAsync(testData, SocketFlags.None, externalEndpoint);

            // Try to receive it back (with short timeout)
            var buffer = new byte[64];
            var cts = new CancellationTokenSource(1000);

            try
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, externalEndpoint, cts.Token);
                return result.ReceivedBytes > 0;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static async Task<IPAddress> ResolveHostAsync(string host)
    {
        var addresses = await Dns.GetHostAddressesAsync(host);
        return addresses.First(a => a.AddressFamily == AddressFamily.InterNetwork);
    }

    private static async Task<IPEndPoint?> StunBindingRequest(Socket socket, IPEndPoint server)
    {
        // Build STUN Binding Request
        var transactionId = RandomNumberGenerator.GetBytes(12);
        var request = new byte[20];

        // Message Type: Binding Request (0x0001)
        request[0] = 0x00;
        request[1] = 0x01;

        // Message Length: 0
        request[2] = 0x00;
        request[3] = 0x00;

        // Magic Cookie: 0x2112A442
        request[4] = 0x21;
        request[5] = 0x12;
        request[6] = 0xA4;
        request[7] = 0x42;

        // Transaction ID
        Array.Copy(transactionId, 0, request, 8, 12);

        // Send request
        await socket.SendToAsync(request, SocketFlags.None, server);

        // Receive response
        var buffer = new byte[512];
        var receiveTask = socket.ReceiveFromAsync(buffer, SocketFlags.None, server);
        var timeoutTask = Task.Delay(3000);

        var completedTask = await Task.WhenAny(receiveTask, timeoutTask);
        if (completedTask == timeoutTask)
            return null;

        var result = await receiveTask;
        var response = buffer.AsSpan(0, result.ReceivedBytes);

        // Verify it's a Binding Response (0x0101)
        if (response[0] != 0x01 || response[1] != 0x01)
            return null;

        // Parse attributes
        int pos = 20; // Skip header
        while (pos + 4 <= response.Length)
        {
            int attrType = (response[pos] << 8) | response[pos + 1];
            int attrLen = (response[pos + 2] << 8) | response[pos + 3];
            pos += 4;

            if (pos + attrLen > response.Length)
                break;

            // XOR-MAPPED-ADDRESS (0x0020) or MAPPED-ADDRESS (0x0001)
            if (attrType == 0x0020 || attrType == 0x0001)
            {
                // Skip first byte (reserved), check family
                int family = response[pos + 1];
                if (family != 0x01) // IPv4
                {
                    pos += (attrLen + 3) & ~3; // Align to 4 bytes
                    continue;
                }

                int port;
                byte[] ipBytes = new byte[4];

                if (attrType == 0x0020) // XOR-MAPPED-ADDRESS
                {
                    port = ((response[pos + 2] << 8) | response[pos + 3]) ^ 0x2112;
                    ipBytes[0] = (byte)(response[pos + 4] ^ 0x21);
                    ipBytes[1] = (byte)(response[pos + 5] ^ 0x12);
                    ipBytes[2] = (byte)(response[pos + 6] ^ 0xA4);
                    ipBytes[3] = (byte)(response[pos + 7] ^ 0x42);
                }
                else // MAPPED-ADDRESS
                {
                    port = (response[pos + 2] << 8) | response[pos + 3];
                    ipBytes[0] = response[pos + 4];
                    ipBytes[1] = response[pos + 5];
                    ipBytes[2] = response[pos + 6];
                    ipBytes[3] = response[pos + 7];
                }

                return new IPEndPoint(new IPAddress(ipBytes), port);
            }

            pos += (attrLen + 3) & ~3; // Align to 4 bytes
        }

        return null;
    }
}
