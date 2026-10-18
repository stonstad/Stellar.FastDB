using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Stellar.Collections
{
    internal sealed partial class FastDBStream<TKey, TValue> where TKey : struct
    {
        // Loading checks blocks in file order on the calling thread and gathers their records into batches. Batches are
        // deserialized on the thread pool, each with its own decryptor, and applied to the index in file order.
        private const int MaxLoadBatchBytes = 1024 * 1024;

        private sealed class LoadBatch
        {
            public LoadRecord[] Records;
            public int Count;
            public byte[] Data;                     // the records' content, back to back
            public int Length;
            public byte[] Plain;                    // one record's decrypted content
            public Aes Decryptor;                   // decryptors aren't thread safe, so each batch has its own
            public byte[] IV;
            public Task Decoding;
            public int Failed;                      // records before this one are applied before Error is thrown
            public ExceptionDispatchInfo Error;
        }

        // what loading could read of a record that failed to deserialize, when DeserializationFailureBehavior lets it
        // carry on
        private enum LoadOutcome : byte
        {
            Read,
            KeyOnly,                                // the value failed, but the key could be read on its own
            Unreadable,                             // not even the key could be read
        }

        private struct LoadRecord
        {
            public long Position;                   // file offset of the content
            public int Offset;                      // offset of the content in Data
            public int Length;
            public int BlockOffset;
            public RecordType Type;
            public LoadOutcome Outcome;
            public TKey Key;
            public TValue Value;
        }

        private void LoadRecords()
        {
            long fileLength = RandomAccess.GetLength(_Handle);

            // the count from the last close or sweep pass; a file cut since then can't hold that many records
            int capacity = _RecordCount;
            if (capacity > (fileLength - HeaderSize) / 2)
                capacity = 0;
            // headroom, as a ConcurrentDictionary grows once any of its locks covers more than its share of entries
            _Values = new ConcurrentDictionary<TKey, TValue>(Environment.ProcessorCount, capacity + capacity / 8);
            _Entries.EnsureCapacity(capacity);
            _Keys.EnsureCapacity(capacity);

            byte[] buffer = new byte[ReadBufferSize];
            int start = 0;                      // next unparsed byte in buffer
            int end = 0;                        // end of the bytes read into buffer
            long readPosition = HeaderSize;     // file offset of buffer[end]
            long position = HeaderSize;         // file offset of buffer[start]

            // a sweep interrupted by a crash leaves a gap; a sweep that reached the end may have truncated the file
            // before clearing the gap from the header
            bool gapAhead = _GapSize > 0;
            long gapEnd = 0;
            if (gapAhead)
            {
                if (_GapStart < HeaderSize)
                    throw ThrowHelper.CorruptBlock(FilePath);
                if (_GapStart >= fileLength)
                    gapAhead = false; // the file was already cut at or before the gap
                else
                    gapEnd = Math.Min(_GapStart + _GapSize, fileLength);
            }

            // every record takes at least 2 bytes, so a small file gets small batches
            int batchSize = (int)Math.Min(Math.Max(1, Options.LoadBatchSize), Math.Max(1, (fileLength - HeaderSize) / 2));
            int batchBytes = (int)Math.Min(MaxLoadBatchBytes, Math.Max(1, fileLength - HeaderSize));
            LoadBatch[] batches = new LoadBatch[Math.Max(2, Environment.ProcessorCount * 2)];
            int first = 0;                      // oldest batch being decoded
            int decoding = 0;                   // batches being decoded, from first on
            LoadBatch batch = null;             // batch being filled, after them

            try
            {
                while (true)
                {
                    if (gapAhead && position == _GapStart)
                    {
                        // the sweep resumes from the entries before the gap, so they're indexed first
                        ApplyAll();

                        position = gapEnd;
                        readPosition = gapEnd;
                        start = 0;
                        end = 0;
                        gapAhead = false;

                        // resume the interrupted sweep: entries so far are before the gap, the rest come after it
                        _GapSize = gapEnd - _GapStart;
                        _Sweeping = true;
                        _SweepWrite = _Entries.Count;
                        _SweepRead = _Entries.Count;
                    }

                    // blocks before the gap must end at the gap
                    long limit = fileLength;
                    if (gapAhead)
                        limit = _GapStart;
                    if (limit - position <= CrcSize + 1)
                        break;

                    int headerBytes = (int)Math.Min(BlockHeaderReserve, limit - position);
                    if (!Fill(headerBytes))
                        break;

                    uint crc = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(start));
                    if (!TryReadVarint(buffer.AsSpan(start + CrcSize, headerBytes - CrcSize), out ulong header, out int headerLength))
                        break;

                    bool single = (header & 1) == 0;
                    ulong payloadLength = single ? header >> 2 : header >> 1;
                    long maximumLength = Math.Min(limit - position - CrcSize - headerLength, int.MaxValue - BlockHeaderReserve);
                    if (payloadLength == 0 || payloadLength > (ulong)maximumLength)
                        break;

                    int blockLength = CrcSize + headerLength + (int)payloadLength;
                    if (!Fill(blockLength))
                        break;
                    if (Crc32.HashToUInt32(buffer.AsSpan(start + CrcSize, blockLength - CrcSize)) != crc)
                        break;

                    long bufferPosition = position - start; // file offset of buffer[0]
                    int payloadStart = start + CrcSize + headerLength;
                    if (!single)
                        AddRecords(payloadStart, (int)payloadLength, bufferPosition, position);
                    else if ((header & 2) == 0)
                        AddRecord(RecordType.Put, payloadStart, (int)payloadLength, bufferPosition, position);
                    else
                        AddRecord(RecordType.Remove, payloadStart, (int)payloadLength, bufferPosition, position);

                    start += blockLength;
                    position += blockLength;
                }

                ApplyAll();
            }
            finally
            {
                // after a failure, a batch still being decoded keeps its decryptor until it's collected
                for (int i = decoding; i < batches.Length; i++)
                    batches[(first + i) % batches.Length]?.Decryptor?.Dispose();
            }

            // damage before the gap isn't a torn last write; cutting the file there would lose everything after it
            if (gapAhead)
                throw ThrowHelper.CorruptBlock(FilePath);

            // a crash can leave a partly written block at the end; cut it so new blocks follow the last good one
            if (position < fileLength && !Options.IsReadOnlyEnabled)
                RandomAccess.SetLength(_Handle, position);
            _FileLength = position;

            bool Fill(int count)
            {
                if (end - start >= count)
                    return true;

                if (start > 0)
                {
                    Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
                    end -= start;
                    start = 0;
                }

                if (count > buffer.Length)
                    Array.Resize(ref buffer, count);

                while (end < count)
                {
                    int read = RandomAccess.Read(_Handle, buffer.AsSpan(end), readPosition);
                    if (read == 0)
                        return false;
                    end += read;
                    readPosition += read;
                }
                return true;
            }

            void AddRecords(int offset, int count, long bufferPosition, long blockPosition)
            {
                int stop = offset + count;
                while (offset < stop)
                {
                    // records before the damage are applied first, so an earlier deserialization error wins
                    if (!TryReadVarint(buffer.AsSpan(offset, stop - offset), out ulong recordHeader, out int headerLength))
                    {
                        ApplyAll();
                        throw ThrowHelper.CorruptBlock(FilePath);
                    }

                    ulong contentLength = recordHeader >> 1;
                    if (contentLength == 0 || contentLength > (ulong)(stop - offset - headerLength))
                    {
                        ApplyAll();
                        throw ThrowHelper.CorruptBlock(FilePath);
                    }

                    if ((recordHeader & 1) == 0)
                        AddRecord(RecordType.Put, offset + headerLength, (int)contentLength, bufferPosition, blockPosition);
                    else
                        AddRecord(RecordType.Remove, offset + headerLength, (int)contentLength, bufferPosition, blockPosition);

                    offset += headerLength + (int)contentLength;
                }
            }

            void AddRecord(RecordType type, int offset, int length, long bufferPosition, long blockPosition)
            {
                batch ??= NextBatch();
                if (batch.Length + length > batch.Data.Length)
                    Array.Resize(ref batch.Data, Math.Max(batch.Data.Length * 2, batch.Length + length));
                Buffer.BlockCopy(buffer, offset, batch.Data, batch.Length, length);

                long recordPosition = bufferPosition + offset;
                ref LoadRecord record = ref batch.Records[batch.Count++];
                record.Type = type;
                record.Position = recordPosition;
                record.Offset = batch.Length;
                record.Length = length;
                record.BlockOffset = (int)(recordPosition - blockPosition);
                batch.Length += length;

                if (batch.Count == batch.Records.Length || batch.Length >= MaxLoadBatchBytes)
                {
                    batch.Decoding = Task.Factory.StartNew(state => DecodeBatch((LoadBatch)state), batch, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
                    batch = null;
                    decoding++;

                    // keep up with the decoders, so their batches are free to refill
                    while (decoding > 0 && batches[first].Decoding.IsCompleted)
                        ApplyOldest();
                }
            }

            LoadBatch NextBatch()
            {
                if (decoding == batches.Length)
                    ApplyOldest();

                ref LoadBatch next = ref batches[(first + decoding) % batches.Length];
                if (next == null)
                {
                    next = new LoadBatch() { Records = new LoadRecord[batchSize], Data = new byte[batchBytes] };
                    if (Options.IsEncryptionEnabled)
                    {
                        Debug.Assert(_Aes.Mode == CipherMode.CBC && _Aes.Padding == PaddingMode.PKCS7);
                        next.Decryptor = Aes.Create();
                        next.Decryptor.Key = _Aes.Key;
                        next.IV = _Aes.IV;
                    }
                }

                next.Count = 0;
                next.Length = 0;
                next.Error = null;
                return next;
            }

            void ApplyOldest()
            {
                LoadBatch oldest = batches[first];
                oldest.Decoding.GetAwaiter().GetResult();
                oldest.Decoding = null;
                first = (first + 1) % batches.Length;
                decoding--;
                ApplyBatch(oldest);
            }

            void ApplyAll()
            {
                // this thread decodes the last batch rather than wait for the others
                LoadBatch last = batch;
                batch = null;
                if (last != null)
                    DecodeBatch(last);

                while (decoding > 0)
                    ApplyOldest();
                if (last != null)
                    ApplyBatch(last);
            }
        }

        // keeps a failure rather than throw it, so failures are thrown in file order
        private void DecodeBatch(LoadBatch batch)
        {
            batch.Failed = batch.Count;
            for (int i = 0; i < batch.Count; i++)
            {
                ref LoadRecord record = ref batch.Records[i];
                ReadOnlyMemory<byte> content = default;     // the plain content, once it's decrypted
                try
                {
                    if (batch.Decryptor == null)
                        content = new ReadOnlyMemory<byte>(batch.Data, record.Offset, record.Length);
                    else
                    {
                        if (batch.Plain == null || batch.Plain.Length < record.Length)
                            batch.Plain = new byte[Math.Max(record.Length, 4096)];
                        if (!batch.Decryptor.TryDecryptCbc(batch.Data.AsSpan(record.Offset, record.Length), batch.IV, batch.Plain, out int plainLength))
                            throw ThrowHelper.DecryptionFailed();
                        content = new ReadOnlyMemory<byte>(batch.Plain, 0, plainLength);
                    }

                    if (record.Type == RecordType.Put)
                        (record.Key, record.Value) = Deserialize(content);
                    else
                        record.Key = DeserializeKey(content);
                    record.Outcome = LoadOutcome.Read;
                }
                catch (Exception ex)
                {
                    if (Options.DeserializationFailureBehavior == ErrorBehaviorType.Exception)
                    {
                        batch.Failed = i;
                        batch.Error = ExceptionDispatchInfo.Capture(ex);
                        return;
                    }

                    // a value that no longer fits its type usually leaves the key readable; a tombstone is only its key
                    record.Value = default;
                    record.Outcome = LoadOutcome.Unreadable;
                    if (record.Type == RecordType.Put && !content.IsEmpty && TryDeserializeKeyOnly(content, out record.Key))
                        record.Outcome = LoadOutcome.KeyOnly;
                }
            }
        }

        private void ApplyBatch(LoadBatch batch)
        {
            for (int i = 0; i < batch.Failed; i++)
            {
                ref LoadRecord record = ref batch.Records[i];
                if (record.Outcome == LoadOutcome.Unreadable)
                {
                    // with no key, the record can't be indexed, so compaction stays off rather than move or drop it
                    _UnreadableRecords++;
                    continue;
                }

                if (record.Type == RecordType.Put)
                {
                    // a record whose value can't be read is still the key's latest, so it stays in the file and the
                    // key's older value doesn't come back in memory
                    TrackPut(record.Key, record.Position, record.Length, record.BlockOffset);
                    if (record.Outcome == LoadOutcome.KeyOnly)
                    {
                        _Values.TryRemove(record.Key, out _);
                        (_KeysWithoutValues ??= new HashSet<TKey>()).Add(record.Key);
                    }
                    else
                    {
                        _Values[record.Key] = record.Value;
                        _KeysWithoutValues?.Remove(record.Key);
                    }
                }
                else
                {
                    TrackRemove(record.Key, record.Position, record.Length, record.BlockOffset);
                    _Values.TryRemove(record.Key, out _);
                    _KeysWithoutValues?.Remove(record.Key);
                }
            }

            batch.Error?.Throw();
        }
    }
}
