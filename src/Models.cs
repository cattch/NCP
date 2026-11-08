using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Ncp
{
    [DataContract]
    public sealed class IccProfileRef
    {
        [DataMember(IsRequired=true)] public string Hash;
        [DataMember(IsRequired=true)] public string Name;
        public void Validate()
        {
            if(Hash==null || Hash.Length!=64 || Hash.Any(c=>!(c>='0'&&c<='9')&&!(c>='a'&&c<='f')))
                throw new InvalidDataException("ICC 파일 식별자가 올바르지 않습니다.");
            if(string.IsNullOrWhiteSpace(Name) || Name.Length>255 || Name.IndexOfAny(Path.GetInvalidFileNameChars())>=0)
                throw new InvalidDataException("ICC 파일 이름이 올바르지 않습니다.");
        }
        public bool Same(IccProfileRef other) {return other!=null && Hash==other.Hash;}
        public static bool Equal(IccProfileRef a,IccProfileRef b) {return a==null?b==null:a.Same(b);}
    }
    [DataContract]
    public sealed class IccState
    {
        [DataMember] public bool PerUser;
        [DataMember] public string ProfileName;
        [DataMember(EmitDefaultValue=false)] public List<string> Profiles;
        public string Fingerprint {get {return (PerUser?"1:":"0:")+(ProfileName??"").ToUpperInvariant();}}
        public void Validate()
        {
            if(ProfileName!=null && (ProfileName.Length>255 || string.IsNullOrWhiteSpace(ProfileName) || ProfileName.IndexOfAny(Path.GetInvalidFileNameChars())>=0))
                throw new InvalidDataException("Windows ICC 복원 정보가 올바르지 않습니다.");
            if(Profiles!=null && (Profiles.Count>2048 || Profiles.Any(p=>string.IsNullOrWhiteSpace(p) || p.Length>255 || p.IndexOfAny(Path.GetInvalidFileNameChars())>=0)))
                throw new InvalidDataException("Windows ICC 연결 목록이 올바르지 않습니다.");
        }
    }
    [DataContract]
    public sealed class IccOriginal
    {
        [DataMember(IsRequired=true)] public string DisplayKey;
        [DataMember(IsRequired=true)] public IccState State;
        [DataMember(EmitDefaultValue=false)] public List<string> ManagedProfiles=new List<string>();
    }
    [DataContract]
    public sealed class ColorValues
    {
        [DataMember(IsRequired = true)] public double Brightness = 50;
        [DataMember(IsRequired = true)] public double Contrast = 50;
        [DataMember(IsRequired = true)] public double Gamma = 1;
        [DataMember(IsRequired = true)] public int Vibrance = 50;
        [DataMember(IsRequired = true)] public int Hue;
        public ColorValues Copy() { return (ColorValues)MemberwiseClone(); }
        public void Validate()
        {
            Range(Brightness, 0, 100, "밝기"); Range(Contrast, 0, 100, "대비");
            Range(Gamma, .3, 2.8, "감마"); Range(Vibrance, 0, 100, "디지털 바이브런스"); Range(Hue, 0, 359, "색조");
        }
        private static void Range(double value, double min, double max, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < min || value > max)
                throw new InvalidDataException(name + " 값이 허용 범위를 벗어났습니다.");
        }
        public bool Same(ColorValues v)
        {
            return v != null && Math.Abs(Brightness-v.Brightness)<.00001 && Math.Abs(Contrast-v.Contrast)<.00001
                && Math.Abs(Gamma-v.Gamma)<.00001 && Vibrance==v.Vibrance && Hue==v.Hue;
        }
    }

    [DataContract]
    public sealed class Preset
    {
        [DataMember(IsRequired = true)] public string Id;
        [DataMember(IsRequired = true)] public string Name;
        [DataMember(IsRequired = true)] public ColorValues Values;
        [DataMember(EmitDefaultValue=false)] public IccProfileRef Icc;
        public static Preset Default()
        {
            return new Preset { Id="default", Name="기본값", Values=new ColorValues() };
        }
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Name) || Name.Length > 60 || Name.Any(char.IsControl))
                throw new InvalidDataException("프리셋 이름은 1~60자의 일반 문자여야 합니다.");
            if (Values == null) throw new InvalidDataException("색상 값이 없습니다.");
            Values.Validate();
            if(Icc!=null)Icc.Validate();
        }
    }

    [DataContract]
    public sealed class PresetFile
    {
        [DataMember(IsRequired = true)] public string Format = "NCP.Preset";
        [DataMember(IsRequired = true)] public int Version = 1;
        [DataMember(IsRequired = true)] public string Name;
        [DataMember(IsRequired = true)] public ColorValues Values;
        [DataMember(EmitDefaultValue=false)] public string IccName;
        [DataMember(EmitDefaultValue=false)] public string IccData;
        public Preset ToPreset()
        {
            if (Format != "NCP.Preset" || (Version != 1 && Version!=2)) throw new InvalidDataException("지원하지 않는 NCP 프리셋 형식입니다.");
            if((IccName==null)!=(IccData==null) || (Version==1 && IccData!=null))throw new InvalidDataException("ICC 공유 데이터가 올바르지 않습니다.");
            var p = new Preset { Id=Guid.NewGuid().ToString("N"), Name=Name, Values=Values };
            p.Validate(); return p;
        }
    }

    [DataContract]
    public sealed class AppliedRecord
    {
        [DataMember] public string DisplayKey;
        [DataMember] public string PresetId;
        [DataMember] public string PresetName;
        [DataMember] public ColorValues Values;
        [DataMember] public string AppliedUtc;
        [DataMember] public string PanelFingerprint;
        [DataMember(EmitDefaultValue=false)] public IccProfileRef Icc;
        [DataMember(EmitDefaultValue=false)] public string IccFingerprint;
    }

    [DataContract]
    public sealed class OriginalRecord
    {
        [DataMember] public string DisplayKey;
        [DataMember] public ColorValues Values;
        [DataMember] public double[] Channels;
    }

    [DataContract]
    public sealed class PresetProfile
    {
        [DataMember(IsRequired=true)] public string Id;
        [DataMember(IsRequired=true)] public string Name;
        [DataMember(IsRequired=true)] public List<Preset> Presets=new List<Preset>();
        [DataMember] public List<string> Order=new List<string>();
        [DataMember(EmitDefaultValue=false)] public bool DefaultPresetRemoved;
        public override string ToString() {return Name;}
        public void Validate()
        {
            if(string.IsNullOrEmpty(Id) || string.IsNullOrWhiteSpace(Name) || Name.Length>60 || Name.Any(char.IsControl)
                || Presets==null || Presets.Count>200)throw new InvalidDataException("프로필 정보가 올바르지 않습니다.");
            var ids=new HashSet<string>();
            foreach(var p in Presets) {
                if(p==null || string.IsNullOrEmpty(p.Id) || p.Id=="default" || !ids.Add(p.Id))throw new InvalidDataException("프로필의 프리셋 ID가 올바르지 않습니다.");
                p.Validate();
            }
            if(Order==null)Order=new List<string>();
            if(Order.Count>201 || Order.Any(string.IsNullOrEmpty) || Order.Distinct().Count()!=Order.Count)throw new InvalidDataException("프로필의 프리셋 순서가 올바르지 않습니다.");
        }
    }

    [DataContract]
    public sealed class AppData
    {
        [DataMember(IsRequired=true)] public int Version=1;
        [DataMember(IsRequired=true)] public List<Preset> Presets=new List<Preset>();
        [DataMember] public List<string> PresetOrder=new List<string>();
        [DataMember(EmitDefaultValue=false)] public bool DefaultPresetRemoved;
        [DataMember] public List<AppliedRecord> Applied=new List<AppliedRecord>();
        [DataMember] public List<OriginalRecord> Originals=new List<OriginalRecord>();
        [DataMember] public List<IccOriginal> IccOriginals=new List<IccOriginal>();
        [DataMember] public string SelectedDisplay;
        [DataMember] public List<PresetProfile> Profiles=new List<PresetProfile>();
        [DataMember] public string ActiveProfileId;
        public void Validate()
        {
            if(Version!=1 || Presets==null || Presets.Count>200) throw new InvalidDataException("지원하지 않는 설정 파일입니다.");
            if(Profiles==null)Profiles=new List<PresetProfile>();
            if(Profiles.Count>50)throw new InvalidDataException("프로필은 최대 50개까지 저장할 수 있습니다.");
            var profileIds=new HashSet<string>();
            foreach(var profile in Profiles) {
                if(profile==null || !profileIds.Add(profile.Id??""))throw new InvalidDataException("프로필 ID가 올바르지 않습니다.");
                profile.Validate();
            }
            var ids=new HashSet<string>();
            foreach(var p in Presets) {
                if(p==null || string.IsNullOrEmpty(p.Id) || p.Id=="default" || !ids.Add(p.Id))
                    throw new InvalidDataException("프리셋 ID가 올바르지 않습니다.");
                p.Validate();
            }
            if(PresetOrder==null)PresetOrder=new List<string>();
            if(PresetOrder.Count>201 || PresetOrder.Any(string.IsNullOrEmpty) || PresetOrder.Distinct().Count()!=PresetOrder.Count)
                throw new InvalidDataException("프리셋 순서가 올바르지 않습니다.");
            if(Applied==null) Applied=new List<AppliedRecord>();
            if(Originals==null) Originals=new List<OriginalRecord>();
            if(IccOriginals==null)IccOriginals=new List<IccOriginal>();
            if(IccOriginals.Count>100)throw new InvalidDataException("ICC 복원 기록이 너무 많습니다.");
            foreach(var original in IccOriginals) {
                if(original==null || string.IsNullOrEmpty(original.DisplayKey) || original.State==null)throw new InvalidDataException("ICC 복원 기록이 올바르지 않습니다.");
                original.State.Validate();
                if(original.ManagedProfiles==null)original.ManagedProfiles=new List<string>();
                new IccState {Profiles=original.ManagedProfiles}.Validate();
            }
            if(Applied.Count>100 || Originals.Count>100) throw new InvalidDataException("디스플레이 기록이 너무 많습니다.");
            foreach(var a in Applied) {
                if(a==null || a.Values==null || string.IsNullOrEmpty(a.DisplayKey)) throw new InvalidDataException("적용 기록이 올바르지 않습니다.");
                a.Values.Validate();
                if(a.Icc!=null)a.Icc.Validate();
            }
            foreach(var o in Originals) {
                if(o==null || o.Values==null || string.IsNullOrEmpty(o.DisplayKey) || o.Channels==null || o.Channels.Length!=9)
                    throw new InvalidDataException("원본 기록이 올바르지 않습니다.");
                o.Values.Validate();
                for(int c=0;c<3;c++) new ColorValues {Brightness=o.Channels[c],Contrast=o.Channels[3+c],Gamma=o.Channels[6+c]}.Validate();
            }
        }
    }

    public sealed class Store
    {
        public readonly string DirectoryPath;
        readonly bool initializeDefaults;
        public string FilePath { get { return Path.Combine(DirectoryPath,"settings.json"); } }
        public Store(string path,bool initializeDefaults=false) { DirectoryPath=Path.GetFullPath(path);this.initializeDefaults=initializeDefaults; }
        public AppData Load()
        {
            if(!File.Exists(FilePath)) {
                if(!initializeDefaults)return new AppData();
                if(File.Exists(FilePath+".bak"))throw new IOException("settings.json이 없고 백업 파일만 남아 있습니다. 백업을 확인한 뒤 복원하세요.");
                var initial=BundledProfile.Create(DirectoryPath);Save(initial);return initial;
            }
            var data=Read<AppData>(FilePath, 8*1024*1024); data.Validate(); return data;
        }
        public void Save(AppData data) { data.Validate(); Atomic(FilePath,data); }
        public static T Read<T>(string path,int limit)
        {
            using(var s=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                if(s.Length==0 || s.Length>limit) throw new InvalidDataException("파일 크기가 허용 범위를 벗어났습니다.");
                var serializer=new DataContractJsonSerializer(typeof(T));
                var result=(T)serializer.ReadObject(s);
                if(result==null) throw new InvalidDataException("빈 파일입니다.");
                return result;
            }
        }
        public static void Atomic<T>(string path,T data)
        {
            string parent=Path.GetDirectoryName(Path.GetFullPath(path)); Directory.CreateDirectory(parent);
            string temp=Path.Combine(parent,"."+Path.GetFileName(path)+"."+Guid.NewGuid().ToString("N")+".tmp");
            try {
                using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                    new DataContractJsonSerializer(typeof(T)).WriteObject(stream,data); stream.Flush(true);
                }
                if(File.Exists(path)) File.Replace(temp,path,path+".bak"); else File.Move(temp,path);
            } finally { if(File.Exists(temp)) File.Delete(temp); }
        }
        public static Preset Import(string path,IccLibrary library=null)
        {
            var file=Read<PresetFile>(path,IccLibrary.MaximumPresetBytes);var preset=file.ToPreset();
            if(file.IccData!=null) {
                if(library==null)throw new InvalidDataException("ICC 프리셋은 NCP 안에서 가져오세요.");
                preset.Icc=library.Import(file.IccName,Convert.FromBase64String(file.IccData));
            }
            return preset;
        }
        public static void Export(string path,Preset p,IccLibrary library=null)
        {
            p.Validate(); var file=new PresetFile { Name=p.Name, Values=p.Values.Copy() };
            if(p.Icc!=null) {
                if(library==null)throw new InvalidOperationException("ICC 프리셋은 NCP 안에서 내보내세요.");
                file.Version=2;file.IccName=p.Icc.Name;file.IccData=Convert.ToBase64String(library.Read(p.Icc));
            }
            Atomic(path,file);
        }
    }
}
