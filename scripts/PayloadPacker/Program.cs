using StreamHub.Packaging;

if (args.Length is < 2 or > 3 || args.Length == 3 && args[2] != "--legacy") throw new ArgumentException("stage-directory archive.zip [--legacy]");
var result = PayloadArchive.Create(args[0], args[1], args.Length != 3);
Console.WriteLine($"Payload: {result.Aliases} duplicate files omitted, {result.OmittedBytes} uncompressed bytes; runtime directories remain separate.");
