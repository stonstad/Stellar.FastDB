using MessagePack;
using System;
using System.Diagnostics;
using System.Text.Json;

namespace Stellar.Collections
{
    internal sealed partial class FastDBStream<TKey, TValue> where TKey : struct
    {
        // appends one record's content to writer
        private void Serialize(PooledBufferWriter writer, TKey key, TValue value)
        {
            int start = writer.Length;

            if (Options.Serializer == SerializerType.MessagePack_Contractless || Options.Serializer == SerializerType.MessagePack_Contract)
                MessagePackSerializer.Serialize(writer, (key, value), _MessagePackOptions);
            else if (Options.Serializer == SerializerType.SystemTextJson_JSON)
            {
                Utf8JsonWriter json = writer.GetJsonWriter(_JsonWriterOptions);
                JsonSerializer.Serialize(json, new T() { K = key, V = value }, _JsonSerializerOptions);
                json.Flush();
            }
            else
                throw new NotImplementedException();

            if (Options.IsEncryptionEnabled)
                EncryptRecord(writer, start);

            Debug.Assert(writer.Length > start, $"Failure serializing type ({nameof(TKey)},{nameof(TValue)})");
        }

        // appends one key's content to writer
        private void SerializeKey(PooledBufferWriter writer, TKey key)
        {
            int start = writer.Length;

            if (Options.Serializer == SerializerType.MessagePack_Contractless || Options.Serializer == SerializerType.MessagePack_Contract)
                MessagePackSerializer.Serialize(writer, key, _MessagePackOptions);
            else if (Options.Serializer == SerializerType.SystemTextJson_JSON)
            {
                Utf8JsonWriter json = writer.GetJsonWriter(_JsonWriterOptions);
                JsonSerializer.Serialize(json, key, _JsonSerializerOptions);
                json.Flush();
            }
            else
                throw new NotImplementedException();

            if (Options.IsEncryptionEnabled)
                EncryptRecord(writer, start);

            Debug.Assert(writer.Length > start, $"Failure serializing type ({nameof(TKey)})");
        }

        // content is already decrypted
        private (TKey key, TValue value) Deserialize(ReadOnlyMemory<byte> content)
        {
            Debug.Assert(content.Length > 0);

            if (Options.Serializer == SerializerType.MessagePack_Contractless || Options.Serializer == SerializerType.MessagePack_Contract)
                return MessagePackSerializer.Deserialize<(TKey, TValue)>(content, _MessagePackOptions);
            else if (Options.Serializer == SerializerType.SystemTextJson_JSON)
            {
                T record = JsonSerializer.Deserialize<T>(content.Span, _JsonSerializerOptions);
                return (record.K, record.V);
            }
            else
                throw new NotImplementedException();
        }

        // content is already decrypted
        private TKey DeserializeKey(ReadOnlyMemory<byte> content)
        {
            Debug.Assert(content.Length > 0);

            if (Options.Serializer == SerializerType.MessagePack_Contractless || Options.Serializer == SerializerType.MessagePack_Contract)
                return MessagePackSerializer.Deserialize<TKey>(content, _MessagePackOptions);
            else if (Options.Serializer == SerializerType.SystemTextJson_JSON)
                return JsonSerializer.Deserialize<TKey>(content.Span, _JsonSerializerOptions);
            else
                throw new NotImplementedException();
        }
    }
}
