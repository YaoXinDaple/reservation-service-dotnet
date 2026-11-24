namespace ReservationService.Labs;

// --pause support: print a ready-to-paste cqlsh command and stop until Enter, so the presenter
// can run the same CQL in another terminal and show the database's own answer.
public sealed class LiveQuery(bool enabled)
{
    // Container started by compose.yaml; `docker exec` works from any directory.
    private const string Container = "reservation-service-dotnet-cassandra-1";

    public bool Enabled => enabled;

    public void Print(string cql, string hint = "复制下面这条命令执行")
    {
        if (!enabled) return;
        Console.WriteLine($"【另一个终端】{hint}：");
        Console.WriteLine($"  docker exec -it {Container} cqlsh -e \"{cql}\"\n");
    }

    public void Pause()
    {
        if (!enabled) return;
        Console.Write("        ⏸ 去另一个终端查询，看完回到这里按 Enter 继续…");
        Console.ReadLine();
        Console.WriteLine();
    }
}
