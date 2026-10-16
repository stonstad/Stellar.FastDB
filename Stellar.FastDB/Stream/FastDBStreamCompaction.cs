using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Stellar.Collections
{
    internal sealed partial class FastDBStream<TKey, TValue> where TKey : struct
    {
        // A sweep moves live records down into a gap that travels from the first dead record to the end of the file,
        // swallowing dead records on its way; when the gap reaches the end, the file is cut. Entries [0, _SweepWrite)
        // are the records before the gap, and entries from _SweepRead on are the records after it.
        //
        // One step runs at a time, holding _SweepLock. A step holds _CommitLock only for its in-memory work and plain
        // writes, and flushes outside it, so writers never wait on a flush. Lock order: _SweepLock, _CommitLock,
        // _StreamLock.
        private const int MaxSweepStep = 1024 * 1024;       // most bytes one step writes, to the gap or to the end
        private const int MaxSweepRead = 4 * 1024 * 1024;   // most bytes of blocks one step reads
        private const int MinSweepGap = 64 * 1024;          // a smaller gap is grown by moving records to the end instead
        private const int MinSweepBudget = 64 * 1024;       // background sweeping starts once it may move this much
        private const int MaxPassesPerCompact = 3;

        private readonly object _SweepLock = new object();
        private bool _Sweeping;
        private int _SweepWrite;
        private int _SweepRead;
        private bool _GapFlushPending;          // a header slot written since the last flush
        private long _SweepBudget;              // live bytes background sweeping may still move
        private int _SweeperScheduled;

        // Budget is only earned by writes, so when writing pauses, sweeping would stop wherever its budget ran out, often
        // partway through a pass with the gap still inside the file. The idle timer finishes the job instead.
        private long _Commits;                  // commits so far, so idle compaction can tell that writing resumed
        private long _LastCommitTime;           // Environment.TickCount64 at the last commit
        private Timer _IdleTimer;
        private int _IdleCheckArmed;

        // FastDBOptions.CompactionIdleDelay in ms, kept within what a Timer accepts
        private long IdleCompactionDelay => (long)Math.Clamp(Options.CompactionIdleDelay.TotalMilliseconds, 0, int.MaxValue);

        private byte[] _SweepInput = new byte[64 * 1024];
        private byte[] _SweepGapOutput = new byte[64 * 1024];
        private byte[] _SweepTailOutput = new byte[64 * 1024];
        private readonly List<(int Entry, int Offset, bool ToGap)> _SweepKept = new List<(int, int, bool)>();
        private readonly List<int> _SweepCopies = new List<int>();
        private readonly List<(TKey Key, EntryKind Kind, int Length)> _SweepDropped = new List<(TKey, EntryKind, int)>();
        private readonly List<Entry> _SweepOriginals = new List<Entry>();

        // the step in progress
        private long _StepConsumedTo;       // end of the blocks the step consumes
        private int _StepNextEntry;         // first entry after them
        private int _StepGapLength;         // planned output for the gap, in _SweepGapOutput
        private int _StepTailLength;        // planned output for the end of the file, in _SweepTailOutput
        private int _StepGapBlockStart;     // where the gap's block starts in _SweepGapOutput, once written
        private long _StepMoved;            // bytes written into the gap
        private long _StepTailBytes;        // bytes written at the end of the file

        public void Compact()
        {
            if (_Handle == null || Options.IsReadOnlyEnabled)
                return;

            lock (_SweepLock)
            {
                int passes = MaxPassesPerCompact;
                while (SweepStep(ref passes))
                {
                }

                lock (_CommitLock)
                    _SweepBudget = 0;
            }

            // a writer that appended while this held _CommitLock is waiting for someone to commit it
            ScheduleCommit();
        }

        public Task CompactAsync()
        {
            return Task.Run(() => Compact());
        }

        // after load, before the collection is used: finish an interrupted sweep and reclaim everything
        private void CompactAtOpen()
        {
            if (Options.IsReadOnlyEnabled)
                return;

            lock (_SweepLock)
            {
                if (_GapSize > 0 && !_Sweeping)
                {
                    // a sweep that reached the end cut the file before it cleared the gap
                    WriteGap(0, 0);
                    _GapFlushPending = true;
                }

                int passes = MaxPassesPerCompact;
                while (SweepStep(ref passes))
                {
                }
            }
        }

        // caller holds _CommitLock: earns background sweeping a budget for the bytes a commit made dead, and starts it
        private void OnCommitted(long deadBytes)
        {
            double effort = Options.CompactionEffortValue;
            if (effort <= 0 || Options.IsReadOnlyEnabled)
                return;

            Volatile.Write(ref _Commits, _Commits + 1);
            Volatile.Write(ref _LastCommitTime, Environment.TickCount64);

            if (effort < 1 && deadBytes > 0)
                _SweepBudget += (long)(deadBytes * effort / (1 - effort));

            if (ShouldSweep(MinSweepBudget))
                ScheduleSweeper();

            if (effort < 1 && (_Sweeping || _ReclaimableEntries > 0))
                ArmIdleCheck(IdleCompactionDelay);
        }

        private void ArmIdleCheck(long delay)
        {
            if (Interlocked.CompareExchange(ref _IdleCheckArmed, 1, 0) != 0)
                return;

            try
            {
                _IdleTimer ??= new Timer(static state => ((FastDBStream<TKey, TValue>)state).IdleCheck(), this, Timeout.Infinite, Timeout.Infinite);
                _IdleTimer.Change(delay, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // the collection is closing
            }
        }

        // on the idle timer: once no commit has happened for IdleCompactionDelay, compacts until no garbage is left or a
        // commit happens, without spending the budget writers earn
        private void IdleCheck()
        {
            Volatile.Write(ref _IdleCheckArmed, 0);
            try
            {
                long idle = Environment.TickCount64 - Volatile.Read(ref _LastCommitTime);
                if (idle < IdleCompactionDelay)
                {
                    ArmIdleCheck(IdleCompactionDelay - idle);
                    return;
                }

                long commits = Volatile.Read(ref _Commits);
                int passes = MaxPassesPerCompact;
                while (true)
                {
                    lock (_SweepLock)
                    {
                        if (_Handle == null || Volatile.Read(ref _Commits) != commits || !SweepStep(ref passes))
                            break;
                    }
                }

                lock (_CommitLock)
                {
                    if (_SweepBudget < 0)
                        _SweepBudget = 0;
                }
            }
            catch
            {
                // a failed step leaves the file and the index consistent; the next commit arms the check again
            }
        }

        private bool ShouldSweep(long minimumBudget)
        {
            double effort = Options.CompactionEffortValue;
            if (effort <= 0 || _Handle == null || (!_Sweeping && _ReclaimableEntries == 0))
                return false;
            return effort >= 1 || Interlocked.Read(ref _SweepBudget) >= minimumBudget;
        }

        private void ScheduleSweeper()
        {
            if (Interlocked.CompareExchange(ref _SweeperScheduled, 1, 0) == 0)
                ThreadPool.UnsafeQueueUserWorkItem(static state => ((FastDBStream<TKey, TValue>)state).SweepInBackground(), this);
        }

        private void SweepInBackground()
        {
            try
            {
                while (true)
                {
                    lock (_SweepLock)
                    {
                        if (!ShouldSweep(1))
                            break;

                        int passes = 1;
                        if (!SweepStep(ref passes))
                            break;
                    }
                }
            }
            catch
            {
                // a failed step leaves the file and the index consistent; sweeping resumes after a later commit
            }
            finally
            {
                Volatile.Write(ref _SweeperScheduled, 0);
            }

            // a commit may have earned budget after the last check, while this sweeper was still marked as running
            if (ShouldSweep(MinSweepBudget))
                ScheduleSweeper();
        }

        // caller holds _SweepLock; returns false when there is nothing to do
        private bool SweepStep(ref int passesLeft)
        {
            // the last header written must be on disk before this step overwrites what the one before it points at
            if (_GapFlushPending)
            {
                FlushToDisk();
                _GapFlushPending = false;
            }

            long source;
            long fileLength;
            try
            {
                lock (_CommitLock)
                {
                    if (_Handle == null)
                        return false;

                    if (!_Sweeping)
                    {
                        if (passesLeft <= 0 || !StartPass())
                            return false;
                        passesLeft--;
                    }

                    source = _GapStart + _GapSize;
                    fileLength = _FileLength;
                    if (source >= fileLength)
                    {
                        FinishPass();
                        return true;
                    }
                }
            }
            finally
            {
                ScheduleCommit();
            }

            // writers only append past fileLength, so the blocks after the gap don't change while this reads them
            int chunk = (int)Math.Min(MaxSweepRead, fileLength - source);
            if (_SweepInput.Length < chunk)
                _SweepInput = new byte[chunk];
            if (ReadAt(_SweepInput.AsSpan(0, chunk), source) != chunk)
                throw ThrowHelper.CorruptBlock(FilePath);

            try
            {
                lock (_CommitLock)
                {
                    try
                    {
                        PlanStep(source, chunk);
                        WriteStep();
                    }
                    catch
                    {
                        UndoStep();
                        throw;
                    }
                }
            }
            finally
            {
                ScheduleCommit();
            }

            try
            {
                FlushToDisk();
            }
            catch
            {
                lock (_CommitLock)
                    UndoStep();
                ScheduleCommit();
                throw;
            }

            try
            {
                lock (_CommitLock)
                {
                    int slot;
                    try
                    {
                        slot = WriteGap(_GapStart + _StepMoved, _StepConsumedTo - (_GapStart + _StepMoved));
                    }
                    catch
                    {
                        UndoStep();
                        throw;
                    }

                    _GapFlushPending = true;
                    ApplyStep(slot);
                }
            }
            finally
            {
                ScheduleCommit();
            }
            return true;
        }

        // caller holds _CommitLock
        private bool StartPass()
        {
            if (_ReclaimableEntries == 0)
                return false;

            Span<Entry> entries = CollectionsMarshal.AsSpan(_Entries);
            int first = 0;
            while (first < entries.Length && entries[first].Kind == EntryKind.Live)
                first++;

            if (first == entries.Length)
            {
                _ReclaimableEntries = 0;
                return false;
            }

            // the gap starts at the block holding that record; the block's earlier records are swept with it
            long blockStart = entries[first].Position - entries[first].BlockOffset;
            while (first > 0 && entries[first - 1].Position - entries[first - 1].BlockOffset == blockStart)
                first--;

            _GapStart = blockStart;
            _GapSize = 0;
            _SweepWrite = first;
            _SweepRead = first;
            _Sweeping = true;
            return true;
        }

        // caller holds _CommitLock: the gap reached the end of the file, so cut it off and clear it
        private void FinishPass()
        {
            if (_SweepRead != _Entries.Count)
                throw new InvalidOperationException("The compaction index is out of step with the file.");

            _FileLength = _GapStart;
            RandomAccess.SetLength(_Handle, _FileLength);
            CrashPoint(-1);

            int slot = WriteGap(0, 0);
            _GapFlushPending = true;
            CrashPoint(slot);
            WriteRecordCount();

            _Entries.RemoveRange(_SweepWrite, _Entries.Count - _SweepWrite);
            _Sweeping = false;
        }

        // caller holds _CommitLock: decides what happens to each record in the blocks read into _SweepInput, and copies
        // the records it keeps into the gap's output or the end-of-file output
        private void PlanStep(long source, int chunk)
        {
            // a small gap can't take much, so grow it by moving records to the end of the file instead
            bool grow = _GapSize < MinSweepGap;
            long room = 0;
            if (!grow)
                room = Math.Min(_GapSize, MaxSweepStep);

            _SweepKept.Clear();
            _SweepCopies.Clear();
            _SweepDropped.Clear();
            int gapLength = BlockHeaderReserve;
            int tailLength = BlockHeaderReserve;
            bool anyToGap = false;
            bool anyToTail = false;
            int offset = 0;
            int entry = _SweepRead;

            while (offset < chunk)
            {
                int blockLength = ParseBlock(source, offset, ref chunk, out bool single, out ulong header, out int headerLength);
                if (blockLength == 0)
                    break;

                int payloadStart = offset + CrcSize + headerLength;
                int blockEnd = offset + blockLength;

                // an upper bound on what the block keeps; a tombstone counted here may still turn out droppable
                int keepBound = 0;
                int records = 0;
                for (int position = payloadStart; position < blockEnd; records++)
                {
                    NextRecord(single, header, position, blockEnd, out int contentStart, out int contentLength, out _);
                    if (entry + records >= _Entries.Count)
                        throw new InvalidOperationException("The compaction index is out of step with the file.");
                    if (_Entries[entry + records].Kind != EntryKind.Dead)
                        keepBound += RecordSize(RecordType.Put, contentLength) + 1;
                    position = contentStart + contentLength;
                }

                bool toGap = false;
                if (keepBound > 0)
                {
                    if (!grow && gapLength + keepBound <= room)
                        toGap = true;
                    else if (!grow && anyToGap)
                        break;
                    else if (anyToTail && tailLength + keepBound > MaxSweepStep)
                        break;
                }

                for (int position = payloadStart; position < blockEnd; entry++)
                {
                    NextRecord(single, header, position, blockEnd, out int contentStart, out int contentLength, out RecordType type);
                    position = contentStart + contentLength;

                    Entry current = _Entries[entry];
                    if (current.Position != source + contentStart || current.Length != contentLength)
                        throw new InvalidOperationException("The compaction index is out of step with the file.");

                    if (current.Kind == EntryKind.Dead || (current.Kind == EntryKind.Tombstone && TombstoneDroppable(current.Key)))
                    {
                        _SweepDropped.Add((current.Key, current.Kind, contentLength));
                        if (current.Kind == EntryKind.Dead)
                            AdjustDeadRecords(current.Key, -1);
                        continue;
                    }

                    ulong recordHeader = RecordHeader(type, contentLength);
                    if (toGap)
                    {
                        gapLength += CopyRecord(ref _SweepGapOutput, gapLength, recordHeader, contentStart, contentLength, out int copied);
                        _SweepKept.Add((entry, copied, true));
                        anyToGap = true;
                    }
                    else
                    {
                        tailLength += CopyRecord(ref _SweepTailOutput, tailLength, recordHeader, contentStart, contentLength, out int copied);
                        _SweepKept.Add((entry, copied, false));
                        anyToTail = true;
                    }
                }

                offset = blockEnd;
            }

            _StepConsumedTo = source + offset;
            _StepNextEntry = entry;
            _StepGapLength = gapLength;
            _StepTailLength = tailLength;
            _StepGapBlockStart = 0;
            _StepMoved = 0;
            _StepTailBytes = 0;
        }

        // caller holds _CommitLock: writes the step's blocks without flushing them
        private void WriteStep()
        {
            int gapLength = _StepGapLength;
            int tailLength = _StepTailLength;
            int gapRecords = 0;
            int tailRecords = 0;
            foreach ((_, _, bool toGap) in _SweepKept)
            {
                if (toGap)
                    gapRecords++;
                else
                    tailRecords++;
            }

            // to the end of the file first: it touches nothing any header points at. The copies are indexed right away,
            // as dead duplicates of the originals, so the index matches the file even if the step goes no further.
            if (tailRecords > 0)
            {
                int blockStart = PrepareFrame(_SweepTailOutput, 0, tailLength, tailRecords);
                long position = _FileLength;
                RandomAccess.Write(_Handle, _SweepTailOutput.AsSpan(blockStart, tailLength - blockStart), position);
                _FileLength += tailLength - blockStart;
                _StepTailBytes = tailLength - blockStart;

                foreach ((int index, int copied, bool toGap) in _SweepKept)
                {
                    if (toGap)
                        continue;

                    Entry copy = _Entries[index];
                    copy.Position = position + (copied - blockStart);
                    copy.BlockOffset = copied - blockStart;
                    if (copy.Kind == EntryKind.Live)
                    {
                        copy.Kind = EntryKind.Dead;
                        AdjustDeadRecords(copy.Key, 1);
                        _DeadBytes += RecordSize(RecordType.Put, copy.Length);
                    }
                    else
                        _DeadBytes += RecordSize(RecordType.Remove, copy.Length);
                    _ReclaimableEntries++;

                    _SweepCopies.Add(_Entries.Count);
                    _Entries.Add(copy);
                }
                CrashPoint(-1);
            }

            // into the gap: never more than the gap's size, so the blocks the records came from stay intact
            if (gapRecords > 0)
            {
                int blockStart = PrepareFrame(_SweepGapOutput, 0, gapLength, gapRecords);
                _StepMoved = gapLength - blockStart;
                _StepGapBlockStart = blockStart;
                RandomAccess.Write(_Handle, _SweepGapOutput.AsSpan(blockStart, (int)_StepMoved), _GapStart);
                CrashPoint(-1);
            }
        }

        // caller holds _CommitLock: the step won't complete, so restore the counts the plan changed
        private void UndoStep()
        {
            foreach ((TKey key, EntryKind kind, _) in _SweepDropped)
                if (kind == EntryKind.Dead)
                    AdjustDeadRecords(key, 1);
            _SweepDropped.Clear();
            _SweepKept.Clear();
        }

        // caller holds _CommitLock: the header now records the new gap, so bring the index in line with it. Writers may
        // have killed records since the plan; those are moved as they are now, dead or alive.
        private void ApplyStep(int slot)
        {
            long gapStart = _GapStart - _StepMoved;

            // read every original before writing any back: writing can overwrite slots still to be read
            _SweepOriginals.Clear();
            foreach ((int index, _, _) in _SweepKept)
                _SweepOriginals.Add(_Entries[index]);

            Span<Entry> entries = CollectionsMarshal.AsSpan(_Entries);
            int write = _SweepWrite;
            int copy = 0;
            for (int i = 0; i < _SweepKept.Count; i++)
            {
                (_, int copied, bool toGap) = _SweepKept[i];
                Entry original = _SweepOriginals[i];

                if (toGap)
                {
                    original.Position = gapStart + (copied - _StepGapBlockStart);
                    original.BlockOffset = copied - _StepGapBlockStart;
                    entries[write] = original;
                    if (original.Kind == EntryKind.Live)
                        CollectionsMarshal.GetValueRefOrNullRef(_Keys, original.Key).Entry = write;
                    write++;
                    continue;
                }

                // the copy at the end of the file takes over; the original is swept away
                int copyIndex = _SweepCopies[copy++];
                ref Entry duplicate = ref entries[copyIndex];
                if (original.Kind == EntryKind.Live)
                {
                    duplicate.Kind = EntryKind.Live;
                    AdjustDeadRecords(original.Key, -1);
                    _DeadBytes -= RecordSize(RecordType.Put, original.Length);
                    CollectionsMarshal.GetValueRefOrNullRef(_Keys, original.Key).Entry = copyIndex;
                }
                else if (original.Kind == EntryKind.Dead)
                {
                    AdjustDeadRecords(original.Key, -1);
                    _DeadBytes -= RecordSize(RecordType.Put, original.Length);
                }
                else
                    _DeadBytes -= RecordSize(RecordType.Remove, original.Length);
                _ReclaimableEntries--;
            }

            foreach ((TKey key, EntryKind kind, int length) in _SweepDropped)
            {
                _ReclaimableEntries--;
                if (kind == EntryKind.Tombstone)
                {
                    _DeadBytes -= RecordSize(RecordType.Remove, length);
                    continue;
                }

                _DeadBytes -= RecordSize(RecordType.Put, length);
                ref KeyInfo info = ref CollectionsMarshal.GetValueRefOrNullRef(_Keys, key);
                if (!Unsafe.IsNullRef(ref info) && info.Entry < 0 && info.DeadRecords == 0)
                    _Keys.Remove(key);
            }

            _SweepWrite = write;
            _SweepRead = _StepNextEntry;
            _SweepBudget -= _StepMoved + _StepTailBytes;
            _SweepKept.Clear();
            _SweepDropped.Clear();

            CrashPoint(slot);
        }

        // returns the length of the block at offset in _SweepInput, reading more if it is the step's first block and
        // runs past the chunk; returns 0 if a later block runs past the chunk
        private int ParseBlock(long source, int offset, ref int chunk, out bool single, out ulong header, out int headerLength)
        {
            single = false;
            header = 0;
            headerLength = 0;

            int available = chunk - offset;
            if (available < CrcSize + 1 || !TryReadVarint(_SweepInput.AsSpan(offset + CrcSize, Math.Min(MaxVarintLength, available - CrcSize)), out header, out headerLength))
            {
                if (offset > 0)
                    return 0;
                throw ThrowHelper.CorruptBlock(FilePath);
            }

            single = (header & 1) == 0;
            ulong payloadLength = single ? header >> 2 : header >> 1;
            long remaining = _FileLength - source - offset;
            if (payloadLength == 0 || (long)payloadLength > remaining - CrcSize - headerLength)
                throw ThrowHelper.CorruptBlock(FilePath);

            int blockLength = CrcSize + headerLength + (int)payloadLength;
            if (offset + blockLength > chunk)
            {
                if (offset > 0)
                    return 0;

                _SweepInput = new byte[blockLength];
                if (ReadAt(_SweepInput.AsSpan(0, blockLength), source) != blockLength)
                    throw ThrowHelper.CorruptBlock(FilePath);
                chunk = blockLength;
            }

            if (Crc32.HashToUInt32(_SweepInput.AsSpan(offset + CrcSize, blockLength - CrcSize)) != BinaryPrimitives.ReadUInt32LittleEndian(_SweepInput.AsSpan(offset)))
                throw ThrowHelper.CorruptBlock(FilePath);
            return blockLength;
        }

        // finds the record at position in a block of _SweepInput
        private void NextRecord(bool single, ulong header, int position, int blockEnd, out int contentStart, out int contentLength, out RecordType type)
        {
            if (single)
            {
                contentStart = position;
                contentLength = blockEnd - position;
                type = (header & 2) == 0 ? RecordType.Put : RecordType.Remove;
                return;
            }

            if (!TryReadVarint(_SweepInput.AsSpan(position, blockEnd - position), out ulong recordHeader, out int recordHeaderLength))
                throw ThrowHelper.CorruptBlock(FilePath);

            contentStart = position + recordHeaderLength;
            contentLength = (int)(recordHeader >> 1);
            type = (recordHeader & 1) == 0 ? RecordType.Put : RecordType.Remove;
            if (contentLength <= 0 || contentStart + contentLength > blockEnd)
                throw ThrowHelper.CorruptBlock(FilePath);
        }

        // appends a record from _SweepInput to output, returning the bytes written; copied is where its content landed
        private int CopyRecord(ref byte[] output, int length, ulong recordHeader, int contentStart, int contentLength, out int copied)
        {
            int size = VarintLength(recordHeader) + contentLength;
            if (length + size > output.Length)
                Array.Resize(ref output, Math.Max(length + size, output.Length * 2));

            copied = length + WriteVarint(output, length, recordHeader);
            Buffer.BlockCopy(_SweepInput, contentStart, output, copied, contentLength);
            return size;
        }

        private bool TombstoneDroppable(TKey key)
        {
            // safe to drop once nothing it cancels is left in the file, or the key has a newer live record
            ref KeyInfo info = ref CollectionsMarshal.GetValueRefOrNullRef(_Keys, key);
            return Unsafe.IsNullRef(ref info) || info.Entry >= 0 || info.DeadRecords == 0;
        }

        private void AdjustDeadRecords(TKey key, int change)
        {
            ref KeyInfo info = ref CollectionsMarshal.GetValueRefOrNullRef(_Keys, key);
            if (!Unsafe.IsNullRef(ref info))
                info.DeadRecords += change;
        }

        // gives tests a copy of the file at points where a crash could interrupt compaction; calls to it are compiled
        // out without FASTDB_TEST_HOOKS
        [Conditional("FASTDB_TEST_HOOKS")]
        private void CrashPoint(int slotOffset)
        {
#if FASTDB_TEST_HOOKS
            Action<byte[], int> hook = FastDBTestHooks.CrashPoint;
            if (hook == null)
                return;

            byte[] file = new byte[RandomAccess.GetLength(_Handle)];
            ReadAt(file, 0);
            hook(file, slotOffset);
#endif
        }
    }
}
