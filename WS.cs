using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WebSocketSharp;
using System.Diagnostics;
using System.Net.Sockets;

namespace SnowflakeWin
{
    internal class WS {

        private static readonly Dictionary<string, int> DEFAULT_PORTS = new() {
            { "http", 80 },
            { "https", 443 }
        };

        private static readonly bool WSS_ENABLED = true;

        // Build an escaped URL string from unescaped components. Only scheme and host
        // are required. See RFC 3986, section 3.
        public static string BuildUrl(string scheme, string host, int port, string path, List<string[]>? param = null) {
            var parts = new StringBuilder();
            parts.Append(Uri.EscapeDataString(scheme));
            parts.Append("://");

            // If it contains a colon but no square brackets, treat it as IPv6.
            var colon = new Regex(@"/:/");
            var sqbrck = new Regex(@"/[\[\]]/");
            if (colon.IsMatch(host) && !sqbrck.IsMatch(host)) {
                parts.Append('[');
                parts.Append(host);
                parts.Append(']');
            } else {
                parts.Append(Uri.EscapeDataString(host));
            }

            if (!DEFAULT_PORTS.ContainsKey(scheme) || DEFAULT_PORTS[scheme] != port) {
                parts.Append(':');
                parts.Append(Uri.EscapeDataString(port.ToString()));
            }
            if (path.Length > 0) {
                if (!path.StartsWith('/')) {
                    path = '/' + path;
                }
                var escaper = new Regex(@"[^/]");
                string escapedPath = escaper.Replace(path, match => Uri.EscapeDataString(match.Value));
                if (!path.Equals(escapedPath))
                    Debug.WriteLine($"'{path}' -> '{escapedPath}'");
                parts.Append(escapedPath);
            }
            if (param != null) {
                parts.Append('?');
                foreach (var p in param) {
                    parts.Append(Uri.EscapeDataString(p[0]));
                    parts.Append('=');
                    parts.Append(Uri.EscapeDataString(p[1]));
                    parts.Append('&');
                }
                // Remove trailing '&', or trailing '?' in case param list is empty
                parts.Length--;
            }
            Debug.WriteLine($"@ WS.BuildUrl: URL is '{parts}'");
            return parts.ToString();
        }

        public static WebSocket MakeWebSocket(string host, int port, List<string[]>? param = null) {
            string wsProtocol = WS.WSS_ENABLED ? "wss" : "ws";
            string url = WS.BuildUrl(wsProtocol, host, port, "/", param);
            return new WebSocket(url);
        }

        public static Task<bool> ProbeWebSocket(string host, int port) {
            // [Ref] https://stackoverflow.com/a/38998516/4824627
            var promise = new TaskCompletionSource<bool>();

            WebSocket ws = WS.MakeWebSocket(host, port);
            Debug.WriteLine($"@ WS.ProbeWebSocket: Probing WebSocket at '{ws.Url}'");
            UI.Log("Probing WebSocket...");
            ws.OnOpen += delegate {
                Debug.WriteLine("@ WS.ProbeWebSocket/OnOpen: WebSocket is accepting connections!");
                UI.Log("WebSocket accepted connection!");
                try {
                    Debug.WriteLine($"@ WS.ProbeWebSocket/OnOpen: ReadyState={ws.ReadyState}");
                    var shouldClose = promise.TrySetResult(true);
                    if (shouldClose) {
                        Debug.WriteLine("@ WS.ProbeWebSocket/OnOpen: Attempting to close WebSocket");
                        ws.Close();
                    }
                } catch (Exception ex) {
                    Debug.WriteLine($"@ WS.ProbeWebSocket/OnOpen: Error! {ex.Message}");
                }
            };
            ws.OnError += delegate {
                Debug.WriteLine("@ WS.ProbeWebSocket/OnError: WebSocket connection error :(");
                UI.Log("WebSocket connection error");
                try {
                    Debug.WriteLine($"@ WS.ProbeWebSocket/OnError: ReadyState={ws.ReadyState}");
                    var shouldClose = promise.TrySetResult(false);
                    if (shouldClose) {
                        Debug.WriteLine("@ WS.ProbeWebSocket/OnError: Attempting to close WebSocket");
                        ws.Close();
                    }
                } catch (Exception ex) {
                    Debug.WriteLine($"@ WS.ProbeWebSocket/OnError: Error! {ex.Message}");
                }
            };
            ws.Connect();

            return promise.Task;
        }
    }
}
