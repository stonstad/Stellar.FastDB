using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Stellar.Collections
{
    internal sealed partial class FastDBStream<TKey, TValue> where TKey : struct
    {
        private const int _RecordsIndexPosition =
            sizeof(ushort) +  // version
            sizeof(byte) +    // serializer
            sizeof(byte);     // format

        private void ReadHeaderInternal()
        {
            _Stream.Seek(0, SeekOrigin.Begin);
            Version = _BinaryReader.ReadUInt16();
            Serializer = (SerializerType)_BinaryReader.ReadByte();
            _Format = (FormatType)_BinaryReader.ReadByte();

            Options = new FastDBOptions(Options)
            {
                Serializer = Serializer,
                IsCompressionEnabled = _Format.HasFlag(FormatType.Compressed),
                IsEncryptionEnabled = _Format.HasFlag(FormatType.Encrypted),
            };
        }

        private void CreateHeaderInternal()
        {
            _BinaryWriter.Seek(0, SeekOrigin.Begin);
            _BinaryWriter.Write(Version);
            _BinaryWriter.Write((byte)Serializer);
            _BinaryWriter.Write((byte)_Format);
            _BinaryWriter.Write(_Allocated.Count);
            _BinaryWriter.Write(_Deleted.Count);
            _BinaryWriter.Flush();
        }

        private void UpdateRecordsIndex()
        {
            _BinaryWriter.Seek(_RecordsIndexPosition, SeekOrigin.Begin);
            _BinaryWriter.Write(_Allocated.Count);
            _BinaryWriter.Write(_Deleted.Count);
            _BinaryWriter.Flush();
        }

        private void LoadRecords(ConcurrentDictionary<TKey, TValue> collection)
        {
            int allocatedCount = _BinaryReader.ReadInt32();
            int deletedCount = _BinaryReader.ReadInt32();
            while (_Stream.Position < _Stream.Length)
            {
                //(TKey Key, TValue Value)? record = ReadInternalBytes();
                (TKey Key, TValue Value)? record = ReadInternalStream();
                if (record.HasValue)
                    collection.TryAdd(record.Value.Key, record.Value.Value);
            }
        }

        private (TKey Key, TValue Value)? ReadInternalBytes()
        {
            int position;
            byte state;
            do
            {
                position = (int)_BinaryReader.BaseStream.Position;
                state = _BinaryReader.ReadByte();
            }
            while (state == (byte)MemoryStateType.Unallocated && _Stream.Position < _Stream.Length);

            if (_Stream.Position == _Stream.Length)
                return null;

            Debug.Assert(state == (byte)MemoryStateType.Allocated || state == (byte)MemoryStateType.Deleted || state == (byte)MemoryStateType.Pending);

            int bytesLength = _BinaryReader.ReadInt32();
            int length = sizeof(byte) + sizeof(int) + bytesLength;

            if (state == (byte)MemoryStateType.Allocated)
            {
                byte[] bytes = _BufferPool.Rent(bytesLength);

                try
                {
                    _BinaryReader.Read();
                    _BinaryReader.ReadBytes(bytesLength);
                }
                catch
                {
                    _BufferPool.Return(bytes);
                    if (Options.DeserializationFailureBehavior == ErrorBehaviorType.Exception)
                        throw;
                    else
                        return null;
                }

                try
                {
                    (TKey Key, TValue value) record = Deserialize(bytes);
                    _Allocated.TryAdd(record.Key, new Index(position, length));
                    _BufferPool.Return(bytes);
                    return record;
                }
                catch
                {
                    _BufferPool.Return(bytes);
                    if (Options.StorageFailureBehavior == ErrorBehaviorType.Exception)
                        throw;
                    return null;
                }
            }
            else if (state == (byte)MemoryStateType.Deleted || state == (byte)MemoryStateType.Pending)
            {
                _Stream.Seek(_Stream.Position + bytesLength, SeekOrigin.Begin);
                _Deleted.Add(position, length);
                return null;
            }
            else
                throw new ArgumentOutOfRangeException();
        }

        private (TKey Key, TValue Value)? ReadInternalStream()
        {
            int position;
            byte state;
            do
            {
                position = (int)_BinaryReader.BaseStream.Position;
                state = _BinaryReader.ReadByte();
            }
            while (state == (byte)MemoryStateType.Unallocated && _Stream.Position < _Stream.Length && _Stream.Position < _Stream.Length);

            if (_Stream.Position == _Stream.Length)
                return null;

            Debug.Assert(state == (byte)MemoryStateType.Allocated || state == (byte)MemoryStateType.Deleted || state == (byte)MemoryStateType.Pending);

            int bytesLength = _BinaryReader.ReadInt32();
            int length = sizeof(byte) + sizeof(int) + bytesLength;

            if (state == (byte)MemoryStateType.Allocated)
            {
                (TKey, TValue) record;
                try
                {
                    record = Deserialize(_Stream, bytesLength);
                    _Allocated.TryAdd(record.Item1, new Index(position, length));
                    return record;

                }
                catch
                {
                    if (Options.DeserializationFailureBehavior == ErrorBehaviorType.Exception)
                        throw;
                    else
                        return null;
                }
            }
            else if (state == (byte)MemoryStateType.Deleted || state == (byte)MemoryStateType.Pending)
            {
                _Stream.Seek(_Stream.Position + bytesLength, SeekOrigin.Begin);
                _Deleted.Add(position, length);
                return null;
            }
            else
                throw new ArgumentOutOfRangeException();
        }

        //private bool AddInternalOld(TKey key, TValue value, byte[] bytes = null)
        //{
        //    if (_Stream == null)
        //        return false;

        //    // serialization
        //    try
        //    {
        //        if (bytes == null)
        //            bytes = Serialize(key, value);
        //    }
        //    catch
        //    {
        //        if (_Options.SerializationFailureBehavior == SerializationFailureBehaviorType.Exception)
        //            throw;
        //        else
        //            return false;
        //    }

        //    // persistence
        //    int bytesLength = bytes.Length;

        //    int length = 0;
        //    length += sizeof(byte);     // state
        //    length += sizeof(int);      // content length
        //    length += bytesLength;    // content

        //    lock (_StreamSyncRoot)
        //    {
        //        long insertionPosition;

        //        try
        //        {
        //            // find deallocated memory
        //            (int Position, int Length)? deallocatedMemory = FindDeallocatedMemory(length);

        //            if (deallocatedMemory.HasValue)
        //            {
        //                _Deleted.Remove(deallocatedMemory.Value.Position);
        //                _Stream.Seek(deallocatedMemory.Value.Position, SeekOrigin.Begin);
        //            }
        //            else
        //                _Stream.Seek(0, SeekOrigin.End);

        //            insertionPosition = _Stream.Position;

        //            // write content but don't commit yet
        //            _BinaryWriter.Write((byte)MemoryStateType.Pending);
        //            _BinaryWriter.Write(bytesLength);
        //            _BinaryWriter.Write(bytes);
        //            _BinaryWriter.Flush();
        //        }
        //        catch
        //        {
        //            if (_Options.StorageFailureBehavior == StorageFailureBehaviorType.Exception)
        //                throw;
        //            else
        //                return false;
        //        }

        //        // commit write (only needed if write is successful)
        //        try
        //        {
        //            _Stream.Seek(insertionPosition, SeekOrigin.Begin);
        //            _BinaryWriter.Write((byte)MemoryStateType.Allocated);
        //            _BinaryWriter.Flush();
        //            _Allocated.Add(key, new Index(insertionPosition, length));
        //            return true;
        //        }
        //        catch
        //        {
        //            if (_Options.StorageFailureBehavior == StorageFailureBehaviorType.Exception)
        //                throw;
        //            else
        //                return false;
        //        }
        //    }
        //}

        private bool AddInternal(TKey key, TValue value, byte[] bytes = null)
        {
            if (_Stream == null || _CancellationTokenSource.IsCancellationRequested)
                return false;

            Debug.Assert(!_Allocated.ContainsKey(key));

            // serialization
            try
            {
                if (bytes == null)
                    bytes = Serialize(key, value);

                if (_Stream == null || _CancellationTokenSource.IsCancellationRequested)
                    return false;
            }
            catch
            {
                if (Options.SerializationFailureBehavior == ErrorBehaviorType.Exception)
                    throw;
                else
                    return false;
            }

            // persistence
            int bytesLength = bytes.Length;

            int length = 0;
            length += sizeof(byte);   // state
            length += sizeof(int);    // content length
            length += bytesLength;    // content

            lock (_StreamLock)
            {
                long insertionPosition;

                try
                {
                    // find deallocated memory
                    (int Position, int Length)? deallocatedMemory = FindDeallocatedMemory(length);

                    if (deallocatedMemory.HasValue)
                    {
                        _Deleted.Remove(deallocatedMemory.Value.Position);
                        _Stream.Seek(deallocatedMemory.Value.Position, SeekOrigin.Begin);
                    }
                    else
                        _Stream.Seek(0, SeekOrigin.End);

                    insertionPosition = _Stream.Position;

                    // write content but don't commit yet
                    _BinaryWriter.Write((byte)MemoryStateType.Pending);
                    _BinaryWriter.Write(bytesLength);
                    _BinaryWriter.Write(bytes);
                    _BinaryWriter.Flush();
                }
                catch
                {
                    if (Options.StorageFailureBehavior == ErrorBehaviorType.Exception)
                        throw;
                    else
                        return false;
                }

                // commit write (only needed if write is successful)
                try
                {
                    _Stream.Seek(insertionPosition, SeekOrigin.Begin);
                    _BinaryWriter.Write((byte)MemoryStateType.Allocated);
                    _BinaryWriter.Flush();
                    _Allocated.Add(key, new Index(insertionPosition, length));
                    return true;
                }
                catch
                {
                    if (Options.StorageFailureBehavior == ErrorBehaviorType.Exception)
                        throw;
                    else
                        return false;
                }
            }
        }

        private bool AddBulkInternal(IDictionary<TKey, TValue> dictionary)
        {
            if (_Stream == null)
                return false;

            lock (_StreamLock)
            {
                _Stream.Seek(0, SeekOrigin.End);

                foreach (var kvp in dictionary)
                {
                    long start = _Stream.Position;
                    _BinaryWriter.Write((byte)MemoryStateType.Pending);
                    _BinaryWriter.Write(0); // content length

                    long contentStart = _Stream.Position;
                    Serialize(_Stream, kvp.Key, kvp.Value);
                    long contentStop = _Stream.Position;

                    int contentLength = (int)(contentStop - contentStart);

                    _Stream.Seek(start, SeekOrigin.Begin);
                    _BinaryWriter.Write((byte)MemoryStateType.Allocated);
                    _BinaryWriter.Write(contentLength);
                    _Stream.Seek(contentStop, SeekOrigin.Begin);

                    Index index = new Index(start, sizeof(byte) + sizeof(int) + contentLength);

                    Debug.Assert(!_Allocated.ContainsKey(kvp.Key));
                    _Allocated.Add(kvp.Key, index);
                }
            }
            return true;
        }

        private bool RemoveInternal(TKey key)
        {
            if (_Stream == null)
                return false;

            lock (_StreamLock)
            {
                Debug.Assert(_Allocated.ContainsKey(key));
                if (_Allocated.TryGetValue(key, out Index index))
                {
                    try
                    {
                        _BinaryWriter.Seek(index.Start, SeekOrigin.Begin);
                        _BinaryWriter.Write((byte)MemoryStateType.Deleted);
                        _BinaryWriter.Flush();

                        _Allocated.Remove(key);
                        _Deleted.Add(index.Start, index.Length);

                        // do not write size change to header (as net record count is same)
                    }
                    catch
                    {
                        if (Options.StorageFailureBehavior == ErrorBehaviorType.Exception)
                            throw;
                        else
                            return false;
                    }

                    _BinaryReader.ReadInt32(); // preserve deleted record length

                    for (int i = 0; i < index.Length - sizeof(byte) - sizeof(int); i++)
                        _BinaryWriter.Write((byte)MemoryStateType.Unallocated);
                    _BinaryWriter.Flush();

                    return true;
                }
            }

            return false;
        }

        private bool UpdateInternal(TKey key, TValue value, byte[] bytes = null)
        {
            lock (_StreamLock)
            {
                if (RemoveInternal(key))
                    return AddInternal(key, value, bytes);
                return false;
            }
        }

        private (int Position, int Length)? FindDeallocatedMemory(int targetLength)
        {
            if (_Deleted.Count == 0)
                return null;

            int left = 0;
            int right = _Deleted.Count - 1;
            (int, int)? result = null;

            while (left <= right)
            {
                int mid = left + (right - left) / 2;
                int key = _Deleted.Keys[mid];
                int value = _Deleted[key];

                if (value >= targetLength)
                {
                    result = (key, value);
                    right = mid - 1;
                }
                else
                    left = mid + 1;
            }

            return result;
        }

        private async Task DefragmentMemoryInternal()
        {
            await Task.CompletedTask;
        }
    }
}