using System.Security.Cryptography;
using System.Text;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.Tests
{
    internal sealed class TestCredentialProtector : ICredentialProtector
    {
        private static readonly byte[] Key = SHA256.HashData(
            Encoding.UTF8.GetBytes("LectureCopilot.Tests/CredentialProtector/v1"));

        public byte[] Protect(byte[] plaintext)
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] tag = new byte[16];
            byte[] ciphertext = new byte[plaintext.Length];
            using var aes = new AesGcm(Key, tag.Length);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);

            byte[] protectedBytes = new byte[nonce.Length + tag.Length + ciphertext.Length];
            Buffer.BlockCopy(nonce, 0, protectedBytes, 0, nonce.Length);
            Buffer.BlockCopy(tag, 0, protectedBytes, nonce.Length, tag.Length);
            Buffer.BlockCopy(ciphertext, 0, protectedBytes, nonce.Length + tag.Length, ciphertext.Length);
            return protectedBytes;
        }

        public byte[] Unprotect(byte[] ciphertext)
        {
            if (ciphertext.Length < 28)
                throw new CryptographicException("Simulated protected payload is truncated.");

            ReadOnlySpan<byte> nonce = ciphertext.AsSpan(0, 12);
            ReadOnlySpan<byte> tag = ciphertext.AsSpan(12, 16);
            ReadOnlySpan<byte> encrypted = ciphertext.AsSpan(28);
            byte[] plaintext = new byte[encrypted.Length];
            using var aes = new AesGcm(Key, tag.Length);
            aes.Decrypt(nonce, encrypted, tag, plaintext);
            return plaintext;
        }
    }
}
