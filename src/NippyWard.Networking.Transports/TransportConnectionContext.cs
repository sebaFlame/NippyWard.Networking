using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Runtime.CompilerServices;
using System.Diagnostics;

using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

using NippyWard.Networking.Logging;

namespace NippyWard.Networking.Transports
{
    //TODO: bandwidth throttling (with feature)
    public abstract class TransportConnectionContext
        : ConnectionContext,
            IMeasuredDuplexPipe,
            IConnectionIdFeature,
            IConnectionTransportFeature,
            IConnectionItemsFeature,
            IConnectionLifetimeFeature,
            IConnectionEndPointFeature,
            IDisposable
    {
        /// <summary>
        /// Connection for receiving data
        /// </summary>
        public abstract PipeReader Input { get; }

        /// <summary>
        /// Connection for sending data
        /// </summary>
        public abstract PipeWriter Output { get; }

        public abstract long BytesRead { get; }
        long IMeasuredDuplexPipe.TotalBytesReceived => this.BytesRead;

        public abstract long BytesSent { get; }
        long IMeasuredDuplexPipe.TotalBytesSent => this.BytesSent;

        public long SendSpeed
            => Interlocked.Read(ref this._sendSpeedInBytes);
        long IMeasuredDuplexPipe.BytesSentPerSecond => this.SendSpeed;

        public long ReceiveSpeed
            => Interlocked.Read(ref this._receiveSpeedInBytes);
        long IMeasuredDuplexPipe.BytesReceivedPerSecond => this.ReceiveSpeed;

        public override string ConnectionId { get; set; }

        //DO NOT USE INTERNALLY
        public override IDuplexPipe Transport { get; set; }
        public override IFeatureCollection Features { get; }
        public override IDictionary<object, object?> Items { get; set; }
        public override CancellationToken ConnectionClosed { get => this._connectionClosedTokenSource.Token; set => throw new NotSupportedException(); }

        protected readonly Pipe _sendToEndpoint;
        protected readonly Pipe _receiveFromEndpoint;
        protected readonly PipeScheduler _sendScheduler;
        protected readonly PipeScheduler _receiveScheduler;
        protected readonly ILogger? _logger;

        private readonly CancellationTokenSource _connectionClosedTokenSource;
        private Task _receiveTask;
        private Task _sendTask;

        //values used for speed calculation
        private long _previousTotalBytesSent;
        private long _previousTotalBytesReceived;
        private long _receiveSpeedInBytes;
        private long _sendSpeedInBytes;

        private static readonly Task _NotStartedTask;
        private static readonly System.Timers.Timer _BandwidthTimer;
        //delegates
        private static readonly Action<ThreadTaskWrapper> _DoSendTaskWrapper = DoSendAsync;
        private static readonly Action<object?> _DoSend = DoSendAsync;
        private static readonly Action<ThreadTaskWrapper> _DoReceiveTaskWrapper = DoReceiveAsync;
        private static readonly Action<object?> _DoReceive = DoReceiveAsync;

        static TransportConnectionContext()
        {
            _NotStartedTask = Task.FromException(new InvalidOperationException("Thread has not been started"));

            _BandwidthTimer = new System.Timers.Timer(1000);
            _BandwidthTimer.Start();
        }

        protected TransportConnectionContext
        (
            EndPoint localEndpoint,
            EndPoint remoteEndpoint,
            Pipe sendToEndpoint,
            Pipe receiveFromEndpoint,
            PipeScheduler sendScheduler,
            PipeScheduler receiveScheduler,
            IFeatureCollection? featureCollection = null,
            string? name = null,
            ILogger? logger = null
        )
        {
            this._sendToEndpoint = sendToEndpoint;
            this._receiveFromEndpoint = receiveFromEndpoint;

            this._sendScheduler = sendScheduler;
            this._receiveScheduler = receiveScheduler;

            this._logger = logger;

            if (featureCollection is null)
            {
                this.Features = new FeatureCollection();
            }
            else
            {
                this.Features = new FeatureCollection(featureCollection);
            }

            //set features
            this.Features.Set<IMeasuredDuplexPipe>(this);

            if (string.IsNullOrWhiteSpace(name))
            {
                name = this.GetType().Name;
            }
            this.ConnectionId = name.Trim();
            this.Features.Set<IConnectionIdFeature>(this);

            this.Transport = this;
            this.Features.Set<IConnectionTransportFeature>(this);

            this.Items = new ConnectionItems();
            this.Features.Set<IConnectionItemsFeature>(this);

            this._connectionClosedTokenSource = new CancellationTokenSource();
            this.Features.Set<IConnectionLifetimeFeature>(this);

            this.LocalEndPoint = localEndpoint;
            this.RemoteEndPoint = remoteEndpoint;
            this.Features.Set<IConnectionEndPointFeature>(this);

            _BandwidthTimer.Elapsed += this.OnBandwidthEvent;

            //initialize initial task state
            this._sendTask = _NotStartedTask;
            this._receiveTask = _NotStartedTask;
        }

        ~TransportConnectionContext()
        {
            this.Dispose(false);
        }

        private void OnBandwidthEvent(object? source, ElapsedEventArgs e)
        {
            long totalBytesSent = this.BytesSent;
            Interlocked.Exchange
            (
                ref this._sendSpeedInBytes,
                totalBytesSent - this._previousTotalBytesSent
            );
            //should be only thread writing here
            this._previousTotalBytesSent = totalBytesSent;

            long totalBytesReceived = this.BytesRead;
            Interlocked.Exchange
            (
                ref this._receiveSpeedInBytes,
                totalBytesReceived - this._previousTotalBytesReceived
            );
            //should be only thread writing here
            this._previousTotalBytesReceived = totalBytesSent;
        }

        public override void Abort(ConnectionAbortedException abortReason)
        {
            this.TraceLog("abort on ConnectionContext called");

            if(this._connectionClosedTokenSource.IsCancellationRequested)
            {
                return;
            }

            //complete if not completed yet, so write thread can end
            //this does not override existing completion
            try
            {
                this.Output.Complete(new ConnectionAbortedException(nameof(TransportConnectionContext)));
            }
            catch
            { }

            //complete if not completed yet, so read thread can end
            //this does not override existing completion
            try
            {
                this.Input.Complete(new ConnectionAbortedException(nameof(TransportConnectionContext)));
            }
            catch
            { }

            try
            {
                this._connectionClosedTokenSource.Cancel();
            }
            catch
            { }
        }

        /// <summary>
        /// This method needs to be called immediately after construction!
        /// This method blocks until threads have started!
        /// </summary>
        protected TransportConnectionContext InitializeSendReceiveTasks()
        {
            ThreadTaskWrapper receiveWrapper = new ThreadTaskWrapper(this, this._receiveScheduler);
            ThreadTaskWrapper sendWrapper = new ThreadTaskWrapper(this, this._sendScheduler);

            try
            {

                //ensure these are on a threadpool thread
                //this ensures all code gets executed on a thread and NOT in this method
                //so when an inline scheduler gets used, this method does not block
                //indefinitely
                ThreadPool.UnsafeQueueUserWorkItem(_DoSendTaskWrapper, sendWrapper, false);
                ThreadPool.UnsafeQueueUserWorkItem(_DoReceiveTaskWrapper, receiveWrapper, false);

                sendWrapper.WaitHandle.WaitOne();
                receiveWrapper.WaitHandle.WaitOne();

                this._receiveTask = receiveWrapper.Task!;
                this._sendTask = sendWrapper.Task!;
            }
            finally
            {
                receiveWrapper.Dispose();
                sendWrapper.Dispose();
            }

            return this;
        }

        private static void DoReceiveAsync(ThreadTaskWrapper wrapper)
            => wrapper.Scheduler.Schedule(_DoReceive, wrapper);

        //starts without existing executioncontext
        private static void DoReceiveAsync(object? state)
        {
            ThreadTaskWrapper wrapper = (ThreadTaskWrapper)state!;
            TransportConnectionContext ctx = wrapper.Ctx;
            wrapper.Signal(ctx.DoReceiveAsync());
        }
        
        private static void DoSendAsync(ThreadTaskWrapper wrapper)
            => wrapper.Scheduler.Schedule(_DoSend, wrapper);

        //starts without existing executioncontext
        private static void DoSendAsync(object? state)
        {
            ThreadTaskWrapper wrapper = (ThreadTaskWrapper)state!;
            TransportConnectionContext ctx = wrapper.Ctx;
            wrapper.Signal(ctx.DoSendAsync());
        }

        protected abstract Task DoReceiveAsync();
        protected abstract Task DoSendAsync();

        public Task AwaitSendTask() => this._sendTask;
        public Task AwaitReceiveTask() => this._receiveTask;

        public override async ValueTask DisposeAsync()
        {
            try
            {
                //ensure connection closed
                this._connectionClosedTokenSource.Cancel();
            }
            catch
            { }

            this.Dispose();

            this.TraceLog("awaiting Receive/Send thread");

            //ensure receive/send thread ended
            //and Pipes get completed
            //discard any errors (they will be logged in each thread)
            try
            {
                await Task.WhenAll(this._receiveTask, this._sendTask);
            }
            catch
            { }

            await base.DisposeAsync();
        }

        /// <summary>
        /// Release any resources held by this instance
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
        }

        protected abstract void DisposeCore(bool isDisposing);

        private void Dispose(bool isDisposing)
        {
            this.TraceLog("disposing");

            this.DisposeCore(isDisposing);

            //complete if not completed yet, so write thread can end
            //this does not override existing completion
            try
            {
                this.Output.Complete(new ObjectDisposedException(nameof(TransportConnectionContext)));
            }
            catch
            { }

            //complete if not completed yet, so read thread can end
            //this does not override existing completion
            try
            {
                this.Input.Complete(new ObjectDisposedException(nameof(TransportConnectionContext)));
            }
            catch
            { }

            try
            {
                //clean up cancellationtoken
                this._connectionClosedTokenSource.Dispose();
            }
            catch
            { }

            try
            {
                _BandwidthTimer.Elapsed -= this.OnBandwidthEvent;
            }
            catch
            { }

            if (!isDisposing)
            {
                return;
            }

            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Gets a string representation of this object
        /// </summary>
        public override string ToString() => this.ConnectionId;

        [Conditional("TRACELOG")]
        protected void TraceLog
        (
            string message,
            [CallerFilePath] string? file = null,
            [CallerMemberName] string? caller = null,
            [CallerLineNumber] int lineNumber = 0
        )
        {
#if TRACELOG
            this._logger?.TraceLog(this.ConnectionId, message, $"{System.IO.Path.GetFileName(file)}:{caller}#{lineNumber}");
#endif
        }
    }
}