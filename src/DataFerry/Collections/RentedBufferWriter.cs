﻿using System.Buffers;

namespace lvlup.DataFerry.Collections
{
    /// <summary>
    /// Represents a buffer writer that rents arrays from a <see cref="StackArrayPool{T}"/> to minimize new allocations.
    /// </summary>
    /// <remarks>This class implements <see cref="IBufferWriter{T}"/> and <see cref="IDisposable"/>.</remarks>
    /// <typeparam name="T">The type of elements in the buffer.</typeparam>
    public sealed class RentedBufferWriter<T> : IBufferWriter<T>, IDisposable
    {
        private T[] _buffer;
        private int _index;
        private readonly StackArrayPool<T> _pool;
        private readonly List<(ReadOnlySequence<byte> Segment, int Length)> _segments;
        private const int DefaultInitialBufferSize = 256;

        /// <summary>
        /// Gets the amount of free space available in the underlying buffer.
        /// </summary>
        public int FreeCapacity => _buffer.Length - _index;

        /// <summary>
        /// Initializes a new instance of the <see cref="RentedBufferWriter{T}"/> class.
        /// </summary>
        /// <remarks>A new instance of this class should be created for each request.</remarks>
        /// <param name="pool">The <see cref="StackArrayPool{T}"/> to use for renting buffers.</param>
        public RentedBufferWriter(StackArrayPool<T> pool)
        {
            _pool = pool;
            _buffer = Array.Empty<T>();
            _segments = Enumerable.Empty<(ReadOnlySequence<byte>, int)>();
        }

        /// <summary>
        /// Advances the current write position in the buffer.
        /// </summary>
        /// <param name="count">The number of elements to advance.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Advancing by <paramref name="count"/> would exceed the buffer's capacity.</exception>
        public void Advance(int count)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(count, 0, nameof(count));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(_index, _buffer.Length - count, nameof(_index));

            _index += count;
        }

        /// <summary>
        /// Returns a <see cref="Memory{T}"/> representing a contiguous region of memory that can be written to.
        /// </summary>
        /// <param name="sizeHint">The minimum size of the returned memory. If the value is 0, a non-empty memory is returned.</param>
        /// <returns>A <see cref="Memory{T}"/> representing a contiguous region of memory that can be written to.</returns>
        public Memory<T> GetMemory(int sizeHint = 0)
        {
            CheckAndResizeBuffer(sizeHint);
            return _buffer.AsMemory(_index);
        }

        /// <summary>
        /// Returns a <see cref="Span{T}"/> representing a contiguous region of memory that can be written to.
        /// </summary>
        /// <param name="sizeHint">The minimum size of the returned span. If the value is 0, a non-empty span is returned.</param>
        /// <returns>A <see cref="Span{T}"/> representing a contiguous region of memory that can be written to.</returns>
        public Span<T> GetSpan(int sizeHint = 0)
        {
            CheckAndResizeBuffer(sizeHint);
            return _buffer.AsSpan(_index);
        }

        /// <summary>
        /// Gets the segments that have been written to the buffer.
        /// </summary>
        /// <returns>An enumerable collection of <see cref="ReadOnlySequence{byte}"/> instances representing the segments.</returns>
        public IEnumerable<ReadOnlySequence<byte>> GetSegments()
        {
            if (_index > 0)
            {
                // Create a ReadOnlySequence<byte> from the last segment
                var sequence = new ReadOnlySequence<byte>(_buffer, 0, _index);
                _segments.Add(sequence, _index);
            }

            return _segments.Select(s => s.Segment);
        }

        /// <summary>
        /// Creates a new array and copies the contents of the buffer to it.
        /// </summary>
        /// <returns>A new array containing the contents of the buffer.</returns>
        //public T[] ToArray() => _buffer.AsSpan(0, _index).ToArray();
        public T[] ToArray()
        {
            // Concatenate all segments into a single array
            var result = new T[_segments.Sum(s => s.Length) + _index];
            var offset = 0;
            foreach (var (segment, length) in _segments)
            {
                segment.Slice(0, length).CopyTo(result.AsMemory(offset));
                offset += length;
            }
            _buffer.AsSpan(0, _index).CopyTo(result.AsSpan(offset));
            return result;
        }        

        /// <summary>
        /// Ensures that the buffer has enough capacity to accommodate the specified size hint.
        /// If necessary, the buffer is resized by renting a new array from the pool and copying the existing data.
        /// </summary>
        /// <param name="sizeHint">The minimum size that the buffer should be able to accommodate.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="sizeHint"/> is negative.</exception>
        internal void CheckAndResizeBuffer(int sizeHint)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(sizeHint, 0, nameof(sizeHint));

            if (sizeHint <= 0) sizeHint = 1;

            // If capacity is exceeded, we need to resize
            if (sizeHint > FreeCapacity)
            {
                // Track the sequence with its length
                var sequence = new ReadOnlySequence<byte>(_buffer, 0, _index);
                _segments.Add(sequence, _index);

                // We calculate what the new buffer size should be
                int currentLength = _buffer.Length;
                // Attempt to grow by the larger of the sizeHint and double the current size
                int growBy = Math.Max(sizeHint, currentLength);

                if (currentLength == 0)
                {
                    growBy = Math.Max(growBy, DefaultInitialBufferSize);
                }

                int newSize = currentLength + growBy;

                // Check if we have enough memory
                if ((uint)newSize > int.MaxValue)
                {
                    // Attempt to grow to ArrayMaxLength.
                    var needed = (uint)(currentLength - FreeCapacity + sizeHint);

                    ArgumentOutOfRangeException.ThrowIfGreaterThan(needed, (uint)Array.MaxLength, 
                        "Requested buffer size exceeds max length.");

                    newSize = Array.MaxLength;
                }

                // Copy the contents of the buffer
                // and return it to the pool if applicable
                var oldBuffer = _buffer;
                _buffer = _pool.Rent(newSize);
                oldBuffer.AsSpan(0, _index).CopyTo(_buffer);
                if (oldBuffer.Length != 0) _pool.Return(oldBuffer);
                _index = 0;
            }
        }

        /// <summary>
        /// Returns the internal buffer to the pool and disposes of the <see cref="RentedBufferWriter{T}"/> instance.
        /// </summary>
        /// <remarks>In the dotnet internal implementation, the BufferWriter instance itself is reused.
        /// We don't do that here since our instance scope will generally be defined (web requests)
        /// and thus disposals will happen less frequently.</remarks>
        public void Dispose()
        {
            foreach (var segment in _segments)
            {
                if (segment.ToArray().Length != 0)
                {
                    _pool.Return(segment.ToArray());
                }
            }

            if (_buffer.Length > 0)
            {
                _pool.Return(_buffer);
            }

            _buffer = Array.Empty<T>();
            _index = 0;
        }
    }
}
