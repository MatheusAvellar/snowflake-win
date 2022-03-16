using System;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Timers;
using System.Linq;
using System.Drawing;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Media.Imaging;

namespace SnowflakeWin
{
    internal class UI
    {
        public static MainWindow main;
        private static DateTime creationTime;
        private static Timer durationTimer;
        private static string[] snowflakeStates = { "Off", "On", "Active" };

        public static void Create(MainWindow main) {
            UI.main = main;
            UI.creationTime = DateTime.Now;
            // Create a timer and set a two second interval.
            UI.durationTimer = new Timer {
                Interval = 100,
                AutoReset = true,
                Enabled = true
            };
            UI.durationTimer.Elapsed += OnDurationTick;
        }

        public static void SetState(string state) {
            if (UI.IsWindowNull())
                return;
            
            // Change tray icon
            UI.main.Dispatcher.Invoke(() => {
                Icon ico = null;
                switch (state) {
                    case "Off":    ico = UI.main.iconOff;    break;
                    case "On":     ico = UI.main.iconOn;     break;
                    case "Active": ico = UI.main.iconActive; break;
                }
                if (ico != null) {
                    UI.main.SetIcon(ico);
                    UI.main.notifyIcon.Icon = ico;
                    UI.main.notifyIcon.Text = UI.main.trayTextFromStatus[state];
                }
            });
            if (UI.snowflakeStates.Contains(state)) {
                var logo = UI.main.snowflakeLogo;
                logo.Dispatcher.Invoke(() => { logo.Tag = state; });
            }
        }

        private static void OnDurationTick(object source, ElapsedEventArgs e) {
            if (UI.IsWindowNull())
                return;

            var now = DateTime.Now;
            TimeSpan interval = (TimeSpan)(now - creationTime);
            var str = new StringBuilder();
            if(interval.Days > 0)
                str.Append($"{interval.Days}d ");
            str.Append($"{Pad(interval.Hours)}:{Pad(interval.Minutes)}:{Pad(interval.Seconds)}");
            var durTB = UI.main.durationTextBlock;
            durTB.Dispatcher.Invoke(() => {
                durTB.Text = str.ToString();
            });
        }

        private static string Pad(int v) {
            return v.ToString().PadLeft(2, '0');
        }

        private static bool IsWindowNull() {
            return UI.main == null;
        }

        public static void Log(string message) {
            if (UI.IsWindowNull())
                return;

            Debug.WriteLine($"@ UI.Log: {message}");
            var log = UI.main.logTextBox;
            DateTime now = DateTime.Now;
            string time = $"{now.Hour.ToString().PadLeft(2, '0')}" +
                          $":{now.Minute.ToString().PadLeft(2, '0')}" +
                          $":{now.Second.ToString().PadLeft(2, '0')}";
            log.Dispatcher.Invoke(() => {
                log.Text += $"[{time}] {message}{Environment.NewLine}";
                log.ScrollToEnd();
            });
        }

        public static void SetID(string id) {
            if (UI.IsWindowNull())
                return;
            string outStr = id;
            if (id.Length > 0) {
                var formattedId = Regex.Replace(id, @"(.{5})(.{6})", "$1 $2");
                outStr = formattedId.ToUpper();
            }
            var idTB = UI.main.idTextBlock;
            idTB.Dispatcher.Invoke(() => {
                idTB.Text = outStr;
            });
        }
    }
}
