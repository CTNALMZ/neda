using System;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.EngineIntegration.UnrealPlugin
{
    public class WebSocketServer
    {
        private readonly HttpListener _listener;
        private bool _isRunning = false;

        public WebSocketServer(string url = "http://localhost:8085/")
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(url);
        }

        public async Task StartAsync()
        {
            _listener.Start();
            _isRunning = true;

            Console.WriteLine("[NEDA-WS] WebSocket Server Başlatıldı → ws://localhost:8085/");

            while (_isRunning)
            {
                HttpListenerContext context = await _listener.GetContextAsync();

                if (context.Request.IsWebSocketRequest)
                {
                    HandleConnection(context);
                }
                else
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                }
            }
        }

        private async void HandleConnection(HttpListenerContext context)
        {
            Console.WriteLine("[NEDA-WS] Unreal Engine bağlantısı alındı.");

            WebSocketContext wsContext = await context.AcceptWebSocketAsync(null);
            WebSocket webSocket = wsContext.WebSocket;

            byte[] buffer = new byte[4096];

            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    CancellationToken.None
                );

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine("[NEDA-WS] Unreal bağlantısı kapandı.");
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "UE closed", CancellationToken.None);
                    break;
                }

                string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                Console.WriteLine("[UE → NEDA] " + message);

                string response = ProcessUnrealCommand(message);

                byte[] responseBytes = Encoding.UTF8.GetBytes(response);
                await webSocket.SendAsync(
                    new ArraySegment<byte>(responseBytes),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None
                );
            }
        }

        private string ProcessUnrealCommand(string msg)
        {
            // 🔥 Tamamen gerçek bir işlem pipeline’ına bağlayacağız

            if (msg == "ping")
                return "pong";

            if (msg.StartsWith("face_detected"))
            {
                // Güvenlik sistemini tetikleme
                return "{ \"status\": \"ok\", \"action\": \"trigger_security\" }";
            }

            if (msg.StartsWith("command:"))
            {
                string extracted = msg.Substring("command:".Length);
                return "{ \"neda_response\": \"" + extracted + "\" }";
            }

            return "{ \"status\": \"ok\", \"echo\": \"" + msg + "\" }";
        }
    }
}
