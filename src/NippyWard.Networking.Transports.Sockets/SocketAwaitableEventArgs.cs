using System;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using System.Reflection;
using System.Linq.Expressions;

using NippyWard.Networking.Connections;

using Microsoft.Extensions.Logging;

namespace NippyWard.Networking.Transports.Sockets
{
    //based on 
    //https://github.com/dotnet/aspnetcore/blob/77042af34bde9103b865c6f9a2baa99adc23e6b3/src/Servers/Kestrel/Transport.Sockets/src/Internal/SocketAwaitableEventArgs.cs
    //and AwaitableSocketAsyncEventArgs in
    //https://github.com/dotnet/runtime/blob/2a394578fb13cb143779229e101667ec925a2eed/src/libraries/System.Net.Sockets/src/System/Net/Sockets/Socket.Tasks.cs
    /// <summary>
    /// Awaitable SocketAsyncEventArgs, where awaiting the args yields either the BytesTransferred or throws the relevant socket exception
    /// </summary>
    public class SocketAwaitableEventArgs : SocketAsyncEventArgs, IValueTaskSource, IValueTaskSource<int>, IValueTaskSource<Socket>
    {
        private readonly ILogger? _logger;
        private readonly PipeScheduler _ioScheduler;
        private Action<object?>? _continuation;
        private short _token;
        //private ExecutionContext? _executionContext;
        //private object? _scheduler;
        private CancellationToken _cancellationToken;
        private CancellationTokenRegistration _cancellationTokenRegistration;

        private static readonly Action<object?> _ContinuationCompleted = _ => { };
        private static readonly Func<Socket, SocketAsyncEventArgs, CancellationToken, bool> _AcceptAsync;
        private static readonly Func<Socket, SocketAsyncEventArgs, CancellationToken, bool> _ReceiveAsync;
        private static readonly Func<Socket, SocketAsyncEventArgs, CancellationToken, bool> _SendAsync;

        static SocketAwaitableEventArgs()
        {
            _AcceptAsync = InitializeAysncCancellationFunction(nameof(Socket.AcceptAsync));
            _ReceiveAsync = InitializeAysncCancellationFunction(nameof(Socket.ReceiveAsync));
            _SendAsync = InitializeAysncCancellationFunction(nameof(Socket.SendAsync));
        }

        //TODO: hacky!, private (unstable API?) methods!
        private static Func<Socket, SocketAsyncEventArgs, CancellationToken, bool> InitializeAysncCancellationFunction(string methodName)
        {
            MethodInfo method = typeof(Socket).GetMethod
            (
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new Type[] { typeof(SocketAsyncEventArgs), typeof(CancellationToken) },
                null
            )!;

            ParameterExpression socketParameter = Expression.Parameter(typeof(Socket));
            ParameterExpression argsParameter = Expression.Parameter(typeof(SocketAsyncEventArgs));
            ParameterExpression cancelParameter = Expression.Parameter(typeof(CancellationToken));

            MethodCallExpression methodCall = Expression.Call
            (
                socketParameter,
                method,
                argsParameter,
                cancelParameter
            );

            return Expression.Lambda<Func<Socket, SocketAsyncEventArgs, CancellationToken, bool>>
            (
                methodCall,
                socketParameter,
                argsParameter,
                cancelParameter
            )
            .Compile();
        }

        /// <summary>
        /// Create a new SocketAwaitableEventArgs instance, optionally providing a scheduler for callbacks
        /// </summary>
        /// <param name="ioScheduler"></param>
        public SocketAwaitableEventArgs(PipeScheduler ioScheduler, ILogger? logger)
            : base()
        {
            this._ioScheduler = ioScheduler;
            this._logger = logger;
        }

        #region IValueTaskSource implementeation
        protected override void OnCompleted(SocketAsyncEventArgs _)
        {
            Action<object?>? c = this._continuation;

            if (c != null
                || (c = Interlocked.CompareExchange(ref this._continuation, _ContinuationCompleted, null)) != null)
            {
                object? continuationState = this.UserToken;
                this.UserToken = null;
                this._continuation = _ContinuationCompleted; // in case someone's polling IsCompleted

                _ioScheduler.Schedule(c, continuationState);
            }
        }

        //protected override void OnCompleted(SocketAsyncEventArgs _)
        //{
        //    // When the operation completes, see if OnCompleted was already called to hook up a continuation.
        //    // If it was, invoke the continuation.
        //    Action<object?>? c = this._continuation;
        //    if (c != null
        //        || (c = Interlocked.CompareExchange(ref _continuation, _ContinuationCompleted, null)) != null)
        //    {
        //        Debug.Assert(c != _ContinuationCompleted, "The delegate should not have been the completed sentinel.");

        //        object? continuationState = this.UserToken;
        //        this.UserToken = null;
        //        this._continuation = _ContinuationCompleted; // in case someone's polling IsCompleted

        //        ExecutionContext? ec = this._executionContext;
        //        if (ec is null)
        //        {
        //            this.InvokeContinuation(c, continuationState, forceAsync: false, requiresExecutionContextFlow: false);
        //        }
        //        else
        //        {
        //            // This case should be relatively rare, as the async Task/ValueTask method builders
        //            // use the awaiter's UnsafeOnCompleted, so this will only happen with code that
        //            // explicitly uses the awaiter's OnCompleted instead.
        //            _executionContext = null;
        //            ExecutionContext.Run(ec, runState =>
        //            {
        //                (SocketAwaitableEventArgs, Action<object?>, object) t = ((SocketAwaitableEventArgs, Action<object?>, object))runState!;
        //                t.Item1.InvokeContinuation(t.Item2, t.Item3, forceAsync: false, requiresExecutionContextFlow: false);
        //            }, (this, c, continuationState));
        //        }
        //    }
        //}

        public int GetResult(short token)
        {
            int bytes = this.BytesTransferred;
            SocketError error = this.SocketError;

            this.Release();

            if (error != SocketError.Success)
            {
                CreateException(this.SocketError);
            }

            return bytes;
        }

        void IValueTaskSource.GetResult(short token)
            => this.GetResult(token);

        Socket IValueTaskSource<Socket>.GetResult(short token)
        {
            Socket socket = this.AcceptSocket!;
            SocketError error = this.SocketError;

            this.Release();

            if (error != SocketError.Success)
            {
                CreateException(this.SocketError);
            }

            return socket;
        }

        protected static SocketException CreateException(SocketError e)
        {
            return new SocketException((int)e);
        }

        public ValueTaskSourceStatus GetStatus(short token)
        {
            Action<object?>? c = this._continuation;
            SocketError socketError = this.SocketError;

            return !ReferenceEquals(c, _ContinuationCompleted) ? ValueTaskSourceStatus.Pending :
                    socketError == SocketError.Success ? ValueTaskSourceStatus.Succeeded :
                    ValueTaskSourceStatus.Faulted;
        }

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            this.UserToken = state;
            Action<object?>? prevContinuation = Interlocked.CompareExchange(ref _continuation, continuation, null);

            if (ReferenceEquals(prevContinuation, _ContinuationCompleted))
            {
                this.UserToken = null;

                ThreadPool.QueueUserWorkItem(continuation, state, preferLocal: true);
            }
        }

        //public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        //{
        //    if (token != _token)
        //    {
        //        throw new InvalidOperationException("incorrect token");
        //    }

        //    if ((flags & ValueTaskSourceOnCompletedFlags.FlowExecutionContext) != 0)
        //    {
        //        this._executionContext = ExecutionContext.Capture();
        //    }

        //    if ((flags & ValueTaskSourceOnCompletedFlags.UseSchedulingContext) != 0)
        //    {
        //        SynchronizationContext? sc = SynchronizationContext.Current;
        //        if (sc != null && sc.GetType() != typeof(SynchronizationContext))
        //        {
        //            this._scheduler = sc;
        //        }
        //        else
        //        {
        //            TaskScheduler ts = TaskScheduler.Current;
        //            if (ts != TaskScheduler.Default)
        //            {
        //                this._scheduler = ts;
        //            }
        //        }
        //    }

        //    this.UserToken = state; // Use UserToken to carry the continuation state around
        //    Action<object>? prevContinuation = Interlocked.CompareExchange(ref _continuation, continuation, null);
        //    if (ReferenceEquals(prevContinuation, _ContinuationCompleted))
        //    {
        //        // Lost the race condition and the operation has now already completed.
        //        // We need to invoke the continuation, but it must be asynchronously to
        //        // avoid a stack dive.  However, since all of the queueing mechanisms flow
        //        // ExecutionContext, and since we're still in the same context where we
        //        // captured it, we can just ignore the one we captured.
        //        bool requiresExecutionContextFlow = this._executionContext != null;
        //        this._executionContext = null;
        //        this.UserToken = null; // we have the state in "state"; no need for the one in UserToken
        //        this.InvokeContinuation(continuation, state, forceAsync: true, requiresExecutionContextFlow);
        //    }
        //    else if (prevContinuation != null)
        //    {
        //        // Flag errors with the continuation being hooked up multiple times.
        //        // This is purely to help alert a developer to a bug they need to fix.
        //        throw new InvalidOperationException("Multiple continuations");
        //    }
        //}

        //private void InvokeContinuation(Action<object?> continuation, object? state, bool forceAsync, bool requiresExecutionContextFlow)
        //{
        //    object? scheduler = this._scheduler;
        //    this._scheduler = null;

        //    if (scheduler != null)
        //    {
        //        if (scheduler is SynchronizationContext sc)
        //        {
        //            sc.Post(s =>
        //            {
        //                (Action<object>, object) t = ((Action<object>, object))s!;
        //                t.Item1(t.Item2);
        //            }, (continuation, state));
        //        }
        //        else
        //        {
        //            Debug.Assert(scheduler is TaskScheduler, $"Expected TaskScheduler, got {scheduler}");
        //            Task.Factory.StartNew(continuation, state, CancellationToken.None, TaskCreationOptions.DenyChildAttach, (TaskScheduler)scheduler);
        //        }
        //    }
        //    else if (forceAsync)
        //    {
        //        if (requiresExecutionContextFlow)
        //        {
        //            ThreadPool.QueueUserWorkItem(continuation, state, preferLocal: true);
        //        }
        //        else
        //        {
        //            ThreadPool.UnsafeQueueUserWorkItem(continuation, state, preferLocal: true);
        //        }
        //    }
        //    else
        //    {
        //        continuation(state);
        //    }
        //}
        #endregion

        #region Custom socket implementation
        private void Release()
        {
            this._token++;
            this._continuation = null;
            this._cancellationTokenRegistration.Dispose();
            this._cancellationToken = default;
        }

        public ValueTask<Socket> AcceptAsync(Socket socket, CancellationToken cancellationToken = default)
        {
            Debug.Assert(Volatile.Read(ref _continuation) == null, "Expected null continuation to indicate reserved for use");

            this._cancellationToken = cancellationToken;

            if (_AcceptAsync(socket, this, cancellationToken))
            {
                return new ValueTask<Socket>(this, _token);
            }

            Socket acceptSocket = this.AcceptSocket!;
            SocketError error = this.SocketError;

            this.AcceptSocket = null;

            this.Release();

            return error == SocketError.Success ?
                new ValueTask<Socket>(acceptSocket) :
                throw CreateException(error);
        }

        public ValueTask<int> ReceiveAsync(Socket socket, CancellationToken cancellationToken = default)
        {
            Debug.Assert(Volatile.Read(ref _continuation) == null, "Expected null continuation to indicate reserved for use");

            this._cancellationToken = cancellationToken;

            if (_ReceiveAsync(socket, this, cancellationToken))
            {
                return new ValueTask<int>(this, this._token);
            }

            int bytesTransferred = this.BytesTransferred;
            SocketError error = this.SocketError;

            this.Release();

            return error == SocketError.Success ?
                new ValueTask<int>(bytesTransferred) :
                throw CreateException(error);
        }

        public ValueTask<int> SendAsync(Socket socket, CancellationToken cancellationToken = default)
        {
            Debug.Assert(Volatile.Read(ref _continuation) == null, "Expected null continuation to indicate reserved for use");

            this._cancellationToken = cancellationToken;

            if (_SendAsync(socket, this, cancellationToken))
            {
                return new ValueTask<int>(this, this._token);
            }

            int bytesTransferred = this.BytesTransferred;
            SocketError error = this.SocketError;

            this.Release();

            return error == SocketError.Success ?
                new ValueTask<int>(bytesTransferred) :
                throw CreateException(error);
        }

        public ValueTask ConnectAsync(Socket socket, CancellationToken cancellationToken = default)
        {
            Debug.Assert(Volatile.Read(ref _continuation) == null, "Expected null continuation to indicate reserved for use");

            this._cancellationTokenRegistration = cancellationToken.Register((e) => Socket.CancelConnectAsync((SocketAsyncEventArgs)e!), this);

            try
            {
                if (socket.ConnectAsync(this))
                {
                    return new ValueTask(this, _token);
                }
            }
            catch
            {
                this._cancellationTokenRegistration.Dispose();
                this.Release();
                throw;
            }

            //completed

            this._cancellationTokenRegistration.Dispose();

            SocketError error = this.SocketError;

            this.Release();

            return error == SocketError.Success ?
                default :
                throw CreateException(error);
        }
        #endregion

        /// <summary>
        /// Abort the current async operation (and prevent future operations)
        /// </summary>
        public void Abort(SocketError error = SocketError.OperationAborted)
        {
            if (error == SocketError.Success)
            {
                throw new ArgumentException($"{error} is not an error to abort with");
            }

            this.SocketError = error;

            this.Dispose();
        }
    }
}