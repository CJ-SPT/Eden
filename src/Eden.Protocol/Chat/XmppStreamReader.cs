using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Eden.Protocol.Chat;

// XMPP restarts its unclosed XML root after SASL. Frame bounded stanzas before
// invoking the XML parser, rather than buffering a never-ending XML document.
internal sealed class XmppStreamReader(Stream stream) : IDisposable
{
    internal const int MaximumStanzaCharacters = 16384;
    private const int MaximumStanzaDepth = 32;
    private readonly StreamReader _reader = new(stream, new UTF8Encoding(false, true), false, 2048, leaveOpen: true);
    private readonly char[] _buffer = new char[2048];
    private int _offset;
    private int _length;

    public async Task<XElement?> ReadAsync(CancellationToken cancellation)
    {
        StringBuilder frame = new();
        int depth = 0;
        while (true)
        {
            int next = await CharacterAsync(cancellation);
            if (next < 0)
            {
                return frame.Length == 0 ? null : throw new InvalidDataException("Truncated XMPP stanza.");
            }
            char character = (char)next;
            if (depth == 0 && char.IsWhiteSpace(character))
            {
                continue;
            }
            if (character != '<')
            {
                if (depth == 0)
                {
                    throw new InvalidDataException("Invalid XMPP stream text.");
                }
                Append(frame, character);
                continue;
            }
            string tag = await TagAsync(cancellation);
            if (tag.StartsWith("<?xml ", StringComparison.Ordinal) && depth == 0)
            {
                continue;
            }
            if (tag.StartsWith("<!", StringComparison.Ordinal) || tag.StartsWith("<?", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Unsupported XML declaration in XMPP.");
            }
            if (depth == 0 && tag == "</stream:stream>")
            {
                return null;
            }
            if (depth == 0 && tag.StartsWith("<stream:stream ", StringComparison.Ordinal))
            {
                return Parse(tag + "</stream:stream>");
            }
            foreach (char value in tag)
            {
                Append(frame, value);
            }
            if (tag.StartsWith("</", StringComparison.Ordinal))
            {
                depth--;
            }
            else if (!tag.EndsWith("/>", StringComparison.Ordinal))
            {
                depth++;
                if (depth > MaximumStanzaDepth)
                {
                    throw new InvalidDataException("The XMPP stanza is too deeply nested.");
                }
            }
            if (depth <= 0)
            {
                return Parse("<eden xmlns='jabber:client' xmlns:stream='http://etherx.jabber.org/streams'>" + frame + "</eden>")
                    .Elements()
                    .Single();
            }
        }
    }

    private async Task<string> TagAsync(CancellationToken cancellation)
    {
        StringBuilder tag = new("<");
        char quote = '\0';
        while (true)
        {
            int next = await CharacterAsync(cancellation);
            if (next < 0)
            {
                throw new InvalidDataException("Truncated XMPP tag.");
            }
            char value = (char)next;
            Append(tag, value);
            if (quote != '\0')
            {
                if (value == quote)
                {
                    quote = '\0';
                }
            }
            else if (value is '\'' or '"')
            {
                quote = value;
            }
            else if (value == '>')
            {
                return tag.ToString();
            }
        }
    }

    private async ValueTask<int> CharacterAsync(CancellationToken cancellation)
    {
        if (_offset == _length)
        {
            _length = await _reader.ReadAsync(_buffer.AsMemory(), cancellation);
            _offset = 0;
            if (_length == 0)
            {
                return -1;
            }
        }
        return _buffer[_offset++];
    }

    private static void Append(StringBuilder builder, char value)
    {
        if (builder.Length >= MaximumStanzaCharacters)
        {
            throw new InvalidDataException("The XMPP stanza is too large.");
        }
        builder.Append(value);
    }

    private static XElement Parse(string value)
    {
        using var reader = XmlReader.Create(
            new StringReader(value),
            new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumStanzaCharacters + 256,
            }
        );
        return XElement.Load(reader, LoadOptions.PreserveWhitespace);
    }

    public void Dispose() => _reader.Dispose();
}
