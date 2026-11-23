using System.Text.RegularExpressions;

namespace ReservationService.Cassandra;

public sealed class CassandraOptions
{
    public string[] ContactPoints { get; set; } = ["127.0.0.1"];
    public int Port { get; set; } = 9042;
    public string LocalDatacenter { get; set; } = "datacenter1";
    public string Keyspace { get; set; } = "reservation_service";

    public void Validate()
    {
        if (!Regex.IsMatch(Keyspace, "^[a-z][a-z0-9_]{0,47}$"))
            throw new ArgumentException("Keyspace must match [a-z][a-z0-9_]{0,47}.");
        if (ContactPoints.Length == 0 || ContactPoints.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one Cassandra contact point is required.");
        if (Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(LocalDatacenter))
            throw new ArgumentException("Invalid Cassandra port or local datacenter.");
    }
}
