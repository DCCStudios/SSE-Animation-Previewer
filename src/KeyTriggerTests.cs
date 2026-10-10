using System.Text;
namespace SSEPreview;

internal static class KeyTriggerTests
{
    internal static void Run(Action<bool,string> check)
    {
        string root=Path.Combine(AppContext.BaseDirectory,"key-group-fixture");Directory.CreateDirectory(root);
        var assets=new Assets();
        void Config(string name,string content){string file=Path.Combine(root,name);File.WriteAllText(file,content);assets.External[@"SKSE\plugins\dtryKeyUtil\config\custom\"+name]=file;}
        Config("first.ini","[KeyTrace_EventID_Keyboard]\nlabel-a=45|1|keys.esp\n[KeyTrace_EventID_Mouse]\nlabel-a=45|7|keys.esp\n[KeyTrace_userEvent]\nlabel-a=Forward|10|keys.esp\nlabel-b=forward|7|keys.esp\n");
        Config("second.ini","[keytrace_eventid_keyboard]\nlabel-b=045|4|keys.esp\nduplicate=45|1|keys.esp\ninvalid=bad|7|keys.esp\n");
        var records=new Records();
        GameRecord Record(string kind,uint id){var r=new GameRecord{Kind=kind,Plugin="keys.esp",Id=id,Key=$"keys.esp|{id:X6}"};records.All[r.Key]=r;return r;}
        foreach(uint id in new uint[]{1,4,7,10}){
            var spell=Record("SPEL",id);var spit=new byte[36];BitConverter.GetBytes(4u).CopyTo(spit,8);
            spell.Fields.Add(new("SPIT",spit));spell.Fields.Add(new("EFID",BitConverter.GetBytes(id+1)));spell.Fields.Add(new("EFIT",new byte[12]));
            var magic=Record("MGEF",id+1);var data=new byte[152];BitConverter.GetBytes(id+2).CopyTo(data,96);magic.Fields.Add(new("DATA",data));
            string model=$@"previewer\key-{id}.nif";Record("ARTO",id+2).Fields.Add(new("MODL",Encoding.UTF8.GetBytes(model)));
            assets.External[@"meshes\"+model]="record-only fixture";
        }
        var effects=new Effects(assets,records);effects.ReadConfigs();
        var groups=effects.Triggers.Where(t=>t.AdditionalInstructions!=null).ToArray();
        check(groups.Length==2,"Repeated dtry device/input bindings produce one grouped press and release");
        check(effects.Triggers.Count==14,"Grouped actions retain individual entries and do not merge mouse or case-sensitive user events");
        var press=groups.Single(t=>t.Instruction.StartsWith("@APPLYSPELL"));var release=groups.Single(t=>t.Instruction.StartsWith("@UNAPPLYSPELL"));
        check(press.Instructions.Count()==3&&press.Conditions.Contains("label-a=45")&&press.Conditions.Contains("label-b=045")&&press.Source.Contains("first.ini")&&press.Source.Contains("second.ini"),
            "Group identity uses input values across configs, normalizes numeric IDs, and preserves every source binding");
        check(effects.Diagnostics.Any(d=>d.Contains("invalid dtry input ID bad")),"Malformed input IDs are reported and excluded");
        check(effects.Preview(press,.2).Count==2,"Grouped press resolves both distinct abilities and deduplicates repeated spells");
        var cues=effects.Build(new(),false,[new(press,.2),new(press,.5),new(release,1.25),new(press,2)]);
        check(cues.Count==4&&cues.Count(c=>c.Time==.2&&c.RemovedAt==1.25)==2&&cues.Count(c=>c.Time==2&&double.IsPositiveInfinity(c.RemovedAt))==2,
            "Grouped timeline press/release removes every active binding, ignores repeated presses, and permits reapplication");
        var rewind=effects.Build(new(),false,[new(press,.2),new(release,1.25)]);
        check(rewind.Count==2&&rewind.All(c=>c.Time==.2&&c.RemovedAt==1.25),"Rebuilding grouped trigger history has no leaked active spell state");
        check(effects.Diagnostics.Count(d=>d.Contains("invalid dtry input ID bad"))==1,"Configuration warnings survive repeated timeline builds without duplicating");
        effects.Preview(new("Missing spell","@CAST|999|keys.esp","fixture"),0);
        check(effects.Diagnostics.Any(d=>d.StartsWith("Missing spell"))&&effects.Diagnostics.Any(d=>d.Contains("invalid dtry input ID bad")),
            "Direct preview combines current effect failures with retained configuration warnings");
        effects.Preview(press,0);
        check(!effects.Diagnostics.Any(d=>d.StartsWith("Missing spell"))&&effects.Diagnostics.Count(d=>d.Contains("invalid dtry input ID bad"))==1,
            "A new direct preview removes stale effect failures while retaining config warnings");
        Config("second.ini","[KeyTrace_EventID_Keyboard]\nlabel-b=045|4|keys.esp\nduplicate=45|1|keys.esp\n");
        effects.ReadConfigs();effects.Build(new(),false);
        check(effects.Diagnostics.Count==0,"Reloading corrected configuration clears resolved warnings");
    }
}
