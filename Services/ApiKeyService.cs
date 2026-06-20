using System.IO;
using System.Security.Cryptography;
using System.Text;
using YTNotifier.Constants;

namespace YTNotifier.Services;

/// <summary>
/// APIキーを AES-256 で難読化して api_key.dat に保存/読み込みするサービス。
/// 鍵はコード埋め込みの固定ソルトから PBKDF2 で導出する。
/// </summary>
public static class ApiKeyService
{
    // 固定ソルト（難読化目的。変更するとexisting api_key.datが読めなくなるため変更禁止）
    private static readonly byte[] Salt = new byte[]
    {
        0x59, 0x54, 0x4E, 0x6F, 0x74, 0x69, 0x66, 0x69,
        0x65, 0x72, 0x41, 0x70, 0x70, 0x4B, 0x65, 0x79
    };
    private const int Iterations = 10000;
    private const int KeySize    = 32; // AES-256
    private const int IvSize     = 16;

    private static string GetPath(string appDataDir)
        => Path.Combine(appDataDir, AppConstants.FileApiKey);

    private static byte[] DeriveKey()
    {
        using var kdf = new Rfc2898DeriveBytes(
            "YTNotifier_v1_ApiKey_Secret",
            Salt, Iterations,
            HashAlgorithmName.SHA256);
        return kdf.GetBytes(KeySize);
    }

    /// <summary>APIキーを暗号化して api_key.dat に保存する</summary>
    public static void Save(string appDataDir, string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            // 空文字の場合はファイルを削除
            var path = GetPath(appDataDir);
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        var key       = DeriveKey();
        using var aes = Aes.Create();
        aes.Key       = key;
        aes.GenerateIV();

        using var ms        = new MemoryStream();
        ms.Write(aes.IV, 0, IvSize); // 先頭16バイトにIVを書く

        using var enc    = aes.CreateEncryptor();
        using var cs     = new CryptoStream(ms, enc, CryptoStreamMode.Write);
        var plainBytes   = Encoding.UTF8.GetBytes(apiKey);
        cs.Write(plainBytes, 0, plainBytes.Length);
        cs.FlushFinalBlock();

        File.WriteAllBytes(GetPath(appDataDir), ms.ToArray());
    }

    /// <summary>api_key.dat を復号してAPIキーを返す。失敗時は空文字</summary>
    public static string Load(string appDataDir)
    {
        try
        {
            var path = GetPath(appDataDir);
            if (!File.Exists(path)) return string.Empty;

            var data = File.ReadAllBytes(path);
            if (data.Length <= IvSize) return string.Empty;

            var iv         = data[..IvSize];
            var cipher     = data[IvSize..];
            var key        = DeriveKey();

            using var aes  = Aes.Create();
            aes.Key        = key;
            aes.IV         = iv;

            using var dec  = aes.CreateDecryptor();
            using var ms   = new MemoryStream(cipher);
            using var cs   = new CryptoStream(ms, dec, CryptoStreamMode.Read);
            using var sr   = new StreamReader(cs, Encoding.UTF8);
            return sr.ReadToEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

}
