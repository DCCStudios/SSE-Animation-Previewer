using System.Text.Json;
using FO4_Base_Library;
namespace SSEPreview;

// Application preferences are independent of scenes and game/mod paths.
internal sealed class ViewerPreferences
{
    public bool RequireAlt {get;set;}=true;
    public Dictionary<string,JsonElement> RenderSettings {get;set;}=[];
    public static string FilePath=>Path.Combine(AppContext.BaseDirectory,"viewer-settings.json");
    internal static readonly string[] Names=["Setting_LightRig_SSE","Setting_ShadowMaps16_SSE","Setting_BackColorName","Setting_BackFade","Setting_BackFadeDirectional","Setting_DrawHiddenSegments","Setting_GPUSkinning","Setting_LightsFollowCamera","Setting_RecalculateNormals","Setting_RenderGridColor","Setting_ShowHelperShapes","Setting_SingleBoneSkinning","Setting_TBN","Settings_Camara","Settings_RenderGrid"];
    public static ViewerPreferences Load(string path){
        var settings=File.Exists(path)?JsonSerializer.Deserialize<ViewerPreferences>(File.ReadAllText(path))??new():new ViewerPreferences();
        foreach(var name in Names)if(settings.RenderSettings.TryGetValue(name,out var value)){
            var property=typeof(Config_App).GetProperty(name)!;
            property.SetValue(Config_App.Current,value.Deserialize(property.PropertyType));
        }
        return settings;
    }
    public void Save(string path){
        foreach(var name in Names){var p=typeof(Config_App).GetProperty(name)!;RenderSettings[name]=JsonSerializer.SerializeToElement(p.GetValue(Config_App.Current),p.PropertyType);}
        SceneFile.Save(path,JsonSerializer.Serialize(this,new JsonSerializerOptions{WriteIndented=true}));
    }
    internal static void Verify(){
        var log=new List<string>();
        void Check(bool ok,string text){if(!ok)throw new InvalidDataException(text);log.Add("PASS "+text);}
        string path=Path.Combine(AppContext.BaseDirectory,"verification-viewer-settings.json");
        Config_App.Current.Setting_BackColorName="MidnightBlue";
        Config_App.Current.Setting_BackFade=37;
        Config_App.Current.Setting_GPUSkinning=false;
        var rig=Config_App.Current.ActiveLights();var key=rig.KeyLight;key.Strength=.73f;key.AzimuthDeg=123;rig.KeyLight=key;Config_App.Current.SetActiveLights(rig);
        var original=new ViewerPreferences{RequireAlt=false};original.Save(path);
        Config_App.Current=new Config_App{Game=Config_App.Game_Enum.Skyrim};
        var restored=Load(path);
        Check(!restored.RequireAlt,"Alt-free camera preference survives a fresh configuration");
        Check(Config_App.Current.Setting_BackColorName=="MidnightBlue"&&Config_App.Current.Setting_BackFade==37&&!Config_App.Current.Setting_GPUSkinning,"Background and rendering options restore");
        Check(Config_App.Current.ActiveLights().KeyLight.AzimuthDeg==123&&Math.Abs(Config_App.Current.ActiveLights().KeyLight.Strength-.73f)<.001,"Lighting direction and strength restore");
        foreach(var name in Names){var p=typeof(Config_App).GetProperty(name)!;var actual=JsonSerializer.SerializeToElement(p.GetValue(Config_App.Current),p.PropertyType);Check(actual.GetRawText()==original.RenderSettings[name].GetRawText(),name+" round trips");}
        using var viewport=new Viewport();viewport.RequireAlt=false;Check(viewport.CanStartDrag(Keys.None),"Alt-free mode accepts plain mouse drags");viewport.RequireAlt=true;Check(!viewport.CanStartDrag(Keys.None)&&viewport.CanStartDrag(Keys.Alt),"Maya-style mode requires Alt for mouse drags");
        restored.RequireAlt=true;restored.Save(path);Check(Load(path).RequireAlt,"Overwriting preferences persists the latest toggle");
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"preferences-tests.txt"),log);
    }
}
