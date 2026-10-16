using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Stellar.Collections
{
    internal sealed partial class FastDBStream<TKey, TValue> where TKey : struct
    {
        // One entry per record in the file, in file order, so compaction can tell live records from dead ones and copy
        // their bytes without deserializing anything.
        private enum EntryKind : byte
        {
            Live,
            Dead,
            Tombstone,
        }

        private struct Entry
        {
            public long Position;       // file offset of the record's content
            public int Length;          // content length
            public int BlockOffset;     // bytes from the start of the record's block to its content
            public TKey Key;
            public EntryKind Kind;
        }

        private struct KeyInfo
        {
            public int Entry;           // the key's live entry, or -1 once the key is removed
            public int DeadRecords;     // the key's dead records still in the file
        }

        // caller holds _CommitLock, or is loading
        private List<Entry> _Entries = new List<Entry>();
        private Dictionary<TKey, KeyInfo> _Keys = new Dictionary<TKey, KeyInfo>();
        private long _LiveBytes;
        private long _DeadBytes;
        private int _ReclaimableEntries;    // dead entries and tombstones

        private void TrackPut(TKey key, long position, int length, int blockOffset)
        {
            int index = _Entries.Count;
            _Entries.Add(new Entry() { Position = position, Length = length, BlockOffset = blockOffset, Key = key, Kind = EntryKind.Live });
            _LiveBytes += RecordSize(RecordType.Put, length);

            ref KeyInfo info = ref CollectionsMarshal.GetValueRefOrAddDefault(_Keys, key, out bool exists);
            if (exists && info.Entry >= 0)
            {
                MarkDead(info.Entry);
                info.DeadRecords++;
            }
            info.Entry = index;
        }

        private void TrackRemove(TKey key, long position, int length, int blockOffset)
        {
            _Entries.Add(new Entry() { Position = position, Length = length, BlockOffset = blockOffset, Key = key, Kind = EntryKind.Tombstone });
            _DeadBytes += RecordSize(RecordType.Remove, length);
            _ReclaimableEntries++;

            ref KeyInfo info = ref CollectionsMarshal.GetValueRefOrNullRef(_Keys, key);
            if (!Unsafe.IsNullRef(ref info) && info.Entry >= 0)
            {
                MarkDead(info.Entry);
                info.DeadRecords++;
                info.Entry = -1;
            }
        }

        private void MarkDead(int index)
        {
            ref Entry entry = ref CollectionsMarshal.AsSpan(_Entries)[index];
            entry.Kind = EntryKind.Dead;

            int size = RecordSize(RecordType.Put, entry.Length);
            _LiveBytes -= size;
            _DeadBytes += size;
            _ReclaimableEntries++;
        }

        private void ResetIndex()
        {
            _Entries = new List<Entry>();
            _Keys = new Dictionary<TKey, KeyInfo>();
            _LiveBytes = 0;
            _DeadBytes = 0;
            _ReclaimableEntries = 0;
        }
    }
}
