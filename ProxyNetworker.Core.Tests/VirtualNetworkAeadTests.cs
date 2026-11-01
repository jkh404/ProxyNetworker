using ProxyNetworker.Core;

namespace ProxyNetworker.Core.Tests;

public sealed class VirtualNetworkAeadTests
{
    private static readonly byte[] Key = Convert.FromHexString(
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");

    private static readonly byte[] Nonce = Convert.FromHexString("000102030405060708090A0B");

    private static readonly byte[] AssociatedData = Convert.FromHexString("FEEDFACEDEADBEEF");

    [Fact]
    public void EncryptMatchesStandardGcmVector()
    {
        var plaintext = "ProxyNetworker standard GCM vector"u8;
        var expectedCiphertext = Convert.FromHexString(
            "1770B963BCABA76FFA2EE5E0D49B581EF7B7E95091093B5C7F24A8A56B0C63C66E62");
        var expectedTag = Convert.FromHexString("1501BC0200F6AA09590E9753E14B5A09");
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[VirtualNetworkAead.TagSize];

        VirtualNetworkAead.Encrypt(Key, Nonce, plaintext, ciphertext, tag, AssociatedData);

        Assert.Equal(expectedCiphertext, ciphertext);
        Assert.Equal(expectedTag, tag);
    }

    [Fact]
    public void ManagedFallbackMatchesStandardGcmVector()
    {
        var plaintext = "ProxyNetworker standard GCM vector"u8;
        var expectedCiphertext = Convert.FromHexString(
            "1770B963BCABA76FFA2EE5E0D49B581EF7B7E95091093B5C7F24A8A56B0C63C66E62");
        var expectedTag = Convert.FromHexString("1501BC0200F6AA09590E9753E14B5A09");
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[VirtualNetworkAead.TagSize];

        VirtualNetworkAead.EncryptManaged(Key, Nonce, plaintext, ciphertext, tag, AssociatedData);

        Assert.Equal(expectedCiphertext, ciphertext);
        Assert.Equal(expectedTag, tag);
        Assert.True(VirtualNetworkAead.TryDecryptManaged(
            Key,
            Nonce,
            expectedCiphertext,
            expectedTag,
            new byte[plaintext.Length],
            AssociatedData));
    }

    [Fact]
    public void EmptyPlaintextMatchesStandardGcmVector()
    {
        var expectedTag = Convert.FromHexString("1EE657D890D87462B866D98362ADD6CA");
        var tag = new byte[VirtualNetworkAead.TagSize];

        VirtualNetworkAead.Encrypt(Key, Nonce, [], [], tag, AssociatedData);

        Assert.Equal(expectedTag, tag);
    }

    [Fact]
    public void DecryptAcceptsStandardVectorAndRejectsTampering()
    {
        var expectedPlaintext = "ProxyNetworker standard GCM vector"u8.ToArray();
        var ciphertext = Convert.FromHexString(
            "1770B963BCABA76FFA2EE5E0D49B581EF7B7E95091093B5C7F24A8A56B0C63C66E62");
        var tag = Convert.FromHexString("1501BC0200F6AA09590E9753E14B5A09");
        var plaintext = new byte[ciphertext.Length];

        Assert.True(VirtualNetworkAead.TryDecrypt(Key, Nonce, ciphertext, tag, plaintext, AssociatedData));
        Assert.Equal(expectedPlaintext, plaintext);

        tag[0] ^= 0x01;
        Assert.False(VirtualNetworkAead.TryDecrypt(Key, Nonce, ciphertext, tag, plaintext, AssociatedData));
    }

    [Fact]
    public void InPlaceDecryptAcceptsStandardVector()
    {
        var expectedPlaintext = "ProxyNetworker standard GCM vector"u8.ToArray();
        var payload = Convert.FromHexString(
            "1770B963BCABA76FFA2EE5E0D49B581EF7B7E95091093B5C7F24A8A56B0C63C66E62");
        var tag = Convert.FromHexString("1501BC0200F6AA09590E9753E14B5A09");

        Assert.True(VirtualNetworkAead.TryDecryptInPlace(Key, Nonce, payload, tag, AssociatedData));
        Assert.Equal(expectedPlaintext, payload);
    }
}
