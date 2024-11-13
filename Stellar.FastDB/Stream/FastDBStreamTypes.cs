using System;

namespace Stellar.Collections
{
    internal sealed partial class FastDBStream<TKey, TValue> where TKey : struct
    {
        private struct T
        {
            public TKey K { get; set; }
            public TValue V { get; set; }
        }

        private struct Index
        {
            public int Start;
            public int Length;

            public Index(long start, int length)
            {
                Start = (int)start;
                Length = length;
            }
        }

        private enum BufferOperationType : byte
        {
            None,
            Add,
            Remove,
            Update
        }

        private enum MemoryStateType : byte
        {
            Unallocated = 0,
            Allocated = 1,
            Deleted = 2,
            Pending = 3,
        }

        [Flags]
        private enum FormatType : byte
        {
            None = 0,
            Encrypted = 1,
            Compressed = 2,
        }

        private class QueueEntry
        {
            public BufferOperationType Type;
            public TKey Key;
            public TValue Value;
            public byte[] Bytes;

            public QueueEntry()
            {
            }

            public void Set(BufferOperationType type, TKey key)
            {
                Type = type;
                Key = key;
            }

            public void Set(BufferOperationType type, TKey key, TValue value)
            {
                Type = type;
                Key = key;
                Value = value;
            }

            public void Clear()
            {
                Type = BufferOperationType.None;
                Key = default;
                Value = default;
                Bytes = null;
            }

            public override string ToString()
            {
                return $"{Type} {Key}";
            }
        }
    }
}