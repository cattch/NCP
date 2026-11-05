using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Ncp
{
    public static class SharePackages
    {
        const long MaximumBytes=128L*1024*1024;
        [DataContract]
        sealed class Manifest
        {
            [DataMember(IsRequired=true)] public string Kind;
            [DataMember(IsRequired=true)] public int Version=1;
            [DataMember(IsRequired=true)] public string Name;
            [DataMember(IsRequired=true)] public List<Preset> Presets;
            [DataMember] public List<string> Order;
        }
        public static void ExportPreset(string path,Preset preset,IccLibrary library)
        {
            preset.Validate();Write(path,new Manifest {Kind="NCP.Preset",Name=preset.Name,Presets=new List<Preset>{preset},Order=new List<string>{preset.Id}},library);
        }
        public static void ExportProfile(string path,PresetProfile profile,IccLibrary library)
        {
            profile.Validate();Write(path,new Manifest {Kind="NCP.Profile",Name=profile.Name,Presets=profile.Presets,Order=profile.Order},library);
        }
        static string EntryName(IccProfileRef profile) {return "icc/NCP-"+profile.Hash+".icc";}
        static void Write(string path,Manifest manifest,IccLibrary library)
        {
            string full=Path.GetFullPath(path);string directory=Path.GetDirectoryName(full);Directory.CreateDirectory(directory);
            string temporary=full+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {
                using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                using(var archive=new ZipArchive(stream,ZipArchiveMode.Create)) {
                    using(var entry=archive.CreateEntry("manifest.json",CompressionLevel.Optimal).Open())
                        new DataContractJsonSerializer(typeof(Manifest)).WriteObject(entry,manifest);
                    long total=0;
                    foreach(var profile in manifest.Presets.Where(p=>p.Icc!=null).Select(p=>p.Icc).GroupBy(i=>i.Hash).Select(g=>g.First())) {
                        var bytes=library.Read(profile);total+=bytes.Length;
                        if(total>MaximumBytes)throw new InvalidDataException("프로필에 포함된 ICC 파일 합계가 128MB를 초과합니다.");
                        using(var entry=archive.CreateEntry(EntryName(profile),CompressionLevel.Optimal).Open())entry.Write(bytes,0,bytes.Length);
                    }
                }
                if(File.Exists(full))File.Replace(temporary,full,full+".bak");else File.Move(temporary,full);
            } finally {if(File.Exists(temporary))File.Delete(temporary);}
        }
        static byte[] ReadEntry(ZipArchiveEntry entry,int maximum)
        {
            if(entry==null || entry.Length<=0 || entry.Length>maximum)throw new InvalidDataException("공유 파일 안의 데이터 크기가 올바르지 않습니다.");
            using(var stream=entry.Open())using(var memory=new MemoryStream()) {
                var buffer=new byte[8192];int count;
                while((count=stream.Read(buffer,0,buffer.Length))>0) {
                    if(memory.Length+count>maximum)throw new InvalidDataException("공유 파일의 압축 해제 크기가 너무 큽니다.");
                    memory.Write(buffer,0,count);
                }
                if(memory.Length!=entry.Length)throw new InvalidDataException("공유 파일이 손상되었습니다.");return memory.ToArray();
            }
        }
        static Manifest Read(string path,string kind,IccLibrary library)
        {
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))return Read(stream,kind,library);
        }
        static Manifest Read(Stream stream,string kind,IccLibrary library)
        {
            if(stream.Length>MaximumBytes)throw new InvalidDataException("공유 파일은 최대 128MB까지 지원합니다.");
            using(var archive=new ZipArchive(stream,ZipArchiveMode.Read,true)) {
                if(archive.Entries.Count<1 || archive.Entries.Count>201)throw new InvalidDataException("공유 파일의 항목 수가 올바르지 않습니다.");
                if(archive.Entries.Select(e=>e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=archive.Entries.Count)
                    throw new InvalidDataException("공유 파일 안에 중복 경로가 있습니다.");
                Manifest manifest;
                using(var memory=new MemoryStream(ReadEntry(archive.GetEntry("manifest.json"),1024*1024)))
                    manifest=(Manifest)new DataContractJsonSerializer(typeof(Manifest)).ReadObject(memory);
                if(manifest==null || manifest.Kind!=kind || manifest.Version!=1 || manifest.Presets==null)
                    throw new InvalidDataException("지원하지 않는 NCP 공유 파일입니다.");
                var profile=new PresetProfile {Id="package",Name=manifest.Name,Presets=manifest.Presets,Order=manifest.Order};
                // A single default preset can be exported; profile collections retain the receiver's own default.
                if(kind=="NCP.Preset") {
                    if(manifest.Presets.Count!=1)throw new InvalidDataException("프리셋 공유 파일에 여러 프리셋이 들어 있습니다.");
                    manifest.Presets[0].Validate();
                } else profile.Validate();
                var references=manifest.Presets.Where(p=>p.Icc!=null).Select(p=>p.Icc).GroupBy(i=>i.Hash).Select(g=>g.First()).ToList();
                var expected=new HashSet<string>(references.Select(EntryName),StringComparer.Ordinal);expected.Add("manifest.json");
                if(archive.Entries.Any(e=>!expected.Contains(e.FullName)) || archive.Entries.Count!=expected.Count)
                    throw new InvalidDataException("공유 파일의 ICC 목록이 일치하지 않습니다.");
                long total=references.Sum(i=>archive.GetEntry(EntryName(i)).Length);
                if(total>MaximumBytes)throw new InvalidDataException("ICC 압축 해제 크기 합계가 너무 큽니다.");
                var data=new Dictionary<string,byte[]>();
                foreach(var reference in references) {
                    var bytes=ReadEntry(archive.GetEntry(EntryName(reference)),IccLibrary.MaximumBytes);IccLibrary.ValidateBytes(bytes);
                    if(IccLibrary.Hash(bytes)!=reference.Hash)throw new InvalidDataException("공유 파일의 ICC 해시가 일치하지 않습니다.");
                    data.Add(reference.Hash,bytes);
                }
                foreach(var reference in references)library.Import(reference.Name,data[reference.Hash]);
                return manifest;
            }
        }
        public static Preset ImportPreset(string path,IccLibrary library) {return Read(path,"NCP.Preset",library).Presets[0];}
        public static PresetProfile ImportProfile(string path,IccLibrary library)
        {
            return ToProfile(Read(path,"NCP.Profile",library));
        }
        internal static PresetProfile ImportProfile(Stream stream,IccLibrary library)
        {
            return ToProfile(Read(stream,"NCP.Profile",library));
        }
        static PresetProfile ToProfile(Manifest manifest)
        {
            return new PresetProfile {Id=Guid.NewGuid().ToString("N"),Name=manifest.Name,Presets=manifest.Presets,Order=manifest.Order??new List<string>()};
        }
    }
}
