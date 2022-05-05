using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.CompilerServices;

namespace ThePlague.Networking.Tls.Buffer
{
    //base on BufferSegment
    internal class TlsBufferSegment : ReadOnlySequenceSegment<byte>
    {
        private IMemoryOwner<byte>? _memoryOwner;
        private byte[]? _array;
        private TlsBufferSegment? _next;
        private int _end;

        /// <summary>
        /// The End represents the offset into AvailableMemory where the range of "active" bytes ends. At the point when the block is leased
        /// the End is guaranteed to be equal to Start. The value of Start may be assigned anywhere between 0 and
        /// Buffer.Length, and must be equal to or less than End.
        /// </summary>
        public int End
        {
            get => this._end;
            set
            {
                this._end = value;
                this.Memory = this.AvailableMemory.Slice(0, value);
            }
        }

        /// <summary>
        /// Reference to the next block of data when the overall "active" bytes spans multiple blocks. At the point when the block is
        /// leased Next is guaranteed to be null. Start, End, and Next are used together in order to create a linked-list of discontiguous
        /// working memory. The "active" memory is grown when bytes are copied in, End is increased, and Next is assigned. The "active"
        /// memory is shrunk when bytes are consumed, Start is increased, and blocks are returned to the pool.
        /// </summary>
        public TlsBufferSegment? NextSegment
        {
            get => _next;
            set
            {
                this.Next = value;
                this._next = value;
            }
        }

        public void SetOwnedMemory(IMemoryOwner<byte> memoryOwner)
        {
            this._memoryOwner = memoryOwner;
            this.AvailableMemory = memoryOwner.Memory;
        }

        public void SetOwnedMemory(byte[] arrayPoolBuffer)
        {
            this._array = arrayPoolBuffer;
            this.AvailableMemory = arrayPoolBuffer;
        }

        public void ResetMemory()
        {
            IMemoryOwner<byte>? memoryOwner = this._memoryOwner;
            if (memoryOwner != null)
            {
                this._memoryOwner = null;
                memoryOwner.Dispose();
            }
            else
            {
                ArrayPool<byte>.Shared.Return(_array);
                _array = null;
            }

            this.Next = null;
            this.RunningIndex = 0;
            this.Memory = default;
            this._next = null;
            this._end = 0;
            this.AvailableMemory = default;
        }

        // Exposed for testing
        internal object? MemoryOwner => (object?)_memoryOwner ?? _array;

        public Memory<byte> AvailableMemory { get; private set; }

        public int Length => this.End;

        public int WritableBytes
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => this.AvailableMemory.Length - this.End;
        }

        public void SetNext(TlsBufferSegment segment)
        {
            this.NextSegment = segment;

            segment = this;

            while (segment.Next != null)
            {
                segment.NextSegment.RunningIndex = segment.RunningIndex + segment.Length;
                segment = segment.NextSegment;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static long GetLength(TlsBufferSegment startSegment, int startIndex, TlsBufferSegment endSegment, int endIndex)
        {
            return (endSegment.RunningIndex + (uint)endIndex) - (startSegment.RunningIndex + (uint)startIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static long GetLength(long startPosition, TlsBufferSegment endSegment, int endIndex)
        {
            return (endSegment.RunningIndex + (uint)endIndex) - startPosition;
        }
    }
}