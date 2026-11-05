using System.Text;
using System.Xml.Linq;
using Eden.Protocol.Chat;

namespace Eden.Protocol.Tests.Chat;

public sealed class XmppStreamTests
{
    [Fact]
    public async Task FragmentedUtf8AndSaslRestartPreserveNativeStanzasAndText()
    {
        const string header =
            "<stream:stream to='eden.chat' xmlns='jabber:client' xmlns:stream='http://etherx.jabber.org/streams' version='1.0'>";
        const string data =
            "<?xml version='1.0'?>"
            + header
            + "<auth xmlns='urn:ietf:params:xml:ns:xmpp-sasl' mechanism='PLAIN'>abc=</auth>"
            + header
            + "<message id='m'><body>こんにちは 🚀 &amp; &lt;b&gt;</body></message></stream:stream>";
        using FragmentedStream stream = new(Encoding.UTF8.GetBytes(data));
        using XmppStreamReader reader = new(stream);
        Assert.Equal("stream", (await reader.ReadAsync(CancellationToken.None))!.Name.LocalName);
        Assert.Equal("auth", (await reader.ReadAsync(CancellationToken.None))!.Name.LocalName);
        Assert.Equal("stream", (await reader.ReadAsync(CancellationToken.None))!.Name.LocalName);
        var message = (await reader.ReadAsync(CancellationToken.None))!;
        Assert.Equal("こんにちは 🚀 & <b>", message.Element(XName.Get("body", XmppWire.ClientNamespace))!.Value);
        Assert.Null(await reader.ReadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("<!DOCTYPE message [<!ENTITY x SYSTEM 'file:///private'>]><message>&x;</message>")]
    [InlineData("<message attr='unterminated")]
    [InlineData("outside XML")]
    public async Task InvalidOrExternalEntityInputIsRejected(string xml)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(xml));
        using XmppStreamReader reader = new(stream);
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task UnboundedTextAndNestingAreRejectedBeforeBuildingAnXmlTree()
    {
        foreach (
            string xml in new[]
            {
                "<message><body>" + new string('x', XmppStreamReader.MaximumStanzaCharacters) + "</body></message>",
                string.Concat(Enumerable.Repeat("<x>", 40)) + string.Concat(Enumerable.Repeat("</x>", 40)),
            }
        )
        {
            using MemoryStream stream = new(Encoding.UTF8.GetBytes(xml));
            using XmppStreamReader reader = new(stream);
            await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(CancellationToken.None));
        }
    }

    private sealed class FragmentedStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
