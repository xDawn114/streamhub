using System.Text;
using System.Text.Json;

namespace StreamHub.Host.Runtime;

/// <summary>Unambiguous, bounded JSON for the small control endpoints.</summary>
public static class ControlJsonPolicy
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static void Validate(ReadOnlySpan<byte> body)
    {
        try { _ = StrictUtf8.GetCharCount(body); }
        catch (DecoderFallbackException error) { throw new JsonException("Invalid UTF-8 control request.", error); }
        var reader = new Utf8JsonReader(body, new JsonReaderOptions { MaxDepth = 8 });
        var containers = new Stack<HashSet<string>?>();
        bool root = true;
        while (reader.Read())
        {
            if (root)
            {
                if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("A control request must be an object.");
                root = false;
            }
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    // JsonSerializerDefaults.Web is case-insensitive. Reject
                    // both duplicate names and aliases such as enabled/Enabled.
                    containers.Push(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    break;
                case JsonTokenType.StartArray: containers.Push(null); break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray: containers.Pop(); break;
                case JsonTokenType.PropertyName:
                    if (!containers.Peek()!.Add(reader.GetString()!))
                        throw new JsonException("Duplicate control property.");
                    break;
            }
        }
        if (root || containers.Count != 0) throw new JsonException("Incomplete control request.");
    }
}
