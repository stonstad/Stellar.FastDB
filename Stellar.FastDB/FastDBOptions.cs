using MessagePack;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Stellar.Collections
{
    /// <summary>
    /// Specifies the object serializer and associated output format.
    /// </summary>
    public enum SerializerType : byte
    {
        /// <summary>
        /// Specifies a binary serialization format which is efficient and adaptive to schema changes. This is the default
        /// serializer.
        /// </summary>
        MessagePack_Contractless,
        
        /// <summary>
        /// Specifies a binary serialization format which is highly efficient, compact, and adaptive to schema changes.
        /// </summary>
        MessagePack_Contract,
        
        /// <summary>
        /// Specifies a UTF8 JSON serialization format which is comparatively inefficient and adaptive to schema changes.
        /// </summary>
        SystemTextJson_JSON,
    }

    /// <summary>
    /// Specifies when a write returns, and so which failures it survives. Writers that arrive together share one
    /// file write in every mode.
    /// </summary>
    public enum DurabilityMode
    {
        /// <summary>
        /// A write returns once it has reached the operating system, so it survives a crash of the process but not
        /// a power loss. This is the default.
        /// </summary>
        Flushed,

        /// <summary>
        /// A write returns immediately and reaches the file in the background. Reads see it at once. Use Flush() or
        /// FlushAsync() to wait until earlier writes have reached the operating system.
        /// </summary>
        Buffered,

        /// <summary>
        /// A write returns once it has been flushed to disk, so it survives a power loss. This is the slowest mode for
        /// a single writer.
        /// </summary>
        Synced,
    }

    /// <summary>
    /// Specifies how hard background compaction works while a collection is being written: for each byte that an
    /// overwrite or removal makes dead, how much live data compaction may move. Once no write has been committed for
    /// FastDBOptions.CompactionIdleDelay (250 ms by default), compaction finishes at every level except Off.
    /// </summary>
    public enum CompactionEffort
    {
        /// <summary>
        /// No background compaction, including when idle. Compact() still works.
        /// </summary>
        Off,

        /// <summary>
        /// Moves 1 byte of live data for every 3 bytes made dead.
        /// </summary>
        Low,

        /// <summary>
        /// Moves 1 byte of live data for every byte made dead. Steady writing keeps a collection file at about twice the
        /// size of its live data. This is the default.
        /// </summary>
        Balanced,

        /// <summary>
        /// Moves 3 bytes of live data for every byte made dead.
        /// </summary>
        High,

        /// <summary>
        /// Compacts as fast as possible whenever there is dead space.
        /// </summary>
        Maximum,
    }

    /// <summary>
    /// Specifies the desired behavior when a duplicate key insertion is requested.
    /// </summary>
    public enum DuplicateKeyBehaviorType
    {
        /// <summary>
        /// If a duplicate key is encountered an exception is thrown.
        /// </summary>
        Exception,
        
        /// <summary>
        /// If a duplicate key is encountered an upsert is performed.
        /// </summary>
        Upsert,
        
        /// <summary>
        /// If a duplicate key is encountered false is returned.
        /// </summary>
        ReturnFalse,
    }

    /// <summary>
    /// Species the desired behavior when an error occurs.
    /// </summary>
    public enum ErrorBehaviorType
    {
        /// <summary>
        /// Errors throw an exception when encountered.
        /// </summary>
        Exception,
        /// <summary>
        /// Errors return false when encountered.
        /// </summary>
        ReturnFalse,
    }

    /// <summary>
    /// Defines options for a Stellar.FastDB database or collection. Options are immutable after initial construction.
    /// </summary>
    public sealed class FastDBOptions
    {
        /// <summary>
        /// Specifies a top-level directory to store the database directory and all associated files.
        /// </summary>
        public string BaseDirectory { get; init; } = Environment.CurrentDirectory;
        
        /// <summary>
        /// Specifies the name of the directory containing all database files.
        /// </summary>
        public string DatabaseName { get; init; } = "Fast_DB";
        
        public string DirectoryPath => Path.Combine(BaseDirectory, DatabaseName);
        
        /// <summary>
        /// Specifies the extension of each database collection file.
        /// </summary>
        public string FileExtension { get; init; } = "db";
        
        /// <summary>
        /// Specifies the serializer to use when writing to the file system.
        /// </summary>
        public SerializerType Serializer { get; init; }
        
        /// <summary>
        /// Specifies when a write returns. The default is <see cref="DurabilityMode.Flushed"/>.
        /// </summary>
        public DurabilityMode Durability { get; init; }

        /// <summary>
        /// Specifies how many bytes of serialized data may wait to be written in <see cref="DurabilityMode.Buffered"/>
        /// mode. When the limit is reached, writes wait until the backlog drains. The default is 256 MB.
        /// </summary>
        public long MaxBufferedBytes { get; init; } = 256L * 1024 * 1024;

        /// <summary>
        /// Specifies how hard background compaction works while a collection is being written. The default is
        /// <see cref="CompactionEffort.Balanced"/>: steady writing keeps a collection file at about twice the size of its
        /// live data, but a burst of writes can grow it well past that. Once no write has been committed for
        /// <see cref="CompactionIdleDelay"/>, compaction finishes at every level except Off, and the file shrinks to about
        /// the size of its live data.
        /// </summary>
        public CompactionEffort CompactionEffort { get; init; } = CompactionEffort.Balanced;

        /// <summary>
        /// Specifies how long writing must pause before background compaction finishes regardless of
        /// <see cref="CompactionEffort"/>, shrinking the file to about the size of its live data. The default is 250 ms.
        /// </summary>
        public TimeSpan CompactionIdleDelay { get; init; } = TimeSpan.FromMilliseconds(250);

        // the effort as a number from 0 to 1: for each byte made dead, compaction may move value / (1 - value) bytes of
        // live data, and 1 compacts as fast as possible
        internal double CompactionEffortValue
        {
            get
            {
                switch (CompactionEffort)
                {
                    case CompactionEffort.Off:
                        return 0;
                    case CompactionEffort.Low:
                        return 0.25;
                    case CompactionEffort.High:
                        return 0.75;
                    case CompactionEffort.Maximum:
                        return 1;
                    default:
                        return 0.5;
                }
            }
        }

        /// <summary>
        /// Specifies how many records opening a collection deserializes as one batch. Batches are deserialized on all
        /// cores and applied in file order. The default is 4096.
        /// </summary>
        public int LoadBatchSize { get; init; } = 4096;

        /// <summary>
        /// Specifies how many records a bulk add needs before it serializes them on all cores rather than on the calling
        /// thread. The default is 1,000.
        /// </summary>
        public int ParallelBulkThreshold { get; init; } = 1000;

        /// <summary>
        /// Specifies the fewest records each core serializes when a bulk add runs on all cores. The default is 250.
        /// </summary>
        public int ParallelBulkChunkSize { get; init; } = 250;

        /// <summary>
        /// Specifies whether data is persisted in-memory only, or written to the file system.
        /// </summary>
        public bool IsMemoryOnlyEnabled { get; init; }
        
        /// <summary>
        /// Specifies whether the database is opened in read-only mode.
        /// </summary>
        public bool IsReadOnlyEnabled { get; init; }
        
        /// <summary>
        /// Specifies whether encryption is enabled. Specification of a password is required.
        /// </summary>
        public bool IsEncryptionEnabled { get; init; }
        
        /// <summary>
        /// Specifies the database password used to encrypt a database.
        /// </summary>
        public string EncryptionPassword { get; init; }
        
        /// <summary>
        /// Specifies the encryption algorithm used by encryption.
        /// </summary>
        public HashAlgorithmName EncryptionAlgorithm { get; init; } = HashAlgorithmName.SHA256;
        
        /// <summary>
        /// Specifies whether compression is enabled.
        /// </summary>
        public bool IsCompressionEnabled { get; init; }

        /// <summary>
        /// Specifies additional options for the MessagePack serializer.
        /// </summary>
        public MessagePackSerializerOptions MessagePackOptions { get; init; }
        
        /// <summary>
        /// Specifies additional options for the System.Text.JSON serializer.
        /// </summary>
        public JsonSerializerOptions JsonSerializerOptions { get; init; }
        
        /// <summary>
        /// Specifies the desired behavior when a duplicate key insertion is requested.
        /// </summary>
        public DuplicateKeyBehaviorType AddDuplicateKeyBehavior { get; init; }
        
        /// <summary>
        /// Specifies the desired behavior when a duplicate key insertion is requested during a BulkAdd operation.
        /// </summary>
        public DuplicateKeyBehaviorType BulkAddDuplicateKeyBehavior { get; init; }
        
        /// <summary>
        /// Specifies the desired behavior when a requested record is not found.
        /// </summary>
        public ErrorBehaviorType UpdateKeyNotFoundBehavior { get; init; }

        /// <summary>
        /// Specifies the desired behavior when a requested record is not found.
        /// </summary>
        public ErrorBehaviorType RemoveKeyNotFoundBehavior { get; init; }

        /// <summary>
        /// Specifies the desired behavior when a write operation fails.
        /// </summary>
        public ErrorBehaviorType StorageFailureBehavior { get; init; }
        
        /// <summary>
        /// Specifies the desired behavior when a serialization operation fails.
        /// </summary>
        public ErrorBehaviorType SerializationFailureBehavior { get; init; }
        
        /// <summary>
        /// Specifies the desired behavior when a deserialization operation fails.
        /// </summary>
        public ErrorBehaviorType DeserializationFailureBehavior { get; init; }

        /// <summary>
        /// Assigns an optional function to modify database file names which are generated from collection
        /// class names.
        /// </summary>
        public Func<string, string> GeneratedFileNameCreationFunction { get; set; }


        /// <summary>
        /// Default constructor for Stellar.FastDB configuration options.
        /// </summary>
        public FastDBOptions()
        {
        }

        internal FastDBOptions(FastDBOptions copy)
        {
            DatabaseName = copy.DatabaseName;
            BaseDirectory = copy.BaseDirectory;
            FileExtension = copy.FileExtension;
            Serializer = copy.Serializer;
            Durability = copy.Durability;
            MaxBufferedBytes = copy.MaxBufferedBytes;
            CompactionEffort = copy.CompactionEffort;
            CompactionIdleDelay = copy.CompactionIdleDelay;
            LoadBatchSize = copy.LoadBatchSize;
            ParallelBulkThreshold = copy.ParallelBulkThreshold;
            ParallelBulkChunkSize = copy.ParallelBulkChunkSize;
            IsMemoryOnlyEnabled = copy.IsMemoryOnlyEnabled;
            IsReadOnlyEnabled = copy.IsReadOnlyEnabled;
            IsEncryptionEnabled = copy.IsEncryptionEnabled;
            EncryptionPassword = copy.EncryptionPassword;
            EncryptionAlgorithm = copy.EncryptionAlgorithm;
            IsCompressionEnabled = copy.IsCompressionEnabled;
            MessagePackOptions = copy.MessagePackOptions;
            JsonSerializerOptions = copy.JsonSerializerOptions;
            AddDuplicateKeyBehavior = copy.AddDuplicateKeyBehavior;
            BulkAddDuplicateKeyBehavior = copy.BulkAddDuplicateKeyBehavior;
            UpdateKeyNotFoundBehavior = copy.UpdateKeyNotFoundBehavior;
            RemoveKeyNotFoundBehavior = copy.RemoveKeyNotFoundBehavior;
            StorageFailureBehavior = copy.StorageFailureBehavior;
            SerializationFailureBehavior = copy.SerializationFailureBehavior;
            DeserializationFailureBehavior = copy.DeserializationFailureBehavior;
        }
    }
}