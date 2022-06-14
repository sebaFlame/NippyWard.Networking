using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.IO.Pipes;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

using NippyWard.Networking.Logging;

namespace NippyWard.Networking.Transports.Pipes
{
    internal partial class NamedPipeConnectionContext
    {
        //as seen from server
        internal const string _InputSuffix = "_in";
        internal const string _OutputSuffix = "_out";

        public static async ValueTask<ConnectionContext> ConnectAsync
        (
            NamedPipeEndPoint endpoint,
            System.IO.Pipes.PipeOptions pipeOptions
                = System.IO.Pipes.PipeOptions.Asynchronous,
            System.IO.Pipelines.PipeOptions? sendPipeOptions = null,
            System.IO.Pipelines.PipeOptions? receivePipeOptions = null,
            IFeatureCollection? featureCollection = null,
            string? name = null,
            ILogger? logger = null
        )
        {
            //use 2 pipes to allow for graceful shutdown
            NamedPipeClientStream inputStream = new NamedPipeClientStream
            (
                endpoint.ServerName,
                string.Concat(endpoint.PipeName, _OutputSuffix),
                PipeDirection.In,
                pipeOptions
            );

            NamedPipeClientStream outputStream = new NamedPipeClientStream
            (
                endpoint.ServerName,
                string.Concat(endpoint.PipeName, _InputSuffix),
                PipeDirection.Out,
                pipeOptions
            );

            logger?.TraceLog(name ?? nameof(NamedPipeConnectionContext), $"connecting to {endpoint}");

            //TODO: exception handling per stream
            await Task.WhenAll
            (
                inputStream.ConnectAsync(),
                outputStream.ConnectAsync()
            );

            TransportConnectionContext ctx = NamedPipeConnectionContext.Create
            (
                endpoint,
                outputStream,
                inputStream,
                sendPipeOptions,
                receivePipeOptions,
                featureCollection,
                name,
                logger
            );

            return ctx;
        }
    }
}
