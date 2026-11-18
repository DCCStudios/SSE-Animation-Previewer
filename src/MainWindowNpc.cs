using System.Drawing;
using FO4_Base_Library;

namespace SSEPreview;

// NPC looks: an NPC record supplies the race, sex, weight, head parts, hair colour, skin colour, outfit and the
// baked head the game would load for it. Rules and addresses are in NpcLook.cs and FaceTintComposite.cs.
public sealed partial class MainWindow
{
    const string NoNpc="(No NPC · race and head parts below)";
    readonly ComboBox npcs=Combo();
    readonly CheckBox bakedHead=new(){Text="Use the NPC's baked head (FaceGeom)",AutoSize=true,Checked=true};
    readonly Label npcNote=new(){AutoSize=true,MaximumSize=new Size(330,0)};
    NpcLook? npcLook;
    readonly Dictionary<string,string> faceTintCache=[];long faceTintRevision=-1;

    void PopulateNpcs(string? keepKey=null)
    {
        bool was=selecting;selecting=true;
        try{
            npcs.Items.Clear();npcs.Items.Add(NoNpc);npcs.Items.AddRange(records.Of("NPC_").Cast<object>().ToArray());
            var kept=keepKey==null?null:records.All.GetValueOrDefault(keepKey);
            if(kept?.Kind=="NPC_"){npcs.SelectedItem=kept;npcLook=NpcLooks.Build(records,kept);}
            else{npcs.SelectedIndex=0;npcLook=null;if(keepKey!=null)assets.Diagnostics.Add("Selected NPC "+keepKey+" is no longer loaded; the race's own looks are shown.");}
        }finally{selecting=was;}
        UpdateNpcNote();PopulatePartnerRaces();
    }
    void ClearNpc()
    {
        if(npcLook==null)return;
        bool was=selecting;selecting=true;try{npcs.SelectedIndex=0;npcLook=null;}finally{selecting=was;}
        UpdateNpcNote();
    }
    // Choosing an NPC fills the character controls from its record, so every one of them can still be changed.
    void ChooseNpc()
    {
        if(selecting)return;
        if(npcs.SelectedItem is not GameRecord npc){npcLook=null;UpdateNpcNote();RebuildScene();return;}
        var look=NpcLooks.Build(records,npc);npcLook=look;
        selecting=true;
        try{
            if(look.Race!=null){if(!races.Items.Contains(look.Race))races.Items.Add(look.Race);races.SelectedItem=look.Race;}
            sex.SelectedIndex=look.Female?1:0;clipSkeleton=null;
        }finally{selecting=false;}
        PopulateHeads();
        selecting=true;
        try{
            var wanted=look.Parts.Where(p=>((p.First("DATA")?.Bytes.FirstOrDefault()??0)&8)==0).ToList();
            foreach(var part in wanted)if(!heads.Items.Contains(part))heads.Items.Add(part,false);
            var keys=wanted.Select(p=>p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for(int i=0;i<heads.Items.Count;i++)heads.SetItemChecked(i,keys.Contains(((GameRecord)heads.Items[i]).Key));
            weight.Value=look.Weight;hairChoice=look.HairColor?.Key;
            worn.Clear();worn.AddRange(look.Outfit);
        }finally{selecting=false;}
        PopulateHairColors();UpdateNpcNote();RebuildScene();
    }
    void UpdateNpcNote()
    {
        bakedHead.Enabled=npcLook!=null;
        if(npcLook is not {} look){npcNote.Text="";return;}
        string head=assets.Exists(look.FaceGeom)?"baked head "+look.FaceGeom[@"meshes\actors\character\facegendata\facegeom\".Length..]:"no baked head file ("+look.FaceGeom[@"meshes\actors\character\facegendata\facegeom\".Length..]+")";
        string traits=look.Traits==look.Npc?"":$" · looks from {look.Traits.EditorId}";
        npcNote.Text=$"{look.Race?.EditorId??"unknown race"}, {(look.Female?"female":"male")}, weight {look.Weight}{traits} · skin {look.Skin.R}, {look.Skin.G}, {look.Skin.B}{(look.HasSkin?"":" (no QNAM)")} · {head}"+(Math.Abs(look.Height-1)>.001f?FormattableString.Invariant($" · height {look.Height:0.##} (not applied)"):"");
    }
    /// <summary>The face tint texture composed from the race's masks and the actor's layers, as a game path; null
    /// when the race has no tint masks in use (mid grey, which the engine uses for an empty slot, is the same).</summary>
    string? ComposedFaceTint(GameRecord? race,bool female)
    {
        var layers=NpcLooks.TintLayers(records,race,female,npcLook?.Layers);
        if(!layers.Any(l=>l.Alpha>0&&l.Mask.Texture.Length>0))return null;
        if(faceTintRevision!=assets.Revision){faceTintCache.Clear();faceTintRevision=assets.Revision;}
        string key=string.Join(";",layers.Where(l=>l.Alpha>0).Select(l=>FormattableString.Invariant($"{l.Mask.Texture}|{l.Color.ToArgb():X8}|{l.Alpha:0.###}")));
        if(faceTintCache.TryGetValue(key,out var cached)&&assets.Exists(cached))return cached;
        var notes=new List<string>();
        string path=FaceTintComposite.Register(assets,FaceTintComposite.Compose(assets,layers,notes));
        foreach(string note in notes)if(!scene.Diagnostics.Contains(note))scene.Diagnostics.Add(note);
        return faceTintCache[key]=path;
    }
    /// <summary>Adds the NPC's baked head in place of its head parts, under the game's own conditions (24727): the
    /// file exists and holds a shape named after each of the record's own head parts that has a model. Returns false
    /// when the parts have to be built instead, with the reason in Diagnostics.</summary>
    bool AddBakedHead(GameRecord? race,bool female,Color? hair,bool hairChosen,Color? skinTone,bool hairCovered)
    {
        if(npcLook is not {} look||!bakedHead.Checked)return false;
        if(look.Race!=race||look.Female!=female){scene.Diagnostics.Add("The race or sex was changed after the NPC was chosen, so its baked head is not used.");return false;}
        if(!assets.Exists(look.FaceGeom)){
            scene.Diagnostics.Add($"{look.Traits.EditorId} has no baked head ({look.FaceGeom}). In game its head is built from parts with an empty face tint (the dark face bug); here the tint is composed from the record.");
            return false;
        }
        Nifcontent_Class_Manolo nif;
        try{nif=new Nifcontent_Class_Manolo();nif.Load_Manolo(assets.Read(look.FaceGeom));}
        catch(Exception ex){scene.Diagnostics.Add("Baked head "+look.FaceGeom+" could not be read: "+ex.Message);return false;}
        var names=NifRenderableShape.FromNif(nif).Select(s=>s.ShapeName??"").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var absent=look.OwnParts.Where(p=>!string.IsNullOrWhiteSpace(p.First("MODL")?.Text)&&!names.Contains(p.EditorId)).Select(p=>p.EditorId).ToList();
        if(absent.Count>0){
            scene.Diagnostics.Add($"Baked head {look.FaceGeom} has no shape for head part {string.Join(", ",absent)}; the game rejects the file and builds the head from parts with an empty face tint (the dark face bug). Here the tint is composed from the record.");
            return false;
        }
        // Tint: the computed FaceTint file when the record lists a face part, else the path the NIF names (slot 6).
        bool ownsFace=look.OwnParts.Any(p=>p.First("PNAM")?.UInt()==1);
        string? composed=null;
        // Equipment on slot 31 replaces the hair part and its extras; in the baked file they are shapes named after the parts.
        HashSet<string>? hidden=null;
        if(hairCovered){
            hidden=records.HeadPartRecords(look.Parts.Where(p=>p.First("PNAM")?.UInt()==3)).Select(p=>p.EditorId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if(hidden.Count>0)scene.Diagnostics.Add("Hair head parts are not drawn: worn equipment covers slot 31 (Hair), as in game.");
        }
        scene.AddModel(look.FaceGeom,hairColor:hairChosen?hair:null,skinColor:skinTone,bipedSlots:1u,hiddenShapes:hidden,faceTextures:slots=>{
            for(int i=0;i<slots.Length;i++)slots[i]=GameTexture(slots[i]);
            string tint=ownsFace&&assets.Exists(look.FaceTint)?look.FaceTint:slots[6];
            if(tint.Length==0||!assets.Exists(tint)){
                composed??=ComposedFaceTint(race,female);
                scene.Diagnostics.Add($"Face tint {(tint.Length==0?look.FaceTint:tint)} is missing; "+(composed!=null?"it is composed from the record's tint layers.":"the face is drawn untinted."));
                tint=composed??"";
            }
            slots[6]=tint;return slots;
        });
        return true;
    }
    // A NIF written by the Creation Kit names its face tint "data\textures\...".
    static string GameTexture(string path)
    {
        path=Assets.Normalize(path??"");
        if(path.StartsWith(@"data\",StringComparison.OrdinalIgnoreCase))path=path[5..];
        return path;
    }
    /// <summary>The face shader's textures when the head is built from parts (26836): normal, subsurface and detail
    /// from the actor's face texture set, else the race's default for the sex; the tint from the composite.</summary>
    Func<string[],string[]> PartFaceTextures(GameRecord? race,bool female)
    {
        var set=npcLook?.FaceTextures??(race==null?null:records.Resolve(race,race.First(female?"DFTF":"DFTM")?.UInt()??0));
        var face=records.TexturePaths(set);string? tint=ComposedFaceTint(race,female);
        return slots=>{
            if(face!=null)foreach(int i in new[]{1,2,3})if(face[i].Length>0)slots[i]=face[i];
            slots[6]=tint??"";return slots;
        };
    }

    // Loads each NPC by EditorID (or plugin|id key), captures its head with the baked files and built from parts, and
    // compares the composed face tint with the baked one. Arguments: an output folder, Data or mod folders, then NPCs.
    async Task NpcSmoke(string[] arguments)
    {
        var log=new List<string>();string output=arguments.Length>0?Path.GetFullPath(arguments[0]):Path.Combine(AppContext.BaseDirectory,"npc-smoke");Directory.CreateDirectory(output);
        try{
            var folders=arguments.Skip(1).TakeWhile(Directory.Exists).ToArray();var wanted=arguments.Skip(1+folders.Length).ToArray();
            if(folders.Length==0||wanted.Length==0)throw new ArgumentException("--npc-smoke needs an output folder, at least one Data or mod folder, then NPC EditorIDs.");
            for(int i=0;i<folders.Length;i++)await Mount(folders[i],i>0);
            followCamera.Checked=false;scene.FollowScene=false;smpPhysics.Checked=false;gridFloor.Checked=false;scene.ShowFloor=false;
            log.Add($"{records.Of("NPC_").Count():N0} NPC records in {folders.Length} folder(s)");
            async Task Shot(string name,float distance){
                clock.Pause();scene.SetTime(clock.Time);scene.Frame();
                if(scene.Skeleton.SkeletonDictionary.TryGetValue("NPC Head [Head]",out var bone)){var at=bone.GetGlobalTransform.Translation;viewport.camera.FocusPosition=new OpenTK.Mathematics.Vector3(at.X,at.Y,at.Z+(distance>100?-18:2));viewport.camera.distance=distance;viewport.UpdateProjection(true);}
                await WaitRenderReady(120,name);
                using var image=viewport.CaptureBitmap();image?.Save(Path.Combine(output,name+".png"));
            }
            int failed=0;
            // "plugin:<file name>" stands for the first three NPCs that plugin defines.
            wanted=wanted.SelectMany(w=>w.StartsWith("plugin:",StringComparison.OrdinalIgnoreCase)?records.Of("NPC_").Where(n=>n.Key.StartsWith(w[7..]+"|",StringComparison.OrdinalIgnoreCase)&&assets.Exists(NpcLooks.Build(records,n).FaceGeom)).Take(3).Select(n=>n.Key):[w]).ToArray();
            foreach(string id in wanted){
                var npc=records.Of("NPC_").FirstOrDefault(n=>n.EditorId.Equals(id,StringComparison.OrdinalIgnoreCase))??(records.All.GetValueOrDefault(id) is {Kind:"NPC_"} byKey?byKey:null);
                if(npc==null){log.Add($"FAIL {id}: no such NPC in the loaded plugins");failed++;continue;}
                bakedHead.Checked=true;npcs.SelectedItem=npc;await Task.Delay(300);
                var look=npcLook!;string safe=string.Concat(npc.EditorId.Where(char.IsLetterOrDigit));
                log.Add($"== {npc.EditorId} [{npc.Plugin}] key {npc.Key}");
                log.Add("  "+npcNote.Text);
                log.Add($"  own parts: {string.Join(", ",look.OwnParts.Select(p=>p.EditorId))}; built list: {string.Join(", ",look.Parts.Select(p=>p.EditorId))}");
                log.Add($"  layers: {string.Join("; ",look.Layers.Select(l=>FormattableString.Invariant($"#{l.Index} {l.Color.R},{l.Color.G},{l.Color.B} x {l.Alpha:0.##}")))}");
                log.Add($"  outfit: {string.Join(", ",look.Outfit.Select(o=>o.EditorId))}; hair colour {look.HairColor?.EditorId??"(race default)"}; face textures {look.FaceTextures?.EditorId??"(race default)"}");
                foreach(string note in look.Notes)log.Add("  note: "+note);
                var layers=NpcLooks.TintLayers(records,look.Race,look.Female,look.Layers);var notes=new List<string>();
                var pixels=FaceTintComposite.Compose(assets,layers,notes);
                foreach(string note in notes)log.Add("  note: "+note);
                if(FaceTintComposite.Compare(assets,pixels,look.FaceTint) is {} difference)log.Add(FormattableString.Invariant($"  composed tint against baked {look.FaceTint}: mean {difference.Mean:0.00}, max {difference.Max} of 255"));
                else log.Add("  no baked face tint to compare with: "+look.FaceTint);
                bool baked=viewport.Model.meshes.Any(m=>m.MeshData.Shape.NifShader?.IsTypeFaceTint==true)&&assets.Exists(look.FaceGeom)&&!scene.Diagnostics.Any(d=>d.Contains("dark face"));
                log.Add($"  baked head used: {baked}");
                await Shot(safe+"-baked",70);await Shot(safe+"-baked-body",330);
                bakedHead.Checked=false;await Task.Delay(300);
                await Shot(safe+"-parts",70);
                foreach(var mesh in viewport.Model.meshes.Where(m=>m.MeshData.Shape.NifShader?.IsTypeFaceTint==true))
                    log.Add($"  parts face shape {mesh.MeshData.Shape.ShapeName}: {string.Join(" | ",mesh.MeshData.Material.Textures_Path_List.Select((t,i)=>t.Length>0?$"{i}:{t}":"").Where(t=>t.Length>0))}");
                foreach(string d in scene.Diagnostics.Distinct())log.Add("  scene: "+d);
            }
            log.Add(failed==0?"PASS":"FAIL "+failed+" NPC(s) not found");if(failed>0)Environment.ExitCode=1;
        }catch(Exception ex){log.Add("FAIL "+ex);Environment.ExitCode=1;}
        finally{File.WriteAllLines(Path.Combine(output,"npc-smoke.txt"),log);Close();}
    }
}
