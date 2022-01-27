using System;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ThePlague.Networking.Sockets
{
    // This type is largely similar to the type of the same name in KestrelHttpServer, with some minor tweaks:
    // - when scheduing a callback against an already complete task (semi-synchronous case), prefer to use the io pipe scheduler for onward continuations, not the thread pool
    // - when invoking final continuations, we detect the Inline pipe scheduler and bypass the indirection
    // - the addition of an Abort concept (which invokes any pending continuations, guaranteeing failure)

    /// <summary>
    /// Awaitable SocketAsyncEventArgs, where awaiting the args yields either the BytesTransferred or throws the relevant socket exception
    /// </summary>
    public class SocketAwaitableEventArgs : SocketAsyncEventArgs, ICriticalNotifyCompletion
    {
        /// <summary>
        /// Abort the current async operation (and prevent future operations)
        /// </summary>
        public void Abort(SocketError error = SocketError.OperationAborted)
        {
            if(error == SocketError.Success)
            {
                throw new ArgumentException(nameof(error));
            }

            this._forcedError = error;
            this.OnCompleted(this);
        }

        private volatile SocketError _forcedError; // Success = 0, no field init required

        private static readonly Action _CallbackCompleted = () => { };

        private readonly PipeScheduler _ioScheduler;

        private Action _callback;

        internal static readonly Action<object> _InvokeStateAsAction = state => ((Action)state)();

        /// <summary>
        /// Create a new SocketAwaitableEventArgs instance, optionally providing a scheduler for callbacks
        /// </summary>
        /// <param name="ioScheduler"></param>
        public SocketAwaitableEventArgs(PipeScheduler ioScheduler = null)
        {
            // treat null and Inline interchangeably
            if(ioScheduler == PipeScheduler.Inline)
            {
                ioScheduler = null;
            }

            this._ioScheduler = ioScheduler;
        }

        /// <summary>
        /// Get the awaiter for this instance; used as part of "await"
        /// </summary>
        public SocketAwaitableEventArgs GetAwaiter() => this;

        /// <summary>
        /// Indicates whether the current operation is complete; used as part of "await"
        /// </summary>
        public bool IsCompleted => ReferenceEquals(this._callback, _CallbackCompleted);

        /// <summary>
        /// Gets the result of the async operation is complete; used as part of "await"
        /// </summary>
        public int GetResult()
        {
            Debug.Assert(ReferenceEquals(this._callback, _CallbackCompleted));

            this._callback = null;

            if(this._forcedError != SocketError.Success)
            {
                ThrowSocketException(this._forcedError);
            }

            if(this.SocketError != SocketError.Success)
            {
                ThrowSocketException(this.SocketError);
            }

            return this.BytesTransferred;

            static void ThrowSocketException(SocketError e)
                => throw new SocketException((int)e);
        }

        /// <summary>
        /// Schedules a continuation for this operation; used as part of "await"
        /// </summary>
        public void OnCompleted(Action continuation)
        {
            if(ReferenceEquals(Volatile.Read(ref this._callback), _CallbackCompleted)
                || ReferenceEquals(Interlocked.CompareExchange(ref this._callback, continuation, null), _CallbackCompleted))
            {
                // this is the rare "kinda already complete" case; push to worker to prevent possible stack dive,
                // but prefer the custom scheduler when possible
                if(this._ioScheduler == null)
                {
                    Task.Run(continuation);
                }
                else
                {
                    this._ioScheduler.Schedule(_InvokeStateAsAction, continuation);
                }
            }
        }

        /// <summary>
        /// Schedules a continuation for this operation; used as part of "await"
        /// </summary>
        public void UnsafeOnCompleted(Action continuation)
            => this.OnCompleted(continuation);

        /// <summary>
        /// Marks the operation as complete - this should be invoked whenever a SocketAsyncEventArgs operation returns false
        /// </summary>
        public void Complete()
            => this.OnCompleted(this);

        /// <summary>
        /// Invoked automatically when an operation completes asynchronously
        /// </summary>
        protected override void OnCompleted(SocketAsyncEventArgs e)
        {
            Action continuation = Interlocked.Exchange(ref this._callback, _CallbackCompleted);

            if(continuation != null)
            {
                if(this._ioScheduler == null)
                {
                    continuation.Invoke();
                }
                else
                {
                    this._ioScheduler.Schedule(_InvokeStateAsAction, continuation);
                }
            }
        }
    }
}