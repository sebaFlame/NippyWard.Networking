namespace ThePlague.Networking.Sockets
{
    /// <summary>
    /// When possible, determines how the pipe first reached a close state
    /// </summary>
    public enum PipeShutdownKind
    {
        // 0**: things to do with the pipe
        /// <summary>
        /// The pipe is still open
        /// </summary>
        None = 0, // important this stays zero for default value, etc
        /// <summary>
        /// The pipe itself was disposed
        /// </summary>
        PipeDisposed = 1,
        /// <summary>
        /// Abort Requested
        /// </summary>
        ConnectionAborted = 2,

        // 1**: things to do with the read loop
        /// <summary>
        /// The socket-reader reached a natural EOF from the socket
        /// </summary>
        ReadEndOfStream = 100,
        /// <summary>
        /// The socket-reader encountered a dispose failure
        /// </summary>
        ReadDisposed = 101,
        /// <summary>
        /// The socket-reader encountered an IO failure
        /// </summary>
        ReadIOException = 102,
        /// <summary>
        /// The socket-reader encountered a general failure
        /// </summary>
        ReadException = 103,
        /// <summary>
        /// The socket-reader encountered a socket failure - the SocketError may be populated
        /// </summary>
        ReadSocketError = 104,
        /// <summary>
        /// When attempting to flush incoming data, the pipe indicated that it was complete
        /// </summary>
        ReadFlushCompleted = 105,
        /// <summary>
        /// When attempting to flush incoming data, the pipe indicated cancelation
        /// </summary>
        ReadFlushCanceled = 106,

        // 2**: things to do with the write loop
        /// <summary>
        /// The socket-writerreached a natural EOF from the pipe
        /// </summary>
        WriteEndOfStream = 200,
        /// <summary>
        /// The socket-writer encountered a dispose failure
        /// </summary>
        WriteDisposed = 201,
        /// <summary>
        /// The socket-writer encountered an IO failure
        /// </summary>
        WriteIOException = 203,
        /// <summary>
        /// The socket-writer encountered a general failure
        /// </summary>
        WriteException = 204,
        /// <summary>
        /// The socket-writer encountered a socket failure - the SocketError may be populated
        /// </summary>
        WriteSocketError = 205,

        // 2**: things to do with the reader/writer themselves
        /// <summary>
        /// The input's reader was completed
        /// </summary>
        InputReaderCompleted = 300,
        /// <summary>
        /// The input's writer was completed
        /// </summary>
        InputWriterCompleted = 301,
        /// <summary>
        /// The output's reader was completed
        /// </summary>
        OutputReaderCompleted = 302,
        /// <summary>
        /// The input's writer was completed
        /// </summary>
        OutputWriterCompleted = 303,
        /// <summary>
        /// An application defined exit was triggered by the client
        /// </summary>
        ProtocolExitClient = 400,
        /// <summary>
        /// An application defined exit was triggered by the server
        /// </summary>
        ProtocolExitServer = 401,
    }
}
