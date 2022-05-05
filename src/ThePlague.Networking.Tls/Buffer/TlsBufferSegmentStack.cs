using System;
using System.Runtime.CompilerServices;

namespace ThePlague.Networking.Tls.Buffer
{
    internal struct TlsBufferSegmentStack
    {
        private SegmentAsValueType[] _array;
        private int _size;

        public TlsBufferSegmentStack(int size)
        {
            this._array = new SegmentAsValueType[size];
            this._size = 0;
        }

        public int Count => _size;

        public bool TryPop(out TlsBufferSegment? result)
        {
            int size = this._size - 1;
            SegmentAsValueType[] array = _array;

            if ((uint)size >= (uint)array.Length)
            {
                result = default;
                return false;
            }

            this._size = size;
            result = array[size];
            array[size] = default;
            return true;
        }

        // Pushes an item to the top of the stack.
        public void Push(TlsBufferSegment item)
        {
            int size = _size;
            SegmentAsValueType[] array = _array;

            if ((uint)size < (uint)array.Length)
            {
                array[size] = item;
                this._size = size + 1;
            }
            else
            {
                this.PushWithResize(item);
            }
        }

        // Non-inline from Stack.Push to improve its code quality as uncommon path
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void PushWithResize(TlsBufferSegment item)
        {
            Array.Resize(ref _array, 2 * _array.Length);
            _array[this._size] = item;
            this._size++;
        }

        /// <summary>
        /// A simple struct we wrap reference types inside when storing in arrays to
        /// bypass the CLR's covariant checks when writing to arrays.
        /// </summary>
        /// <remarks>
        /// We use <see cref="SegmentAsValueType"/> as a wrapper to avoid paying the cost of covariant checks whenever
        /// the underlying array that the <see cref="TlsBufferSegmentStack"/> class uses is written to.
        /// We've recognized this as a perf win in ETL traces for these stack frames:
        /// clr!JIT_Stelem_Ref
        ///   clr!ArrayStoreCheck
        ///     clr!ObjIsInstanceOf
        /// </remarks>
        private readonly struct SegmentAsValueType
        {
            private readonly TlsBufferSegment _value;
            private SegmentAsValueType(TlsBufferSegment value) => _value = value;
            public static implicit operator SegmentAsValueType(TlsBufferSegment s) => new SegmentAsValueType(s);
            public static implicit operator TlsBufferSegment(SegmentAsValueType s) => s._value;
        }
    }
}
