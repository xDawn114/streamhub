using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using StreamHub.Host.Runtime;

// Call the real handler in memory. No sockets, installed identity, service,
// desktop, filesystem roots or credentials from the user's machine are used.
int checks = 0, effects = 0;
void Check(bool success, string label) { if (!success) throw new Exception(label); checks++; }
async Task<int> Request(byte[] bytes, bool chunked = false, string type = "application/json", string? authorization = null)
{
    var api = new ControlApiServer(new RuntimeClient((_, _) => { effects++; throw new Exception("Unexpected service effect"); }),
        (_, _, _) => { effects++; throw new Exception("Unexpected display effect"); },
        new ControlApiIdentity([], []), X509KeyStorageFlags.EphemeralKeySet);
    var token = Enumerable.Repeat((byte)0x41, 32).ToArray();
    typeof(ControlApiServer).GetField("_token", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(api, token);
    var context = new DefaultHttpContext(); context.Connection.RemoteIpAddress = IPAddress.Loopback;
    context.Request.Method = "POST"; context.Request.Path = "/v1/session/desktop";
    context.Request.ContentType = type; context.Request.ContentLength = chunked ? null : bytes.Length;
    context.Request.Headers.Authorization = authorization ?? "Bearer " + Convert.ToHexString(token);
    context.Request.Body = new MemoryStream(bytes); context.Response.Body = new MemoryStream();
    await (Task)typeof(ControlApiServer).GetMethod("HandleAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(api, [context])!;
    context.Response.Body.Position = 0;
    using var result = await JsonDocument.ParseAsync(context.Response.Body);
    Check(!result.RootElement.GetRawText().Contains(Convert.ToHexString(token)), "credentials absent from errors");
    return context.Response.StatusCode;
}
byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
const string invalidAction = "{\"leaseId\":\"lease-fixture\",\"clientId\":\"client-fixture\",\"action\":\"forbidden\"}";
Check(await Request(Utf8(invalidAction)) == 400, "valid JSON reaches action validation");
foreach (var body in new[] {
    "{}", "null", "[]", "", "{", "{\"action\":}",
    "{\"leaseId\":null,\"clientId\":null,\"action\":null}",
    "{\"leaseId\":false}", "{\"leaseId\":42}", "{\"unknown\":true}",
    "{\"leaseId\":\"lease-fixture\",\"clientId\":\"client-fixture\",\"action\":\"status\",\"action\":\"lock\"}",
    "{\"leaseId\":\"lease-fixture\",\"clientId\":\"client-fixture\",\"action\":\"status\",\"Action\":\"lock\"}",
    "{\"leaseId\":\"lease-fixture\",\"clientId\":\"client-fixture\",\"action\":\"status\",\"a\\u0063tion\":\"lock\"}",
    "{\"leaseId\":\"a\",}", "{/* comment */\"action\":\"status\"}",
    "{\"unknown\":{\"nested\":{\"a\":{\"b\":{\"c\":{\"d\":{\"e\":{\"f\":{}}}}}}}}}"
}) { int status = await Request(Utf8(body)); Check(status is 400 or 409, $"invalid body rejected: status={status}, body={body}"); }
Check(await Request([0x7B, 0x22, 0xC0, 0xAF, 0x22, 0x3A, 0x31, 0x7D]) == 400, "invalid UTF-8 rejected");
foreach (bool chunked in new[] { false, true })
{
    var exact = Utf8(invalidAction.PadRight(8192));
    Check(await Request(exact, chunked) == 400, "8 KiB body allowed through to schema/action validation");
    Check(await Request(Utf8(invalidAction.PadRight(8193)), chunked) == 413, "over-limit known/chunked body rejected");
}
Check(await Request(Utf8(invalidAction), type: "text/plain") == 415, "JSON content type enforced");
foreach (string header in new[] { "", "Bearer abc", "Bearer " + new string('0', 64), "Bearer " + new string('G', 64),
    "Bearer " + new string('4', 64) + ", Bearer " + new string('4', 64) })
    Check(await Request(Utf8(invalidAction), authorization: header) == 401, "invalid bearer never reaches parser or effects");

// A deterministic bounded malformed corpus is a smoke test, not an audit or
// coverage-guided fuzzing claim. Each sample must fail without an effect/500.
var random = new Random(98);
for (int i = 0; i < 128; i++)
{
    var bytes = new byte[random.Next(1, 512)]; random.NextBytes(bytes); bytes[0] = (byte)'{';
    Check(await Request(bytes, chunked: i % 2 == 0) == 400, "malformed corpus fails closed");
}
ControlJsonPolicy.Validate(Utf8("{\"a\":{\"key\":1},\"b\":{\"key\":2}}"));
Check(effects == 0, "invalid traffic has zero service/display side effects");
Console.WriteLine($"{checks} control-boundary assertions passed; 128 malformed samples; zero service/display effects.");
