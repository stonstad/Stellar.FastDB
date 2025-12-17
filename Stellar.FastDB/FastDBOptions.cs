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
    /// Specifies the write buffering mode which affects data consistency, parallelism, and write performance.
    /// </summary>
    public enum BufferModeType
    {
        /// <summary>
        /// Data is written without buffering. Reads and writes are immediate consistency in memory and in storage.
        /// </summary>
        Disabled,
        
        /// <summary>
        /// Data is written with buffering. Reads are immediate consistency in memory and eventual consistency in storage. Use Flush() or FlushAsync() to achieve storage immediate consistency.
        /// </summary>
        WriteEnabled,
        
        /// <summary>
        /// Data is written with buffering. Reads are immediate consistency in memory and eventual consistency in storage. Use Flush() or FlushAsync() to achieve storage immediate consistency. Serialization, compression, and encryption operations are performed in asynchronous threads.
        /// </summary>

        WriteParallelEnabled,
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
        /// Specifies the buffering mode to use when writing to the file system.
        /// </summary>
        public BufferModeType BufferMode { get; init; }
        
        /// <summary>
        /// Specifies the number of asynchronous threads to use while writing buffered data to the file system.
        /// </summary>
        public int MaxDegreeOfParallelism { get; init; } = 8;
        
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
        public HashAlgorithmName EncyptionAlgorithm { get; init; } = HashAlgorithmName.SHA256;
        
        /// <summary>
        /// Specifies whether compression is enabled.
        /// </summary>
        public bool IsCompressionEnabled { get; init; }

        /// <summary>
        /// Specifies whether a flush is skipped after a record is written to a stream. If disabled, streams are flushed after writing record data and again after committing the record state.
        /// </summary>
        public bool IsBufferedWritesEnabled { get; init; } = true;

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
            BufferMode = copy.BufferMode;
            MaxDegreeOfParallelism = copy.MaxDegreeOfParallelism;
            IsMemoryOnlyEnabled = copy.IsMemoryOnlyEnabled;
            IsReadOnlyEnabled = copy.IsReadOnlyEnabled;
            IsEncryptionEnabled = copy.IsEncryptionEnabled;
            EncryptionPassword = copy.EncryptionPassword;
            EncyptionAlgorithm = copy.EncyptionAlgorithm;
            IsCompressionEnabled = copy.IsCompressionEnabled;
            IsBufferedWritesEnabled = copy.IsBufferedWritesEnabled;
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