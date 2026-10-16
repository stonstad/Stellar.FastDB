using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;

namespace Stellar.Collections
{
    internal sealed partial class FastDBStream<TKey, TValue> where TKey : struct
    {
        private const ushort FormatVersion = 2;

        // magic (4), version (2), serializer (1), format (1), encryption salt (16), encryption checksum (16),
        // gap slot A (24), gap slot B (24), record count (8), reserved (32)
        private const int HeaderSize = 128;

        // gap start (8), gap size (8), sequence (4), crc32 of the first 20 bytes (4); the valid slot with the higher
        // sequence wins, so a torn header write leaves the other slot intact
        private const int GapSlotOffset = 40;
        private const int GapSlotSize = 24;

        // count (4), crc32 of the count (4); only sizes the in-memory index at open, so it's written at close and when
        // a sweep pass ends rather than on every write
        private const int RecordCountOffset = 88;
        private const int RecordCountSize = 8;

        // Each write call is one block: crc32 of the rest of the block (4), a varint header, then the payload.
        // One record: header is (content length << 2 | remove << 1), and the payload is the content.
        // Several records: header is (payload length << 1 | 1), and each record is a varint (content length << 1 | remove)
        // followed by the content.
        private const int CrcSize = sizeof(uint);
        private const int MaxVarintLength = 5;
        private const int BlockHeaderReserve = CrcSize + MaxVarintLength;

        private const int MaxBlockSize = 1024 * 1024;
        private const int ReadBufferSize = 1024 * 1024;

        private static ReadOnlySpan<byte> Magic => "FSDB"u8;

        private long _FileLength;

        // the gap a compaction sweep is moving through the file; size 0 when no sweep is under way
        private long _GapStart;
        private long _GapSize;
        private uint _GapSequence;

        private int _RecordCount;   // the count in the header, or 0 when it has none

        private void CreateHeaderInternal()
        {
            byte[] header = new byte[HeaderSize];
            Magic.CopyTo(header);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), FormatVersion);
            header[6] = (byte)Serializer;
            header[7] = (byte)_Format;
            if (Options.IsEncryptionEnabled)
            {
                _EncryptionChecksum = Encrypt(new byte[] { _EncryptionSalt[0], _EncryptionSalt[1] });
                Debug.Assert(_EncryptionChecksum.Length == 16);
                _EncryptionSalt.CopyTo(header, 8);
                _EncryptionChecksum.CopyTo(header, 24);
            }

            RandomAccess.Write(_Handle, header, 0);
            _FileLength = HeaderSize;
        }

        private void ReadHeaderInternal()
        {
            byte[] header = new byte[HeaderSize];
            int length = ReadAt(header, 0);

            if (length < Magic.Length || !header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            {
                // version 1 files have no magic and start with the version
                if (length >= sizeof(ushort) && BinaryPrimitives.ReadUInt16LittleEndian(header) == 1)
                    throw ThrowHelper.UnsupportedFileVersion(FilePath, 1);
                throw ThrowHelper.NotAFastDBFile(FilePath);
            }

            if (length < HeaderSize)
                throw ThrowHelper.NotAFastDBFile(FilePath);

            Version = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
            if (Version != FormatVersion)
                throw ThrowHelper.UnsupportedFileVersion(FilePath, Version);

            Serializer = (SerializerType)header[6];
            _Format = (FormatType)header[7];

            Options = new FastDBOptions(Options)
            {
                Serializer = Serializer,
                IsCompressionEnabled = _Format.HasFlag(FormatType.Compressed),
                IsEncryptionEnabled = _Format.HasFlag(FormatType.Encrypted),
            };

            _EncryptionSalt = header.AsSpan(8, 16).ToArray();
            _EncryptionChecksum = header.AsSpan(24, 16).ToArray();

            ReadGap(header);

            ReadOnlySpan<byte> count = header.AsSpan(RecordCountOffset, RecordCountSize);
            _RecordCount = 0;
            if (Crc32.HashToUInt32(count.Slice(0, sizeof(int))) == BinaryPrimitives.ReadUInt32LittleEndian(count.Slice(sizeof(int))))
                _RecordCount = Math.Max(0, BinaryPrimitives.ReadInt32LittleEndian(count));
        }

        // caller holds _CommitLock, or is loading
        private void WriteRecordCount()
        {
            int count = _Values.Count;
            if (count == _RecordCount)
                return;

            Span<byte> data = stackalloc byte[RecordCountSize];
            BinaryPrimitives.WriteInt32LittleEndian(data, count);
            BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(sizeof(int)), Crc32.HashToUInt32(data.Slice(0, sizeof(int))));
            RandomAccess.Write(_Handle, data, RecordCountOffset);
            _RecordCount = count;
        }

        private void ReadGap(byte[] header)
        {
            _GapStart = 0;
            _GapSize = 0;
            _GapSequence = 0;

            bool found = false;
            for (int slot = 0; slot < 2; slot++)
            {
                ReadOnlySpan<byte> data = header.AsSpan(GapSlotOffset + slot * GapSlotSize, GapSlotSize);
                if (Crc32.HashToUInt32(data.Slice(0, GapSlotSize - CrcSize)) != BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(GapSlotSize - CrcSize)))
                    continue;

                uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(16));
                if (found && (int)(sequence - _GapSequence) <= 0)
                    continue;

                found = true;
                _GapStart = BinaryPrimitives.ReadInt64LittleEndian(data);
                _GapSize = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(8));
                _GapSequence = sequence;
            }
        }

        // writes the gap to the slot the last write didn't use, returning the slot's offset; the caller flushes
        private int WriteGap(long start, long size)
        {
            uint sequence = _GapSequence + 1;
            byte[] slot = new byte[GapSlotSize];
            BinaryPrimitives.WriteInt64LittleEndian(slot, start);
            BinaryPrimitives.WriteInt64LittleEndian(slot.AsSpan(8), size);
            BinaryPrimitives.WriteUInt32LittleEndian(slot.AsSpan(16), sequence);
            BinaryPrimitives.WriteUInt32LittleEndian(slot.AsSpan(20), Crc32.HashToUInt32(slot.AsSpan(0, GapSlotSize - CrcSize)));

            int offset = GapSlotOffset + (int)(sequence % 2) * GapSlotSize;
            RandomAccess.Write(_Handle, slot, offset);
            _GapStart = start;
            _GapSize = size;
            _GapSequence = sequence;
            return offset;
        }

        private int ReadAt(Span<byte> buffer, long fileOffset)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = RandomAccess.Read(_Handle, buffer.Slice(total), fileOffset + total);
                if (read == 0)
                    break;
                total += read;
            }
            return total;
        }

        private static ulong RecordHeader(RecordType type, int contentLength)
        {
            ulong remove = 0;
            if (type == RecordType.Remove)
                remove = 1;
            return ((ulong)contentLength << 1) | remove;
        }

        private static int RecordSize(RecordType type, int contentLength)
        {
            return VarintLength(RecordHeader(type, contentLength)) + contentLength;
        }

        private static int VarintLength(ulong value)
        {
            int length = 1;
            while (value >= 0x80)
            {
                value >>= 7;
                length++;
            }
            return length;
        }

        private static int WriteVarint(byte[] buffer, int offset, ulong value)
        {
            int start = offset;
            while (value >= 0x80)
            {
                buffer[offset++] = (byte)(value | 0x80);
                value >>= 7;
            }
            buffer[offset++] = (byte)value;
            return offset - start;
        }

        private static bool TryReadVarint(ReadOnlySpan<byte> buffer, out ulong value, out int length)
        {
            value = 0;
            for (int i = 0; i < buffer.Length && i < MaxVarintLength; i++)
            {
                value |= (ulong)(buffer[i] & 0x7F) << (7 * i);
                if (buffer[i] < 0x80)
                {
                    length = i + 1;
                    return true;
                }
            }

            length = 0;
            return false;
        }
    }
}
