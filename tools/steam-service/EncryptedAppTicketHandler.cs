using SteamKit2;
using SteamKit2.Internal;

namespace SteamService;

/// <summary>
/// Handler for ClientRequestEncryptedAppTicketResponse messages from Steam.
/// </summary>
public class EncryptedAppTicketHandler : ClientMsgHandler
{
    private readonly SteamAuthService _service;

    public EncryptedAppTicketHandler(SteamAuthService service)
    {
        _service = service;
    }

    public override void HandleMsg(IPacketMsg packetMsg)
    {
        if (packetMsg.MsgType == EMsg.ClientRequestEncryptedAppTicketResponse)
        {
            var response = new ClientMsgProtobuf<CMsgClientRequestEncryptedAppTicketResponse>(packetMsg);

            var result = (EResult)response.Body.eresult;
            var appId = response.Body.app_id;
            var encryptedTicket = response.Body.encrypted_app_ticket;

            byte[]? ticketBytes = null;
            if (encryptedTicket != null)
            {
                // Serialize the entire EncryptedAppTicket protobuf message (matches steam-user's _encodeProto)
                using var ms = new MemoryStream();
                ProtoBuf.Serializer.Serialize(ms, encryptedTicket);
                ticketBytes = ms.ToArray();
            }

            _service.HandleEncryptedAppTicketResponse(result, appId, ticketBytes);
        }
    }
}
