using SIPSorcery.Net;
using System.Collections.Generic;

namespace SnowflakeWin
{
    internal class Config
    {
        public static string brokerUrl = "snowflake-broker.freehaven.net";
        public static string natType = "unknown";

        public static string relayHost = "snowflake.freehaven.net";
        public static int relayPort = 443;

        public static string cookieName = "snowflake-allow";

        // Bytes per second. Set to undefined to disable limit.
        public static int rateLimitBytes = -1;
        public static int minRateLimit = 10 * 1024;
        public static int rateLimitHistory = 5;

        public static int defaultBrokerPollInterval = 60 * 1000; // 1 poll every minute
        public static double slowestBrokerPollInterval = 6 * 60 * 60.0 * 1000; //1 poll every 6 hours
        public static double pollAdjustment = 100.0 * 1000;

        // Recheck our NAT type once every 2 days
        public static int natCheckInterval = 2 * 24 * 60 * 60 * 1000;

        // Timeout for client offer
        public static int clientOfferTimeout = 60 * 1000;

        // Timeout after sending answer before datachannel is opened
        public static int datachannelTimeout = 20 * 1000;

        // Timeout to close proxypair if no messages are sent
        public static int messageTimeout = 30 * 1000;

        public static int maxNumClients = 1;

        public static string proxyType = "";

        // TODO: Different ICE servers.
        public static string pcConfigICEServer = "stun:stun.l.google.com:19302";
        public static RTCConfiguration pcConfig = new RTCConfiguration {
            iceServers = new List<RTCIceServer> {
                    new RTCIceServer {
                        urls = Config.pcConfigICEServer
                    }
                }
        };

        public const string PROBEURL = "https://snowflake-broker.freehaven.net:8443/probe";

        public static void Create(string proxyType) {
            Config.proxyType = proxyType;
        }
    }
}
