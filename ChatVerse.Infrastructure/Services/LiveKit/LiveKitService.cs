using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatVerse.Infrastructure.Services.LiveKit;

public class LiveKitService
{
    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly string _serverUrl;
    private readonly RoomServiceClient _roomClient;
    private readonly ILogger<LiveKitService> _logger;

    public LiveKitService(IConfiguration config, ILogger<LiveKitService> logger)
    {
        _apiKey = config["LiveKit:ApiKey"]!;
        _apiSecret = config["LiveKit:ApiSecret"]!;
        _serverUrl = config["LiveKit:ServerUrl"]!;
        _roomClient = new RoomServiceClient(_serverUrl, _apiKey, _apiSecret);
        _logger = logger;
    }

    // ============================================================
    //  GenerateToken
    // ============================================================
    public string GenerateToken(
        string roomName,
        string userId,
        string username,
        bool canPublish = true,
        bool canSubscribe = true)
    {
        var grants = new VideoGrants
        {
            RoomJoin = true,
            Room = roomName,
            CanPublish = canPublish,
            CanSubscribe = canSubscribe,
            CanPublishData = true
        };

        var token = new AccessToken(_apiKey, _apiSecret)
            .WithIdentity(userId)
            .WithName(username)
            .WithTtl(TimeSpan.FromHours(4))
            .WithGrants(grants);

        return token.ToJwt();
    }

    // ============================================================
    //  CreateRoomAsync
    // ============================================================
    public async Task<bool> CreateRoomAsync(
        string roomName,
        int emptyTimeoutSeconds = 300,
        int maxParticipants = 10)
    {
        try
        {
            await _roomClient.CreateRoom(new CreateRoomRequest
            {
                Name = roomName,
                EmptyTimeout = (uint)emptyTimeoutSeconds,
                MaxParticipants = (uint)maxParticipants
            });

            _logger.LogInformation("LiveKit room created: {RoomName}", roomName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create LiveKit room: {RoomName}", roomName);
            return false;
        }
    }

    // ============================================================
    //  GetRoomParticipantsAsync
    // ============================================================
    public async Task<List<Livekit.Server.Sdk.Dotnet.ParticipantInfo>>
        GetRoomParticipantsAsync(string roomName)
    {
        try
        {
            var response = await _roomClient.ListParticipants(
                new ListParticipantsRequest { Room = roomName });

            return response.Participants.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get participants: {RoomName}", roomName);
            return new();
        }
    }

    // ============================================================
    //  RemoveParticipantAsync
    // ============================================================
    public async Task<bool> RemoveParticipantAsync(string roomName, string userId)
    {
        try
        {
            await _roomClient.RemoveParticipant(new RoomParticipantIdentity
            {
                Room = roomName,
                Identity = userId
            });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove participant {UserId}", userId);
            return false;
        }
    }

    // ============================================================
    //  DeleteRoomAsync
    // ============================================================
    public async Task<bool> DeleteRoomAsync(string roomName)
    {
        try
        {
            await _roomClient.DeleteRoom(new DeleteRoomRequest
            {
                Room = roomName      // ← 'Name' nahi, 'Room' hai is SDK mein
            });
            _logger.LogInformation("LiveKit room deleted: {RoomName}", roomName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete room: {RoomName}", roomName);
            return false;
        }
    }

    // ============================================================
    //  ListActiveRoomsAsync
    // ============================================================
    public async Task<List<Livekit.Server.Sdk.Dotnet.Room>> ListActiveRoomsAsync()
    {
        try
        {
            var response = await _roomClient.ListRooms(new ListRoomsRequest());
            return response.Rooms.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list rooms");
            return new();
        }
    }

    public string GetServerUrl() => _serverUrl;
}

