using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;

namespace CNC1MqttScript
{
    public class CNCAuthService
    {
        private readonly ILogger<CNCAuthService> _logger;
        private readonly IManagedMqttClient _mqttClient;

        public required string Host { get; set; }
        public required string Username { get; set; }
        public required string Password { get; set; }
        public required string Token { get; set; }
        public required string ControllerType { get; set; }
        public required long? BuadRate { get; set; } = null;
        public required string Port { get; set; }

        public CNCAuthService(string host, string username, string password, string token, int buadrate, string port, string controllertype)
        {
            Host = host;
            Username = username;
            Password = password;
            Token = token;
            ControllerType = controllertype;
            BuadRate = buadrate;
            Port = port;
        }

        public CNCAuthService(ILogger<CNCAuthService> logger, IManagedMqttClient mqttClient)
        {
            _logger = logger;
            _mqttClient = mqttClient;
        }

        public async Task<string?> GetAuthTokenAsync()
        {
            if (string.IsNullOrEmpty(Host) || string.IsNullOrEmpty(Username) || string.IsNullOrEmpty(Password))
            {
                _logger.LogError("Missing Host, Username, or Password for authentication.");
                return null;
            }

            using var client = new HttpClient();
            var signinUrl = $"{Host}/api/signin";
            var credentials = new { name = Username, password = Password };
            var payload = JsonSerializer.Serialize(credentials);
            var content = new StringContent(payload, Encoding.UTF8, "application/json");

            try
            {
                var response = await client.PostAsync(signinUrl, content);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"Failed to authenticate: {response.StatusCode}");
                    return null;
                }

                var responseBody = await response.Content.ReadAsStringAsync();
                var jsonDoc = JsonDocument.Parse(responseBody);

                if (jsonDoc.RootElement.TryGetProperty("token", out var tokenElement))
                {
                    Token = tokenElement.GetString() ?? string.Empty;
                    _logger.LogInformation("Successfully retrieved authentication token.");
                    return Token;
                }

                _logger.LogError("Token was not found in the response.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Exception while retrieving token: {ex.Message}");
            }
            return null;
        }

        public async Task SubscribeToMqttTokenAsync()
        {
            _mqttClient.ApplicationMessageReceivedAsync += async e =>
            {
                if (e.ApplicationMessage.Topic.Equals("CNCS/CNC1/Server/MqttToken", StringComparison.InvariantCultureIgnoreCase))
                {
                    var receivedTokenJson = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment.ToArray());
                    var tokenObject = JsonSerializer.Deserialize<JsonElement>(receivedTokenJson);
                    
                    if (tokenObject.TryGetProperty("TOKN", out var tokenElement))
                    {
                        Token = tokenElement.GetString() ?? string.Empty;
                        _logger.LogInformation("Updated authentication token via MQTT.");
                    }
                    else
                    {
                        _logger.LogWarning("Received an empty or malformed token from MQTT.");
                    }
                }
                await Task.CompletedTask;
            };

            await _mqttClient.SubscribeAsync("CNCS/CNC1/Server/MqttToken");
            _logger.LogInformation("Subscribed to MQTT token updates.");
        }

        public async Task PublishTokenAsync(string newToken)
        {
            if (string.IsNullOrWhiteSpace(newToken))
            {
                _logger.LogError("Attempted to publish an empty token.");
                return;
            }

            var tokenJson = JsonSerializer.Serialize(new { TOKN = newToken });
            var message = new MqttApplicationMessageBuilder()
                .WithTopic("CNCS/CNC1/Server/MqttToken")
                .WithPayload(tokenJson)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce)
                .WithRetainFlag()
                .Build();

            await _mqttClient.EnqueueAsync(message);
            _logger.LogInformation("Published new authentication token via MQTT.");
            Token = newToken;
        }

        public async Task OpenPort(SocketIOClient.SocketIO client)
        {
            var programLogger = _logger;
            await client.EmitAsync("list");
            
            client.On("serialport:list", async response =>
            {
                try
                {
                    var ports = response.GetValue<JsonElement>();
                    bool portFound = false;

                    foreach (var port in ports.EnumerateArray())
                    {
                        var portName = port.GetProperty("port").GetString();

                        if (portName == Port)
                        {
                            portFound = true;
                            bool inUse = port.TryGetProperty("inuse", out JsonElement inuseElement) && inuseElement.GetBoolean();
                            
                            await client.EmitAsync("open", new object[]
                            {
                                Port,
                                new
                                {
                                    controllerType = ControllerType,
                                    baudrate = BuadRate,
                                    rtscts = false,
                                    pin = new { dtr = (object)null, rts = (object)null }
                                }
                            });
                            break;
                        }
                    }

                    if (!portFound)
                    {
                       programLogger.LogError($"[ERROR] Port {Port} not found in the port list!");
                    }
                }
                catch (Exception ex)
                {
                    programLogger.LogInformation($"[DEBUG] Exception in serialport:list handler: {ex.Message}");
                }
            });
        }
    }
}
