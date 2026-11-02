namespace LiteLink.Shared
{
    public interface IConnection
    {
        Task Connect();
        bool IsConnected { get; }
        bool IsConnecting { get; }
        Task<byte[]> Invoke(byte[] data);
        Task Terminate();
    }
}
