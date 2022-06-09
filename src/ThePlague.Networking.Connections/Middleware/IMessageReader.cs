﻿using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace ThePlague.Networking.Connections.Middleware
{
    public interface IMessageReader<TMessage>
    {
        /// <summary>
        /// Parse a single message from <paramref name="input"/> and return it.
        /// Advance <paramref name="consumed"/> and <paramref name="examined"/>
        /// accordingly.
        /// Not intended to process the message!
        /// </summary>
        /// <param name="input">The buffer to parse</param>
        /// <param name="consumed">The byte length of the parsed <paramref name="message"/></param>
        /// <param name="examined">Set to the end of <paramref name="input"/> to allow correct buffering</param>
        /// <param name="message">The parsed message</param>
        /// <returns>true when parsing succeeded, false when it did not succeed</returns>
        bool TryParseMessage
        (
            in ReadOnlySequence<byte> input,
            out SequencePosition consumed,
            out SequencePosition examined,
            [NotNullWhen(true)] out TMessage? message
        );
    }
}
