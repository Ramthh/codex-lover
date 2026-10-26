using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Resources;
using System.Security.Cryptography;
using System.Text.Json;

// Reads PE metadata and raw resource bytes. It never loads or executes the app.
if (args.Length != 4)
    throw new ArgumentException("Usage: StoreAssetInspector <extracted-app.dll> <source-Resources-directory> <expected-version> <new-result.json>");
var assemblyPath = Path.GetFullPath(args[0]);
var sourceRoot = Path.GetFullPath(args[1]);
var expectedVersion = Version.Parse(args[2]);
var outputPath = Path.GetFullPath(args[3]);
if (File.Exists(outputPath)) throw new IOException("Existing evidence is preserved; choose a new result file.");

using var assemblyStream = File.OpenRead(assemblyPath);
using var pe = new PEReader(assemblyStream);
var metadata = pe.GetMetadataReader();
var assembly = metadata.GetAssemblyDefinition();
if (metadata.GetString(assembly.Name) != "CodexAccountMonitor" || assembly.Version != expectedVersion)
    throw new InvalidDataException("Unexpected managed assembly identity or version.");
var resource = metadata.ManifestResources.Select(handle => metadata.GetManifestResource(handle))
    .Single(value => value.Implementation.IsNil && metadata.GetString(value.Name) == "CodexAccountMonitor.g.resources");
var resourceSection = pe.GetSectionData(pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress);
var offset = checked((int)resource.Offset);
var length = resourceSection.GetReader(offset, sizeof(int)).ReadInt32();
if (length <= 0) throw new InvalidDataException("Invalid embedded WPF resource length.");
using var resourceStream = new MemoryStream(resourceSection.GetContent(offset + sizeof(int), length).ToArray(), writable: false);
using var resources = new ResourceReader(resourceStream);
var expected = new[]
{
    (Model: "Sol", Key: "resources/sol/robot-wave.png", Source: "Sol/robot-wave.png"),
    (Model: "Astra", Key: "resources/models/astra.png", Source: "Models/Astra.png"),
    (Model: "Luna", Key: "resources/models/luna.png", Source: "Models/Luna.png"),
    (Model: "Terra", Key: "resources/models/terra.png", Source: "Models/Terra.png")
};
var checks = new List<object>();
foreach (var model in expected)
{
    resources.GetResourceData(model.Key, out var typeName, out var encoded);
    if (typeName is not ("ResourceTypeCode.Stream" or "ResourceTypeCode.ByteArray") || encoded.Length < 4)
        throw new InvalidDataException("Unexpected embedded image type: " + model.Model);
    var size = BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(0, 4));
    if (size < 24 || size != encoded.Length - 4) throw new InvalidDataException("Invalid embedded image size: " + model.Model);
    var png = encoded.AsSpan(4);
    if (!png[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
        !png.Slice(12, 4).SequenceEqual("IHDR"u8)) throw new InvalidDataException("Model resource is not PNG: " + model.Model);
    var hash = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
    var sourcePath = Path.Combine(sourceRoot, model.Source.Replace('/', Path.DirectorySeparatorChar));
    using var sourceStream = File.OpenRead(sourcePath);
    var sourceHash = Convert.ToHexString(SHA256.HashData(sourceStream)).ToLowerInvariant();
    if (hash != sourceHash) throw new InvalidDataException("Packaged model image differs from source: " + model.Model);
    checks.Add(new
    {
        model = model.Model, resource = model.Key, bytes = size, sha256 = hash,
        width = BinaryPrimitives.ReadInt32BigEndian(png.Slice(16, 4)),
        height = BinaryPrimitives.ReadInt32BigEndian(png.Slice(20, 4)), matchesSource = true
    });
}
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
JsonSerializer.Serialize(output, new
{
    status = "passed", assembly = assemblyPath, assemblyVersion = assembly.Version.ToString(),
    assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath))).ToLowerInvariant(),
    models = checks, scope = "Read-only PE identity and exact embedded PNG bytes; no app or UI execution.",
    checkedUtc = DateTimeOffset.UtcNow
}, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine("Passed managed version and all four embedded model PNG hashes.");
