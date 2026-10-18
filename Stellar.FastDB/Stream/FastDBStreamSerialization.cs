using MessagePack;
using MessagePack.Formatters;
using System;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

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

            Debug.Assert(writer.Length > start, $"Failure serializing type ({typeof(TKey).Name},{typeof(TValue).Name})");
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

            Debug.Assert(writer.Length > start, $"Failure serializing type ({typeof(TKey).Name})");
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

        // for a record whose value failed to deserialize: reads just its key, skipping the value without decoding it,
        // so the record can still be indexed under its key; content is already decrypted
        private bool TryDeserializeKeyOnly(ReadOnlyMemory<byte> content, out TKey key)
        {
            try
            {
                if (Options.Serializer == SerializerType.MessagePack_Contractless || Options.Serializer == SerializerType.MessagePack_Contract)
                    key = MessagePackSerializer.Deserialize<(TKey, SkippedValue)>(content, _KeyOnlyMessagePackOptions).Item1;
                else if (Options.Serializer == SerializerType.SystemTextJson_JSON)
                    key = JsonSerializer.Deserialize<KeyOnly>(content.Span, _JsonSerializerOptions).K;
                else
                    throw new NotImplementedException();
                return true;
            }
            catch
            {
                key = default;
                return false;
            }
        }

        // a JSON record's key alone; K is required, so a record without one isn't taken for the default key
        private struct KeyOnly
        {
            [JsonRequired]
            public TKey K { get; set; }
        }

        private struct SkippedValue
        {
        }

        // reads past a MessagePack value without decoding it. It's handed to a CompositeResolver directly, so the source
        // generator, which can't use a type nested in a generic class, is kept away from it.
#pragma warning disable MsgPack010 // Formatter should be declared with at least internal visibility
        private sealed class SkipFormatter : IMessagePackFormatter<SkippedValue>
        {
            public void Serialize(ref MessagePackWriter writer, SkippedValue value, MessagePackSerializerOptions options)
            {
                throw new NotSupportedException();
            }

            public SkippedValue Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                reader.Skip();
                return default;
            }
        }
#pragma warning restore MsgPack010
    }
}
