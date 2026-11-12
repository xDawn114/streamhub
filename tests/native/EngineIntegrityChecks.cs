using System.Text;
using StreamHub.Packaging;

using var record = File.OpenRead(args[0]);
var pinned = EngineIntegrity.ExpectedHash(record);
using (var engine = File.OpenRead(args[1])) EngineIntegrity.Verify(engine, pinned.ToLowerInvariant(), "remote");
Console.WriteLine("PASS: actual compiled engine accepted against embedded-record policy.");
var changed = File.ReadAllBytes(args[1]); changed[0] ^= 1;
try { using var bad = new MemoryStream(changed, false); EngineIntegrity.Verify(bad, pinned, "tablet"); throw new Exception("Tampered engine accepted"); }
catch (IOException) { Console.WriteLine("PASS: one-byte engine tampering rejected."); }
foreach (var value in new[] { "", "123", new string('G', 64) })
{
    try
    {
        using var invalid = new MemoryStream(Encoding.UTF8.GetBytes("{\"binarySha256\":\"" + value + "\"}"));
        EngineIntegrity.ExpectedHash(invalid); throw new Exception("Malformed pin accepted");
    }
    catch (InvalidDataException) { }
}
Console.WriteLine("PASS: absent, short and non-hex SHA256 values rejected.");
