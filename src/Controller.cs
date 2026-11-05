using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ncp
{
    public sealed class Controller : IDisposable
    {
        readonly Store store;
        readonly IColorBackend backend;
        readonly IIccBackend iccBackend;
        readonly IccLibrary iccLibrary;
        IccState iccReading;
        public AppData Data {get;private set;}
        public DisplayTarget Selected {get;private set;}
        public DisplayReading Reading {get;private set;}
        public List<DisplayTarget> Displays {get;private set;}
        public string StorageError {get;private set;}
        public bool CanSave {get {return StorageError==null;} }
        public bool AppliedInSession {get;private set;}
        ColorValues sessionValues;
        string sessionKey,sessionFingerprint;
        public Controller(Store store,IColorBackend backend,IIccBackend iccBackend=null)
        {
            this.store=store; this.backend=backend;this.iccBackend=iccBackend;iccLibrary=new IccLibrary(store.DirectoryPath); Displays=new List<DisplayTarget>();
            try { Data=store.Load(); }
            catch(Exception e) { Data=new AppData(); StorageError="설정 파일을 읽지 못했습니다. 원본 파일은 보존됩니다.\n"+store.FilePath+"\n"+e.Message; }
            if(Data.Profiles.Count==0) {
                var profile=new PresetProfile {Id=Guid.NewGuid().ToString("N"),Name="기본 프로필",Presets=Data.Presets,Order=Data.PresetOrder};
                Data.Profiles.Add(profile);Data.ActiveProfileId=profile.Id;
            } else {
                var profile=Data.Profiles.FirstOrDefault(p=>p.Id==Data.ActiveProfileId)??Data.Profiles[0];
                Data.ActiveProfileId=profile.Id;Data.Presets=profile.Presets;Data.PresetOrder=profile.Order;
            }
        }
        public PresetProfile ActiveProfile {get {return Data.Profiles.First(p=>p.Id==Data.ActiveProfileId);}}
        void SyncProfile() {ActiveProfile.Presets=Data.Presets;ActiveProfile.Order=Data.PresetOrder;}
        public void SwitchProfile(string id)
        {
            if(!CanSave)throw new IOException(StorageError);
            SyncProfile();var profile=Data.Profiles.FirstOrDefault(p=>p.Id==id);
            if(profile==null)throw new InvalidOperationException("프로필을 찾을 수 없습니다.");
            Data.ActiveProfileId=profile.Id;Data.Presets=profile.Presets;Data.PresetOrder=profile.Order;Persist();
        }
        public PresetProfile CreateProfile(string name,bool copyCurrent)
        {
            return AddProfile(name,copyCurrent?Data.Presets:new List<Preset>(),copyCurrent?Data.PresetOrder:new List<string>());
        }
        public void RenameProfile(string name)
        {
            if(!CanSave)throw new IOException(StorageError);
            var profile=ActiveProfile;name=(name??"").Trim();
            if(Data.Profiles.Any(p=>p.Id!=profile.Id && string.Equals(p.Name,name,StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("같은 이름의 프로필이 있습니다.");
            new PresetProfile {Id=profile.Id,Name=name,Presets=profile.Presets,Order=profile.Order}.Validate();
            if(profile.Name==name)return;
            string previous=profile.Name;profile.Name=name;
            try {Persist();}catch {profile.Name=previous;throw;}
        }
        public PresetProfile AddProfile(string name,IEnumerable<Preset> presets,IEnumerable<string> order)
        {
            if(!CanSave)throw new IOException(StorageError);
            if(Data.Profiles.Count>=50)throw new InvalidOperationException("프로필은 최대 50개까지 저장할 수 있습니다.");
            name=(name??"").Trim();
            if(Data.Profiles.Any(p=>string.Equals(p.Name,name,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("같은 이름의 프로필이 있습니다.");
            var profile=new PresetProfile {Id=Guid.NewGuid().ToString("N"),Name=name};
            var map=new Dictionary<string,string>{{"default","default"}};
            foreach(var p in presets) {
                var clone=new Preset {Id=Guid.NewGuid().ToString("N"),Name=p.Name,Values=p.Values.Copy(),Icc=p.Icc};
                if(map.ContainsKey(p.Id))throw new InvalidDataException("중복된 프리셋 ID입니다.");
                map.Add(p.Id,clone.Id);profile.Presets.Add(clone);
            }
            profile.Order=order.Where(map.ContainsKey).Select(id=>map[id]).ToList();profile.Validate();
            SyncProfile();Data.Profiles.Add(profile);Data.ActiveProfileId=profile.Id;Data.Presets=profile.Presets;Data.PresetOrder=profile.Order;Persist();return profile;
        }
        public void DeleteProfile()
        {
            if(!CanSave)throw new IOException(StorageError);
            if(Data.Profiles.Count<=1)throw new InvalidOperationException("마지막 프로필은 삭제할 수 없습니다.");
            Data.Profiles.RemoveAll(p=>p.Id==Data.ActiveProfileId);
            var next=Data.Profiles[0];Data.ActiveProfileId=next.Id;Data.Presets=next.Presets;Data.PresetOrder=next.Order;Persist();
            CleanupIccBackups();
        }
        public void CleanupIccBackups()
        {
            if(!CanSave)return;
            // Saved presets across every profile own the local backups. An applied
            // snapshot uses the independently installed Windows copy, not this cache.
            var references=Data.Profiles.SelectMany(p=>p.Presets).Concat(Data.Presets).Where(p=>p.Icc!=null).Select(p=>p.Icc);
            iccLibrary.RemoveUnused(references);
        }
        public IccProfileRef ImportIcc(string path)
        {
            if(!CanSave)throw new IOException(StorageError);return iccLibrary.Import(path);
        }
        public void ExportPreset(string path,Preset preset)
        {
            if(Path.GetExtension(path).Equals(".json",StringComparison.OrdinalIgnoreCase))Store.Export(path,preset,iccLibrary);
            else SharePackages.ExportPreset(path,preset,iccLibrary);
        }
        public void ExportProfile(string path) {SyncProfile();SharePackages.ExportProfile(path,ActiveProfile,iccLibrary);}
        public PresetProfile AddInitialProfile()
        {
            if(!CanSave)throw new IOException(StorageError);
            if(Data.Profiles.Count>=50)throw new InvalidOperationException("프로필은 최대 50개까지 저장할 수 있습니다.");
            return AddImportedProfile(BundledProfile.Load(iccLibrary));
        }
        public PresetProfile ImportProfile(string path)
        {
            return AddImportedProfile(SharePackages.ImportProfile(path,iccLibrary));
        }
        PresetProfile AddImportedProfile(PresetProfile profile)
        {
            string name=profile.Name;int n=2;
            while(Data.Profiles.Any(p=>string.Equals(p.Name,name,StringComparison.OrdinalIgnoreCase))) {
                string suffix=" ("+(n++)+")";name=profile.Name.Substring(0,Math.Min(profile.Name.Length,60-suffix.Length))+suffix;
            }
            return AddProfile(name,profile.Presets,profile.Order);
        }
        public List<Preset> Presets()
        {
            var items=new List<Preset>{DefaultPreset()};
            items.AddRange(Data.Presets);
            var byId=items.ToDictionary(p=>p.Id);
            var ordered=Data.PresetOrder.Where(byId.ContainsKey).Select(id=>byId[id]).ToList();
            var included=new HashSet<string>(Data.PresetOrder);
            ordered.AddRange(items.Where(p=>!included.Contains(p.Id)));
            return ordered;
        }
        public bool MovePreset(string id,int insertionIndex)
        {
            if(!CanSave)throw new IOException(StorageError);
            var items=Presets();
            int source=items.FindIndex(p=>p.Id==id);
            if(source<0)throw new InvalidOperationException("이동할 프리셋을 찾을 수 없습니다.");
            if(insertionIndex<0 || insertionIndex>items.Count)throw new ArgumentOutOfRangeException("insertionIndex");
            int target=insertionIndex>source?insertionIndex-1:insertionIndex;
            if(target==source)return false;
            var moved=items[source]; items.RemoveAt(source); items.Insert(target,moved);
            var previous=Data.PresetOrder;
            Data.PresetOrder=items.Select(p=>p.Id).ToList();
            try {Persist();}
            catch {Data.PresetOrder=previous;throw;}
            return true;
        }
        public OriginalRecord Original {get {return Selected==null?null:Data.Originals.FirstOrDefault(o=>o.DisplayKey==Selected.Key);} }
        public Preset DefaultPreset()
        {
            return Preset.Default();
        }
        public AppliedRecord LastApplied {get {return Selected==null?null:Data.Applied.FirstOrDefault(a=>a.DisplayKey==Selected.Key);} }
        public Preset LastAppliedPreset()
        {
            var last=LastApplied;
            return last==null?null:Presets().FirstOrDefault(p=>p.Id==last.PresetId && p.Values.Same(last.Values) && IccProfileRef.Equal(p.Icc,last.Icc));
        }
        public bool HasReadableMatch
        {
            get {
                var last=LastApplied;
                if(last!=null && last.PresetId=="default" && iccBackend!=null
                    && (iccReading==null || IccAssociationPolicy.ContainsNcp(iccReading)))return false;
                return last!=null && Reading!=null && Reading.Vibrance==last.Values.Vibrance
                    && Reading.Hue==last.Values.Hue && Reading.Fingerprint==last.PanelFingerprint
                    && (last.IccFingerprint==null || (iccReading!=null && iccReading.Fingerprint==last.IccFingerprint));
            }
        }
        public void RefreshDisplays()
        {
            if(backend==null) throw new InvalidOperationException("NVIDIA 연결을 사용할 수 없습니다.");
            string old=Selected==null?Data.SelectedDisplay:Selected.Key;
            Displays=backend.Enumerate();
            Selected=Displays.FirstOrDefault(d=>d.Key==old) ?? Displays.FirstOrDefault();
            Reading=null;
            if(Selected!=null) Refresh();
        }
        public void Select(DisplayTarget display)
        {
            Selected=display; Reading=null; Refresh();
            Data.SelectedDisplay=display.Key;
            if(CanSave) Persist();
        }
        public void Refresh()
        {
            if(Selected==null) return;
            Reading=null;
            Reading=backend.Read(Selected);
            iccReading=null;
            if(iccBackend!=null && (Data.IccOriginals.Any(o=>o.DisplayKey==Selected.Key)
                || (LastApplied!=null && LastApplied.PresetId=="default"))) {
                try {iccReading=iccBackend.Read(Selected);}catch { }
            }
            // Never reconstruct gamma from a last-used label. A mismatch only invalidates the session status.
            AppliedInSession=sessionValues!=null && sessionKey==Selected.Key
                && Reading.Vibrance==sessionValues.Vibrance && Reading.Hue==sessionValues.Hue
                && Reading.Fingerprint==sessionFingerprint;
            if(LastApplied!=null && LastApplied.IccFingerprint!=null && (iccReading==null || iccReading.Fingerprint!=LastApplied.IccFingerprint))AppliedInSession=false;
            if(LastApplied!=null && LastApplied.PresetId=="default" && iccBackend!=null
                && (iccReading==null || IccAssociationPolicy.ContainsNcp(iccReading)))AppliedInSession=false;
            // Capture only unambiguous first-run panel values. They are a saved-panel baseline, not a live LUT backup.
            if(CanSave && Original==null && LastApplied==null && Reading.PanelValues!=null && Reading.Channels!=null) {
                Data.Originals.Add(new OriginalRecord {DisplayKey=Selected.Key,Values=Reading.PanelValues.Copy(),Channels=(double[])Reading.Channels.Clone()});
                Persist();
            }
        }
        void Persist()
        {
            if(!CanSave) throw new IOException(StorageError);
            SyncProfile();
            try { store.Save(Data); }
            catch(Exception e) { StorageError="프리셋 저장에 실패했습니다. 앱을 다시 열기 전에 저장 공간과 폴더 권한을 확인하세요.\n"+e.Message; throw; }
        }
        public Preset SavePreset(string id,string name,ColorValues values,IccProfileRef icc=null)
        {
            if(id=="default") throw new InvalidOperationException("기본값은 변경할 수 없습니다. 새 프리셋으로 저장하세요.");
            if(!CanSave) throw new IOException(StorageError);
            var p=new Preset {Id=id??Guid.NewGuid().ToString("N"),Name=(name??"").Trim(),Values=values.Copy(),Icc=icc}; p.Validate();
            if(Data.Presets.Any(x=>x.Id!=p.Id && string.Equals(x.Name,p.Name,StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("같은 이름의 프리셋이 있습니다. 다른 이름을 입력하세요.");
            int index=Data.Presets.FindIndex(x=>x.Id==p.Id);
            bool changedIcc=index>=0 && !IccProfileRef.Equal(Data.Presets[index].Icc,p.Icc);
            if(index<0) {
                if(Data.Presets.Count>=200) throw new InvalidOperationException("프리셋은 최대 200개까지 저장할 수 있습니다.");
                Data.Presets.Add(p);
            } else Data.Presets[index]=p;
            Persist();if(changedIcc)CleanupIccBackups();return p;
        }
        public void Delete(string id)
        {
            if(id=="default") throw new InvalidOperationException("기본값은 삭제할 수 없습니다.");
            if(!CanSave) throw new IOException(StorageError);
            Data.Presets.RemoveAll(p=>p.Id==id); Data.PresetOrder.Remove(id); Persist();
            CleanupIccBackups();
            // Deleting an active preset does not change the display. Its independent applied snapshot remains.
        }
        public Preset Import(string path)
        {
            var p=Path.GetExtension(path).Equals(".json",StringComparison.OrdinalIgnoreCase)?Store.Import(path,iccLibrary):SharePackages.ImportPreset(path,iccLibrary); string name=p.Name; int n=2;
            while(Data.Presets.Any(x=>string.Equals(x.Name,name,StringComparison.OrdinalIgnoreCase))) {
                string suffix=" ("+(n++)+")"; name=p.Name.Substring(0,Math.Min(p.Name.Length,60-suffix.Length))+suffix;
            }
            return SavePreset(null,name,p.Values,p.Icc);
        }
        // This is the sole controller entry point that writes display colors. It is called only by mouse actions.
        public void Apply(Preset preset)
        {
            if(backend==null || Selected==null) throw new InvalidOperationException("적용할 NVIDIA 모니터를 선택하세요.");
            if(!CanSave) throw new IOException(StorageError);
            preset=preset.Id=="default"?DefaultPreset():Data.Presets.SingleOrDefault(p=>p.Id==preset.Id);
            if(preset==null) throw new InvalidOperationException("삭제되었거나 저장되지 않은 프리셋입니다.");
            preset.Validate();
            Refresh();
            var before=AppliedInSession?sessionValues:null;
            var iccOriginal=Data.IccOriginals.FirstOrDefault(o=>o.DisplayKey==Selected.Key);
            IccState beforeIcc=null;string installedName=null,appliedIccFingerprint=null;
            // A moved/reinstalled program can have no ownership history while old
            // NCP associations still exist in Windows. The default button repairs them.
            bool orphaned=false;
            if(preset.Id=="default" && iccOriginal==null && iccBackend!=null) {
                var current=iccBackend.Read(Selected);
                if(IccAssociationPolicy.ContainsNcp(current)) {beforeIcc=current;orphaned=true;}
            }
            if(preset.Icc!=null || iccOriginal!=null || orphaned) {
                if(iccBackend==null)throw new InvalidOperationException("Windows ICC 연결을 사용할 수 없습니다.");
                if(beforeIcc==null)beforeIcc=iccBackend.Read(Selected);
                if(iccOriginal==null) {
                    iccOriginal=new IccOriginal {DisplayKey=Selected.Key,State=IccAssociationPolicy.WithoutNcp(beforeIcc)};Data.IccOriginals.Add(iccOriginal);Persist();
                }
                if(preset.Icc!=null) {
                    iccLibrary.Read(preset.Icc);
                    installedName=iccBackend.EnsureInstalled(iccLibrary.PathFor(preset.Icc),preset.Icc);
                }
                var baseline=new HashSet<string>(iccOriginal.State.Profiles??new List<string>(),StringComparer.OrdinalIgnoreCase);
                if(iccOriginal.State.ProfileName!=null)baseline.Add(iccOriginal.State.ProfileName);
                var additions=(beforeIcc.Profiles??new List<string>()).Where(WindowsIccBackend.IsNcpProfile).ToList();
                if(installedName!=null)additions.Add(installedName);
                bool changed=false;
                foreach(string added in additions.Where(p=>WindowsIccBackend.IsNcpProfile(p)||!baseline.Contains(p)))
                    if(!iccOriginal.ManagedProfiles.Contains(added,StringComparer.OrdinalIgnoreCase)) {iccOriginal.ManagedProfiles.Add(added);changed=true;}
                if(changed)Persist();
            }
            AppliedInSession=false;
            try {
                if(beforeIcc!=null) {
                    if(preset.Icc!=null)iccBackend.Apply(Selected,installedName,iccOriginal.ManagedProfiles);
                    else iccBackend.Restore(Selected,iccOriginal.State,iccOriginal.ManagedProfiles);
                    appliedIccFingerprint=iccBackend.Read(Selected).Fingerprint;
                }
                backend.Apply(Selected,preset.Values,null,before,null);
            } catch(Exception e) {
                sessionValues=null;
                if(beforeIcc!=null)try {iccBackend.Restore(Selected,beforeIcc,iccOriginal.ManagedProfiles,true);}catch(Exception restore) {
                    throw new InvalidOperationException(e.Message+"\nICC 복원도 실패했습니다: "+restore.Message,e);
                }
                if(beforeIcc!=null)try {CleanupIccCopies();}catch(Exception cleanup) {
                    throw new InvalidOperationException(e.Message+"\n원래 ICC로 복원했지만 NCP 사본 정리에 실패했습니다: "+cleanup.Message,e);
                }
                throw;
            }
            sessionValues=preset.Values.Copy();
            sessionKey=Selected.Key; sessionFingerprint=Reading.Fingerprint;
            AppliedInSession=true;
            var record=new AppliedRecord {DisplayKey=Selected.Key,PresetId=preset.Id,PresetName=preset.Name,Values=preset.Values.Copy(),
                AppliedUtc=DateTime.UtcNow.ToString("o"),PanelFingerprint=Reading.Fingerprint,Icc=preset.Icc,
                IccFingerprint=appliedIccFingerprint};
            Data.Applied.RemoveAll(a=>a.DisplayKey==Selected.Key); Data.Applied.Add(record);
            try { Persist(); }
            catch(Exception e) {throw new IOException("색상 적용은 성공했지만 기록을 저장하지 못했습니다.\n"+e.Message,e);}
            Refresh();
            if(beforeIcc!=null || (preset.Id=="default" && iccBackend!=null)) {
                try {CleanupIccCopies();}
                catch(Exception e) {throw new IOException("색상 적용은 완료했지만, 사용하지 않는 NCP ICC 사본을 정리하지 못했습니다.\n"+e.Message,e);}
            }
        }
        void CleanupIccCopies()
        {
            var protectedNames=Data.IccOriginals.SelectMany(o=>(o.State.Profiles??new List<string>()).Concat(new[]{o.State.ProfileName}))
                .Where(n=>n!=null && !WindowsIccBackend.IsNcpProfile(n)).ToList();
            iccBackend.CleanupUnused(protectedNames);
        }
        public string StatusTitle()
        {
            if(Selected==null) return "연결된 NVIDIA 모니터 없음";
            if(Reading==null) return "현재 상태를 확인할 수 없음";
            var last=LastApplied;
            if(AppliedInSession && last!=null) return "적용 완료 · "+last.PresetName;
            if(last!=null) {
                if(Reading.Vibrance!=last.Values.Vibrance || Reading.Hue!=last.Values.Hue || Reading.Fingerprint!=last.PanelFingerprint)
                    return "외부 변경 감지 · 현재 프리셋 확인 필요";
                return "마지막 적용 · "+last.PresetName;
            }
            return "현재 색상 읽음 · 아직 적용하지 않음";
        }
        public string StatusDetail()
        {
            if(Reading==null) return "새로고침으로 연결 상태를 확인하세요. 색상은 자동으로 변경하지 않습니다.";
            string actual="현재 채도 "+Reading.Vibrance+"% · 색조 "+Reading.Hue+"°";
            return actual+"  |  "+(AppliedInSession?"NVIDIA 적용 요청 성공. 밝기·대비·감마 재조회 미지원.":"밝기·대비·감마 재조회 미지원 — 전체 적용 상태는 확인할 수 없습니다.");
        }
        public void Dispose() {if(backend!=null) backend.Dispose();}
    }
}
