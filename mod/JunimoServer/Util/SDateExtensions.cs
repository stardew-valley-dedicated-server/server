using StardewValley;
using StardewModdingAPI.Utilities;

namespace JunimoServer.Util
{
    public static class SDateExtensions
    {
        public static bool EqualsIgnoreYear(this SDate date, SDate otherDate)
        {
            return date.Day == otherDate.Day && date.Season == otherDate.Season;
        }
        
        public static bool IsDayZero(this SDate date)
        {
            return date.Day == 0 && date.Season == Season.Spring && date.Year == 1;
        }
    }
}