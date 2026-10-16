using System;
using System.Buffers;
using System.Text.Json;

namespace Stellar.Collections
{
    /// <summary>
    /// Receives serialized records in an array rented from ArrayPool&lt;byte&gt;.Shared, which Release() returns.
    /// Each thread reuses one writer, so serializing allocates nothing once the pool is warm.
    /// </summary>
    internal sealed class PooledBufferWriter : IBufferWriter<byte>
    {
        [ThreadStatic]
        private static PooledBufferWriter t_Idle;

        private byte[] _Buffer = Array.Empty<byte>();
        private int _Length;
        private Utf8JsonWriter _Json;

        public int Length => _Length;

        public ReadOnlySpan<byte> WrittenSpan => _Buffer.AsSpan(0, _Length);

        // a writer acquired while this thread's writer is in use gets a new one
        public static PooledBufferWriter Acquire()
        {
            PooledBufferWriter writer = t_Idle;
            if (writer == null)
                return new PooledBufferWriter();

            t_Idle = null;
            return writer;
        }

        // returns the array and makes this the thread's writer again
        public void Release()
        {
            Clear();
            t_Idle = this;
        }

        // returns the array, for a writer that didn't come from Acquire()
        public void Clear()
        {
            if (_Buffer.Length > 0)
                ArrayPool<byte>.Shared.Return(_Buffer);
            _Buffer = Array.Empty<byte>();
            _Length = 0;
        }

        // the bytes at [start, start + length), which may run past what's written into space GetSpan() reserved
        public Span<byte> GetWrittenSpan(int start, int length)
        {
            return _Buffer.AsSpan(start, length);
        }

        public void Advance(int count)
        {
            _Length += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Reserve(sizeHint);
            return _Buffer.AsMemory(_Length);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Reserve(sizeHint);
            return _Buffer.AsSpan(_Length);
        }

        public void Truncate(int length)
        {
            _Length = length;
        }

        // the JSON writer is kept with the thread's writer, and replaced only when the options it needs change
        public Utf8JsonWriter GetJsonWriter(JsonWriterOptions options)
        {
            if (_Json == null || _Json.Options.Encoder != options.Encoder || _Json.Options.Indented != options.Indented)
                _Json = new Utf8JsonWriter(this, options);
            else
                _Json.Reset(this);
            return _Json;
        }

        private void Reserve(int sizeHint)
        {
            if (sizeHint < 1)
                sizeHint = 1;
            if (_Buffer.Length - _Length >= sizeHint)
                return;

            byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(_Length + sizeHint, Math.Max(_Buffer.Length * 2, 256)));
            _Buffer.AsSpan(0, _Length).CopyTo(buffer);
            if (_Buffer.Length > 0)
                ArrayPool<byte>.Shared.Return(_Buffer);
            _Buffer = buffer;
        }
    }
}
