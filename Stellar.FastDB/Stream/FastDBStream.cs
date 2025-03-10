using MessagePack;
using MessagePack.Resolvers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Stellar.Collections
{
    internal sealed partial class FastDBStream<TKey, TValue> where TKey : struct
    {
        public FastDBOptions Options { get; private set; }
        public ushort Version { get; private set; } = 1;
        public string FilePath { get; private set; }
        public SerializerType Serializer { get; set; }
        public bool IsEncrypted => _Format.HasFlag(FormatType.Encrypted);
        public bool IsCompressed => _Format.HasFlag(FormatType.Compressed);
        private FormatType _Format;

        private Stream _Stream;
        private BinaryWriter _BinaryWriter;
        private BinaryReader _BinaryReader;

        private SortedDictionary<TKey, Index> _Allocated = new SortedDictionary<TKey, Index>();
        private SortedList<int, int> _Deleted = new SortedList<int, int>();

        private Pool<QueueEntry> _QueueEntryPool = new Pool<QueueEntry>();
        private BufferPool _BufferPool = new BufferPool();

        private MessagePackSerializerOptions _MessagePackOptions;
        private JsonSerializerOptions _JsonSerializerOptions;

        private readonly object _StreamLock = new object();

        public FastDBStream(FastDBOptions options)
        {
            Options = options;
        }

        public FastDBStream(string filePath, FastDBOptions options)
        {
            FilePath = filePath;
            Options = options;
        }

        public FastDBStream(Stream stream, FastDBOptions options)
        {
            _Stream = stream;
            Options = options;
        }

        public void Load(ConcurrentDictionary<TKey, TValue> collection)
        {
            if (_Stream == null)
            {
                if (Options.IsReadOnlyEnabled)
                    _Stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None);
                else
                    _Stream = new FileStream(FilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }

            _BinaryWriter = new BinaryWriter(_Stream);
            _BinaryReader = new BinaryReader(_Stream);
            _CancellationTokenSource = new CancellationTokenSource();

            lock (_StreamLock)
            {
                if (_Stream.Length == 0)
                {
                    InitializeStream();
                    InitializeEncryption();
                    CreateHeaderInternal();
                    ValidateEncryption();
                }
                else
                {
                    ReadHeaderInternal();
                    InitializeStream();
                    InitializeEncryption();
                    ValidateEncryption();
                    LoadRecords(collection);
                }
            }

            StartTasks();
        }

        private void InitializeStream()
        {
            if (Options.Serializer == SerializerType.MessagePack_Contractless)
            {
                if (Options.MessagePackOptions == null)
                    _MessagePackOptions = ContractlessStandardResolver.Options;
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
                if (Options.IsCompressionEnabled)
                {
                    _MessagePackOptions = _MessagePackOptions.WithCompression(MessagePackCompression.Lz4BlockArray);
                    _Format |= FormatType.Compressed;
                }
            }
            else if (Options.Serializer == SerializerType.SystemTextJson_JSON)
            {
                if (Options.JsonSerializerOptions == null)
                    _JsonSerializerOptions = new JsonSerializerOptions();
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
            if (Options.BufferMode == BufferModeType.Disabled)
                return AddInternal(key, value);
            else
            {
                QueueEntry entry = _QueueEntryPool.Rent();
                entry.Set(BufferOperationType.Add, key, value);

                ResetSemaphores();
                if (Options.BufferMode == BufferModeType.WriteParallelEnabled)
                    _SerializationChannel.Writer.TryWrite(entry);
                else
                    _FileSystemChannel.Writer.TryWrite(entry);
                return true;
            }
        }

        public async Task<bool> AddAsync(TKey key, TValue value)
        {
            return await Task.Run(() => AddInternal(key, value));
        }

        public bool AddBulk(IDictionary<TKey, TValue> dictionary)
        {
            return AddBulkInternal(dictionary);
        }

        public async Task<bool> AddBulkAsync(IDictionary<TKey, TValue> dictionary)
        {
            return await Task.Run(() => AddBulkInternal(dictionary));
        }

        public bool Remove(TKey key)
        {
            if (Options.BufferMode == BufferModeType.Disabled)
                return RemoveInternal(key);
            else
            {
                QueueEntry entry = _QueueEntryPool.Rent();
                entry.Set(BufferOperationType.Remove, key);

                ResetSemaphores();
                if (Options.BufferMode == BufferModeType.WriteParallelEnabled)
                    _SerializationChannel.Writer.TryWrite(entry);
                else
                    _FileSystemChannel.Writer.TryWrite(entry);
                return true;
            }
        }

        public async Task<bool> RemoveAsync(TKey key)
        {
            return await Task.Run(() => RemoveInternal(key));
        }

        public bool Update(TKey key, TValue value)
        {
            if (Options.BufferMode == BufferModeType.Disabled)
                return UpdateInternal(key, value);
            else
            {
                QueueEntry entry = _QueueEntryPool.Rent();
                entry.Set(BufferOperationType.Update, key, value);

                ResetSemaphores();
                if (Options.BufferMode == BufferModeType.WriteParallelEnabled)
                    _SerializationChannel.Writer.TryWrite(entry);
                else
                    _FileSystemChannel.Writer.TryWrite(entry);
                return true;
            }
        }

        public async Task<bool> UpdateAsync(TKey key, TValue value)
        {
            return await Task.Run(() => UpdateInternal(key, value));
        }

        public async Task DefragmentMemory()
        {
            await DefragmentMemoryInternal();
        }

        public long GetSizeBytes()
        {
            return _Stream.Length;
        }

        public void Flush()
        {
            if (Options.IsReadOnlyEnabled)
                return;

            if (Options.BufferMode == BufferModeType.WriteEnabled)
                _FileSystemChannelEmpty.Wait();
            else if (Options.BufferMode == BufferModeType.WriteParallelEnabled)
            {
                _SerializationChannelEmpty.Wait();
                _SequentialProcessingChannelEmpty.Wait();
                _FileSystemChannelEmpty.Wait();
            }

            _BinaryWriter.Flush();
            _Stream.Flush();

            _QueueEntryPool.Clear();
            _BufferPool.Clear();
        }

        public async Task FlushAsync()
        {
            await Task.Run(() => Flush());
        }

        public void Clear()
        {
            CancelTasks();

            _Allocated.Clear();
            _Deleted.Clear();

            _BinaryWriter.Flush();
            _Stream.Flush();

            _Stream.SetLength(0);
            CreateHeaderInternal();

            _QueueEntryPool.Clear();
            _BufferPool.Clear();

            StartTasks();
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
            CancelTasks();

            if (!Options.IsReadOnlyEnabled)
            {
                _BinaryWriter.Flush();
                _Stream.Flush();
            }

            if (!Options.IsReadOnlyEnabled)
                _BinaryWriter.Close();
            _BinaryReader.Close();
            _Stream.Close();

            if (!Options.IsReadOnlyEnabled)
                _BinaryWriter.Dispose();

            _BinaryReader.Dispose();
            _Stream.Dispose();

            _Aes?.Dispose();
            _AesEncryptor?.Dispose();
            _AesDecryptor?.Dispose();
            _EncryptionStream?.Dispose();
            _DecryptionStream?.Dispose();
            _CancellationTokenSource?.Dispose();

            _QueueEntryPool = null;
            _BufferPool = null;

            _Allocated = null;
            _Deleted = null;
            _BinaryWriter = null;
            _BinaryReader = null;
            _Stream = null;
            _Aes = null;
            _AesEncryptor = null;
            _AesDecryptor = null;
            _EncryptionStream = null;
            _DecryptionStream = null;
            _QueueEntryPool = null;
        }
    }
}