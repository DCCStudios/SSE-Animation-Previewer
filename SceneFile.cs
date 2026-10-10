using System.Text;
namespace SSEPreview;
internal static class SceneFile
{
    // Write alongside the destination so the final replacement stays on the same volume.
    // An interrupted/failed write never truncates the previous scene.
    public static void Save(string path,string json){
        path=Path.GetFullPath(path);
        string temporary=Path.Combine(Path.GetDirectoryName(path)!,"."+Path.GetFileName(path)+"."+Guid.NewGuid().ToString("N")+".tmp");
        try{
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
                byte[] bytes=new UTF8Encoding(false).GetBytes(json);stream.Write(bytes);stream.Flush(true);
            }
            for(int attempt=0;;attempt++){
                try{if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);break;}
                catch(IOException ex) when(attempt<3&&(ex.HResult&0xffff) is 32 or 33 or 1175){Thread.Sleep(30*(attempt+1));}
            }
        }finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
