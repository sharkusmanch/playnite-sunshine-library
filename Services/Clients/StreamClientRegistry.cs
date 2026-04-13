using System.Collections.Generic;
using System.Linq;

namespace SunshineLibrary.Services.Clients
{
    public class StreamClientRegistry
    {
        private readonly List<StreamClient> clients;

        public StreamClientRegistry()
        {
            clients = new List<StreamClient>
            {
                new MoonlightClient(),
                // Future: new ArtemisClient(), new MoonlightEmbeddedClient(), ...
            };
        }

        public IReadOnlyList<StreamClient> All => clients;

        public StreamClient GetById(string id) =>
            clients.FirstOrDefault(c => c.Id == id);

        public StreamClient Resolve(ClientSettings settings) =>
            GetById(settings?.ActiveClientId ?? MoonlightClient.ClientId) ?? clients[0];
    }
}
