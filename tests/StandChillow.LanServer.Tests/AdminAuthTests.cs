using System.Net;
using StandChillow.LanServer.Net;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Авторизация админа кодом: код на запуск, привязка к IP, сброс. Без сокетов — это чистая
/// логика «кто доказал, что он админ».
/// </summary>
public sealed class AdminAuthTests
{
    [Fact]
    public void GeneratedCodeIs24LatinDigitsAndNeverTheSameTwice()
    {
        var first = AdminAuth.GenerateCode(AdminAuth.DefaultCodeLength);
        var second = AdminAuth.GenerateCode(AdminAuth.DefaultCodeLength);

        Assert.Equal(24, first.Length);
        Assert.All(first, c => Assert.Contains(c, AdminAuth.Alphabet));
        Assert.NotEqual(first, second);
        Assert.DoesNotContain('0', AdminAuth.Alphabet);
        Assert.DoesNotContain('O', AdminAuth.Alphabet);
        Assert.DoesNotContain('1', AdminAuth.Alphabet);
        Assert.DoesNotContain('l', AdminAuth.Alphabet);
    }

    [Fact]
    public void CodeIsComparedWithoutCaseAndSpaces()
    {
        var auth = new AdminAuth("AbCdEfGh2345JkLmNpQrSt2");

        Assert.True(auth.Matches("AbCdEfGh2345JkLmNpQrSt2"));
        Assert.True(auth.Matches("  abcdefgh2345jklmnpqrst2  "));
        Assert.False(auth.Matches("AbCdEfGh2345JkLmNpQrSt3"));
        Assert.False(auth.Matches(""));
        Assert.False(auth.Matches(null));
    }

    [Fact]
    public void AuthorizationIsPerAddressUntilReset()
    {
        var auth = new AdminAuth("CODEcode2345CODEcode23");
        var lan = IPAddress.Parse("192.168.1.70");
        var other = IPAddress.Parse("192.168.1.67");

        Assert.False(auth.IsAuthorized(lan));
        Assert.True(auth.Authorize(lan));
        Assert.False(auth.Authorize(lan)); // второй раз — уже был
        Assert.True(auth.IsAuthorized(lan));
        Assert.False(auth.IsAuthorized(other));
        Assert.Equal(1, auth.AuthorizedCount);

        auth.Reset();
        Assert.False(auth.IsAuthorized(lan));
        Assert.Equal(0, auth.AuthorizedCount);
        Assert.Equal(AdminAuth.DefaultCodeLength, auth.Code.Length);
    }

    [Fact]
    public void ResetWithExplicitCodeKeepsIt()
    {
        var auth = new AdminAuth("OLDold2345OLDold2345O");
        auth.Authorize(IPAddress.Loopback);

        var code = auth.Reset("NEWnew2345NEWnew2345N");

        Assert.Equal("NEWnew2345NEWnew2345N", code);
        Assert.Equal(code, auth.Code);
        Assert.False(auth.IsAuthorized(IPAddress.Loopback));
    }

    [Fact]
    public void V4MappedAndPlainAddressesAreTheSamePerson()
    {
        var auth = new AdminAuth("Mapped2345Mapped2345Ma");
        var v4 = IPAddress.Parse("192.168.1.70");
        var mapped = v4.MapToIPv6();

        Assert.True(auth.Authorize(mapped));
        Assert.True(auth.IsAuthorized(v4));
    }

    [Fact]
    public void NullAddressIsNeverAuthorized()
    {
        var auth = new AdminAuth("Null2345Null2345Null2345");

        Assert.False(auth.IsAuthorized(null));
        Assert.False(auth.Authorize(null));
        Assert.Equal(0, auth.AuthorizedCount);
    }

    [Fact]
    public void EmptyConfiguredCodeIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new AdminAuth("   "));
    }
}
