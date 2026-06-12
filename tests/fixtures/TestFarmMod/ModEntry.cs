using System.Collections.Generic;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley.GameData;

namespace TestFarmMod;

/// <summary>
/// E2E test fixture mod. Appends a single entry to <c>Data/AdditionalFarms</c> so the
/// registry holds two entries (base-game MeadowlandsFarm + this one), letting the E2E suite
/// prove the server resolves a mod farm by its <c>Id</c> rather than by position. The farm
/// reuses the vanilla Standard map (<c>MapName: "Farm"</c>), so no custom .tmx/.xnb asset is
/// needed — only the registry entry matters for the by-Id test.
/// </summary>
public class ModEntry : Mod
{
    public override void Entry(IModHelper helper)
    {
        helper.Events.Content.AssetRequested += OnAssetRequested;
    }

    private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (!e.Name.IsEquivalentTo("Data/AdditionalFarms"))
        {
            return;
        }

        e.Edit(asset =>
        {
            var farms = asset.GetData<List<ModFarmType>>();
            farms.Add(
                new ModFarmType
                {
                    // Kept in sync by hand with ModFarmDisambiguationTests.FixtureFarmId — the
                    // test selects this Id over /newgame (separate assembly, can't share a const).
                    Id = "JunimoTest.SecondFarm",
                    // Reuse the vanilla Standard map; the by-Id test only needs the entry to be
                    // selectable, not to render a distinct map.
                    MapName = "Farm",
                    TooltipStringPath = "Strings/1_6_Strings:Farm_Standard_Description",
                    SpawnMonstersByDefault = false,
                }
            );
        });
    }
}
