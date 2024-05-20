using StardewModdingAPI.Utilities;

namespace JunimoServer.Util
{
    public class SDateHelper
    {
        private static readonly SDate EggFestival = new SDate(13, "spring");
        private static readonly SDate FlowerDance = new SDate(24, "spring");
        private static readonly SDate Luau = new SDate(11, "summer");
        private static readonly SDate DanceOfJellies = new SDate(28, "summer");
        private static readonly SDate StardewValleyFair = new SDate(16, "fall");
        private static readonly SDate SpiritsEve = new SDate(27, "fall");
        private static readonly SDate FestivalOfIce = new SDate(8, "winter");
        private static readonly SDate FeastOfWinterStar = new SDate(25, "winter");

        private static readonly SDate GrandpasGhost = new SDate(1, "spring", 3);

        private static readonly SDate[] Festivals =
        {
            EggFestival, FlowerDance, Luau, DanceOfJellies, StardewValleyFair, SpiritsEve, FestivalOfIce,
            FeastOfWinterStar
        };


        public static bool IsFestivalToday()
        {
            var currentDate = SDate.Now();

            // grandpas ghost only happens on year 3
            if (currentDate == GrandpasGhost)
            {
                return true;
            }

            foreach (var festival in Festivals)
            {
                if (currentDate.EqualsIgnoreYear(festival))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsEggFestivalToday()
        {
            return SDate.Now().EqualsIgnoreYear(EggFestival);
        }

        public static bool IsFlowerDanceToday()
        {
            return SDate.Now().EqualsIgnoreYear(FlowerDance);
        }
        
        public static bool IsLuauToday()
        {
            return SDate.Now().EqualsIgnoreYear(Luau);
        }

        public static bool IsDanceOfJelliesToday()
        {
            return SDate.Now().EqualsIgnoreYear(DanceOfJellies);
        }

        public static bool IsStardewValleyFairToday()
        {
            return SDate.Now().EqualsIgnoreYear(StardewValleyFair);
        }

        public static bool IsSpiritsEveToday()
        {
            return SDate.Now().EqualsIgnoreYear(SpiritsEve);
        }

        public static bool IsFestivalOfIceToday()
        {
            return SDate.Now().EqualsIgnoreYear(FestivalOfIce);
        }

        public static bool IsFeastOfWinterStarToday()
        {
            return SDate.Now().EqualsIgnoreYear(FeastOfWinterStar);
        }
    }
}