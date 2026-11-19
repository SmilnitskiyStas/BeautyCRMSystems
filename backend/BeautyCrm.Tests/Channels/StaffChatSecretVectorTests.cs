using System.Text.Json;
using BeautyCrm.Infrastructure.Data.Beauty;
using Microsoft.Extensions.Configuration;

namespace BeautyCrm.Tests.Channels;

/// <summary>
/// TASK-897 (a): сумісність шифрування chat_id backend -> worker. Шифротексти у спільному векторі створені реальним
/// .NET <see cref="AesGcmSecretProtector"/>; воркер розшифровує ті самі рядки тим самим ключем
/// (worker/src/inventory/telegram.test.ts, .ai/artifacts/contracts/ADR-025-chat-id-secret-vector.json). Без БД.
/// Перегенерація: QA_EMIT_VECTOR=1 dotnet test --filter StaffChatSecretVector (пише файл вектора).
/// </summary>
public class StaffChatSecretVectorTests
{
    private static string VectorPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var f = Path.Combine(dir.FullName, ".ai", "artifacts", "contracts", "ADR-025-chat-id-secret-vector.json");
            if (Directory.Exists(Path.Combine(dir.FullName, ".ai", "artifacts", "contracts"))) return f;
        }
        throw new FileNotFoundException(".ai/artifacts/contracts not found above " + AppContext.BaseDirectory);
    }

    private static readonly string[] ChatIds = ["777000111", "-1001234567890", "5"];

    private static AesGcmSecretProtector Protector(string keyB64) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Channels:EncryptionKey"] = keyB64 }).Build());

    [Fact]
    public void Vector_ciphertexts_made_by_dotnet_protect_decrypt_to_chat_ids()
    {
        var keyB64 = Convert.ToBase64String("TEST-ONLY-CHAT-ID-VECTOR-KEY-032"u8.ToArray());
        var p = Protector(keyB64);
        if (Environment.GetEnvironmentVariable("QA_EMIT_VECTOR") == "1")
        {
            var cases = ChatIds.Select(c => new { chatId = c, protectedValue = p.Protect(c) }).ToArray();
            File.WriteAllText(VectorPath(), JsonSerializer.Serialize(new
            {
                artifact = "ADR-025-chat-id-secret-vector",
                warning = "TEST-ONLY key. Never configure it in any environment.",
                format = "base64(nonce[12] | tag[16] | ciphertext), AES-256-GCM, plaintext = UTF-8 chat id (raw string, not JSON)",
                keyBase64 = keyB64,
                generator = "AesGcmSecretProtector.Protect (backend), see StaffChatSecretVectorTests",
                cases,
            }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(VectorPath()));
        Assert.Equal(keyB64, doc.RootElement.GetProperty("keyBase64").GetString());
        var seen = new List<string>();
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var chat = c.GetProperty("chatId").GetString()!;
            Assert.Equal(chat, p.Unprotect(c.GetProperty("protectedValue").GetString()!));
            seen.Add(chat);
        }
        Assert.Equal(ChatIds, seen);
    }
}
