using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace YTNotifier.Services;

/// <summary>
/// バックアップファイルを AES-256 で暗号化/復号するサービス。
/// ファイル形式:
///   [0..7]   マジックバイト "YTNOTIFY"
///   [8..23]  IV (16バイト)
///   [24..]   AES-256-CBC 暗号化データ（内部は ZIP バイナリ）
/// 拡張子: .ytbk
/// </summary>
public static class BackupCryptoService
{
    private static readonly byte[] Magic    = Encoding.ASCII.GetBytes("YTNOTIFY");
    private static readonly byte[] Salt     = new byte[]
    {
        0x59, 0x54, 0x42, 0x61, 0x63, 0x6B, 0x75, 0x70,
        0x43, 0x72, 0x79, 0x70, 0x74, 0x6F, 0x4B, 0x65
    };
    private const int Iterations  = 10000;
    private const int KeySize     = 32; // AES-256
    private const int IvSize      = 16;
    public  const string Extension = ".ytbk";

    private static byte[] DeriveKey()
    {
        using var kdf = new Rfc2898DeriveBytes(
            "YTNotifier_v1_Backup_Secret",
            Salt, Iterations,
            HashAlgorithmName.SHA256);
        return kdf.GetBytes(KeySize);
    }

    /// <summary>ZIPバイナリを暗号化して .ytbk ファイルに書き出す</summary>
    public static void Encrypt(byte[] zipBytes, string destPath)
    {
        var key = DeriveKey();
        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();

        using var ms = new MemoryStream();
        ms.Write(Magic, 0, Magic.Length);   // 8バイト: マジック
        ms.Write(aes.IV, 0, IvSize);        // 16バイト: IV

        using var enc = aes.CreateEncryptor();
        using var cs  = new CryptoStream(ms, enc, CryptoStreamMode.Write);
        cs.Write(zipBytes, 0, zipBytes.Length);
        cs.FlushFinalBlock();

        File.WriteAllBytes(destPath, ms.ToArray());
    }

    /// <summary>.ytbk ファイルを復号してZIPバイナリを返す。失敗時は null</summary>
    public static byte[]? Decrypt(string srcPath)
    {
        try
        {
            var data = File.ReadAllBytes(srcPath);

            // マジックバイト確認
            if (data.Length < Magic.Length + IvSize)
                return null;
            for (int i = 0; i < Magic.Length; i++)
                if (data[i] != Magic[i]) return null;

            var iv     = data[Magic.Length..(Magic.Length + IvSize)];
            var cipher = data[(Magic.Length + IvSize)..];
            var key    = DeriveKey();

            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV  = iv;

            using var dec = aes.CreateDecryptor();
            using var ms  = new MemoryStream(cipher);
            using var cs  = new CryptoStream(ms, dec, CryptoStreamMode.Read);
            using var out_ = new MemoryStream();
            cs.CopyTo(out_);
            return out_.ToArray();
        }
        catch { return null; }
    }

    /// <summary>ファイルが .ytbk 形式かどうか確認</summary>
    public static bool IsYtbk(string path)
    {
        try
        {
            var header = new byte[Magic.Length];
            using var fs = File.OpenRead(path);
            return fs.Read(header, 0, Magic.Length) == Magic.Length
                && header.SequenceEqual(Magic);
        }
        catch { return false; }
    }
}
