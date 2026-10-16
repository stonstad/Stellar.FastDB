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

        private enum RecordType : byte
        {
            Put = 1,
            Remove = 2,
        }

        private enum PutMode
        {
            Add,
            Update,
            AddOrUpdate,
        }

        [Flags]
        private enum FormatType : byte
        {
            None = 0,
            Encrypted = 1,
            Compressed = 2,
        }
    }
}
