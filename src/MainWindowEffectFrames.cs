using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.Json;
using FO4_Base_Library;

namespace SSEPreview;

/// <summary>--effect-frames: effect NIFs rendered as frame sequences (and GIFs) from a jobs file, in one process.</summary>
public sealed partial class MainWindow
{
    /// <summary>The jobs file. Settings at the top apply to every job; each job is one NIF and its frames.</summary>
    sealed class EffectFramesFile
    {
        public string? Data{get;set;}               // folder mounted alone; no plugin records, no animation library
        public string? Base{get;set;}               // Skyrim Data folder whose archives fill in what Data lacks
        public bool Pbr{get;set;}
        public int? Budget{get;set;}
        public string? Background{get;set;}         // a .NET colour name, flat (no fade)
        public bool Floor{get;set;}                 // the grid floor; off by default
        public float? FloorSize{get;set;}           // floor width in units (default 6144)
        public float? FarPlane{get;set;}            // far clip distance at least this far
        public string? Envmap{get;set;}
        public float? EnvIntensity{get;set;}
        public string? Window{get;set;}             // window size "WxH" (default 1500x950); the viewport is part of it
        public double ReadyTimeout{get;set;}=180;   // seconds to wait for a NIF and its textures before failing the job
        public string? Ffmpeg{get;set;}             // ffmpeg.exe, else SSE_FFMPEG, else ffmpeg on PATH
        public List<EffectFramesJob> Jobs{get;set;}=[];
    }
    sealed class EffectFramesJob
    {
        public string Nif{get;set;}="";
        public string Out{get;set;}="";
        public List<double>? Times{get;set;}        // explicit clip times, or Start + Fps + Count
        public double Start{get;set;}
        public double? Fps{get;set;}
        public int? Count{get;set;}
        public double? MaxClock{get;set;}       // upper bound on the effect clock (default 60 s); raise it for long sequences
        public double? Distance{get;set;}
        public float? Yaw{get;set;}
        public float? Pitch{get;set;}
        public List<float>? Focus{get;set;}         // [x, y, z]
        public bool FocusOrigin{get;set;}           // look at 0, 0, 100
        public List<EffectFramesExtra>? Extra{get;set;}
        public float? Bloom{get;set;}
        public int? Width{get;set;}                 // output size: crop to Width:Height around the centre, then scale
        public int? Height{get;set;}
        public string? Gif{get;set;}
        public double? GifFps{get;set;}
    }
    sealed class EffectFramesExtra{public string Nif{get;set;}="";public double Offset{get;set;}}

    /// <summary>Thrown when a NIF or its textures were not ready in time: the caller may retry (exit code 3).</summary>
    sealed class NotReadyException(string message):Exception(message);

    /// <summary>Renders until the engine has no pending pipeline work and every texture is uploaded. Textures upload
    /// on render ticks, so each pass asks for a frame; a capture taken earlier is the "Loading..." card.</summary>
    async Task WaitRenderReady(double timeoutSeconds,string what)
    {
        var watch=Stopwatch.StartNew();int pass=0;
        // Can_Render turns true only once the engine has set up mesh geometry. An effect made only of particle systems
        // has no engine mesh, so it never does; the previewer draws those particles itself, and they are ready as is.
        bool Ready()=>viewport.Model.TexturesReady&&(viewport.Model.Can_Render||viewport.Model.meshes.Count==0)&&!(viewport.Intent?.HasWork??false);
        while(!Ready()){
            if(watch.Elapsed.TotalSeconds>timeoutSeconds)
                throw new NotReadyException($"{what}: not ready after {timeoutSeconds:0} s (textures ready {viewport.Model.TexturesReady}, renderable {viewport.Model.Can_Render}, pipeline work {viewport.Intent?.HasWork})");
            viewport.UpdateRequired=true;viewport.Invalidate();
            // A hidden window may not get paint messages often; render directly now and then.
            if(++pass%5==0){using var _=viewport.CaptureBitmap();}
            await Task.Delay(40);
        }
    }

    async Task EffectFrames(string jobsPath)
    {
        var log=new List<string>();int exit=0;
        string logPath=Path.ChangeExtension(Path.GetFullPath(jobsPath),".log.txt");
        string backgroundBefore=Config_App.Current.Setting_BackColorName;int fadeBefore=Config_App.Current.Setting_BackFade;
        var total=Stopwatch.StartNew();
        log.Add(FormattableString.Invariant($"started {(DateTime.Now-Process.GetCurrentProcess().StartTime).TotalSeconds:0.0} s after the process"));
        try{
            var file=JsonSerializer.Deserialize<EffectFramesFile>(File.ReadAllText(jobsPath),new JsonSerializerOptions{PropertyNameCaseInsensitive=true,ReadCommentHandling=JsonCommentHandling.Skip,AllowTrailingCommas=true})
                ??throw new InvalidDataException("empty jobs file");
            if(file.Jobs.Count==0)throw new InvalidDataException("the jobs file lists no jobs");
            var size=(file.Window??"1500x950").Split('x');Size=new Size(int.Parse(size[0],CultureInfo.InvariantCulture),int.Parse(size[1],CultureInfo.InvariantCulture));
            if(file.Pbr){truePbr.Checked=true;viewport.TruePbr=true;}
            if(file.Budget is int budget&&budget>0){particleBudget.Value=Math.Clamp(budget,0,65535);ParticleNif.PreviewBudget=(int)particleBudget.Value;}
            if(file.Envmap is {} envmap)viewport.PbrEnvironmentCubemap=Assets.Normalize(envmap);
            if(file.EnvIntensity is float envIntensity)viewport.PbrEnvironmentIntensity=envIntensity;
            if(file.Background is {} background){
                if(!Color.FromName(background).IsKnownColor)throw new ArgumentException("Background "+background+" is not a known colour name");
                Config_App.Current.Setting_BackColorName=background;Config_App.Current.Setting_BackFade=0;
            }
            scene.ShowFloor=file.Floor;if(file.FloorSize is float floorSize&&floorSize>0)scene.FloorSize=floorSize;
            if(file.FarPlane is float far&&far>0){scene.MinimumFarPlane=far;viewport.FarPlaneExtension=far;}

            // One mount for every job: the folder alone, no plugin records, no animation library.
            string data=file.Data??EffectDataRoot(file.Jobs[0].Nif)??throw new InvalidDataException("no Data folder given and none found above "+file.Jobs[0].Nif);
            var mountWatch=Stopwatch.StartNew();
            await assets.Mount(data,false,new Progress<string>(_=>{}),rebuildClips:false);
            if(file.Base is {} baseFolder){
                if(!Directory.Exists(baseFolder))throw new DirectoryNotFoundException("Base folder not found: "+baseFolder);
                var (archives,files)=await Task.Run(()=>assets.AddFallbackArchives(baseFolder));
                log.Add($"base: {files:N0} files from {archives} archive(s) in {baseFolder}");
            }
            log.Add(FormattableString.Invariant($"mounted {data} in {mountWatch.Elapsed.TotalSeconds:0.0} s"));

            foreach(var job in file.Jobs){
                var jobWatch=Stopwatch.StartNew();var jobLog=new List<string>();string name=Path.GetFileName(job.Nif);
                try{
                    if(!File.Exists(job.Nif))throw new FileNotFoundException("NIF not found: "+job.Nif);
                    if(string.IsNullOrWhiteSpace(job.Out))throw new InvalidDataException(name+": no Out folder");
                    Directory.CreateDirectory(job.Out);
                    foreach(string old in Directory.GetFiles(job.Out,"f????.png"))File.Delete(old);
                    var times=job.Times??(job.Count is int count&&count>0?Enumerable.Range(0,count).Select(i=>job.Start+i/(job.Fps??30)).ToList():null)
                        ??throw new InvalidDataException(name+": give Times, or Start, Fps and Count");
                    var extras=(job.Extra??[]).Select(e=>(e.Nif,e.Offset)).ToList();
                    // No 10 s cap: each NIF (and Extra) shows for as long as its own controllers run. With the cap, a looping
                    // Extra vanished 10 s after it spawned.
                    PreviewParticle(job.Nif,double.PositiveInfinity,extras);clock.Pause();followCamera.Checked=false;scene.FollowScene=false;
                    double maxClock=job.MaxClock is double m&&m>0?m:60;
                    if(clock.Duration<.1||clock.Duration>maxClock)throw new InvalidDataException(name+": effect clock "+clock.Duration.ToString("F3",CultureInfo.InvariantCulture)+" s is outside 0.1-"+maxClock.ToString("0.###",CultureInfo.InvariantCulture)+" s, so the authored duration was not read (set MaxClock if the sequence really runs that long)");
                    // Camera: framed on the effect as the window frames it, then the job's angles, focus and distance.
                    if(job.Yaw!=null||job.Pitch!=null){var focus=viewport.camera.FocusPosition;viewport.camera=new OrbitCamera{MaxDistance=1000000};viewport.camera.Rotate(job.Yaw??45,job.Pitch??65);viewport.camera.FocusPosition=focus;}
                    viewport.camera.MaxDistance=Math.Max(viewport.camera.MaxDistance,1000000);
                    if(job.FocusOrigin)viewport.camera.FocusPosition=new OpenTK.Mathematics.Vector3(0,0,100);
                    if(job.Focus is {Count:3} f)viewport.camera.FocusPosition=new OpenTK.Mathematics.Vector3(f[0],f[1],f[2]);
                    viewport.camera.distance=(float)(job.Distance??350);viewport.UpdateProjection(true);

                    // Frames in clock order: the simulation steps forward from one to the next instead of starting over.
                    var order=times.Select((t,i)=>(t,i)).OrderBy(x=>x.t).ToList();
                    bool first=true;
                    foreach(var (t,i) in order){
                        clock.Seek(t);scene.SetTime(t);
                        if(first){await WaitRenderReady(file.ReadyTimeout,name);first=false;}
                        Bitmap? image=viewport.CaptureBitmap();
                        // A texture that started loading during this frame (a mesh shown later in the effect) leaves the
                        // card in the image: wait, then take the frame again.
                        if(!viewport.Model.TexturesReady){image?.Dispose();await WaitRenderReady(file.ReadyTimeout,$"{name} at {t:0.###} s");image=viewport.CaptureBitmap();}
                        if(image==null)throw new InvalidOperationException(name+": the viewport has no size; make Window larger");
                        using(image){
                            if(job.Bloom is float bloom&&bloom>0)ImageBloom.Apply(image,bloom);
                            using var output=Resize(image,job.Width,job.Height);
                            output.Save(Path.Combine(job.Out,$"f{i:0000}.png"));
                        }
                        jobLog.Add(FormattableString.Invariant($"f{i:0000} t={t:0.0000} visible meshes {scene.VisibleEffectMeshes}, particles {scene.DrawnParticles}"));
                    }
                    string summary=FormattableString.Invariant($"{name}: {times.Count} frames, duration {clock.Duration:0.###} s, in {jobWatch.Elapsed.TotalSeconds:0.0} s");
                    if(job.Gif is {} gif){
                        var gifWatch=Stopwatch.StartNew();
                        WriteGif(file.Ffmpeg,job.Out,gif,job.GifFps??job.Fps??30);
                        summary+=FormattableString.Invariant($"; GIF {gif} in {gifWatch.Elapsed.TotalSeconds:0.0} s");
                    }
                    jobLog.Insert(0,summary);jobLog.AddRange(scene.Diagnostics.Take(10).Select(d=>"diagnostic: "+d));
                    log.Add("OK "+summary);
                }catch(NotReadyException ex){log.Add("NOT READY "+ex.Message);jobLog.Insert(0,"NOT READY "+ex.Message);exit=exit==0?3:exit;}
                catch(Exception ex){log.Add("FAIL "+name+": "+ex.Message);jobLog.Insert(0,"FAIL "+ex);exit=1;}
                if(Directory.Exists(job.Out))File.WriteAllLines(Path.Combine(job.Out,"frames.txt"),jobLog);
            }
        }catch(Exception ex){log.Add("FAIL "+ex);exit=1;}
        finally{
            Config_App.Current.Setting_BackColorName=backgroundBefore;Config_App.Current.Setting_BackFade=fadeBefore;
            log.Add(FormattableString.Invariant($"total {total.Elapsed.TotalSeconds:0.0} s, exit code {exit}"));
            Environment.ExitCode=exit;
            try{File.WriteAllLines(logPath,log);}finally{Close();}
        }
    }

    /// <summary>Crops to the requested aspect around the centre and scales to the requested size; either alone keeps
    /// the aspect. With neither, the image is copied as is.</summary>
    static Bitmap Resize(Bitmap source,int? width,int? height)
    {
        if(width==null&&height==null)return new Bitmap(source);
        int w=width??(int)Math.Round(source.Width*(height!.Value/(double)source.Height));
        int h=height??(int)Math.Round(source.Height*(width!.Value/(double)source.Width));
        double aspect=w/(double)h;var crop=new Rectangle(0,0,source.Width,source.Height);
        if(source.Width/(double)source.Height>aspect){int cw=(int)Math.Round(source.Height*aspect);crop=new((source.Width-cw)/2,0,cw,source.Height);}
        else{int ch=(int)Math.Round(source.Width/aspect);crop=new(0,(source.Height-ch)/2,source.Width,ch);}
        var result=new Bitmap(w,h);
        using var g=Graphics.FromImage(result);g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
        g.DrawImage(source,new Rectangle(0,0,w,h),crop,GraphicsUnit.Pixel);
        return result;
    }

    /// <summary>The GIF encoding: palettegen reserve_transparent=0, paletteuse diff_mode=none, -gifflags -transdiff,
    /// looping, and byte 12 of the file (the pixel aspect ratio) set to 0.</summary>
    static void WriteGif(string? ffmpeg,string folder,string gif,double fps)
    {
        string exe=ffmpeg??Environment.GetEnvironmentVariable("SSE_FFMPEG")??"ffmpeg";
        string full=Path.GetFullPath(gif);Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var start=new ProcessStartInfo(exe){UseShellExecute=false,RedirectStandardError=true,CreateNoWindow=true,WorkingDirectory=folder};
        foreach(string a in new[]{"-y","-loglevel","error","-framerate",fps.ToString("0.###",CultureInfo.InvariantCulture),"-i","f%04d.png",
            "-vf","split[a][b];[a]palettegen=max_colors=256:reserve_transparent=0[p];[b][p]paletteuse=dither=sierra2_4a:diff_mode=none",
            "-gifflags","-transdiff","-loop","0",full})start.ArgumentList.Add(a);
        using var process=Process.Start(start)??throw new InvalidOperationException("could not start "+exe);
        string errors=process.StandardError.ReadToEnd();process.WaitForExit();
        if(process.ExitCode!=0)throw new InvalidOperationException($"ffmpeg exit code {process.ExitCode}: {errors.Trim()}");
        using var stream=new FileStream(full,FileMode.Open,FileAccess.ReadWrite);
        if(stream.Length>12){stream.Position=12;stream.WriteByte(0);}
    }
}
