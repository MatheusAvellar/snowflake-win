using System;
using System.Collections.Generic;
using System.Text;
using System.Security.Cryptography;
using System.Net.Http;
using System.Threading.Tasks;
using SIPSorcery.Net;
using System.Text.Json;
using System.Net;
using System.Diagnostics;

namespace SnowflakeWin
{
    public class JSONOffer
    {
        public string? Status { get; set; }
        public string? Offer { get; set; }
    }

    internal class Util
    {

        public static readonly HttpClient httpClient = new HttpClient();
        private static HttpClient offerHttpClient = new HttpClient();

        internal static readonly char[] chars = "abcdefghijklmnopqrstuvwxyz1234567890".ToCharArray();
        public static string GenSnowflakeID() {
            // [Ref] https://stackoverflow.com/a/1344255/4824627
            const int size = 11;
            byte[] data = new byte[4 * size];
            using (var crypto = RandomNumberGenerator.Create()) {
                crypto.GetBytes(data);
            }
            StringBuilder result = new StringBuilder(size);
            for (int i = 0; i < size; i++) {
                var rnd = BitConverter.ToUInt32(data, i * 4);
                var idx = rnd % chars.Length;

                result.Append(chars[idx]);
            }
            return result.ToString();
        }

        // returns a promise that resolves to "restricted" if we
        // fail to make a test connection to a known restricted
        // NAT, "unrestricted" if the test connection succeeds, and
        // "unknown" if we fail to reach the probe test server
        public static Task<string> CheckNATType(int timeout) {
            Debug.WriteLine("@ Util.CheckNATType: Started check");
            UI.Log("Checking NAT type...");
            var promise = new TaskCompletionSource<string>();

            Task.Run(async delegate {
                Debug.WriteLine("@ Util.CheckNATType/Task: Started Task");

                var peerconn = new RTCPeerConnection(
                    new RTCConfiguration {
                        iceServers = new List<RTCIceServer> {
                            new RTCIceServer {
                                // FIXME: shouldn't this use Config's STUN servers?
                                urls = "stun:stun1.l.google.com:19302" //Config.pcConfigICEServer?
                            }
                        }
                    }
                );
                var channel = await peerconn.createDataChannel("NAT test");
                var closeEverything = () => {
                    if(peerconn != null) peerconn.close();
                    if(channel != null) channel.close();
                };

                bool open = false;
                channel.onopen += () => {
                    Debug.WriteLine("@ Util.CheckNATType/OnOpen: Channel opened; NAT type is 'unrestricted'");
                    open = true;
                    promise.SetResult("unrestricted");
                    closeEverything();
                };

                var onIceCandidate = (RTCIceCandidate? evt) => {
                    Debug.WriteLine($"@ Util.CheckNATType/OnICECandidate: ICE candidate {evt?.usernameFragment} // state={peerconn.iceGatheringState}");
                    
                    if (evt == null) {
                        // ice gathering is finished
                        Task<string> offerTask = Util.SendOffer(peerconn.localDescription);
                        offerTask.Wait();
                        var answer = offerTask.Result;
                        if (answer != null) {
                            // Wait `timeout` milliseconds to check if any channel is open
                            _ = new JSLikeTimeout(() => {
                                // If Task hasn't been completed/cancelled and we haven't opened any channel yet
                                if (!promise.Task.IsCompleted && !open) {
                                    // "Give up", NAT is restricted
                                    Debug.WriteLine("@ Util.CheckNATType/OnICECandidate: Timeout waiting for channel to open; NAT type is 'restricted'");
                                    promise.SetResult("restricted");
                                    closeEverything();
                                    return;
                                }
                            }, timeout);

                            bool parsed = RTCSessionDescriptionInit.TryParse(answer, out RTCSessionDescriptionInit remoteDesc);
                            if (parsed) {
                                Debug.WriteLine("@ Util.CheckNATType/OnICECandidate: Setting remote description");
                                peerconn.setRemoteDescription(remoteDesc);
                                return;
                            } else {
                                Debug.WriteLine("@ Util.CheckNATType/OnICECandidate: Error parsing answer");
                                promise.SetResult("unknown");
                                closeEverything();
                                return;
                            }
                        } else {
                            //Debug.WriteLine($"@ Util.CheckNATType/OnICECandidate: {ex}");
                            Debug.WriteLine("@ Util.CheckNATType/OnICECandidate: Error receiving probetest answer");
                            promise.SetResult("unknown");
                            closeEverything();
                            return;
                        }
                    }
                };

                peerconn.onicecandidate += onIceCandidate;

                peerconn.onicegatheringstatechange += (state) => {
                    Debug.WriteLine($"@ Util.CheckNATType/OnICEGatheringStateChange: {state}");
                    // C#'s PeerCoon doesn't trigger OnICECandidate with null automatically on completion
                    if (state == RTCIceGatheringState.complete)
                        onIceCandidate(null);
                };

                try {
                    Debug.WriteLine("@ Util.CheckNATType/Task: Creating offer, setting local description");
                    RTCSessionDescriptionInit desc = peerconn.createOffer();
                    await peerconn.setLocalDescription(desc).ConfigureAwait(false);
                    Debug.WriteLine($"@ Util.CheckNATType/Task: State={peerconn.connectionState}");
                } catch (Exception ex) {
                    Debug.WriteLine($"@ Util.CheckNATType/Task: Error! {ex.Message}");
                    promise.SetCanceled();
                    closeEverything();
                }
            });

            return promise.Task;
        }

        // Assumes getClientOffer happened, and a WebRTC SDP answer has been generated.
        // Sends it back to the broker, which passes it back to the original client.
        public static Task<string> SendOffer(RTCSessionDescription offer) {
            Debug.WriteLine("@ Util.SendOffer: Requested send offer");
            var promise = new TaskCompletionSource<string>();
            var task = promise.Task;

            Task.Run(async delegate {
                var RTCsdi = new RTCSessionDescriptionInit {
                    type = offer.type,
                    sdp = offer.sdp.ToString()
                };
                // var data = { "Status": "client match", "Offer": JSON.stringify(offer)};
                string payload = JsonSerializer.Serialize(new JSONOffer {
                    Status = "client match",
                    Offer = RTCsdi.toJSON()
                });
                Util.offerHttpClient.Timeout = TimeSpan.FromSeconds(30); //xhr.timeout = 30 * 1000;
                try {
                    Debug.WriteLine($"@ Util.SendOffer: Sending offer: '{payload}'");
                    var response = await Util.offerHttpClient.PostAsync(Config.PROBEURL, new StringContent(payload));
                    if (response.StatusCode == HttpStatusCode.OK) {
                        string content = await response.Content.ReadAsStringAsync();
                        Debug.WriteLine($"@ Util.SendOffer: Received response: '{content}'");
                        var json = JsonSerializer.Deserialize<Dictionary<string, string>>(content);
                        promise.SetResult(json["Answer"]); // Should contain offer.
                    } else {
                        Debug.WriteLine($"@ Util.SendOffer: Probe ERROR: Unexpected response '{response.StatusCode}' - {response.ReasonPhrase}");
                        Debug.WriteLine("@ Util.SendOffer: Failed to get answer from probe service");
                        promise.SetResult(null);
                    }
                } catch (Exception ex) {
                    Debug.WriteLine($"@ Util.SendOffer: Signaling Server: exception while connecting: {ex.Message}");
                    Debug.WriteLine("@ Util.SendOffer: unable to connect to signaling server");
                    promise.SetResult(null);
                }
            });

            return task;
        }
    }
}
