using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LocalAccountManager;

byte[] entropy = Encoding.UTF8.GetBytes("AI-Roblox-Account-Manager.ReleaseSigning.v1");
try
{
    if (args.Length == 3 && args[0] == "init")
    {
        if (File.Exists(args[1]) || File.Exists(args[2])) throw new Exception("Signing key files already exist; no keys were replaced.");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] secret = key.ExportPkcs8PrivateKey();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
            File.WriteAllBytes(args[1], Dpapi.Protect(secret, entropy));
            File.WriteAllBytes(args[2], key.ExportSubjectPublicKeyInfo());
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
        Console.WriteLine("Created a Windows-user-protected publisher key and its public verification key. Private key contents were not printed.");
    }
    else if (args.Length == 6 && args[0] == "sign")
    {
        string zip = args[2], version = args[5];
        if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+\.\d+$") || Path.GetFileName(zip) != $"RobloxAccountManager-{version}-win-x64.zip") throw new Exception("Release version/file mismatch.");
        byte[] secret = Dpapi.Unprotect(File.ReadAllBytes(args[1]), entropy);
        using var key = ECDsa.Create();
        try { key.ImportPkcs8PrivateKey(secret, out _); } finally { CryptographicOperations.ZeroMemory(secret); }
        using var input = File.OpenRead(zip);
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new { version, fileName = Path.GetFileName(zip), size = input.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(input)) });
        File.WriteAllBytes(args[3], manifest);
        File.WriteAllBytes(args[4], key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Console.WriteLine("Signed release manifest. No private key or account data was included.");
    }
    else throw new Exception("Usage: init private.dpapi public.bin | sign private.dpapi release.zip manifest.json manifest.sig version");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
