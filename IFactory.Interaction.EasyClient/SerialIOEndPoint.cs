using System.IO.Ports;
namespace IFactory.Interaction.EasyClient;
public interface ISerialIOEndPoint
{
    string PortName { get; set; }
    byte Databits { get; set; }
    int BaudRate { get; set; }
    Parity Parity { get; set; }
    StopBits StopBits { get; set; }
    bool RtsEnable { get; set; }
    bool DtrEnable { get => false; set { } }
    int WirteTimeout { get => -1; set { } }
}
public class SerialIOEndPoint : ISerialIOEndPoint
{
    public string PortName { get; set; } = "";
    public byte Databits { get; set; } = 8;
    public int BaudRate { get; set; }
    public Parity Parity { get; set; } = Parity.None;
    public StopBits StopBits { get; set; } = StopBits.One;
    public bool RtsEnable { get; set; }
    public bool DtrEnable { get; set; }
    public int WirteTimeout { get; set; } = -1;
    public SerialIOEndPoint() { }
    public SerialIOEndPoint(string uri) : this(ChannelOptions.Address(uri)) { }
    public SerialIOEndPoint(Uri uri)
    {
        var query = ChannelOptions.Parse(uri);
        PortName = uri.Host;
        if (uri.Scheme != "sio" || string.IsNullOrEmpty(PortName)) throw new ArgumentException("A sio URI with a port name is required.");
        BaudRate = ChannelOptions.PositiveInt(query, "BaudRate", 0);
        if (BaudRate == 0) throw new ArgumentException("BaudRate is required.");
        int bits = ChannelOptions.PositiveInt(query, "DataBits", 8);
        if (bits < 5 || bits > 8) throw new ArgumentException("DataBits must be between 5 and 8.");
        Databits = (byte)bits;
        if (query.TryGetValue("Parity", out var parity))
        {
            if (!Enum.TryParse<Parity>(parity, true, out var value) || !Enum.IsDefined(value)) throw new ArgumentException("Invalid Parity.");
            Parity = value;
        }
        if (query.TryGetValue("StopBits", out var stop))
        {
            if (!Enum.TryParse<StopBits>(stop, true, out var value) || !Enum.IsDefined(value) || value == StopBits.None) throw new ArgumentException("Invalid StopBits.");
            StopBits = value;
        }
        RtsEnable = ReadBool(query, "RtsEnable");
        DtrEnable = ReadBool(query, "DtrEnable");
        if (query.TryGetValue("WriteTimeout", out var timeout))
        {
            if (!int.TryParse(timeout, out int value) || (value <= 0 && value != -1)) throw new ArgumentException("Invalid WriteTimeout.");
            WirteTimeout = value;
        }
    }
    private static bool ReadBool(Dictionary<string, string> query, string key)
    {
        if (!query.TryGetValue(key, out var text)) return false;
        if (!bool.TryParse(text, out bool value)) throw new ArgumentException($"Invalid {key}.");
        return value;
    }
}
