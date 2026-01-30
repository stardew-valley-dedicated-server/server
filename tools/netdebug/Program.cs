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
using Spectre.Console;

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
                if (!await CheckTcpDumpAvailable())
                {
                    AnsiConsole.MarkupLine("[red]Error:[/] tcpdump is not installed or not in PATH");
                    AnsiConsole.MarkupLine("[dim]Install tcpdump to use gog-ports/gog-requests[/]");
                    return;
                }
                var udpTask = RunTcpDump("udp", udpRegex);
                var tcpTask = RunTcpDump("tcp", tcpRegex);
                await Task.WhenAll(udpTask, tcpTask);
                break;
            default:
                AnsiConsole.MarkupLine($"[red]Error:[/] Unknown action '[yellow]{action}[/]'");
                AnsiConsole.WriteLine();
                PrintHelp();
                break;
        }
    }

    private static void PrintHelp()
    {
        var panel = new Panel(
            new Rows(
                new Markup("[bold]netdebug[/] [dim]<action>[/] [dim][[options]][/]"),
                new Rule().RuleStyle("dim"),
                new Markup("\n[cyan]Actions:[/]"),
                new Markup("  [white]nat[/]            Discover NAT type (mapping, filtering, hairpinning)"),
                new Markup("  [white]ping[/] [dim][[host]][/]   Test latency (default: auth.gog.com)"),
                new Markup("  [white]speed[/]          Test download speed via Cloudflare"),
                new Markup("  [white]gog-ports[/]      Track unique ports used for GOG Galaxy traffic"),
                new Markup("  [white]gog-requests[/]   Show live GOG Galaxy requests with resolved domains"),
                new Markup("\n[cyan]Options:[/]"),
                new Markup("  [white]--sort[/]         Sort ports numerically (gog-ports only)")
            ))
        {
            Header = new PanelHeader(" Network Diagnostics ", Justify.Center),
            Border = BoxBorder.Rounded,
            Padding = new Padding(2, 1)
        };

        AnsiConsole.Write(panel);
    }

    private static async Task PingHost(string host)
    {
        AnsiConsole.Write(new Rule($"[cyan]Ping Test[/] [dim]({host})[/]").LeftJustified());
        AnsiConsole.WriteLine();

        using var ping = new Ping();
        var results = new List<long>();
        var failed = 0;

        // Resolve host first
        IPAddress? ip = null;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Resolving hostname...", async ctx =>
            {
                try
                {
                    ip = (await Dns.GetHostAddressesAsync(host)).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                }
                catch { }
            });

        if (ip == null)
        {
            AnsiConsole.MarkupLine("[red]Failed to resolve hostname (no IPv4 address)[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[dim]Resolved to:[/] [white]{ip}[/]");
        AnsiConsole.WriteLine();

        // Send 10 pings with progress
        await AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn()
            )
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[cyan]Pinging...[/]", maxValue: 10);

                for (int i = 0; i < 10; i++)
                {
                    try
                    {
                        var reply = await ping.SendPingAsync(ip, 3000);
                        if (reply.Status == IPStatus.Success)
                        {
                            results.Add(reply.RoundtripTime);
                        }
                        else
                        {
                            failed++;
                        }
                    }
                    catch
                    {
                        failed++;
                    }

                    task.Increment(1);
                    if (i < 9) await Task.Delay(500);
                }
            });

        AnsiConsole.WriteLine();

        // Results table
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[cyan]Metric[/]").Centered())
            .AddColumn(new TableColumn("[cyan]Value[/]").Centered());

        if (results.Count > 0)
        {
            table.AddRow("Min", $"[green]{results.Min()}ms[/]");
            table.AddRow("Max", $"[yellow]{results.Max()}ms[/]");
            table.AddRow("Avg", $"[white]{results.Average():F1}ms[/]");
        }

        var lossColor = failed == 0 ? "green" : (failed < 5 ? "yellow" : "red");
        table.AddRow("Packet Loss", $"[{lossColor}]{failed}/10 ({failed * 10}%)[/]");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static async Task SpeedTest()
    {
        AnsiConsole.Write(new Rule("[cyan]Speed Test[/] [dim](Cloudflare)[/]").LeftJustified());
        AnsiConsole.WriteLine();

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        var testSizes = new[] { (10, "10 MB"), (50, "50 MB"), (100, "100 MB") };
        var speedResults = new List<(string size, double mbps, double seconds)>();

        await AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new TransferSpeedColumn(),
                new SpinnerColumn()
            )
            .StartAsync(async ctx =>
            {
                foreach (var (sizeMb, label) in testSizes)
                {
                    var bytes = sizeMb * 1_000_000;
                    var url = $"https://speed.cloudflare.com/__down?bytes={bytes}";
                    var task = ctx.AddTask($"[cyan]{label}[/]", maxValue: bytes);

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
                            task.Value = totalRead;
                        }

                        sw.Stop();
                        var seconds = sw.Elapsed.TotalSeconds;
                        var mbps = (totalRead * 8.0 / 1_000_000) / seconds;
                        speedResults.Add((label, mbps, seconds));
                    }
                    catch
                    {
                        task.StopTask();
                        speedResults.Add((label, -1, 0));
                    }
                }
            });

        AnsiConsole.WriteLine();

        // Results table
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[cyan]Size[/]").Centered())
            .AddColumn(new TableColumn("[cyan]Speed[/]").Centered())
            .AddColumn(new TableColumn("[cyan]Time[/]").Centered());

        foreach (var (size, mbps, seconds) in speedResults)
        {
            if (mbps < 0)
            {
                table.AddRow(size, "[red]Failed[/]", "-");
            }
            else
            {
                var speedColor = mbps > 50 ? "green" : (mbps > 10 ? "yellow" : "red");
                table.AddRow(size, $"[{speedColor}]{mbps:F1} Mbps[/]", $"[dim]{seconds:F1}s[/]");
            }
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static async Task<bool> CheckTcpDumpAvailable()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "tcpdump",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
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
        process.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                AnsiConsole.MarkupLine($"[red]tcpdump ({protocol}):[/] {Markup.Escape(e.Data)}");
            }
        };

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
            string localSide, gogSide, arrow, arrowColor;
            if (IsGogHost(dstHost))
            {
                // Outgoing: server -> GOG
                localSide = $"{srcHost}:{srcPort}";
                gogSide = $"{dstHost}:{dstPort}";
                arrow = "->";
                arrowColor = "green";
            }
            else
            {
                // Incoming: GOG -> server
                localSide = $"{dstHost}:{dstPort}";
                gogSide = $"{srcHost}:{srcPort}";
                arrow = "<-";
                arrowColor = "cyan";
            }

            AnsiConsole.MarkupLine($"[dim]{timestamp}[/] [yellow]{proto}[/] {Markup.Escape(localSide)} [{arrowColor}]{arrow}[/] [cyan]{Markup.Escape(gogSide)}[/] [dim]({length} bytes)[/]");
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

        AnsiConsole.Clear();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[cyan]GOG Galaxy Ports[/]")
            .AddColumn(new TableColumn("[yellow]Protocol[/]").Centered())
            .AddColumn(new TableColumn("[green]Outgoing (Server -> GOG)[/]"))
            .AddColumn(new TableColumn("[cyan]Incoming (GOG -> Server)[/]"));

        table.AddRow(
            "[yellow]TCP[/]",
            string.Join(", ", tcpOut),
            string.Join(", ", tcpIn)
        );
        table.AddRow(
            "[yellow]UDP[/]",
            string.Join(", ", udpOut),
            string.Join(", ", udpIn)
        );

        AnsiConsole.Write(table);
    }

    /// <summary>
    /// Redacts a public IP address for privacy, showing only the first octet.
    /// Example: 203.45.67.89 becomes 203.x.x.x
    /// </summary>
    private static string RedactPublicIp(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        if (bytes.Length == 4)
        {
            // Check if it's a private IP (don't redact those)
            if (IsPrivateIp(bytes))
                return ip.ToString();
            return $"{bytes[0]}.x.x.x";
        }
        return ip.ToString();
    }

    private static bool IsPrivateIp(byte[] bytes)
    {
        // 10.0.0.0/8
        if (bytes[0] == 10) return true;
        // 172.16.0.0/12
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
        // 192.168.0.0/16
        if (bytes[0] == 192 && bytes[1] == 168) return true;
        // 127.0.0.0/8 (loopback)
        if (bytes[0] == 127) return true;
        return false;
    }

    /// <summary>
    /// Formats an endpoint with redacted public IP for display.
    /// </summary>
    private static string FormatEndpoint(IPEndPoint endpoint, bool redactIp = true)
    {
        var ip = redactIp ? RedactPublicIp(endpoint.Address) : endpoint.Address.ToString();
        return $"{ip}:{endpoint.Port}";
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

    private static async Task DiscoverNatType()
    {
        AnsiConsole.Write(new Rule("[bold blue]NAT Behavior Discovery[/] [grey](RFC 4787)[/]").LeftJustified());
        AnsiConsole.WriteLine();

        // Create a single UDP socket to use for all tests
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        socket.ReceiveTimeout = 3000;

        var localEndpoint = (IPEndPoint)socket.LocalEndPoint!;
        AnsiConsole.MarkupLine($"  [grey]Local endpoint:[/] [white]{localEndpoint}[/]");
        AnsiConsole.WriteLine();

        // Track results for final summary
        var mappings = new List<(string server, IPEndPoint mapped)>();
        bool? isEndpointIndependentMapping = null;
        string? filteringResult = null;
        string? finalNatType = null;
        bool? hairpinSupported = null;
        bool? portPreserved = null;

        // Test 1: Mapping Behavior
        AnsiConsole.Write(new Rule("[blue]Mapping Behavior[/]").LeftJustified().RuleStyle("grey"));

        var mappingTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[white]STUN Server[/]"))
            .AddColumn(new TableColumn("[white]External Endpoint[/]"))
            .AddColumn(new TableColumn("[white]Status[/]").Centered());

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("blue"))
            .StartAsync("Testing mapping behavior...", async ctx =>
            {
                foreach (var (host, port) in stunServers)
                {
                    ctx.Status($"[grey]Querying {host}...[/]");
                    try
                    {
                        var serverAddr = await ResolveHostAsync(host);
                        var serverEndpoint = new IPEndPoint(serverAddr, port);
                        var mapped = await StunBindingRequest(socket, serverEndpoint);

                        if (mapped != null)
                        {
                            mappings.Add((host, mapped));
                            mappingTable.AddRow(
                                $"[grey]{host}:{port}[/]",
                                $"[white]{FormatEndpoint(mapped)}[/]",
                                "[green]OK[/]"
                            );
                        }
                        else
                        {
                            mappingTable.AddRow(
                                $"[grey]{host}:{port}[/]",
                                "[grey]-[/]",
                                "[red]No response[/]"
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        mappingTable.AddRow(
                            $"[grey]{host}:{port}[/]",
                            "[grey]-[/]",
                            $"[red]{Markup.Escape(ex.Message)}[/]"
                        );
                    }
                }
            });

        AnsiConsole.Write(mappingTable);

        if (mappings.Count >= 2)
        {
            var uniquePorts = mappings.Select(m => m.mapped.Port).Distinct().ToList();
            var uniqueIps = mappings.Select(m => RedactPublicIp(m.mapped.Address)).Distinct().ToList();

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"  [grey]Public IP:[/] [white]{string.Join(", ", uniqueIps)}[/]");
            AnsiConsole.MarkupLine($"  [grey]Mapped ports:[/] [white]{string.Join(", ", mappings.Select(m => m.mapped.Port))}[/]");

            isEndpointIndependentMapping = uniquePorts.Count == 1;

            if (isEndpointIndependentMapping == true)
            {
                AnsiConsole.MarkupLine("  [green][[OK]][/] Endpoint-Independent Mapping [grey](Full/Restricted/Port Restricted Cone)[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("  [red][[!!]][/] Address- and Port-Dependent Mapping [grey](Symmetric NAT)[/]");
                finalNatType = "Strict";
            }
        }
        else
        {
            AnsiConsole.MarkupLine("  [red][[!!]][/] Unable to determine mapping behavior [grey](not enough responses)[/]");
        }

        AnsiConsole.WriteLine();

        // Test 2: Hairpinning
        AnsiConsole.Write(new Rule("[blue]Hairpinning[/]").LeftJustified().RuleStyle("grey"));

        if (mappings.Count > 0)
        {
            var externalEndpoint = mappings[0].mapped;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("blue"))
                .StartAsync($"[grey]Testing hairpinning via {FormatEndpoint(externalEndpoint)}...[/]", async ctx =>
                {
                    hairpinSupported = await TestHairpinning(socket, externalEndpoint);
                });

            if (hairpinSupported == true)
            {
                AnsiConsole.MarkupLine("  [green][[OK]][/] Supported [grey]- Internal hosts can communicate via external address[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("  [red][[!!]][/] Not supported [grey]- Internal hosts must use internal addresses[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLine("  [grey]Skipped (no external mapping available)[/]");
        }

        AnsiConsole.WriteLine();

        // Test 3: Port Preservation
        AnsiConsole.Write(new Rule("[blue]Port Preservation[/]").LeftJustified().RuleStyle("grey"));

        if (mappings.Count > 0)
        {
            var localPort = localEndpoint.Port;
            var externalPort = mappings[0].mapped.Port;
            portPreserved = localPort == externalPort;

            AnsiConsole.MarkupLine($"  [grey]Local port:[/] [white]{localPort}[/]  [grey]External port:[/] [white]{externalPort}[/]");

            if (portPreserved == true)
            {
                AnsiConsole.MarkupLine("  [green][[OK]][/] Port preserved");
            }
            else
            {
                AnsiConsole.MarkupLine("  [grey][[--]][/] Port not preserved [grey](normal behavior)[/]");
            }
        }

        AnsiConsole.WriteLine();

        // Test 4: Filtering Behavior
        AnsiConsole.Write(new Rule("[blue]Filtering Behavior[/]").LeftJustified().RuleStyle("grey"));

        if (finalNatType == "Strict")
        {
            AnsiConsole.MarkupLine("  [grey]Skipped (NAT type already determined as Strict)[/]");
        }
        else if (isEndpointIndependentMapping == true)
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("blue"))
                .StartAsync("[grey]Testing filtering behavior...[/]", async ctx =>
                {
                    foreach (var (host, port) in natTestServers)
                    {
                        ctx.Status($"[grey]Testing {host}...[/]");
                        try
                        {
                            var serverAddr = await ResolveHostAsync(host);
                            var serverEndpoint = new IPEndPoint(serverAddr, port);

                            // First verify this server responds to basic binding
                            var basicTest = await StunBindingRequest(socket, serverEndpoint);
                            if (basicTest == null)
                                continue;

                            // Test 1: Change IP + Change Port (Full Cone test)
                            var fullConeResult = await StunChangeRequest(socket, serverEndpoint, changeIp: true, changePort: true);
                            if (fullConeResult)
                            {
                                filteringResult = "full_cone";
                                break;
                            }

                            // Test 2: Change Port only (Restricted Cone test)
                            var restrictedResult = await StunChangeRequest(socket, serverEndpoint, changeIp: false, changePort: true);
                            if (restrictedResult)
                            {
                                filteringResult = "restricted_cone";
                                break;
                            }

                            // Server responds to basic binding but not to change requests
                            filteringResult = "port_restricted";
                            break;
                        }
                        catch
                        {
                            continue;
                        }
                    }
                });

            if (filteringResult != null)
            {
                switch (filteringResult)
                {
                    case "full_cone":
                        AnsiConsole.MarkupLine("  [green][[OK]][/] Endpoint-Independent Filtering [grey](Full Cone NAT)[/]");
                        finalNatType = "Open";
                        break;
                    case "restricted_cone":
                        AnsiConsole.MarkupLine("  [green][[OK]][/] Address-Dependent Filtering [grey](Restricted Cone NAT)[/]");
                        finalNatType = "Moderate";
                        break;
                    case "port_restricted":
                        AnsiConsole.MarkupLine("  [green][[OK]][/] Address- and Port-Dependent Filtering [grey](Port Restricted Cone NAT)[/]");
                        finalNatType = "Moderate";
                        break;
                }
            }
            else
            {
                AnsiConsole.MarkupLine("  [grey]Could not determine (no RFC 5780 servers responded)[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLine("  [grey]Skipped (mapping test incomplete)[/]");
        }

        AnsiConsole.WriteLine();

        // Final Summary
        AnsiConsole.Write(new Rule("[bold blue]Summary[/]").LeftJustified());
        AnsiConsole.WriteLine();

        var summaryTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[white]Property[/]"))
            .AddColumn(new TableColumn("[white]Result[/]"));

        // NAT Type
        if (finalNatType != null)
        {
            var (natColor, natDesc) = finalNatType switch
            {
                "Open" => ("green", "Full Cone NAT - best for P2P"),
                "Moderate" => ("blue", "Restricted/Port Restricted - most connections work"),
                "Strict" => ("red", "Symmetric NAT - may require relay"),
                _ => ("grey", "Unknown")
            };
            summaryTable.AddRow("NAT Type", $"[{natColor} bold]{finalNatType}[/] [grey]({natDesc})[/]");
        }
        else if (isEndpointIndependentMapping == true)
        {
            summaryTable.AddRow("NAT Type", "[blue]Open or Moderate[/] [grey](filtering unknown)[/]");
        }
        else
        {
            summaryTable.AddRow("NAT Type", "[grey]Unable to determine[/]");
        }

        // Other properties
        if (mappings.Count > 0)
        {
            var uniqueIps = mappings.Select(m => RedactPublicIp(m.mapped.Address)).Distinct();
            summaryTable.AddRow("Public IP", $"[white]{string.Join(", ", uniqueIps)}[/]");
        }

        if (isEndpointIndependentMapping.HasValue)
        {
            summaryTable.AddRow("Mapping", isEndpointIndependentMapping.Value
                ? "[green]Endpoint-Independent[/]"
                : "[red]Address/Port-Dependent[/]");
        }

        if (hairpinSupported.HasValue)
        {
            summaryTable.AddRow("Hairpinning", hairpinSupported.Value
                ? "[green]Supported[/]"
                : "[red]Not Supported[/]");
        }

        if (portPreserved.HasValue)
        {
            summaryTable.AddRow("Port Preservation", portPreserved.Value
                ? "[green]Yes[/]"
                : "[grey]No[/]");
        }

        AnsiConsole.Write(summaryTable);
        AnsiConsole.WriteLine();
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

        // Wait for response with timeout
        var buffer = new byte[512];
        var cts = new CancellationTokenSource(3000);

        try
        {
            var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cts.Token);
            // Verify it's a Binding Response (0x0101)
            if (result.ReceivedBytes < 20 || buffer[0] != 0x01 || buffer[1] != 0x01)
                return false;

            // Verify transaction ID matches (bytes 8-19)
            for (int i = 0; i < 12; i++)
            {
                if (buffer[8 + i] != transactionId[i])
                    return false;
            }
            return true;
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

    private static async Task<bool> TestHairpinning(Socket mainSocket, IPEndPoint mainExternalEndpoint)
    {
        // Proper hairpinning test using two sockets:
        // 1. Create a second socket (receiver) and get its external mapping
        // 2. Send a packet from the main socket to the receiver's external address
        // 3. If the receiver gets the packet, hairpinning is supported
        //
        // This tests: internal host A -> NAT external address of B -> internal host B

        using var receiverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        receiverSocket.Bind(new IPEndPoint(IPAddress.Any, 0));
        receiverSocket.ReceiveTimeout = 2000;

        try
        {
            // Get the external mapping for the receiver socket using the first STUN server
            var (host, port) = stunServers[0];
            var serverAddr = await ResolveHostAsync(host);
            var serverEndpoint = new IPEndPoint(serverAddr, port);

            var receiverExternal = await StunBindingRequest(receiverSocket, serverEndpoint);
            if (receiverExternal == null)
                return false;

            // Generate a unique test packet
            var testData = RandomNumberGenerator.GetBytes(16);

            // Start listening on receiver socket before sending
            var receiveTask = Task.Run(async () =>
            {
                var buffer = new byte[64];
                var cts = new CancellationTokenSource(2000);
                try
                {
                    var result = await receiverSocket.ReceiveFromAsync(
                        buffer,
                        SocketFlags.None,
                        new IPEndPoint(IPAddress.Any, 0),
                        cts.Token);

                    // Verify we received our test data
                    if (result.ReceivedBytes == testData.Length)
                    {
                        for (int i = 0; i < testData.Length; i++)
                        {
                            if (buffer[i] != testData[i])
                                return false;
                        }
                        return true;
                    }
                    return false;
                }
                catch
                {
                    return false;
                }
            });

            // Small delay to ensure receiver is listening
            await Task.Delay(50);

            // Send from main socket to receiver's external address
            await mainSocket.SendToAsync(testData, SocketFlags.None, receiverExternal);

            // Wait for result
            return await receiveTask;
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

        // Verify transaction ID matches (bytes 8-19)
        for (int i = 0; i < 12; i++)
        {
            if (response[8 + i] != transactionId[i])
                return null;
        }

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
