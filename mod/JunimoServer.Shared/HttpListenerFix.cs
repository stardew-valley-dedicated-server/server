using System;
using System.Net;
using System.Reflection;
using HarmonyLib;
using StardewModdingAPI;

namespace JunimoServer.Shared;

/// <summary>
/// Fixes dotnet/runtime#28658: race condition in HttpEndPointListener on .NET 6 (Linux).
///
/// The bug: HttpEndPointListener's constructor calls Accept(args), which starts async
/// socket acceptance, BEFORE initializing _unregisteredConnections. If AcceptAsync
/// completes synchronously (a connection is already pending in the kernel backlog),
/// ProcessAccept runs immediately and does lock(_unregisteredConnections) on a null field,
/// throwing ArgumentNullException in Monitor.ReliableEnter.
///
/// This is unfixed in all .NET 6.x releases (verified through v6.0.36). Microsoft considers
/// HttpListener on Linux deprecated in favor of Kestrel (dotnet/runtime#63941).
///
/// Fix: Harmony prefix on the constructor that initializes _unregisteredConnections BEFORE
/// Accept() is called. We patch the constructor because the JIT may inline ProcessAccept
/// into Accept, bypassing any patch on ProcessAccept itself.
/// </summary>
public static class HttpListenerFix
{
    private static FieldInfo? _unregisteredConnectionsField;
    private static Func<object>? _createDictionary;
    private static IMonitor? _monitor;

    public static void Apply(Harmony harmony, IMonitor monitor)
    {
        _monitor = monitor;

        // HttpEndPointListener is internal; find it via the assembly
        var httpListenerAssembly = typeof(HttpListener).Assembly;
        var endPointListenerType = httpListenerAssembly.GetType("System.Net.HttpEndPointListener");

        if (endPointListenerType == null)
        {
            monitor.Log(
                "HttpListenerFix: HttpEndPointListener type not found, patch not needed (non-Linux?)",
                LogLevel.Trace
            );
            return;
        }

        _unregisteredConnectionsField = AccessTools.Field(
            endPointListenerType,
            "_unregisteredConnections"
        );
        if (_unregisteredConnectionsField == null)
        {
            monitor.Log(
                "HttpListenerFix: _unregisteredConnections field not found. Skipping patch.",
                LogLevel.Warn
            );
            return;
        }

        // Build a factory for the correct Dictionary<HttpConnection, HttpConnection> type.
        // HttpConnection is internal, so we must construct the generic type via reflection.
        var fieldType = _unregisteredConnectionsField.FieldType;
        _createDictionary = () => Activator.CreateInstance(fieldType)!;

        // Patch the constructor. This runs before Accept() is called, so we can
        // pre-initialize _unregisteredConnections before any async callback fires.
        // We can't patch ProcessAccept because the JIT may inline it into Accept.
        var ctor = AccessTools.Constructor(
            endPointListenerType,
            new[] { typeof(HttpListener), typeof(System.Net.IPAddress), typeof(int), typeof(bool) }
        );
        if (ctor == null)
        {
            monitor.Log("HttpListenerFix: constructor not found. Skipping patch.", LogLevel.Warn);
            return;
        }

        harmony.Patch(
            original: ctor,
            prefix: new HarmonyMethod(typeof(HttpListenerFix), nameof(Constructor_Prefix))
        );

        monitor.Log(
            "HttpListenerFix: patched HttpEndPointListener constructor (dotnet/runtime#28658)",
            LogLevel.Info
        );
    }

    /// <summary>
    /// PREFIX on constructor: pre-initializes _unregisteredConnections so it's never null
    /// when ProcessAccept runs. The constructor will overwrite it with its own instance
    /// on the last line, which is fine. This just closes the race window.
    /// </summary>
    private static void Constructor_Prefix(object __instance)
    {
        _unregisteredConnectionsField!.SetValue(__instance, _createDictionary!());
    }
}
