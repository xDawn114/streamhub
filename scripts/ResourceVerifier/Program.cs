using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Parse single-file bundle and CLI metadata only. Never load/run installer code.
if (args.Length is < 2 or > 3 || args[1] is not ("none" or "payload") || args.Length == 3 && args[1] != "payload") throw new ArgumentException("file.exe none|payload [empty-export-directory]");
byte[] bytes = File.ReadAllBytes(args[0]);
byte[] signature = Convert.FromHexString("8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE");
int marker = bytes.AsSpan().IndexOf(signature);
if (marker < 8 || bytes.AsSpan(marker+signature.Length).IndexOf(signature) >= 0) throw new InvalidDataException("Bundle marker missing/ambiguous.");
long headerOffset = BitConverter.ToInt64(bytes, marker-8);
using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream, Encoding.UTF8, true);
stream.Position = headerOffset; uint major = reader.ReadUInt32(), minor = reader.ReadUInt32(); int count = reader.ReadInt32();
if (major != 6 || minor != 0 || count is < 1 or > 10000) throw new InvalidDataException("Unsupported bundle header.");
reader.ReadString(); reader.ReadInt64(); reader.ReadInt64(); reader.ReadInt64(); reader.ReadInt64(); reader.ReadUInt64();
byte[]? assembly = null;
for (int i=0;i<count;i++)
{
    long offset=reader.ReadInt64(), size=reader.ReadInt64(), compressed=reader.ReadInt64(); byte type=reader.ReadByte(); string name=reader.ReadString();
    if (type!=1 || name!="串流中心安装.dll") continue;
    if (size<1 || size>1024L*1024*1024 || offset<0 || offset+(compressed>0?compressed:size)>bytes.LongLength) throw new InvalidDataException("Invalid entry bounds.");
    using var data=new MemoryStream(bytes,(int)offset,(int)(compressed>0?compressed:size),false);
    if(compressed>0) { using var inflater=new DeflateStream(data,CompressionMode.Decompress); using var target=new MemoryStream(); inflater.CopyTo(target); assembly=target.ToArray(); }
    else assembly=data.ToArray();
    if(assembly.LongLength!=size)throw new InvalidDataException("Bundle entry size differs.");
}
if(assembly is null)throw new InvalidDataException("Installer assembly not present in bundle.");
using var pe=new PEReader(new MemoryStream(assembly,false)); var metadata=pe.GetMetadataReader(); byte[]? payload=null, nativeRecord=null;
foreach(var handle in metadata.ManifestResources)
{
    var resource=metadata.GetManifestResource(handle);
    var resourceName=metadata.GetString(resource.Name);
    if(resourceName is not ("StreamHub.Payload.zip" or "StreamHub.Sunshine.build.json"))continue;
    if(!resource.Implementation.IsNil)throw new InvalidDataException("External payload resource unsupported.");
    var data=pe.GetSectionData(pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress+checked((int)resource.Offset)).GetReader();
    int size=data.ReadInt32();var value=data.ReadBytes(size);
    if(resourceName=="StreamHub.Payload.zip")payload=value;else nativeRecord=value;
}
if(args[1]=="none") { if(payload is not null)throw new InvalidDataException("Uninstaller contains installation payload."); Console.WriteLine("PASS: bundled uninstaller has no installation payload; installer code not executed.");return; }
if(payload is null)throw new InvalidDataException("Installation payload missing.");
using var container=StreamHub.Packaging.PayloadContainer.Open(new MemoryStream(payload,false));
var archive=container.Archive;
var logical=StreamHub.Packaging.PayloadArchive.Open(archive);
logical.VerifyFiles();
if(nativeRecord is null)throw new InvalidDataException("Installer is missing its pinned native engine record.");
using var nativeStream=new MemoryStream(nativeRecord,false);
var pinnedEngine=StreamHub.Packaging.EngineIntegrity.ExpectedHash(nativeStream);
foreach(var profile in new[]{"remote","tablet"})
{
    var engine=logical.Files[$"components/streaming/{profile}/sunshine.exe"];
    using var input=engine.Open();
    StreamHub.Packaging.EngineIntegrity.Verify(input,pinnedEngine,profile);
}
var manifestEntry=archive.GetEntry("组件清单.json")??throw new InvalidDataException("Component manifest missing.");
using var manifestStream=manifestEntry.Open();using var manifest=JsonDocument.Parse(manifestStream);
Console.WriteLine($"PASS: bundled payload reconstructs {logical.Files.Count} verified files ({logical.AliasCount} aliases), version {manifest.RootElement.GetProperty("version").GetString()}; installer code not executed.");
if (args.Length == 3) { logical.Extract(args[2]); Console.WriteLine("PASS: verified resources exported without running installer code."); }
