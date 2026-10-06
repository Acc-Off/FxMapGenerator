using System.Security.Cryptography;
using RageLib.GTA5.Cryptography;
using RageLib.GTA5.Cryptography.Helpers;

namespace FxMapGenerator.GameData.Gta;

/// <summary>
/// Loads the RPF decryption keys from user-provided key files and installs them into gta-toolkit's
/// <see cref="GTA5Constants"/>. Nothing is bundled and nothing is written back to disk. Taken from EmotePreviewer (same
/// author, MIT); the key folder is shared with it.
/// </summary>
/// <remarks>
/// The current Legacy GTA5.exe only carries the AES key and the hash LUT in plain form, so the NG keys and tables cannot
/// be recovered from it. The user creates the four files once with EmotePreviewerKeyTool (a separate repository).
/// </remarks>
public static class GtaKeys
{
    public const string AesKeyFile = "gtav_aes_key.dat";                    // 32 bytes
    public const string NgKeyFile = "gtav_ng_key.dat";                      // 101 × 272 bytes
    public const string NgDecryptTablesFile = "gtav_ng_decrypt_tables.dat"; // 17 × 16 × 256 × u32
    public const string HashLutFile = "gtav_hash_lut.dat";                  // 256 bytes
    public static readonly IReadOnlyList<string> RequiredFiles = [AesKeyFile, NgKeyFile, NgDecryptTablesFile, HashLutFile];

    /// <summary>Environment variable naming the key folder (second in the search order).</summary>
    public const string KeyFolderVariable = "FXMAPGEN_KEYS";

    /// <summary>This tool's key folder: %LOCALAPPDATA%\FxMapGenerator\keys.</summary>
    public static string DefaultKeyFolder => Path.Combine(LocalAppData, "FxMapGenerator", "keys");

    /// <summary>EmotePreviewer's key folder, searched last so that keys made for it serve here too: %LOCALAPPDATA%\EmotePreviewer\keys.</summary>
    public static string SharedKeyFolder => Path.Combine(LocalAppData, "EmotePreviewer", "keys");

    static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    static readonly object Sync = new();
    static string? _installedFrom;

    /// <summary>Where the keys were installed from in this process, or null.</summary>
    public static string? InstalledFrom
    {
        get { lock (Sync) return _installedFrom; }
    }

    /// <summary>The key folders in search order: the one given, <see cref="KeyFolderVariable"/>, <see cref="DefaultKeyFolder"/>, <see cref="SharedKeyFolder"/>.</summary>
    public static IReadOnlyList<(string Source, string Folder)> Candidates(string? explicitFolder)
    {
        var list = new List<(string, string)>();
        if (!string.IsNullOrWhiteSpace(explicitFolder)) list.Add(("given", explicitFolder));
        var env = Environment.GetEnvironmentVariable(KeyFolderVariable);
        if (!string.IsNullOrWhiteSpace(env)) list.Add(("environment", env));
        list.Add(("default", DefaultKeyFolder));
        list.Add(("emotePreviewer", SharedKeyFolder));
        return list;
    }

    /// <summary>The files of <see cref="RequiredFiles"/> that <paramref name="folder"/> lacks (all of them when the folder does not exist).</summary>
    public static IReadOnlyList<string> MissingFiles(string folder) =>
        Directory.Exists(folder) ? RequiredFiles.Where(f => !File.Exists(Path.Combine(folder, f))).ToList() : RequiredFiles;

    /// <summary>
    /// The first complete key folder in the search order, or null. A folder given explicitly is the only candidate:
    /// when it is incomplete the others are not tried (the user asked for that one).
    /// </summary>
    public static KeyLookup Find(string? explicitFolder)
    {
        var tried = new List<KeyLookup.Tried>();
        foreach (var (source, folder) in Candidates(explicitFolder))
        {
            var missing = MissingFiles(folder);
            if (missing.Count == 0) return new KeyLookup(folder, source, tried);
            tried.Add(new KeyLookup.Tried(folder, source, missing));
            if (source == "given") break;
        }
        return new KeyLookup(null, null, tried);
    }

    /// <summary>
    /// Loads the four key files from <paramref name="folder"/>, checks them against the known SHA1 hashes and installs
    /// them. Once per folder: calling again with the folder already installed does nothing.
    /// </summary>
    public static void Install(string folder)
    {
        var full = Path.GetFullPath(folder);
        lock (Sync)
        {
            if (string.Equals(_installedFrom, full, StringComparison.OrdinalIgnoreCase)) return;
            var aes = File.ReadAllBytes(Path.Combine(full, AesKeyFile));
            var ngKeys = CryptoIO.ReadNgKeys(Path.Combine(full, NgKeyFile));
            var tables = CryptoIO.ReadNgTables(Path.Combine(full, NgDecryptTablesFile));
            var lut = File.ReadAllBytes(Path.Combine(full, HashLutFile));

            Verify(aes, GTA5HashConstants.PC_AES_KEY_HASH, AesKeyFile);
            Verify(lut, GTA5HashConstants.PC_LUT_HASH, HashLutFile);
            for (int i = 0; i < ngKeys.Length; i++) Verify(ngKeys[i], GTA5HashConstants.PC_NG_KEY_HASHES[i], $"{NgKeyFile} key {i}");
            var tableBytes = new byte[0x400];
            for (int i = 0; i < 17; i++)
                for (int j = 0; j < 16; j++)
                {
                    Buffer.BlockCopy(tables[i][j], 0, tableBytes, 0, 0x400);
                    Verify(tableBytes, GTA5HashConstants.PC_NG_DECRYPT_TABLE_HASHES[i * 16 + j], $"{NgDecryptTablesFile} table {i}/{j}");
                }

            GTA5Constants.PC_AES_KEY = aes;
            GTA5Constants.PC_NG_KEYS = ngKeys;
            GTA5Constants.PC_NG_DECRYPT_TABLES = tables;
            GTA5Constants.PC_LUT = lut;
            _installedFrom = full;
        }
    }

    static void Verify(byte[] data, byte[] expectedSha1, string what)
    {
        if (!SHA1.HashData(data).AsSpan().SequenceEqual(expectedSha1))
            throw new InvalidDataException($"{what} does not match the expected SHA1 hash; the key file is damaged or from another game version");
    }
}

/// <summary>The result of <see cref="GtaKeys.Find"/>: the folder found (null when none is complete) and the folders tried before it.</summary>
public sealed record KeyLookup(string? Folder, string? Source, IReadOnlyList<KeyLookup.Tried> TriedFolders)
{
    public sealed record Tried(string Folder, string Source, IReadOnlyList<string> Missing);
}
