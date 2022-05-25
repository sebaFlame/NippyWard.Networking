using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Benchmark
{
    internal sealed partial class ArrayMemoryPool<T> : MemoryPool<T>
    {
        private const int MaximumBufferSize = int.MaxValue;

        public sealed override int MaxBufferSize => MaximumBufferSize;

        public sealed override IMemoryOwner<T> Rent(int minimumBufferSize = -1)
        {
            if (minimumBufferSize == -1)
            {
                minimumBufferSize = 1 + (4095 / Unsafe.SizeOf<T>());
            }
            else if (((uint)minimumBufferSize) > MaximumBufferSize)
            {
                throw new ArgumentOutOfRangeException();
            }

            return new ArrayMemoryPoolBuffer(minimumBufferSize);
        }

        protected sealed override void Dispose(bool disposing) { }  // ArrayMemoryPool is a shared pool so Dispose() would be a nop even if there were native resources to dispose.

        private sealed class ArrayMemoryPoolBuffer : IMemoryOwner<T>
        {
            private T[]? _array;

            public ArrayMemoryPoolBuffer(int size)
            {
                _array = ArrayPool<T>.Shared.Rent(size);
            }

            public Memory<T> Memory
            {
                get
                {
                    T[]? array = _array;
                    if (array == null)
                    {
                        throw new ObjectDisposedException(nameof(array));
                    }

                    return new Memory<T>(array);
                }
            }

            public void Dispose()
            {
                T[]? array = _array;
                if (array != null)
                {
                    _array = null;
                    ArrayPool<T>.Shared.Return(array);
                }
            }
        }
    }
}
