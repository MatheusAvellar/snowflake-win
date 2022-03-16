using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SnowflakeWin
{
    interface IRateLimit
    {
        bool Update(int a);

        double When();

        bool IsLimited();
    }

    internal class BucketRateLimit : IRateLimit
    {
        private int capacity { get; set; }
        private int time { get; set; }
        private long lastUpdate { get; set; }
        private double amount { get; set; }
        public BucketRateLimit(int capacity, int time) {
            this.capacity = capacity;
            this.time = time;
        }

        private void Age() {
            long now = DateTime.Now.Ticks;
            double delta = (now - this.lastUpdate) / 1000.0;
            this.lastUpdate = now;
            this.amount -= delta * this.capacity / (double)this.time;
            if (this.amount < 0.0)
                this.amount = 0.0;
        }

        public bool Update(int n) {
            this.Age();
            this.amount += n;
            return this.amount <= this.capacity;
        }

        // How many seconds in the future will the limit expire?
        public double When() {
            this.Age();
            return (this.amount - this.capacity) / (this.capacity / (double)this.time);
        }

        public bool IsLimited() {
            this.Age();
            return this.amount > this.capacity;
        }
    }

    internal class DummyRateLimit : IRateLimit
    {
        public DummyRateLimit(int a, int b) {}

        public bool Update(int n) {
            return true;
        }

        public double When() {
            return 0.0;
        }

        public bool IsLimited() {
            return false;
        }
    }
}
