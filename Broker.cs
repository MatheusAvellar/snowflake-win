using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.Net.Http;
using System.Net;
using SIPSorcery.Net;
using System.Diagnostics;

namespace SnowflakeWin
{
    public class BrokerJSONPayload
    {
        public string Version { get; set; }
        public string Sid { get; set; }
        public string Type { get; set; }
        public string NAT { get; set; }
        public int Clients { get; set; }
    }

    public class BrokerJSONAnswer
    {
        public string Version { get; set; }
        public string Sid { get; set; }
        public string Answer { get; set; }
    }

    internal class Broker
    {
        public readonly string BROKER_URL; // https://snowflake-broker.freehaven.net/

        public string proxyType { get; set; }

        private const string STATUS_MATCH = "client match";
        private const string STATUS_TIMEOUT = "no match";

        public Broker() {
            string burl = Config.brokerUrl;
            // Ensure url has the right protocol + trailing slash.
            if (burl.StartsWith("localhost"))
                burl = "http://" + burl;

            if (!burl.StartsWith("http"))
                burl = "https://" + burl;

            if (!burl.EndsWith('/'))
                burl += '/';

            this.BROKER_URL = burl;
        }

        // Promises some client SDP Offer.
        // Registers this Snowflake with the broker using an HTTP POST request, and
        // waits for a response containing some client offer that the Broker chooses
        // for this proxy..
        // TODO: Actually support multiple clients.
        public Task<Dictionary<string, string>> GetClientOffer(string id, int numClientsConnected) {
            var promise = new TaskCompletionSource<Dictionary<string, string>>();
            var task = promise.Task;

            Task.Run(async delegate {
                var JSONpayload = CreatePayload(id, numClientsConnected);
                string payload = JsonSerializer.Serialize(JSONpayload);
                Debug.WriteLine($"@ Broker.GetClientOffer:\n\tSending payload '{payload}'\n\tto '{this.BROKER_URL}proxy'...");
                HttpResponseMessage response = await Util.httpClient.PostAsync(this.BROKER_URL + "proxy", new StringContent(payload));

                if (response.StatusCode == HttpStatusCode.OK) {
                    string responseString = await response.Content.ReadAsStringAsync();
                    string printStr = (responseString.Length < 64) ? (responseString) : (responseString[..30] + "..." + responseString[^30..]);
                    Debug.WriteLine($"@ Broker.GetClientOffer: Received response: {printStr}");
                    var json = JsonSerializer.Deserialize<Dictionary<string, string>>(responseString);
                    if (json == null) {
                        Debug.WriteLine($"@ Broker.GetClientOffer: Empty or non-JSON response.");
                        return;
                    }

                    string? status = json["Status"]?.ToString();
                    if (status == STATUS_MATCH) {
                        Debug.WriteLine($"@ Broker.GetClientOffer: Success!");
                        promise.SetResult(json);
                        return;
                    } else if (status == STATUS_TIMEOUT) {
                        // { "Status": "no match", "Offer": "", "NAT": "" }
                        UI.Log("No clients");
                        Debug.WriteLine($"@ Broker.GetClientOffer: Timed out waiting for a client offer.");
                        promise.SetException(new AggregateException("Timed out"));
                        return;
                    } else {
                        Debug.WriteLine($"Broker ERROR: Unexpected status '{status}'");
                        promise.SetException(new AggregateException("Unexpected status"));
                        return;
                    }
                } else {
                    Debug.WriteLine($"Broker ERROR: Unexpected response '{response.StatusCode}' - {response.ReasonPhrase}");
                    promise.SetException(new AggregateException("Unexpected response"));
                    return;
                    //snowflake.ui.setStatus(' failure. Please refresh.');
                }
            });

            return task;
        }

        public BrokerJSONPayload CreatePayload(string id, int numClientsConnected) {
            int clients = (int)Math.Floor(Convert.ToDouble(numClientsConnected) / 8.0) * 8;

            return new BrokerJSONPayload {
                Version = "1.2",
                Sid = id,
                Type = Config.proxyType,
                NAT = Config.natType,
                Clients = clients,
            };
        }

        // Assumes getClientOffer happened, and a WebRTC SDP answer has been generated.
        // Sends it back to the broker, which passes it to back to the original client.
        public async void SendAnswer(string id, RTCSessionDescription answer) {
            Debug.WriteLine($"@ Broker.SendAnswer: Sending answer back to broker (ID='{id}')");
            Debug.WriteLine($"@ Broker.SendAnswer: Answer:\n{answer.sdp}");

            string payload = JsonSerializer.Serialize(new BrokerJSONAnswer {
                Version = "1.0",
                Sid = id,
                Answer = answer.sdp.ToString(),
            });

            var response = await Util.httpClient.PostAsync(this.BROKER_URL + "answer", new StringContent(payload));
            if (response.StatusCode == HttpStatusCode.OK) {
                string responseContent = await response.Content.ReadAsStringAsync();
                Debug.WriteLine($"@ Broker.SendAnswer: Successfully sent answer; got response='{responseContent}'");
            } else {
                Debug.WriteLine($"Broker ERROR: Unexpected response '{response.StatusCode}' - {response.ReasonPhrase}");
                UI.Log("Failure. Please refresh?");
            }
        }
    }
}
