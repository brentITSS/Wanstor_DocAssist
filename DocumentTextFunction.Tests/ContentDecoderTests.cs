using DocumentTextFunction;
using Xunit;

namespace DocumentTextFunction.Tests;

public class ContentDecoderTests
{
    [Fact]
    public void Spaces_and_escaped_slashes_still_decode()
    {
        var original = Convert.ToBase64String("%PDF-1.6 callout + / arrow"u8.ToArray());
        var corrupted = original.Replace('+', ' ').Replace("/", "\\/");

        var decoded = ContentDecoder.Decode(corrupted);

        Assert.Equal("%PDF-1.6 callout + / arrow", System.Text.Encoding.UTF8.GetString(decoded));
    }
}
