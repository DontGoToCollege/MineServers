using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MineServers.Core;

/// <summary>Posts server news (started, stopped, crashed, joins) to a Discord channel through a webhook link the owner made.</summary>
public static class Discord
{
    public static bool IsWebhook(string? url) =>
        url != null && (url.StartsWith("https://discord.com/api/webhooks/") || url.StartsWith("https://discordapp.com/api/webhooks/"));

    /// <summary>Fire and forget: a Discord hiccup must never get in the way of the server.</summary>
    public static void Post(string? webhook, string text)
    {
        if (!IsWebhook(webhook)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var body = JsonSerializer.Serialize(new { content = text, allowed_mentions = new { parse = Array.Empty<string>() } });
                using var resp = await Downloads.Http.PostAsync(webhook, new StringContent(body, Encoding.UTF8, "application/json"));
            }
            catch (Exception) { } // offline or Discord down: skip this message
        });
    }
}
