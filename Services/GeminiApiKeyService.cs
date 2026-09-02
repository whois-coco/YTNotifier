using Newtonsoft.Json;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using YTNotifier.Constants;

namespace YTNotifier.Services;

/// <summary>
/// Gemini APIキーを AES-256 で難読化して gemini_api_key.dat に保存/読み込みするサービス。
/// 鍵は固定パスワード・固定ソルトから PBKDF2 で導出する（難読化目的）。
/// YouTube 用の ApiKeyService と異なり、単一キーのみを扱う。
/// </summary>
public static class GeminiApiKeyService
{
    // 固定ソルト（難読化目的。変更するとexisting gemini_api_key.datが読めなくなるため変更禁止）
    private static readonly byte[] Salt = new byte[]
    {
        0x47, 0x65, 0x6D, 0x69, 0x6E, 0x69, 0x41, 0x70,
        0x69, 0x4B, 0x65, 0x79, 0x53, 0x61, 0x6C, 0x74
    };
    private const int Iterations = 10000;
    private const int KeySize    = 32; // AES-256
    private const int IvSize     = 16;

    private static string GetPath(string confDir)
        => Path.Combine(confDir, AppConstants.FileGeminiApiKey);

    private static byte[] DeriveKey()
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            "YTNotifier_v1_GeminiApiKey_Secret",
            Salt, Iterations,
            HashAlgorithmName.SHA256,
            KeySize);
    }

    /// <summary>Gemini APIキーを暗号化して gemini_api_key.dat に保存する</summary>
    public static void Save(string confDir, string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            var path = GetPath(confDir);
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        var plainText  = JsonConvert.SerializeObject(apiKey);
        var key        = DeriveKey();
        using var aes  = Aes.Create();
        aes.Key        = key;
        aes.GenerateIV();

        using var ms       = new MemoryStream();
        ms.Write(aes.IV, 0, IvSize);

        using var enc      = aes.CreateEncryptor();
        using var cs       = new CryptoStream(ms, enc, CryptoStreamMode.Write);
        var plainBytes     = Encoding.UTF8.GetBytes(plainText);
        cs.Write(plainBytes, 0, plainBytes.Length);
        cs.FlushFinalBlock();

        File.WriteAllBytes(GetPath(confDir), ms.ToArray());
    }

    /// <summary>gemini_api_key.dat を復号して Gemini APIキーを返す。未設定・失敗時は null</summary>
    public static string? Load(string confDir)
    {
        try
        {
            var path = GetPath(confDir);
            if (!File.Exists(path)) return null;

            var data = File.ReadAllBytes(path);
            if (data.Length <= IvSize) return null;

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
            var decrypted  = sr.ReadToEnd();

            return JsonConvert.DeserializeObject<string>(decrypted);
        }
        catch
        {
            return null;
        }
    }
}
