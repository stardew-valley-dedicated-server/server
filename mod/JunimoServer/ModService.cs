using StardewModdingAPI;

namespace JunimoServer
{
    public interface IModService
    {
        public void Entry();
    }

    public abstract class ModService : IModService
    {
        protected readonly IModHelper Helper;
        protected readonly IMonitor Monitor;

        public ModService()
        {
        }

        public ModService(IModHelper helper)
        {
            Helper = helper;
        }

        public ModService(IMonitor monitor)
        {
            Monitor = monitor;
        }

        public ModService(IModHelper helper, IMonitor monitor)
        {
            Helper = helper;
            Monitor = monitor;
        }

        public virtual void Entry()
        {

        }
    }
}
