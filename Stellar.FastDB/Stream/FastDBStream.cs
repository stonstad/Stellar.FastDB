using MessagePack;
using MessagePack.Resolvers;
using Microsoft.Win32.SafeHandles;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Stellar.Collections
{
    internal sealed partial class FastDBStream<TKey, TValue> where TKey : struct
    {
        public FastDBOptions Options { get; private set; }
        public ushort Version { get; private set; } = FormatVersion;
        public string FilePath { get; private set; }
        public SerializerType Serializer { get; set; }
        public bool IsEncrypted => _Format.HasFlag(FormatType.Encrypted);
        public bool IsCompressed => _Format.HasFlag(FormatType.Compressed);
        private FormatType _Format;

        private SafeFileHandle _Handle;
        private SafeFileHandle _FlushHandle;    // Windows only: flushes to disk, so they don't hold up writes on _Handle
        private ConcurrentDictionary<TKey, TValue> _Values;

        private MessagePackSerializerOptions _MessagePackOptions;
        private JsonSerializerOptions _JsonSerializerOptions;
        private JsonWriterOptions _JsonWriterOptions;

#if (NET9_0_OR_GREATER)
        private readonly Lock _StreamLock = new Lock();
#else
        private readonly object _StreamLock = new object();
#endif

        public FastDBStream(FastDBOptions options)
        {
            Options = options;
        }

        public FastDBStream(string filePath, FastDBOptions options)
        {
            FilePath = filePath;
            Options = options;
        }

        public ConcurrentDictionary<TKey, TValue> Load()
        {
            if (FilePath == null)
            {
                _Values = new ConcurrentDictionary<TKey, TValue>();
                return _Values;
            }

            // Windows runs the operations on a handle one at a time, so a compaction flush on _Handle would hold up every
            // write meanwhile; flushes get a handle of their own. Sharing writes but not reads still shuts out every other
            // FastDB instance, as each one reads.
            FileShare share = FileShare.None;
            if (OperatingSystem.IsWindows() && !Options.IsReadOnlyEnabled)
                share = FileShare.Write;

            if (Options.IsReadOnlyEnabled)
                _Handle = File.OpenHandle(FilePath, FileMode.Open, FileAccess.Read, FileShare.None);
            else
                _Handle = File.OpenHandle(FilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, share);

            try
            {
                if (share == FileShare.Write)
                    _FlushHandle = File.OpenHandle(FilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

                lock (_StreamLock)
                {
                    if (RandomAccess.GetLength(_Handle) == 0)
                    {
                        _Values = new ConcurrentDictionary<TKey, TValue>();
                        Serializer = Options.Serializer;
                        InitializeStream();
                        InitializeEncryption();
                        if (!Options.IsReadOnlyEnabled)
                        {
                            CreateHeaderInternal();
                            ValidateEncryption();
                        }
                    }
                    else
                    {
                        ReadHeaderInternal();
                        InitializeStream();
                        InitializeEncryption();
                        ValidateEncryption();
                        LoadRecords();
                    }
                }

                // outside _StreamLock: compaction takes _SweepLock and _CommitLock first
                CompactAtOpen();
            }
            catch
            {
                _FlushHandle?.Dispose();
                _FlushHandle = null;
                _Handle.Dispose();
                _Handle = null;
                throw;
            }

            return _Values;
        }

        private void InitializeStream()
        {
            if (Options.Serializer == SerializerType.MessagePack_Contractless)
            {
                if (Options.MessagePackOptions == null)
                    _MessagePackOptions = ContractlessStandardResolver.Options;
                else
                    _MessagePackOptions = Options.MessagePackOptions;

                if (Options.IsCompressionEnabled)
                {
                    _MessagePackOptions = _MessagePackOptions.WithCompression(MessagePackCompression.Lz4BlockArray);
                    _Format |= FormatType.Compressed;
                }
            }
            else if (Options.Serializer == SerializerType.MessagePack_Contract)
            {
                if (Options.MessagePackOptions == null)
                    _MessagePackOptions = StandardResolver.Options;
                else
                    _MessagePackOptions = Options.MessagePackOptions;

                if (Options.IsCompressionEnabled)
                {
                    _MessagePackOptions = _MessagePackOptions.WithCompression(MessagePackCompression.Lz4BlockArray);
                    _Format |= FormatType.Compressed;
                }
            }
            else if (Options.Serializer == SerializerType.SystemTextJson_JSON)
            {
                if (Options.JsonSerializerOptions == null)
                    _JsonSerializerOptions = new JsonSerializerOptions() { IgnoreReadOnlyProperties = true };
                else
                    _JsonSerializerOptions = Options.JsonSerializerOptions;

                // what JsonSerializer.SerializeToUtf8Bytes would use
                _JsonWriterOptions = new JsonWriterOptions()
                {
                    Encoder = _JsonSerializerOptions.Encoder,
                    Indented = _JsonSerializerOptions.WriteIndented,
                    SkipValidation = true,
                };
            }
        }

        private void InitializeEncryption()
        {
            if (Options.IsEncryptionEnabled)
            {
                if (_EncryptionSalt == null)
                    _EncryptionSalt = GenerateEncryptionSalt();
                InitializeAesEncryption(_EncryptionSalt);
                _Format |= FormatType.Encrypted;
            }
        }

        public void ValidateEncryption()
        {
            if (Options.IsEncryptionEnabled)
            {
                try
                {
                    byte[] decryptedChecksum = Decrypt(_EncryptionChecksum);
                    if (decryptedChecksum.Length != 2 ||
                        decryptedChecksum[0] != _EncryptionSalt[0] ||
                        decryptedChecksum[1] != _EncryptionSalt[1])
                    {
                        throw ThrowHelper.DecryptionFailed();
                    }
                }
                catch
                {
                    throw ThrowHelper.DecryptionFailed();
                }
            }
        }

        public bool Add(TKey key, TValue value)
        {
            return Put(key, value, PutMode.Add);
        }

        public ValueTask<bool> AddAsync(TKey key, TValue value)
        {
            return PutAsync(key, value, PutMode.Add);
        }

        public bool Update(TKey key, TValue value)
        {
            return Put(key, value, PutMode.Update);
        }

        public ValueTask<bool> UpdateAsync(TKey key, TValue value)
        {
            return PutAsync(key, value, PutMode.Update);
        }

        public bool AddOrUpdate(TKey key, TValue value)
        {
            return Put(key, value, PutMode.AddOrUpdate);
        }

        public ValueTask<bool> AddOrUpdateAsync(TKey key, TValue value)
        {
            return PutAsync(key, value, PutMode.AddOrUpdate);
        }

        private bool Put(TKey key, TValue value, PutMode mode)
        {
            if (_Handle != null && Options.Durability == DurabilityMode.Buffered)
                WaitForBufferSpace();

            if (!TryPut(key, value, mode, out long sequence))
                return false;
            return Complete(sequence);
        }

        // an exception is returned in the ValueTask rather than thrown, as it was when these methods were async
        private ValueTask<bool> PutAsync(TKey key, TValue value, PutMode mode)
        {
            try
            {
                if (_Handle != null && Options.Durability == DurabilityMode.Buffered && !HasBufferSpace())
                    return PutAfterBufferSpaceAsync(key, value, mode);

                if (!TryPut(key, value, mode, out long sequence))
                    return new ValueTask<bool>(false);

                CommitWaiter waiter = CompleteOrWait(sequence, out bool succeeded);
                if (waiter == null)
                    return new ValueTask<bool>(succeeded);
                return new ValueTask<bool>(waiter, waiter.Version);
            }
            catch (Exception ex)
            {
                return ValueTask.FromException<bool>(ex);
            }
        }

        // nothing may await between serializing a record and appending it, so writers wait for space first
        private async ValueTask<bool> PutAfterBufferSpaceAsync(TKey key, TValue value, PutMode mode)
        {
            await WaitForBufferSpaceAsync().ConfigureAwait(false);
            if (!TryPut(key, value, mode, out long sequence))
                return false;
            return Complete(sequence);
        }

        private bool TryPut(TKey key, TValue value, PutMode mode, out long sequence)
        {
            sequence = 0;
            if (!TrySerialize(key, value, out PooledBufferWriter content))
                return false;

            try
            {
                lock (_StreamLock)
                {
                    if (_Values.ContainsKey(key))
                    {
                        if (mode == PutMode.Add && Options.AddDuplicateKeyBehavior == DuplicateKeyBehaviorType.Exception)
                            throw ThrowHelper.DuplicateKey(key);
                        if (mode == PutMode.Add && Options.AddDuplicateKeyBehavior == DuplicateKeyBehaviorType.ReturnFalse)
                            return false;
                    }
                    else if (mode == PutMode.Update)
                    {
                        if (Options.UpdateKeyNotFoundBehavior == ErrorBehaviorType.Exception)
                            throw ThrowHelper.KeyNotFound(key);
                        return false;
                    }

                    if (content != null)
                    {
                        Append(RecordType.Put, key, content.WrittenSpan);
                        sequence = _Sequence;
                        _Pending.Operations++;
                    }

                    _Values[key] = value;
                    return true;
                }
            }
            finally
            {
                content?.Release();
            }
        }

        public bool AddBulk(IDictionary<TKey, TValue> dictionary)
        {
            if (_Handle != null && Options.Durability == DurabilityMode.Buffered)
                WaitForBufferSpace();

            if (!TryPutBulk(dictionary, out long sequence))
                return false;
            return Complete(sequence);
        }

        public ValueTask<bool> AddBulkAsync(IDictionary<TKey, TValue> dictionary)
        {
            try
            {
                if (_Handle != null && Options.Durability == DurabilityMode.Buffered && !HasBufferSpace())
                    return AddBulkAfterBufferSpaceAsync(dictionary);

                if (!TryPutBulk(dictionary, out long sequence))
                    return new ValueTask<bool>(false);

                CommitWaiter waiter = CompleteOrWait(sequence, out bool succeeded);
                if (waiter == null)
                    return new ValueTask<bool>(succeeded);
                return new ValueTask<bool>(waiter, waiter.Version);
            }
            catch (Exception ex)
            {
                return ValueTask.FromException<bool>(ex);
            }
        }

        private async ValueTask<bool> AddBulkAfterBufferSpaceAsync(IDictionary<TKey, TValue> dictionary)
        {
            await WaitForBufferSpaceAsync().ConfigureAwait(false);
            if (!TryPutBulk(dictionary, out long sequence))
                return false;
            return Complete(sequence);
        }

        private bool TryPutBulk(IDictionary<TKey, TValue> dictionary, out long sequence)
        {
            sequence = 0;
            int count = dictionary.Count;
            KeyValuePair<TKey, TValue>[] items = ArrayPool<KeyValuePair<TKey, TValue>>.Shared.Rent(count);
            int[] ends = null;                      // where each record's content ends in its chunk's writer
            PooledBufferWriter[] chunks = null;     // one writer per chunkSize records

            // a big bulk add serializes its records in chunks, on all cores
            int chunkSize = Math.Max(count, 1);
            if (count > 0 && count >= Options.ParallelBulkThreshold)
            {
                int parallelChunks = Math.Max(1, Math.Min(Environment.ProcessorCount * 2, count / Math.Max(1, Options.ParallelBulkChunkSize)));
                chunkSize = (count + parallelChunks - 1) / parallelChunks;
            }
            int chunkCount = (count + chunkSize - 1) / chunkSize;

            try
            {
                dictionary.CopyTo(items, 0);
                if (_Handle != null)
                {
                    ends = ArrayPool<int>.Shared.Rent(count);
                    chunks = ArrayPool<PooledBufferWriter>.Shared.Rent(chunkCount);
                    if (!TrySerialize(items, count, ends, chunks, chunkSize))
                        return false;
                }

                lock (_StreamLock)
                {
                    if (Options.BulkAddDuplicateKeyBehavior != DuplicateKeyBehaviorType.Upsert)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            if (_Values.ContainsKey(items[i].Key))
                            {
                                if (Options.BulkAddDuplicateKeyBehavior == DuplicateKeyBehaviorType.Exception)
                                    throw ThrowHelper.DuplicateKey(items[i].Key);
                                return false;
                            }
                        }
                    }

                    if (chunks != null && count > 0)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            int start = 0;
                            if (i % chunkSize != 0)
                                start = ends[i - 1];
                            Append(RecordType.Put, items[i].Key, chunks[i / chunkSize].WrittenSpan.Slice(start, ends[i] - start));
                        }
                        sequence = _Sequence;
                        _Pending.Operations++;
                    }

                    for (int i = 0; i < count; i++)
                        _Values[items[i].Key] = items[i].Value;
                    return true;
                }
            }
            finally
            {
                if (chunks != null)
                {
                    // one chunk's writer is this thread's own; the parallel chunks' writers were made for this call
                    for (int i = 0; i < chunkCount; i++)
                    {
                        if (chunkCount == 1)
                            chunks[i]?.Release();
                        else
                            chunks[i]?.Clear();
                    }
                    ArrayPool<PooledBufferWriter>.Shared.Return(chunks, true);
                }
                if (ends != null)
                    ArrayPool<int>.Shared.Return(ends);
                ArrayPool<KeyValuePair<TKey, TValue>>.Shared.Return(items, RuntimeHelpers.IsReferenceOrContainsReferences<KeyValuePair<TKey, TValue>>());
            }
        }

        public bool Remove(TKey key, out TValue value)
        {
            if (_Handle != null && Options.Durability == DurabilityMode.Buffered)
                WaitForBufferSpace();

            if (!TryRemove(key, out value, out long sequence))
                return false;
            return Complete(sequence);
        }

        public ValueTask<bool> RemoveAsync(TKey key)
        {
            try
            {
                if (_Handle != null && Options.Durability == DurabilityMode.Buffered && !HasBufferSpace())
                    return RemoveAfterBufferSpaceAsync(key);

                if (!TryRemove(key, out _, out long sequence))
                    return new ValueTask<bool>(false);

                CommitWaiter waiter = CompleteOrWait(sequence, out bool succeeded);
                if (waiter == null)
                    return new ValueTask<bool>(succeeded);
                return new ValueTask<bool>(waiter, waiter.Version);
            }
            catch (Exception ex)
            {
                return ValueTask.FromException<bool>(ex);
            }
        }

        private async ValueTask<bool> RemoveAfterBufferSpaceAsync(TKey key)
        {
            await WaitForBufferSpaceAsync().ConfigureAwait(false);
            if (!TryRemove(key, out _, out long sequence))
                return false;
            return Complete(sequence);
        }

        private bool TryRemove(TKey key, out TValue value, out long sequence)
        {
            value = default;
            sequence = 0;
            if (!TrySerializeKey(key, out PooledBufferWriter content))
                return false;

            try
            {
                lock (_StreamLock)
                {
                    if (!_Values.TryRemove(key, out value))
                    {
                        if (Options.RemoveKeyNotFoundBehavior == ErrorBehaviorType.Exception)
                            throw ThrowHelper.KeyNotFound(key);
                        return false;
                    }

                    if (content != null)
                    {
                        Append(RecordType.Remove, key, content.WrittenSpan);
                        sequence = _Sequence;
                        _Pending.Operations++;
                    }
                    return true;
                }
            }
            finally
            {
                content?.Release();
            }
        }

        public void RemoveBulk(IEnumerable<TKey> keys)
        {
            if (_Handle != null && Options.Durability == DurabilityMode.Buffered)
                WaitForBufferSpace();

            TryRemoveBulk(keys, out long sequence);
            Complete(sequence);
        }

        public ValueTask RemoveBulkAsync(IEnumerable<TKey> keys)
        {
            try
            {
                if (_Handle != null && Options.Durability == DurabilityMode.Buffered && !HasBufferSpace())
                    return RemoveBulkAfterBufferSpaceAsync(keys);

                TryRemoveBulk(keys, out long sequence);
                CommitWaiter waiter = CompleteOrWait(sequence, out _);
                if (waiter == null)
                    return default;
                return new ValueTask(waiter, waiter.Version);
            }
            catch (Exception ex)
            {
                return ValueTask.FromException(ex);
            }
        }

        private async ValueTask RemoveBulkAfterBufferSpaceAsync(IEnumerable<TKey> keys)
        {
            await WaitForBufferSpaceAsync().ConfigureAwait(false);
            TryRemoveBulk(keys, out long sequence);
            Complete(sequence);
        }

        private bool TryRemoveBulk(IEnumerable<TKey> keys, out long sequence)
        {
            sequence = 0;
            List<TKey> items = new List<TKey>(keys);
            int[] ends = null;                      // where each key's content ends in contents
            PooledBufferWriter contents = null;

            try
            {
                if (_Handle != null)
                {
                    ends = ArrayPool<int>.Shared.Rent(items.Count);
                    if (!TrySerializeKeys(items, ends, out contents))
                        return false;
                }

                lock (_StreamLock)
                {
                    if (Options.RemoveKeyNotFoundBehavior == ErrorBehaviorType.Exception)
                    {
                        foreach (TKey key in items)
                            if (!_Values.ContainsKey(key))
                                throw ThrowHelper.KeyNotFound(key);
                    }

                    for (int i = 0; i < items.Count; i++)
                    {
                        if (_Values.TryRemove(items[i], out _) && contents != null)
                        {
                            int start = 0;
                            if (i > 0)
                                start = ends[i - 1];
                            Append(RecordType.Remove, items[i], contents.WrittenSpan.Slice(start, ends[i] - start));
                            sequence = _Sequence;
                        }
                    }

                    if (sequence != 0)
                        _Pending.Operations++;
                    return true;
                }
            }
            finally
            {
                contents?.Release();
                if (ends != null)
                    ArrayPool<int>.Shared.Return(ends);
            }
        }

        // content is null when there is no file to write to; the caller releases it
        private bool TrySerialize(TKey key, TValue value, out PooledBufferWriter content)
        {
            content = null;
            if (_Handle == null)
                return true;

            content = PooledBufferWriter.Acquire();
            try
            {
                Serialize(content, key, value);
                return true;
            }
            catch
            {
                content.Release();
                content = null;
                if (Options.SerializationFailureBehavior == ErrorBehaviorType.Exception)
                    throw;
                return false;
            }
        }

        // serializes the records back to back into one writer per chunk of chunkSize records, recording where each one
        // ends in its chunk's writer; the caller releases the writers
        private bool TrySerialize(KeyValuePair<TKey, TValue>[] items, int count, int[] ends, PooledBufferWriter[] chunks, int chunkSize)
        {
            int chunkCount = (count + chunkSize - 1) / chunkSize;
            if (chunkCount <= 1)
            {
                if (count == 0)
                    return true;

                chunks[0] = PooledBufferWriter.Acquire();
                try
                {
                    SerializeChunk(items, 0, count, ends, chunks[0]);
                    return true;
                }
                catch
                {
                    if (Options.SerializationFailureBehavior == ErrorBehaviorType.Exception)
                        throw;
                    return false;
                }
            }

            Exception[] errors = new Exception[chunkCount];
            Parallel.For(0, chunkCount, chunk =>
            {
                int first = chunk * chunkSize;
                chunks[chunk] = new PooledBufferWriter();
                try
                {
                    SerializeChunk(items, first, Math.Min(first + chunkSize, count), ends, chunks[chunk]);
                }
                catch (Exception ex)
                {
                    errors[chunk] = ex;
                }
            });

            // the first failure in record order is the one serializing on one thread would have stopped at
            foreach (Exception error in errors)
            {
                if (error == null)
                    continue;
                if (Options.SerializationFailureBehavior == ErrorBehaviorType.Exception)
                    ExceptionDispatchInfo.Capture(error).Throw();
                return false;
            }
            return true;
        }

        private void SerializeChunk(KeyValuePair<TKey, TValue>[] items, int first, int end, int[] ends, PooledBufferWriter writer)
        {
            for (int i = first; i < end; i++)
            {
                Serialize(writer, items[i].Key, items[i].Value);
                ends[i] = writer.Length;
            }
        }

        // content is null when there is no file to write to; the caller releases it
        private bool TrySerializeKey(TKey key, out PooledBufferWriter content)
        {
            content = null;
            if (_Handle == null)
                return true;

            content = PooledBufferWriter.Acquire();
            try
            {
                SerializeKey(content, key);
                return true;
            }
            catch
            {
                content.Release();
                content = null;
                if (Options.SerializationFailureBehavior == ErrorBehaviorType.Exception)
                    throw;
                return false;
            }
        }

        // serializes the keys back to back, recording where each one ends; the caller releases contents
        private bool TrySerializeKeys(List<TKey> keys, int[] ends, out PooledBufferWriter contents)
        {
            contents = PooledBufferWriter.Acquire();
            try
            {
                for (int i = 0; i < keys.Count; i++)
                {
                    SerializeKey(contents, keys[i]);
                    ends[i] = contents.Length;
                }
                return true;
            }
            catch
            {
                contents.Release();
                contents = null;
                if (Options.SerializationFailureBehavior == ErrorBehaviorType.Exception)
                    throw;
                return false;
            }
        }

        public long GetSizeBytes()
        {
            if (_Handle == null)
                return 0;
            return _FileLength;
        }

        public void Flush()
        {
            if (_Handle == null)
                return;

            Exception error;
            lock (_CommitLock)
            {
                CommitPending();
                error = _UnreportedError;
                _UnreportedError = null;
            }
            ScheduleCommit();

            if (error != null && Options.StorageFailureBehavior == ErrorBehaviorType.Exception)
                throw ThrowHelper.WriteFailed(FilePath, error);
        }

        public async Task FlushAsync()
        {
            await Task.Run(() => Flush());
        }

        public void Clear()
        {
            lock (_SweepLock)
            lock (_CommitLock)
            {
                lock (_StreamLock)
                {
                    // writes still waiting to be written are discarded along with everything else
                    Batch batch = _Pending;
                    if (batch.Records.Count > 0)
                    {
                        Volatile.Write(ref _CommittedSequence, batch.LastSequence);
                        Interlocked.Add(ref _PendingBytes, -batch.Bytes);
                        batch.Reset();
                    }
                    _UnreportedError = null;

                    _Values.Clear();
                    ResetIndex();
                    _Sweeping = false;
                    _SweepBudget = 0;

                    if (_Handle != null)
                    {
                        RandomAccess.SetLength(_Handle, HeaderSize);
                        _FileLength = HeaderSize;
                        if (_GapSize > 0)
                        {
                            WriteGap(0, 0);
                            _GapFlushPending = true;
                        }
                    }
                }

                CompleteWaiters();
            }

            // a writer that appended while this held _CommitLock is waiting for someone to commit it
            ScheduleCommit();
            if (Options.Durability == DurabilityMode.Buffered)
                SignalSpaceFreed();
        }

        public void Delete()
        {
            Dispose();
            if (FilePath != null)
                File.Delete(FilePath);
        }

        public async Task DeleteAsync()
        {
            Dispose();
            if (FilePath != null)
                await Task.Run(() => File.Delete(FilePath));
        }

        public void Dispose()
        {
            lock (_SweepLock)
            lock (_CommitLock)
            {
                if (_Handle != null)
                {
                    while (CommitPending())
                    {
                    }

                    // the count only sizes the next load, so failing to write it doesn't fail the close
                    if (!Options.IsReadOnlyEnabled)
                    {
                        try
                        {
                            WriteRecordCount();
                        }
                        catch (IOException)
                        {
                        }
                    }

                    _Handle.Dispose();
                    _Handle = null;
                    _FlushHandle?.Dispose();
                    _FlushHandle = null;
                }

                DisposeEncryption();
            }

            // after the last commits, which may arm it; an idle check already running stops at the closed handle
            _IdleTimer?.Dispose();
        }
    }
}
