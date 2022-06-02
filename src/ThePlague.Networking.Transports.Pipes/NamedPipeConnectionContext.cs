using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.IO.Pipes;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

using ThePlague.Networking.Transports;

namespace ThePlague.Networking.Transports.Pipes
{
    internal partial class NamedPipeConnectionContext : TransportConnectionContext
    {
        public override PipeReader Input => this._input;
        public override PipeWriter Output => this._output;

        private readonly PipeStream _inputStream;
        private readonly PipeStream _outputStream;
        private readonly WrappedReader _input;
        private readonly WrappedWriter _output;

        internal NamedPipeConnectionContext
        (
            NamedPipeEndPoint endPoint,
            Pipe outputPipe,
            Pipe inputPipe,
            PipeStream outputStream,
            PipeStream inputStream,
            IFeatureCollection serverFeatureCollection = null,
            string name = null,
            ILogger logger = null
        )
            : base
            (
                  endPoint,
                  endPoint,
                  outputPipe,
                  inputPipe,
                  serverFeatureCollection,
                  name,
                  logger

            )
        {
            this.RemoteEndPoint = endPoint;
            this.LocalEndPoint = endPoint;

            this._outputStream = outputStream;
            this._inputStream = inputStream;

            this._output = new NamedPipeWriter
            (
                outputPipe.Writer,
                this
            );

            this._input = new NamedPipeReader
            (
                inputPipe.Reader,
                this
            );
        }

        public static TransportConnectionContext Create
        (
            NamedPipeEndPoint endPoint,
            PipeStream outputStream,
            PipeStream inputStream,
            System.IO.Pipelines.PipeOptions sendPipeOptions = null,
            System.IO.Pipelines.PipeOptions receivePipeOptions = null,
            IFeatureCollection featureCollection = null,
            string name = null,
            ILogger logger = null
        )
            => Create
            (
                endPoint,
                new Pipe(sendPipeOptions ?? System.IO.Pipelines.PipeOptions.Default),
                new Pipe(receivePipeOptions ?? System.IO.Pipelines.PipeOptions.Default),
                outputStream,
                inputStream,
                featureCollection,
                name,
                logger
            );

        public static TransportConnectionContext Create
        (
            NamedPipeEndPoint endpoint,
            Pipe outputPipe,
            Pipe inputPipe,
            PipeStream outputStream,
            PipeStream inputStream,
            IFeatureCollection featureCollection = null,
            string name = null,
            ILogger logger = null
        )
            => new NamedPipeConnectionContext
            (
                endpoint,
                outputPipe,
                inputPipe,
                outputStream,
                inputStream,
                featureCollection,
                name,
                logger
            )
            .InitializeSendReceiveTasks();


        internal void InputReaderCompleted()
        {
            try
            {
                this._inputStream.Dispose();
            }
            catch
            { }
        }

        internal void OutputWriterCompleted()
        {
            try
            {
                this._outputStream.Dispose();
            }
            catch
            { }
        }

        internal ValueTask CompleteOutputAsync(Exception ex)
            => this._output.CompleteAsync(ex);

        internal ValueTask CompleteInputAsync(Exception ex)
            => this._input.CompleteAsync(ex);

        protected override void DisposeCore(bool isDisposing)
        {
            try
            {
                this._outputStream.Dispose();
            }
            catch
            { }
            
            try
            {
                this._inputStream.Dispose();
            }
            catch
            { }
        }
    }
}
