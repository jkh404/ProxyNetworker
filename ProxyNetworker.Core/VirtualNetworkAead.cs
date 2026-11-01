using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace ProxyNetworker.Core;

// Some linux-arm64 Native AOT environments have produced self-consistent tags
// that other AEAD implementations reject. Run a known-answer check once and
// fall back to managed GCM only when the platform implementation is not interoperable.
internal static class VirtualNetworkAead
{
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;

    private const int TagSizeBits = TagSize * 8;
    private static readonly bool UseSystemAesGcm = IsSystemAesGcmInteroperable();

    [ThreadStatic]
    private static AesGcm? _systemAesGcm;

    [ThreadStatic]
    private static byte[]? _systemAesKey;

    internal static string BackendName { get; } = UseSystemAesGcm
        ? "System.Security.Cryptography.AesGcm"
        : "BouncyCastle.GcmBlockCipher";

    public static void Encrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> plaintext,
        Span<byte> ciphertext,
        Span<byte> tag,
        ReadOnlySpan<byte> associatedData)
    {
        ValidateEncryptionBuffers(key, nonce, plaintext.Length, ciphertext.Length, tag.Length);

        if (UseSystemAesGcm)
        {
            EncryptSystem(key, nonce, plaintext, ciphertext, tag, associatedData);
            return;
        }

        EncryptManaged(key, nonce, plaintext, ciphertext, tag, associatedData);
    }

    internal static void EncryptManaged(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> plaintext,
        Span<byte> ciphertext,
        Span<byte> tag,
        ReadOnlySpan<byte> associatedData)
    {
        var cipher = CreateCipher(true, key, nonce, associatedData);
        var combined = new byte[checked(plaintext.Length + TagSize)];
        var written = plaintext.Length == 0
            ? 0
            : cipher.ProcessBytes(plaintext.ToArray(), 0, plaintext.Length, combined, 0);
        written += cipher.DoFinal(combined, written);
        if (written != combined.Length)
        {
            throw new CryptographicException("AES-GCM encryption returned an unexpected output length.");
        }

        combined.AsSpan(0, ciphertext.Length).CopyTo(ciphertext);
        combined.AsSpan(ciphertext.Length, TagSize).CopyTo(tag);
    }

    public static bool TryDecrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag,
        Span<byte> plaintext,
        ReadOnlySpan<byte> associatedData)
    {
        if (key.Length != KeySize ||
            nonce.Length != NonceSize ||
            tag.Length != TagSize ||
            ciphertext.Length != plaintext.Length)
        {
            return false;
        }

        return UseSystemAesGcm
            ? TryDecryptSystem(key, nonce, ciphertext, tag, plaintext, associatedData)
            : TryDecryptManaged(key, nonce, ciphertext, tag, plaintext, associatedData);
    }

    public static bool TryDecryptInPlace(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        Span<byte> payload,
        ReadOnlySpan<byte> tag,
        ReadOnlySpan<byte> associatedData)
    {
        if (key.Length != KeySize ||
            nonce.Length != NonceSize ||
            tag.Length != TagSize)
        {
            return false;
        }

        if (UseSystemAesGcm)
        {
            return TryDecryptSystem(key, nonce, payload, tag, payload, associatedData);
        }

        var plaintext = new byte[payload.Length];
        if (!TryDecryptManaged(key, nonce, payload, tag, plaintext, associatedData))
        {
            return false;
        }

        plaintext.CopyTo(payload);
        return true;
    }

    internal static bool TryDecryptManaged(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag,
        Span<byte> plaintext,
        ReadOnlySpan<byte> associatedData)
    {
        var combined = new byte[checked(ciphertext.Length + tag.Length)];
        ciphertext.CopyTo(combined);
        tag.CopyTo(combined.AsSpan(ciphertext.Length));
        var output = new byte[plaintext.Length];
        var cipher = CreateCipher(false, key, nonce, associatedData);

        try
        {
            var written = combined.Length == 0
                ? 0
                : cipher.ProcessBytes(combined, 0, combined.Length, output, 0);
            written += cipher.DoFinal(output, written);
            if (written != plaintext.Length)
            {
                return false;
            }

            output.CopyTo(plaintext);
            return true;
        }
        catch (Org.BouncyCastle.Crypto.InvalidCipherTextException)
        {
            return false;
        }
    }

    private static void EncryptSystem(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> plaintext,
        Span<byte> ciphertext,
        Span<byte> tag,
        ReadOnlySpan<byte> associatedData)
    {
        GetSystemAesGcm(key).Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
    }

    private static bool TryDecryptSystem(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag,
        Span<byte> plaintext,
        ReadOnlySpan<byte> associatedData)
    {
        try
        {
            GetSystemAesGcm(key).Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static AesGcm GetSystemAesGcm(ReadOnlySpan<byte> key)
    {
        if (_systemAesGcm is not null && _systemAesKey is not null && CryptographicOperations.FixedTimeEquals(_systemAesKey, key))
        {
            return _systemAesGcm;
        }

        _systemAesGcm?.Dispose();
        _systemAesKey = key.ToArray();
        _systemAesGcm = new AesGcm(_systemAesKey, TagSize);
        return _systemAesGcm;
    }

    private static bool IsSystemAesGcmInteroperable()
    {
        if (!AesGcm.IsSupported)
        {
            return false;
        }

        var key = Enumerable.Range(0, KeySize).Select(index => (byte)index).ToArray();
        var associatedData = Enumerable.Range(0, 36).Select(index => (byte)(index * 7 + 3)).ToArray();

        try
        {
            foreach (var length in new[] { 0, 1, 16, 17, 1400, 1500 })
            {
                var nonce = Enumerable.Range(0, NonceSize)
                    .Select(index => (byte)(index * 17 + length))
                    .ToArray();
                var plaintext = Enumerable.Range(0, length)
                    .Select(index => (byte)(index * 31 + 7))
                    .ToArray();
                var systemCiphertext = new byte[length];
                var systemTag = new byte[TagSize];
                var managedCiphertext = new byte[length];
                var managedTag = new byte[TagSize];
                var decrypted = new byte[length];

                EncryptSystem(key, nonce, plaintext, systemCiphertext, systemTag, associatedData);
                EncryptManaged(key, nonce, plaintext, managedCiphertext, managedTag, associatedData);
                if (!systemCiphertext.AsSpan().SequenceEqual(managedCiphertext) ||
                    !systemTag.AsSpan().SequenceEqual(managedTag) ||
                    !TryDecryptSystem(key, nonce, managedCiphertext, managedTag, decrypted, associatedData) ||
                    !decrypted.AsSpan().SequenceEqual(plaintext))
                {
                    return false;
                }
            }

            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static GcmBlockCipher CreateCipher(
        bool forEncryption,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> associatedData)
    {
        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(
            forEncryption,
            new AeadParameters(
                new KeyParameter(key.ToArray()),
                TagSizeBits,
                nonce.ToArray(),
                associatedData.ToArray()));
        return cipher;
    }

    private static void ValidateEncryptionBuffers(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        int plaintextLength,
        int ciphertextLength,
        int tagLength)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException("AES-GCM key must be 32 bytes.", nameof(key));
        }

        if (nonce.Length != NonceSize)
        {
            throw new ArgumentException("AES-GCM nonce must be 12 bytes.", nameof(nonce));
        }

        if (ciphertextLength != plaintextLength)
        {
            throw new ArgumentException("AES-GCM ciphertext buffer length must match the plaintext.", nameof(ciphertextLength));
        }

        if (tagLength != TagSize)
        {
            throw new ArgumentException("AES-GCM tag must be 16 bytes.", nameof(tagLength));
        }
    }
}
