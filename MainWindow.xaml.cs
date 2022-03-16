using System;
using System.Windows;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;
using System.Reflection;
using System.ComponentModel;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace SnowflakeWin
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        Broker broker;
        Snowflake snowflake;
        Task<bool> probeTask;

        public readonly NotifyIcon notifyIcon;
        public readonly Icon iconOn = new Icon(Properties.Resources.toolbar_on, 32, 32);
        public readonly Icon iconOff = new Icon(Properties.Resources.toolbar_off, 32, 32);
        public readonly Icon iconActive = new Icon(Properties.Resources.toolbar_active, 32, 32);
        public readonly Dictionary<string, string> trayTextFromStatus = new Dictionary<string, string>() {
            { "Off", "Snowflake is off" },
            { "On", "Snowflake is waiting" },
            { "Active", "Snowflake is active" },
        };

        public MainWindow() {
            InitializeComponent();

            notifyIcon = new NotifyIcon { Visible = false };
            notifyIcon.Click += delegate (object sender, EventArgs args) {
                this.Show();
                this.WindowState = WindowState.Normal;
                notifyIcon.Visible = false;
            };
        }

        public void SetIcon(Icon icon) {
            ImageSource imageSource = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            this.Icon = imageSource;
        }

        protected override void OnStateChanged(EventArgs e) {
            if (WindowState == WindowState.Minimized) {
                notifyIcon.Visible = true;
                this.Hide();
            }

            base.OnStateChanged(e);
        }

        protected override void OnClosing(CancelEventArgs e) {
            notifyIcon.Visible = false;
            notifyIcon.Dispose();

            snowflake.Disable();

            base.OnClosing(e);
        }

        protected override void OnContentRendered(EventArgs e) {
            // Start things in non-UI thread
            Task.Run(() => Begin());
        }

        void Begin() {
            Debug.WriteLine("=============== start ===============");
            Config.Create("badge");
            UI.Create(this);
            UI.SetState("Off");
            UI.Log("Starting Snowflake");
            this.broker = new Broker();
            this.snowflake = new Snowflake(broker);
            Util.httpClient.DefaultRequestHeaders.Add("origin", "https://snowflake.torproject.org");

            var natTask = Util.CheckNATType(Config.datachannelTimeout);
            natTask.Wait();
            var type = natTask.Result;
            Console.WriteLine($"@ Main.ContentRendered: Setting NAT type: {type}");
            Config.natType = type;
            UI.Log($"NAT type set to '{type}'");

            this.probeTask = WS.ProbeWebSocket(Config.relayHost, Config.relayPort);
            this.probeTask.Wait();
            if (this.probeTask.Result) {
                Debug.WriteLine($"@ Main.ContentRendered: Contacting Broker at {broker.BROKER_URL}");
                Debug.WriteLine("@ Main.ContentRendered: Starting snowflake");
                snowflake.SetRelayAddr(Config.relayHost, Config.relayPort);
                snowflake.BeginWebRTC();
            } else {
                snowflake.Disable();
                Debug.WriteLine("@ Main.ContentRendered: Could not connect to bridge.");
            }
        }
    }
}
