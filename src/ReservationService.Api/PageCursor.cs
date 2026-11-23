using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using ReservationService.Cassandra;

namespace ReservationService.Api;

public sealed class PageCursor(IDataProtectionProvider provider, CassandraOptions options)
{
    public byte[]? Decode(string? cursor, string scope, int pageSize)
    {
        if (cursor is null) return null;
        if (cursor.Length is 0 or > 16384) throw new InvalidCursorException();
        try
        {
            var value = Protector(scope, pageSize).Unprotect(cursor, out _);
            return Convert.FromBase64String(value);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            throw new InvalidCursorException();
        }
    }

    public string? Encode(byte[]? state, string scope, int pageSize) => state is null
        ? null
        : Protector(scope, pageSize).Protect(Convert.ToBase64String(state), TimeSpan.FromMinutes(30));

    private ITimeLimitedDataProtector Protector(string scope, int pageSize) => provider
        .CreateProtector("ReservationService.Paging.v1")
        .CreateProtector(options.Keyspace)
        .CreateProtector(scope)
        .CreateProtector(pageSize.ToString(CultureInfo.InvariantCulture))
        .ToTimeLimitedDataProtector();
}

public sealed class InvalidCursorException : Exception;
