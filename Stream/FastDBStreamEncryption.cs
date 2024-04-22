using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Stellar.Collections
{
    internal sealed partial class FastDBStream<TKey, TValue> where TKey : struct
    {
        private Aes _Aes; // thread safe
        private ICryptoTransform _AesEncryptor;  // not thread safe
        private ICryptoTransform _AesDecryptor;  // not thread safe
        private MemoryStream _EncryptionStream = new MemoryStream(); // not thread safe
        private MemoryStream _DecryptionStream = new MemoryStream(); // not thread safe

        private void InitializeAesEncryption()
        {
            _Aes = Aes.Create();
            byte[] saltBytes = Encoding.UTF8.GetBytes(Options.EncryptionSalt);
            Rfc2898DeriveBytes pdb = new Rfc2898DeriveBytes(Options.EncryptionPassword, saltBytes, 1000, HashAlgorithmName.SHA256);

            byte[] keyIVBytes = pdb.GetBytes((_Aes.KeySize / 8) + (_Aes.BlockSize / 8));
            _Aes.Key = keyIVBytes.Take(_Aes.KeySize / 8).ToArray();
            _Aes.IV = keyIVBytes.Skip(_Aes.KeySize / 8).Take(_Aes.BlockSize / 8).ToArray();

            _AesEncryptor = _Aes.CreateEncryptor();
            _AesDecryptor = _Aes.CreateDecryptor();
        }

        private byte[] Encrypt(byte[] dataToEncrypt)
        {
            if (Options.BufferMode == BufferModeType.WriteParallelEnabled) // multithreaded
            {
                using (MemoryStream encryptionStream = new MemoryStream())
                using (ICryptoTransform aesEncryptor = _Aes.CreateEncryptor())
                using (CryptoStream cryptoStream = new CryptoStream(encryptionStream, aesEncryptor, CryptoStreamMode.Write, leaveOpen: false))
                {
                    cryptoStream.Write(dataToEncrypt);
                    cryptoStream.FlushFinalBlock();

                    byte[] result = encryptionStream.ToArray();
                    Debug.Assert(result != null && result.Length > 0);
                    return result;
                }
            }
            else
            {
                _EncryptionStream.SetLength(0);
                using (CryptoStream cryptoStream = new CryptoStream(_EncryptionStream, _AesEncryptor, CryptoStreamMode.Write, leaveOpen: true))
                {
                    cryptoStream.Write(dataToEncrypt);
                    cryptoStream.FlushFinalBlock();

                    byte[] result = _EncryptionStream.ToArray();
                    Debug.Assert(result != null && result.Length > 0);
                    return result;
                }
            }
        }

        private byte[] Decrypt(byte[] dataToDecrypt)
        {
            Debug.Assert(dataToDecrypt != null && dataToDecrypt.Length > 0);

            if (Options.BufferMode == BufferModeType.WriteParallelEnabled)
            {
                using (MemoryStream decryptionStream = new MemoryStream())
                {
                    decryptionStream.Write(dataToDecrypt, 0, dataToDecrypt.Length);
                    decryptionStream.Seek(0, SeekOrigin.Begin);

                    using (ICryptoTransform aesDecryptor = _Aes.CreateDecryptor())
                    using (CryptoStream cryptoStream = new CryptoStream(decryptionStream, aesDecryptor, CryptoStreamMode.Read, leaveOpen: false))
                    using (MemoryStream memoryStream = new MemoryStream())
                    {
                        cryptoStream.CopyTo(memoryStream);
                        byte[] result = memoryStream.ToArray();
                        Debug.Assert(result != null && result.Length > 0);
                        return result;
                    }
                }
            }
            else
            {
                _DecryptionStream.SetLength(0);
                _DecryptionStream.Write(dataToDecrypt, 0, dataToDecrypt.Length);
                _DecryptionStream.Seek(0, SeekOrigin.Begin);

                using (CryptoStream cryptoStream = new CryptoStream(_DecryptionStream, _AesDecryptor, CryptoStreamMode.Read, leaveOpen: true))
                using (MemoryStream memoryStream = new MemoryStream())
                {
                    cryptoStream.CopyTo(memoryStream);
                    byte[] result = memoryStream.ToArray();
                    Debug.Assert(result != null && result.Length > 0);
                    return result;
                }
            }
        }
    }
}