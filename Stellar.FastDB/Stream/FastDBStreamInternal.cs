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
        private const int _HeaderSize =
            sizeof(ushort) +    // version (2)
            sizeof(byte) +      // serializer (1)
            sizeof(byte) +      // format (1)
            sizeof(byte) * 16 + // encryption salt (16)
            sizeof(byte) * 16;  // encryption checksum (16)

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

            _EncryptionSalt = _BinaryReader.ReadBytes(16);
            _EncryptionChecksum = _BinaryReader.ReadBytes(16);
        }

        private void CreateHeaderInternal()
        {
            _BinaryWriter.Seek(0, SeekOrigin.Begin);
            _BinaryWriter.Write(Version);                   // 2 bytes
            _BinaryWriter.Write((byte)Serializer);          // 1 byte
            _BinaryWriter.Write((byte)_Format);             // 1 byte
            if (Options.IsEncryptionEnabled)
            {
                _EncryptionChecksum = Encrypt(new byte[] { _EncryptionSalt[0], _EncryptionSalt[1] });
                _BinaryWriter.Write(_EncryptionSalt);       // 16 bytes
                _BinaryWriter.Write(_EncryptionChecksum);   // 16 bytes
            }
            else
                _BinaryWriter.Write(new byte[32]);          // 32 bytes

            Debug.Assert(_Stream.Position == _HeaderSize);
            _BinaryWriter.Flush();
        }

        private void LoadRecords(ConcurrentDictionary<TKey, TValue> collection)
        {
            switch (Version)
            {
                case 1:
                    LoadRecords_V1(collection);
                    break;
                default:
                    throw new NotImplementedException(nameof(Version));
            }
        }

        private void LoadRecords_V1(ConcurrentDictionary<TKey, TValue> collection)
        {
            while (_Stream.Position < _Stream.Length)
            {
                (TKey Key, TValue Value)? record = ReadInternalStream_V1();
                if (record.HasValue)
                    collection.TryAdd(record.Value.Key, record.Value.Value);
            }
        }

        private (TKey Key, TValue Value)? ReadInternalStream_V1()
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

        private bool AddInternal(TKey key, TValue value, byte[] bytes = null)
        {
            if (_Stream == null || _CancellationTokenSource.IsCancellationRequested)
                return false;

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
                Debug.Assert(!_Allocated.ContainsKey(key));

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
                    if (!Options.IsBufferedWritesEnabled)
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
                if (_Allocated.TryGetValue(key, out Index index))
                {
                    try
                    {
                        _BinaryWriter.Seek(index.Start, SeekOrigin.Begin);
                        _BinaryWriter.Write((byte)MemoryStateType.Deleted); // write tombstone byte
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