using GC.Infrastructure;
using System.Text.Json;

internal static class MissionMetadataChecks
{
    public const string Header = """
        GC_MissionHeader {
         World "{AAAAAAAAAAAAAAAA}worlds/Test.ent"
         m_sName "TVT (20-32) Example"
         m_sDescription "One paragraph"\
          "Another paragraph"
         m_iPlayerCount 32
         m_aManualTags {
          "Night" "Training"
         }
         m_GC_Catalog GC_CatalogMissionMetadata {
          m_iSchemaVersion 1
          m_sMissionType "COOP"
          m_sMapName "Arland"
          m_iMinPlayers 20
          m_iRecommendedPlayers 24
          m_sEra "1980s"
          m_aObjectives {
           "Convoy" "Search & Rescue"
          }
          m_aPlayerAssets {
           "Trucks"
          }
          m_aRequiredAddonIds {
           "6A115D5DFF7ED870"
          }
          m_aForces {
           GC_CatalogForce {
            m_sFactionKey "US"
            m_sForceType "MOTORIZED"
            m_iPlayable 1
            m_iSlots 32
           }
           GC_CatalogForce {
            m_sFactionKey "USSR"
            m_iPlayable 0
           }
          }
          m_sGroupId "Convoy variants"
          m_fGroupSimilarity 0.75
         }
        }
        """;
    public const string Generated = """
        m_iSchemaVersion 1
        m_sWorld "{AAAAAAAAAAAAAAAA}worlds/Test.ent"
        m_sCaptureScope "world_subscene"
        m_iEntityCount 6
        m_aFactions { "US" "USSR" }
        m_aAssets {
         GC_MissionMetadataAsset {
          m_sCategory "tank"
          m_iCount 4
         }
        }
        m_aLimitations { "Placed content only" }
        m_aPrefabs { GC_MissionMetadataPrefab { m_sPrefab "not a player asset" } }
        """;
    static string Embed(string header, string generated) => header.TrimEnd()[..^1]+"\n m_GC_GeneratedMetadata GC_GeneratedMissionMetadata {\n"+generated+"\n }\n}";
    static CatalogMission Parse(string header=Header,string? extra=null,string? generated=null) => CatalogSync.Parse("Missions/Test.conf",generated is null?header:Embed(header,generated),"{1111111111111111}",extra,"test");
    static void Check(bool value,string message){if(!value)throw new Exception(message);Console.WriteLine("PASS: "+message);}
    static void Reject(Action action,string message){try{action();}catch(InvalidDataException){Console.WriteLine("PASS: "+message);return;}throw new Exception(message);}

    public static void Run()
    {
        Check(MissionCategories.Types.SequenceEqual(new[]{"COOP","TVT","COTVT","TRNG","LOL","TEST","BROKEN"}),"Only the seven requested mission types are supported");
        Check(Parse(Header.Replace("\"COOP\"","\"CO-OP - Cooperative\"").Replace("\"Trucks\"","\"Not set\"")).Metadata.GetProperty("assetTypes").GetArrayLength()==0,"Native combo labels import and empty picker entries are omitted");
        Check(Parse(Header.Replace("m_sMissionType \"COOP\"", "m_sMissionType \"BROKEN\"")).Metadata.GetProperty("missionType").GetString()=="BROKEN","Broken is an explicit mission type");
        Check(Parse(Header.Replace("m_aForces", "m_aFactions")).Metadata.GetProperty("forces").GetArrayLength()==0,"Retired faction inputs do not override world data");
        var m=Parse(generated:Generated);var d=m.Metadata;
        Check(!d.TryGetProperty("startTime",out _),"Older snapshot does not invent a start time");
        string Hours(string hours,bool allow=false)=>Generated+"\nm_aStartHours { "+hours.Replace(',',' ')+" }\nm_bAllowHeaderTime "+(allow?"1":"0");
        foreach(var (hours,period) in new[]{("0,5,21,23","Night"),("6,11","Morning"),("12,16","Midday"),("17,20","Evening"),("5,6","Variable")})
        {
            var timed=Parse(generated:Hours(hours));
            Check(timed.Metadata.GetProperty("startTime").GetString()==period && timed.Tags.Contains(period),"Displayed and searchable start period: "+period);
        }
        var overrideHeader=Header.Replace("m_iPlayerCount 32","m_iPlayerCount 32\n m_bOverrideScenarioTimeAndWeather 1\n m_iStartingHours 22");
        Check(Parse(overrideHeader,generated:Hours("8",true)).Metadata.GetProperty("startTime").GetString()=="Night","Allowed header changes starting hour");
        Check(Parse(overrideHeader,generated:Hours("8")).Metadata.GetProperty("startTime").GetString()=="Morning","World preset retains priority");
        Check(Parse(generated:Generated+"\nm_bRandomStartTime 1").Metadata.GetProperty("startTime").GetString()=="Variable","Random start gets a variable label");
        Reject(()=>Parse(generated:Hours("24")),"Out-of-range start hour rejected");
        var composition=Parse(extra:"{\"assetTypes\":[\"APCs\",\"IFVs\",\"Mechanized\",\"Airborne\",\"Transport helicopters\",\"Attack helicopters\",\"Aircraft\",\"Drones\"]}");
        Check(composition.Metadata.GetProperty("assetTypes").EnumerateArray().Select(x=>x.GetString()).SequenceEqual(new[]{"Mechanized","Airborne","Drones"}),"Mechanized consolidates historical vehicle tags while airborne and drones remain independent");
        var addonSnapshot=Generated+"\n"+"""
            m_aLoadedAddons {
             GC_MissionMetadataAddon {
              m_sTitle "Used mod"
              m_bReferencedByPlacedPrefab 1
             }
             GC_MissionMetadataAddon { m_sTitle "Unused mod" }
             GC_MissionMetadataAddon {
              m_sTitle "Arma Reforger"
              m_bVanilla 1
              m_bReferencedByPlacedPrefab 1
             }
            }
            """;
        var detected=Parse(extra:"{\"mods\":[\"Former tag\"]}",generated:addonSnapshot);
        Check(detected.Metadata.GetProperty("mods").EnumerateArray().Select(x=>x.GetString()).SequenceEqual(new[]{"Used mod"}) && detected.Tags.Contains("Used mod") && !detected.Tags.Contains("Former tag"),"Only referenced non-vanilla mods appear in mission details and search");
        var slots=Generated+"\n"+"""
            m_aPlayerSlots {
             GC_MissionMetadataSlot "{1234567890123456}" {
              m_sGroup "Alpha"
              m_iIndex 0
              m_sFaction "US"
              m_sPrefab "Prefabs/Medic.et"
              m_sName "Medic"
              m_sLoadoutFaction "US"
             }
            }
            """;
        Check(Parse(generated:slots).Metadata.GetProperty("generated").GetProperty("playerSlots")[0].GetProperty("prefab").GetString()=="Prefabs/Medic.et","Native player slots and loadout references survive intake");
        Reject(()=>Parse(generated:slots.Replace("m_iIndex 0","m_iIndex zero")),"Malformed native slot index rejected");
        Reject(()=>Parse(generated:slots.Replace("GC_MissionMetadataSlot", "UnknownSlot")),"Unknown native slot class rejected");
        Check(Parse(generated:slots.Replace("m_iIndex 0", "")).Metadata.GetProperty("generated").GetProperty("playerSlots")[0].GetProperty("index").GetInt32()==0,"Omitted native default slot index is zero");
        Reject(()=>Parse(generated:Generated.Replace("m_iSchemaVersion 1", "m_iSchemaVersion 99")),"Unsupported native schema rejected");
        Reject(()=>Parse(generated:Generated.Replace("world_subscene", "unknown")),"Unknown native capture scope rejected");
        Reject(()=>Parse(generated:addonSnapshot.Replace("m_bVanilla 1", "m_bVanilla yes")),"Malformed native bool rejected");
        Check(Parse(generated:Generated.Replace("m_iSchemaVersion 1", "")).Metadata.GetProperty("generated").GetProperty("schemaVersion").GetInt32()==1,"Omitted attributed schema default is one");
        Check(!Parse().Metadata.TryGetProperty("generated",out _),"A header without generated data remains supported");
        Check(d.GetProperty("missionType").GetString()=="COOP" && d.GetProperty("minPlayers").GetInt32()==20 && d.GetProperty("recommendedPlayers").ValueKind==JsonValueKind.Null && m.MaxPlayers==32,"Native capacity and GC minimum are retained; recommended count is retired");
        Check(m.Description=="One paragraph\nAnother paragraph","Multiline description survives serialized config parsing");
        Check(d.GetProperty("forces").GetArrayLength()==0,"Old manually authored forces are ignored");
        Check(d.GetProperty("missionGroup").GetProperty("similarity").GetDouble()==0.75 && m.Tags.Contains("Search & Rescue"),"Legacy family data and objective tags preserved");
        Check(d.GetProperty("assetTypes").GetArrayLength()==1 && !m.Tags.Contains("tank") && !d.GetProperty("generated").TryGetProperty("prefabs",out _),"Detected tanks do not become player equipment; bulky prefab records omitted");
        Check(Parse(extra:"{\"objectives\":[],\"minPlayers\":12}").Metadata.GetProperty("objectives").GetArrayLength()==0,"Explicit sidecar values can clear categories without erasing unrelated metadata");
        var legacy=Parse("""
            SCR_MissionHeader {
             World "{AAAAAAAAAAAAAAAA}worlds/Test.ent"
             m_sName "COTVT (50-69) Kassel"
             m_iPlayerCount 69
             m_GC_Metadata GC_MissionMetadata "{1234567890123456}" {
              m_eEra ERA_1980s
              m_eSpecialAssets {
               MORTARS
              }
              m_FriendlyForce GC_ForceDefinition {
               m_sFactionKey "USSR"
               m_eType MECHANIZED
              }
              m_OpposingForce GC_ForceDefinition {
               m_sFactionKey "US"
               m_eType MECHANIZED
              }
              m_MissionGroup GC_MissionGroupDefinition {
               m_sGroupID "Kassel"
               m_fGroupSimilarity 0.75
              }
             }
            }
            """);
        Check(legacy.Tags.Contains("Artillery")&&!legacy.Tags.Contains("Mortars")&&legacy.Tags.Contains("1980s")&&legacy.Metadata.GetProperty("forces")[1].GetProperty("playable").ValueKind==JsonValueKind.Null,"Legacy mortars import as Artillery and unspecified opposing playability stays unknown");
        var minimal=Parse("World \"{AAAAAAAAAAAAAAAA}worlds/Test.ent\"\nm_sName \"TVT70 Old mission\"\n// m_sName \"spoof\"");
        Check(minimal.Metadata.GetProperty("missionType").GetString()=="TVT"&&minimal.MaxPlayers is null&&minimal.Metadata.GetProperty("minPlayers").ValueKind==JsonValueKind.Null,"Legacy name prefix supported; missing counts remain unknown");
        foreach(var extra in new[]{"{\"minPlayers\":40}","{\"minPlayers\":20,\"recommendedPlayers\":10}","{\"minPlayers\":\"20\"}","{\"schemaVersion\":99}","{\"forces\":[{\"faction\":\"US\",\"playable\":\"yes\"}]}","{\"requiredAddonIds\":[\"Lobby2\"]}","{\"missionGroup\":{\"id\":\"x\",\"similarity\":2}}"})
            Reject(()=>Parse(extra:extra),"Invalid typed metadata rejected: "+extra);
        Reject(()=>Parse(generated:Generated.Replace("worlds/Test.ent","worlds/Other.ent")),"Wrong-world generated snapshot rejected");
        Reject(()=>Parse(generated:Generated.Replace("AAAAAAAAAAAAAAAA","BBBBBBBBBBBBBBBB")),"Conflicting world GUID rejected even when paths match");
        Reject(()=>Parse(Header.Replace("m_sMissionType \"COOP\"","m_sMissionType \"COOP\"\nm_sMissionType \"TVT\"")),"Duplicate metadata fields rejected");
        Reject(()=>Parse("GC_MissionHeader : \"{1111111111111111}Parent.conf\" {\n m_sName \"Inherited\"\n}"),"Unresolved header inheritance rejected explicitly");
        var manifest="""{"key":"test","world":"{AAAAAAAAAAAAAAAA}worlds/Test.ent","worldAliases":[],"tags":[],"name":"Old"}""";
        using var local=JsonDocument.Parse(LocalMissionImport.WithHeader(manifest,Embed(Header,Generated)));
        Check(local.RootElement.GetProperty("name").GetString()==m.Name&&local.RootElement.GetProperty("requiredAddonIds").GetArrayLength()==1,"Local import uses the same native header contract");
        Reject(()=>LocalMissionImport.WithHeader(manifest.Replace("AAAAAAAAAAAAAAAA","BBBBBBBBBBBBBBBB"),Header),"Local header world mismatch rejected");
    }
}
