using SIPSorcery.Net;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Diagnostics;

namespace SnowflakeWin
{
    internal class Snowflake
    {
        private Broker broker;
        private int natFailures = 0;
        private int pollInterval = 0;
        private JSLikeTimeout pollTimeout;
        private IRateLimit rateLimit;
        private int retries = 0;

        private List<ProxyPair> proxyPairs;

        private string relayHost;
        private int relayPort;

        public Snowflake(Broker broker) {
            this.broker = broker;

            this.proxyPairs = new List<ProxyPair>();
            this.pollInterval = Config.defaultBrokerPollInterval;

            if (Config.rateLimitBytes == 0) {
                this.rateLimit = new DummyRateLimit(0, 0);
            } else {
                this.rateLimit = new BucketRateLimit(Config.rateLimitBytes * Config.rateLimitHistory, Config.rateLimitHistory);
            }

            this.retries = 0;
        }

        // Set the target relay address spec, which is expected to be websocket.
        // TODO: Should potentially fetch the target from broker later, or modify
        // entirely for the Tor-independent version.
        public bool SetRelayAddr(string host, int port) {
            this.relayHost = host;
            this.relayPort = port;
            Debug.WriteLine($"@ Snowflake.SetRelayAddr: Using {host}:{port} as Relay.");
            return true;
        }

        // Initialize WebRTC PeerConnection, which requires beginning the signalling
        // process. |pollBroker| automatically arranges signalling.
        public void BeginWebRTC() {
            UI.SetState("On");
            Debug.WriteLine($"@ Snowflake.BeginWebRTC: Polling broker every {this.pollInterval}ms");
            this.PollBroker();
            this.pollTimeout = new JSLikeTimeout((() => {
                this.BeginWebRTC();
            }), this.pollInterval);
        }

        public void PollBroker() {
            // Poll broker for clients.
            ProxyPair? pair = MakeProxyPair(this.broker);
            if (pair == null) {
                Debug.WriteLine("@ Snowflake.PollBroker: At client capacity.");
                return;
            }
            Debug.WriteLine("@ Snowflake.PollBroker: Polling broker...");
            // Do nothing until a new ProxyPair is available.
            string msg = "@ Snowflake.PollBroker: Waiting for client offer";
            if (this.retries > 0) {
                msg += $" [retries: {this.retries}]";
                UI.Log($"Requesting client [retries: {this.retries}]");
            } else {
                UI.Log("Requesting client");
            }
           //this.ui.setStatus(msg);
            Debug.WriteLine(msg);
            // Update NAT type
            Debug.WriteLine($"@ Snowflake.PollBroker: NAT type: {Config.natType}");
            var clientOfferTask = this.broker.GetClientOffer(pair.id, this.proxyPairs.Count);
            try {
                clientOfferTask.Wait(Config.clientOfferTimeout);
                Debug.WriteLine("@ Snowflake.PollBroker: GetClientOffer ended with no errors");
            } catch (Exception ex) {
                //on error, close proxy pair
                if (ex is AggregateException)
                    Debug.WriteLine($"@ Snowflake.PollBroker: Closing proxy pair");
                else
                    Debug.WriteLine($"@ Snowflake.PollBroker: Error, closing proxy pair\n\t{ex.Message}");
                pair.Close();
                pair = null;
            }

            this.retries++;

            // .then()
            if (pair != null && !clientOfferTask.IsCanceled && !clientOfferTask.IsFaulted) {
                var json = clientOfferTask.Result;
                if (!this.ReceiveOffer(pair, json["Offer"])) {
                    Debug.WriteLine("@ Snowflake.PollBroker: Closing ProxyPair connection");
                    pair.Close();
                    return;
                }

                var clientNAT = json["NAT"];
                //set a timeout for channel creation
                Debug.WriteLine("@ Snowflake.PollBroker: Starting timeout for data channel creation");
                UI.Log("Waiting for data channel to open...");
                JSLikeTimeout t = new JSLikeTimeout((() => {
                    if (!pair.IsWebRTCReady()) {
                        UI.Log("Data channel didn't open :(");
                        Debug.WriteLine("@ Snowflake.PollBroker: ProxyPair data channel timed out waiting for open");
                        pair.Close();
                        // increase poll interval
                        this.pollInterval = (int)Math.Min(
                            this.pollInterval + Config.pollAdjustment,
                            Config.slowestBrokerPollInterval
                            );
                        Debug.WriteLine($"@ Snowflake.PollBroker: Poll interval increased to {this.pollInterval}");
                        if (clientNAT == "restricted") {
                            this.natFailures++;
                        }
                        // if we fail to connect to a restricted client 3 times in
                        // a row, assume we have a restricted NAT
                        if (this.natFailures >= 3) {
                            //this.ui.natType = "restricted";
                            Config.natType = "restricted";
                            Debug.WriteLine("@ Snowflake.PollBroker: Learned NAT type: restricted");
                            this.natFailures = 0;
                        }
                        //this.broker.setNATType(this.ui.natType);
                    } else {
                        // decrease poll interval
                        this.pollInterval = (int)Math.Max(
                            this.pollInterval - Config.pollAdjustment,
                            Config.defaultBrokerPollInterval
                        );
                        Debug.WriteLine($"@ Snowflake.PollBroker: Poll interval decreased to {this.pollInterval}");
                        this.natFailures = 0;
                    }
                }), Config.datachannelTimeout);
            }
        }

        // Receive an SDP offer from some client assigned by the Broker,
        // |pair| - an available ProxyPair.
        private bool ReceiveOffer(ProxyPair pair, string desc) {
            try {
                var offer = JsonSerializer.Deserialize<Dictionary<string, string>>(desc);
                // { "type": "offer", "sdp": "..." }
                if (offer.ContainsKey("sdp")) {
                    Debug.WriteLine($"@ Snowflake.ReceiveOffer: Received:\n\n{offer["sdp"]}\n");
                    var RTCsdi = new RTCSessionDescriptionInit {
                        type = offer["type"] == "offer" ? RTCSdpType.offer : 0,
                        sdp = offer["sdp"]
                    };
                    if (pair.ReceiveWebRTCOffer(RTCsdi)) {
                        Debug.WriteLine("@ Snowflake.ReceiveOffer: Sending answer");
                        this.SendAnswer(pair);
                        return true;
                    } else {
                        Debug.WriteLine("@ Snowflake.ReceiveOffer: Offer not successfully received");
                        return false;
                    }
                } else {
                    Debug.WriteLine("@ Snowflake.ReceiveOffer: No SDP in JSON :(");
                    return false;
                }
            } catch (Exception e) {
                Debug.WriteLine($"@ Snowflake.ReceiveOffer: ERROR: Unable to receive Offer: {e.Message}");
                return false;
            }
        }

        public void SendAnswer(ProxyPair pair) {
            RTCSessionDescriptionInit RTCsdi = pair.peerconn.createAnswer();
            Debug.WriteLine("@ Snowflake.SendAnswer: WebRTC answer ready");
            try {
                var t = pair.peerconn.setLocalDescription(RTCsdi);
                t.Wait();
                Debug.WriteLine($"@ Snowflake.SendAnswer: Set local description to '{RTCsdi.toJSON()}'");
                pair.SendAnswerIfReady(); // FIXME: why are we sending answer before setting local desc?
            } catch (Exception) {
                pair.Close();
                Debug.WriteLine("@ Snowflake.SendAnswer: webrtc: Failed to create or set Answer");
            }
        }

        public ProxyPair MakeProxyPair(Broker broker) {
            if (this.proxyPairs.Count >= Config.maxNumClients)
                return null;

            var pair = new ProxyPair(this.relayHost, this.relayPort, this.rateLimit);
            this.proxyPairs.Add(pair);

            string ids = string.Join(" | ", proxyPairs.ConvertAll(pp => pp.id).ToArray());
            Debug.WriteLine($"@ Snowflake.MakeProxyPair: Snowflake IDs: {ids}");

            pair.OnCleanup = delegate {
                // Delete from the list of proxy pairs.
                this.proxyPairs.Remove(pair);
                // Clear ID from UI
                UI.SetID(""); // TODO: change this when there's multiple pairs
            };
            pair.Begin(broker);
            return pair;
        }

        // Stop all proxypairs.
        public void Disable() {
            Debug.WriteLine("@ Snowflake.Disable: Disabling Snowflake.");
            if (this.pollTimeout != null){
                this.pollTimeout.Cancel();
                this.pollTimeout = null;
            }

            while (this.proxyPairs.Count > 0) {
                // Pop()
                ProxyPair pair = this.proxyPairs.Last();
                this.proxyPairs.RemoveAt(proxyPairs.Count - 1);
                // Close()
                pair.Close();
            }
        }
    }
}
