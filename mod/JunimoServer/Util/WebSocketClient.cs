using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Websocket.Client;

namespace JunimoServer.Util
{
    class WebSocketClient : IDisposable
    {
        private WebsocketClient _ws;

        //private readonly IMonitor _monitor;
        public WebSocketClient(string url)
        {
            //ManualResetEvent exitEvent = new ManualResetEvent(false);

            // TODO: Do we need secure protocol wss://?
            Uri uri = new Uri(url);

            var factory = new Func<ClientWebSocket>(() => new ClientWebSocket
            {
                Options =
                {
                    KeepAliveInterval = TimeSpan.FromSeconds(5),
                }
            });

            // Console.WriteLine("Setting up WS");
            _ws = new WebsocketClient(uri, factory);

            _ws.ErrorReconnectTimeout = TimeSpan.FromSeconds(3);
            //_ws.ReconnectTimeout = TimeSpan.FromSeconds(3);
            //_ws.LostReconnectTimeout = TimeSpan.FromSeconds(3); 

            _ws.ReconnectionHappened.Subscribe(msg => {
                // Console.WriteLine($"Reconnection happened, type: {msg.Type}");

                // var logData = new Dictionary<string, string>
                // {
                //     { "Type", msg.Type.ToString() },
                //     { "ToString", msg?.ToString() },
                // };

                // Console.WriteLine(Serialize(logData));
                // Console.WriteLine($"+++++++++++++++++++++++++++++++++++");
            });

            _ws.DisconnectionHappened.Subscribe(msg => {
                // Console.WriteLine($"Disconnection happened type: {msg.Type}");

                // var logData = new Dictionary<string, string>
                // {
                //     { "Type", msg.Type.ToString() },
                //     { "CloseStatus", msg.CloseStatus?.ToString() },
                //     { "CloseStatusDescription", msg.CloseStatusDescription?.ToString() },
                //     { "Exception", msg.Exception?.Message?.ToString() },
                // };

                // Console.WriteLine(Serialize(logData));

                // if (msg.Type == DisconnectionType.NoMessageReceived)
                // {
                //     Console.WriteLine("No message received, server probably starting up...");
                // }
                // else if (msg.Type == DisconnectionType.Error)
                // {
                //     Console.WriteLine("Error");
                // }

                //Console.WriteLine($"Retrying to start WS");
                //_ws.Start();

                // Console.WriteLine($"-----------------------------------");
            });

            _ws.MessageReceived.Subscribe(msg => Console.WriteLine($"Message received: {msg}"));

            // Console.WriteLine("Connecting to WS");
            _ws.Start();

            //Console.WriteLine("Waiting for WS exitEvent...");
            //exitEvent.WaitOne();
        }

        public Task Send(string message)
        {
            return Task.Run(() => _ws.Send(message));
        }

        public void Dispose()
        {
            _ws.Dispose();
        }

        private string Serialize(object data)
        {
            return Newtonsoft.Json.JsonConvert.SerializeObject(data, Newtonsoft.Json.Formatting.Indented);
        }

        private T Deserialize<T>(string data)
        {
            return Newtonsoft.Json.JsonConvert.DeserializeObject<T>(data);
        }

        //public async Task ClientWebSocket(string serverUri)
        //{
        //    _ws = new ClientWebSocket();
        //    await _ws.ConnectAsync(new Uri(serverUri), CancellationToken.None);
        //    Console.WriteLine("Connected to the server. Start sending messages...");
        //}

        //public async Task ConnectToServer(string serverUri)
        //{
        //    // Send messages to the server
        //    await Send("Hello, WebSocket!");

        //    // Receive messages from the server
        //    byte[] receiveBuffer = new byte[1024];
        //    while (_ws.State == WebSocketState.Open)
        //    {
        //        WebSocketReceiveResult result = await _ws.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), CancellationToken.None);
        //        if (result.MessageType == WebSocketMessageType.Text)
        //        {
        //            string receivedMessage = Encoding.UTF8.GetString(receiveBuffer, 0, result.Count);
        //            Console.WriteLine($"Received message from server: {receivedMessage}");
        //        }
        //    }
        //}

        //public async Task Send(string message)
        //{
        //    await _ws.SendAsync(
        //        new ArraySegment<byte>(Encoding.UTF8.GetBytes(message)),
        //        WebSocketMessageType.Text,
        //        true, 
        //        CancellationToken.None
        //    );
        //}
    }
}