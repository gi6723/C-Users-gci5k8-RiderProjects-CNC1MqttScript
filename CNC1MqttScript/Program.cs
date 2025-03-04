using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using SocketIO.Core;
using SocketIOClient;
using SocketIOClient.Transport;

namespace CNC1MqttScript
{
    public class Program
    {
        private static bool _authenticationCompleted = false; // Prevent duplicate authentication

        public static async Task Main(string[] args)
        {
            // 1️⃣ Load Configuration
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddIniFile("appsettings.ini", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            // 2️⃣ Setup Dependency Injection
            var services = new ServiceCollection()
                .AddLogging(builder =>
                {
                    builder.AddConsole();
                    builder.SetMinimumLevel(LogLevel.Information); // Log only essential info
                })
                .AddSingleton<IConfiguration>(configuration)
                .AddSingleton<CNCAuthService>()
                .AddSingleton<CNCLiveDataPublisher>()
                .BuildServiceProvider();

            // 3️⃣ Retrieve Loggers
            var programLogger = services.GetRequiredService<ILogger<Program>>();
            var cncAuthLogger = services.GetRequiredService<ILogger<CNCAuthService>>();
            var cncLiveLogger = services.GetRequiredService<ILogger<CNCLiveDataPublisher>>();

            programLogger.LogInformation("🚀 CNC1 MQTT Script starting...");

            // 4️⃣ Setup MQTT Client
            string mqttHost = configuration["MQTT:Host"] ?? "152.70.157.193";
            int mqttPort = int.TryParse(configuration["MQTT:Port"], out var port) ? port : 1883;
            string mqttUser = configuration["MQTT:Username"] ?? "Truman";
            string mqttPass = configuration["MQTT:Password"] ?? "GoTigers";

            var mqttClientOptions = new MqttClientOptionsBuilder()
                .WithTcpServer(mqttHost, mqttPort)
                .WithCredentials(mqttUser, mqttPass)
                .Build();

            var managedOptions = new ManagedMqttClientOptionsBuilder()
                .WithClientOptions(mqttClientOptions)
                .Build();

            var mqttClient = new MqttFactory().CreateManagedMqttClient();
            await mqttClient.StartAsync(managedOptions);
            programLogger.LogInformation("✅ MQTT client connected.");

            // 5️⃣ Initialize CNCAuthService with empty strings for required properties
            var cncAuthService = new CNCAuthService(cncAuthLogger, mqttClient)
            {
                Host = string.Empty,
                Username = string.Empty,
                Password = string.Empty,
                Token = string.Empty,
                ControllerType = string.Empty,
                BuadRate = null,
                Port = string.Empty,
            };
            
            await cncAuthService.SubscribeToMqttTokenAsync(); // Subscribe to token topic

            // 6️⃣ Subscribe to CNC Server Credentials
            await mqttClient.SubscribeAsync("CNCS/CNC1/Server/Host");
            await mqttClient.SubscribeAsync("CNCS/CNC1/Server/Username");
            await mqttClient.SubscribeAsync("CNCS/CNC1/Server/Password");
            await mqttClient.SubscribeAsync("CNCS/CNC1/Server/ControllerType");
            await mqttClient.SubscribeAsync("CNCS/CNC1/Server/BuadRate");
            await mqttClient.SubscribeAsync("CNCS/CNC1/Server/Port");


            programLogger.LogInformation("📡 Listening for CNC server credentials...");

            // 7️⃣ Handle Receiving CNC Credentials via MQTT
            mqttClient.ApplicationMessageReceivedAsync += async e =>
            {
                if (_authenticationCompleted) return;
                
                
                string topic = e.ApplicationMessage.Topic;
                string payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment.ToArray()).Trim();
                Dictionary<string, object> jsonPayload = null;

                jsonPayload = JsonSerializer.Deserialize<Dictionary<string, object>>(payload);
                programLogger.LogInformation(
                    $"📩 Received message on topic '{topic}': {payload}"); // <-- Add this to log incoming messages
                
                if (string.IsNullOrWhiteSpace(payload))
                {
                    programLogger.LogWarning($"⚠️ Received empty payload for topic: {topic}");
                    return;
                }

                switch (topic)
                {
                    case "CNCS/CNC1/Server/Host":
                        if (jsonPayload.TryGetValue("URL", out object hostValue))
                        {
                            cncAuthService.Host = hostValue.ToString();
                            programLogger.LogInformation($"✅ Set CNC Host: {cncAuthService.Host}");
                        }
                        break;

                    case "CNCS/CNC1/Server/Username":
                        if (jsonPayload.TryGetValue("USRNM", out object userValue))
                        {
                            cncAuthService.Username = userValue.ToString();
                            programLogger.LogInformation($"✅ Set CNC Username: {cncAuthService.Username}");
                        }
                        break;

                    case "CNCS/CNC1/Server/Password":
                        if (jsonPayload.TryGetValue("PSSWD", out object passValue))
                        {
                            cncAuthService.Password = passValue.ToString();
                            programLogger.LogInformation($"✅ Set CNC Password: {cncAuthService.Password}");
                        }
                        break;

                    case "CNCS/CNC1/Server/ControllerType":
                        if (jsonPayload.TryGetValue("CNTRL", out object controllerValue))
                        {
                            cncAuthService.ControllerType = controllerValue.ToString();
                            programLogger.LogInformation($"✅ Set CNC ControllerType: {cncAuthService.ControllerType}");
                        }
                        break;

                    case "CNCS/CNC1/Server/BuadRate":
                        if (jsonPayload.TryGetValue("BUADRATE", out object baudValue) && long.TryParse(baudValue.ToString(), out long baudRate))
                        {
                            cncAuthService.BuadRate = baudRate;
                            programLogger.LogInformation($"✅ Set CNC BuadRate: {cncAuthService.BuadRate}");
                        }
                        break;

                    case "CNCS/CNC1/Server/Port":
                        if (jsonPayload.TryGetValue("PORT", out object portValue))
                        {
                            cncAuthService.Port = portValue.ToString();
                            programLogger.LogInformation($"✅ Set CNC Port: {cncAuthService.Port}");
                        }
                        break;
                }

                if (!string.IsNullOrEmpty(cncAuthService.Host) &&
                    !string.IsNullOrEmpty(cncAuthService.Username) &&
                    !string.IsNullOrEmpty(cncAuthService.Password) &&
                    !string.IsNullOrEmpty(cncAuthService.ControllerType) &&
                    (cncAuthService.BuadRate != null) &&
                    !string.IsNullOrEmpty(cncAuthService.Port)
                    )
                {
                    _authenticationCompleted = true;
                    programLogger.LogInformation("🔑 CNC Credentials received. Authenticating...");

                    var token = await cncAuthService.GetAuthTokenAsync();
                    if (!string.IsNullOrEmpty(token))
                    {
                        await cncAuthService.PublishTokenAsync(token);

                        programLogger.LogInformation("📨 CNC Token published.");

                        try
                        {
                            var socket = new SocketIOClient.SocketIO(cncAuthService.Host,
                                new SocketIOClient.SocketIOOptions
                                {
                                    Path = "/socket.io/",
                                    EIO = EngineIO.V3,
                                    AutoUpgrade = false,
                                    Transport = TransportProtocol.WebSocket,
                                    ExtraHeaders = new Dictionary<string, string>
                                    {
                                        { "Authorization", $"Bearer {token}" } // 🔐 Authenticated WebSocket connection
                                    }
                                });

                            await socket.ConnectAsync();
                            programLogger.LogInformation("✅ Socket.IO connected.");

                            //Connecting to CNC Port
                            await cncAuthService.OpenPort(socket);
                            programLogger.LogInformation("✅ Connected to CNC Port.");

                            //Initializing and Starting live data publisher
                            var liveDataPublisher = new CNCLiveDataPublisher(cncLiveLogger, mqttClient, socket);
                            liveDataPublisher.Start();

                            programLogger.LogInformation("🏁 CNCLiveDataPublisher is now running.");
                        }
                        catch (Exception ex)
                        {
                            programLogger.LogError($"Error establishing socket connection: {ex.Message}");
                        }
                    }
                }


            }; 
            await Task.Delay(-1);
        }
    }
}
