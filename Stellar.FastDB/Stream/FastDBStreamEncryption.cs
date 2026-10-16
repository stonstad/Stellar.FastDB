using System;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace Stellar.Collections
{
    internal sealed partial class FastDBStream<TKey, TValue> where TKey : struct
    {
        // Every record is encrypted with AES-CBC under the same key and IV, derived from the password and the file's
        // salt. An Aes instance isn't thread safe, so each thread that writes records encrypts with its own.
        private Aes _Aes;                       // the header's checksum, at open
        private ThreadLocal<Aes> _Encryptors;   // records, one per writing thread
        private byte[] _AesIV;
        private byte[] _EncryptionSalt;
        private byte[] _EncryptionChecksum;

        public static byte[] GenerateEncryptionSalt()
        {
            byte[] saltBytes = new byte[16];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                rng.GetBytes(saltBytes);
            return saltBytes;
        }

        private void InitializeAesEncryption(byte[] encryptionSalt)
        {
            Debug.Assert(encryptionSalt != null && encryptionSalt.Length > 0);

            _EncryptionSalt = encryptionSalt;
            _Aes = Aes.Create();
#if (NET10_0_OR_GREATER)
            byte[] keyIVBytes =  Rfc2898DeriveBytes.Pbkdf2(Options.EncryptionPassword, encryptionSalt, 1000, Options.EncryptionAlgorithm, (_Aes.KeySize / 8) + (_Aes.BlockSize / 8));
#else
            Rfc2898DeriveBytes pdb = new Rfc2898DeriveBytes(Options.EncryptionPassword, encryptionSalt, 1000, Options.EncryptionAlgorithm);
            byte[] keyIVBytes = pdb.GetBytes((_Aes.KeySize / 8) + (_Aes.BlockSize / 8));
#endif
            _Aes.Key = keyIVBytes.Take(_Aes.KeySize / 8).ToArray();
            _Aes.IV = keyIVBytes.Skip(_Aes.KeySize / 8).Take(_Aes.BlockSize / 8).ToArray();
            _AesIV = _Aes.IV;

            byte[] key = _Aes.Key;
            _Encryptors = new ThreadLocal<Aes>(() =>
            {
                Aes encryptor = Aes.Create();
                encryptor.Key = key;
                return encryptor;
            }, trackAllValues: true);
        }

        // at open only
        private byte[] Encrypt(ReadOnlySpan<byte> data)
        {
            return _Aes.EncryptCbc(data, _AesIV, PaddingMode.PKCS7);
        }

        // at open only
        private byte[] Decrypt(byte[] data)
        {
            return _Aes.DecryptCbc(data, _AesIV, PaddingMode.PKCS7);
        }

        // encrypts the writer's content from start on, in place
        private void EncryptRecord(PooledBufferWriter writer, int start)
        {
            Aes encryptor = _Encryptors.Value;
            int length = encryptor.GetCiphertextLengthCbc(writer.Length - start, PaddingMode.PKCS7);

            // the ciphertext goes after the plaintext, as the two can't overlap, then moves down over it
            Span<byte> output = writer.GetSpan(length);
            if (!encryptor.TryEncryptCbc(writer.WrittenSpan.Slice(start), _AesIV, output, out int written, PaddingMode.PKCS7))
                throw new CryptographicException();
            output.Slice(0, written).CopyTo(writer.GetWrittenSpan(start, written));
            writer.Truncate(start + written);
        }

        private void DisposeEncryption()
        {
            _Aes?.Dispose();
            _Aes = null;

            if (_Encryptors != null)
            {
                foreach (Aes encryptor in _Encryptors.Values)
                    encryptor.Dispose();
                _Encryptors.Dispose();
                _Encryptors = null;
            }
        }
    }
}
