using System;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace SnowflakeWin
{
    internal class JSLikeTimeout
    {
        public readonly int ms;
        public readonly Action function;
        public readonly Task task;
        private CancellationTokenSource cancelTokenSrc;

        public JSLikeTimeout(Action action, int ms) {
            this.function = action;
            this.ms = ms;
            // Create a cancellation token in case we want to "clearTimeout()"
            this.cancelTokenSrc = new CancellationTokenSource();
            // Start the timeout
            this.task = Task.Run(async delegate {
                Debug.WriteLine($"Scheduled task for {ms}ms");
                // Wait specified time
                await Task.Delay(ms);
                // Check if a "clearTimeout()" has been called
                if (!this.cancelTokenSrc.IsCancellationRequested) {
                    // If not, run scheduled "function"
                    action();
                }
            }, this.cancelTokenSrc.Token);
        }

        // "clearTimeout()"
        public void Cancel() {
            this.cancelTokenSrc.Cancel();
        }
    }
}
