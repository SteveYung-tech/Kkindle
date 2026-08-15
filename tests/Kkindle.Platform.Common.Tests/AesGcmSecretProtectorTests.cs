using System.Security.Cryptography;
using System.Text;
using Kkindle.Platform.Common;
using Xunit;

namespace Kkindle.Platform.Common.Tests;

public sealed class AesGcmSecretProtectorTests
{
    [Fact]
    public void ProtectAndUnprotect_RoundTrips()
    {
        var protector = new TestProtector(RandomNumberGenerator.GetBytes(32));
        var plaintext = Encoding.UTF8.GetBytes("跨平台 secret");
        var protectedValue = protector.Protect(plaintext);
        Assert.Equal(plaintext, protector.Unprotect(protectedValue));
        Assert.NotEqual(plaintext, protectedValue);
    }

    [Fact]
    public void Protect_UsesFreshNonceForEveryBlob()
    {
        var protector = new TestProtector(RandomNumberGenerator.GetBytes(32));
        var plaintext = Encoding.UTF8.GetBytes("same input");
        Assert.NotEqual(protector.Protect(plaintext), protector.Protect(plaintext));
    }

    [Fact]
    public void Unprotect_RejectsTamperedCiphertext()
    {
        var protector = new TestProtector(RandomNumberGenerator.GetBytes(32));
        var protectedValue = protector.Protect(Encoding.UTF8.GetBytes("secret"));
        protectedValue[^1] ^= 0x01;
        Assert.Throws<AuthenticationTagMismatchException>(() => protector.Unprotect(protectedValue));
    }

    [Fact]
    public void EmptyValues_DoNotOpenKeyStore()
    {
        var protector = new TestProtector(RandomNumberGenerator.GetBytes(32));
        Assert.Empty(protector.Protect([]));
        Assert.Empty(protector.Unprotect([]));
        Assert.Equal(0, protector.KeyRequests);
    }

    private sealed class TestProtector(byte[] key) : AesGcmSecretProtector
    {
        public int KeyRequests { get; private set; }
        protected override byte[] GetOrCreateKey()
        {
            KeyRequests++;
            return [.. key];
        }
    }
}
