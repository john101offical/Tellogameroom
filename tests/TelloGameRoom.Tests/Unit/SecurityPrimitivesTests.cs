using Microsoft.Extensions.Options;
using TelloGameRoom.Server.Infrastructure;
using TelloGameRoom.Server.Security;

namespace TelloGameRoom.Tests.Unit;

public class SecurityPrimitivesTests
{
    private static Argon2PasswordHasher Hasher(int memoryKb = 1024, int iterations = 1) =>
        new(Options.Create(new SecurityOptions { Argon2MemoryKb = memoryKb, Argon2Iterations = iterations }));

    [Fact]
    public void Password_verifies_and_wrong_password_does_not()
    {
        var hasher = Hasher();
        var hash = hasher.Hash("correct horse battery");
        Assert.StartsWith("$argon2id$v=19$m=1024,t=1,p=1$", hash, StringComparison.Ordinal);
        Assert.True(hasher.Verify("correct horse battery", hash, out var rehash));
        Assert.False(rehash);
        Assert.False(hasher.Verify("Correct horse battery", hash, out _));
    }

    [Fact]
    public void Same_password_gets_a_different_hash_every_time()
    {
        var hasher = Hasher();
        Assert.NotEqual(hasher.Hash("password123"), hasher.Hash("password123"));
    }

    [Fact]
    public void Hash_made_with_weaker_parameters_still_verifies_and_asks_for_rehash()
    {
        var old = Hasher(memoryKb: 512).Hash("password123");
        Assert.True(Hasher(memoryKb: 2048).Verify("password123", old, out var rehash));
        Assert.True(rehash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("plaintext")]
    [InlineData("$argon2i$v=19$m=1024,t=1,p=1$c2FsdHNhbHQ$aGFzaGhhc2hoYXNoaGFzaA")]
    [InlineData("$argon2id$v=19$m=999999999,t=1,p=1$c2FsdHNhbHQ$aGFzaGhhc2hoYXNoaGFzaA")]
    [InlineData("$argon2id$v=19$m=1024,t=1,p=1$!!!$???")]
    public void Malformed_hashes_never_verify(string stored) =>
        Assert.False(Hasher().Verify("password123", stored, out _));

    [Fact]
    public void Tokens_are_long_random_and_hash_deterministically()
    {
        var a = SecretTokens.NewToken();
        var b = SecretTokens.NewToken();
        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 43);
        Assert.Equal(SecretTokens.Hash(a), SecretTokens.Hash(a));
        Assert.True(SecretTokens.HashEquals(a, SecretTokens.Hash(a)));
        Assert.False(SecretTokens.HashEquals(b, SecretTokens.Hash(a)));
    }

    [Fact]
    public void Pairing_codes_are_typed_friendly()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = SecretTokens.NewPairingCode();
            Assert.Equal(8, code.Length);
            Assert.DoesNotContain(code, c => "0O1IL".Contains(c, StringComparison.Ordinal));
        }

        Assert.Equal("ABCD-EFGH", SecretTokens.FormatPairingCode("ABCDEFGH"));
        Assert.Equal("ABCDEFGH", SecretTokens.NormalizePairingCode(" abcd-efgh "));
    }
}
