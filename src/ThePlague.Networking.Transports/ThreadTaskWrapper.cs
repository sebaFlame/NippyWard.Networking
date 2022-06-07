using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

using System.IO.Pipelines;

namespace ThePlague.Networking.Transports
{
    internal class ThreadTaskWrapper : IDisposable
    {
        internal TransportConnectionContext Ctx { get; private set; }
        internal PipeScheduler Scheduler { get; private set; }
        internal Task? Task { get; private set; }
        internal WaitHandle WaitHandle => this._evt.WaitHandle;

        private ManualResetEventSlim _evt;

        public ThreadTaskWrapper
        (
            TransportConnectionContext ctx,
            PipeScheduler scheduler
        )
        {
            this.Ctx = ctx;
            this.Scheduler = scheduler;
            this._evt = new ManualResetEventSlim();
        }

        public void Signal(Task t)
        {
            this.Task = t;
            this._evt.Set();
        }

        public void Dispose()
        {
            this._evt.Dispose();
        }
    }
}
