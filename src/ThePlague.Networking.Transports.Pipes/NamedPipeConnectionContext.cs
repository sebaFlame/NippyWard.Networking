using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.IO.Pipes;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;

using ThePlague.Networking.Pipelines;

namespace ThePlague.Networking.Transports.Pipes
{
    internal class NamedPipeConnectionContext
        : ConnectionContext,
            IDuplexPipe,
            IConnectionIdFeature,
            IConnectionTransportFeature,
            IConnectionItemsFeature,
            IConnectionLifetimeFeature,
            IDisposable
    {
        public override string ConnectionId { get; set; }
        public override IFeatureCollection Features { get; }
        public override IDictionary<object, object> Items { get; set; }
        public override IDuplexPipe Transport { get; set; }

        public PipeReader Input => this._input;
        public PipeWriter Output => this._output;

        internal readonly PipeStream _InputStream;
        internal readonly PipeStream _OutputStream;

        private readonly WrappedReader _input;
        private readonly WrappedWriter _output;

        //as seen from server
        internal const string _InputSuffix = "_in";
        internal const string _OutputSuffix = "_out";

        internal NamedPipeConnectionContext
        (
            NamedPipeEndPoint endPoint,
            PipeStream outputStream,
            PipeStream inputStream,
            StreamPipeWriterOptions sendOptions = null,
            StreamPipeReaderOptions receiveOptions = null,
            IFeatureCollection serverFeatureCollection = null
        )
        {
            this._OutputStream = outputStream;
            this._InputStream = inputStream;

            this.ConnectionId = Guid.NewGuid().ToString();
            this.RemoteEndPoint = endPoint;

            this._output = new WrappedWriter
            (
                PipeWriter.Create
                (
                    outputStream,
                    sendOptions ?? new StreamPipeWriterOptions(leaveOpen: true)
                ),
                this
            );

            this._input = new WrappedReader
            (
                PipeReader.Create
                (
                    inputStream,
                    receiveOptions ?? new StreamPipeReaderOptions(leaveOpen: true)
                ),
                this
            );

            this.Transport = this;

            this.Features = new FeatureCollection(serverFeatureCollection);
            this.Items = new ConnectionItems();
        }

        ~NamedPipeConnectionContext()
        {
            this.Dispose(false);
        }

        public static async ValueTask<ConnectionContext> ConnectAsync
        (
            NamedPipeEndPoint endPoint,
            System.IO.Pipes.PipeOptions pipeOptions
                = System.IO.Pipes.PipeOptions.Asynchronous,
            StreamPipeWriterOptions sendOptions = null,
            StreamPipeReaderOptions receiveOptions = null,
            IFeatureCollection featureCollection = null
        )
        {
            //use 2 pipes to allow for graceful shutdown
            NamedPipeClientStream inputStream = new NamedPipeClientStream
            (
                endPoint.ServerName,
                string.Concat(endPoint.PipeName, _OutputSuffix),
                PipeDirection.In,
                pipeOptions
            );

            NamedPipeClientStream outputStream = new NamedPipeClientStream
            (
                endPoint.ServerName,
                string.Concat(endPoint.PipeName, _InputSuffix),
                PipeDirection.Out,
                pipeOptions
            );

            //TODO: exception handling per stream
            await Task.WhenAll
            (
                inputStream.ConnectAsync(),
                outputStream.ConnectAsync()
            );

            return new NamedPipeConnectionContext
            (
                endPoint,
                outputStream,
                inputStream,
                sendOptions,
                receiveOptions,
                featureCollection
            );
        }

        internal void CompleteOutput(Exception ex)
        {
            this._output.Complete(ex);
        }

        internal void CompleteInput(Exception ex)
        {
            this._input.Complete(ex);
        }

        public override void Abort(ConnectionAbortedException abortReason)
        {
            try
            {
                this._output.Complete(abortReason);
            }
            catch
            { }

            try
            {
                this._input.Complete(abortReason);
            }
            catch
            { }
        }

        protected void Dispose(bool isDisposing)
        {
            try
            {
                this._output.Complete(new ObjectDisposedException(nameof(NamedPipeConnectionContext)));
            }
            catch
            { }

            try
            {
                this._input.Complete(new ObjectDisposedException(nameof(NamedPipeConnectionContext)));
            }
            catch
            { }

            try
            {
                this._OutputStream.Dispose();
            }
            catch
            { }
            
            try
            {
                this._InputStream.Dispose();
            }
            catch
            { }

            if(!isDisposing)
            {
                return;
            }

            GC.SuppressFinalize(this);
        }

        public void Dispose() => this.Dispose(true);

        public override ValueTask DisposeAsync()
        {
            this.Dispose(true);
            return base.DisposeAsync();
        }
    }
}
