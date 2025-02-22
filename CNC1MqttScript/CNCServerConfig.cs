namespace CNC1MqttScript;

public class CNCServerConfig
{
    //cncjs server credentials
    public string Host { get; set; }
    public string Username { get; set; }
    public string Password { get; set; }
    public required string Port { get; set; }

    public bool IsReadyToAuth()
    {
        return !string.IsNullOrEmpty(Host)
               && !string.IsNullOrEmpty(Username)
               && !string.IsNullOrEmpty(Password);
    }
}