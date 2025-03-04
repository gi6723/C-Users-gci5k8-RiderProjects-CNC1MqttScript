using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;
using SocketIOClient;

namespace CNC1MqttScript
{
    public class CNCLiveDataPublisher
    {
        private readonly ILogger<CNCLiveDataPublisher> _logger;
        private readonly IManagedMqttClient _mqttClient;
        private readonly SocketIOClient.SocketIO _socket;

        public CNCLiveDataPublisher(
            ILogger<CNCLiveDataPublisher> logger,
            IManagedMqttClient mqttClient,
            SocketIOClient.SocketIO socket)
        {
            _logger = logger;
            _mqttClient = mqttClient;
            _socket = socket;
        }

        // Use a bounded channel with a capacity limit (e.g. 1000) to apply backpressure.
        private readonly Channel<(string, SocketIOResponse)> _eventQueue = Channel.CreateBounded<(string, SocketIOResponse)>(
            new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.DropWrite // Drop events if full; alternatively, use Wait to backpressure.
            });

        // Dictionary to track the last time each topic was published.
        private readonly Dictionary<string, DateTime> _lastPublishedTime = new();
        private readonly TimeSpan _publishInterval = TimeSpan.FromMilliseconds(500); // Publish at most once every 500ms per topic

        public void Start()
        {
            _ = ProcessEventQueue(); // Start processing events in the background

            _socket.OnAny((string eventName, SocketIOResponse response) =>
            {
                // Add the event to the channel. If the channel is full, the event is dropped.
                _eventQueue.Writer.TryWrite((eventName, response));
            });

            _logger.LogInformation("📡 Universal CNC event listener is active.");
        }

        private async Task ProcessEventQueue()
        {
            await foreach (var (eventName, response) in _eventQueue.Reader.ReadAllAsync())
            {
                await HandleGenericEvent(eventName, response);
            }
        }

        private async Task HandleGenericEvent(string eventName, SocketIOResponse response)
        {
            try
            {
                switch (eventName)
                {
                    case "workflow:state":
                        await HandleWorkflowState(response, eventName);
                        break;
                    case "sender:status":
                        await HandleSenderStatus(response, eventName);
                        break;
                    case "controller:state":
                        await HandleControllerState(response, eventName);
                        break;
                    default:
                        string rawJson = response.ToString();
                        await PublishBulk(eventName, rawJson);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);
            }
        }

        private async Task PublishBulk(string eventName, string rawJson)
        {
            try
            {
                string topic = $"CNCS/CNC1/Data/Bulk/{eventName}";
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(rawJson)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
                    .WithRetainFlag(false)
                    .Build();

                await _mqttClient.EnqueueAsync(message);
                _logger.LogInformation($"📤 [Bulk] Published '{eventName}' to topic {topic}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Error publishing Bulk event '{eventName}': {ex.Message}");
            }
        }

        // --------------------------------------
        // Workflow State
        // --------------------------------------
        private async Task HandleWorkflowState(SocketIOResponse response, string eventName)
        {
            try
            {
                var json = response.GetValue<JsonElement>();
                string state = "unknown";

                _logger.LogInformation($"🔍 Parsing workflow state: {json}");

                // If the JSON itself is a string, use it directly.
                if (json.ValueKind == JsonValueKind.String)
                {
                    state = json.GetString() ?? "unknown";
                    _logger.LogInformation($"✅ Found workflow state as string: {state}");
                }
                // If JSON is an array, extract the first element if it is a string.
                else if (json.ValueKind == JsonValueKind.Array && json.GetArrayLength() > 0)
                {
                    JsonElement firstElement = json[0];
                    if (firstElement.ValueKind == JsonValueKind.String)
                    {
                        state = firstElement.GetString() ?? "unknown";
                        _logger.LogInformation($"✅ Found workflow state in array: {state}");
                    }
                }
                // If JSON is an object, try dictionary deserialization.
                else if (json.ValueKind == JsonValueKind.Object)
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json.GetRawText());
                    if (dict != null && dict.TryGetValue("workflow:state", out JsonElement stateElement))
                    {
                        state = stateElement.GetString() ?? "unknown";
                        _logger.LogInformation($"✅ Found workflow state in dictionary: {state}");
                    }
                }

                // Fallback: Use recursive search if still unknown.
                if (state == "unknown" && RecursiveSearch(json, "workflow:state", out JsonElement foundState))
                {
                    state = foundState.GetString() ?? "unknown";
                    _logger.LogInformation($"✅ Found workflow state using recursive search: {state}");
                }

                if (state == "unknown")
                {
                    _logger.LogWarning("⚠️ Workflow state was not found, returning 'unknown'.");
                }

                await PublishGuiData("WorkflowState", state);
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError($"❌ JSON Error parsing 'workflow:state': {jsonEx.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Error parsing 'workflow:state': {ex.Message}");
            }
        }

        // --------------------------------------
        // Sender Status
        // --------------------------------------
        private async Task HandleSenderStatus(SocketIOResponse response, string eventName)
        {
            try
            {
                var json = response.GetValue<JsonElement>();

                string gcodeFileName = "";
                float total = 0, sent = 0, received = 0;
                string elapsedTime = "", remainingTime = "";

                if (json.ValueKind == JsonValueKind.Object)
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json.GetRawText());
                    if (dict != null)
                    {
                        if (dict.TryGetValue("name", out JsonElement nameEl) && nameEl.ValueKind == JsonValueKind.String)
                            gcodeFileName = nameEl.GetString() ?? "";

                        if (dict.TryGetValue("total", out JsonElement totalEl))
                            total = ConvertToFloat(totalEl);

                        if (dict.TryGetValue("sent", out JsonElement sentEl))
                            sent = ConvertToFloat(sentEl);

                        if (dict.TryGetValue("received", out JsonElement receivedEl))
                            received = ConvertToFloat(receivedEl);

                        if (dict.TryGetValue("elapsedTime", out JsonElement elapsedEl))
                            elapsedTime = ConvertToString(elapsedEl);

                        if (dict.TryGetValue("remainingTime", out JsonElement remainingEl))
                            remainingTime = ConvertToString(remainingEl);
                    }
                }
                else if (json.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in json.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object)
                        {
                            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.GetRawText());
                            if (dict != null)
                            {
                                if (string.IsNullOrEmpty(gcodeFileName) && 
                                    dict.TryGetValue("name", out JsonElement nameEl) && 
                                    nameEl.ValueKind == JsonValueKind.String)
                                    gcodeFileName = nameEl.GetString() ?? "";

                                if (Math.Abs(total) < 0.00001 && dict.TryGetValue("total", out JsonElement totalEl))
                                    total = ConvertToFloat(totalEl);

                                if (Math.Abs(sent) < 0.00001 && dict.TryGetValue("sent", out JsonElement sentEl))
                                    sent = ConvertToFloat(sentEl);

                                if (Math.Abs(received) < 0.00001 && dict.TryGetValue("received", out JsonElement receivedEl))
                                    received = ConvertToFloat(receivedEl);

                                if (string.IsNullOrEmpty(elapsedTime) && 
                                    dict.TryGetValue("elapsedTime", out JsonElement elapsedEl))
                                    elapsedTime = ConvertToString(elapsedEl);

                                if (string.IsNullOrEmpty(remainingTime) && 
                                    dict.TryGetValue("remainingTime", out JsonElement remainingEl))
                                    remainingTime = ConvertToString(remainingEl);
                            }
                        }
                    }
                }

                // Fallback: Recursive search for missing fields.
                if (string.IsNullOrEmpty(gcodeFileName) && RecursiveSearch(json, "name", out JsonElement foundName))
                    gcodeFileName = foundName.GetString() ?? "";
                if (Math.Abs(total) < 0.00001 && RecursiveSearch(json, "total", out JsonElement foundTotal))
                    total = ConvertToFloat(foundTotal);
                if (Math.Abs(sent) < 0.00001 && RecursiveSearch(json, "sent", out JsonElement foundSent))
                    sent = ConvertToFloat(foundSent);
                if (Math.Abs(received) < 0.00001 && RecursiveSearch(json, "received", out JsonElement foundReceived))
                    received = ConvertToFloat(foundReceived);
                if (string.IsNullOrEmpty(elapsedTime) && RecursiveSearch(json, "elapsedTime", out JsonElement foundElapsed))
                    elapsedTime = ConvertToString(foundElapsed);
                if (string.IsNullOrEmpty(remainingTime) && RecursiveSearch(json, "remainingTime", out JsonElement foundRemaining))
                    remainingTime = ConvertToString(foundRemaining);

                // Publish the extracted data.
                await PublishGuiData("SelectedGcodeFile", gcodeFileName);
                await PublishGuiData("TotalGcodeCommands", total);
                await PublishGuiData("SentGcodeCommands", sent);
                await PublishGuiData("ProccessedGcodeCommands", received);
                await PublishGuiData("ElapsedTime", elapsedTime);
                await PublishGuiData("RemainingTime", remainingTime);
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Error parsing 'senderStatus': {ex.Message}");
            }
        }

        // --------------------------------------
        // Controller State
        // --------------------------------------
        private async Task HandleControllerState(SocketIOResponse response, string eventName)
        {
            try
            {
                // First argument is a string, e.g. "Grbl"
                string firstArg = response.GetValue<string>(0);
                // Second argument is the object with "status" and "parserstate"
                JsonElement secondArg = response.GetValue<JsonElement>(1);

                _logger.LogInformation($"[ControllerState] firstArg = {firstArg}");
                _logger.LogInformation($"[ControllerState] secondArg = {secondArg}");

                double feedrate = 0;
                double spindleSpeed = 0;
                string activeState = "unknown";

                if (secondArg.ValueKind == JsonValueKind.Object &&
                    secondArg.TryGetProperty("status", out JsonElement status) &&
                    status.ValueKind == JsonValueKind.Object)
                {
                    if (status.TryGetProperty("activeState", out JsonElement activeStateEl) &&
                        activeStateEl.ValueKind == JsonValueKind.String)
                    {
                        activeState = activeStateEl.GetString() ?? "unknown";
                    }

                    if (status.TryGetProperty("feedrate", out JsonElement feedEl))
                    {
                        feedrate = ConvertToFloat(feedEl);
                    }

                    if (status.TryGetProperty("spindle", out JsonElement spindleEl))
                    {
                        spindleSpeed = ConvertToFloat(spindleEl);
                    }

                    // Extract Wpos from within "status"
                    string wposFormatted = "";
                    if (status.TryGetProperty("wpos", out JsonElement wposElement) &&
                        wposElement.ValueKind == JsonValueKind.Object)
                    {
                        string x = wposElement.TryGetProperty("x", out JsonElement xEl) ? xEl.GetString() ?? "" : "";
                        string y = wposElement.TryGetProperty("y", out JsonElement yEl) ? yEl.GetString() ?? "" : "";
                        string z = wposElement.TryGetProperty("z", out JsonElement zEl) ? zEl.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(x) && !string.IsNullOrEmpty(y) && !string.IsNullOrEmpty(z))
                        {
                            wposFormatted = $"{x}~{y}~{z}";
                        }
                    }

                    if (!string.IsNullOrEmpty(wposFormatted))
                    {
                        await PublishGuiData("Wpos", wposFormatted);
                    }
                }
                else
                {
                    _logger.LogInformation("Could not find 'status' property in secondArg.");
                }

                // Fallback recursive searches for missing fields.
                if (Math.Abs(feedrate) < 0.00001 && RecursiveSearch(secondArg, "feedrate", out JsonElement foundFeedrate))
                    feedrate = ConvertToFloat(foundFeedrate);
                if (Math.Abs(spindleSpeed) < 0.00001 && RecursiveSearch(secondArg, "spindle", out JsonElement foundSpindle))
                    spindleSpeed = ConvertToFloat(foundSpindle);
                if (activeState == "unknown" &&
                    RecursiveSearch(secondArg, "activeState", out JsonElement foundActiveState) &&
                    foundActiveState.ValueKind == JsonValueKind.String)
                {
                    activeState = foundActiveState.GetString() ?? "unknown";
                }

                _logger.LogInformation($"[ControllerState] Final => feedrate={feedrate}, spindleSpeed={spindleSpeed}, activeState={activeState}");

                await PublishGuiData("FeedRate", feedrate);
                await PublishGuiData("SpindleSpeed", spindleSpeed);
                await PublishGuiData("ActiveState", activeState);
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Error parsing JSON in controller state: {ex.Message}");
            }
        }

        private float ConvertToFloat(JsonElement element)
        {
            try
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.Number:
                        if (element.TryGetSingle(out float floatVal))
                            return floatVal;
                        break;
                    case JsonValueKind.String:
                        if (float.TryParse(element.GetString(), out float parsedFloat))
                            return parsedFloat;
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[ConvertToFloat] Could not parse '{element}': {ex.Message}");
            }
            return 0f; // Default to 0 if parsing fails
        }

        private string ConvertToString(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return element.GetString() ?? "";
                case JsonValueKind.Number:
                    return element.GetRawText();
                default:
                    return "";
            }
        }

        // Recursive search for keys in the JSON structure.
        private static bool RecursiveSearch(JsonElement element, string key, out JsonElement foundElement)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty(key, out foundElement))
                    return true;

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (RecursiveSearch(property.Value, key, out foundElement))
                        return true;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement child in element.EnumerateArray())
                {
                    if (RecursiveSearch(child, key, out foundElement))
                        return true;
                }
            }
            foundElement = default;
            return false;
        }

        // Throttled publication of GUI data per topic.
        private async Task PublishGuiData(string eventName, object data)
        {
            try
            {
                // Ensure we only publish once every _publishInterval per topic.
                if (_lastPublishedTime.TryGetValue(eventName, out DateTime lastTime) &&
                    DateTime.UtcNow - lastTime < _publishInterval)
                {
                    return; // Skip if the last message was sent recently
                }

                // Update last sent timestamp.
                _lastPublishedTime[eventName] = DateTime.UtcNow;

                var payloadObj = new
                {
                    eventtime = DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalSeconds,
                    value = data
                };

                string topic = $"CNCS/CNC1/Data/GUI/{eventName}";
                string payload = JsonSerializer.Serialize(payloadObj);

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
                    .WithRetainFlag(false)
                    .Build();

                await _mqttClient.EnqueueAsync(message);
                _logger.LogInformation($"📤 [GUI] Published {payload} to {topic}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Error publishing GUI data: {ex.Message}");
            }
        }
    }
}
