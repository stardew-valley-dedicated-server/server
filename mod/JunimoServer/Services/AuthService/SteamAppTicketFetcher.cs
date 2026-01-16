namespace JunimoServer.Services.Auth
{
    /// <summary>
    /// Response from the steam-auth service containing an encrypted app ticket.
    /// </summary>
    public class SteamEncryptedAppTicket
    {
        public string Ticket { get; set; }
        public string SteamId { get; set; }
        public long Created { get; set; }
        public long Expiry { get; set; }
    }
}
