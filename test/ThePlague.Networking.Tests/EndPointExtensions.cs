using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime;

using System.IO.Pipelines;

using ThePlague.Networking.Connections;
using ThePlague.Networking.Transports.Sockets;
using ThePlague.Networking.Transports.Pipes;

namespace ThePlague.Networking.Tests
{
    internal static class EndPointExtensions
    {
        internal static ServerBuilder ConfigureEndpoint
        (
            this ServerBuilder serverBuilder,
            EndPoint endpoint,
            Func<string> createName
        )
        {
            return endpoint switch
            {
                IPEndPoint
                    => serverBuilder.UseSocket
                    (
                        endpoint,
                        createName/*,
                        new PipeOptions
                        (
                            useSynchronizationContext: false,
                            resumeWriterThreshold: 1,
                            pauseWriterThreshold: 1,
                            writerScheduler: PipeScheduler.Inline,
                            readerScheduler: PipeScheduler.Inline
                        ),
                        new PipeOptions
                        (
                            useSynchronizationContext: false,
                            resumeWriterThreshold: 1,
                            pauseWriterThreshold: 1,
                            writerScheduler: PipeScheduler.Inline,
                            readerScheduler: PipeScheduler.Inline
                        )*/
                    ),
                UnixDomainSocketEndPoint
                    => serverBuilder.UseSocket(endpoint, createName),
                NamedPipeEndPoint when OperatingSystem.IsWindows()
                    => serverBuilder.UseNamedPipe((NamedPipeEndPoint)endpoint, createName),
                _
                    => throw new NotSupportedException()
            };
        }

        internal static ClientBuilder ConfigureEndpoint
        (
            this ClientBuilder clientBuilder,
            EndPoint endpoint,
            Func<string> createName
        )
        {
            return endpoint switch
            {
                IPEndPoint
                    => clientBuilder.UseSocket
                    (
                        createName/*,
                        new PipeOptions
                        (
                            useSynchronizationContext: false,
                            resumeWriterThreshold: 1,
                            pauseWriterThreshold: 1,
                            writerScheduler: PipeScheduler.Inline,
                            readerScheduler: PipeScheduler.Inline
                        ),
                        new PipeOptions
                        (
                            useSynchronizationContext: false,
                            resumeWriterThreshold: 1,
                            pauseWriterThreshold: 1,
                            writerScheduler: PipeScheduler.Inline,
                            readerScheduler: PipeScheduler.Inline
                        )*/
                    ),
                UnixDomainSocketEndPoint
                    => clientBuilder.UseSocket(createName),
                NamedPipeEndPoint when OperatingSystem.IsWindows()
                    => clientBuilder.UseNamedPipe(createName),
                _
                    => throw new NotSupportedException()
            };
        }
    }
}
