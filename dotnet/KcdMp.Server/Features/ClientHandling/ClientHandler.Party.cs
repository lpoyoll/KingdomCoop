namespace KcdMp.Server.Features.ClientHandling;

public partial class ClientHandler
{
    public CompanionPartyCoordinator CompanionParty { get; } = new();

    public ClientSession? FindReady(byte id)
    {
        lock (_lock) return _clients.FirstOrDefault(c => c.IsReady && c.Id == id);
    }
}
