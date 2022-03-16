using SIPSorcery.Net;
using System;
using System.Collections.Generic;
using System.Text;
using WebSocketSharp;
using System.Diagnostics;

namespace SnowflakeWin
{
    /*
    Represents a single:

       client <-- webrtc --> snowflake <-- websocket --> relay

    Every ProxyPair has a Snowflake ID, which is necessary when responding to the
    Broker with an WebRTC answer.
    */
    internal class ProxyPair
    {
        private ulong MAX_BUFFER = 10 * 1024 * 1024;
        public RTCPeerConnection peerconn;
        private RTCDataChannel client; // WebRTC Data channel
        private WebSocket relay; // websocket
        private JSLikeTimeout timer;
        private JSLikeTimeout messageTimer;
        private JSLikeTimeout flushTimeout;

        private string relayHost;
        private int relayPort;
        private IRateLimit rateLimit;
        private string relayLabel;
        private RTCConfiguration pcConfig;
        public readonly string id;

        private List<byte[]> c2rSchedule;
        private List<byte[]> r2cSchedule;

        public Action OnCleanup;

        /*
        Constructs a ProxyPair where:
        - @relayAddr is the destination relay
        - @rateLimit specifies a rate limit on traffic
        */
        public ProxyPair(string relayHost, int relayPort, IRateLimit rateLimit) {
            this.relayHost = relayHost;
            this.relayPort = relayPort;
            this.rateLimit = rateLimit;
            this.pcConfig = Config.pcConfig;

            this.id = Util.GenSnowflakeID();
            UI.SetID(this.id);

            this.c2rSchedule = new List<byte[]>();
            this.r2cSchedule = new List<byte[]>();
        }

        private bool shouldSendAnswer;
        private Broker broker;
        public void SendAnswerIfReady() {
            if (this.shouldSendAnswer) {
                Debug.WriteLine("@ ProxyPair.SendAnswerIfReady: Sending 'late' answer");
                this.broker.SendAnswer(this.id, this.peerconn.localDescription);
            }
        }
        public void Begin(Broker broker) {
            Debug.WriteLine("@ ProxyPair.Begin: Creating RTC peer connection");
            this.broker = broker;

            this.peerconn = new RTCPeerConnection(this.pcConfig); /*<-UI.Log("HEY FIX ME @ ProxyPair.Begin");*/
            var onIceCandidate = (RTCIceCandidate candidate) => {
                Debug.WriteLine($"@ ProxyPair.Begin/OnICECandidate: ICE candidate: '{candidate?.usernameFragment}'");
                if (candidate == null && this.peerconn.connectionState != RTCPeerConnectionState.closed) {
                    // TODO: Use a promise.all to tell Snowflake about all offers at once,
                    // once multiple proxypairs are supported.
                    Debug.WriteLine("@ ProxyPair.Begin/OnICECandidate: Finished gathering ICE candidates.");
                    if (this.peerconn.localDescription != null) {
                        this.shouldSendAnswer = false;
                        broker.SendAnswer(this.id, this.peerconn.localDescription);
                    } else {
                        this.shouldSendAnswer = true;
                    }
                }
            };
            this.peerconn.onicecandidate += onIceCandidate;

            peerconn.onicegatheringstatechange += (state) => {
                Debug.WriteLine($"@ ProxyPair.Begin/OnICEGatheringStateChange: {state}");
                // C#'s PeerCoon doesn't trigger OnICECandidate with null automatically on completion
                if (state == RTCIceGatheringState.complete)
                    onIceCandidate(null);
            };

            this.peerconn.onconnectionstatechange += (state) => {
                Debug.WriteLine($"@ ProxyPair.Begin/OCSC: Connection state changed to {state}");
            };
            this.peerconn.onsignalingstatechange += () => {
                Debug.WriteLine($"@ ProxyPair.Begin/OSSC: Signaling state changed to {this.peerconn.signalingState}");
            };
            this.peerconn.onnegotiationneeded += () => {
                Debug.WriteLine("@ ProxyPair.Begin/OnNegotiationNeeded: We need to negotiate!");
            };
            this.peerconn.OnTimeout += (mediaType) => {
                Debug.WriteLine($"@ ProxyPair.Begin/OnTimeout: Timeout on media type {mediaType}");
            };
            this.peerconn.oniceconnectionstatechange += e => {
                Debug.WriteLine($"@ ProxyPair.Begin/OnICECSC: ICE connection state changed to {this.peerconn.iceConnectionState}");
            };

            // OnDataChannel triggered remotely from the client when connection succeeds.
            this.peerconn.ondatachannel += (dataChannel) => {
                Debug.WriteLine("@ ProxyPair.Begin/OnDataChannel: Data Channel established...");
                this.PrepareDataChannel(dataChannel);
                this.client = dataChannel;
            };
        }

        public bool ReceiveWebRTCOffer(RTCSessionDescriptionInit offer) {
            if (offer.type != RTCSdpType.offer) {
                Debug.WriteLine("@ ProxyPair.ReceiveWebRTCOffer: Invalid SDP received -- was not an offer.");
                return false;
            }
            Debug.WriteLine($"@ ProxyPair.ReceiveWebRTCOffer: SDP type={offer.type} successfully received.");
            try {
                var res = this.peerconn.setRemoteDescription(offer);
                Debug.WriteLine($"@ ProxyPair.ReceiveWebRTCOffer: Set remote description; result={res}");
                return true;
            } catch (Exception) {
                Debug.WriteLine("@ ProxyPair.ReceiveWebRTCOffer: Invalid SDP message.");
                return false;
            }
        }

        public void PrepareDataChannel(RTCDataChannel dataChannel) {
            dataChannel.onopen += delegate {
                Debug.WriteLine("@ ProxyPair.PrepareDataChannel/OnOpen: WebRTC DataChannel opened!");
                UI.SetState("Active");
                // This is the point when the WebRTC datachannel is done, so the next step
                // is to establish websocket to the server.
                this.ConnectRelay();
            };
            dataChannel.onclose += delegate {
                Debug.WriteLine("@ ProxyPair.PrepareDataChannel/OnClose: WebRTC DataChannel closed.");
                UI.SetState("On");
                UI.Log("Disconnected from WebRTC");
                this.Flush();
                this.Close();
            };
            dataChannel.onerror += delegate {
                Debug.WriteLine("@ ProxyPair.PrepareDataChannel/OnError: Data channel error!");
            };
            dataChannel.binaryType = "arraybuffer";
            dataChannel.onmessage += this.OnClientToRelayMessage;
        }

        // Assumes WebRTC datachannel is connected.
        public void ConnectRelay() {
            Debug.WriteLine("@ ProxyPair.ConnectRelay: Connecting to relay...");
            // Get a remote IP address from the PeerConnection, if possible. Add it to
            // the WebSocket URL's query string if available.
            // MDN marks remoteDescription as "experimental". However the other two
            // options, currentRemoteDescription and pendingRemoteDescription, which
            // are not marked experimental, were undefined when I tried them in Firefox
            // 52.2.0.
            // https://developer.mozilla.org/en-US/docs/Web/API/RTCPeerConnection/remoteDescription
            var remoteDesc = this.peerconn.remoteDescription;

            // TODO: might not be .AddressOrHost. Original line was:
            // peer_ip = Parse.ipFromSDP((ref = this.pc.remoteDescription) != null ? ref.sdp : void 0);
            var peer_ip = (remoteDesc != null ? remoteDesc.sdp : null)?.AddressOrHost;

            List<string[]> param = new List<string[]>();
            if (peer_ip != null) {
                param.Add(new string[] { "client_ip", peer_ip });
            }
            this.relay = WS.MakeWebSocket(this.relayHost, this.relayPort, param);
            this.relayLabel = "websocket-relay";
            this.relay.OnOpen += delegate {
                if (this.timer != null) {
                    this.timer.Cancel();
                    this.timer = null;
                }
                Debug.WriteLine($"@ ProxyPair.ConnectRelay: {this.relayLabel} connected!");
                UI.Log("Connected");
            };
            this.relay.OnClose += OnRelayClose;
            this.relay.OnError += this.OnRelayError;
            this.relay.OnMessage += this.OnRelayToClientMessage;

            // TODO: Better websocket timeout handling.
            this.timer = new JSLikeTimeout(() => {
                if (this.timer == null)
                    return;

                Debug.WriteLine($"@ ProxyPair.ConnectRelay: {this.relayLabel} timed out connecting.");
                this.OnRelayClose();
            }, 5000);
        }

        private void OnRelayToClientMessage(object sender, MessageEventArgs evt) {
            Debug.WriteLine($"@ ProxyPair.OnRelayToClientMessage: websocket --> WebRTC data: {evt.Data.Length} bytes^");
            this.r2cSchedule.Add(Encoding.UTF8.GetBytes(evt.Data));
            this.Flush();
        }

        public void OnRelayClose(object? sender = null, EventArgs? e = null) {
            Debug.WriteLine($"@ ProxyPair.OnRelayClose: {this.relayLabel} closed.");
            UI.Log("Disconnected");
            UI.SetState("On");
            this.Flush();
            this.Close();
        }
        public void OnRelayError(object sender, ErrorEventArgs evt) {
            // ??? FIXME?
            //var ws = evt.Target;
            //Debug.WriteLine(ws.label + ' error.');
            Debug.WriteLine("@ ProxyPair.OnRelayError: UH OH");
            this.Close();
        }

        public void Flush() {
            Debug.WriteLine("@ ProxyPair.Flush");
            if (this.flushTimeout != null) {
                this.flushTimeout.Cancel();
            }
            this.flushTimeout = null;
            bool busy = true;
            while (busy && !this.rateLimit.IsLimited()) {
                // Check chunks
                busy = false;
                // WebRTC --> websocket
                // FIXME: WebSocketSharp doesn't have a .bufferedAmount equivalent :(
                Debug.WriteLine("@ ProxyPair.Flush: FIXME");
                if (this.IsRelayReady() /*&& this.relay.bufferedAmount < this.MAX_BUFFER*/ && this.c2rSchedule.Count > 0) {
                    var chunk = this.c2rSchedule[0];
                    this.c2rSchedule.RemoveAt(0);
                    this.rateLimit.Update(chunk.Length);
                    this.relay.Send(chunk);
                    busy = true;
                }
                // websocket --> WebRTC
                if (this.IsWebRTCReady() && this.client.bufferedAmount < this.MAX_BUFFER && this.r2cSchedule.Count > 0) {
                    var chunk = this.r2cSchedule[0];
                    this.r2cSchedule.RemoveAt(0);
                    this.rateLimit.Update(chunk.Length);
                    this.client.send(chunk);
                    busy = true;
                }
            }
            if (this.r2cSchedule.Count > 0
                || this.c2rSchedule.Count > 0
                //|| (this.IsRelayReady() && this.relay.bufferedAmount > 0) // FIXME: .bufferedAmount thing
                || (this.IsWebRTCReady() && this.client.bufferedAmount > 0)) {
                this.flushTimeout = new JSLikeTimeout(this.Flush, (int)(this.rateLimit.When() * 1000));
            }
        }
        public void Close() {
            if (this.timer != null) {
                this.timer.Cancel();
                this.timer = null;
            }
            if (this.messageTimer != null) {
                this.messageTimer.Cancel();
                this.messageTimer = null;
            }
            if (this.IsWebRTCReady()) {
                this.client.close();
            }
            if (this.IsPeerConnOpen()) {
                this.peerconn.close();
            }
            if (this.IsRelayReady()) {
                this.relay.Close();
            }
            this.OnCleanup();
        }

        public bool IsWebRTCReady() {
            Debug.WriteLine($"@ ProxyPair.IsWebRTCReady: IsClientNull={(this.client == null)}; ReadyState={this.client?.readyState}");
            return (this.client != null) && (this.client.readyState == RTCDataChannelState.open);
        }
        public bool IsRelayReady() {
            return (this.relay != null) && (this.relay.ReadyState == WebSocketState.Open);
        }
        public bool IsPeerConnOpen() {
            return (this.peerconn != null) && (this.peerconn.connectionState != RTCPeerConnectionState.closed);
        }

        private void OnClientToRelayMessage(RTCDataChannel dc, DataChannelPayloadProtocols protocol, byte[] data) {
            if (this.messageTimer != null) {
                this.messageTimer.Cancel();
            }

            // WebRTC --> websocket
            Debug.WriteLine($"@ ProxyPair.OnClientToRelayMessage: WebRTC --> websocket data: {data.Length} bytes^");
            this.c2rSchedule.Add(data);

            // if we don't receive any keep-alive messages from the client, close the
            // connection
            this.messageTimer = new JSLikeTimeout((() => {
                Debug.WriteLine("@ ProxyPair.OnClientToRelayMessage: Closing stale connection.");
                this.Flush();
                this.Close();
            }), Config.messageTimeout);

            this.Flush();
        }
    }
}