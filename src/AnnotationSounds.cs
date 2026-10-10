using System.Runtime.InteropServices;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SSEPreview;

// Sounds named by a clip's SoundPlay.<EditorID> annotations, resolved through the loaded plugins the way the game's
// BSAudioManager does: a sound descriptor (SNDR) with that editor ID, or a sound marker (SOUN) whose SDSC names one.
public sealed record SoundEvent(double Time,string EditorId,string Text);

public sealed class SoundInfo
{
    public required string EditorId;
    public GameRecord? Descriptor,Marker;
    public List<string> Files=[],MissingFiles=[];
    // BNAM: static attenuation in dB, random extra attenuation up to DbVariance dB, pitch shift and random variance in %.
    public double StaticAttenuationDb,DbVariance,FrequencyShift,FrequencyVariance;
    // Product of the category chain's static volume multipliers and default menu values.
    public double CategoryVolume=1;
    public List<string> Categories=[];
    public bool Looping,Conditions;
    public List<string> Problems=[];
    public double Gain=>Math.Pow(10,-StaticAttenuationDb/20)*CategoryVolume;
    public bool Playable=>Files.Count>0;
    public string Summary()
    {
        if(Descriptor==null)return "not found in the loaded plugins";
        var parts=new List<string>{$"{Descriptor.EditorId} [{Descriptor.Plugin}]"};
        if(Marker!=null)parts.Insert(0,$"marker {Marker.EditorId} [{Marker.Plugin}] ->");
        parts.Add($"{Files.Count} file(s)"+(MissingFiles.Count>0?$", {MissingFiles.Count} missing":""));
        parts.Add(FormattableString.Invariant($"static attenuation {StaticAttenuationDb:0.##} dB")+(DbVariance>0?FormattableString.Invariant($" (+0..{DbVariance:0} dB random)"):""));
        if(Categories.Count>0)parts.Add(FormattableString.Invariant($"category {string.Join(" < ",Categories)} x{CategoryVolume:0.###}"));
        parts.Add(FormattableString.Invariant($"gain {Gain:0.###}"));
        if(FrequencyShift!=0||FrequencyVariance!=0)parts.Add(FormattableString.Invariant($"pitch {FrequencyShift:+0;-0;0}% ±{FrequencyVariance:0}%"));
        if(Looping)parts.Add("looping in game, plays once here");
        if(Conditions)parts.Add("has conditions, assumed to pass");
        return string.Join(", ",parts);
    }
}

public sealed class SoundLibrary
{
    readonly Records records;readonly Assets assets;
    readonly Dictionary<string,GameRecord> descriptors=new(StringComparer.OrdinalIgnoreCase),markers=new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> loadedPlugins=new(StringComparer.OrdinalIgnoreCase);
    public long RecordsRevision {get;}
    public long AssetsRevision {get;}
    public SoundLibrary(Records records,Assets assets)
    {
        this.records=records;this.assets=assets;RecordsRevision=records.Revision;AssetsRevision=assets.Revision;
        foreach(var r in records.All.Values){
            loadedPlugins.Add(r.Plugin);
            if(r.Kind=="SNDR")descriptors[r.EditorId]=r;else if(r.Kind=="SOUN")markers[r.EditorId]=r;
        }
    }
    public static List<SoundEvent> Events(IEnumerable<Annotation> events)
    {
        var list=new List<SoundEvent>();
        foreach(var e in events){
            string text=e.Text.Trim();int dot=text.IndexOf('.');
            if(dot<=0||!text[..dot].Equals("SoundPlay",StringComparison.OrdinalIgnoreCase))continue;
            string id=text[(dot+1)..].Trim();if(id.Length>0)list.Add(new(e.Time,id,text));
        }
        return list;
    }
    readonly Dictionary<string,SoundInfo> resolved=new(StringComparer.OrdinalIgnoreCase);
    public SoundInfo Resolve(string editorId)
    {
        if(resolved.TryGetValue(editorId,out var cached))return cached;
        var info=new SoundInfo{EditorId=editorId};
        if(!descriptors.TryGetValue(editorId,out var descriptor)&&markers.TryGetValue(editorId,out var marker)){
            info.Marker=marker;uint id=marker.First("SDSC")?.UInt()??0;
            descriptor=records.Resolve(marker,id);
            if(descriptor==null&&id!=0)info.Problems.Add(MissingReference(marker,id,"sound descriptor"));
        }
        info.Descriptor=descriptor?.Kind=="SNDR"?descriptor:null;
        if(info.Descriptor is {} d){
            foreach(var f in d.All("ANAM")){
                // ANAM paths are relative to Data\Sound; some plugins write them from Data.
                string path=Assets.Normalize(f.Text);if(path.StartsWith(@"data\",StringComparison.OrdinalIgnoreCase))path=path[5..];if(path.Length==0)continue;
                if(!path.StartsWith(@"sound\",StringComparison.OrdinalIgnoreCase))path=@"sound\"+path;
                // The game plays the .xwm or .fuz of the same name when the listed file is not there.
                string? found=new[]{path,Path.ChangeExtension(path,".xwm"),Path.ChangeExtension(path,".fuz"),Path.ChangeExtension(path,".wav")}.FirstOrDefault(assets.Exists);
                if(found!=null)info.Files.Add(found);else info.MissingFiles.Add(path);
            }
            if(d.First("BNAM") is {Bytes.Length:>=6} b){
                info.FrequencyShift=(sbyte)b.Bytes[0];info.FrequencyVariance=(sbyte)b.Bytes[1];info.DbVariance=b.Bytes[3];
                info.StaticAttenuationDb=BitConverter.ToUInt16(b.Bytes,4)/100.0;
            }
            if(d.First("LNAM") is {Bytes.Length:>=2} l)info.Looping=(l.Bytes[1]&0x38)!=0;
            info.Conditions=d.All("CTDA").Any();
            uint category=d.First("GNAM")?.UInt()??0;var seen=new HashSet<string>();
            for(var owner=d;category!=0;){
                var c=records.Resolve(owner,category);
                if(c==null){info.Problems.Add(MissingReference(owner,category,"sound category"));break;}
                if(!seen.Add(c.Key))break;
                info.Categories.Add(c.EditorId);
                double vnam=(c.First("VNAM") is {Bytes.Length:>=2} v)?BitConverter.ToUInt16(v.Bytes,0)/65535.0:1;
                double unam=(c.First("UNAM") is {Bytes.Length:>=2} u)?BitConverter.ToUInt16(u.Bytes,0)/65535.0:1;
                info.CategoryVolume*=vnam*unam;
                owner=c;category=c.First("PNAM")?.UInt()??0;
            }
            foreach(string missing in info.MissingFiles)info.Problems.Add($"{d.EditorId} [{d.Plugin}] lists {missing}, which is not in the mounted folders or their archives.");
            if(d.All("ANAM").Any()==false)info.Problems.Add($"{d.EditorId} [{d.Plugin}] lists no sound files.");
        }else if(info.Marker==null)info.Problems.Add($"No sound descriptor (SNDR) or sound marker (SOUN) has the editor ID {editorId} in the loaded plugins.");
        return resolved[editorId]=info;
    }
    string MissingReference(GameRecord owner,uint id,string what)
    {
        string plugin=(id>>24)<owner.Masters.Count?owner.Masters[(int)(id>>24)]:owner.Plugin;
        return loadedPlugins.Contains(plugin)
            ?$"{owner.EditorId} [{owner.Plugin}] points to a {what} ({plugin} {id&0xFFFFFF:X6}) that does not exist."
            :$"{owner.EditorId} [{owner.Plugin}] needs the {what} {id&0xFFFFFF:X6} from {plugin}, which is not in the loaded mod folders.";
    }
    // Searches every plugin in the folders next to the mounted mod folders (an MO2 mods directory) for these editor
    // IDs, so a warning can name the mod to add. Plugins that are already loaded are skipped.
    public Dictionary<string,List<string>> FindInUnloadedPlugins(IEnumerable<string> editorIds,CancellationToken cancel=default)
    {
        var wanted=editorIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var found=wanted.ToDictionary(x=>x,_=>new List<string>(),StringComparer.OrdinalIgnoreCase);
        if(wanted.Count==0)return found;
        var patterns=wanted.Select(id=>(Id:id,Bytes:System.Text.Encoding.ASCII.GetBytes(id.ToLowerInvariant()+"\0"))).ToList();
        var roots=assets.Roots.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parents=roots.Select(Path.GetDirectoryName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach(string parent in parents){
            IEnumerable<string> folders;try{folders=Directory.EnumerateDirectories(parent).ToList();}catch(Exception){continue;}
            foreach(string folder in folders){
                if(roots.Contains(folder))continue;
                IEnumerable<string> plugins;try{plugins=Directory.EnumerateFiles(folder).Where(p=>Path.GetExtension(p).ToLowerInvariant() is ".esp" or ".esm" or ".esl").ToList();}catch(Exception){continue;}
                foreach(string plugin in plugins){
                    cancel.ThrowIfCancellationRequested();
                    byte[] bytes;try{if(new FileInfo(plugin).Length>512L*1024*1024)continue;bytes=File.ReadAllBytes(plugin);}catch(Exception){continue;}
                    var span=bytes.AsSpan();int at=0;
                    while(true){
                        int next=span[at..].IndexOf("EDID"u8);if(next<0)break;at+=next+4;
                        if(at+2>span.Length)break;int length=BitConverter.ToUInt16(bytes,at);
                        foreach(var (id,pattern) in patterns){
                            if(length!=pattern.Length||at+2+length>span.Length)continue;
                            bool same=true;for(int i=0;i<length&&same;i++){byte c=bytes[at+2+i];if(c>='A'&&c<='Z')c+=32;same=c==pattern[i];}
                            if(same){string where=$"{Path.GetFileName(plugin)} in the mod folder {Path.GetFileName(folder)}";if(!found[id].Contains(where))found[id].Add(where);}
                        }
                    }
                }
            }
        }
        return found;
    }
}

// Decoded sound files and the mixer that plays them. Output is 44.1 kHz stereo; mono files play centred.
public sealed class SoundPlayer:IDisposable
{
    public static readonly WaveFormat MixFormat=WaveFormat.CreateIeeeFloatWaveFormat(44100,2);
    readonly MixingSampleProvider mixer=new(MixFormat){ReadFully=true};
    WaveOutEvent? output;
    readonly Dictionary<string,(float[] Samples,WaveFormat Format)> decoded=new(StringComparer.OrdinalIgnoreCase);
    readonly Random random=new();
    public readonly List<string> Problems=[];
    // What was started, for the Annotations tab and the tests.
    public readonly List<(double Time,string EditorId,string File,double Gain,double Pitch)> Played=[];
    // Tests read the mix offline instead of opening a device.
    public bool Offline;
    public int Voices=>mixer.MixerInputs.Count();
    public void Play(SoundInfo info,Assets assets,double sceneTime)
    {
        if(!info.Playable)return;
        string file=info.Files[random.Next(info.Files.Count)];
        (float[] Samples,WaveFormat Format) sound;
        try{sound=Decode(assets,file);}
        catch(Exception ex){string problem=$"{file}: could not decode ({ex.Message})";if(!Problems.Contains(problem))Problems.Add(problem);return;}
        double gain=info.Gain*Math.Pow(10,-random.NextDouble()*info.DbVariance/20);
        double pitch=Math.Max(.1,1+(info.FrequencyShift+(random.NextDouble()*2-1)*info.FrequencyVariance)/100);
        if(mixer.MixerInputs.Count()>=48)return;
        mixer.AddMixerInput(Voice(sound.Samples,sound.Format,(float)gain,pitch));
        Played.Add((sceneTime,info.EditorId,file,gain,pitch));
        if(!Offline)EnsureOutput();
    }
    public void StopAll(){mixer.RemoveAllMixerInputs();}
    void EnsureOutput()
    {
        if(output!=null)return;
        try{output=new WaveOutEvent{DesiredLatency=90};output.Init(mixer);output.Play();}
        catch(Exception ex){output=null;string problem="No audio output device: "+ex.Message;if(!Problems.Contains(problem))Problems.Add(problem);}
    }
    public int Render(float[] buffer)=>mixer.Read(buffer,0,buffer.Length);
    static ISampleProvider Voice(float[] samples,WaveFormat format,float gain,double pitch)
    {
        // Pitch: the samples are presented at rate x pitch and resampled to the mix rate.
        int rate=(int)Math.Round(format.SampleRate*pitch);
        ISampleProvider source=new BufferSource(samples,WaveFormat.CreateIeeeFloatWaveFormat(rate,format.Channels));
        if(rate!=MixFormat.SampleRate)source=new WdlResamplingSampleProvider(source,MixFormat.SampleRate);
        if(format.Channels==1)source=new MonoToStereoSampleProvider(source);
        return new VolumeSampleProvider(source){Volume=gain};
    }
    sealed class BufferSource(float[] samples,WaveFormat format):ISampleProvider
    {
        int at;public WaveFormat WaveFormat=>format;
        public int Read(float[] buffer,int offset,int count){int n=Math.Min(count,samples.Length-at);if(n<=0)return 0;Array.Copy(samples,at,buffer,offset,n);at+=n;return n;}
    }
    public (float[] Samples,WaveFormat Format) Decode(Assets assets,string path)
    {
        if(decoded.TryGetValue(path,out var cached))return cached;
        byte[] bytes=assets.Read(path);
        // .fuz: FUZE header, lip data, then the xWMA audio.
        if(bytes.Length>12&&bytes.AsSpan(0,4).SequenceEqual("FUZE"u8)){int lip=BitConverter.ToInt32(bytes,8);bytes=bytes[(12+lip)..];}
        if(bytes.Length<12||!bytes.AsSpan(0,4).SequenceEqual("RIFF"u8))throw new InvalidDataException("not a RIFF file");
        (float[] Samples,WaveFormat Format) result=bytes.AsSpan(8,4).SequenceEqual("XWMA"u8)?Xwma.Decode(bytes):Wav(bytes);
        if(result.Format.Channels>2)result=Downmix(result);
        return decoded[path]=result;
    }
    static (float[],WaveFormat) Wav(byte[] bytes)
    {
        using var reader=new WaveFileReader(new MemoryStream(bytes));
        WaveStream stream=reader.WaveFormat.Encoding is WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat?reader:WaveFormatConversionStream.CreatePcmStream(reader);
        var provider=stream.ToSampleProvider();var list=new List<float>();var buffer=new float[provider.WaveFormat.SampleRate*provider.WaveFormat.Channels];int n;
        while((n=provider.Read(buffer,0,buffer.Length))>0)list.AddRange(buffer.AsSpan(0,n).ToArray());
        return (list.ToArray(),WaveFormat.CreateIeeeFloatWaveFormat(provider.WaveFormat.SampleRate,provider.WaveFormat.Channels));
    }
    static (float[],WaveFormat) Downmix((float[] Samples,WaveFormat Format) s)
    {
        int ch=s.Format.Channels;var mono=new float[s.Samples.Length/ch];
        for(int i=0;i<mono.Length;i++){float sum=0;for(int c=0;c<ch;c++)sum+=s.Samples[i*ch+c];mono[i]=sum/ch;}
        return (mono,WaveFormat.CreateIeeeFloatWaveFormat(s.Format.SampleRate,1));
    }
    public void Dispose(){output?.Dispose();output=null;}
}

// xWMA (Skyrim's .xwm): WMA packets in a RIFF container. Media Foundation cannot open the container, so the packets go
// straight into Windows' WMA decoder. The decoder needs the WMAv2 encoder options the container leaves out; 0x1F
// (as ffmpeg's xWMA reader assumes) decodes bit-identically to ffmpeg.
static class Xwma
{
    static bool started;
    public static (float[],WaveFormat) Decode(byte[] bytes)
    {
        if(!started){MediaFoundationApi.Startup();started=true;}
        int fmtAt=-1,dataAt=-1,dataLength=0;
        for(int p=12;p+8<=bytes.Length;){string id=System.Text.Encoding.ASCII.GetString(bytes,p,4);int len=BitConverter.ToInt32(bytes,p+4);if(id=="fmt ")fmtAt=p+8;else if(id=="data"){dataAt=p+8;dataLength=Math.Min(len,bytes.Length-dataAt);}p+=8+len+(len&1);}
        if(fmtAt<0||dataAt<0)throw new InvalidDataException("xWMA without fmt or data chunk");
        ushort tag=BitConverter.ToUInt16(bytes,fmtAt);int channels=BitConverter.ToInt16(bytes,fmtAt+2),rate=BitConverter.ToInt32(bytes,fmtAt+4),average=BitConverter.ToInt32(bytes,fmtAt+8),align=BitConverter.ToInt16(bytes,fmtAt+12);
        if(align<=0)throw new InvalidDataException("xWMA block align is 0");
        var transform=(IMFTransform)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("2eeb4adf-4578-4d10-bca7-bb955f56320a"))!)!;
        try{
            var input=MediaFoundationApi.CreateMediaType();
            input.SetGUID(MediaFoundationAttributes.MF_MT_MAJOR_TYPE,MediaTypes.MFMediaType_Audio);
            input.SetGUID(MediaFoundationAttributes.MF_MT_SUBTYPE,new Guid($"{tag:x8}-0000-0010-8000-00aa00389b71"));
            input.SetUINT32(MediaFoundationAttributes.MF_MT_AUDIO_NUM_CHANNELS,channels);input.SetUINT32(MediaFoundationAttributes.MF_MT_AUDIO_SAMPLES_PER_SECOND,rate);
            input.SetUINT32(MediaFoundationAttributes.MF_MT_AUDIO_AVG_BYTES_PER_SECOND,average);input.SetUINT32(MediaFoundationAttributes.MF_MT_AUDIO_BLOCK_ALIGNMENT,align);
            input.SetUINT32(MediaFoundationAttributes.MF_MT_AUDIO_BITS_PER_SAMPLE,16);
            // WMAUDIO2WAVEFORMAT extras: samples per block, encode options, super block align.
            var extra=new byte[10];BitConverter.GetBytes(8192u).CopyTo(extra,0);BitConverter.GetBytes((ushort)0x1F).CopyTo(extra,4);BitConverter.GetBytes((uint)align).CopyTo(extra,6);
            input.SetBlob(MediaFoundationAttributes.MF_MT_USER_DATA,extra,extra.Length);
            transform.SetInputType(0,input,0);
            transform.SetOutputType(0,MediaFoundationApi.CreateMediaTypeFromWaveFormat(new WaveFormat(rate,16,channels)),0);
            transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING,IntPtr.Zero);
            var pcm=new MemoryStream();
            void Drain(){
                while(true){
                    var sample=MediaFoundationApi.CreateSample();var buffer=MediaFoundationApi.CreateMemoryBuffer(rate*channels*2);sample.AddBuffer(buffer);
                    var outputs=new MFT_OUTPUT_DATA_BUFFER[1];outputs[0].pSample=sample;
                    try{
                        int hr=transform.ProcessOutput(_MFT_PROCESS_OUTPUT_FLAGS.None,1,outputs,out _);
                        if(hr==unchecked((int)0xC00D6D72))return; // MF_E_TRANSFORM_NEED_MORE_INPUT
                        if(hr!=0)Marshal.ThrowExceptionForHR(hr);
                        sample.ConvertToContiguousBuffer(out var contiguous);
                        try{contiguous.Lock(out var pointer,out _,out int length);var data=new byte[length];Marshal.Copy(pointer,data,0,length);contiguous.Unlock();pcm.Write(data);}
                        finally{Marshal.ReleaseComObject(contiguous);}
                    }finally{Marshal.ReleaseComObject(buffer);Marshal.ReleaseComObject(sample);}
                }
            }
            long time=0;
            for(int k=0;k+align<=dataLength;k+=align){
                var buffer=MediaFoundationApi.CreateMemoryBuffer(align);buffer.Lock(out var pointer,out _,out _);Marshal.Copy(bytes,dataAt+k,pointer,align);buffer.Unlock();buffer.SetCurrentLength(align);
                var sample=MediaFoundationApi.CreateSample();sample.AddBuffer(buffer);sample.SetSampleTime(time);time+=10_000_000L*align/Math.Max(1,average);
                try{transform.ProcessInput(0,sample,0);Drain();}finally{Marshal.ReleaseComObject(buffer);Marshal.ReleaseComObject(sample);}
            }
            transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_DRAIN,IntPtr.Zero);Drain();
            var raw=pcm.ToArray();var samples=new float[raw.Length/2];
            for(int i=0;i<samples.Length;i++)samples[i]=BitConverter.ToInt16(raw,i*2)/32768f;
            return (samples,WaveFormat.CreateIeeeFloatWaveFormat(rate,channels));
        }finally{Marshal.ReleaseComObject(transform);}
    }
}
