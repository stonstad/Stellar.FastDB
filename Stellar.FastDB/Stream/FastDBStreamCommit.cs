using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace Stellar.Collections
{
    internal sealed partial class FastDBStream<TKey, TValue> : IThreadPoolWorkItem where TKey : struct
    {
        // Writers append records to the pending batch under _StreamLock, in the same order they change memory. One
        // committer at a time, holding _CommitLock, swaps the pending batch out and writes it, so writers that arrive
        // during a write share the next one. A writer learns how its write went from its sequence number: every record
        // up to _CommittedSequence is written, except those in a range in _Failures.
        private sealed class Batch
        {
            public byte[] Buffer = new byte[64 * 1024];
            public int Length;
            public readonly List<(int Start, int Records)> Frames = new List<(int, int)>();
            public readonly List<(int BlockStart, long Position)> Written = new List<(int, long)>();
            public readonly List<(RecordType Type, TKey Key, int Offset, int Length)> Records = new List<(RecordType, TKey, int, int)>();
            public long LastSequence;
            public long Bytes;
            public int Operations;                  // writes that will check how this batch went

            public void Reset()
            {
                Length = 0;
                Frames.Clear();
                Written.Clear();
                Records.Clear();
                Bytes = 0;
                Operations = 0;
            }
        }

        // a batch that failed to write, kept until every write in it has checked
        private sealed class Failure
        {
            public long First;
            public long Last;
            public Exception Error;
            public int Readers;
        }

        // what an async writer awaits while another thread writes its batch; reused once awaited
        private sealed class CommitWaiter : IValueTaskSource<bool>, IValueTaskSource
        {
            private readonly FastDBStream<TKey, TValue> _Stream;
            private ManualResetValueTaskSourceCore<bool> _Core;
            public long Sequence;

            public CommitWaiter(FastDBStream<TKey, TValue> stream)
            {
                _Stream = stream;
                _Core.RunContinuationsAsynchronously = true;
            }

            public short Version => _Core.Version;

            public void SetResult(bool succeeded)
            {
                _Core.SetResult(succeeded);
            }

            public void SetException(Exception error)
            {
                _Core.SetException(error);
            }

            public bool GetResult(short token)
            {
                try
                {
                    return _Core.GetResult(token);
                }
                finally
                {
                    _Core.Reset();
                    _Stream.ReturnWaiter(this);
                }
            }

            void IValueTaskSource.GetResult(short token)
            {
                GetResult(token);
            }

            public ValueTaskSourceStatus GetStatus(short token)
            {
                return _Core.GetStatus(token);
            }

            public void OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags)
            {
                _Core.OnCompleted(continuation, state, token, flags);
            }
        }

        private readonly object _CommitLock = new object();
        private Batch _Pending = new Batch();
        private Batch _Writing = new Batch();
        private long _Sequence;                 // last record appended
        private long _CommittedSequence;        // last record written, or discarded by Clear()
        private long _PendingBytes;             // appended and not yet written
        private int _CommitPassScheduled;
        private Exception _UnreportedError;     // a failed buffered write, reported by the next Flush()
        private TaskCompletionSource _SpaceFreed;   // created only when a buffered writer waits for space

        private readonly List<Failure> _Failures = new List<Failure>();
        private int _FailureCount;              // read without the lock, so successful writes never take it

        private readonly object _WaiterLock = new object();
        private readonly List<CommitWaiter> _Waiters = new List<CommitWaiter>();
        private readonly Stack<CommitWaiter> _IdleWaiters = new Stack<CommitWaiter>();

        // caller holds _StreamLock
        private void Append(RecordType type, TKey key, ReadOnlySpan<byte> content)
        {
            Batch batch = _Pending;
            ulong header = RecordHeader(type, content.Length);
            int size = VarintLength(header) + content.Length;

            int frame = batch.Frames.Count - 1;
            if (frame < 0 || (batch.Frames[frame].Records > 0 && batch.Length - batch.Frames[frame].Start - BlockHeaderReserve + size > MaxBlockSize))
            {
                Reserve(batch, BlockHeaderReserve);
                batch.Frames.Add((batch.Length, 0));
                batch.Length += BlockHeaderReserve;
                frame++;
            }

            Reserve(batch, size);
            batch.Length += WriteVarint(batch.Buffer, batch.Length, header);
            batch.Records.Add((type, key, batch.Length, content.Length));
            content.CopyTo(batch.Buffer.AsSpan(batch.Length));
            batch.Length += content.Length;

            batch.Frames[frame] = (batch.Frames[frame].Start, batch.Frames[frame].Records + 1);
            batch.Bytes += size;
            batch.LastSequence = ++_Sequence;
        }

        // caller holds _StreamLock and has appended a write's records: counts their bytes, pending since the batch held
        // bytesBefore, toward the backlog in one step rather than one per record, and the write toward the batch's
        // writers; returns the write's last sequence
        private long EndWrite(long bytesBefore)
        {
            Interlocked.Add(ref _PendingBytes, _Pending.Bytes - bytesBefore);
            _Pending.Operations++;
            return _Sequence;
        }

        private static void Reserve(Batch batch, int count)
        {
            if (batch.Length + count > batch.Buffer.Length)
                Array.Resize(ref batch.Buffer, Math.Max(batch.Length + count, batch.Buffer.Length * 2));
        }

        // caller holds _CommitLock; returns false if nothing was pending
        private bool CommitPending()
        {
            Batch batch;
            lock (_StreamLock)
            {
                if (_Pending.Records.Count == 0)
                    return false;

                batch = _Pending;
                _Pending = _Writing;
                _Writing = batch;
            }

            long fileLength = _FileLength;
            try
            {
                for (int i = 0; i < batch.Frames.Count; i++)
                {
                    int end = batch.Length;
                    if (i + 1 < batch.Frames.Count)
                        end = batch.Frames[i + 1].Start;

                    int blockStart = PrepareFrame(batch.Buffer, batch.Frames[i].Start, end, batch.Frames[i].Records);
#if FASTDB_TEST_HOOKS
                    if (FastDBTestHooks.FailWrite != null && FastDBTestHooks.FailWrite())
                        throw new IOException("Simulated write failure.");
#endif
                    RandomAccess.Write(_Handle, batch.Buffer.AsSpan(blockStart, end - blockStart), _FileLength);
                    batch.Written.Add((blockStart, _FileLength));
                    _FileLength += end - blockStart;
                }

                if (Options.Durability == DurabilityMode.Synced)
                    FlushToDisk();

                // index the records only once every block is written, so a failed write leaves no entries behind
                long deadBefore = _DeadBytes;
                int record = 0;
                for (int i = 0; i < batch.Frames.Count; i++)
                {
                    (int blockStart, long blockPosition) = batch.Written[i];
                    for (int r = 0; r < batch.Frames[i].Records; r++, record++)
                    {
                        (RecordType type, TKey key, int offset, int length) = batch.Records[record];
                        long position = blockPosition + (offset - blockStart);
                        if (type == RecordType.Put)
                            TrackPut(key, position, length, offset - blockStart);
                        else
                            TrackRemove(key, position, length, offset - blockStart);
                    }
                }

                OnCommitted(_DeadBytes - deadBefore);
            }
            catch (Exception ex)
            {
                if (Options.Durability == DurabilityMode.Buffered)
                    _UnreportedError ??= ex;
                else
                    AddFailure(batch, ex);
                Truncate(fileLength);
            }

            // a failure is recorded before its sequence is published, so a writer that sees the sequence sees it
            Volatile.Write(ref _CommittedSequence, batch.LastSequence);
            Interlocked.Add(ref _PendingBytes, -batch.Bytes);
            batch.Reset();

            CompleteWaiters();
            if (Options.Durability == DurabilityMode.Buffered)
                SignalSpaceFreed();
            return true;
        }

        // writes the block header and crc in front of the records in buffer[start..end), returning where the block starts
        private static int PrepareFrame(byte[] buffer, int start, int end, int records)
        {
            int payloadStart = start + BlockHeaderReserve;
            ulong header;
            if (records == 1)
            {
                // one record: its type moves into the block header and its own header is dropped
                TryReadVarint(buffer.AsSpan(payloadStart, end - payloadStart), out ulong recordHeader, out int recordHeaderLength);
                payloadStart += recordHeaderLength;
                header = ((recordHeader >> 1) << 2) | ((recordHeader & 1) << 1);
            }
            else
                header = ((ulong)(end - payloadStart) << 1) | 1;

            int blockStart = payloadStart - VarintLength(header) - CrcSize;
            WriteVarint(buffer, blockStart + CrcSize, header);
            Span<byte> block = buffer.AsSpan(blockStart, end - blockStart);
            BinaryPrimitives.WriteUInt32LittleEndian(block, Crc32.HashToUInt32(block.Slice(CrcSize)));
            return blockStart;
        }

        private void FlushToDisk()
        {
#if FASTDB_TEST_HOOKS
            long started = Stopwatch.GetTimestamp();
#endif
            // _FlushHandle on Windows; elsewhere it's null and fsync on _Handle doesn't hold up other operations
            RandomAccess.FlushToDisk(_FlushHandle ?? _Handle);
#if FASTDB_TEST_HOOKS
            Interlocked.Increment(ref FastDBTestHooks.Flushes);
            Interlocked.Add(ref FastDBTestHooks.FlushTicks, Stopwatch.GetTimestamp() - started);
#endif
        }

        private void Truncate(long fileLength)
        {
            if (_FileLength == fileLength)
                return;

            _FileLength = fileLength;
            try
            {
                RandomAccess.SetLength(_Handle, fileLength);
            }
            catch
            {
                // load stops at the first bad block, so leftover bytes are ignored
            }
        }

        // caller holds _CommitLock
        private void AddFailure(Batch batch, Exception error)
        {
            if (batch.Operations == 0)
                return;

            lock (_Failures)
            {
                _Failures.Add(new Failure()
                {
                    First = batch.LastSequence - batch.Records.Count + 1,
                    Last = batch.LastSequence,
                    Error = error,
                    Readers = batch.Operations,
                });
                Volatile.Write(ref _FailureCount, _Failures.Count);
            }
        }

        // each write that isn't buffered calls this once, after its last record's sequence is committed
        private bool Succeeded(long sequence)
        {
            if (Volatile.Read(ref _FailureCount) == 0)
                return true;

            Exception error = null;
            lock (_Failures)
            {
                for (int i = 0; i < _Failures.Count; i++)
                {
                    Failure failure = _Failures[i];
                    if (sequence < failure.First || sequence > failure.Last)
                        continue;

                    error = failure.Error;
                    if (--failure.Readers == 0)
                    {
                        _Failures.RemoveAt(i);
                        Volatile.Write(ref _FailureCount, _Failures.Count);
                    }
                    break;
                }
            }

            if (error == null)
                return true;
            if (Options.StorageFailureBehavior == ErrorBehaviorType.Exception)
                throw ThrowHelper.WriteFailed(FilePath, error);
            return false;
        }

        // waits until the record with this sequence is written, writing the pending batch if no one else is
        private bool Commit(long sequence)
        {
            lock (_CommitLock)
            {
                if (_CommittedSequence < sequence)
                    CommitPending();
            }
            ScheduleCommit();
            return Succeeded(sequence);
        }

        private bool Complete(long sequence)
        {
            if (sequence == 0)
                return true;

            if (Options.Durability == DurabilityMode.Buffered)
            {
                ScheduleCommit();
                return true;
            }
            return Commit(sequence);
        }

        // returns null once the write with this sequence is done, with its result in succeeded, or a waiter to await
        // while another thread writes it
        private CommitWaiter CompleteOrWait(long sequence, out bool succeeded)
        {
            succeeded = true;
            if (sequence == 0)
                return null;

            if (Options.Durability == DurabilityMode.Buffered)
            {
                ScheduleCommit();
                return null;
            }

            if (Monitor.TryEnter(_CommitLock))
            {
                try
                {
                    if (_CommittedSequence < sequence)
                        CommitPending();
                }
                finally
                {
                    Monitor.Exit(_CommitLock);
                }
                ScheduleCommit();
            }
            else
            {
                // whoever holds _CommitLock schedules a commit for anything still pending when it releases
                lock (_WaiterLock)
                {
                    if (Volatile.Read(ref _CommittedSequence) < sequence)
                    {
                        if (!_IdleWaiters.TryPop(out CommitWaiter waiter))
                            waiter = new CommitWaiter(this);
                        waiter.Sequence = sequence;
                        _Waiters.Add(waiter);
                        return waiter;
                    }
                }
            }

            succeeded = Succeeded(sequence);
            return null;
        }

        // CompleteOrWait for a write that returns ValueTask<bool>
        private ValueTask<bool> CompleteAsync(long sequence)
        {
            CommitWaiter waiter = CompleteOrWait(sequence, out bool succeeded);
            if (waiter == null)
                return new ValueTask<bool>(succeeded);
            return new ValueTask<bool>(waiter, waiter.Version);
        }

        // caller holds _CommitLock: completes the async writers whose records are now written
        private void CompleteWaiters()
        {
            lock (_WaiterLock)
            {
                long committed = _CommittedSequence;
                for (int i = _Waiters.Count - 1; i >= 0; i--)
                {
                    CommitWaiter waiter = _Waiters[i];
                    if (waiter.Sequence > committed)
                        continue;

                    _Waiters[i] = _Waiters[_Waiters.Count - 1];
                    _Waiters.RemoveAt(_Waiters.Count - 1);

                    bool succeeded;
                    try
                    {
                        succeeded = Succeeded(waiter.Sequence);
                    }
                    catch (Exception ex)
                    {
                        waiter.SetException(ex);
                        continue;
                    }
                    waiter.SetResult(succeeded);
                }
            }
        }

        private void ReturnWaiter(CommitWaiter waiter)
        {
            lock (_WaiterLock)
                _IdleWaiters.Push(waiter);
        }

        private void ScheduleCommit()
        {
            // the fence keeps the caller's last store (releasing _CommitLock, or clearing _CommitPassScheduled) ahead of
            // the read, so either this sees a writer's append or the writer sees that store. Unlike Interlocked.Read, it
            // doesn't take _PendingBytes' cache line from the other writers.
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref _PendingBytes) > 0 && Interlocked.CompareExchange(ref _CommitPassScheduled, 1, 0) == 0)
                ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        }

        void IThreadPoolWorkItem.Execute()
        {
            CommitPass();
        }

        private void CommitPass()
        {
            lock (_CommitLock)
            {
                while (_Handle != null && CommitPending())
                {
                }
            }
            Volatile.Write(ref _CommitPassScheduled, 0);
            ScheduleCommit();
        }

        private bool HasBufferSpace()
        {
            return Volatile.Read(ref _PendingBytes) < Options.MaxBufferedBytes;
        }

        // a buffered write waits for the backlog to drain below MaxBufferedBytes before it serializes
        private bool MustWaitForBufferSpace => _Handle != null && Options.Durability == DurabilityMode.Buffered && !HasBufferSpace();

        private void WaitForBufferSpace()
        {
            while (!HasBufferSpace())
            {
                Task freed = SpaceFreed();
                if (HasBufferSpace())
                    return;
                ScheduleCommit();
                freed.Wait();
            }
        }

        private async Task WaitForBufferSpaceAsync()
        {
            while (!HasBufferSpace())
            {
                Task freed = SpaceFreed();
                if (HasBufferSpace())
                    return;
                ScheduleCommit();
                await freed.ConfigureAwait(false);
            }
        }

        // completes when the next commit frees space; created only when a writer waits for it
        private Task SpaceFreed()
        {
            TaskCompletionSource signal = Volatile.Read(ref _SpaceFreed);
            if (signal == null)
            {
                TaskCompletionSource created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                signal = Interlocked.CompareExchange(ref _SpaceFreed, created, null) ?? created;
            }
            return signal.Task;
        }

        private void SignalSpaceFreed()
        {
            Interlocked.Exchange(ref _SpaceFreed, null)?.TrySetResult();
        }
    }
}
