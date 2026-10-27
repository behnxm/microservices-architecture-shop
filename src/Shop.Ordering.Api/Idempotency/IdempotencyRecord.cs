using System;
using System.Security.Cryptography;
using System.Text;

namespace Shop.Ordering.Api.Idempotency;

public sealed class IdempotencyRecord
{
    public string Key { get; private set; } = default!;
    public string RequestHash { get; private set; } = default!;
    public Guid OrderId { get; private set; }
    public int StatusCode { get; private set; }
    public string ResponseBody { get; private set; } = default!;
    public DateTimeOffset CreatedAt { get; private set; }

    private IdempotencyRecord() { }

    public static IdempotencyRecord Create(string key, string requestHash, Guid orderId, int statusCode, string responseBody)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(responseBody);

        return new IdempotencyRecord
        {
            Key = key.Trim(),
            RequestHash = requestHash.Trim(),
            OrderId = orderId,
            StatusCode = statusCode,
            ResponseBody = responseBody,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public static string ComputeSha256(string raw)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexStringLower(bytes);
    }
}
