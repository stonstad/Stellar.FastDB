using MessagePack;
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Stellar.Collections
{
    internal sealed partial class FastDBStream<TKey, TValue> where TKey : struct
    {
        private byte[] Serialize(TKey key, TValue value)
        {
            byte[] contentBytes;

            if (Options.Serializer == SerializerType.MessagePack_Contractless || Options.Serializer == SerializerType.MessagePack_Contract)
                contentBytes = MessagePackSerializer.Serialize((key, value), _MessagePackOptions);
            else if (Options.Serializer == SerializerType.SystemTextJson_JSON)
                contentBytes = JsonSerializer.SerializeToUtf8Bytes(new T() { K = key, V = value }, _JsonSerializerOptions);
            else
                throw new NotImplementedException();

            if (Options.IsEncryptionEnabled)
                contentBytes = Encrypt(contentBytes);

            Debug.Assert(contentBytes != null && contentBytes.Length > 0);

            return contentBytes;
        }

        private (TKey key, TValue value) Deserialize(byte[] contentBytes)
        {
            Debug.Assert(contentBytes != null && contentBytes.Length > 0);

            if (Options.IsEncryptionEnabled)
                contentBytes = Decrypt(contentBytes);

            if (Options.Serializer == SerializerType.MessagePack_Contractless || Options.Serializer == SerializerType.MessagePack_Contract)
                return MessagePackSerializer.Deserialize<(TKey, TValue)>(contentBytes, _MessagePackOptions);
            else if (Options.Serializer == SerializerType.SystemTextJson_JSON)
            {
                T record = JsonSerializer.Deserialize<T>(contentBytes, _JsonSerializerOptions);
                return (record.K, record.V);
            }
            else
                throw new NotImplementedException();
        }

        private void Serialize(Stream stream, TKey key, TValue value)
        {
            if (Options.IsEncryptionEnabled)
            {
                using (CryptoStream cryptoStream = new CryptoStream(stream, _AesEncryptor, CryptoStreamMode.Write, leaveOpen: true))
                {
                    if (Options.Serializer == SerializerType.MessagePack_Contractless || Options.Serializer == SerializerType.MessagePack_Contract)
                        MessagePackSerializer.Serialize(cryptoStream, (key, value), _MessagePackOptions);
                    else if (Options.Serializer == SerializerType.SystemTextJson_JSON)
                        JsonSerializer.Serialize(cryptoStream, new T() { K = key, V = value }, _JsonSerializerOptions);
                    else
                        throw new NotImplementedException();
                    cryptoStream.FlushFinalBlock();
                }
            }
            else
            {
                if (Options.Serializer == SerializerType.MessagePack_Contractless || Options.Serializer == SerializerType.MessagePack_Contract)
                    MessagePackSerializer.Serialize(stream, (key, value), _MessagePackOptions);
                else if (Options.Serializer == SerializerType.SystemTextJson_JSON)
                    JsonSerializer.Serialize(stream, new T() { K = key, V = value }, _JsonSerializerOptions);
                else
                    throw new NotImplementedException();
            }
        }

        private (TKey key, TValue value) Deserialize(Stream stream, int bytesLength)
        {
            Debug.Assert(bytesLength > 0);

            if (Options.IsEncryptionEnabled)
            {
                //byte[] buffer = new byte[bytesLength];
                byte[] buffer = _BufferPool.Rent(bytesLength);
                stream.Read(buffer);

                using (MemoryStream intermediateStream = new MemoryStream(buffer))
                {
                    intermediateStream.Position = 0;

                    using (CryptoStream cryptoStream = new CryptoStream(intermediateStream, _AesDecryptor, CryptoStreamMode.Read, leaveOpen: true))
                    {
                        if (Options.Serializer == SerializerType.MessagePack_Contractless || Options.Serializer == SerializerType.MessagePack_Contract)
                        {
                            _BufferPool.Return(buffer);
                            (TKey, TValue) value = MessagePackSerializer.Deserialize<(TKey, TValue)>(cryptoStream, _MessagePackOptions);
                            return value;
                        }
                        else if (Options.Serializer == SerializerType.SystemTextJson_JSON)
                        {
                            _BufferPool.Return(buffer);
                            T value = JsonSerializer.Deserialize<T>(cryptoStream, _JsonSerializerOptions);
                            return (value.K, value.V);
                        }
                        else
                            throw new NotImplementedException();
                    }
                }
            }
            else
            {
                //byte[] buffer = new byte[bytesLength];
                byte[] buffer = _BufferPool.Rent(bytesLength);
                stream.Read(buffer);

                if (Options.Serializer == SerializerType.MessagePack_Contractless || Options.Serializer == SerializerType.MessagePack_Contract)
                {
                    Memory<byte> data = new Memory<byte>(buffer);
                    (TKey, TValue) value = MessagePackSerializer.Deserialize<(TKey, TValue)>(data, _MessagePackOptions);
                    _BufferPool.Return(buffer);
                    return value;
                }
                else if (Options.Serializer == SerializerType.SystemTextJson_JSON)
                {
                    ReadOnlySpan<byte> dataSpan = new ReadOnlySpan<byte>(buffer);
                    T value = JsonSerializer.Deserialize<T>(dataSpan, _JsonSerializerOptions);
                    _BufferPool.Return(buffer);
                    return (value.K, value.V);
                }
                else
                    throw new NotImplementedException();
            }
        }
    }
}