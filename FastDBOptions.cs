using MessagePack;
using System;
using System.IO;
using System.Text.Json;

namespace Stellar.Collections
{
    public enum SerializerType : byte
    {
        MessagePack_Contractless,
        MessagePack_Contract,
        SystemTextJson_JSON,
    }

    public enum BufferModeType
    {
        Disabled,
        WriteEnabled,
        WriteParallelEnabled,
    }

    public enum DuplicateKeyBehaviorType
    {
        Exception,
        Upsert,
        ReturnFalse,
    }

    public enum ErrorBehaviorType
    {
        Exception,
        ReturnFalse,
    }


     public sealed class FastDBOptions
    {
        public string BaseDirectory { get; init; } = Environment.CurrentDirectory;
        public string DatabaseName { get; init; } = "Fast_DB";
        public string DirectoryPath => Path.Combine(BaseDirectory, DatabaseName);
        public string FileExtension { get; init; } = "db";
        public SerializerType Serializer { get; init; }
        public BufferModeType BufferMode { get; init; }
        public int MaxDegreeOfParallelism { get; init; } = 16;
        public bool IsMemoryOnlyEnabled { get; init; }
        public bool IsReadOnlyEnabled { get; init; }
        public bool IsEncryptionEnabled { get; init; }
        public string EncryptionPassword { get; init; }
        public string EncryptionSalt { get; init; }
        public bool IsCompressionEnabled { get; init; }
        public MessagePackSerializerOptions MessagePackOptions { get; init; }
        public JsonSerializerOptions JsonSerializerOptions { get; init; }
        public DuplicateKeyBehaviorType AddDuplicateKeyBehavior { get; init; }
        public DuplicateKeyBehaviorType BulkAddDuplicateKeyBehavior { get; init; }
        public ErrorBehaviorType KeyNotFoundBehavior { get; init; }
        public ErrorBehaviorType StorageFailureBehavior { get; init; }
        public ErrorBehaviorType SerializationFailureBehavior { get; init; }
        public ErrorBehaviorType DeserializationFailureBehavior { get; init; }


        public FastDBOptions()
        {
        }

        public FastDBOptions(FastDBOptions copy)
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
            EncryptionSalt = copy.EncryptionSalt;
            IsCompressionEnabled = copy.IsCompressionEnabled;
            MessagePackOptions = copy.MessagePackOptions;
            JsonSerializerOptions = copy.JsonSerializerOptions;
            AddDuplicateKeyBehavior = copy.AddDuplicateKeyBehavior;
            BulkAddDuplicateKeyBehavior = copy.BulkAddDuplicateKeyBehavior;
            KeyNotFoundBehavior = copy.KeyNotFoundBehavior;
            StorageFailureBehavior = copy.StorageFailureBehavior;
            SerializationFailureBehavior = copy.SerializationFailureBehavior;
            DeserializationFailureBehavior = copy.DeserializationFailureBehavior;
        }
    }
}